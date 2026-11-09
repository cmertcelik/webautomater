using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WebAutomater.Core.Documents;

/// <summary>Kullanıcının tanımladığı etiket: adı aynı zamanda klasör adıdır; kelimelerden biri PDF'te geçerse dosya bu etikete girer.</summary>
public sealed record GroupingTag(string Name, IReadOnlyList<string> Keywords)
{
    /// <summary>"iflas kararı, konkordato" → ["iflas kararı", "konkordato"]. Boşlar ve tekrarlar atılır.</summary>
    public static List<string> ParseKeywords(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .DistinctBy(TextFold.Fold)
            .Where(k => TextFold.Fold(k).Length > 0)
            .ToList();

    /// <summary>Etiket adından geçerli bir klasör adı.</summary>
    public string FolderName => GroupingPlanner.SafeFolderName(Name);
}

/// <param name="SingleFileFolders">Numarası başka hiçbir dosyada geçmeyen dosyalar için de klasör açılsın.</param>
public sealed record GroupingOptions(int MinDigits = 5, int MaxDigits = 7, bool SingleFileFolders = false);

/// <summary>Metni karşılaştırma için sadeleştirir: küçük harf, Türkçe karakterler ve aksanlar düz harfe, boşluklar teke.</summary>
public static class TextFold
{
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        // Satır sonunda bölünmüş kelime ("kon-\nkordato", yumuşak tire): parçalar birleştirilir.
        var swallowSpace = false;
        var hyphen = false;
        var hyphenSpace = false;
        foreach (var raw in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark)
                continue;
            if (raw is '\u00AD' or '\u0002')
            {
                swallowSpace = true;
                continue;
            }

            if (hyphen)
            {
                // Harfe bitişik tireden sonra satır sonu geliyorsa tire de boşluk da atılır.
                if (raw is '\n' or '\r')
                {
                    hyphen = hyphenSpace = false;
                    swallowSpace = true;
                    continue;
                }

                if (char.IsWhiteSpace(raw))
                {
                    hyphenSpace = true;
                    continue;
                }

                sb.Append('-');
                hyphen = false;
                pendingSpace = hyphenSpace;
                hyphenSpace = false;
            }

            if (raw == '-' && !pendingSpace && sb.Length > 0 && char.IsLetter(sb[^1]))
            {
                hyphen = true;
                continue;
            }

            if (char.IsWhiteSpace(raw))
            {
                if (!swallowSpace)
                    pendingSpace = sb.Length > 0;
                continue;
            }

            swallowSpace = false;
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            // FormD'de "İ" → "I" + birleşen nokta; nokta yukarıda atıldı.
            sb.Append(raw is 'ı' or 'I' ? 'i' : char.ToLowerInvariant(raw));
        }

        if (hyphen)
            sb.Append('-');
        return sb.ToString();
    }
}

/// <summary>Bir etiketin bir dosyada bulunması ve bulunan kelime.</summary>
public sealed record TagHit(int TagIndex, string Keyword);

/// <summary>Etiket kelimelerini sadeleştirilmiş metinde arar.</summary>
public sealed class TagMatcher
{
    private readonly List<(int Tag, string Keyword, string Folded)> _keywords = [];

    public TagMatcher(IReadOnlyList<GroupingTag> tags)
    {
        TagCount = tags.Count;
        for (var i = 0; i < tags.Count; i++)
        {
            foreach (var keyword in tags[i].Keywords)
            {
                var folded = TextFold.Fold(keyword);
                if (folded.Length > 0)
                    _keywords.Add((i, keyword, folded));
            }
        }
    }

    public int TagCount { get; }

    public bool IsEmpty => _keywords.Count == 0;

    /// <summary>Metinde geçen ve <paramref name="hits"/>'te henüz olmayan etiketleri ekler. Kelimenin ekli hali de sayılır ("iflasın").</summary>
    public void Match(string text, Dictionary<int, TagHit> hits)
    {
        if (hits.Count == TagCount)
            return;
        var folded = TextFold.Fold(text);
        foreach (var (tag, keyword, key) in _keywords)
        {
            if (!hits.ContainsKey(tag) && folded.Contains(key, StringComparison.Ordinal))
                hits[tag] = new TagHit(tag, keyword);
        }
    }

