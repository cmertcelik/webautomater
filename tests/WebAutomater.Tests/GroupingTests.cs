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

public class GroupingPlannerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "kok");

    private static readonly GroupingTag[] Tags =
    [
        new("İflas", ["iflas kararı"]),
        new("Konkordato", ["konkordato"]),
    ];

    private static GroupingCandidate C(string name, params int[] tags) =>
        new(Path.Combine(Root, name), tags.Select(t => new TagHit(t, Tags[t].Keywords[0])).ToList());

    private static string Dest(GroupingPlan plan, string name) =>
        plan.Moves.Single(m => Path.GetFileName(m.Source) == name).DestinationFolder;

    [Fact]
    public void Same_number_files_follow_the_tagged_one()
    {
        var plan = GroupingPlanner.Plan(Root, [C("35476 karar.pdf", 0), C("35476 tebligat.pdf"), C("35476 ek.pdf")], Tags, new());
        var expected = Path.Combine(Root, "İflas", "35476");
        Assert.All(plan.Moves, m => Assert.Equal(expected, m.DestinationFolder));
        Assert.Equal(3, plan.Moves.Count);
        Assert.Contains("35476 karar.pdf", plan.Moves.Single(m => m.Source.EndsWith("tebligat.pdf")).Reason);
    }

    [Fact]
    public void Tagged_file_without_number_goes_to_tag_folder_and_untagged_single_stays()
    {
        var plan = GroupingPlanner.Plan(Root, [C("karar.pdf", 1), C("dilekce.pdf"), C("99999 tek.pdf")], Tags, new());
        Assert.Equal(Path.Combine(Root, "Konkordato"), Dest(plan, "karar.pdf"));
        Assert.Equal(2, plan.Unchanged.Count);
    }

    [Fact]
    public void Untagged_same_number_files_get_a_number_folder()
    {
        var plan = GroupingPlanner.Plan(Root, [C("11111 a.pdf"), C("11111 b.pdf")], Tags, new());
        Assert.All(plan.Moves, m => Assert.Equal(Path.Combine(Root, "11111"), m.DestinationFolder));
    }

    [Fact]
    public void First_tag_in_list_wins_when_a_case_matches_several()
    {
        var plan = GroupingPlanner.Plan(Root, [C("22222 a.pdf", 1), C("22222 b.pdf", 0)], Tags, new(CaseSubfolders: false));
        Assert.All(plan.Moves, m => Assert.Equal(Path.Combine(Root, "İflas"), m.DestinationFolder));
        Assert.Contains("ayrıca uyuyor: Konkordato", plan.Moves.Single(m => m.Source.EndsWith("a.pdf")).Reason);
    }

    [Fact]
    public void New_file_joins_case_already_placed_in_a_tag_folder()
    {
        var existingFolder = Path.Combine(Root, "Konkordato", "33333");
        var existing = new Dictionary<string, List<ExistingCase>> { ["33333"] = [new ExistingCase(1, existingFolder)] };
        // İçinde başka etiket geçse de dosya numarasının bulunduğu klasöre gider; dava bölünmez.
        var plan = GroupingPlanner.Plan(Root, [C("33333 yeni.pdf", 0)], Tags, new(), existing);
        Assert.Equal(existingFolder, Dest(plan, "33333 yeni.pdf"));
    }

    [Fact]
    public void Files_sharing_any_number_are_linked()
    {
        var plan = GroupingPlanner.Plan(Root, [C("44444.pdf", 0), C("44444-55555.pdf"), C("55555 ek.pdf")], Tags, new());
        Assert.Equal(3, plan.Moves.Count);
        Assert.All(plan.Moves, m => Assert.Equal(Path.Combine(Root, "İflas", "44444"), m.DestinationFolder));
    }
}

public class GroupingEndToEndTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-grup şğü").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Scans_pdfs_and_moves_them_into_tag_and_case_folders()
    {
        File.WriteAllBytes(Path.Combine(_dir, "35476 karar.pdf"), TestPdf.Create("Giris", "IFLAS KARARI verildi"));
        File.WriteAllBytes(Path.Combine(_dir, "35476 tebligat.pdf"), TestPdf.Create("Tebligat"));
        File.WriteAllBytes(Path.Combine(_dir, "diger.pdf"), TestPdf.Create("Bos"));
        Directory.CreateDirectory(Path.Combine(_dir, "İflas", "35476"));
        File.WriteAllBytes(Path.Combine(_dir, "İflas", "35476", "35476 tebligat.pdf"), TestPdf.Create("eski"));
        // Önceki gruplamadan kalan numara klasörü: etiketi artık bulundu, o da taşınmalı.
        Directory.CreateDirectory(Path.Combine(_dir, "77777"));
        File.WriteAllBytes(Path.Combine(_dir, "77777", "77777 a.pdf"), TestPdf.Create("Konkordato talebi"));
        File.WriteAllBytes(Path.Combine(_dir, "77777", "77777 b.pdf"), TestPdf.Create("x"));

        GroupingTag[] tags = [new("İflas", ["iflas kararı"]), new("Konkordato", ["konkordato"])];
        var options = new GroupingOptions();
        var matcher = new TagMatcher(tags);
        var candidates = GroupingPlanner.FindCandidates(_dir, options)
            .Select(p => matcher.ScanFile(p))
            .Select(s => new GroupingCandidate(s.Path, s.Hits))
            .ToList();
        Assert.Equal(5, candidates.Count);

        var plan = GroupingPlanner.Plan(_dir, candidates, tags, options, GroupingPlanner.FindExistingCases(_dir, tags, options));
        var result = GroupingPlanner.Apply(plan);

        Assert.Empty(result.Failed);
        Assert.Equal(4, result.Moved.Count);
        Assert.True(File.Exists(Path.Combine(_dir, "İflas", "35476", "35476 karar.pdf")));
        // Aynı adlı dosyanın üzerine yazılmaz.
        Assert.True(File.Exists(Path.Combine(_dir, "İflas", "35476", "35476 tebligat (2).pdf")));
        Assert.True(File.Exists(Path.Combine(_dir, "Konkordato", "77777", "77777 b.pdf")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "77777")));
        Assert.True(File.Exists(Path.Combine(_dir, "diger.pdf")));
    }
}
