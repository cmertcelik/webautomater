using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using WebAutomater.Core.Models;
using WebAutomater.Core.Storage;
using WebAutomater.Core.Text;

namespace WebAutomater.Cli.Commands;

internal sealed class AddWaitCommand : AsyncCommand<AddWaitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<senaryo>")]
        [Description("Senaryo dosyası (.wa.json).")]
        public string Scenario { get; init; } = "";

        [CommandOption("-a|--after")]
        [Description("Bekleme bu numaralı adımdan sonra eklenir (--ms ve --text için). Verilmezse sona eklenir.")]
        public int? After { get; init; }

        [CommandOption("--ms")]
        [Description("Sabit süre bekle (ms).")]
        public int? Milliseconds { get; init; }

        [CommandOption("--text")]
        [Description("Bu yazı sayfada görünene kadar bekle.")]
        public string? Text { get; init; }

        [CommandOption("--element")]
        [Description("Bu numaralı adımın öğesi görünene kadar bekle; bekleme o adımın önüne eklenir.")]
        public int? Element { get; init; }

        [CommandOption("--timeout")]
        [Description("Yazı/öğe beklemesinde en uzun süre (ms).")]
        [DefaultValue(60000)]
        public int Timeout { get; init; } = 60_000;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var given = new object?[] { settings.Milliseconds, settings.Text, settings.Element }.Count(o => o is not null);
        if (given != 1)
            return Shared.Error("--ms, --text veya --element seçeneklerinden tam olarak birini verin.");

        var scenario = await ScenarioStore.LoadAsync(settings.Scenario, ct);
        Step step;
        int index;
        if (settings.Element is { } number)
        {
            var target = Shared.GetStep(scenario, number);
            if (target.Selectors.Count == 0)
                return Shared.Error($"{number}. adımın bir öğesi yok.");
            step = StepDescriber.WaitForElementOf(target, settings.Timeout);
            index = number - 1;
        }
        else
        {
            step = settings.Text is not null
                ? new Step { Type = StepType.WaitForText, Value = settings.Text, TimeoutMs = settings.Timeout }
                : new Step { Type = StepType.Wait, Value = settings.Milliseconds!.Value.ToString() };
            if (settings.After is { } after)
                Shared.GetStep(scenario, after);
            index = settings.After ?? scenario.Steps.Count;
        }

        scenario.Steps.Insert(index, step);
        await ScenarioStore.SaveAsync(scenario, settings.Scenario, ct);
        Shared.PrintSteps(scenario);
        AnsiConsole.MarkupLine($"[green]{index + 1}. adım olarak eklendi.[/]");
        return 0;
    }
}
