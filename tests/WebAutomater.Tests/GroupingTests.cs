using WebAutomater.Core.Documents;

namespace WebAutomater.Tests;

public class TextFoldTests
{
    [Theory]
    [InlineData("İFLAS KARARI", "iflas karari")]
    [InlineData("Iflas  Kararı\n", "iflas karari")]
    [InlineData("KONKORDATO  MÜHLETİ", "konkordato muhleti")]
    [InlineData("kon-\nkordato", "konkordato")]
    [InlineData("kon-\r\nkordato", "konkordato")]
    [InlineData("iflas-konkordato", "iflas-konkordato")]
    [InlineData("iflas- konkordato", "iflas- konkordato")]
    [InlineData("şğüöç ŞĞÜÖÇ", "sguoc sguoc")]
    public void Folds_case_turkish_letters_and_spaces(string input, string expected) =>
        Assert.Equal(expected, TextFold.Fold(input));

    [Fact]
    public void Parses_comma_separated_keywords() =>
        Assert.Equal(["iflas kararı", "konkordato"], GroupingTag.ParseKeywords(" iflas kararı, ,konkordato, İFLAS KARARI "));
}

public class CaseNumberTests
{
    [Theory]
    [InlineData("35476 karar.pdf", new[] { "35476" })]
    [InlineData("Ek-35476_2.pdf", new[] { "35476" })]
    [InlineData("12.05.2024 tebligat 35476.pdf", new[] { "35476" })]
    [InlineData("20240512.pdf", new string[0])]
    [InlineData("belge.pdf", new string[0])]
    [InlineData("35476-35477.pdf", new[] { "35476", "35477" })]
    public void Extracts_numbers_within_digit_range(string name, string[] expected) =>
        Assert.Equal(expected, CaseNumber.FromFileName(name, 5, 7));
}

public class CaseFolderNameTests
{
    private static readonly GroupingOptions Options = new();

    [Theory]
    [InlineData("34765", "34765", "")]
    [InlineData("34765-vesayet", "34765", "vesayet")]
    [InlineData("34765 - vesayet-İflas", "34765", "vesayet-İflas")]
    public void Parses_number_and_labels(string name, string number, string labels)
    {
        Assert.True(CaseFolderName.TryParse(name, Options, out var parsed));
        Assert.Equal(number, parsed.Number);
        Assert.Equal(labels, parsed.Labels);
    }

    [Theory]
    [InlineData("İflas")]
    [InlineData("2024")]
    [InlineData("20240512")]
    [InlineData("35476a")]
    public void Ignores_other_folders(string name) => Assert.False(CaseFolderName.TryParse(name, Options, out _));

    [Fact]
    public void Adds_only_missing_labels()
    {
        var name = new CaseFolderName("34765", "vesayet");
        Assert.Equal("34765-vesayet-İflas", name.WithLabels(["VESAYET", "İflas"]).ToString());
        Assert.Equal("34765-vesayet", name.WithLabels(["vesayet"]).ToString());
    }
}

