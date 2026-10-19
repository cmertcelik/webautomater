using Microsoft.Playwright;
using WebAutomater.Core.Models;

namespace WebAutomater.Core.Browsers;

public static class BrowserFactory
{
    public static readonly string[] Engines = ["chromium", "firefox", "webkit"];

    /// <summary>
    /// Tarayıcıyı başlatır; Playwright tarayıcısı henüz indirilmemişse önce indirir.
    /// </summary>
    public static async Task<IBrowser> LaunchAsync(
        IPlaywright playwright, BrowserSettings settings, bool headless, Action<string>? log = null)
    {
        var type = settings.Engine.ToLowerInvariant() switch
        {
            "firefox" => playwright.Firefox,
            "webkit" => playwright.Webkit,
            _ => playwright.Chromium,
        };
        var options = new BrowserTypeLaunchOptions
        {
            Headless = headless,
            Channel = string.IsNullOrWhiteSpace(settings.Channel) ? null : settings.Channel,
        };

        try
        {
            return await type.LaunchAsync(options);
        }
        catch (PlaywrightException ex) when (options.Channel is null && BrowserInstaller.IsMissingBrowserError(ex))
        {
            log?.Invoke($"{type.Name} tarayıcısı bulunamadı, indiriliyor (ilk kullanımda bir kez)...");
            var exit = await BrowserInstaller.InstallAsync(type.Name);
            if (exit != 0)
                throw new InvalidOperationException(
                    $"{type.Name} indirilemedi (çıkış kodu {exit}). 'wa install {type.Name}' komutunu deneyin.", ex);
            return await type.LaunchAsync(options);
        }
    }

    public static ViewportSize? ViewportFor(BrowserSettings settings) =>
        settings is { ViewportWidth: > 0, ViewportHeight: > 0 }
            ? new ViewportSize { Width = settings.ViewportWidth.Value, Height = settings.ViewportHeight.Value }
            : null;
}
