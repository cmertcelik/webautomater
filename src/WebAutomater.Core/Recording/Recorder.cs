using System.Text.Json;
using Microsoft.Playwright;
using WebAutomater.Core.Browsers;
using WebAutomater.Core.Models;

namespace WebAutomater.Core.Recording;

public enum StepChangeKind { Added, Updated, Removed }

/// <summary>Record: işlemler adım olur. Check: tıklanan öğe işlem görmez, kontrol düğümü oluşturulur.</summary>
public enum RecorderMode { Record, Check }

public sealed class StepChangedEventArgs(StepChangeKind kind, Step step, int index) : EventArgs
{
    public StepChangeKind Kind { get; } = kind;
    public Step Step { get; } = step;
    public int Index { get; } = index;
}

/// <summary>
/// Görünür bir tarayıcı açar ve kullanıcının yaptığı işlemleri, aralarındaki sürelerle
/// birlikte <see cref="Scenario"/> adımlarına dönüştürür.
/// </summary>
public sealed class Recorder : IAsyncDisposable
{
    // Bir işlemden bu süre içinde gerçekleşen sayfa geçişleri o işlemin sonucu sayılır.
    private const int NavigationAttributionMs = 2000;
    private const int DoubleClickWindowMs = 600;

    private static readonly JsonSerializerOptions EventJson = new() { PropertyNameCaseInsensitive = true };

    private readonly object _gate = new();
    private readonly List<IPage> _pages = [];
    private readonly Dictionary<IPage, string> _lastUrls = [];
    // Ana çerçevede başlamış ama henüz tamamlanmamış sayfa geçişinin başlangıç zamanı.
    private readonly Dictionary<IPage, long> _navigationStarts = [];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private long _lastTs;
    private long _lastActionTs;
    private bool _started;
    private (Step Step, StepPort Port)? _tail;
    private readonly HashSet<Step> _added = [];
    private volatile RecorderMode _mode;

    /// <param name="scenario">Eklenecek senaryo; boşsa yeni senaryo.</param>
    /// <param name="attachTo">Doluysa kayıt bu düğümün bu çıkışından dal olarak başlar ve başa "Sayfaya git" eklenmez.</param>
    public Recorder(Scenario? scenario = null, (Step Step, StepPort Port)? attachTo = null)
    {
        Scenario = scenario ?? new Scenario();
        ScenarioGraph.EnsureGraph(Scenario);
        _tail = attachTo;
        IsBranch = attachTo is not null;
    }

    private bool IsBranch { get; }

    /// <summary>Doluyken işlemler kaydedilmez; dal kaydında başlangıçtan oraya kadar otomatik gidilirken kullanılır.</summary>
    public bool CapturePaused
    {
        get;
        set
        {
            field = value;
            if (!value)
            {
                lock (_gate)
                {
                    _lastTs = Now();
                    _lastActionTs = 0; // Otomatik gidişin son geçişi kullanıcının işlemine atfedilmesin.
                }
            }
        }
    }

    public RecorderMode Mode => _mode;

    /// <summary>Mod, tarayıcıdaki çubuktan ya da <see cref="SetModeAsync"/> ile değişince; arka plandan çağrılır.</summary>
    public event Action<RecorderMode>? ModeChanged;

    public Scenario Scenario { get; }

    /// <summary>Kayda adım eklendiğinde/güncellendiğinde/silindiğinde; arka plan iş parçacığından çağrılır.</summary>
    public event EventHandler<StepChangedEventArgs>? StepChanged;

    /// <summary>Tarayıcı kapatıldığında ya da <see cref="StopAsync"/> çağrıldığında tamamlanır.</summary>
    public Task Completion => _completion.Task;

    public Action<string>? Log { get; set; }

    /// <summary>Yalnızca otomatik testler için: tarayıcıyı görünmez açar.</summary>
    public bool Headless { get; set; }

    public IReadOnlyList<IPage> Pages
    {
        get { lock (_gate) return [.. _pages]; }
    }

