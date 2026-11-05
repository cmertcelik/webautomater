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

public sealed record GroupingOptions(int MinDigits = 5, int MaxDigits = 7, bool CaseSubfolders = true);

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

    /// <summary>Tüm sayfalara bakar; tüm etiketler bulununca durur. Açılamayan dosya hata olarak döner.</summary>
    public FileTagScan ScanFile(string path, CancellationToken token = default)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(path);
        }
        catch (PdfOpenException ex)
        {
            return new FileTagScan(path, [], HasNoText: false, ex.Message);
        }

        using (document)
        {
            var hits = new Dictionary<int, TagHit>();
            var withText = 0;
            var read = 0;
            // Bölünmüş satırlar sayfa sınırını aşabilsin diye önceki sayfanın sonu da eklenir.
            var tail = "";
            for (var i = 0; i < document.PageCount && hits.Count < TagCount && !IsEmpty; i++)
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
                Match(tail + text, hits);
                tail = text.Length > 200 ? text[^200..] : text;
            }

            var noText = read > 0 && withText == 0;
            return new FileTagScan(path, hits.Values.OrderBy(h => h.TagIndex).ToList(), noText);
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

public sealed record GroupingCandidate(string Path, IReadOnlyList<TagHit> Hits);

/// <summary>Bir dosya numarasının daha önce yerleştirildiği etiket klasörü.</summary>
public sealed record ExistingCase(int TagIndex, string Folder);

public sealed record PlannedMove(string Source, string DestinationFolder, string Reason);

public sealed record UnchangedFile(string Path, string Reason);

public sealed record GroupingPlan(string Root, IReadOnlyList<PlannedMove> Moves, IReadOnlyList<UnchangedFile> Unchanged);

public sealed record GroupingResult(IReadOnlyList<(string Source, string Destination)> Moved, IReadOnlyList<string> Failed);

/// <summary>
/// Dosyaları etiketlerine ve dosya numaralarına göre klasörlere dağıtır.
/// Aynı numaralı dosyalar aynı konuya aittir: içlerinden birinde etiket kelimesi geçiyorsa hepsi o etiketin klasörüne gider.
/// </summary>
public static class GroupingPlanner
{
    private static readonly EnumerationOptions TopLevel = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = false,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private static readonly EnumerationOptions Recursive = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        RecurseSubdirectories = true,
        MatchCasing = MatchCasing.CaseInsensitive,
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

    /// <summary>
    /// Gruplanacak dosyalar: klasörün doğrudan içindeki PDF'ler ve daha önce oluşturulmuş numara klasörlerindeki
    /// (ör. "35476") PDF'ler; numaranın etiketi sonradan bulunursa o klasör de etikete taşınır. Etiket klasörlerine dokunulmaz.
    /// </summary>
    public static List<string> FindCandidates(string root, GroupingOptions options)
    {
        var files = Directory.EnumerateFiles(root, "*.pdf", TopLevel).ToList();
        foreach (var dir in Directory.EnumerateDirectories(root, "*", TopLevel))
        {
            if (CaseNumber.IsCaseFolderName(Path.GetFileName(dir), options.MinDigits, options.MaxDigits))
                files.AddRange(Directory.EnumerateFiles(dir, "*.pdf", TopLevel));
        }

        files.Sort(StringComparer.CurrentCultureIgnoreCase);
        return files;
    }