public class GroupingPlannerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "kok");
    private static readonly GroupingOptions Options = new();

    private static readonly GroupingTag[] Tags =
    [
        new("vesayet", ["vesayet kararı"]),
        new("İflas", ["iflas"]),
    ];

    private static string F(string name) => Path.Combine(Root, name);

    private static ExistingCaseFolder Folder(string name, params string[] files)
    {
        CaseFolderName.TryParse(name, Options, out var parsed);
        var path = Path.Combine(Root, name);
        return new ExistingCaseFolder(path, parsed, files.Select(f => Path.Combine(path, f)).ToList());
    }

    [Fact]
    public void Groups_same_number_files_without_any_tags()
    {
        var grouping = GroupingPlanner.GroupByNumber(Root, [F("34765 karar.pdf"), F("34765 ek.docx"), F("99999 tek.pdf"), F("dilekce.pdf")], [], Options);
        var group = Assert.Single(grouping.Groups);
        Assert.Equal("34765", group.Number);
        Assert.Equal(2, group.IncomingFiles.Count);
        Assert.Equal(2, grouping.Unchanged.Count);

        var plan = GroupingPlanner.BuildPlan(grouping, new Dictionary<string, List<TagEvidence>>(), []);
        var folder = Assert.Single(plan.Folders);
        Assert.Equal(F("34765"), folder.FinalFolder);
        Assert.True(folder.IsNew);
    }

    [Fact]
    public void Single_file_folders_are_optional()
    {
        var grouping = GroupingPlanner.GroupByNumber(Root, [F("99999 tek.pdf")], [], Options with { SingleFileFolders = true });
        Assert.Equal("99999", Assert.Single(grouping.Groups).Number);
    }

    [Fact]
    public void Found_tags_are_appended_to_folder_name_in_tag_order()
    {
        var grouping = GroupingPlanner.GroupByNumber(Root, [F("34765 a.pdf"), F("34765 b.pdf")], [], Options);
        var hits = new Dictionary<string, List<TagEvidence>>
        {
            ["34765"] = [new(1, "İflas", F("34765 b.pdf"), "iflas"), new(0, "vesayet", F("34765 a.pdf"), "vesayet kararı")],
        };
        var plan = GroupingPlanner.BuildPlan(grouping, hits, Tags);
        Assert.Equal(F("34765-vesayet-İflas"), Assert.Single(plan.Folders).FinalFolder);
    }

    [Fact]
    public void New_file_joins_existing_labeled_folder_and_only_new_files_are_scanned()
    {
        var existing = Folder("34765-vesayet", "34765 eski.pdf");
        var grouping = GroupingPlanner.GroupByNumber(Root, [F("34765 yeni.pdf")], [existing], Options);
        var group = Assert.Single(grouping.Groups);
        Assert.Same(existing, group.Folder);
        Assert.Equal([F("34765 yeni.pdf")], group.FilesToScan);

        // Yeni dosyada bulunan, klasörde zaten yazılı etiket tekrar eklenmez; yeni etiket eklenir.
        var plan = GroupingPlanner.BuildPlan(grouping, new Dictionary<string, List<TagEvidence>>
        {
            ["34765"] = [new(0, "vesayet", F("34765 yeni.pdf"), "vesayet kararı"), new(1, "İflas", F("34765 yeni.pdf"), "iflas")],
        }, Tags);
        var folder = Assert.Single(plan.Folders);
        Assert.Equal(Path.Combine(Root, "34765-vesayet-İflas"), folder.FinalFolder);
        Assert.True(folder.Renames);
        Assert.Equal("İflas", Assert.Single(folder.NewLabels).TagName);
    }

    [Fact]
    public void Unlabeled_existing_folder_is_rescanned_and_renamed_without_new_files()
    {
        var existing = Folder("11111", "11111 a.pdf", "11111 b.txt");
        var grouping = GroupingPlanner.GroupByNumber(Root, [], [existing], Options);
        Assert.Equal([Path.Combine(existing.Path, "11111 a.pdf")], Assert.Single(grouping.Groups).FilesToScan);

        var none = GroupingPlanner.BuildPlan(grouping, new Dictionary<string, List<TagEvidence>>(), Tags);
        Assert.False(none.HasChanges);

        var plan = GroupingPlanner.BuildPlan(grouping, new Dictionary<string, List<TagEvidence>>
        {
            ["11111"] = [new(1, "İflas", existing.Files[0], "iflas")],
        }, Tags);
        Assert.Equal(F("11111-İflas"), Assert.Single(plan.Folders).FinalFolder);
    }

    [Fact]
    public void Single_file_joins_existing_folder_of_its_number()
    {
        var grouping = GroupingPlanner.GroupByNumber(Root, [F("22222 tek.pdf")], [Folder("22222", "22222 x.pdf")], Options);
        Assert.Equal([F("22222 tek.pdf")], Assert.Single(grouping.Groups).IncomingFiles);
    }

    [Fact]
    public void Folders_of_same_number_are_merged_into_labeled_one()
    {
        var plain = Folder("33333", "33333 a.pdf");
        var labeled = Folder("33333-İflas", "33333 b.pdf");
        var grouping = GroupingPlanner.GroupByNumber(Root, [], [plain, labeled], Options);
        var group = Assert.Single(grouping.Groups);
        Assert.Same(labeled, group.Folder);
        Assert.Equal(plain.Files, group.IncomingFiles);
    }

    [Fact]
    public void Files_sharing_any_number_are_linked()
    {
        var grouping = GroupingPlanner.GroupByNumber(Root, [F("44444.pdf"), F("44444-55555.pdf"), F("55555 ek.pdf")], [], Options);
        Assert.Equal(3, Assert.Single(grouping.Groups).IncomingFiles.Count);
    }
}

