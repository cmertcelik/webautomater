using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using WebAutomater.Core.Browsers;

namespace WebAutomater.Cli.Commands;

internal sealed class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[tarayici]")]
        [Description("chromium (varsayılan), firefox veya webkit.")]
        public string Browser { get; init; } = "chromium";

        [CommandOption("--with-deps")]
        [Description("Linux'ta gerekli sistem kütüphanelerini de kurar (yönetici yetkisi ister).")]
        public bool WithDeps { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (!BrowserFactory.Engines.Contains(settings.Browser))
            return Shared.Error($"Bilinmeyen tarayıcı: {settings.Browser}");
        AnsiConsole.MarkupLine($"[bold]{settings.Browser}[/] indiriliyor...");
        var exit = await BrowserInstaller.InstallAsync(settings.Browser, settings.WithDeps);
        if (exit == 0)
            AnsiConsole.MarkupLine("[green]Hazır.[/]");
        return exit;
    }
}
