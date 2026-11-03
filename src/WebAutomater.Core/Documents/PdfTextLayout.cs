using System.Text;

namespace WebAutomater.Core.Documents;

/// <summary>
/// Bir sayfanın seçilebilir metin katmanı: her karakter ve sayfadaki kutusu.
/// Fareyle seçim için gereken hesaplar (imleç konumu, kelime/satır, seçim dikdörtgenleri, kopyalanacak metin)
/// burada, kütüphaneden bağımsız yapılır.
/// <para>
/// İmleç konumu karakterlerin arasıdır: 0 ilk karakterin önü, <see cref="Count"/> son karakterin arkası.
/// Bir [başlangıç, bitiş) aralığı bu konumlar arasındaki karakterleri seçer.
/// </para>
/// </summary>
public sealed class PdfTextLayout
{
    public static readonly PdfTextLayout Empty = new([], []);

    private readonly string[] _chars;
    private readonly PdfRect?[] _boxes;
    private readonly Line[] _lines;
    private readonly int[] _lineOf;

    /// <summary>Bir satır: karakter aralığı, kapladığı alan ve yazı yönü (döndürülmüş sayfalar için).</summary>
    private sealed record Line(int Start, int End, PdfRect Bounds, double DirX, double DirY, double OriginX, double OriginY);

    /// <param name="chars">Her karakterin metni (genelde tek harf; PDFium'un eklediği boşluk ve satır sonları dahil).</param>
    /// <param name="boxes">Her karakterin kutusu; kutusu olmayan (eklenmiş boşluk, satır sonu) için null.</param>
    public PdfTextLayout(IReadOnlyList<string> chars, IReadOnlyList<PdfRect?> boxes)
    {
        if (chars.Count != boxes.Count)
            throw new ArgumentException("Karakter ve kutu sayısı aynı olmalı.");
        _chars = [.. chars];
        _boxes = [.. boxes];
        _lineOf = new int[_chars.Length];
        _lines = BuildLines();
    }

    public int Count => _chars.Length;

    public bool HasText => _lines.Length > 0;

