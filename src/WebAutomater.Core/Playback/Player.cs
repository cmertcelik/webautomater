using System.Diagnostics;
using Microsoft.Playwright;
using WebAutomater.Core.Browsers;
using WebAutomater.Core.Diagnostics;
using WebAutomater.Core.Models;
using WebAutomater.Core.Text;

namespace WebAutomater.Core.Playback;

public sealed class StepFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Kaydedilmiş senaryoyu doğrudan veya veri kaynağındaki her satır için oynatır.</summary>
public sealed class Player
{
    private const int PollIntervalMs = 200;
    // Birden çok eşleşme bu süre boyunca sürerse ilk görünen öğe kullanılır.
    private const int AmbiguityGraceMs = 1500;

    // Kontrol düğümünde durum bu süre içinde sağlanmazsa "Hayır" sayılır. Adıma özel süre verilebilir.
    private const int DefaultConditionTimeoutMs = 3000;
    // Bağlantılar döngü kurabilir ("hata varsa başa dön"); çıkışsız döngü oynatmayı sonsuza dek kilitlemesin.
    private const int MaxStepVisitsPerIteration = 5000;

    private sealed record PlanItem(int Iteration, int? RowNumber, IReadOnlyDictionary<string, string>? Row);

    // Sayfada açılmak istenen dosya seçme pencereleri. Dinleyici olduğu için Playwright işletim sisteminin
    // penceresini açtırmaz (görünür oynatmada ekranda asılı kalmaz); yükleme adımı bunu kullanabilir.
    private readonly Dictionary<IPage, IFileChooser> _fileChoosers = [];

    private void WatchFileChoosers(IPage page)
    {
        page.FileChooser += (_, chooser) =>
        {
            lock (_fileChoosers)
                _fileChoosers[page] = chooser;
        };
        page.FrameNavigated += (_, frame) =>
        {
            if (frame == page.MainFrame)
                lock (_fileChoosers)
                    _fileChoosers.Remove(page);
        };
        page.Close += (_, _) =>
        {
            lock (_fileChoosers)
                _fileChoosers.Remove(page);
        };
    }

    private IFileChooser? TakeFileChooser(IPage page)
    {
        lock (_fileChoosers)
            return _fileChoosers.Remove(page, out var chooser) ? chooser : null;
    }

    /// <summary>Tekrarlar arasında korunabilen tarayıcı durumu.</summary>
    private sealed class Session(IBrowserContext context, Action<IPage>? onNewPage = null)
    {
        public IBrowserContext Context { get; } = context;
        public List<IPage> Pages { get; } = [];

        /// <summary>Döngü başlangıcına ulaşıldı: sonraki tekrarlar oradan sürebilir.</summary>
        public bool LoopReady { get; set; }
        public string? LoopUrl { get; set; }
        public int LoopPageIndex { get; set; }
        public bool LastFailed { get; set; }

        public void Track(IPage page)
        {
            lock (Pages)
            {
                if (Pages.Contains(page))
                    return;
                Pages.Add(page);
            }

            onNewPage?.Invoke(page);
        }
    }

    private const string LogArea = "Oynatma";

