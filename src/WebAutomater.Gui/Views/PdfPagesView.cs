using System.Buffers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WebAutomater.Core.Documents;
using WebAutomater.Gui.ViewModels;

namespace WebAutomater.Gui.Views;

/// <summary>
/// PDF sayfalarını alt alta gösterir. Bir ScrollViewer'ın içinde durur ve toplam boyutu sayfa ölçülerinden
/// hesaplar; yalnızca görünen sayfalar (ve hemen yanındakiler) arka planda çizilir. Sayfa sayısı ne kadar
/// çok olursa olsun bellek ve çizim yükü sabit kalır.
/// <para>
/// Sayfadaki yazı fareyle seçilip kopyalanabilir: sürükleyerek (sayfalar arasında da), çift tıklayarak kelime,
/// üç kez tıklayarak satır; Shift+tıklama seçimi uzatır. Ctrl+C kopyalar, Ctrl+A tümünü seçer, sağ tık menüsü de var.
/// </para>
/// </summary>
public sealed class PdfPagesView : Control
{
    public static readonly StyledProperty<PdfDocument?> DocumentProperty =
        AvaloniaProperty.Register<PdfPagesView, PdfDocument?>(nameof(Document));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PdfPagesView, double>(nameof(Zoom), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> FitWidthProperty =
        AvaloniaProperty.Register<PdfPagesView, bool>(nameof(FitWidth), true, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IReadOnlyDictionary<int, List<PageHighlight>>?> HighlightsProperty =
        AvaloniaProperty.Register<PdfPagesView, IReadOnlyDictionary<int, List<PageHighlight>>?>(nameof(Highlights));

    public static readonly StyledProperty<bool> ShowHighlightsProperty =
        AvaloniaProperty.Register<PdfPagesView, bool>(nameof(ShowHighlights), true);

    public static readonly StyledProperty<bool> HideUnverifiedProperty =
        AvaloniaProperty.Register<PdfPagesView, bool>(nameof(HideUnverified));

    private static readonly IBrush ValidHighlight = new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xD6, 0x00));
    private static readonly IBrush UnverifiedHighlight = new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x6D, 0x00));
    private static readonly IBrush SelectionBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0x1E, 0x6F, 0xE0));
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    public const double MinZoom = 0.1;
    public const double MaxZoom = 6;
    private const double Gap = 10;
    private const double PointsToDip = 96.0 / 72.0;
    private const int Prefetch = 2;
    private const long MaxPagePixels = 40_000_000; // Çok büyük sayfa + yüksek yakınlaştırmada bellek taşmasın.
    private const long CacheBudgetBytes = 512L * 1024 * 1024;

    private sealed record CachedPage(Bitmap Bitmap, int PixelWidth, long Bytes)
    {
        public long LastUsed { get; set; }
    }

    private readonly record struct RenderRequest(int Generation, PdfDocument Document, int Page, int PixelWidth, int PixelHeight, bool NeedLayout);

    /// <summary>Seçimin bir ucu: sayfa ve o sayfadaki imleç konumu (karakterlerin arası).</summary>
    private readonly record struct TextPosition(int Page, int Caret) : IComparable<TextPosition>
    {
        public int CompareTo(TextPosition other) =>
            Page != other.Page ? Page.CompareTo(other.Page) : Caret.CompareTo(other.Caret);
    }

    private readonly Dictionary<int, CachedPage> _cache = [];
    private readonly Dictionary<int, PdfTextLayout> _layouts = [];
    private readonly HashSet<(int Page, int PixelWidth)> _failed = [];
    private readonly Lock _wantedLock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private List<RenderRequest> _wanted = [];
    private RenderRequest? _inFlight;
    private CancellationTokenSource? _workerCts;

    private ScrollViewer? _scroll;
    private double[] _tops = [];
    private double[] _widths = [];
    private double[] _heights = [];
    private double _maxWidth;
    private double _maxPointWidth;
    private double _totalHeight;
    private double _lastViewportWidth;
    private int _generation;
    private long _useClock;
    private long _cacheBytes;

    private TextPosition? _anchor;
    private TextPosition? _focus;
    private bool _selecting;
    private Point _dragPoint;
    private readonly DispatcherTimer _autoScroll;
    private readonly MenuItem _copyItem;

    static PdfPagesView()
    {
        AffectsMeasure<PdfPagesView>(ZoomProperty, DocumentProperty);
        AffectsRender<PdfPagesView>(HighlightsProperty, ShowHighlightsProperty, HideUnverifiedProperty);
    }

    public IReadOnlyDictionary<int, List<PageHighlight>>? Highlights
    {
        get => GetValue(HighlightsProperty);
        set => SetValue(HighlightsProperty, value);
    }

    public bool ShowHighlights
    {
        get => GetValue(ShowHighlightsProperty);
        set => SetValue(ShowHighlightsProperty, value);
    }

    public PdfPagesView()
    {
        // Sürüklerken fare görünür alanın dışına çıkınca sayfa kendiliğinden kayar.
        _autoScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) => AutoScrollTick());

        _copyItem = new MenuItem { Header = "Kopyala", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        _copyItem.Click += async (_, _) => await CopySelectionAsync();
        var selectAll = new MenuItem { Header = "Tümünü seç", InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        selectAll.Click += (_, _) => SelectAll();
        var menu = new ContextMenu { ItemsSource = new[] { _copyItem, selectAll } };
        menu.Opening += (_, _) => _copyItem.IsEnabled = HasSelection;
        ContextMenu = menu;
    }

    /// <summary>Seçili yazı var mı?</summary>
    public bool HasSelection => _anchor is { } a && _focus is { } f && a != f;

    public bool HideUnverified
    {
        get => GetValue(HideUnverifiedProperty);
        set => SetValue(HideUnverifiedProperty, value);
    }

    public PdfDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public bool FitWidth
    {
        get => GetValue(FitWidthProperty);
        set => SetValue(FitWidthProperty, value);
    }

    public int CurrentPage { get; private set; }

    /// <summary>Görünen sayfa değişince (1'den başlar).</summary>
    public event Action<int>? CurrentPageChanged;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll is not null)
        {
            _scroll.ScrollChanged += OnScrollChanged;
            // Ctrl+tekerlek, ScrollViewer kaydırmadan önce yakalanmalı.
            _scroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
            _scroll.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            _scroll.AddHandler(PointerPressedEvent, (_, _) => _scroll.Focus(), RoutingStrategies.Tunnel);
        }

        _workerCts = new CancellationTokenSource();
        var token = _workerCts.Token;
        _ = Task.Run(() => WorkerLoopAsync(token));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _workerCts?.Cancel();
        if (_scroll is not null)
        {
            _scroll.ScrollChanged -= OnScrollChanged;
            _scroll.RemoveHandler(PointerWheelChangedEvent, OnWheel);
            _scroll.RemoveHandler(KeyDownEvent, OnKeyDown);
            _scroll = null;
        }

        StopDrag();

        ClearCache();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DocumentProperty)
        {
            _generation++;
            ClearCache();
            _failed.Clear();
            lock (_wantedLock)
                _wanted = [];
            _layouts.Clear();
            ClearSelection();
            RebuildLayout();
            ApplyFitWidth();
            if (_scroll is not null)
                _scroll.Offset = default;
            SetCurrentPage(Document is { PageCount: > 0 } ? 1 : 0);
            InvalidateVisual();
        }
        else if (change.Property == ZoomProperty)
        {
            RebuildLayout();
            InvalidateVisual();
        }
        else if (change.Property == FitWidthProperty)
        {
            ApplyFitWidth();
        }
    }

    // ---------- Düzen ----------

    private void RebuildLayout()
    {
        var doc = Document;
        var count = doc?.PageCount ?? 0;
        _tops = new double[count];
        _widths = new double[count];
        _heights = new double[count];
        _maxWidth = 0;
        _maxPointWidth = 0;
        var y = Gap;
        var scale = Zoom * PointsToDip;
        for (var i = 0; i < count; i++)
        {
            var (w, h) = doc!.GetPageSize(i);
            _tops[i] = y;
            _widths[i] = w * scale;
            _heights[i] = h * scale;
            _maxWidth = Math.Max(_maxWidth, _widths[i]);
            _maxPointWidth = Math.Max(_maxPointWidth, w);
            y += _heights[i] + Gap;
        }

        _totalHeight = count == 0 ? 0 : y;
    }

    private void ApplyFitWidth()
    {
        if (!FitWidth || _scroll is null || _maxPointWidth <= 0 || _scroll.Viewport.Width <= 0)
            return;
        var available = _scroll.Viewport.Width - 2 * Gap;
        if (available > 10)
            Zoom = Math.Clamp(available / (_maxPointWidth * PointsToDip), MinZoom, MaxZoom);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var viewportWidth = _scroll?.Viewport.Width ?? 0;
        return new Size(Math.Max(_maxWidth + 2 * Gap, viewportWidth), _totalHeight);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null)
            return;
        if (Math.Abs(_scroll.Viewport.Width - _lastViewportWidth) > 0.5)
        {
            _lastViewportWidth = _scroll.Viewport.Width;
            InvalidateMeasure();
            ApplyFitWidth();
        }

        UpdateCurrentPage();
        InvalidateVisual();
    }

    private int FirstPageAt(double y)
    {
        // Üstü y'den küçük son sayfa.
        var index = Array.BinarySearch(_tops, y);
        if (index < 0)
            index = ~index - 1;
        return Math.Clamp(index, 0, Math.Max(_tops.Length - 1, 0));
    }

    private void UpdateCurrentPage()
    {
        if (_scroll is null || _tops.Length == 0)
            return;
        SetCurrentPage(FirstPageAt(_scroll.Offset.Y + _scroll.Viewport.Height / 2) + 1);
    }

    private void SetCurrentPage(int page)
    {
        if (page == CurrentPage)
            return;
        CurrentPage = page;
        CurrentPageChanged?.Invoke(page);
    }

    public void ScrollToPage(int oneBasedPage)
    {
        if (_scroll is null || _tops.Length == 0)
            return;
        var index = Math.Clamp(oneBasedPage - 1, 0, _tops.Length - 1);
        _scroll.Offset = new Vector(_scroll.Offset.X, _tops[index] - Gap);
    }

    public void ZoomBy(double factor) => SetManualZoom(Zoom * factor, null);

    private void SetManualZoom(double zoom, Point? anchorInViewport)
    {
        if (_scroll is null || Document is null)
            return;
        var newZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var ratio = newZoom / Zoom;
        var anchor = anchorInViewport ?? new Point(_scroll.Viewport.Width / 2, _scroll.Viewport.Height / 2);
        var content = new Point(_scroll.Offset.X + anchor.X, _scroll.Offset.Y + anchor.Y);
        FitWidth = false;
        Zoom = newZoom;
        _scroll.UpdateLayout();
        _scroll.Offset = new Vector(Math.Max(0, content.X * ratio - anchor.X), Math.Max(0, content.Y * ratio - anchor.Y));
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_scroll is null || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
            return;
        e.Handled = true;
        SetManualZoom(Zoom * Math.Pow(1.1, e.Delta.Y), e.GetPosition(_scroll));
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_scroll is null || Document is null)
            return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (e.Key == Key.C && HasSelection)
            {
                e.Handled = true;
                await CopySelectionAsync();
            }
            else if (e.Key == Key.A)
            {
                e.Handled = true;
                SelectAll();
            }

            if (e.Handled)
                return;
        }

        if (e.Key == Key.Escape && HasSelection)
        {
            e.Handled = true;
            ClearSelection();
            return;
        }
        var page = _scroll.Viewport.Height * 0.9;
        Vector? target = e.Key switch
        {
            Key.PageDown or Key.Space => _scroll.Offset + new Vector(0, page),
            Key.PageUp => _scroll.Offset - new Vector(0, page),
            Key.Home => new Vector(_scroll.Offset.X, 0),
            Key.End => new Vector(_scroll.Offset.X, _totalHeight),
            _ => null,
        };
        if (target is { } value)
        {
            _scroll.Offset = value;
            e.Handled = true;
        }
    }

    // ---------- Çizim ----------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var doc = Document;
        if (doc is null || _scroll is null || _tops.Length == 0)
            return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var viewTop = _scroll.Offset.Y;
        var viewBottom = viewTop + _scroll.Viewport.Height;
        var first = FirstPageAt(viewTop);
        var last = FirstPageAt(viewBottom);
        var clock = ++_useClock;

        // Saydam zemin: sayfaların arasında ve kenarında da fare olayları alınsın (seçimi sürükleyebilmek için).
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        for (var i = first; i <= last; i++)
        {
            var rect = PageRect(i, scaling);
            context.FillRectangle(Brushes.White, rect);
            if (_cache.TryGetValue(i, out var cached))
            {
                cached.LastUsed = clock;
                // Yakınlaştırma sırasında eski görüntü yenisi gelene kadar ölçeklenerek gösterilir.
                context.DrawImage(cached.Bitmap, rect);
            }

            DrawHighlights(context, i, rect);
            DrawSelection(context, i, rect);
        }

        RequestPages(doc, first, last, scaling);
    }

    /// <summary>Sayfanın denetim içindeki yeri; çizimle aynı yuvarlama, böylece fare ile görüntü örtüşür.</summary>
    private Rect PageRect(int page, double scaling)
    {
        var contentWidth = Math.Max(Bounds.Width, _maxWidth + 2 * Gap);
        var x = Math.Round((contentWidth - _widths[page]) / 2 * scaling) / scaling;
        var y = Math.Round(_tops[page] * scaling) / scaling;
        return new Rect(x, y, _widths[page], _heights[page]);
    }

    private void DrawSelection(DrawingContext context, int page, Rect pageRect)
    {
        if (SelectionOn(page) is not var (start, end) || start == end)
            return;
        var scale = Zoom * PointsToDip;
        foreach (var r in GetLayout(page).GetSelectionRects(start, end))
        {
            context.FillRectangle(SelectionBrush,
                new Rect(pageRect.X + r.Left * scale, pageRect.Y + r.Top * scale,
                    (r.Right - r.Left) * scale, (r.Bottom - r.Top) * scale));
        }
    }

    private void DrawHighlights(DrawingContext context, int page, Rect pageRect)
    {
        if (!ShowHighlights || Highlights is null || !Highlights.TryGetValue(page, out var items))
            return;
        var scale = Zoom * PointsToDip;
        foreach (var item in items)
        {
            if (!item.IsValid && HideUnverified)
                continue;
            var b = item.Bounds;
            // Yazının kenarlarına değmesin diye çok az taşırılır.
            const double pad = 1;
            context.FillRectangle(
                item.IsValid ? ValidHighlight : UnverifiedHighlight,
                new Rect(pageRect.X + b.Left * scale - pad, pageRect.Y + b.Top * scale - pad,
                    (b.Right - b.Left) * scale + 2 * pad, (b.Bottom - b.Top) * scale + 2 * pad));
        }
    }

    private (int Width, int Height) PixelSize(int page, double scaling)
    {
        var w = Math.Max(1, _widths[page] * scaling);
        var h = Math.Max(1, _heights[page] * scaling);
        if (w * h > MaxPagePixels)
        {
            var reduce = Math.Sqrt(MaxPagePixels / (w * h));
            w *= reduce;
            h *= reduce;
        }

        return ((int)Math.Round(w), (int)Math.Round(h));
    }

    private void RequestPages(PdfDocument doc, int first, int last, double scaling)
    {
        var wanted = new List<RenderRequest>();
        void Add(int page)
        {
            if (page < 0 || page >= _tops.Length)
                return;
            var (w, h) = PixelSize(page, scaling);
            if (_cache.TryGetValue(page, out var c) && c.PixelWidth == w)
                return;
            if (!_failed.Contains((page, w)))
                wanted.Add(new RenderRequest(_generation, doc, page, w, h, !_layouts.ContainsKey(page)));
        }

        for (var i = first; i <= last; i++)
            Add(i);
        for (var i = 1; i <= Prefetch; i++)
        {
            Add(last + i);
            Add(first - i);
        }

        lock (_wantedLock)
        {
            if (_inFlight is { } busy)
                wanted.Remove(busy);
            _wanted = wanted;
        }

        if (wanted.Count > 0 && _signal.CurrentCount == 0)
            _signal.Release();
    }

    private async Task WorkerLoopAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(token);
                while (TakeNext() is { } request)
                {
                    token.ThrowIfCancellationRequested();
                    Bitmap? bitmap = null;
                    PdfTextLayout? layout = null;
                    try
                    {
                        bitmap = RenderBitmap(request);
                        // Metin katmanı da arka planda hazırlansın: fare yazının üstüne gelince imleç hemen değişsin,
                        // seçime başlarken arayüz beklemesin.
                        if (request.NeedLayout)
                            layout = request.Document.GetTextLayout(request.Page);
                    }
                    catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or OutOfMemoryException or ArgumentException)
                    {
                        // Açılamayan sayfa beyaz kalır; aşağıda tekrar denenmemesi için işaretlenir.
                    }

                    // Sonuç arayüze işlenene kadar beklenir: aynı sayfa iki kez çizilmesin.
                    await Dispatcher.UIThread.InvokeAsync(() => Complete(request, bitmap, layout));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private RenderRequest? TakeNext()
    {
        lock (_wantedLock)
        {
            if (_wanted.Count == 0)
            {
                _inFlight = null;
                return null;
            }

            var next = _wanted[0];
            _wanted.RemoveAt(0);
            _inFlight = next;
            return next;
        }
    }

    private static Bitmap RenderBitmap(RenderRequest r)
    {
        var stride = r.PixelWidth * 4;
        var size = stride * r.PixelHeight;
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            r.Document.RenderPage(r.Page, r.PixelWidth, r.PixelHeight, buffer);
            unsafe
            {
                fixed (byte* p = buffer)
                {
                    // Bitmap veriyi kopyalar; arabellek hemen geri verilebilir.
                    return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, (IntPtr)p,
                        new PixelSize(r.PixelWidth, r.PixelHeight), new Vector(96, 96), stride);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Complete(RenderRequest request, Bitmap? bitmap, PdfTextLayout? layout)
    {
        if (request.Generation != _generation)
        {
            bitmap?.Dispose();
            return;
        }

        if (layout is not null)
            _layouts.TryAdd(request.Page, layout);

        if (bitmap is null)
        {
            _failed.Add((request.Page, request.PixelWidth));
            return;
        }

        if (_cache.Remove(request.Page, out var old))
        {
            _cacheBytes -= old.Bytes;
            old.Bitmap.Dispose();
        }

        var bytes = (long)request.PixelWidth * request.PixelHeight * 4;
        _cache[request.Page] = new CachedPage(bitmap, request.PixelWidth, bytes) { LastUsed = ++_useClock };
        _cacheBytes += bytes;
        EvictIfNeeded(request.Page);
        InvalidateVisual();
    }

    private void EvictIfNeeded(int keep)
    {
        while (_cacheBytes > CacheBudgetBytes && _cache.Count > 1)
        {
            // Görünen sayfalar her çizimde tazelenir, en eski kullanılan görünmeyendir.
            var victim = _cache.Where(p => p.Key != keep).MinBy(p => p.Value.LastUsed);
            _cache.Remove(victim.Key);
            _cacheBytes -= victim.Value.Bytes;
            victim.Value.Bitmap.Dispose();
        }
    }

    // ---------- Yazı seçimi ----------

    private PdfTextLayout GetLayout(int page)
    {
        if (_layouts.TryGetValue(page, out var layout))
            return layout;
        try
        {
            layout = Document?.GetTextLayout(page) ?? PdfTextLayout.Empty;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            layout = PdfTextLayout.Empty;
        }

        _layouts[page] = layout;
        return layout;
    }

    /// <summary>Denetimdeki bir noktanın düştüğü sayfa ve o sayfadaki konumu (nokta cinsinden).</summary>
    private (int Page, double X, double Y) ToPagePoint(Point point)
    {
        var page = FirstPageAt(point.Y);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var rect = PageRect(page, scaling);
        var scale = Zoom * PointsToDip;
        return (page, (point.X - rect.X) / scale, (point.Y - rect.Y) / scale);
    }

    private TextPosition CaretAt(Point point)
    {
        var (page, x, y) = ToPagePoint(point);
        // Sayfaların arasındaki boşluk bir sonraki sayfanın başı sayılır.
        if (y > _heights[page] / (Zoom * PointsToDip) && page + 1 < _tops.Length)
            return new TextPosition(page + 1, 0);
        return new TextPosition(page, GetLayout(page).CaretAt(x, y));
    }

    /// <summary>Seçimin bu sayfaya düşen [başlangıç, bitiş) aralığı; sayfa seçimde değilse null.</summary>
    private (int Start, int End)? SelectionOn(int page)
    {
        if (_anchor is not { } a || _focus is not { } f || a == f)
            return null;
        var (start, end) = a.CompareTo(f) <= 0 ? (a, f) : (f, a);
        if (page < start.Page || page > end.Page)
            return null;
        return (page == start.Page ? start.Caret : 0, page == end.Page ? end.Caret : int.MaxValue);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Document is null || _tops.Length == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var point = e.GetPosition(this);
        if (e.ClickCount >= 2)
        {
            var (page, x, y) = ToPagePoint(point);
            var layout = GetLayout(page);
            var (start, end) = e.ClickCount == 2 ? layout.WordAt(x, y) : layout.LineAt(x, y);
            _anchor = new TextPosition(page, start);
            _focus = new TextPosition(page, end);
            StopDrag();
        }
        else
        {
            var caret = CaretAt(point);
            if ((e.KeyModifiers & KeyModifiers.Shift) == 0 || _anchor is null)
                _anchor = caret;
            _focus = caret;
            _selecting = true;
            _dragPoint = point;
            e.Pointer.Capture(this);
        }

        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Document is null || _tops.Length == 0)
            return;
        var point = e.GetPosition(this);
        if (_selecting)
        {
            _dragPoint = point;
            ExtendSelection();
            if (ScrollSpeed() != default)
                _autoScroll.Start();
            return;
        }

        // Yazının üstünde metin imleci; metin katmanı henüz hazır değilse beklenmez.
        var (page, x, y) = ToPagePoint(point);
        Cursor = _layouts.TryGetValue(page, out var layout) && layout.IsOverText(x, y) ? TextCursor : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting)
            return;
        e.Pointer.Capture(null);
        StopDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        StopDrag();
    }

    private void StopDrag()
    {
        _selecting = false;
        _autoScroll.Stop();
    }

    private void ExtendSelection()
    {
        var caret = CaretAt(_dragPoint);
        if (_focus == caret)
            return;
        _focus = caret;
        InvalidateVisual();
    }

    /// <summary>Fare görünür alanın ne kadar dışında: kayma hızı (sıfırsa içeride).</summary>
    private Vector ScrollSpeed()
    {
        if (_scroll is null)
            return default;
        static double Outside(double value, double size) => value < 0 ? value : value > size ? value - size : 0;
        var x = Outside(_dragPoint.X - _scroll.Offset.X, _scroll.Viewport.Width);
        var y = Outside(_dragPoint.Y - _scroll.Offset.Y, _scroll.Viewport.Height);
        return new Vector(Math.Clamp(x, -60, 60), Math.Clamp(y, -60, 60));
    }

    private void AutoScrollTick()
    {
        var speed = ScrollSpeed();
        if (!_selecting || _scroll is null || speed == default)
        {
            _autoScroll.Stop();
            return;
        }

        var old = _scroll.Offset;
        var max = new Vector(Math.Max(0, _scroll.Extent.Width - _scroll.Viewport.Width), Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height));
        var target = new Vector(Math.Clamp(old.X + speed.X, 0, max.X), Math.Clamp(old.Y + speed.Y, 0, max.Y));
        if (target == old)
            return;
        _scroll.Offset = target;
        // Fare yerinde durur, altındaki içerik kayar.
        _dragPoint += target - old;
        ExtendSelection();
    }

    public void ClearSelection()
    {
        _anchor = _focus = null;
        StopDrag();
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (_tops.Length == 0)
            return;
        _anchor = new TextPosition(0, 0);
        _focus = new TextPosition(_tops.Length - 1, int.MaxValue);
        InvalidateVisual();
    }

    /// <summary>Seçili yazı; sayfalar ve satırlar alt satıra geçerek ayrılır.</summary>
    public string GetSelectedText()
    {
        if (_anchor is not { } a || _focus is not { } f || a == f)
            return "";
        var (start, end) = a.CompareTo(f) <= 0 ? (a, f) : (f, a);
        var parts = new List<string>();
        for (var page = start.Page; page <= end.Page && page < _tops.Length; page++)
        {
            var (from, to) = SelectionOn(page)!.Value;
            parts.Add(GetLayout(page).GetText(from, to));
        }

        return string.Join('\n', parts).Replace("\n", Environment.NewLine);
    }

    private async Task CopySelectionAsync()
    {
        var text = GetSelectedText();
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private void ClearCache()
    {
        foreach (var page in _cache.Values)
            page.Bitmap.Dispose();
        _cache.Clear();
        _cacheBytes = 0;
    }
}
