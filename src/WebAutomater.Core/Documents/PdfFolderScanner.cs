namespace WebAutomater.Core.Documents;

/// <summary>Bir PDF'te bulunan numara ve geçtiği sayfa (1'den başlar).</summary>
public sealed record PageMatch(int Page, IdentifierMatch Match);

/// <summary>
/// Bir dosyanın tarama sonucu. <see cref="Error"/> doluysa dosya açılamamıştır (parolalı, bozuk vb.).
/// </summary>
public sealed record PdfScanResult(
    string Path,
    IReadOnlyList<PageMatch> Matches,
    int PageCount,
    int PagesWithoutText,
    string? Error = null)
{
    public bool HasNoText => Error is null && PageCount > 0 && PagesWithoutText == PageCount;
}

/// <summary>Bir klasördeki (alt klasörler dahil) tüm PDF'lerde numara arar.</summary>
public static class PdfFolderScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    /// <summary>Klasör ve alt klasörlerindeki PDF'ler, yola göre sıralı.</summary>
    public static List<string> FindPdfs(string root)
    {
        var files = Directory.EnumerateFiles(root, "*.pdf", Options).ToList();
        files.Sort(StringComparer.CurrentCultureIgnoreCase);
        return files;
    }

    /// <summary>Tek bir PDF'i açıp tüm sayfalarını tarar. Açılamayan dosya hata olarak döner, istisna atmaz.</summary>
    public static PdfScanResult ScanFile(string path, CancellationToken token = default)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(path);
        }
        catch (PdfOpenException ex)
        {
            return new PdfScanResult(path, [], 0, 0, ex.Message);
        }

        using (document)
        {
            var matches = new List<PageMatch>();
            var withoutText = 0;
            for (var i = 0; i < document.PageCount; i++)
            {
                token.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = document.ExtractText(i);
                }
                catch (InvalidOperationException)
                {
                    // Bozuk tek sayfa taramayı durdurmasın.
                    withoutText++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(text))
                    withoutText++;
                matches.AddRange(IdentifierFinder.Find(text).Select(m => new PageMatch(i + 1, m)));
            }

            return new PdfScanResult(path, matches, document.PageCount, withoutText);
        }
    }
}