    /// <summary>Etiket aramada dosya başına okunan en fazla sayfa; karar/ilam gibi belgelerde kelimeler baştadır.</summary>
    public const int MaxPages = 100;

    /// <summary>Bundan büyük dosyalar etiket için okunmaz (tamamı belleğe alınır; çok büyük taranmış dosyalar belleği tüketir).</summary>
    public const long MaxFileBytes = 300L * 1024 * 1024;

    // Bu kadar sayfada hiç yazı yoksa belge taranmış görüntüdür; kalan sayfaları okumak boşa zaman.
    private const int NoTextProbePages = 5;

    /// <summary>
    /// Sayfaları sırayla okur; tüm etiketler bulununca durur. Hiçbir durumda istisna atmaz (iptal hariç):
    /// açılamayan, bozuk ya da çok büyük dosya <see cref="FileTagScan.Error"/> ile döner.
    /// </summary>
    public FileTagScan ScanFile(string path, CancellationToken token = default)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
                return new FileTagScan(path, [], HasNoText: false, $"dosya {MaxFileBytes / 1024 / 1024} MB'tan büyük, içi okunmadı");
            using var document = PdfDocument.Open(path);
            var hits = new Dictionary<int, TagHit>();
            var withText = 0;
            var read = 0;
            // Bölünmüş satırlar sayfa sınırını aşabilsin diye önceki sayfanın sonu da eklenir.
            var tail = "";
            var pages = Math.Min(document.PageCount, MaxPages);
            for (var i = 0; i < pages && hits.Count < TagCount && !IsEmpty; i++)
            {
                token.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = document.ExtractText(i);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                read++;
                if (!string.IsNullOrWhiteSpace(text))
                    withText++;
                else if (withText == 0 && read >= NoTextProbePages)
                    break;
                Match(tail + text, hits);
                tail = text.Length > 200 ? text[^200..] : text;
            }

            var noText = read > 0 && withText == 0;
            return new FileTagScan(path, hits.Values.OrderBy(h => h.TagIndex).ToList(), noText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PdfOpenException ex)
        {
            return new FileTagScan(path, [], HasNoText: false, ex.Message);
        }
        catch (Exception ex)
        {
            Diagnostics.AppLog.Error("Etiket arama", $"PDF okunamadı: {path}", ex);
            return new FileTagScan(path, [], HasNoText: false, Diagnostics.AppLog.Describe(ex));
        }
    }
}

public sealed record FileTagScan(string Path, IReadOnlyList<TagHit> Hits, bool HasNoText, string? Error = null);

/// <summary>Dosya adındaki dosya numaraları ("35476 karar.pdf" → 35476).</summary>
public static partial class CaseNumber
{
    [GeneratedRegex(@"(?<!\d)\d+(?!\d)")]
    private static partial Regex DigitRun();

    public static List<string> FromFileName(string fileName, int minDigits, int maxDigits)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return DigitRun().Matches(name)
            .Select(m => m.Value)
            .Where(v => v.Length >= minDigits && v.Length <= maxDigits)
            .Distinct()
            .ToList();
    }

    public static bool IsCaseFolderName(string name, int minDigits, int maxDigits) =>
        name.Length >= minDigits && name.Length <= maxDigits && name.All(char.IsAsciiDigit);
}

/// <summary>Bir dosya numarası klasörü: "34765" ya da etiketleri eklenmiş "34765-vesayet-iflas".</summary>
public sealed partial record CaseFolderName(string Number, string Labels)
{
    [GeneratedRegex(@"^(\d+)(?:\s*-\s*(.*))?$")]
    private static partial Regex Pattern();

    public bool HasLabels => Labels.Length > 0;

    public static bool TryParse(string folderName, GroupingOptions options, out CaseFolderName parsed)
    {
        parsed = null!;
        var match = Pattern().Match(folderName.Trim());
        if (!match.Success)
            return false;
        var number = match.Groups[1].Value;
        if (number.Length < options.MinDigits || number.Length > options.MaxDigits)
            return false;
        parsed = new CaseFolderName(number, match.Groups[2].Value.Trim().Trim('-').Trim());
        return true;
    }

