using Microsoft.Playwright;

namespace WebAutomater.Core.Browsers;

public static class BrowserInstaller
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Playwright'ın kendi indiricisini çalıştırır (gömülü sürücü üzerinden).</summary>
    public static async Task<int> InstallAsync(string engine = "chromium", bool withDependencies = false)
    {
        await Gate.WaitAsync();
        try
        {
            var args = withDependencies ? new[] { "install", "--with-deps", engine } : new[] { "install", engine };
            return await Task.Run(() => Microsoft.Playwright.Program.Main(args));
        }
        finally
        {
            Gate.Release();
        }
    }

    public static bool IsMissingBrowserError(PlaywrightException ex) =>
        ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase);
}
