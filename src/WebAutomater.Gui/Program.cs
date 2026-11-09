using Avalonia;
using WebAutomater.Core.Diagnostics;

namespace WebAutomater.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppLog.Info("Uygulama", $"Başlatıldı (sürüm {typeof(Program).Assembly.GetName().Version}, {Environment.OSVersion}).");
        AppErrors.Install();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            AppLog.Info("Uygulama", "Kapatıldı.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Uygulama", "Uygulama beklenmeyen bir hatayla kapandı.", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
