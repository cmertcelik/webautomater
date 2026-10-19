using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using WebAutomater.Core.Data;
using WebAutomater.Core.Models;
using WebAutomater.Core.Storage;
using WebAutomater.Core.Text;

namespace WebAutomater.Cli.Commands;

internal sealed class BindCommand : AsyncCommand<BindCommand.Settings>
{
    private const string Constant = "(kayıtlı değeri kullan)";
    private const string Skip = "(bu adımı atla, değiştirme)";

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<senaryo>")]
        [Description("Senaryo dosyası (.wa.json).")]
        public string Scenario { get; init; } = "";

        [CommandOption("-d|--data")]
        [Description("Kolonları okunacak veri kaynağı. Verilirse etkileşimli eşleştirme açılır.")]
        public string? Data { get; init; }

        [CommandOption("--sheet")]
        [Description("Excel çalışma sayfası adı.")]
        public string? Sheet { get; init; }

        [CommandOption("-s|--step")]
        [Description("Bağlanacak adım numarası.")]
        public int? Step { get; init; }

        [CommandOption("-c|--column")]
        [Description("Adıma bağlanacak kolon adı.")]
        public string? Column { get; init; }

        [CommandOption("--clear")]
        [Description("Adımın kolon bağlantısını kaldırır.")]
        public bool Clear { get; init; }

        [CommandOption("--all")]
        [Description("Etkileşimli modda tıklama ve tuş adımlarını da sor.")]
        public bool All { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var scenario = await ScenarioStore.LoadAsync(settings.Scenario, ct);
        var dataPath = settings.Data ?? scenario.DataSourcePath;
        var data = dataPath is null ? null : DataSourceLoader.Load(dataPath, settings.Sheet ?? scenario.DataSourceSheet);

        if (settings.Step is { } number)
        {
            var step = Shared.GetStep(scenario, number);
            if (settings.Clear)
            {
                step.Parameter = null;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(settings.Column))
                    return Shared.Error("--column veya --clear belirtin.");
                if (!StepDescriber.IsParameterizable(step.Type))
                    return Shared.Error($"'{StepDescriber.Action(step.Type)}' adımı veri kolonuna bağlanamaz.");
                if (data is not null && !data.HasColumn(settings.Column))
                    return Shared.Error($"'{settings.Column}' kolonu yok. Kolonlar: {string.Join(", ", data.Columns)}");
                step.Parameter = data?.Columns.First(c => c.Equals(settings.Column, StringComparison.OrdinalIgnoreCase)) ?? settings.Column;
            }
        }
        else
        {
            if (data is null)
                return Shared.Error("Etkileşimli eşleştirme için --data ile bir veri kaynağı verin.");
            if (Console.IsInputRedirected)
                return Shared.Error("Etkileşimli mod terminal gerektirir; --step ve --column kullanın.");
            Interactive(scenario, data, settings.All);
        }

        if (data is not null)
        {
            scenario.DataSourcePath = Path.GetFullPath(data.Path);
            scenario.DataSourceSheet = data.Sheet;
        }
        await ScenarioStore.SaveAsync(scenario, settings.Scenario, ct);
        Shared.PrintSteps(scenario);
        AnsiConsole.MarkupLine("[green]Bağlantılar kaydedildi.[/]");
        return 0;
    }

    private static void Interactive(Scenario scenario, DataTableSource data, bool all)
    {
        Shared.PrintData(data, 3);
        var primary = new[] { StepType.Fill, StepType.Select, StepType.Check, StepType.Uncheck, StepType.Upload };
        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            if (!step.Enabled || !StepDescriber.IsParameterizable(step.Type))
                continue;
            if (!all && !primary.Contains(step.Type) && string.IsNullOrWhiteSpace(step.Parameter))
                continue;

            var current = string.IsNullOrWhiteSpace(step.Parameter) ? "" : $" [cyan](şu an: {Markup.Escape(step.Parameter)})[/]";
            var prompt = new SelectionPrompt<string>()
                .Title($"\n[bold]{i + 1}.[/] {Markup.Escape(StepDescriber.Describe(step))}{current}\n[grey]{Markup.Escape(StepDescriber.ParameterHint(step.Type))}[/]")
                .PageSize(12)
                .AddChoices([Skip, Constant, .. data.Columns]);
            var choice = AnsiConsole.Prompt(prompt);
            if (choice == Skip)
                continue;
            step.Parameter = choice == Constant ? null : choice;
        }
    }
}