    /// <summary>Etiket klasör adında zaten yazılı mı ("34765-vesayet-iflas" içinde "iflas").</summary>
    public bool HasLabel(string label) =>
        ($"-{Labels}-").Contains($"-{label}-", StringComparison.CurrentCultureIgnoreCase);

    public CaseFolderName WithLabels(IEnumerable<string> labels)
    {
        var added = labels.Where(l => !HasLabel(l)).ToList();
        return added.Count == 0 ? this : this with { Labels = string.Join("-", new[] { Labels }.Where(l => l.Length > 0).Concat(added)) };
    }

    // Windows'ta yol uzunluğu sınırlı; çok etiket birikince klasör adı kısaltılır.
    public override string ToString()
    {
        var name = Labels.Length == 0 ? Number : $"{Number}-{Labels}";
        return name.Length > 120 ? name[..120].TrimEnd('-', ' ', '.') : name;
    }
}

/// <summary>Kök klasörde daha önce oluşturulmuş bir numara klasörü ve içindeki dosyalar.</summary>
public sealed record ExistingCaseFolder(string Path, CaseFolderName Name, IReadOnlyList<string> Files);

/// <summary>Aynı dosya numarasına ait dosyalar: var olan klasör (varsa) ve oraya taşınacak dosyalar.</summary>
public sealed record CaseGroup(
    string Number,
    ExistingCaseFolder? Folder,
    IReadOnlyList<string> IncomingFiles,
    IReadOnlyList<ExistingCaseFolder> MergedFolders)
{
    public IEnumerable<string> AllFiles => (Folder?.Files ?? []).Concat(IncomingFiles);

    /// <summary>
    /// Etiket aranacak PDF'ler. Etiketi zaten yazılmış klasörde yalnızca yeni gelen dosyalara bakılır;
    /// etiketsiz klasörde (etiketler sonradan eklenmiş olabilir) hepsine.
    /// </summary>
    public IReadOnlyList<string> FilesToScan =>
        (Folder is { Name.HasLabels: true } ? IncomingFiles : AllFiles)
        .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        .ToList();
}

public sealed record UnchangedFile(string Path, string Reason);

/// <summary>1. aşama: yalnızca dosya adlarına göre gruplar.</summary>
public sealed record NumberGrouping(string Root, IReadOnlyList<CaseGroup> Groups, IReadOnlyList<UnchangedFile> Unchanged);

/// <summary>Bir grupta bulunan etiket ve kanıtı.</summary>
public sealed record TagEvidence(int TagIndex, string TagName, string File, string Keyword);

/// <summary>Bir numara klasöründe yapılacaklar: oluşturma/yeniden adlandırma ve içeri taşınacak dosyalar.</summary>
public sealed record CaseFolderPlan(
    CaseGroup Group,
    string FinalFolder,
    IReadOnlyList<TagEvidence> NewLabels)
{
    public bool IsNew => Group.Folder is null;

    public bool Renames => Group.Folder is { } f && !string.Equals(f.Path, FinalFolder, StringComparison.Ordinal);
}

public sealed record GroupingPlan(string Root, IReadOnlyList<CaseFolderPlan> Folders, IReadOnlyList<UnchangedFile> Unchanged)
{
    public int MoveCount => Folders.Sum(f => f.Group.IncomingFiles.Count);

    public int RenameCount => Folders.Count(f => f.Renames);

    public bool HasChanges => MoveCount > 0 || RenameCount > 0 || Folders.Any(f => f.IsNew);
}

public sealed record GroupingResult(
    IReadOnlyList<(string Source, string Destination)> Moved,
    IReadOnlyList<(string Source, string Destination)> Renamed,
    IReadOnlyList<string> Failed);