    private Line[] BuildLines()
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i <= _chars.Length; i++)
        {
            if (i < _chars.Length && !IsLineBreak(_chars[i]))
                continue;
            AddLine(start, i);
            if (i < _chars.Length)
                _lineOf[i] = Math.Max(lines.Count - 1, 0);
            start = i + 1;
        }

        return [.. lines];

        void AddLine(int from, int to)
        {
            int? first = null, last = null;
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            for (var i = from; i < to; i++)
            {
                if (_boxes[i] is not { } b)
                    continue;
                first ??= i;
                last = i;
                left = Math.Min(left, b.Left);
                top = Math.Min(top, b.Top);
                right = Math.Max(right, b.Right);
                bottom = Math.Max(bottom, b.Bottom);
            }

            if (first is null)
            {
                // Kutusu olmayan satır (boş satır): karakterleri bir önceki satıra say.
                for (var i = from; i < to; i++)
                    _lineOf[i] = Math.Max(lines.Count - 1, 0);
                return;
            }

            // Yazı yönü: ilk karakterden son karaktere. Tek karakterlik satır ya da üst üste binen
            // karakterlerde yatay kabul edilir.
            var (fx, fy) = Center(_boxes[first.Value]!.Value);
            var (lx, ly) = Center(_boxes[last!.Value]!.Value);
            double dx = lx - fx, dy = ly - fy;
            var length = Math.Sqrt(dx * dx + dy * dy);
            var height = Math.Min(right - left, bottom - top);
            if (length < Math.Max(height / 2, 0.5))
                (dx, dy, length) = (1, 0, 1);

            var index = lines.Count;
            lines.Add(new Line(from, to, new PdfRect(left, top, right, bottom), dx / length, dy / length, fx, fy));
            for (var i = from; i < to; i++)
                _lineOf[i] = index;
        }
    }

    private static bool IsLineBreak(string c) => c is "\r" or "\n" or "\r\n";

    private static (double X, double Y) Center(PdfRect r) => ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);

    /// <summary>Nokta bir karakterin üstünde mi? (Fare imleci için.)</summary>
    public bool IsOverText(double x, double y)
    {
        foreach (var line in _lines)
        {
            if (!Contains(line.Bounds, x, y, 1))
                continue;
            for (var i = line.Start; i < line.End; i++)
            {
                if (_boxes[i] is { } b && Contains(b, x, y, 1))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Noktaya en yakın imleç konumu. Nokta yazının dışındaysa (boşluk, kenar, sayfa dışı) en yakın satıra göre bulunur;
    /// böylece sürükleme yazının dışına taşsa da seçim düzgün uzar.
    /// </summary>
    public int CaretAt(double x, double y)
    {
        if (_lines.Length == 0)
            return 0;
        var line = NearestLine(x, y);
        return CaretInLine(line, x, y);
    }

    private Line NearestLine(double x, double y)
    {
        Line best = _lines[0];
        var bestScore = double.MaxValue;
        foreach (var line in _lines)
        {
            // Satırın dik yönündeki uzaklık çok daha önemli: aynı yükseklikteki uzak bir satır,
            // üstteki yakın bir satırdan önce gelmesin.
            var (along, across) = Distances(line, x, y);
            var score = across * 4 + along;
            if (score < bestScore)
            {
                bestScore = score;
                best = line;
            }
        }

        return best;
    }

    /// <summary>Noktanın satır kutusuna, yazı yönünde ve ona dik yöndeki uzaklığı.</summary>
    private static (double Along, double Across) Distances(Line line, double x, double y)
    {
        var b = line.Bounds;
        var dx = x < b.Left ? b.Left - x : x > b.Right ? x - b.Right : 0;
        var dy = y < b.Top ? b.Top - y : y > b.Bottom ? y - b.Bottom : 0;
        return Math.Abs(line.DirX) >= Math.Abs(line.DirY) ? (dx, dy) : (dy, dx);
    }

    private int CaretInLine(Line line, double x, double y)
    {
        var t = Project(line, x, y);
        var lastWithBox = line.Start;
        for (var i = line.Start; i < line.End; i++)
        {
            if (_boxes[i] is not { } b)
                continue;
            var (cx, cy) = Center(b);
            if (t < Project(line, cx, cy))
                return i;
            lastWithBox = i;
        }

        // Son karakterin arkası; satır sonundaki eklenmiş boşlukları da kapsar ki satır sonuna kadar seçilebilsin.
        return lastWithBox + 1 < line.End ? line.End : lastWithBox + 1;
    }

    private static double Project(Line line, double x, double y) =>
        (x - line.OriginX) * line.DirX + (y - line.OriginY) * line.DirY;

    /// <summary>Noktadaki kelimenin aralığı (çift tıklama). Harf/rakam değilse tek karakter.</summary>
    public (int Start, int End) WordAt(double x, double y)
    {
        var index = CharAt(x, y);
        if (index < 0)
            return (0, 0);
        if (!IsWordChar(index))
            return (index, index + 1);
        var start = index;
        while (start > 0 && IsWordChar(start - 1))
            start--;
        var end = index + 1;
        while (end < _chars.Length && IsWordChar(end))
            end++;
        return (start, end);
    }

    /// <summary>Noktadaki satırın aralığı (üç kez tıklama).</summary>
    public (int Start, int End) LineAt(double x, double y)
    {
        if (_lines.Length == 0)
            return (0, 0);
        var line = NearestLine(x, y);
        return (line.Start, line.End);
    }

    private bool IsWordChar(int i) => _chars[i].Length > 0 && (char.IsLetterOrDigit(_chars[i], 0) || _chars[i] is "_");

    /// <summary>Noktadaki ya da ona en yakın karakter; metin yoksa -1.</summary>
    private int CharAt(double x, double y)
    {
        if (_lines.Length == 0)
            return -1;
        var line = NearestLine(x, y);
        var t = Project(line, x, y);
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = line.Start; i < line.End; i++)
        {
            if (_boxes[i] is not { } b)
                continue;
            if (Contains(b, x, y, 0))
                return i;
            var (cx, cy) = Center(b);
            var distance = Math.Abs(Project(line, cx, cy) - t);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Aralıktaki metin; satır sonları "\n" olarak.</summary>
    public string GetText(int start, int end)
    {
        (start, end) = Clamp(start, end);
        var sb = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            var c = _chars[i];
            if (IsLineBreak(c))
            {
                // PDFium satır sonunu "\r" + "\n" iki karakter olarak verir; tek satır sonu yazılsın.
                if (c != "\n" || i == start || _chars[i - 1] != "\r")
                    sb.Append('\n');
                continue;
            }

            foreach (var ch in c)
            {
                // PDFium satır sonunda bölünen kelimenin tiresini 0x02 ya da 0xFFFE olarak işaretler.
                if (ch is '\u0002' or '￾')
                    sb.Append('-');
                else if (ch == '\t' || !char.IsControl(ch))
                    sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>Aralığı ekranda boyamak için dikdörtgenler: her satır için bir tane.</summary>
    public IReadOnlyList<PdfRect> GetSelectionRects(int start, int end)
    {
        (start, end) = Clamp(start, end);
        var rects = new List<PdfRect>();
        if (start >= end)
            return rects;
        for (var l = _lineOf[start]; l < _lines.Length && _lines[l].Start < end; l++)
        {
            var line = _lines[l];
            var from = Math.Max(start, line.Start);
            var to = Math.Min(end, line.End);
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            for (var i = from; i < to; i++)
            {
                if (_boxes[i] is not { } b)
                    continue;
                left = Math.Min(left, b.Left);
                top = Math.Min(top, b.Top);
                right = Math.Max(right, b.Right);
                bottom = Math.Max(bottom, b.Bottom);
            }

            if (left <= right)
            {
                // Seçim satırın tamamı kalınlığında olsun; harf boylarına göre inip çıkmasın.
                var lb = line.Bounds;
                rects.Add(Math.Abs(line.DirX) >= Math.Abs(line.DirY)
                    ? new PdfRect(left, lb.Top, right, lb.Bottom)
                    : new PdfRect(lb.Left, top, lb.Right, bottom));
            }
        }

        return rects;
    }

    private (int, int) Clamp(int start, int end)
    {
        if (start > end)
            (start, end) = (end, start);
        return (Math.Clamp(start, 0, _chars.Length), Math.Clamp(end, 0, _chars.Length));
    }

    private static bool Contains(PdfRect r, double x, double y, double tolerance) =>
        x >= r.Left - tolerance && x <= r.Right + tolerance && y >= r.Top - tolerance && y <= r.Bottom + tolerance;
}