public class GroupingEndToEndTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-grup şğü").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static GroupingPlan Plan(string root, GroupingTag[] tags, GroupingOptions options)
    {
        var grouping = GroupingPlanner.ScanFolder(root, options);
        var matcher = new TagMatcher(tags);
        var hits = new Dictionary<string, List<TagEvidence>>();
        foreach (var group in grouping.Groups)
        {
            hits[group.Number] = group.FilesToScan
                .Select(f => (File: f, Scan: matcher.ScanFile(f)))
                .SelectMany(x => x.Scan.Hits.Select(h => new TagEvidence(h.TagIndex, tags[h.TagIndex].Name, x.File, h.Keyword)))
                .ToList();
        }

        return GroupingPlanner.BuildPlan(grouping, hits, tags);
    }

    [Fact]
    public void Groups_by_number_then_labels_folders_and_handles_reruns()
    {
        File.WriteAllBytes(Path.Combine(_dir, "35476 karar.pdf"), TestPdf.Create("Giris", "VESAYET KARARI verildi"));
        File.WriteAllBytes(Path.Combine(_dir, "35476 tebligat.pdf"), TestPdf.Create("Tebligat"));
        File.WriteAllText(Path.Combine(_dir, "35476 not.txt"), "not");
        File.WriteAllBytes(Path.Combine(_dir, "77777 a.pdf"), TestPdf.Create("x"));
        File.WriteAllBytes(Path.Combine(_dir, "77777 b.pdf"), TestPdf.Create("y"));
        File.WriteAllText(Path.Combine(_dir, "77777 bozuk.pdf"), "pdf değil");
        File.WriteAllBytes(Path.Combine(_dir, "diger.pdf"), TestPdf.Create("Bos"));
        GroupingTag[] tags = [new("vesayet", ["vesayet kararı"])];
        var options = new GroupingOptions();

        var result = GroupingPlanner.Apply(Plan(_dir, tags, options));

        Assert.Empty(result.Failed);
        Assert.Equal(6, result.Moved.Count);
        Assert.True(File.Exists(Path.Combine(_dir, "35476-vesayet", "35476 tebligat.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "35476-vesayet", "35476 not.txt")));
        // Bozuk PDF gruplamayı durdurmaz; adındaki numaraya göre gruplanır.
        Assert.True(File.Exists(Path.Combine(_dir, "77777", "77777 bozuk.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "diger.pdf")));
        Assert.Equal(["35476-vesayet", "77777"], Directory.GetDirectories(_dir).Select(Path.GetFileName).Order());

        // Sonradan gelen dosya ve sonradan eklenen etiket: aynı numaralı dosya var olan klasöre gider, etiketsiz klasör yeniden taranır.
        File.WriteAllBytes(Path.Combine(_dir, "77777 yeni.pdf"), TestPdf.Create("Iflas talebi"));
        File.WriteAllBytes(Path.Combine(_dir, "35476 yeni.pdf"), TestPdf.Create("x"));
        tags = [new("vesayet", ["vesayet kararı"]), new("İflas", ["iflas"])];

        var second = Plan(_dir, tags, options);
        Assert.Equal(2, second.MoveCount);
        var rerun = GroupingPlanner.Apply(second);

        Assert.Empty(rerun.Failed);
        Assert.True(File.Exists(Path.Combine(_dir, "77777-İflas", "77777 yeni.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "77777-İflas", "77777 a.pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "35476-vesayet", "35476 yeni.pdf")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "77777")));
    }

    [Fact]
    public void Same_name_file_is_not_overwritten()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "12345"));
        File.WriteAllText(Path.Combine(_dir, "12345", "12345 a.pdf"), "eski");
        File.WriteAllText(Path.Combine(_dir, "12345 a.pdf"), "yeni");

        var result = GroupingPlanner.Apply(Plan(_dir, [], new GroupingOptions()));

        Assert.Empty(result.Failed);
        Assert.Equal("eski", File.ReadAllText(Path.Combine(_dir, "12345", "12345 a.pdf")));
        Assert.Equal("yeni", File.ReadAllText(Path.Combine(_dir, "12345", "12345 a (2).pdf")));
    }
}

public class TagMatcherRobustnessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-tag").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Broken_or_missing_files_return_errors_instead_of_throwing()
    {
        var matcher = new TagMatcher([new GroupingTag("x", ["kelime"])]);
        var broken = Path.Combine(_dir, "bozuk.pdf");
        File.WriteAllBytes(broken, [0x25, 0x50, 0x44, 0x46, 0x00, 0xFF]);
        Assert.NotNull(matcher.ScanFile(broken).Error);
        Assert.NotNull(matcher.ScanFile(Path.Combine(_dir, "yok.pdf")).Error);
    }

    [Fact]
    public void Scanned_pdf_stops_early_and_reports_no_text()
    {
        var path = Path.Combine(_dir, "taranmis.pdf");
        File.WriteAllBytes(path, TestPdf.Create(Enumerable.Repeat(" ", 30).ToArray()));
        var scan = new TagMatcher([new GroupingTag("x", ["kelime"])]).ScanFile(path);
        Assert.Null(scan.Error);
        Assert.True(scan.HasNoText);
    }
}
