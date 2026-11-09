using System.Runtime.CompilerServices;
using WebAutomater.Core.Diagnostics;

namespace WebAutomater.Tests;

internal static class TestLogSetup
{
    /// <summary>Testler kullanıcının gerçek günlük klasörüne yazmasın.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        AppLog.Directory = Path.Combine(Path.GetTempPath(), "wa-test-logs-" + Environment.ProcessId);
}

public class AppLogTests
{
    [Fact]
    public void Writes_level_area_message_and_exception_details()
    {
        var marker = Guid.NewGuid().ToString("N");
        Exception error;
        try
        {
            throw new InvalidOperationException("iç hata " + marker);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        AppLog.Error("Belge Tarama", $"Gruplama yarıda kaldı\nikinci satır {marker}", error);

        var text = File.ReadAllText(AppLog.CurrentFile);
        var line = text.Split('\n').First(l => l.Contains(marker) && l.Contains("[HATA]"));
        Assert.Contains("[Belge Tarama] Gruplama yarıda kaldı | ikinci satır", line);
        Assert.Contains("InvalidOperationException: iç hata " + marker, text);
        Assert.Contains(nameof(Writes_level_area_message_and_exception_details), text);
    }

    [Fact]
    public void Never_throws_even_when_log_folder_is_unusable()
    {
        var original = AppLog.Directory;
        var file = Path.GetTempFileName();
        try
        {
            // Klasör yerine dosya: dizin oluşturulamaz.
            AppLog.Directory = Path.Combine(file, "alt");
            AppLog.Info("Test", "yazılamaz");
        }
        finally
        {
            AppLog.Directory = original;
            File.Delete(file);
        }
    }

    [Fact]
    public void Describe_unwraps_aggregate_and_names_common_errors()
    {
        Assert.Equal("asıl", AppLog.Describe(new AggregateException(new InvalidOperationException("asıl"))));
        Assert.Contains("Bellek", AppLog.Describe(new OutOfMemoryException()));
    }
}
