using System.Runtime.InteropServices;

namespace WebAutomater.Core.Documents;

/// <summary>Sayfa üzerinde bir dikdörtgen: nokta cinsinden, sol üst köşe başlangıç, döndürme uygulanmış.</summary>
public readonly record struct PdfRect(double Left, double Top, double Right, double Bottom);

public sealed class PdfOpenException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// PDFium üzerinden bir PDF. Dosya tamamen belleğe okunur: böylece Windows'ta dosya kilitli kalmaz
/// ve Türkçe karakterli/ağ yolları kütüphanenin yol kodlamasına takılmaz.
/// PDFium iş parçacığı güvenli değildir; tüm çağrılar tek bir genel kilit altında yapılır.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private static readonly Lock Gate = new();
    private static bool _initialized;

    private GCHandle _pin;
    private IntPtr _handle;
    private readonly (double Width, double Height)[] _pageSizes;

    private PdfDocument(string path, GCHandle pin, IntPtr handle, (double, double)[] pageSizes)
    {
        Path = path;
        _pin = pin;
        _handle = handle;
        _pageSizes = pageSizes;
    }

    public string Path { get; }

    public int PageCount => _pageSizes.Length;

    /// <summary>Sayfa boyutu, nokta (1/72 inç) cinsinden; döndürme uygulanmış halde.</summary>
    public (double Width, double Height) GetPageSize(int index) => _pageSizes[index];

    public static PdfDocument Open(string path)
    {
        byte[] bytes;
        try
        {
            // Başka bir uygulama dosyayı açık tutuyor olsa da okuyabilelim.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            if (stream.Length > Array.MaxLength)
                throw new PdfOpenException("Dosya çok büyük (en fazla 2 GB).");
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfOpenException($"Dosya okunamadı: {ex.Message}", ex);
        }

        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        var keepPin = false;
        try
        {
            lock (Gate)
            {
                EnsureInitialized();
                var handle = Native.FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)bytes.Length, IntPtr.Zero);
                if (handle == IntPtr.Zero)
                    throw new PdfOpenException(DescribeError(Native.FPDF_GetLastError()));

                var count = Native.FPDF_GetPageCount(handle);
                var sizes = new (double, double)[Math.Max(count, 0)];
                for (var i = 0; i < sizes.Length; i++)
                {
                    // Boyutu okunamayan bozuk bir sayfa tüm belgeyi engellemesin: A4 varsay.
                    sizes[i] = Native.FPDF_GetPageSizeByIndex(handle, i, out var w, out var h) != 0 && w > 0 && h > 0
                        ? (w, h)
                        : (595, 842);
                }

                keepPin = true;
                return new PdfDocument(path, pin, handle, sizes);
            }
        }
        catch (DllNotFoundException ex)
        {
            throw new PdfOpenException("PDF bileşeni (pdfium) yüklenemedi. Uygulamayı yeniden kurmayı deneyin.", ex);
        }
        finally
        {
            if (!keepPin)
                pin.Free();
        }
    }

    /// <summary>
    /// Sayfayı BGRA olarak verilen arabelleğe çizer (beyaz zemin, satır adımı = genişlik * 4).
    /// </summary>
    public void RenderPage(int index, int pixelWidth, int pixelHeight, Span<byte> destination)
    {
        var stride = pixelWidth * 4;
        if (destination.Length < stride * pixelHeight)
            throw new ArgumentException("Arabellek küçük.", nameof(destination));

        unsafe
        {
            fixed (byte* buffer = destination)
            {
                lock (Gate)
                {
                    var page = LoadPage(index);
                    var bitmap = Native.FPDFBitmap_CreateEx(pixelWidth, pixelHeight, Native.BitmapBgra, (IntPtr)buffer, stride);
                    if (bitmap == IntPtr.Zero)
                    {
                        Native.FPDF_ClosePage(page);
                        throw new InvalidOperationException("Sayfa görüntüsü oluşturulamadı.");
                    }

                    try
                    {
                        Native.FPDFBitmap_FillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
                        Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, pixelWidth, pixelHeight, 0, Native.RenderAnnotations);
                    }
                    finally
                    {
                        Native.FPDFBitmap_Destroy(bitmap);
                        Native.FPDF_ClosePage(page);
                    }
                }
            }
        }
    }

    /// <summary>Sayfanın metin katmanı. Taranmış sayfalarda boş döner.</summary>
    public string ExtractText(int index)
    {
        lock (Gate)
        {
            var page = LoadPage(index);
            try
            {
                var text = Native.FPDFText_LoadPage(page);
                if (text == IntPtr.Zero)
                    return "";
                try
                {
                    var count = Native.FPDFText_CountChars(text);
                    if (count <= 0)
                        return "";
                    var buffer = new char[count + 1];
                    unsafe
                    {
                        fixed (char* p = buffer)
                        {
                            var written = Native.FPDFText_GetText(text, 0, count, (IntPtr)p);
                            // Dönen sayı sondaki NUL karakteri de içerir.
                            return written <= 1 ? "" : new string(buffer, 0, written - 1);
                        }
                    }
                }
                finally
                {
                    Native.FPDFText_ClosePage(text);
                }
            }
            finally
            {
                Native.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// <see cref="ExtractText"/> metnindeki aralıkların sayfadaki konumları. Her aralık için bir liste döner
    /// (metin birden çok satıra ya da parçaya bölünmüşse birden çok dikdörtgen).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<PdfRect>> GetTextBounds(int index, IReadOnlyList<(int Start, int Length)> ranges)
    {
        var (width, height) = _pageSizes[index];
        lock (Gate)
        {
            var page = LoadPage(index);
            try
            {
                var text = Native.FPDFText_LoadPage(page);
                if (text == IntPtr.Zero)
                    return ranges.Select(_ => (IReadOnlyList<PdfRect>)[]).ToList();
                try
                {
                    var result = new List<IReadOnlyList<PdfRect>>(ranges.Count);
                    foreach (var (start, length) in ranges)
                        result.Add(GetRangeBounds(page, text, start, length, width, height));
                    return result;
                }
                finally
                {
                    Native.FPDFText_ClosePage(text);
                }
            }
            finally
            {
                Native.FPDF_ClosePage(page);
            }
        }
    }

    private static List<PdfRect> GetRangeBounds(IntPtr page, IntPtr text, int start, int length, double width, double height)
    {
        // Metin dizinleri ile PDFium'un karakter dizinleri her zaman aynı değildir.
        var first = Native.FPDFText_GetCharIndexFromTextIndex(text, start);
        var last = Native.FPDFText_GetCharIndexFromTextIndex(text, start + length - 1);
        var rects = new List<PdfRect>();
        if (first < 0 || last < first)
            return rects;

        var count = Native.FPDFText_CountRects(text, first, last - first + 1);
        for (var i = 0; i < count; i++)
        {
            if (Native.FPDFText_GetRect(text, i, out var left, out var top, out var right, out var bottom) == 0)
                continue;
            rects.Add(ToPageView(page, width, height, left, top, right, bottom));
        }

        return rects;
    }

    /// <summary>
    /// PDF koordinatındaki bir kutuyu ekrandaki sayfa koordinatına (nokta, sol üst başlangıç) çevirir.
    /// Çeviriyi PDFium yapar: döndürülmüş sayfalar ve kırpma kutusu da doğru çıkar.
    /// </summary>
    private static PdfRect ToPageView(IntPtr page, double width, double height, double left, double top, double right, double bottom)
    {
        // PDFium tam sayı döndürdüğü için hassasiyet adına büyütülmüş bir alana çevrilir.
        const int precision = 20;
        var sizeX = (int)Math.Round(width * precision);
        var sizeY = (int)Math.Round(height * precision);
        Native.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, left, top, out var x1, out var y1);
        Native.FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, right, bottom, out var x2, out var y2);
        return new PdfRect(
            Math.Min(x1, x2) / (double)precision,
            Math.Min(y1, y2) / (double)precision,
            Math.Max(x1, x2) / (double)precision,
            Math.Max(y1, y2) / (double)precision);
    }

    /// <summary>
    /// Sayfanın fareyle seçilebilir metin katmanı: her karakter ve ekrandaki kutusu.
    /// Taranmış (yalnızca resim olan) sayfalarda boş döner.
    /// </summary>
    public PdfTextLayout GetTextLayout(int index)
    {
        var (width, height) = _pageSizes[index];
        lock (Gate)
        {
            var page = LoadPage(index);
            try
            {
                var text = Native.FPDFText_LoadPage(page);
                if (text == IntPtr.Zero)
                    return PdfTextLayout.Empty;
                try
                {
                    var count = Math.Max(Native.FPDFText_CountChars(text), 0);
                    var chars = new string[count];
                    var boxes = new PdfRect?[count];
                    for (var i = 0; i < count; i++)
                    {
                        var code = Native.FPDFText_GetUnicode(text, i);
                        chars[i] = code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
                            ? char.ConvertFromUtf32((int)code)
                            : "";
                        boxes[i] = GetCharBox(page, text, i, width, height);
                    }

                    return new PdfTextLayout(chars, boxes);
                }
                finally
                {
                    Native.FPDFText_ClosePage(text);
                }
            }
            finally
            {
                Native.FPDF_ClosePage(page);
            }
        }
    }

    private static PdfRect? GetCharBox(IntPtr page, IntPtr text, int index, double width, double height)
    {
        // PDFium'un eklediği boşluk ve satır sonlarının sayfada yeri yoktur.
        if (Native.FPDFText_IsGenerated(text, index) == 1)
            return null;

        // "Gevşek" kutu yazı tipinin tam satır yüksekliğidir: seçim harf boyuna göre inip çıkmaz.
        double left, top, right, bottom;
        if (Native.FPDFText_GetLooseCharBox(text, index, out var loose) != 0 && loose.Right > loose.Left && loose.Top > loose.Bottom)
            (left, top, right, bottom) = (loose.Left, loose.Top, loose.Right, loose.Bottom);
        else if (Native.FPDFText_GetCharBox(text, index, out left, out right, out bottom, out top) == 0 || right <= left || top <= bottom)
            return null;

        return ToPageView(page, width, height, left, top, right, bottom);
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_handle == IntPtr.Zero)
                return;
            Native.FPDF_CloseDocument(_handle);
            _handle = IntPtr.Zero;
            _pin.Free();
        }
    }

    private IntPtr LoadPage(int index)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        var page = Native.FPDF_LoadPage(_handle, index);
        return page != IntPtr.Zero ? page : throw new InvalidOperationException($"Sayfa {index + 1} açılamadı.");
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
            return;
        Native.FPDF_InitLibrary();
        _initialized = true;
    }

    private static string DescribeError(uint code) => code switch
    {
        Native.ErrorFile => "Dosya açılamadı.",
        Native.ErrorFormat => "Dosya geçerli bir PDF değil ya da bozuk.",
        Native.ErrorPassword => "Bu PDF parola korumalı; parolalı dosyalar açılamıyor.",
        Native.ErrorSecurity => "Bu PDF desteklenmeyen bir güvenlik yöntemi kullanıyor.",
        _ => $"PDF açılamadı (hata kodu {code}).",
    };

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct FsRectF
        {
            public float Left, Top, Right, Bottom;
        }

        private const string Lib = "pdfium";

        public const int BitmapBgra = 4;
        public const int RenderAnnotations = 0x01;
        public const uint ErrorFile = 2, ErrorFormat = 3, ErrorPassword = 4, ErrorSecurity = 5;

        [DllImport(Lib)] public static extern void FPDF_InitLibrary();
        [DllImport(Lib)] public static extern uint FPDF_GetLastError();
        [DllImport(Lib)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, IntPtr password);
        [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Lib)] public static extern int FPDF_GetPageSizeByIndex(IntPtr document, int index, out double width, out double height);
        [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Lib)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr buffer, int stride);
        [DllImport(Lib)] public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
        [DllImport(Lib)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Lib)] public static extern void FPDFText_ClosePage(IntPtr textPage);
        [DllImport(Lib)] public static extern int FPDFText_CountChars(IntPtr textPage);
        [DllImport(Lib)] public static extern int FPDFText_GetText(IntPtr textPage, int startIndex, int count, IntPtr result);
        [DllImport(Lib)] public static extern int FPDFText_GetCharIndexFromTextIndex(IntPtr textPage, int textIndex);
        [DllImport(Lib)] public static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);
        [DllImport(Lib)] public static extern int FPDFText_GetRect(IntPtr textPage, int rectIndex, out double left, out double top, out double right, out double bottom);
        [DllImport(Lib)] public static extern uint FPDFText_GetUnicode(IntPtr textPage, int index);
        [DllImport(Lib)] public static extern int FPDFText_IsGenerated(IntPtr textPage, int index);
        [DllImport(Lib)] public static extern int FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);
        [DllImport(Lib)] public static extern int FPDFText_GetLooseCharBox(IntPtr textPage, int index, out FsRectF rect);
        [DllImport(Lib)] public static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
    }
}
