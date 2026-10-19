using System.ComponentModel;
using Spectre.Console.Cli;
using WebAutomater.Core.Storage;

namespace WebAutomater.Cli.Commands;

internal sealed class ShowCommand : AsyncCommand<ShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<senaryo>")]
        [Description("Senaryo dosyası (.wa.json).")]
        public string Scenario { get; init; } = "";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var scenario = await ScenarioStore.LoadAsync(settings.Scenario, ct);
        Shared.PrintSteps(scenario);
        return 0;
    }
}
