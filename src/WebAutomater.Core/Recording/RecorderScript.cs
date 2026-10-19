namespace WebAutomater.Core.Recording;

internal static class RecorderScript
{
    private static readonly Lazy<string> Lazy = new(() =>
    {
        using var stream = typeof(RecorderScript).Assembly.GetManifestResourceStream("WebAutomater.Core.recorder.js")
            ?? throw new InvalidOperationException("Gömülü kayıt betiği bulunamadı.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Source => Lazy.Value;
}
