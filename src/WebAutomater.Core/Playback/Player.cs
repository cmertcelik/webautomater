using System.Diagnostics;
using Microsoft.Playwright;
using WebAutomater.Core.Browsers;
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

    private sealed record PlanItem(int Iteration, int? RowNumber, IReadOnlyDictionary<string, string>? Row);

    public async Task<PlaybackResult> RunAsync(
        Scenario scenario, PlaybackOptions options, Action<PlaybackEvent>? onEvent = null, CancellationToken ct = default)
    {
        var result = new PlaybackResult();
        var problems = ScenarioValidator.Validate(scenario, options.Data);
        if (problems.Count > 0)
            throw new InvalidOperationException("Senaryo oynatılamıyor:\n- " + string.Join("\n- ", problems));

        var plan = BuildPlan(options);
        var total = plan.Count;
        void Emit(PlaybackEvent e) => onEvent?.Invoke(e);

        using var playwright = await Playwright.CreateAsync();
        var browser = await BrowserFactory.LaunchAsync(playwright, scenario.Browser, options.Headless,
            msg => Emit(new PlaybackEvent(PlaybackEventKind.Info, 0, total, Message: msg)));
        await using var browserScope = browser;
        // Bekleyen Playwright çağrıları iptal belirteci almadığından, iptalde tarayıcı kapatılarak sonlandırılır.
        await using var registration = ct.Register(() => _ = browser.CloseAsync());

        IBrowserContext? sharedContext = null;
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
                    var context = options.FreshSessionPerIteration
                        ? await NewContextAsync(browser, scenario, options)
                        : sharedContext ??= await NewContextAsync(browser, scenario, options);

                    iteration = await RunIterationAsync(scenario, options, item, attempt, total, context, Emit, ct);

                    if (options.FreshSessionPerIteration)
                        await SafeCloseAsync(context);
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
            if (sharedContext is not null)
                await SafeCloseAsync(sharedContext);
        }

        result.Cancelled = ct.IsCancellationRequested;
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
        Scenario scenario, PlaybackOptions options, PlanItem item, int attempt, int total, IBrowserContext context,
        Action<PlaybackEvent> emit, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var pages = new List<IPage>();
        void Track(IPage p)
        {
            lock (pages)
                if (!pages.Contains(p))
                    pages.Add(p);
        }
        context.Page += (_, p) => Track(p);

        // Paylaşılan oturumda önceki tekrarın sekmeleri kapatılır, tek temiz sekmeyle başlanır.
        foreach (var old in context.Pages.ToList())
            await old.CloseAsync();
        Track(await context.NewPageAsync());

        var steps = scenario.Steps;
        var stepCount = steps.Count;
        var firstStep = true;
        for (var i = 0; i < stepCount; i++)
        {
            var step = steps[i];
            if (!step.Enabled)
                continue;

            try
            {
                ct.ThrowIfCancellationRequested();
                var delay = firstStep ? 0 : DelayFor(step, options);
                firstStep = false;
                if (delay > 0)
                    await Task.Delay(delay, ct);

                var value = ValueResolver.Resolve(step, item.Row, item.Iteration, item.RowNumber);
                emit(new PlaybackEvent(PlaybackEventKind.StepStarted, item.Iteration, total, i, stepCount,
                    StepDescriber.Describe(step, value), item.RowNumber));

                var page = await GetPageAsync(pages, step.PageIndex, options.ActionTimeoutMs, ct);
                await ExecuteAsync(page, step, value, item, options, msg =>
                    emit(new PlaybackEvent(PlaybackEventKind.Info, item.Iteration, total, i, stepCount, msg, item.RowNumber)), ct);

                emit(new PlaybackEvent(PlaybackEventKind.StepCompleted, item.Iteration, total, i, stepCount, RowNumber: item.RowNumber));
            }
            catch (Exception ex)
            {
                var cancelled = ct.IsCancellationRequested;
                var message = cancelled ? "İptal edildi" : CleanMessage(ex);
                string? shot = null;
                if (!cancelled)
                    shot = await TryScreenshotAsync(pages, step.PageIndex, options, item.Iteration, attempt, i);
                emit(new PlaybackEvent(PlaybackEventKind.StepFailed, item.Iteration, total, i, stepCount, message, item.RowNumber));
                return new IterationResult(item.Iteration, item.RowNumber, false, i, message, watch.Elapsed, shot);
            }
        }
        return new IterationResult(item.Iteration, item.RowNumber, true, null, null, watch.Elapsed, null);
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
            case StepType.Upload:
                var files = (value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var missing = files.Where(f => !File.Exists(f)).ToList();
                if (missing.Count > 0)
                    throw new StepFailedException($"Yüklenecek dosya bulunamadı: {string.Join(", ", missing)} (adımda tam dosya yolunu girin)");
                await target.SetInputFilesAsync(files);
                break;
            default:
                throw new StepFailedException($"Desteklenmeyen adım türü: {step.Type}");
        }

        await navigation.WaitAsync(step, options, info, ct);
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