    /// <summary>Etiket klasörlerinde zaten duran dosyaların numaraları: aynı numaralı yeni dosyalar onların yanına gider.</summary>
    public static Dictionary<string, List<ExistingCase>> FindExistingCases(string root, IReadOnlyList<GroupingTag> tags,
        GroupingOptions options)
    {
        var cases = new Dictionary<string, List<ExistingCase>>();
        for (var i = 0; i < tags.Count; i++)
        {
            var folder = Path.Combine(root, tags[i].FolderName);
            if (!Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.pdf", Recursive))
            {
                foreach (var number in CaseNumber.FromFileName(file, options.MinDigits, options.MaxDigits))
                {
                    if (!cases.TryGetValue(number, out var list))
                        cases[number] = list = [];
                    var dir = Path.GetDirectoryName(file)!;
                    if (!list.Any(c => c.TagIndex == i && string.Equals(c.Folder, dir, PathComparison)))
                        list.Add(new ExistingCase(i, dir));
                }
            }
        }

        return cases;
    }

    public static GroupingPlan Plan(string root, IReadOnlyList<GroupingCandidate> candidates, IReadOnlyList<GroupingTag> tags,
        GroupingOptions options, IReadOnlyDictionary<string, List<ExistingCase>>? existing = null)
    {
        existing ??= new Dictionary<string, List<ExistingCase>>();
        var numbers = candidates.Select(c => CaseNumber.FromFileName(c.Path, options.MinDigits, options.MaxDigits)).ToList();

        // Ortak numarası olan dosyaları birleştir (birleşim-bul).
        var parent = Enumerable.Range(0, candidates.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        var firstWithNumber = new Dictionary<string, int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            foreach (var number in numbers[i])
            {
                if (firstWithNumber.TryGetValue(number, out var other))
                    parent[Find(i)] = Find(other);
                else
                    firstWithNumber[number] = i;
            }
        }

        var moves = new List<PlannedMove>();
        var unchanged = new List<UnchangedFile>();
        foreach (var group in Enumerable.Range(0, candidates.Count).GroupBy(Find))
        {
            var members = group.ToList();

            // Grubun numarası: en çok dosyada geçen.
            var caseNumber = members.SelectMany(m => numbers[m])
                .GroupBy(n => n)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Key)
                .FirstOrDefault();
            var groupNumbers = members.SelectMany(m => numbers[m]).Distinct().ToList();

            // Numara daha önce bir etiket klasörüne yerleştirildiyse oraya; yoksa üyelerde bulunan en öncelikli etikete.
            var existingCase = groupNumbers
                .SelectMany(n => existing.TryGetValue(n, out var list) ? list : [])
                .OrderBy(c => c.TagIndex)
                .FirstOrDefault();
            var groupHits = members
                .SelectMany(m => candidates[m].Hits.Select(h => (Member: m, Hit: h)))
                .Where(x => x.Hit.TagIndex >= 0 && x.Hit.TagIndex < tags.Count)
                .ToList();
            var chosenTag = existingCase?.TagIndex ?? (groupHits.Count > 0 ? groupHits.Min(x => x.Hit.TagIndex) : (int?)null);

            string? destination;
            if (existingCase is not null)
                destination = existingCase.Folder;
            else if (chosenTag is { } tag)
            {
                destination = Path.Combine(root, tags[tag].FolderName);
                if (caseNumber is not null && options.CaseSubfolders)
                    destination = Path.Combine(destination, caseNumber);
            }
            else if (caseNumber is not null &&
                     (members.Count > 1 || members.Any(m => IsInCaseFolder(root, candidates[m].Path, options))))
                destination = Path.Combine(root, caseNumber);
            else
                destination = null;

            foreach (var member in members)
            {
                var candidate = candidates[member];
                var name = Path.GetFileName(candidate.Path);
                if (destination is null)
                {
                    unchanged.Add(new UnchangedFile(candidate.Path, caseNumber is null
                        ? "Etiket kelimesi geçmiyor, dosya numarası yok"
                        : $"Etiket kelimesi geçmiyor; {caseNumber} numaralı başka dosya yok"));
                    continue;
                }

                var reason = Reason(candidate, chosenTag, existingCase, groupHits, candidates, tags, caseNumber, members.Count);
                if (string.Equals(Path.GetDirectoryName(candidate.Path), destination, PathComparison))
                {
                    unchanged.Add(new UnchangedFile(candidate.Path, "Zaten doğru klasörde · " + reason));
                    continue;
                }

                moves.Add(new PlannedMove(candidate.Path, destination, reason));
            }
        }

        moves.Sort((a, b) =>
        {
            var byFolder = StringComparer.CurrentCultureIgnoreCase.Compare(a.DestinationFolder, b.DestinationFolder);
            return byFolder != 0 ? byFolder : StringComparer.CurrentCultureIgnoreCase.Compare(a.Source, b.Source);
        });
        unchanged.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Path, b.Path));
        return new GroupingPlan(root, moves, unchanged);
    }

    private static string Reason(GroupingCandidate candidate, int? chosenTag, ExistingCase? existingCase,
        List<(int Member, TagHit Hit)> groupHits, IReadOnlyList<GroupingCandidate> candidates, IReadOnlyList<GroupingTag> tags,
        string? caseNumber, int memberCount)
    {
        string reason;
        var own = chosenTag is { } tag ? candidate.Hits.FirstOrDefault(h => h.TagIndex == tag) : null;
        if (chosenTag is null)
            reason = $"{caseNumber} numaralı {memberCount} dosya";
        else if (own is not null && existingCase is null)
            reason = $"\"{own.Keyword}\" geçiyor";
        else if (existingCase is not null)
            reason = $"{caseNumber} numaralı dosyalar zaten \"{tags[existingCase.TagIndex].Name}\" klasöründe" +
                     (own is not null ? $" (\"{own.Keyword}\" de geçiyor)" : "");
        else
        {
            var source = groupHits.First(x => x.Hit.TagIndex == chosenTag);
            reason = $"Aynı numaralı \"{Path.GetFileName(candidates[source.Member].Path)}\" dosyasında \"{source.Hit.Keyword}\" geçiyor";
        }

        var others = candidate.Hits.Where(h => h.TagIndex != chosenTag && h.TagIndex < tags.Count)
            .Select(h => tags[h.TagIndex].Name).ToList();
        if (others.Count > 0)
            reason += $" · ayrıca uyuyor: {string.Join(", ", others)}";
        return reason;
    }

    private static bool IsInCaseFolder(string root, string path, GroupingOptions options)
    {
        var dir = Path.GetDirectoryName(path);
        return dir is not null && !string.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(root), PathComparison) &&
               CaseNumber.IsCaseFolderName(Path.GetFileName(dir), options.MinDigits, options.MaxDigits);
    }

    /// <summary>
    /// Planı uygular. Hedefte aynı adlı dosya varsa üzerine yazılmaz, "ad (2).pdf" olarak taşınır.
    /// Boşalan numara klasörleri silinir.
    /// </summary>
    public static GroupingResult Apply(GroupingPlan plan, CancellationToken token = default)
    {
        var moved = new List<(string, string)>();
        var failed = new List<string>();
        var emptied = new HashSet<string>(PathComparer);
        foreach (var move in plan.Moves)
        {
            if (token.IsCancellationRequested)
                break;
            try
            {
                Directory.CreateDirectory(move.DestinationFolder);
                var destination = UniquePath(move.DestinationFolder, Path.GetFileName(move.Source));
                File.Move(move.Source, destination);
                moved.Add((move.Source, destination));
                if (IsInCaseFolder(plan.Root, move.Source, new GroupingOptions(1, int.MaxValue)))
                    emptied.Add(Path.GetDirectoryName(move.Source)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{Path.GetFileName(move.Source)}: {ex.Message}");
            }
        }

        foreach (var dir in emptied)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Boş klasör kalırsa sorun değil.
            }
        }

        return new GroupingResult(moved, failed);
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
