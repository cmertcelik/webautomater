using System.Text;
using ClosedXML.Excel;
using WebAutomater.Core.Documents;

namespace WebAutomater.Tests;

public class IdentifierFinderTests
{
    private const string ValidTc = "10000000146";
    private const string ValidVkn = "1234567890";

    [Fact]
    public void Finds_tc_and_vkn_with_kind_and_validity()
    {
        var found = IdentifierFinder.Find($"TC: {ValidTc}\nVergi No: {ValidVkn}.").ToList();
        Assert.Equal(2, found.Count);
        Assert.Equal(new IdentifierMatch(ValidTc, IdentifierKind.TcKimlik, true, 4, 11), found[0]);
        Assert.Equal(new IdentifierMatch(ValidVkn, IdentifierKind.VergiNo, true, 26, 10), found[1]);
    }

    [Fact]
    public void Lists_standalone_numbers_even_when_checksum_fails_but_marks_them()
    {
        var found = Assert.Single(IdentifierFinder.Find("Tel: 05321234567"));
        Assert.Equal(IdentifierKind.TcKimlik, found.Kind);
        Assert.False(found.IsValid);
    }

    [Theory]
    [InlineData("123456789")]              // kısa
    [InlineData("123456789012")]           // uzun
    [InlineData("TR33 0006 1005 1978 6457 8413 26")] // IBAN
    [InlineData("Tutar 1234567890,50 TL")] // ondalık
    [InlineData("ETTN a1b2-12345678901-x")] // tireye bitişik, kontrol hanesi tutmuyor
    [InlineData("FTR12345678901")]         // harfe bitişik, kontrol hanesi tutmuyor
    public void Ignores_non_identifiers(string text) => Assert.Empty(IdentifierFinder.Find(text));

    [Fact]
    public void Accepts_label_glued_number_when_checksum_is_valid() =>
        Assert.Equal(ValidTc, Assert.Single(IdentifierFinder.Find($"TCKN{ValidTc}")).Value);

    [Fact]
    public void Joins_space_split_digits_only_when_checksum_is_valid()
    {
        var spaced = string.Join(' ', ValidTc.ToCharArray());
        Assert.Equal(ValidTc, Assert.Single(IdentifierFinder.Find($"Kimlik: {spaced}")).Value);
        Assert.Empty(IdentifierFinder.Find("1 2 3 4 5 6 7 8 9 0 9"));
    }

    [Fact]
    public void Separates_adjacent_numbers()
    {
        var found = IdentifierFinder.Find($"{ValidTc} {ValidVkn}").Select(m => m.Value).ToList();
        Assert.Equal([ValidTc, ValidVkn], found);
    }

    [Fact]
    public void Newline_does_not_join_numbers() =>
        Assert.Empty(IdentifierFinder.Find("12345\n678901"));
}

public class PdfDocumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-pdf şğü").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Opens_turkish_path_extracts_text_and_renders()
    {
        var path = Path.Combine(_dir, "Örnek belge.pdf");
        File.WriteAllBytes(path, TestPdf.Create("TC 10000000146", "Sayfa iki"));

        using var doc = PdfDocument.Open(path);
        Assert.Equal(2, doc.PageCount);
        Assert.Equal((595, 842), doc.GetPageSize(0));
        Assert.Contains("10000000146", doc.ExtractText(0));
        Assert.DoesNotContain("10000000146", doc.ExtractText(1));

        var pixels = new byte[200 * 283 * 4];
        doc.RenderPage(0, 200, 283, pixels);
        Assert.Equal(255, pixels[0]); // zemin beyaz
        Assert.Contains(pixels, b => b < 128); // yazı çizilmiş
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Text_bounds_sit_on_the_rendered_digits_for_any_rotation(int rotate)
    {
        var path = Path.Combine(_dir, $"r{rotate}.pdf");
        File.WriteAllBytes(path, TestPdf.Create(rotate, "TC 10000000146"));

        using var doc = PdfDocument.Open(path);
        var text = doc.ExtractText(0);
        var start = text.IndexOf("10000000146", StringComparison.Ordinal);
        var rect = Assert.Single(doc.GetTextBounds(0, [(start, 11)])[0]);

        var (w, h) = doc.GetPageSize(0);
        var pw = (int)w;
        var ph = (int)h;
        var pixels = new byte[pw * ph * 4];
        doc.RenderPage(0, pw, ph, pixels);
        bool Dark(int x, int y) => pixels[(y * pw + x) * 4] < 128;

        // Yazılı piksellerin tamamı "TC" + numara; numaranınki dikdörtgenin içinde olmalı, boyutu da makul.
        var inside = 0;
        var outside = new List<(int X, int Y)>();
        for (var y = 0; y < ph; y++)
        for (var x = 0; x < pw; x++)
        {
            if (!Dark(x, y))
                continue;
            if (x >= rect.Left - 2 && x <= rect.Right + 2 && y >= rect.Top - 2 && y <= rect.Bottom + 2)
                inside++;
            else
                outside.Add((x, y));
        }

        Assert.True(inside > 500, $"Dikdörtgen yazının üstünde değil: {rect}");
        // Dışarıda kalan yalnızca "TC" olmalı: numaraya göre çok az piksel.
        Assert.InRange(outside.Count, 1, inside / 2);
    }

    [Fact]
    public void File_is_not_locked_while_open()
    {
        var path = Path.Combine(_dir, "a.pdf");
        File.WriteAllBytes(path, TestPdf.Create("x"));
        using var doc = PdfDocument.Open(path);
        File.Move(path, path + ".moved");
        Assert.Contains("x", doc.ExtractText(0));
    }

    [Fact]
    public void Reports_invalid_file_in_turkish()
    {
        var path = Path.Combine(_dir, "bozuk.pdf");
        File.WriteAllText(path, "pdf değil");
        var ex = Assert.Throws<PdfOpenException>(() => PdfDocument.Open(path));
        Assert.Contains("PDF", ex.Message);
    }

    [Fact]
    public void Disposed_document_throws_managed_exception()
    {
        var path = Path.Combine(_dir, "a.pdf");
        File.WriteAllBytes(path, TestPdf.Create("x"));
        var doc = PdfDocument.Open(path);
        doc.Dispose();
        doc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => doc.ExtractText(0));
    }
}

