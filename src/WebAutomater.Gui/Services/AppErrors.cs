using System.Diagnostics;
using Avalonia.Threading;
using WebAutomater.Core.Diagnostics;

namespace WebAutomater.Gui;

/// <summary>Ekranın köşesinde kısa süre görünen hata bildirimi.</summary>
public sealed record ErrorNotice(string Area, string Message, DateTime Time);

/// <summary>
/// Hataları günlüğe yazar ve kullanıcıya küçük bir bildirim gösterir; uygulama kapanmaz.
/// Yakalanmamış hatalar da buraya düşer (<see cref="Install"/>).
/// </summary>
public static class AppErrors
{
    /// <summary>Yeni bildirim; her zaman arayüz iş parçacığında çağrılır.</summary>
    public static event Action<ErrorNotice>? Raised;

    /// <summary>Hatayı günlüğe yazar ve bildirim gösterir. Herhangi bir iş parçacığından çağrılabilir.</summary>
    public static void Report(string area, string message, Exception? ex = null)
    {
        AppLog.Error(area, message, ex);
        var text = ex is null ? message : $"{message}\n{AppLog.Describe(ex)}";
        Show(new ErrorNotice(area, text, DateTime.Now));
    }

    private static void Show(ErrorNotice notice)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                Raised?.Invoke(notice);
            else
                Dispatcher.UIThread.Post(() => Raised?.Invoke(notice));
        }
        catch (Exception)
        {
            // Bildirim gösterilemese de hata günlükte.
        }
    }

    /// <summary>
    /// Uygulama başlarken bir kez: süreç genelindeki yakalanmamış hatalar günlüğe yazılır.
    /// </summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Uygulama", e.IsTerminating ? "Beklenmeyen hata; uygulama kapanıyor." : "Beklenmeyen hata.",
                e.ExceptionObject as Exception);

        // Beklenmeyen arka plan görevlerinin hataları sessizce kaybolmasın.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Warning("Uygulama", "Arka plan görevinde yakalanmamış hata.", e.Exception);
            e.SetObserved();
        };

    }

    /// <summary>
    /// Avalonia başladıktan sonra bir kez (önce çağrılırsa arayüz döngüsü kurulamaz): arayüz iş parçacığındaki
    /// yakalanmamış hatalar işlenmiş sayılır, bildirilir ve uygulama kapanmaz.
    /// </summary>
    public static void InstallUiHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Report("Uygulama", "Beklenmeyen bir hata oluştu; işlem yarıda kaldı ama uygulama çalışmaya devam ediyor.", e.Exception);
        };
    }

    /// <summary>Günlük klasörünü dosya yöneticisinde açar.</summary>
    public static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppLog.Directory);
            Process.Start(new ProcessStartInfo(AppLog.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warning("Uygulama", "Günlük klasörü açılamadı.", ex);
        }
    }

    /// <summary>Async void olay işleyicileri için: hata uygulamayı kapatmaz, bildirilir.</summary>
    public static async void Run(string area, string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Report(area, $"{what} sırasında hata oluştu.", ex);
        }
    }
}
