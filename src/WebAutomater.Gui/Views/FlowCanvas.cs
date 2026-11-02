using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using WebAutomater.Core.Models;
using WebAutomater.Gui.ViewModels;

namespace WebAutomater.Gui.Views;

/// <summary>
/// Senaryonun düğüm ekranı. Düğümler ve bağlantılar tek bir kontrolde çizilir: yüzlerce düğümde de hızlıdır
/// ve yalnızca görünenler işlenir. Değer düzenleme yan paneldedir, burada yerleşim ve bağlantı yapılır.
/// </summary>
public sealed class FlowCanvas : Control
{
    private const double W = ScenarioGraph.NodeWidth;
    private const double H = ScenarioGraph.NodeHeight;
    private const double PortRadius = 7;
    private const double PortHitRadius = 13;
    private const double EdgeHitDistance = 7;
    private const int EdgeSamples = 28;
    private const double MinZoom = 0.25;
    private const double MaxZoom = 2;

    private static readonly IBrush NextBrush = new ImmutableSolidColorBrush(Color.Parse("#607D8B"));
    private static readonly IBrush YesBrush = new ImmutableSolidColorBrush(Color.Parse("#43A047"));
    private static readonly IBrush NoBrush = new ImmutableSolidColorBrush(Color.Parse("#EF8E00"));
    private static readonly IBrush ErrorBrush = new ImmutableSolidColorBrush(Color.Parse("#E53935"));
    private static readonly IBrush SelectBrush = new ImmutableSolidColorBrush(Color.Parse("#1E88E5"));
    private static readonly IBrush RunningFill = new ImmutableSolidColorBrush(Color.Parse("#552196F3"));
    private static readonly IBrush OkFill = new ImmutableSolidColorBrush(Color.Parse("#3343A047"));
    private static readonly IBrush NoFill = new ImmutableSolidColorBrush(Color.Parse("#44EF8E00"));
    private static readonly IBrush FailedFill = new ImmutableSolidColorBrush(Color.Parse("#44E53935"));
    private static readonly Typeface Regular = new(FontFamily.Default);
    private static readonly Typeface Bold = new(FontFamily.Default, weight: FontWeight.SemiBold);

    private enum DragMode { None, Pan, Node, Connect }

    private MainWindowViewModel? _vm;
    private Vector _pan = new(24, 16);
    private double _zoom = 1;
    private DragMode _drag;
    private Point _dragStart;
    private Vector _dragOffset;
    private bool _dragMoved;
    private StepViewModel? _dragNode;
    private (Step Step, StepPort Port)? _connectFrom;
    private Point _connectEnd;
    private (Step Step, StepPort Port)? _selectedEdge;
    private (Step Step, StepPort Port)? _hoverPort;
    private StepViewModel? _lastSelected;
    private Step? _loopStart;

