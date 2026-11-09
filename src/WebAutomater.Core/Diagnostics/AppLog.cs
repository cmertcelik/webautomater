using System.Text;

namespace WebAutomater.Core.Diagnostics;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Uygulama günlüğü: günlük dosyalara yazılır (%AppData%/WebAutomater/logs/webautomater-YYYYMMDD.log).
/// Hiçbir zaman istisna atmaz; günlük yazılamazsa uygulama çalışmaya devam eder.
/// Kimlik/vergi numarası, çerez gibi kişisel ve gizli bilgiler buraya yazılmamalı.
/// </summary>
public static class AppLog
{
    private const int KeepDays = 30;
    private static readonly object Gate = new();
    private static string? _directory;
    private static bool _cleaned;

    /// <summary>Günlük klasörü. Testler ya da taşınabilir kurulum için değiştirilebilir.</summary>
    public static string Directory
    {
        get => _directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebAutomater", "logs");
        set => _directory = value;
    }

    public static string CurrentFile => Path.Combine(Directory, $"webautomater-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Her yazılan kayıt; arayüzün hata bildirimi göstermesi için. Arka plan iş parçacığından çağrılabilir.</summary>
    public static event Action<LogLevel, string, string, Exception?>? Written;

    public static void Info(string area, string message) => Write(LogLevel.Info, area, message, null);

    public static void Warning(string area, string message, Exception? ex = null) => Write(LogLevel.Warning, area, message, ex);

    public static void Error(string area, string message, Exception? ex = null) => Write(LogLevel.Error, area, message, ex);

    public static void Write(LogLevel level, string area, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(level switch { LogLevel.Error => " [HATA] ", LogLevel.Warning => " [UYARI] ", _ => " [BILGI] " })
                .Append('[').Append(area).Append("] ")
                .Append(message.ReplaceLineEndings(" | "));
            if (ex is not null)
                sb.AppendLine().Append("    ").Append(ex.ToString().ReplaceLineEndings(Environment.NewLine + "    "));
            sb.AppendLine();

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(CurrentFile, sb.ToString(), Encoding.UTF8);
                if (!_cleaned)
                {
                    _cleaned = true;
                    DeleteOldFiles();
                }
            }
        }
        catch (Exception)
        {
            // Günlük yazılamıyor (disk dolu, izin yok): uygulama bundan etkilenmemeli.
        }

        try
        {
            Written?.Invoke(level, area, message, ex);
        }
        catch (Exception)
        {
            // Dinleyicideki hata günlüğü bozmamalı.
        }
    }

    private static void DeleteOldFiles()
    {
        var limit = DateTime.Now.AddDays(-KeepDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "webautomater-*.log"))
        {
            if (File.GetLastWriteTime(file) < limit)
                File.Delete(file);
        }
    }

    /// <summary>Kullanıcıya gösterilecek kısa hata metni: iç içe istisnaların en anlamlısı.</summary>
    public static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner is AggregateException or System.Reflection.TargetInvocationException && inner.InnerException is { } next)
            inner = next;
        return inner switch
        {
            OutOfMemoryException => "Bellek yetmedi (dosya çok büyük olabilir).",
            UnauthorizedAccessException => $"Erişim izni yok: {inner.Message}",
            PathTooLongException => "Dosya yolu çok uzun.",
            _ => inner.Message,
        };
    }
}