/// <summary>
/// Otomatik gruplama. Önce yalnızca dosya adlarına bakılır: adında aynı dosya numarası geçen dosyalar
/// numara klasöründe toplanır (etiket tanımlı olmasa da). Sonra yalnızca bu grupların PDF'lerinde etiket aranır;
/// bulunan etiketler klasör adının sonuna eklenir ("34765" → "34765-vesayet"). Etiket için ayrıca klasör açılmaz.
/// </summary>
public static class GroupingPlanner
{
    private static readonly EnumerationOptions TopLevel = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false,
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static string SafeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 || safe is "." or ".." ? "_" : safe;
    }

    /// <summary>Klasörün doğrudan içindeki dosyaları ve numara klasörlerini okuyup gruplar.</summary>
    public static NumberGrouping ScanFolder(string root, GroupingOptions options, CancellationToken token = default)
    {
        var loose = Directory.EnumerateFiles(root, "*", TopLevel).ToList();
        var folders = new List<ExistingCaseFolder>();
        foreach (var dir in Directory.EnumerateDirectories(root, "*", TopLevel))
        {
            token.ThrowIfCancellationRequested();
            if (CaseFolderName.TryParse(Path.GetFileName(dir), options, out var name))
                folders.Add(new ExistingCaseFolder(dir, name, Directory.EnumerateFiles(dir, "*", TopLevel).ToList()));
        }

        return GroupByNumber(root, loose, folders, options);
    }

    public static NumberGrouping GroupByNumber(string root, IReadOnlyList<string> looseFiles,
        IReadOnlyList<ExistingCaseFolder> folders, GroupingOptions options)
    {
        var numbers = looseFiles.Select(f => CaseNumber.FromFileName(f, options.MinDigits, options.MaxDigits)).ToList();

        // Aynı numaralı birden çok klasör (ör. "34765" ve "34765-iflas") tek klasörde birleşir: etiketli ve kalabalık olan kalır.
        var foldersByNumber = folders
            .GroupBy(f => f.Name.Number)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.Name.HasLabels).ThenByDescending(f => f.Files.Count)
                .ThenBy(f => f.Path, StringComparer.Ordinal).ToList());

        // Ortak numarası olan gevşek dosyaları birleştir (birleşim-bul).
        var parent = Enumerable.Range(0, looseFiles.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        var firstWithNumber = new Dictionary<string, int>();
        for (var i = 0; i < looseFiles.Count; i++)
        {
            foreach (var number in numbers[i])
            {
                if (firstWithNumber.TryGetValue(number, out var other))
                    parent[Find(i)] = Find(other);
                else
                    firstWithNumber[number] = i;
            }
        }

        var incoming = new Dictionary<string, List<string>>();
        var unchanged = new List<UnchangedFile>();
        foreach (var component in Enumerable.Range(0, looseFiles.Count).GroupBy(Find))
        {
            var members = component.ToList();
            var counts = members.SelectMany(m => numbers[m]).GroupBy(n => n).ToList();
            if (counts.Count == 0)
            {
                unchanged.AddRange(members.Select(m => new UnchangedFile(looseFiles[m], "Dosya adında dosya numarası yok")));
                continue;
            }

            // Klasörü zaten olan numara önce gelir; yoksa en çok dosyada geçen numara.
            var number = counts
                .OrderByDescending(g => foldersByNumber.ContainsKey(g.Key))
                .ThenByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .First().Key;
            if (members.Count == 1 && !foldersByNumber.ContainsKey(number) && !options.SingleFileFolders)
            {
                unchanged.Add(new UnchangedFile(looseFiles[members[0]], $"{number} numaralı başka dosya yok"));
                continue;
            }

            incoming[number] = members.Select(m => looseFiles[m]).ToList();
        }

        var groups = new List<CaseGroup>();
        foreach (var number in incoming.Keys.Concat(foldersByNumber.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var list = foldersByNumber.GetValueOrDefault(number) ?? [];
            var files = incoming.GetValueOrDefault(number) ?? [];
            var merged = list.Skip(1).ToList();
            groups.Add(new CaseGroup(number, list.FirstOrDefault(), files.Concat(merged.SelectMany(f => f.Files)).ToList(), merged));
        }

        unchanged.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Path, b.Path));
        return new NumberGrouping(root, groups, unchanged);
    }

    /// <summary>2. aşamanın sonucuyla son planı kurar. Değişmeyecek klasörler plana girmez.</summary>
    /// <param name="hits">Grup numarası → o grubun dosyalarında bulunan etiketler.</param>
    public static GroupingPlan BuildPlan(NumberGrouping grouping, IReadOnlyDictionary<string, List<TagEvidence>> hits,
        IReadOnlyList<GroupingTag> tags)
    {
        var plans = new List<CaseFolderPlan>();
        var taken = new HashSet<string>(PathComparer);
        foreach (var group in grouping.Groups)
        {
            var name = group.Folder?.Name ?? new CaseFolderName(group.Number, "");
            var found = (hits.GetValueOrDefault(group.Number) ?? [])
                .Where(h => h.TagIndex >= 0 && h.TagIndex < tags.Count)
                .GroupBy(h => h.TagIndex)
                .OrderBy(g => g.Key)
                .Select(g => g.First())
                .Where(h => !name.HasLabel(tags[h.TagIndex].FolderName))
                .ToList();
            var final = name.WithLabels(found.Select(h => tags[h.TagIndex].FolderName));
            var finalPath = Path.Combine(grouping.Root, final.ToString());
            if (group.Folder is not null && found.Count == 0)
                finalPath = group.Folder.Path;
            if (!taken.Add(finalPath))
                finalPath = group.Folder?.Path ?? finalPath; // aynı ada iki grup düşerse yeniden adlandırılmaz

            var plan = new CaseFolderPlan(group, finalPath, found);
            if (plan.IsNew || plan.Renames || group.IncomingFiles.Count > 0)
                plans.Add(plan);
        }

        return new GroupingPlan(grouping.Root, plans, grouping.Unchanged);
    }

    /// <summary>
    /// Planı uygular: klasörleri oluşturur ya da etiketli adıyla yeniden adlandırır, dosyaları taşır.
    /// Hedefte aynı adlı dosya varsa üzerine yazılmaz, "ad (2).pdf" olur. Her işlem ayrı denenir; biri başarısız olsa da diğerleri sürer.
    /// </summary>
    public static GroupingResult Apply(GroupingPlan plan, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var moved = new List<(string, string)>();
        var renamed = new List<(string, string)>();
        var failed = new List<string>();
        foreach (var folder in plan.Folders)
        {
            if (token.IsCancellationRequested)
                break;
            var target = folder.FinalFolder;
            try
            {
                if (folder.Group.Folder is { } existing && folder.Renames)
                {
                    if (Directory.Exists(target))
                    {
                        failed.Add($"{Path.GetFileName(existing.Path)}: \"{Path.GetFileName(target)}\" adında klasör zaten var, yeniden adlandırılmadı.");
                        target = existing.Path;
                    }
                    else
                    {
                        Directory.Move(existing.Path, target);
                        renamed.Add((existing.Path, target));
                    }
                }
                else
                {
                    Directory.CreateDirectory(target);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.AppLog.Error("Gruplama", $"Klasör hazırlanamadı: {target}", ex);
                failed.Add($"{Path.GetFileName(target)}: {Diagnostics.AppLog.Describe(ex)}");
                continue;
            }

            foreach (var source in folder.Group.IncomingFiles)
            {
                if (token.IsCancellationRequested)
                    break;
                progress?.Report(Path.GetFileName(source));
                try
                {
                    var destination = UniquePath(target, Path.GetFileName(source));
                    File.Move(source, destination);
                    moved.Add((source, destination));
                }
                catch (Exception ex)
                {
                    Diagnostics.AppLog.Error("Gruplama", $"Dosya taşınamadı: {source} → {target}", ex);
                    failed.Add($"{Path.GetFileName(source)}: {Diagnostics.AppLog.Describe(ex)}");
                }
            }

            // Birleştirilen eski klasörler boşaldıysa silinir.
            foreach (var merged in folder.Group.MergedFolders)
            {
                try
                {
                    if (Directory.Exists(merged.Path) && !Directory.EnumerateFileSystemEntries(merged.Path).Any())
                        Directory.Delete(merged.Path);
                }
                catch (Exception ex)
                {
                    Diagnostics.AppLog.Warning("Gruplama", $"Boş klasör silinemedi: {merged.Path}", ex);
                }
            }
        }

        return new GroupingResult(moved, renamed, failed);
    }

    private static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }
}
