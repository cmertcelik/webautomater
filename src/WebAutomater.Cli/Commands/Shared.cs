using Spectre.Console;
using WebAutomater.Core.Data;
using WebAutomater.Core.Models;
using WebAutomater.Core.Text;

namespace WebAutomater.Cli.Commands;

internal static class Shared
{
    public static void PrintSteps(Scenario scenario)
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("#").RightAligned());
        table.AddColumn("İşlem");
        table.AddColumn("Hedef");
        table.AddColumn("Değer");
        table.AddColumn(new TableColumn("Bekleme").RightAligned());
        table.AddColumn("Kolon");

        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var s = scenario.Steps[i];
            var flags = (s.WaitForNavigation ? " [grey]↪ sayfa[/]" : "") + (s.TimeoutMs is { } t ? $" [grey]≤{t / 1000.0:0.#}sn[/]" : "");
            var dim = s.Enabled ? "" : "[grey strikethrough]";
            var end = s.Enabled ? "" : "[/]";
            table.AddRow(
                $"{dim}{i + 1}{end}",
                $"{dim}{Markup.Escape(StepDescriber.Action(s.Type))}{end}{flags}",
                $"{dim}{Markup.Escape(Truncate(StepDescriber.Target(s), 40))}{end}",
                $"{dim}{Markup.Escape(Truncate(StepDescriber.Value(s), 40))}{end}",
                $"{dim}{s.DelayBeforeMs / 1000.0:0.0} sn{end}",
                string.IsNullOrWhiteSpace(s.Parameter) ? "" : $"[cyan]{Markup.Escape(s.Parameter)}[/]");
        }

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(scenario.Name)}[/]  [grey]{Markup.Escape(scenario.StartUrl ?? "")}[/]");
        AnsiConsole.Write(table);
    }

    public static void PrintData(DataTableSource data, int previewRows = 5)
    {
        if (data.Sheets.Count > 1)
            AnsiConsole.MarkupLine($"Sayfalar: {Markup.Escape(string.Join(", ", data.Sheets))}  (okunan: [bold]{Markup.Escape(data.Sheet ?? "")}[/])");
        AnsiConsole.MarkupLine($"[bold]{data.Columns.Count}[/] kolon, [bold]{data.Rows.Count}[/] satır");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("satır").RightAligned());
        foreach (var c in data.Columns)
            table.AddColumn(Markup.Escape(c));
        foreach (var (row, i) in data.Rows.Take(previewRows).Select((r, i) => (r, i)))
            table.AddRow([(i + 1).ToString(), .. data.Columns.Select(c => Markup.Escape(Truncate(row.GetValueOrDefault(c) ?? "", 30)))]);
        AnsiConsole.Write(table);
        if (data.Rows.Count > previewRows)
            AnsiConsole.MarkupLine($"[grey]... {data.Rows.Count - previewRows} satır daha[/]");
    }

    public static Step GetStep(Scenario scenario, int number)
    {
        if (number < 1 || number > scenario.Steps.Count)
            throw new ArgumentException($"Adım numarası 1 ile {scenario.Steps.Count} arasında olmalı.");
        return scenario.Steps[number - 1];
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static int Error(string message)
    {
        AnsiConsole.MarkupLine($"[red]Hata:[/] {Markup.Escape(message)}");
        return 1;
    }
}
