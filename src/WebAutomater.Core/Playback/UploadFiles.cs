namespace WebAutomater.Core.Playback;

/// <summary>Bir yükleme adımının değerinden çözülen dosyalar.</summary>
/// <param name="Files">Var olan tam yollar (klasör yüklemede klasör yolu).</param>
/// <param name="Missing">Bulunamayan girişler, kullanıcıya gösterilecek biçimde.</param>
public sealed record UploadResolution(IReadOnlyList<string> Files, IReadOnlyList<string> Missing)
{
    public bool IsEmpty => Files.Count == 0 && Missing.Count == 0;
}

/// <summary>
/// Yükleme adımının değerini dosya yollarına çevirir. Değer veri kaynağından gelebilir (her satırda farklı CV).
/// Playwright göreli yolları uygulamanın çalışma klasörüne göre çözer; bu bizim için anlamsız olduğundan yollar burada tamamlanır.
/// </summary>
public static class UploadFiles
{
    private static readonly char[] Separators = ['|', ';', '\n', '\r'];

    /// <summary>Göreli yolların tamamlanacağı klasör: ayarlanan klasör, yoksa veri kaynağı dosyasının klasörü.</summary>
    public static string? BaseDirectory(PlaybackOptions options) =>
        !string.IsNullOrWhiteSpace(options.FilesDirectory) ? options.FilesDirectory
        : options.Data?.Path is { Length: > 0 } dataPath ? Path.GetDirectoryName(Path.GetFullPath(dataPath))
        : null;

    /// <summary>"a.pdf | b.pdf", alt alta yazılmış ya da Windows'un "Yol olarak kopyala"sındaki gibi tırnaklı yollar.</summary>
    public static List<string> Split(string? value) =>
        (value ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"', '\'').Trim())
            .Where(p => p.Length > 0)
            .ToList();

    public static UploadResolution Resolve(string? value, string? baseDirectory)
    {
        var files = new List<string>();
        var missing = new List<string>();
        foreach (var entry in Split(value))
        {
            var path = Expand(entry);
            if (!Path.IsPathRooted(path))
            {
                if (baseDirectory is null)
                {
                    missing.Add($"{entry} (göreli yol; tam yolunu yazın ya da dosyayı veri kaynağının klasörüne koyun)");
                    continue;
                }

                path = Path.Combine(baseDirectory, path);
            }

            path = Path.GetFullPath(path);
            var name = Path.GetFileName(path);
            if (name.IndexOfAny(['*', '?']) >= 0)
            {
                // "C:\CV\{{TC}}*.pdf": adı tam bilinmeyen dosyalar.
                var dir = Path.GetDirectoryName(path)!;
                var matches = Directory.Exists(dir)
                    ? Directory.EnumerateFiles(dir, name, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                        .Order(StringComparer.CurrentCultureIgnoreCase).ToList()
                    : [];
                if (matches.Count == 0)
                    missing.Add($"{path} (bu desene uyan dosya yok)");
                files.AddRange(matches);
                continue;
            }

            if (File.Exists(path) || Directory.Exists(path))
                files.Add(path);
            else
                missing.Add(path);
        }

        return new UploadResolution(files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), missing);
    }

    private static string Expand(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri))
            path = uri.LocalPath;
        return path;
    }
}