public class DocumentLogWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-xl").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly string[] DefaultHeaders =
        ["PDF Dosya Yolu", "Belge Tarihi", "Belge Konusu", "Belge Numarası", "Destis Numarası", "Açıklama"];

    private static DocumentLogEntry Entry(string number = "0012") =>
        new(
        [
            new("PDF Dosya Yolu", @"C:\Belgeler\Örnek.pdf", Width: 70),
            new("Belge Tarihi", new DateTime(2025, 12, 31)),
            new("Belge Konusu", "Konu"),
            new("Belge Numarası", number),
            new("Destis Numarası", "D-5"),
            new("Açıklama", "=1+1"),
        ]);

    [Fact]
    public void Creates_file_with_headers_and_appends_rows_in_order()
    {
        var path = Path.Combine(_dir, "liste.xlsx");
        Assert.Equal(2, DocumentLogWriter.Append(path, Entry()));
        Assert.Equal(3, DocumentLogWriter.Append(path, Entry("77")));

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        Assert.Equal(DefaultHeaders, Enumerable.Range(1, 6).Select(c => ws.Cell(1, c).GetString()));
        Assert.Equal(@"C:\Belgeler\Örnek.pdf", ws.Cell(2, 1).GetString());
        Assert.Equal(new DateTime(2025, 12, 31), ws.Cell(2, 2).GetDateTime());
        Assert.Equal("Konu", ws.Cell(2, 3).GetString());
        Assert.Equal("0012", ws.Cell(2, 4).GetString()); // baştaki sıfır korunur
        Assert.Equal("D-5", ws.Cell(2, 5).GetString());
        Assert.False(ws.Cell(2, 6).HasFormula); // formüle dönüşmez
        Assert.Equal("=1+1", ws.Cell(2, 6).GetString());
        Assert.Equal("77", ws.Cell(3, 4).GetString());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp*"));
    }

    [Fact]
    public void Keeps_existing_data_and_appends_after_last_row()
    {
        var path = Path.Combine(_dir, "mevcut.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Kayıtlar");
            ws.Cell(1, 1).SetValue("Eski başlık");
            ws.Cell(5, 3).SetValue("eski veri");
            wb.AddWorksheet("Diğer").Cell(1, 1).SetValue("dokunma");
            wb.SaveAs(path);
        }

        Assert.Equal(6, DocumentLogWriter.Append(path, Entry()));

        using var result = new XLWorkbook(path);
        Assert.Equal("Eski başlık", result.Worksheet("Kayıtlar").Cell(1, 1).GetString());
        Assert.Equal("eski veri", result.Worksheet("Kayıtlar").Cell(5, 3).GetString());
        Assert.Equal("dokunma", result.Worksheet("Diğer").Cell(1, 1).GetString());
    }

    [Fact]
    public void Matches_columns_by_header_and_adds_new_fields_as_new_columns()
    {
        var path = Path.Combine(_dir, "alanlar.xlsx");
        DocumentLogWriter.Append(path, Entry());

        // Sıra değişti, bir alan çıkarıldı, iki yeni alan eklendi; başlık büyük/küçük harf farklı.
        DocumentLogWriter.Append(path, new DocumentLogEntry(
        [
            new("PDF Dosya Yolu", @"C:\b.pdf"),
            new("belge numarası", "42"),
            new("İncelendi", true),
            new("Tür", "Dilekçe"),
            new("Belge Tarihi", null),
        ]));

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        Assert.Equal([.. DefaultHeaders, "İncelendi", "Tür"], Enumerable.Range(1, 8).Select(c => ws.Cell(1, c).GetString()));
        Assert.Equal(@"C:\b.pdf", ws.Cell(3, 1).GetString());
        Assert.True(ws.Cell(3, 2).IsEmpty());
        Assert.Equal("42", ws.Cell(3, 4).GetString());
        Assert.True(ws.Cell(3, 3).IsEmpty());
        Assert.Equal("Evet", ws.Cell(3, 7).GetString());
        Assert.Equal("Dilekçe", ws.Cell(3, 8).GetString());
        Assert.True(ws.Cell(2, 7).IsEmpty());
    }

    [Fact]
    public void Pdf_path_column_is_optional()
    {
        var path = Path.Combine(_dir, "yolsuz.xlsx");
        Assert.Equal(2, DocumentLogWriter.Append(path, new DocumentLogEntry([new("Konu", "A"), new("Tarih", new DateTime(2025, 1, 2))])));

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        Assert.Equal(["Konu", "Tarih"], Enumerable.Range(1, 2).Select(c => ws.Cell(1, c).GetString()));
        Assert.True(ws.Cell(1, 3).IsEmpty());
        Assert.Equal("A", ws.Cell(2, 1).GetString());
    }

    [Theory]
    [InlineData("31.12.2025")]
    [InlineData("31/12/2025")]
    [InlineData("31122025")]
    [InlineData("31.12.25")]
    [InlineData(" 2025-12-31 ")]
    public void Parses_date_formats(string text)
    {
        Assert.True(DocumentDate.TryParse(text, out var date));
        Assert.Equal(new DateTime(2025, 12, 31), date);
    }

    [Theory]
    [InlineData("32.12.2025")]
    [InlineData("yarın")]
    public void Rejects_bad_dates(string text) => Assert.False(DocumentDate.TryParse(text, out _));
}

