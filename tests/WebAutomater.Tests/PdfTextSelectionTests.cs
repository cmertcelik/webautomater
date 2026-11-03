using WebAutomater.Core.Documents;

namespace WebAutomater.Tests;

/// <summary>PDF görüntüleyicide fareyle yazı seçme ve kopyalama.</summary>
public class PdfTextSelectionTests : IDisposable
{
    private const string Number = "10000000146";
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-sel").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private PdfDocument Open(int rotate, params string[] pages)
    {
        var path = Path.Combine(_dir, $"p{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, TestPdf.Create(rotate, pages));
        return PdfDocument.Open(path);
    }

    private static PdfRect NumberRect(PdfDocument doc)
    {
        var start = doc.ExtractText(0).IndexOf(Number, StringComparison.Ordinal);
        return Assert.Single(doc.GetTextBounds(0, [(start, Number.Length)])[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Double_click_on_number_selects_the_number_for_any_rotation(int rotate)
    {
        using var doc = Open(rotate, $"TC {Number}");
        var layout = doc.GetTextLayout(0);
        var r = NumberRect(doc);

        var (start, end) = layout.WordAt((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        Assert.Equal(Number, layout.GetText(start, end));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Dragging_across_the_number_selects_exactly_it(int rotate)
    {
        using var doc = Open(rotate, $"TC {Number}");
        var layout = doc.GetTextLayout(0);
        var r = NumberRect(doc);
        var horizontal = rotate is 0 or 180;
        var (cx, cy) = ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);

        // Numaranın iki ucundan biraz içeride bırakılan fare: ilk ve son haneler ortalarından geçildiği için dahil.
        int Caret(double towardStart) => horizontal
            ? layout.CaretAt(cx + towardStart * (r.Right - r.Left) / 2, cy)
            : layout.CaretAt(cx, cy + towardStart * (r.Bottom - r.Top) / 2);

        var a = Caret(-1.0);
        var b = Caret(1.0);
        Assert.Equal(Number, layout.GetText(Math.Min(a, b), Math.Max(a, b)));

        // Seçim boyası numaranın üstünde.
        var rect = Assert.Single(layout.GetSelectionRects(a, b));
        Assert.True(rect.Left <= r.Left + 1 && rect.Right >= r.Right - 1 && rect.Top <= r.Top + 1 && rect.Bottom >= r.Bottom - 1,
            $"Seçim {rect}, numara {r}");
    }

    [Fact]
    public void Dragging_from_outside_the_text_selects_the_whole_line()
    {
        using var doc = Open(0, $"TC {Number}");
        var layout = doc.GetTextLayout(0);
        var (w, _) = doc.GetPageSize(0);
        var r = NumberRect(doc);
        var y = (r.Top + r.Bottom) / 2;

        Assert.Equal($"TC {Number}", layout.GetText(layout.CaretAt(0, y), layout.CaretAt(w, y)));
        Assert.Equal($"TC {Number}", layout.GetText(layout.CaretAt(0, 0), layout.CaretAt(w, 842)));
        Assert.True(layout.IsOverText((r.Left + r.Right) / 2, y));
        Assert.False(layout.IsOverText(5, 5));
    }

    [Fact]
    public void Scanned_page_without_text_is_empty()
    {
        using var doc = Open(0, "");
        var layout = doc.GetTextLayout(0);
        Assert.False(layout.HasText);
        Assert.Equal(0, layout.CaretAt(100, 100));
        Assert.Equal("", layout.GetText(0, layout.Count));
    }

    // Sayfadan bağımsız: iki satırlık elle kurulmuş düzen. PDFium satır sonunu kutusuz "\r" "\n" olarak verir.
    private static PdfTextLayout TwoLines()
    {
        var chars = new List<string>();
        var boxes = new List<PdfRect?>();
        void Line(string text, double top)
        {
            for (var i = 0; i < text.Length; i++)
            {
                chars.Add(text[i].ToString());
                boxes.Add(text[i] == ' ' ? null : new PdfRect(10 + i * 10, top, 18 + i * 10, top + 12));
            }
        }

        Line("Ali Veli", 100);
        chars.Add("\r");
        boxes.Add(null);
        chars.Add("\n");
        boxes.Add(null);
        Line("Ankara", 120);
        return new PdfTextLayout(chars, boxes);
    }

    [Fact]
    public void Multi_line_selection_keeps_single_line_breaks_and_one_rect_per_line()
    {
        var layout = TwoLines();
        var start = layout.CaretAt(45, 106); // "Veli"nin ortasından önce: V'nin önü
        var end = layout.CaretAt(500, 126);  // ikinci satırın sonu
        Assert.Equal("Veli\nAnkara", layout.GetText(start, end));
        Assert.Equal(2, layout.GetSelectionRects(start, end).Count);
        Assert.Equal("Ali Veli\nAnkara", layout.GetText(0, layout.Count));
    }

    [Fact]
    public void Point_between_lines_goes_to_the_nearest_line()
    {
        var layout = TwoLines();
        Assert.Equal(0, layout.CaretAt(0, 90));        // üstte, solda: metnin başı
        Assert.Equal(layout.Count, layout.CaretAt(900, 200)); // altta, sağda: metnin sonu
        var (s, e) = layout.LineAt(30, 125);
        Assert.Equal("Ankara", layout.GetText(s, e));
        var (ws, we) = layout.WordAt(14, 104);
        Assert.Equal("Ali", layout.GetText(ws, we));
    }

    [Fact]
    public void Reversed_range_and_hyphen_marker_are_handled()
    {
        var layout = new PdfTextLayout(["a", "\u0002", "b"], [new PdfRect(0, 0, 5, 10), null, new PdfRect(6, 0, 11, 10)]);
        Assert.Equal("a-b", layout.GetText(3, 0));
    }
}
