using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using WebAutomater.Core.Models;
using WebAutomater.Core.Recording;
using WebAutomater.Core.Storage;
using WebAutomater.Core.Text;
using Recorder = WebAutomater.Core.Recording.Recorder;

namespace WebAutomater.Cli.Commands;

internal sealed class RecordCommand : AsyncCommand<RecordCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<url>")]
        [Description("Kayda başlanacak adres.")]
        public string Url { get; init; } = "";

        [CommandOption("-o|--output")]
        [Description("Senaryonun kaydedileceği dosya. Varsayılan: <ad>.wa.json")]
        public string? Output { get; init; }

        [CommandOption("--name")]
        [Description("Senaryo adı.")]
        public string? Name { get; init; }

        [CommandOption("-b|--browser")]
        [Description("chromium (varsayılan), firefox veya webkit.")]
        public string Browser { get; init; } = "chromium";

        [CommandOption("--channel")]
        [Description("Bilgisayarda yüklü tarayıcıyı kullan: chrome veya msedge.")]
        public string? Channel { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var name = settings.Name ?? (Uri.TryCreate(Recorder.NormalizeUrl(settings.Url), UriKind.Absolute, out var u) ? u.Host : "senaryo");
        var scenario = new Scenario
        {
            Name = name,
            Browser = new BrowserSettings { Engine = settings.Browser, Channel = settings.Channel },
        };
        var output = settings.Output ?? ScenarioStore.SuggestFileName(name);

        await using var recorder = new Recorder(scenario)
        {
            Log = msg => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(msg)}[/]"),
        };
        recorder.StepChanged += (_, e) =>
        {
            if (e.Kind == StepChangeKind.Added)
                AnsiConsole.MarkupLine($"  [green]+[/] {e.Index + 1,3}. {Markup.Escape(StepDescriber.Describe(e.Step))}");
        };

        AnsiConsole.MarkupLine("[bold]Kayıt başlıyor.[/] Tarayıcıda işlemlerinizi yapın.");
        AnsiConsole.MarkupLine("Bitirmek için tarayıcıyı kapatın ya da burada [bold]Enter[/] tuşuna basın.\n");
        await recorder.StartAsync(settings.Url, ct);

        var enter = Task.Run(() =>
        {
            if (!Console.IsInputRedirected)
                Console.ReadLine();
            else
                Thread.Sleep(Timeout.Infinite);
        }, CancellationToken.None);
        await Task.WhenAny(recorder.Completion, enter, Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, CancellationToken.None));
        await recorder.StopAsync();

        await ScenarioStore.SaveAsync(scenario, output, CancellationToken.None);
        AnsiConsole.WriteLine();
        Shared.PrintSteps(scenario);
        AnsiConsole.MarkupLine($"\n[green]Kaydedildi:[/] {Markup.Escape(Path.GetFullPath(output))}");
        AnsiConsole.MarkupLine($"Oynatmak için: [bold]wa play \"{Markup.Escape(output)}\" -n 10[/]");
        return 0;
    }
}