    public async Task StartAsync(string url, CancellationToken ct = default)
    {
        url = NormalizeUrl(url);
        _playwright = await Playwright.CreateAsync();
        _browser = await BrowserFactory.LaunchAsync(_playwright, Scenario.Browser, Headless, Log);
        _browser.Disconnected += (_, _) => _completion.TrySetResult();
        ct.ThrowIfCancellationRequested();

        _context = await _browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = ViewportSize.NoViewport });
        await _context.ExposeBindingAsync("__waRecord", (BindingSource source, string json) => OnRecorded(source, json));
        await _context.ExposeBindingAsync("__waModeChanged", (BindingSource source, string mode) => OnPageModeChanged(source, mode));
        await _context.ExposeBindingAsync("__waGetMode", (Func<BindingSource, string>)(_ => ModeName(_mode)));
        await _context.AddInitScriptAsync(RecorderScript.Source);
        _context.Page += (_, page) => AttachPage(page);

        var page = await _context.NewPageAsync();
        AttachPage(page);

        if (!IsBranch)
        {
            Scenario.StartUrl ??= url;
            AddStep(new Step { Type = StepType.Navigate, Value = url, PageIndex = 0 });
        }

        try
        {
            await page.GotoAsync(url);
        }
        catch (PlaywrightException ex)
        {
            Log?.Invoke($"Sayfa yüklenirken uyarı: {ex.Message.Split('\n')[0]}");
        }

        lock (_gate)
        {
            _lastTs = Now();
            _lastActionTs = _lastTs;
            _lastUrls[page] = page.Url;
            _started = true;
        }
    }

    /// <summary>Kayıt ile kontrol modu arasında geçiş; tüm sekme ve çerçevelere iletilir.</summary>
    public async Task SetModeAsync(RecorderMode mode)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        await BroadcastModeAsync(null);
        ModeChanged?.Invoke(mode);
    }

    private void OnPageModeChanged(BindingSource source, string mode)
    {
        var parsed = mode == "check" ? RecorderMode.Check : RecorderMode.Record;
        if (_mode == parsed)
            return;
        _mode = parsed;
        _ = BroadcastModeAsync(source.Frame);
        ModeChanged?.Invoke(parsed);
    }

    private async Task BroadcastModeAsync(IFrame? except)
    {
        var name = ModeName(_mode);
        foreach (var page in Pages.Where(p => !p.IsClosed))
        {
            foreach (var frame in page.Frames.Where(f => f != except))
            {
                try
                {
                    await frame.EvaluateAsync("m => window.__waSetMode && window.__waSetMode(m)", name);
                }
                catch (PlaywrightException)
                {
                    // Çerçeve o anda kapanıyor ya da yükleniyor olabilir; yüklenince modu kendisi sorar.
                }
            }
        }
    }

    private static string ModeName(RecorderMode mode) => mode == RecorderMode.Check ? "check" : "record";

    public async Task StopAsync()
    {
        try
        {
            if (_browser is { IsConnected: true })
                await _browser.CloseAsync();
        }
        catch (PlaywrightException)
        {
            // Kullanıcı tarayıcıyı zaten kapatmış olabilir.
        }
        _completion.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _playwright?.Dispose();
    }

    private void AttachPage(IPage page)
    {
        lock (_gate)
        {
            if (_pages.Contains(page))
                return;
            _pages.Add(page);
            _lastUrls[page] = page.Url;
        }

        page.Request += (_, request) =>
        {
            if (!request.IsNavigationRequest || request.Frame != page.MainFrame)
                return;
            lock (_gate)
                _navigationStarts.TryAdd(page, Now()); // Yönlendirmelerde ilk başlangıç esas alınır.
        };
        page.FrameNavigated += (_, frame) =>
        {
            if (frame == page.MainFrame)
                OnMainFrameNavigated(page, frame.Url);
        };
        page.Close += (_, _) =>
        {
            bool allClosed;
            lock (_gate)
                allClosed = _pages.All(p => p.IsClosed);
            if (allClosed)
                _ = StopAsync();
        };
    }

    // Bir işlemin hemen ardından başlayan sayfa geçişi o işlemin sonucudur: işlem işaretlenir ki
    // oynatmada yeni sayfa beklensin. Aksi halde kullanıcı adres çubuğuna yeni bir adres yazmıştır.
    private void OnMainFrameNavigated(IPage page, string url)
    {
        lock (_gate)
        {
            var previous = _lastUrls.GetValueOrDefault(page);
            _lastUrls[page] = url;
            var now = Now();
            // Geçişin bittiği an değil başladığı an karşılaştırılır; sunucu yavaş yanıt verse de
            // geçiş doğru işleme bağlanır. Aynı belge içi geçişlerde (SPA) istek olmaz.
            var isDocumentLoad = _navigationStarts.Remove(page, out var startedAt);
            if (!_started || CapturePaused)
                return;

            if ((isDocumentLoad ? startedAt : now) - _lastActionTs < NavigationAttributionMs)
            {
                if (isDocumentLoad)
                    MarkLastActionAsNavigating(page);
                return;
            }
            if (url == previous || url == "about:blank")
                return;

            var delay = (int)Math.Max(0, now - _lastTs);
            _lastTs = now;
            _lastActionTs = now;
            AddStepLocked(new Step
            {
                Type = StepType.Navigate,
                Value = url,
                PageIndex = _pages.IndexOf(page),
                DelayBeforeMs = delay,
            });
        }
    }

    private void MarkLastActionAsNavigating(IPage page)
    {
        var pageIndex = _pages.IndexOf(page);
        for (var i = Scenario.Steps.Count - 1; i >= 0; i--)
        {
            var step = Scenario.Steps[i];
            if (step.PageIndex != pageIndex || !_added.Contains(step) || step.Type == StepType.Condition)
                continue;
            if (step.Type is not (StepType.Navigate or StepType.Wait) && !step.WaitForNavigation)
            {
                step.WaitForNavigation = true;
                Raise(StepChangeKind.Updated, step, i);
            }
            return;
        }
    }

    private void OnRecorded(BindingSource source, string json)
    {
        RecordedEvent? ev;
        try
        {
            ev = JsonSerializer.Deserialize<RecordedEvent>(json, EventJson);
        }
        catch (JsonException)
        {
            return;
        }
        if (ev is null || CapturePaused)
            return;
        if (ev.Type == "condition")
        {
            AddCondition(source, ev);
            return;
        }
        if (ev.Selectors.Count == 0)
            return;

        var type = ev.Type switch
        {
            "click" => StepType.Click,
            "dblclick" => StepType.DoubleClick,
            "fill" => StepType.Fill,
            "press" => StepType.Press,
            "select" => StepType.Select,
            "check" => StepType.Check,
            "uncheck" => StepType.Uncheck,
            "upload" => StepType.Upload,
            _ => (StepType?)null,
        };
        if (type is null)
            return;

        var page = source.Page;
        var frame = source.Frame;

        lock (_gate)
        {
            if (ev.Vw > 0 && ev.Vh > 0 && Scenario.Browser.ViewportWidth is null)
            {
                Scenario.Browser.ViewportWidth = ev.Vw;
                Scenario.Browser.ViewportHeight = ev.Vh;
            }

            var step = new Step
            {
                Type = type.Value,
                Selectors = ev.Selectors,
                Value = ev.Value,
                DisplayValue = ev.Display,
                TargetName = ev.Name,
                TargetKind = ev.Kind,
                Multiple = ev.Multiple,
                PageIndex = Math.Max(0, _pages.IndexOf(page)),
            };
            ApplyFrame(step, page, frame);

            var ts = ev.Ts;
            var delay = (int)Math.Max(0, ts - _lastTs);
            _lastTs = Math.Max(_lastTs, ts);
            _lastActionTs = Math.Max(_lastActionTs, Now());

            if (TryMerge(step, delay))
                return;

            step.DelayBeforeMs = delay;
            AddStepLocked(step);
        }
    }

    private void AddCondition(BindingSource source, RecordedEvent ev)
    {
        if (!Enum.TryParse<ConditionKind>(ev.Condition, ignoreCase: true, out var kind))
            return;

        var selectors = kind == ConditionKind.PageHasText ? [] : ev.Selectors;
        // Metni sınanacak öğe metniyle aranırsa, metin değişince başka öğe bulunur ya da hiç bulunmaz:
        // "eşit mi" sorusu anlamını yitirir. Bu yüzden konuma/kimliğe dayalı seçiciler kullanılır.
        if (kind is ConditionKind.TextEquals or ConditionKind.TextContains)
        {
            var stable = selectors.Where(s => !s.StartsWith("internal:text=") && !s.StartsWith("internal:role=")).ToList();
            if (stable.Count > 0)
                selectors = stable;
        }

        lock (_gate)
        {
            var step = new Step
            {
                Type = StepType.Condition,
                Condition = kind,
                Selectors = selectors,
                Value = ev.Value,
                TargetName = kind == ConditionKind.PageHasText ? null : ev.Name,
                TargetKind = kind == ConditionKind.PageHasText ? null : ev.Kind,
                PageIndex = Math.Max(0, _pages.IndexOf(source.Page)),
            };
            ApplyFrame(step, source.Page, source.Frame);

            // Kullanıcının kontrolü kurarken harcadığı süre oynatmada beklenmez.
            _lastTs = Now();
            AddStepLocked(step);
        }
    }

    private static void ApplyFrame(Step step, IPage page, IFrame frame)
    {
        if (frame == page.MainFrame)
            return;
        step.FrameName = string.IsNullOrEmpty(frame.Name) ? null : frame.Name;
        step.FrameUrl = frame.Url;
    }

    // Art arda gelen olayları tek adımda birleştirir: her tuş vuruşu yerine tek bir "yaz",
    // iki tıklama + çift tıklama yerine tek bir "çift tıkla".
    private bool TryMerge(Step step, int delay)
    {
        var steps = Scenario.Steps;
        var last = steps.Count > 0 ? steps[^1] : null;

        // Dal kaydında listedeki önceki adım bu kayda ait olmayabilir.
        if (last is not null && !_added.Contains(last))
            last = null;

        if (step.Type == StepType.Fill && last is { Type: StepType.Fill } && SameTarget(last, step))
        {
            last.Value = step.Value;
            Raise(StepChangeKind.Updated, last, steps.Count - 1);
            return true;
        }

        if (step.Type == StepType.DoubleClick)
        {
            var firstDelay = delay;
            var removed = 0;
            while (removed < 2 && steps.Count > 0 && steps[^1] is { Type: StepType.Click } click && _added.Contains(click)
                   && SameTarget(click, step) && (removed == 0 ? delay : firstDelay) < DoubleClickWindowMs)
            {
                firstDelay = click.DelayBeforeMs;
                RemoveTail(click);
                Raise(StepChangeKind.Removed, click, steps.Count);
                removed++;
            }
            step.DelayBeforeMs = removed > 0 ? firstDelay : delay;
            AddStepLocked(step);
            return true;
        }

        return false;
    }

    private static bool SameTarget(Step a, Step b) =>
        a.PageIndex == b.PageIndex && a.FrameUrl == b.FrameUrl
        && a.Selectors.Count > 0 && b.Selectors.Count > 0 && a.Selectors[0] == b.Selectors[0];

    private void AddStep(Step step)
    {
        lock (_gate)
            AddStepLocked(step);
    }

    private void RemoveTail(Step step)
    {
        // Silinen son düğüme gelen bağlantı yeni kuyruk olur; sonraki adım oraya eklenir.
        var previous = Scenario.Steps
            .SelectMany(s => Enum.GetValues<StepPort>().Select(port => (Step: s, Port: port)))
            .FirstOrDefault(x => ScenarioGraph.GetLink(x.Step, x.Port) == step.Id);
        ScenarioGraph.Remove(Scenario, step);
        _added.Remove(step);
        _tail = previous.Step is null ? null : previous;
    }

    private void AddStepLocked(Step step)
    {
        Scenario.Steps.Add(step);
        _added.Add(step);
        if (_tail is { } tail)
            ScenarioGraph.SetLink(tail.Step, tail.Port, step.Id);
        else
            Scenario.StartStepId = step.Id;
        _tail = (step, StepPort.Next);
        Raise(StepChangeKind.Added, step, Scenario.Steps.Count - 1);
    }

    private void Raise(StepChangeKind kind, Step step, int index) =>
        StepChanged?.Invoke(this, new StepChangedEventArgs(kind, step, index));

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (url.Length == 0)
            return "about:blank";
        if (File.Exists(url))
            return new Uri(Path.GetFullPath(url)).AbsoluteUri;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file" or "about")
            return url;
        if (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || url.StartsWith("127."))
            return "http://" + url;
        return "https://" + url;
    }
}