public class PdfFolderScannerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-scan şğü").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Finds_pdfs_in_subfolders_and_scans_each_page()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "Alt", "Daha alt")).FullName;
        File.WriteAllBytes(Path.Combine(_dir, "a.pdf"), TestPdf.Create("TC 10000000146", "VKN 1234567890"));
        File.WriteAllBytes(Path.Combine(sub, "B.PDF"), TestPdf.Create("Bos sayfa", "TC 10000000146"));
        File.WriteAllText(Path.Combine(_dir, "not.txt"), "10000000146");
        File.WriteAllText(Path.Combine(_dir, "bozuk.pdf"), "pdf değil");

        var files = PdfFolderScanner.FindPdfs(_dir);
        Assert.Equal(3, files.Count);
        Assert.DoesNotContain(files, f => f.EndsWith(".txt"));

        var a = PdfFolderScanner.ScanFile(Path.Combine(_dir, "a.pdf"));
        Assert.Null(a.Error);
        Assert.Equal(2, a.PageCount);
        Assert.Equal([(1, "10000000146"), (2, "1234567890")], a.Matches.Select(m => (m.Page, m.Match.Value)));

        var b = PdfFolderScanner.ScanFile(Path.Combine(sub, "B.PDF"));
        Assert.Equal((2, "10000000146"), (b.Matches.Single().Page, b.Matches.Single().Match.Value));

        var broken = PdfFolderScanner.ScanFile(Path.Combine(_dir, "bozuk.pdf"));
        Assert.NotNull(broken.Error);
        Assert.Empty(broken.Matches);
    }
}

/// <summary>Testler için her sayfasında tek satır yazı olan küçük bir PDF üretir.</summary>
internal static class TestPdf
{
    public static byte[] Create(params string[] pages) => Create(0, pages);

    public static byte[] Create(int rotate, params string[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(' ', pages.Select((_, i) => $"{4 + i * 2} 0 R"))}] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (var i = 0; i < pages.Length; i++)
        {
            var stream = $"BT /F1 30 Tf 40 700 Td ({pages[i]}) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Rotate {rotate} /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}

public class NaturalStringComparerTests
{
    [Fact]
    public void Sorts_numbers_by_value_and_letters_turkish_case_insensitive()
    {
        var comparer = new WebAutomater.Core.Text.NaturalStringComparer(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
        string[] names = ["Belge10.pdf", "belge2.pdf", "Belge1.pdf", "Çizelge.pdf", "Cetvel.pdf", "Dosya.pdf", "2024", "10"];
        var sorted = names.Order(comparer).ToList();
        Assert.Equal(["10", "2024", "Belge1.pdf", "belge2.pdf", "Belge10.pdf", "Cetvel.pdf", "Çizelge.pdf", "Dosya.pdf"], sorted);
        Assert.True(comparer.Compare("a02", "a2") != 0);
        Assert.True(comparer.Compare("a9", "a0010") < 0);
    }
}
