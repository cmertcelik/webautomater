using System.Globalization;
using System.Text;

namespace WebAutomater.Core.Playback;

public static class ReportWriter
{
    /// <summary>Her tekrarın sonucunu Excel'in açabileceği UTF-8 CSV olarak yazar.</summary>
    public static async Task WriteCsvAsync(PlaybackResult result, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("tekrar;satir;durum;deneme;hatali_adim;hata;sure_sn;ekran_goruntusu");
        foreach (var r in result.Iterations)
        {
            sb.Append(r.Iteration).Append(';')
              .Append(r.RowNumber?.ToString() ?? "").Append(';')
              .Append(r.Success ? "BASARILI" : "HATA").Append(';')
              .Append(r.Attempts).Append(';')
              .Append(r.FailedStepIndex is { } i ? (i + 1).ToString() : "").Append(';')
              .Append(Escape(r.Error)).Append(';')
              .Append(r.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(';')
              .Append(Escape(r.ScreenshotPath)).AppendLine();
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Escape(string? s) =>
        string.IsNullOrEmpty(s) ? "" : "\"" + s.Replace("\"", "\"\"").Replace('\n', ' ') + "\"";
}
