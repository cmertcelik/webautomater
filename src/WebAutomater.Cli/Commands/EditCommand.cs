using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using WebAutomater.Core.Storage;

namespace WebAutomater.Cli.Commands;

internal sealed class EditCommand : AsyncCommand<EditCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<senaryo>")]
        [Description("Senaryo dosyası (.wa.json).")]
        public string Scenario { get; init; } = "";

        [CommandOption("-s|--step")]
        [Description("Düzenlenecek adım numarası.")]
        public int Step { get; init; }

        [CommandOption("--value")]
        [Description("Yeni değer (yazılan metin, adres, seçenek...). {{kolon}} şablonu kullanılabilir.")]
        public string? Value { get; init; }

        [CommandOption("--delay")]
        [Description("Adımdan önceki bekleme (ms).")]
        public int? Delay { get; init; }

        [CommandOption("--timeout")]
        [Description("Bu adım için en uzun bekleme (ms); 0 verilirse genel ayar kullanılır.")]
        public int? Timeout { get; init; }

        [CommandOption("--disable")]
        [Description("Adımı oynatmada atla.")]
        public bool Disable { get; init; }

        [CommandOption("--enable")]
        [Description("Atlanan adımı tekrar etkinleştir.")]
        public bool Enable { get; init; }

        [CommandOption("--delete")]
        [Description("Adımı sil.")]
        public bool Delete { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var scenario = await ScenarioStore.LoadAsync(settings.Scenario, ct);
        var step = Shared.GetStep(scenario, settings.Step);

        if (settings.Delete)
            scenario.Steps.Remove(step);
        if (settings.Value is not null)
        {
            step.Value = settings.Value;
            step.DisplayValue = null;
        }
        if (settings.Delay is { } delay)
            step.DelayBeforeMs = Math.Max(0, delay);
        if (settings.Timeout is { } timeout)
            step.TimeoutMs = timeout > 0 ? timeout : null;
        if (settings.Disable)
            step.Enabled = false;
        if (settings.Enable)
            step.Enabled = true;

        await ScenarioStore.SaveAsync(scenario, settings.Scenario, ct);
        Shared.PrintSteps(scenario);
        AnsiConsole.MarkupLine("[green]Kaydedildi.[/]");
        return 0;
    }
}