    public async Task<PlaybackResult> RunAsync(
        Scenario scenario, PlaybackOptions options, Action<PlaybackEvent>? onEvent = null, CancellationToken ct = default)
    {
        var result = new PlaybackResult();
        var problems = ScenarioValidator.Validate(scenario, options.Data, UploadFiles.BaseDirectory(options));
        if (problems.Count > 0)
            throw new InvalidOperationException("Senaryo oynatılamıyor:\n- " + string.Join("\n- ", problems));

        var plan = BuildPlan(options);
        var total = plan.Count;
        AppLog.Info(LogArea, $"\"{scenario.Name}\" oynatılıyor: {total} tekrar" +
                             (options.Data is { } data ? $", veri: {Path.GetFileName(data.Path)}" : "") + (options.Headless ? ", görünmez" : "") + ".");
        void Emit(PlaybackEvent e) => onEvent?.Invoke(e);

        using var playwright = await Playwright.CreateAsync();
        var browser = await BrowserFactory.LaunchAsync(playwright, scenario.Browser, options.Headless,
            msg => Emit(new PlaybackEvent(PlaybackEventKind.Info, 0, total, Message: msg)));
        await using var browserScope = browser;
        // Bekleyen Playwright çağrıları iptal belirteci almadığından, iptalde tarayıcı kapatılarak sonlandırılır.
        await using var registration = ct.Register(() => _ = browser.CloseAsync());

        var flow = new ScenarioFlow(scenario);
        Session? shared = null;
        var fresh = options.FreshSessionPerIteration && !options.KeepPageOpen;
        try
        {
            foreach (var item in plan)
            {
                if (ct.IsCancellationRequested)
                    break;

                IterationResult iteration;
                var attempt = 0;
                while (true)
                {
                    attempt++;
                    Emit(new PlaybackEvent(PlaybackEventKind.IterationStarted, item.Iteration, total, RowNumber: item.RowNumber));
                    var session = fresh
                        ? await NewSessionAsync(browser, scenario, options)
                        : shared ??= await NewSessionAsync(browser, scenario, options);

                    iteration = await RunIterationAsync(flow, scenario, options, item, attempt, total, session, Emit, ct);

                    if (fresh)
                        await SafeCloseAsync(session.Context);
                    if (iteration.Success || ct.IsCancellationRequested || attempt > options.RetryCount)
                        break;
                    Emit(new PlaybackEvent(PlaybackEventKind.Info, item.Iteration, total, RowNumber: item.RowNumber,
                        Message: $"Tekrar {item.Iteration} hatalı, baştan yeniden deneniyor ({attempt}/{options.RetryCount})..."));
                }
                iteration = iteration with { Attempts = attempt };

                // İptal anında yarıda kalan tekrar sonuçlara hata olarak yazılmaz.
                if (ct.IsCancellationRequested && !iteration.Success)
                    break;
                result.Iterations.Add(iteration);
                Emit(new PlaybackEvent(PlaybackEventKind.IterationCompleted, item.Iteration, total,
                    Message: iteration.Success ? "Başarılı" : iteration.Error, RowNumber: item.RowNumber));

                if (!iteration.Success && !options.ContinueOnError)
                    break;
                if (options.DelayBetweenIterationsMs > 0 && item != plan[^1])
                    await Task.Delay(options.DelayBetweenIterationsMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (shared is not null)
                await SafeCloseAsync(shared.Context);
        }

        result.Cancelled = ct.IsCancellationRequested;
        AppLog.Info(LogArea, $"\"{scenario.Name}\" bitti: {result.Iterations.Count(i => i.Success)}/{result.Iterations.Count} tekrar başarılı" +
                             (result.Cancelled ? " (durduruldu)." : "."));
        return result;
    }

    private static List<PlanItem> BuildPlan(PlaybackOptions options)
    {
        var passes = Math.Max(1, options.Iterations);
        var plan = new List<PlanItem>();
        if (options.Data is null)
        {
            for (var i = 1; i <= passes; i++)
                plan.Add(new PlanItem(i, null, null));
            return plan;
        }

        var rows = options.Data.Rows;
        var first = Math.Clamp(options.FirstRow ?? 1, 1, Math.Max(1, rows.Count));
        var last = Math.Clamp(options.LastRow ?? rows.Count, first, rows.Count);
        var n = 0;
        for (var p = 0; p < passes; p++)
            for (var r = first; r <= last; r++)
                plan.Add(new PlanItem(++n, r, rows[r - 1]));
        if (plan.Count == 0)
            throw new InvalidOperationException("Veri kaynağında oynatılacak satır yok.");
        return plan;
    }

    private async Task<Session> NewSessionAsync(IBrowser browser, Scenario scenario, PlaybackOptions options)
    {
        var session = new Session(await NewContextAsync(browser, scenario, options), WatchFileChoosers);
        session.Context.Page += (_, page) => session.Track(page);
        return session;
    }

    private static async Task<IBrowserContext> NewContextAsync(IBrowser browser, Scenario scenario, PlaybackOptions options)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = BrowserFactory.ViewportFor(scenario.Browser) ?? new ViewportSize { Width = 1280, Height = 800 },
        });
        context.SetDefaultTimeout(options.ActionTimeoutMs);
        context.SetDefaultNavigationTimeout(options.NavigationTimeoutMs);
        return context;
    }

    private async Task<IterationResult> RunIterationAsync(
        ScenarioFlow flow, Scenario scenario, PlaybackOptions options, PlanItem item, int attempt, int total, Session session,
        Action<PlaybackEvent> emit, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var pages = session.Pages;
        var stepCount = scenario.Steps.Count;

        IterationResult Finish(bool success, int? stepIndex, string? error, string? shot = null)
        {
            session.LastFailed = !success;
            return new IterationResult(item.Iteration, item.RowNumber, success, stepIndex, error, watch.Elapsed, shot);
        }

        var loopStart = options.KeepPageOpen ? flow.LoopStart : null;
        Step? current;
        if (options.KeepPageOpen && session.LoopReady && HasOpenPage(session))
        {
            current = loopStart;
            try
            {
                await ReturnToLoopPageAsync(session, emit, item, total);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Sayfaya dönülemediyse bir sonraki deneme her şeye baştan başlar.
                session.LoopReady = false;
                return Finish(false, null, "Döngü sayfasına dönülemedi: " + CleanMessage(ex));
            }
        }
        else
        {
            // Paylaşılan oturumda önceki tekrarın sekmeleri kapatılır, tek temiz sekmeyle başlanır.
            foreach (var old in session.Context.Pages.ToList())
                await old.CloseAsync();
            lock (pages)
                pages.Clear();
            session.LoopReady = false;
            session.Track(await session.Context.NewPageAsync());
            current = flow.Start;
        }

        var firstStep = true;
        var visits = 0;
        while (current is not null)
        {
            var step = current;
            var i = flow.IndexOf(step);
            if (++visits > MaxStepVisitsPerIteration)
            {
                var loopMessage = $"Senaryo bir döngüden çıkamadı ({MaxStepVisitsPerIteration} adım çalıştı). Bağlantıları kontrol edin.";
                emit(new PlaybackEvent(PlaybackEventKind.StepFailed, item.Iteration, total, i, stepCount, loopMessage, item.RowNumber));
                return Finish(false, i, loopMessage);
            }

            if (!step.Enabled)
            {
                current = flow.Next(step, StepPort.Next);
                continue;
            }

            try
            {
                ct.ThrowIfCancellationRequested();

                if (step == loopStart && !session.LoopReady)
                {
                    // Döngü sayfası, başlangıç düğümü çalışmadan hemen önceki adrestir.
                    var loopPage = await GetPageAsync(pages, step.PageIndex, options.ActionTimeoutMs, ct);
                    session.LoopUrl = loopPage.Url;
                    session.LoopPageIndex = step.PageIndex;
                    session.LoopReady = true;
                }

                var delay = firstStep ? 0 : DelayFor(step, options);
                firstStep = false;
                if (delay > 0)
                    await Task.Delay(delay, ct);

                var value = ValueResolver.Resolve(step, item.Row, item.Iteration, item.RowNumber);
                emit(new PlaybackEvent(PlaybackEventKind.StepStarted, item.Iteration, total, i, stepCount,
                    StepDescriber.Describe(step, value), item.RowNumber));

                if (step.Type == StepType.End)
                {
                    emit(new PlaybackEvent(PlaybackEventKind.StepCompleted, item.Iteration, total, i, stepCount, RowNumber: item.RowNumber));
                    return step.EndAsFailure
                        ? Finish(false, i, "Senaryo \"Bitir (hatalı say)\" düğümünde sonlandı.")
                        : Finish(true, null, null);
                }

                var page = await GetPageAsync(pages, step.PageIndex, options.ActionTimeoutMs, ct);

                if (step.Type == StepType.Condition)
                {
                    var (passed, detail) = await EvaluateConditionAsync(page, step, value, item, ct);
                    var port = passed ? StepPort.Next : StepPort.False;
                    emit(new PlaybackEvent(PlaybackEventKind.Info, item.Iteration, total, i, stepCount,
                        $"{StepDescriber.Describe(step, value)} → {(passed ? "Evet" : "Hayır")}{(detail is null ? "" : $" ({detail})")}", item.RowNumber));

                    if (!passed && flow.Next(step, StepPort.False) is null)
                    {
                        // Hayır çıkışı bağlı değilse kullanıcı bu durumu düşünmemiştir: sessizce "başarılı" denmez.
                        throw new StepFailedException($"Kontrol sağlanmadı: {StepDescriber.Describe(step, value)}{(detail is null ? "" : $" ({detail})")}");
                    }

                    emit(new PlaybackEvent(PlaybackEventKind.StepCompleted, item.Iteration, total, i, stepCount,
                        passed ? "Evet" : "Hayır", item.RowNumber, port));
                    current = flow.Next(step, port);
                    continue;
                }

                await ExecuteAsync(page, step, value, item, options, msg =>
                    emit(new PlaybackEvent(PlaybackEventKind.Info, item.Iteration, total, i, stepCount, msg, item.RowNumber)), ct);

                emit(new PlaybackEvent(PlaybackEventKind.StepCompleted, item.Iteration, total, i, stepCount, RowNumber: item.RowNumber));
                current = flow.Next(step, StepPort.Next);
            }
            catch (Exception ex)
            {
                var cancelled = ct.IsCancellationRequested;
                var message = cancelled ? "İptal edildi" : CleanMessage(ex);
                // Beklenen adım hataları (öğe bulunamadı, zaman aşımı) kısa yazılır; beklenmeyenler ayrıntısıyla.
                if (!cancelled)
                    AppLog.Write(ex is StepFailedException or TimeoutException or PlaywrightException ? LogLevel.Warning : LogLevel.Error,
                        LogArea, $"Tekrar {item.Iteration}{(item.RowNumber is { } r ? $" (satır {r})" : "")}, adım {i + 1} ({step.Type}): {message}",
                        ex is StepFailedException or TimeoutException ? null : ex);

                if (!cancelled && flow.Next(step, StepPort.Error) is { } planB)
                {
                    emit(new PlaybackEvent(PlaybackEventKind.StepHandledError, item.Iteration, total, i, stepCount, message, item.RowNumber, StepPort.Error));
                    current = planB;
                    continue;
                }

                string? shot = null;
                if (!cancelled)
                    shot = await TryScreenshotAsync(pages, step.PageIndex, options, item.Iteration, attempt, i);
                emit(new PlaybackEvent(PlaybackEventKind.StepFailed, item.Iteration, total, i, stepCount, message, item.RowNumber));
                return Finish(false, i, message, shot);
            }
        }

        return Finish(true, null, null);
    }

    private static bool HasOpenPage(Session session)
    {
        lock (session.Pages)
            return session.LoopPageIndex < session.Pages.Count && !session.Pages[session.LoopPageIndex].IsClosed;
    }

    private static async Task ReturnToLoopPageAsync(Session session, Action<PlaybackEvent> emit, PlanItem item, int total)
    {
        IPage page;
        lock (session.Pages)
            page = session.Pages[session.LoopPageIndex];

        if (session.LoopUrl is null)
            return;
        // Hatalı tekrardan sonra sayfa yarım kalmış olabilir (açık pencere, dolu form): adres aynı olsa da yenilenir.
        if (!session.LastFailed && page.Url == session.LoopUrl)
            return;

        emit(new PlaybackEvent(PlaybackEventKind.Info, item.Iteration, total, RowNumber: item.RowNumber,
            Message: session.LastFailed ? "Önceki tekrar hatalı bitti, döngü sayfası yenileniyor." : "Döngü sayfasına dönülüyor."));
        await page.GotoAsync(session.LoopUrl);
    }

    /// <summary>
    /// Kontrol düğümünü sınar. Sayfa yüklenmesi sürüyor olabileceği için durum sağlanana kadar kısa süre yoklanır;
    /// süre dolarsa sonuç "Hayır"dır. Öğenin bulunamaması hata değil, "Hayır" sonucudur.
    /// </summary>
    private static async Task<(bool Passed, string? Detail)> EvaluateConditionAsync(
        IPage page, Step step, string? value, PlanItem item, CancellationToken ct)
    {
        var timeoutMs = step.TimeoutMs ?? DefaultConditionTimeoutMs;
        var kind = step.Condition ?? ConditionKind.Visible;
        var watch = Stopwatch.StartNew();
        string? detail = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var frame = await GetFrameAsync(page, step, 0, ct);
                bool passed;
                (passed, detail) = await CheckOnceAsync(frame, step, kind, value, item);
                if (passed)
                    return (true, null);
            }
            catch (StepFailedException ex)
            {
                detail = ex.Message;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // Sayfa o anda geçiş yapıyor olabilir; yeniden denenir.
                detail = "sayfa hazır değil";
            }

            if (watch.ElapsedMilliseconds >= timeoutMs)
                return (false, detail);
            await Task.Delay(PollIntervalMs, ct);
        }
    }

    private static async Task<(bool Passed, string? Detail)> CheckOnceAsync(
        IFrame frame, Step step, ConditionKind kind, string? expected, PlanItem item)
    {
        const int probeMs = 1000;
        if (kind == ConditionKind.PageHasText)
        {
            var count = await frame.GetByText(expected ?? "").Filter(new LocatorFilterOptions { Visible = true }).CountAsync();
            return (count > 0, count > 0 ? null : "yazı sayfada yok");
        }

        ILocator? target = null;
        foreach (var selector in step.Selectors)
        {
            var locator = frame.Locator(ValueResolver.ApplyTemplate(selector, item.Row, item.Iteration, item.RowNumber)!);
            int found;
            try
            {
                found = await locator.CountAsync();
            }
            catch (PlaywrightException)
            {
                continue; // Geçersiz seçici; sıradakine geç.
            }
            if (found == 0)
                continue;
            target = found == 1 ? locator : locator.First;
            break;
        }

        if (target is null)
            return (false, "öğe bulunamadı");

        switch (kind)
        {
            case ConditionKind.Visible:
                var visible = await target.IsVisibleAsync();
                return (visible, visible ? null : "öğe görünmüyor");
            case ConditionKind.Enabled:
                var enabled = await target.IsEnabledAsync(new LocatorIsEnabledOptions { Timeout = probeMs });
                return (enabled, enabled ? null : "öğe pasif");
            case ConditionKind.Disabled:
                var disabled = await target.IsDisabledAsync(new LocatorIsDisabledOptions { Timeout = probeMs });
                return (disabled, disabled ? null : "öğe aktif");
            case ConditionKind.Checked:
                var ticked = await target.IsCheckedAsync(new LocatorIsCheckedOptions { Timeout = probeMs });
                return (ticked, ticked ? null : "işaretli değil");
            case ConditionKind.Unchecked:
                var unticked = !await target.IsCheckedAsync(new LocatorIsCheckedOptions { Timeout = probeMs });
                return (unticked, unticked ? null : "işaretli");
            default:
                var text = await target.EvaluateAsync<string>(ReadTextScript, null, new LocatorEvaluateOptions { Timeout = probeMs });
                var passed = TextMatches(text, expected, kind);
                return (passed, passed ? null : $"bulunan: \"{Normalize(text)}\"");
        }
    }

    private const string ReadTextScript = """
        el => {
          if (el instanceof HTMLSelectElement) return Array.from(el.selectedOptions).map(o => o.label || o.textContent).join(', ');
          if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) return el.value;
          return el.innerText ?? el.textContent ?? '';
        }
        """;

    /// <summary>Boşluk farkları ve Türkçe kurallarıyla büyük/küçük harf farkı önemsenmez.</summary>
    public static bool TextMatches(string? actual, string? expected, ConditionKind kind)
    {
        var compare = System.Globalization.CultureInfo.GetCultureInfo("tr-TR").CompareInfo;
        var a = Normalize(actual);
        var b = Normalize(expected);
        return kind == ConditionKind.TextContains
            ? compare.IndexOf(a, b, System.Globalization.CompareOptions.IgnoreCase) >= 0
            : compare.Compare(a, b, System.Globalization.CompareOptions.IgnoreCase) == 0;
    }

    private static string Normalize(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Dal kaydı için: başlangıçtan bir düğüme giden yolu verilen sayfada, beklemesiz çalıştırır.
    /// Kontrol ve bitir düğümleri atlanır. Yol "Hata" çıkışından geçiyorsa o adımın hatası beklenen durumdur.
    /// </summary>
    /// <returns>Yol tamamlandıysa null, değilse hata mesajı.</returns>
    public async Task<string?> ReplayPathAsync(
        IPage firstPage, IReadOnlyList<(Step Step, StepPort Port)> path, PlaybackOptions options, Action<string> info, CancellationToken ct = default)
    {
        var session = new Session(firstPage.Context, WatchFileChoosers);
        session.Track(firstPage);
        foreach (var existing in firstPage.Context.Pages)
            session.Track(existing);
        void OnPage(object? _, IPage page) => session.Track(page);
        firstPage.Context.Page += OnPage;
        try
        {
            var item = new PlanItem(1, null, null);
            foreach (var (step, port) in path)
            {
                if (!step.Enabled || step.Type is StepType.Condition or StepType.End)
                    continue;
                try
                {
                    var page = await GetPageAsync(session.Pages, step.PageIndex, options.ActionTimeoutMs, ct);
                    await ExecuteAsync(page, step, ValueResolver.Resolve(step, null, 1, null), item, options, info, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (port != StepPort.Error)
                        return $"{StepDescriber.Describe(step)}: {CleanMessage(ex)}";
                }
            }

            return null;
        }
        finally
        {
            firstPage.Context.Page -= OnPage;
        }
    }

    private static int DelayFor(Step step, PlaybackOptions options)
    {
        if (options.FixedDelayMs is { } fixedDelay)
            return Math.Max(0, fixedDelay);
        var delay = step.DelayBeforeMs * Math.Max(0, options.SpeedFactor);
        if (options.MaxDelayMs is { } max)
            delay = Math.Min(delay, max);
        return (int)delay;
    }

    private async Task ExecuteAsync(
        IPage page, Step step, string? value, PlanItem item, PlaybackOptions options, Action<string> info, CancellationToken ct)
    {
        // Adıma özel zaman aşımı tanımlıysa Playwright işlemleri de o süreyi kullanır.
        var timeoutMs = step.TimeoutMs ?? options.ActionTimeoutMs;
        if (step.TimeoutMs is not null)
            page.SetDefaultTimeout(timeoutMs);
        try
        {
            await ExecuteCoreAsync(page, step, value, item, options, timeoutMs, info, ct);
        }
        finally
        {
            if (step.TimeoutMs is not null && !page.IsClosed)
                page.SetDefaultTimeout(options.ActionTimeoutMs);
        }
    }

    private async Task ExecuteCoreAsync(
        IPage page, Step step, string? value, PlanItem item, PlaybackOptions options, int timeoutMs,
        Action<string> info, CancellationToken ct)
    {
        switch (step.Type)
        {
            case StepType.Navigate:
                await page.GotoAsync(Recording.Recorder.NormalizeUrl(value ?? ""));
                return;
            case StepType.Wait:
                await Task.Delay(int.Parse(value ?? "0"), ct);
                return;
            case StepType.Press when step.Selectors.Count == 0:
                using (var watcher = new NavigationWatcher(page))
                {
                    await page.Keyboard.PressAsync(value ?? "Enter");
                    await watcher.WaitAsync(step, options, info, ct);
                }
                return;
            case StepType.WaitForText:
                var textFrame = await GetFrameAsync(page, step, timeoutMs, ct);
                var text = textFrame.GetByText(value ?? "").Filter(new LocatorFilterOptions { Visible = true }).First;
                try
                {
                    await text.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeoutMs });
                }
                catch (TimeoutException ex)
                {
                    throw new StepFailedException($"\"{value}\" yazısı {timeoutMs / 1000.0:0.#} sn içinde sayfada görünmedi.", ex);
                }
                return;
        }

        if (step.Type == StepType.Upload)
        {
            await UploadAsync(page, step, value, item, options, timeoutMs, info, ct);
            return;
        }

        var frame = await GetFrameAsync(page, step, timeoutMs, ct);
        var target = await ResolveTargetAsync(frame, step, value, item, timeoutMs, ct);

        if (step.Type == StepType.WaitForElement)
        {
            await target.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeoutMs });
            return;
        }

        using var navigation = new NavigationWatcher(page);
        switch (step.Type)
        {
            case StepType.Click:
                await target.ClickAsync();
                break;
            case StepType.DoubleClick:
                await target.DblClickAsync();
                break;
            case StepType.Hover:
                await target.HoverAsync();
                break;
            case StepType.Fill when options.TypeDelayMs > 0:
                await target.ClearAsync();
                await target.PressSequentiallyAsync(value ?? "", new LocatorPressSequentiallyOptions { Delay = options.TypeDelayMs });
                break;
            case StepType.Fill:
                await target.FillAsync(value ?? "");
                break;
            case StepType.Press:
                await target.PressAsync(value ?? "Enter");
                break;
            case StepType.Select:
                var options_ = step.Multiple ? (value ?? "").Split('|') : [value ?? ""];
                await target.SelectOptionAsync(options_);
                break;
            case StepType.Check:
            case StepType.Uncheck:
                var check = string.IsNullOrWhiteSpace(step.Parameter)
                    ? step.Type == StepType.Check
                    : ValueResolver.IsTruthy(value);
                await SetCheckedAsync(target, check);
                break;
            default:
                throw new StepFailedException($"Desteklenmeyen adım türü: {step.Type}");
        }

        await navigation.WaitAsync(step, options, info, ct);
    }

    /// <summary>
    /// Dosya yükler. Önce kayıtlı dosya girdisine doğrudan yazılır (gizli girdilerde de çalışır, pencere açılmaz).
    /// Girdi sayfada yoksa (site girdiyi tıklamayla anlık oluşturuyorsa) önceki adımın açtırdığı dosya seçme penceresi kullanılır.
    /// </summary>
    private async Task UploadAsync(
        IPage page, Step step, string? value, PlanItem item, PlaybackOptions options, int timeoutMs, Action<string> info, CancellationToken ct)
    {
        var resolved = UploadFiles.Resolve(value, UploadFiles.BaseDirectory(options));
        if (resolved.Missing.Count > 0)
            throw new StepFailedException($"Yüklenecek dosya bulunamadı: {string.Join(", ", resolved.Missing)}");
        var files = resolved.Files;
        if (files.Count == 0)
            info("Yüklenecek dosya yok (değer boş); dosya seçimi temizlendi.");
        else
            info($"Yükleniyor: {string.Join(", ", files.Select(Path.GetFileName))}");

        using var navigation = new NavigationWatcher(page);
        if (step.Selectors.Count == 0)
        {
            var chooser = TakeFileChooser(page) ?? await WaitForFileChooserAsync(page, timeoutMs, ct)
                ?? throw new StepFailedException(
                    $"Dosya seçme penceresi {timeoutMs / 1000.0:0.#} sn içinde açılmadı. Bir önceki adım dosya seçme butonuna tıklamalı.");
            await SetChooserFilesAsync(chooser, files);
        }
        else
        {
            ILocator target;
            try
            {
                var frame = await GetFrameAsync(page, step, timeoutMs, ct);
                target = await ResolveTargetAsync(frame, step, value, item, timeoutMs, ct);
            }
            catch (StepFailedException) when (TakeFileChooser(page) is { } chooser)
            {
                await SetChooserFilesAsync(chooser, files);
                await navigation.WaitAsync(step, options, info, ct);
                return;
            }

            try
            {
                await target.SetInputFilesAsync(files);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Non-multiple", StringComparison.OrdinalIgnoreCase))
            {
                throw new StepFailedException($"Bu alana tek dosya yüklenebilir ama {files.Count} dosya verildi: {string.Join(", ", files.Select(Path.GetFileName))}", ex);
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("not an HTMLInputElement", StringComparison.OrdinalIgnoreCase) ||
                                                 ex.Message.Contains("Node is not an", StringComparison.OrdinalIgnoreCase))
            {
                throw new StepFailedException("Hedef öğe bir dosya girdisi değil. Kaydı yeniden alın ya da seçiciyi input[type=file] öğesine yöneltin.", ex);
            }

            // Kullanılmayan pencere sonraki yükleme adımına kalmasın.
            TakeFileChooser(page);
        }

        await navigation.WaitAsync(step, options, info, ct);
    }

    private static async Task SetChooserFilesAsync(IFileChooser chooser, IReadOnlyList<string> files)
    {
        if (!chooser.IsMultiple && files.Count > 1)
            throw new StepFailedException($"Bu alana tek dosya yüklenebilir ama {files.Count} dosya verildi: {string.Join(", ", files.Select(Path.GetFileName))}");
        await chooser.SetFilesAsync(files);
    }

    private async Task<IFileChooser?> WaitForFileChooserAsync(IPage page, int timeoutMs, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (TakeFileChooser(page) is { } chooser)
                return chooser;
            await Task.Delay(PollIntervalMs, ct);
        }

        return null;
    }

    // Görsel olarak gizlenmiş (özel tasarımlı) onay kutularında Playwright tıklayamaz;
    // bu durumda öğenin kendi click() metodu çağrılır ki sitenin olay dinleyicileri de çalışsın.
    private static async Task SetCheckedAsync(ILocator target, bool check)
    {
        if (await target.IsVisibleAsync())
        {
            await target.SetCheckedAsync(check);
            return;
        }
        await target.EvaluateAsync("(el, v) => { if (el.checked !== v) el.click(); }", check);
    }

    private static async Task<IPage> GetPageAsync(List<IPage> pages, int index, int timeoutMs, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            lock (pages)
            {
                if (index < pages.Count)
                    return pages[index];
            }
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new StepFailedException($"{index + 1}. sekme açılmadı.");
            await Task.Delay(PollIntervalMs, ct);
        }
    }

    private static async Task<IFrame> GetFrameAsync(IPage page, Step step, int timeoutMs, CancellationToken ct)
    {
        if (step.FrameName is null && step.FrameUrl is null)
            return page.MainFrame;

        var wantedUrl = StripQuery(step.FrameUrl);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var frame = page.Frames.FirstOrDefault(f => step.FrameName is not null && f.Name == step.FrameName)
                        ?? page.Frames.FirstOrDefault(f => f != page.MainFrame && f.Url == step.FrameUrl)
                        ?? page.Frames.FirstOrDefault(f => f != page.MainFrame && wantedUrl is not null && StripQuery(f.Url) == wantedUrl);
            if (frame is not null)
                return frame;
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new StepFailedException($"Çerçeve (iframe) bulunamadı: {step.FrameName ?? step.FrameUrl}");
            await Task.Delay(PollIntervalMs, ct);
        }
    }

    private static string? StripQuery(string? url)
    {
        if (url is null)
            return null;
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    }

    /// <summary>
    /// Aday seçicileri sırayla dener ve sayfada tek bir öğeye karşılık gelen ilkini seçer.
    /// Böylece sayfa biraz değişse bile yedek seçicilerle oynatma sürer.
    /// </summary>
    private static async Task<ILocator> ResolveTargetAsync(
        IFrame frame, Step step, string? value, PlanItem item, int timeoutMs, CancellationToken ct)
    {
        List<ILocator> candidates;
        if (step.Type is StepType.Click or StepType.DoubleClick or StepType.Hover && !string.IsNullOrWhiteSpace(step.Parameter))
        {
            // Parametreli tıklama: veri kaynağındaki metni taşıyan öğeye tıklanır (ör. özel açılır listeler).
            var text = value ?? "";
            candidates = [frame.GetByText(text, new FrameGetByTextOptions { Exact = true }), frame.GetByText(text)];
        }
        else
        {
            candidates = step.Selectors
                .Select(s => ValueResolver.ApplyTemplate(s, item.Row, item.Iteration, item.RowNumber)!)
                .Select(s => frame.Locator(s))
                .ToList();
        }

        var watch = Stopwatch.StartNew();
        ILocator? fallback = null;
        long fallbackSince = 0;
        while (true)
        {
            foreach (var locator in candidates)
            {
                int count;
                try
                {
                    count = await locator.CountAsync();
                }
                catch (PlaywrightException)
                {
                    continue; // Geçersiz ya da desteklenmeyen seçici; sıradakine geç.
                }
                if (count == 1)
                    return locator;
                if (count > 1)
                {
                    var visible = locator.Filter(new LocatorFilterOptions { Visible = true });
                    var visibleCount = await visible.CountAsync();
                    if (visibleCount == 1)
                        return visible;
                    if (fallback is null)
                    {
                        fallback = visibleCount > 0 ? visible.First : locator.First;
                        fallbackSince = watch.ElapsedMilliseconds;
                    }
                }
            }

            if (fallback is not null && watch.ElapsedMilliseconds - fallbackSince >= AmbiguityGraceMs)
                return fallback;
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new StepFailedException($"Öğe bulunamadı: {StepDescriber.Target(step)}");
            await Task.Delay(PollIntervalMs, ct);
        }
    }

    private static async Task<string?> TryScreenshotAsync(
        List<IPage> pages, int pageIndex, PlaybackOptions options, int iteration, int attempt, int stepIndex)
    {
        if (options.ScreenshotDirectory is null)
            return null;
        IPage? page;
        lock (pages)
            page = pageIndex < pages.Count ? pages[pageIndex] : pages.LastOrDefault();
        if (page is null || page.IsClosed)
            return null;
        try
        {
            Directory.CreateDirectory(options.ScreenshotDirectory);
            var path = Path.Combine(options.ScreenshotDirectory, attempt > 1
                ? $"tekrar{iteration:D4}-deneme{attempt}-adim{stepIndex + 1:D3}.png"
                : $"tekrar{iteration:D4}-adim{stepIndex + 1:D3}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, Timeout = 5000 });
            return path;
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private static async Task SafeCloseAsync(IBrowserContext context)
    {
        try
        {
            await context.CloseAsync();
        }
        catch (PlaywrightException)
        {
        }
    }

    // Playwright hata mesajlarının ilk satırı kullanıcıya yeterli; ayrıntılı çağrı günlüğü atılır.
    private static string CleanMessage(Exception ex)
    {
        var line = ex.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? ex.GetType().Name;
        if (ex is TimeoutException || line.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return "Zaman aşımı: öğe işlem yapılabilir hale gelmedi. " + line;
        return line;
    }
}
