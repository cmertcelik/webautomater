using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using WebAutomater.Core.Data;
using WebAutomater.Core.Playback;
using WebAutomater.Core.Storage;

namespace WebAutomater.Cli.Commands;

internal sealed class PlayCommand : AsyncCommand<PlayCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<senaryo>")]
        [Description("Senaryo dosyası (.wa.json).")]
        public string Scenario { get; init; } = "";

        [CommandOption("-n|--times")]
        [Description("Tekrar sayısı. Veri kaynağıyla kullanılırsa tüm satırlar bu kadar tur döner.")]
        [DefaultValue(1)]
        public int Times { get; init; } = 1;

        [CommandOption("-d|--data")]
        [Description("Veri kaynağı (Excel/CSV/JSON). Her satır için bir tekrar yapılır.")]
        public string? Data { get; init; }

        [CommandOption("--sheet")]
        [Description("Excel çalışma sayfası adı.")]
        public string? Sheet { get; init; }

        [CommandOption("--use-saved-data")]
        [Description("Senaryoda hatırlanan veri kaynağını kullan.")]
        public bool UseSavedData { get; init; }

        [CommandOption("--from")]
        [Description("İlk veri satırı (1'den başlar).")]
        public int? From { get; init; }

        [CommandOption("--to")]
        [Description("Son veri satırı (dahil).")]
        public int? To { get; init; }

        [CommandOption("--map")]
        [Description("Adım=kolon eşleştirmesi, birden çok verilebilir: --map 3=isim --map 5=telefon")]
        public string[] Map { get; init; } = [];

        [CommandOption("--headless")]
        [Description("Tarayıcı penceresini göstermeden çalıştır.")]
        public bool Headless { get; init; }

        [CommandOption("--speed")]
        [Description("Kayıttaki bekleme sürelerinin çarpanı: 1 = aynı hız, 0.5 = iki kat hızlı, 0 = beklemesiz.")]
        [DefaultValue(1.0)]
        public double Speed { get; init; } = 1.0;

        [CommandOption("--fixed-delay")]
        [Description("Kayıttaki süreler yerine her adımdan önce sabit bekleme (ms).")]
        public int? FixedDelay { get; init; }

        [CommandOption("--max-delay")]
        [Description("Tek bir beklemenin üst sınırı (ms).")]
        public int? MaxDelay { get; init; }

        [CommandOption("--timeout")]
        [Description("Sayfa/öğe geç yüklenirse en uzun bekleme (ms); öğe gelir gelmez devam edilir.")]
        [DefaultValue(15000)]
        public int Timeout { get; init; } = 15000;

        [CommandOption("--type-delay")]
        [Description("Metinleri tuş tuş yaz; tuşlar arası bekleme (ms).")]
        [DefaultValue(0)]
        public int TypeDelay { get; init; }

        [CommandOption("--pause")]
        [Description("Tekrarlar arası bekleme (ms).")]
        [DefaultValue(0)]
        public int Pause { get; init; }

        [CommandOption("--retries")]
        [Description("Hatalı tekrarı baştan yeniden deneme sayısı (geçici ağ/sunucu aksaklıkları için).")]
        [DefaultValue(0)]
        public int Retries { get; init; }

        [CommandOption("--stop-on-error")]
        [Description("İlk hatada dur (varsayılan: sonraki tekrara geç).")]
        public bool StopOnError { get; init; }

        [CommandOption("--keep-session")]
        [Description("Tekrarlar arasında çerez/oturumu koru (ör. bir kez giriş yapıp devam etmek için).")]
        public bool KeepSession { get; init; }

        [CommandOption("--keep-page")]
        [Description("Sayfayı kapatmadan tekrarla: ilk tekrar baştan, sonrakiler döngü başlangıcından çalışır.")]
        public bool KeepPage { get; init; }

        [CommandOption("--report")]
        [Description("Sonuçların yazılacağı CSV dosyası.")]
        public string? Report { get; init; }

        [CommandOption("--screenshots")]
        [Description("Hatalı adımların ekran görüntüleri için klasör. Varsayılan: ./wa-hatalar")]
        public string? Screenshots { get; init; }

        [CommandOption("-v|--verbose")]
        [Description("Her adımı yazdır.")]
        public bool Verbose { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var scenario = await ScenarioStore.LoadAsync(settings.Scenario, ct);

        foreach (var map in settings.Map)
        {
            var parts = map.Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var no))
                return Shared.Error($"--map biçimi 'adım=kolon' olmalı: {map}");
            Shared.GetStep(scenario, no).Parameter = parts[1].Trim();
        }

        DataTableSource? data = null;
        var dataPath = settings.Data ?? (settings.UseSavedData ? scenario.DataSourcePath : null);
        if (dataPath is not null)
        {
            data = DataSourceLoader.Load(dataPath, settings.Sheet ?? (settings.Data is null ? scenario.DataSourceSheet : null));
            AnsiConsole.MarkupLine($"Veri: [bold]{Markup.Escape(Path.GetFileName(data.Path))}[/] — {data.Rows.Count} satır, kolonlar: {Markup.Escape(string.Join(", ", data.Columns))}");
        }
        else if (scenario.BoundParameters.Any())
        {
            AnsiConsole.MarkupLine("[yellow]Uyarı:[/] Bazı adımlar kolonlara bağlı ama veri kaynağı verilmedi; kayıtlı değerler kullanılacak. (--data ya da --use-saved-data)");
        }

        var options = new PlaybackOptions
        {
            Iterations = settings.Times,
            Data = data,
            FirstRow = settings.From,
            LastRow = settings.To,
            Headless = settings.Headless,
            SpeedFactor = settings.Speed,
            FixedDelayMs = settings.FixedDelay,
            MaxDelayMs = settings.MaxDelay,
            ActionTimeoutMs = settings.Timeout,
            TypeDelayMs = settings.TypeDelay,
            DelayBetweenIterationsMs = settings.Pause,
            RetryCount = Math.Max(0, settings.Retries),
            ContinueOnError = !settings.StopOnError,
            FreshSessionPerIteration = !settings.KeepSession,
            KeepPageOpen = settings.KeepPage,
            ScreenshotDirectory = settings.Screenshots ?? Path.Combine(Environment.CurrentDirectory, "wa-hatalar"),
        };

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(scenario.Name)}[/] oynatılıyor... (durdurmak için Ctrl+C)\n");
        var result = await new Player().RunAsync(scenario, options, e => Print(e, settings.Verbose), ct);

        AnsiConsole.WriteLine();
        var summary = $"[green]{result.SuccessCount} başarılı[/], [red]{result.FailureCount} hatalı[/]";
        AnsiConsole.MarkupLine(result.Cancelled ? $"[yellow]Durduruldu.[/] {summary}" : $"Bitti: {summary}");

        if (settings.Report is not null)
        {
            await ReportWriter.WriteCsvAsync(result, settings.Report);
            AnsiConsole.MarkupLine($"Rapor: {Markup.Escape(Path.GetFullPath(settings.Report))}");
        }
        return result.FailureCount == 0 && !result.Cancelled ? 0 : 2;
    }

    private static void Print(PlaybackEvent e, bool verbose)
    {
        var prefix = $"[grey][[{e.Iteration}/{e.TotalIterations}]][/]";
        var row = e.RowNumber is { } r ? $" satır {r}" : "";
        switch (e.Kind)
        {
            case PlaybackEventKind.Info:
                AnsiConsole.MarkupLine($"{(e.Iteration > 0 ? prefix + " " : "")}[grey]{Markup.Escape(e.Message ?? "")}[/]");
                break;
            case PlaybackEventKind.StepStarted when verbose:
                AnsiConsole.MarkupLine($"{prefix}   {e.StepIndex + 1}. {Markup.Escape(e.Message ?? "")}");
                break;
            case PlaybackEventKind.StepFailed:
                AnsiConsole.MarkupLine($"{prefix}{row} [red]✗ adım {e.StepIndex + 1}:[/] {Markup.Escape(e.Message ?? "")}");
                break;
            case PlaybackEventKind.StepHandledError:
                AnsiConsole.MarkupLine($"{prefix}{row} [yellow]↷ adım {e.StepIndex + 1} hatalı, B planından devam:[/] {Markup.Escape(e.Message ?? "")}");
                break;
            case PlaybackEventKind.IterationCompleted when e.Message == "Başarılı":
                AnsiConsole.MarkupLine($"{prefix}{row} [green]✓[/]");
                break;
        }
    }
}
