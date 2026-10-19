using System.Text.Json;
using Microsoft.Playwright;
using WebAutomater.Core.Browsers;
using WebAutomater.Core.Models;

namespace WebAutomater.Core.Recording;

public enum StepChangeKind { Added, Updated, Removed }

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
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private long _lastTs;
    private long _lastActionTs;
    private bool _started;

    public Recorder(Scenario? scenario = null)
    {
        Scenario = scenario ?? new Scenario();
    }

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
        await _context.AddInitScriptAsync(RecorderScript.Source);
        _context.Page += (_, page) => AttachPage(page);

        var page = await _context.NewPageAsync();
        AttachPage(page);

        Scenario.StartUrl ??= url;
        AddStep(new Step { Type = StepType.Navigate, Value = url, PageIndex = 0 });
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

    // Kullanıcı adres çubuğuna yeni bir adres yazdıysa bunu ayrı bir adım olarak kaydeder.
    private void OnMainFrameNavigated(IPage page, string url)
    {
        lock (_gate)
        {
            var previous = _lastUrls.GetValueOrDefault(page);
            _lastUrls[page] = url;
            if (!_started || url == previous || url == "about:blank")
                return;

            var now = Now();
            if (now - _lastActionTs < NavigationAttributionMs)
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
        if (ev is null || ev.Selectors.Count == 0)
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
            if (frame != page.MainFrame)
            {
                step.FrameName = string.IsNullOrEmpty(frame.Name) ? null : frame.Name;
                step.FrameUrl = frame.Url;
            }

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

    // Art arda gelen olayları tek adımda birleştirir: her tuş vuruşu yerine tek bir "yaz",
    // iki tıklama + çift tıklama yerine tek bir "çift tıkla".
    private bool TryMerge(Step step, int delay)
    {
        var steps = Scenario.Steps;
        var last = steps.Count > 0 ? steps[^1] : null;

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
            while (removed < 2 && steps.Count > 0 && steps[^1] is { Type: StepType.Click } click
                   && SameTarget(click, step) && (removed == 0 ? delay : firstDelay) < DoubleClickWindowMs)
            {
                firstDelay = click.DelayBeforeMs;
                steps.RemoveAt(steps.Count - 1);
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

    private void AddStepLocked(Step step)
    {
        Scenario.Steps.Add(step);
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