    public FlowCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        DataContextChanged += (_, _) => AttachViewModel(DataContext as MainWindowViewModel);
    }

    public double ZoomLevel => _zoom;

    private void AttachViewModel(MainWindowViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.FlowChanged -= InvalidateVisual;
            _vm.ScenarioLoaded -= OnScenarioLoaded;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = vm;
        if (_vm is not null)
        {
            _vm.FlowChanged += InvalidateVisual;
            _vm.ScenarioLoaded += OnScenarioLoaded;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        InvalidateVisual();
    }

    private void OnScenarioLoaded()
    {
        _selectedEdge = null;
        _pan = new Vector(24, 16);
        _zoom = 1;
        InvalidateVisual();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedStep) || _vm is null)
            return;
        if (_vm.SelectedStep != _lastSelected)
        {
            _lastSelected = _vm.SelectedStep;
            if (_lastSelected is not null)
                EnsureVisible(_lastSelected.Step);
        }

        InvalidateVisual();
    }

    /// <summary>Düğüm ekran dışındaysa görünür alana getirir (ör. kayıt sırasında eklenen son adım).</summary>
    public void EnsureVisible(Step step)
    {
        if (Bounds.Width <= 0 || step.X is null)
            return;
        var rect = new Rect(step.X.Value * _zoom + _pan.X, step.Y!.Value * _zoom + _pan.Y, W * _zoom, H * _zoom);
        const double margin = 30;
        var dx = rect.Left < margin ? margin - rect.Left : rect.Right > Bounds.Width - margin ? Bounds.Width - margin - rect.Right : 0;
        var dy = rect.Top < margin ? margin - rect.Top : rect.Bottom > Bounds.Height - margin ? Bounds.Height - margin - rect.Bottom : 0;
        _pan += new Vector(dx, dy);
    }

    /// <summary>Görünen alanın ortası sabit kalacak şekilde yakınlaştırır.</summary>
    public void ZoomBy(double factor) => ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), _zoom * factor);

    private void ZoomAround(Point screen, double zoom)
    {
        var world = ToWorld(screen);
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _pan = new Vector(screen.X - world.X * _zoom, screen.Y - world.Y * _zoom);
        InvalidateVisual();
    }

    // ---------- Geometri ----------

    private Point ToWorld(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);

    private static Rect NodeRect(Step s) => new(s.X ?? 0, s.Y ?? 0, W, H);

    private static Point InPort(Step s) => new((s.X ?? 0) + W / 2, s.Y ?? 0);

    private static Point OutPort(Step s, StepPort port)
    {
        var x = s.X ?? 0;
        var y = s.Y ?? 0;
        return port switch
        {
            StepPort.Error => new Point(x + W, y + H / 2),
            StepPort.False => new Point(x + W * 0.72, y + H),
            _ when s.Type == StepType.Condition => new Point(x + W * 0.28, y + H),
            _ => new Point(x + W / 2, y + H),
        };
    }

    private static (Point C1, Point C2) Controls(Point a, Point b, StepPort port)
    {
        var bend = Math.Max(40, Math.Min(140, Math.Abs(b.Y - a.Y) * 0.5 + Math.Abs(b.X - a.X) * 0.15));

        // Aynı sütunda bir düğümü atlayan ya da geri dönen bağlantı düğümlerin arkasından geçmesin: yandan dolaşır.
        // Eğri, denetim noktalarının yatay uzaklığının 3/4'ü kadar açılır.
        var sameColumn = Math.Abs(b.X - a.X) < W;
        var skipsNodes = b.Y - a.Y > H + 60 || b.Y <= a.Y;
        if (sameColumn && skipsNodes)
        {
            var side = (port == StepPort.Next ? -1 : 1) * (W / 2 + 25) / 0.75;
            return (new Point(a.X + side, a.Y + bend), new Point(b.X + side, b.Y - bend));
        }

        var c1 = port == StepPort.Error ? new Point(a.X + bend, a.Y) : new Point(a.X, a.Y + bend);
        return (c1, new Point(b.X, b.Y - bend));
    }

    private static Point Bezier(Point a, Point c1, Point c2, Point b, double t)
    {
        var u = 1 - t;
        return new Point(
            u * u * u * a.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * b.X,
            u * u * u * a.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * b.Y);
    }

    private IEnumerable<(Step From, StepPort Port, Step To)> Edges()
    {
        if (_vm is null)
            yield break;
        var steps = _vm.Steps;
        var byId = new Dictionary<Guid, Step>(steps.Count);
        foreach (var vm in steps)
            byId[vm.Step.Id] = vm.Step;
        foreach (var vm in steps)
        {
            foreach (var port in ScenarioGraph.PortsOf(vm.Step))
            {
                if (ScenarioGraph.GetLink(vm.Step, port) is { } id && byId.TryGetValue(id, out var to))
                    yield return (vm.Step, port, to);
            }
        }
    }

    private static IBrush PortBrush(Step step, StepPort port) => port switch
    {
        StepPort.False => NoBrush,
        StepPort.Error => ErrorBrush,
        _ => step.Type == StepType.Condition ? YesBrush : NextBrush,
    };

    // ---------- Hit test ----------

    private (Step Step, StepPort Port)? PortAt(Point world)
    {
        if (_vm is null)
            return null;
        var radius = PortHitRadius / Math.Min(_zoom, 1);
        foreach (var vm in _vm.Steps)
        {
            foreach (var port in ScenarioGraph.PortsOf(vm.Step))
            {
                var p = OutPort(vm.Step, port);
                if (Distance(p, world) <= radius)
                    return (vm.Step, port);
            }
        }

        return null;
    }

    private StepViewModel? NodeAt(Point world)
    {
        if (_vm is null)
            return null;
        // Listede sonra gelen üstte çizilir.
        for (var i = _vm.Steps.Count - 1; i >= 0; i--)
        {
            if (NodeRect(_vm.Steps[i].Step).Contains(world))
                return _vm.Steps[i];
        }

        return null;
    }

    private (Step Step, StepPort Port)? EdgeAt(Point world)
    {
        foreach (var (from, port, to) in Edges())
        {
            var a = OutPort(from, port);
            var b = InPort(to);
            var (c1, c2) = Controls(a, b, port);
            for (var i = 0; i <= EdgeSamples; i++)
            {
                if (Distance(Bezier(a, c1, c2, b, i / (double)EdgeSamples), world) <= EdgeHitDistance / _zoom)
                    return (from, port);
            }
        }

        return null;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ---------- Girdi ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Focus();
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        _dragStart = screen;
        _dragMoved = false;
        e.Pointer.Capture(this);
        e.Handled = true;

        if (_vm.IsIdle && PortAt(world) is { } port)
        {
            _drag = DragMode.Connect;
            _connectFrom = port;
            _connectEnd = world;
            _selectedEdge = null;
        }
        else if (NodeAt(world) is { } node)
        {
            _selectedEdge = null;
            _vm.SelectedStep = node;
            if (_vm.IsIdle)
            {
                _drag = DragMode.Node;
                _dragNode = node;
                _dragOffset = new Vector(world.X - (node.Step.X ?? 0), world.Y - (node.Step.Y ?? 0));
            }
        }
        else if (_vm.IsIdle && EdgeAt(world) is { } edge)
        {
            _selectedEdge = edge;
            _vm.SelectedStep = null;
        }
        else
        {
            _selectedEdge = null;
            _drag = DragMode.Pan;
        }

        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        switch (_drag)
        {
            case DragMode.Pan:
                _dragMoved = true;
                _pan += screen - _dragStart;
                _dragStart = screen;
                InvalidateVisual();
                break;
            case DragMode.Node when _dragNode is not null:
                _dragMoved = true;
                _dragNode.Step.X = world.X - _dragOffset.X;
                _dragNode.Step.Y = world.Y - _dragOffset.Y;
                InvalidateVisual();
                break;
            case DragMode.Connect:
                _connectEnd = world;
                InvalidateVisual();
                break;
            default:
                var hover = _vm is { IsIdle: true } ? PortAt(world) : null;
                if (hover != _hoverPort)
                {
                    _hoverPort = hover;
                    Cursor = hover is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
                    InvalidateVisual();
                }

                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var world = ToWorld(e.GetPosition(this));
        if (_drag == DragMode.Connect && _connectFrom is { } from && _vm is not null && NodeAt(world) is { } target)
            _vm.Connect(from.Step, from.Port, target.Step);
        else if (_drag == DragMode.Node && _dragMoved)
            _vm?.MarkChanged();
        else if (_drag == DragMode.Pan && !_dragMoved && _vm is not null)
            _vm.SelectedStep = null; // Boş alana tıklama seçimi kaldırır, yan panel kapanır ve alan genişler.

        _drag = DragMode.None;
        _dragNode = null;
        _connectFrom = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            ZoomAround(e.GetPosition(this), _zoom * Math.Pow(1.1, e.Delta.Y));
        }
        else
        {
            _pan += new Vector(e.Delta.X * 50, e.Delta.Y * 50);
        }

        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.D && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0
            && _vm is { IsIdle: true, SelectedStep: { } original })
        {
            _vm.DuplicateStep(original);
            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Delete or Key.Back) || _vm is not { IsIdle: true })
            return;
        if (_selectedEdge is { } edge)
        {
            _vm.Disconnect(edge.Step, edge.Port);
            _selectedEdge = null;
            e.Handled = true;
        }
        else if (_vm.SelectedStep is { } selected)
        {
            _vm.DeleteStep(selected);
            e.Handled = true;
        }
    }

    // ---------- Çizim ----------

    public override void Render(DrawingContext context)
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var background = new ImmutableSolidColorBrush(dark ? Color.Parse("#1E1F22") : Color.Parse("#F3F4F6"));
        var card = new ImmutableSolidColorBrush(dark ? Color.Parse("#2B2D31") : Colors.White);
        var text = new ImmutableSolidColorBrush(dark ? Color.Parse("#EDEDED") : Color.Parse("#1F2328"));
        var muted = new ImmutableSolidColorBrush(dark ? Color.Parse("#A0A4AA") : Color.Parse("#6B7078"));
        var border = new Pen(new ImmutableSolidColorBrush(dark ? Color.Parse("#44474D") : Color.Parse("#D5D8DD")), 1);
        context.FillRectangle(background, new Rect(Bounds.Size));
        if (_vm is null)
            return;

        _loopStart = _vm.LoopStartStep;
        using (context.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y)))
        {
            var view = new Rect(ToWorld(default), ToWorld(new Point(Bounds.Width, Bounds.Height))).Inflate(H);

            foreach (var (from, port, to) in Edges())
                DrawEdge(context, from, port, InPort(to), _selectedEdge == (from, port), text);

            if (_connectFrom is { } connect)
                DrawEdge(context, connect.Step, connect.Port, _connectEnd, true, text);

            foreach (var vm in _vm.Steps)
            {
                if (NodeRect(vm.Step).Intersects(view))
                    DrawNode(context, vm, card, text, muted, border);
            }
        }
    }

    private static void DrawEdge(DrawingContext context, Step from, StepPort port, Point end, bool selected, IBrush labelBrush)
    {
        var a = OutPort(from, port);
        var (c1, c2) = Controls(a, end, port);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(a, false);
            g.CubicBezierTo(c1, c2, end);
            g.EndFigure(false);
        }

        var brush = selected ? SelectBrush : PortBrush(from, port);
        context.DrawGeometry(null, new Pen(brush, selected ? 3 : 2), geometry);

        // Geliş yönünü gösteren küçük ok.
        var arrow = new StreamGeometry();
        using (var g = arrow.Open())
        {
            g.BeginFigure(end, true);
            g.LineTo(new Point(end.X - 5, end.Y - 8));
            g.LineTo(new Point(end.X + 5, end.Y - 8));
            g.EndFigure(true);
        }

        context.DrawGeometry(brush, null, arrow);
    }

    private void DrawNode(DrawingContext context, StepViewModel vm, IBrush card, IBrush text, IBrush muted, Pen border)
    {
        var step = vm.Step;
        var rect = NodeRect(step);
        var selected = _vm!.SelectedStep == vm;
        using var dim = step.Enabled ? default(DrawingContext.PushedState?) : context.PushOpacity(0.5);

        context.DrawRectangle(card, selected ? new Pen(SelectBrush, 2.5) : border, rect, 8, 8);
        var fill = vm.State switch
        {
            StepRunState.Running => RunningFill,
            StepRunState.Ok => OkFill,
            StepRunState.No => NoFill,
            StepRunState.Failed => FailedFill,
            _ => null,
        };
        if (fill is not null)
            context.DrawRectangle(fill, null, rect, 8, 8);

        // Sol kenarda işlem rengi.
        context.DrawRectangle(vm.ActionBrush, null, new Rect(rect.X, rect.Y + 8, 5, rect.Height - 16), 2, 2);

        var titleText = $"{vm.Number}. {vm.ActionText}";
        var tags = "";
        if (_vm.IsStart(step))
            tags += " ▶";
        if (_loopStart == step)
            tags += " ↻";
        DrawText(context, titleText + tags, Bold, 13, text, new Point(rect.X + 14, rect.Y + 7), W - 24);

        var target = vm.TargetName;
        var detail = vm.FlowDetail;
        var y = rect.Y + 27;
        if (!string.IsNullOrEmpty(target))
        {
            DrawText(context, target, Regular, 12, text, new Point(rect.X + 14, y), W - 24);
            y += 18;
        }

        if (!string.IsNullOrEmpty(detail))
            DrawText(context, detail, Regular, 11, muted, new Point(rect.X + 14, y), W - 24);

        // Giriş noktası.
        context.DrawEllipse(card, new Pen(muted, 1.5), InPort(step), 4.5, 4.5);

        foreach (var port in ScenarioGraph.PortsOf(step))
        {
            var p = OutPort(step, port);
            var hovering = _hoverPort == (step, port);
            context.DrawEllipse(PortBrush(step, port), new Pen(card, 1.5), p, hovering ? PortRadius + 2 : PortRadius, hovering ? PortRadius + 2 : PortRadius);
            var label = ScenarioGraph.PortLabel(step, port);
            if (label.Length > 0)
            {
                var at = port == StepPort.Error
                    ? new Point(p.X + 10, p.Y - 16)
                    : new Point(p.X + 10, p.Y + 3);
                DrawText(context, label, Bold, 11, PortBrush(step, port), at, 60);
            }
        }
    }

    private static void DrawText(DrawingContext context, string value, Typeface typeface, double size, IBrush brush, Point at, double maxWidth)
    {
        var formatted = new FormattedText(value, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(formatted, at);
    }
}
