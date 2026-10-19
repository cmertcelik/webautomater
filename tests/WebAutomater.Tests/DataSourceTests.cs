using System.Text;
using ClosedXML.Excel;
using WebAutomater.Core.Data;

namespace WebAutomater.Tests;

public sealed class DataSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-data").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Csv_detects_semicolon_and_reads_columns()
    {
        var path = FilePath("a.csv");
        File.WriteAllText(path, "isim;telefon;yaş\nAli;555 111;30\n;;\nVeli;\"555;222\";41\n");

        var table = DataSourceLoader.Load(path);

        Assert.Equal(["isim", "telefon", "yaş"], table.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("555;222", table.Rows[1]["telefon"]);
        Assert.Equal("30", table.Rows[0]["YAŞ"]);
    }

    [Fact]
    public void Csv_falls_back_to_turkish_windows_encoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = FilePath("tr.csv");
        File.WriteAllBytes(path, Encoding.GetEncoding(1254).GetBytes("şehir,ülke\nİzmir,Türkiye\n"));

        var table = DataSourceLoader.Load(path);

        Assert.Equal(["şehir", "ülke"], table.Columns);
        Assert.Equal("İzmir", table.Rows[0]["şehir"]);
    }

    [Fact]
    public void Csv_names_blank_and_duplicate_headers()
    {
        var path = FilePath("h.csv");
        File.WriteAllText(path, "ad,,ad\n1,2,3\n");

        var table = DataSourceLoader.Load(path);

        Assert.Equal(["ad", "Kolon2", "ad_2"], table.Columns);
    }

    [Fact]
    public void Json_flattens_nested_objects_inside_wrapper()
    {
        var path = FilePath("a.json");
        File.WriteAllText(path, """
            { "kisiler": [
                { "isim": "Ali", "yas": 30, "adres": { "sehir": "Ankara" }, "etiket": ["a", "b"] },
                { "isim": "Ayşe", "aktif": true }
            ] }
            """);

        var table = DataSourceLoader.Load(path);

        Assert.Equal(["isim", "yas", "adres.sehir", "etiket", "aktif"], table.Columns);
        Assert.Equal("Ankara", table.Rows[0]["adres.sehir"]);
        Assert.Equal("a|b", table.Rows[0]["etiket"]);
        Assert.Equal("true", table.Rows[1]["aktif"]);
        Assert.False(table.Rows[1].ContainsKey("yas"));
    }

    [Fact]
    public void Excel_reads_selected_sheet_with_formatted_values()
    {
        var path = FilePath("a.xlsx");
        using (var wb = new XLWorkbook())
        {
            wb.AddWorksheet("Boş");
            var ws = wb.AddWorksheet("Liste");
            ws.Cell("B2").Value = "isim";
            ws.Cell("C2").Value = "yaş";
            ws.Cell("B3").Value = "Ali";
            ws.Cell("C3").Value = 30;
            ws.Cell("B4").Value = "Veli";
            ws.Cell("C4").Value = 41;
            wb.SaveAs(path);
        }

        var table = DataSourceLoader.Load(path, "liste");

        Assert.Equal(["Boş", "Liste"], table.Sheets);
        Assert.Equal(["isim", "yaş"], table.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("41", table.Rows[1]["yaş"]);
    }
}
