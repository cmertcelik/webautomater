using System.ComponentModel;
using Spectre.Console.Cli;
using WebAutomater.Core.Data;

namespace WebAutomater.Cli.Commands;

internal sealed class ColumnsCommand : Command<ColumnsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<veri>")]
        [Description("Excel (.xlsx), CSV veya JSON dosyası.")]
        public string Data { get; init; } = "";

        [CommandOption("--sheet")]
        [Description("Excel çalışma sayfası adı (varsayılan: ilk sayfa).")]
        public string? Sheet { get; init; }

        [CommandOption("--rows")]
        [Description("Önizlenecek satır sayısı.")]
        [DefaultValue(5)]
        public int Rows { get; init; } = 5;
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken ct)
    {
        Shared.PrintData(DataSourceLoader.Load(settings.Data, settings.Sheet), settings.Rows);
        return 0;
    }
}
