using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WebAutomater.Gui.ViewModels;

namespace WebAutomater.Gui.Views;

public partial class DocumentsView : UserControl
{
    private const double ZoomStep = 1.2;

    private const double DragThreshold = 6;

    private DocumentsViewModel? _highlightSource;

    // Sürükle-bırak: basılan satır, sürüklenen öğe, üzerinde durulan klasör.
    private FileNode? _pressedNode;
    private Point _pressPoint;
    private FileNode? _draggedNode;
    private FileNode? _dropHighlight;
    private FileNode? _hoverFolder;
    private readonly DispatcherTimer _hoverExpandTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public DocumentsView()
    {
        InitializeComponent();

        Pages.CurrentPageChanged += page => PageBox.Text = page > 0 ? page.ToString() : "";
        Pages.PropertyChanged += (_, e) =>
        {
            if (e.Property == PdfPagesView.DocumentProperty)
                PageTotal.Text = Pages.Document is { } doc ? $"/ {doc.PageCount}" : "";
        };

        PrevPage.Click += (_, _) => Pages.ScrollToPage(Pages.CurrentPage - 1);
        NextPage.Click += (_, _) => Pages.ScrollToPage(Pages.CurrentPage + 1);
        ZoomIn.Click += (_, _) => Pages.ZoomBy(ZoomStep);
        ZoomOut.Click += (_, _) => Pages.ZoomBy(1 / ZoomStep);
        PageBox.KeyDown += OnPageBoxKeyDown;

        Identifiers.KeyDown += OnIdentifiersKeyDown;
        Identifiers.DoubleTapped += OnIdentifierDoubleTapped;

        Tree.KeyDown += OnTreeKeyDown;
        Tree.DoubleTapped += OnTreeDoubleTapped;

        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tree.AddHandler(PointerReleasedEvent, (_, _) => _pressedNode = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tree.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);
        Tree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHighlight(null, rootTarget: false, hint: null, valid: true));
        Tree.AddHandler(DragDrop.DropEvent, OnTreeDrop);
        _hoverExpandTimer.Tick += OnHoverExpandTick;

        if (NewFolderButton.Flyout is { } newFolderFlyout)
        {
            newFolderFlyout.Opened += (_, _) =>
            {
                ViewModel?.PrepareNewFolder();
                Dispatcher.UIThread.Post(() =>
                {
                    NewFolderBox.Focus();
                    NewFolderBox.SelectAll();
                }, DispatcherPriority.Background);
            };
        }

        CreateFolderButton.Click += async (_, _) => await CreateFolderAsync();
        NewFolderBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await CreateFolderAsync();
            }
        };
        ResetFieldsConfirm.Click += (_, _) => ResetFieldsButton.Flyout?.Hide();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_highlightSource is not null)
            _highlightSource.HighlightsChanged -= Pages.InvalidateVisual;
        _highlightSource = ViewModel;
        if (_highlightSource is not null)
            _highlightSource.HighlightsChanged += Pages.InvalidateVisual;
    }

    private DocumentsViewModel? ViewModel => DataContext as DocumentsViewModel;

    private void OnPageBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (int.TryParse(PageBox.Text, out var page))
            Pages.ScrollToPage(page);
        else
            PageBox.Text = Pages.CurrentPage.ToString();
        e.Handled = true;
    }

    private async void OnIdentifiersKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || ViewModel is not { } vm)
            return;
        e.Handled = true;
        // Listedeki sıra korunsun; seçim sırası değil.
        var selected = vm.VisibleRows.Where(r => Identifiers.SelectedItems?.Contains(r) == true).ToList();
        await vm.CopyRowsAsync(selected);
    }

    private async void OnIdentifierDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Identifiers.SelectedItem is not IdentifierRow row || ViewModel is not { } vm)
            return;
        var alreadyOpen = vm.Document is { } doc && FileOps.SamePath(doc.Path, row.FirstFile);
        if (!await vm.ShowFileAsync(row.FirstFile))
            return;
        if (!alreadyOpen)
            // Yeni belgenin sayfa düzeni bir sonraki yerleşimde hesaplanır; kaydırmayı ondan sonra yap.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Pages.ScrollToPage(row.FirstPage);
    }

    /// <summary>Tarih alanında takvimden gün seçilince açılır pencereyi kapatır.</summary>
    private void OnFieldCalendarDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not Calendar calendar || calendar.FindLogicalAncestorOfType<Popup>() is not { } popup)
            return;
        if (popup.PlacementTarget is Button { Flyout: { } flyout })
            flyout.Hide();
        else
            popup.IsOpen = false;
    }

    private async Task CreateFolderAsync()
    {
        if (ViewModel is { } vm && await vm.CreateFolderAsync())
            NewFolderButton.Flyout?.Hide();
    }

    // ---------- Sürükleyerek taşıma ----------

    private static FileNode? NodeAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as FileNode;

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedNode = null;
        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed)
            return;
        // Oka basmak sürükleme başlatmasın.
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        _pressedNode = NodeAt(e.Source);
        _pressPoint = e.GetPosition(Tree);
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedNode is not { } node || !e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed)
            return;
        var delta = e.GetPosition(Tree) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;

        _pressedNode = null;
        _draggedNode = node;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(node.FullPath));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            _draggedNode = null;
            SetDropHighlight(null, rootTarget: false, hint: null, valid: true);
            StopHoverExpand();
        }
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_draggedNode is not { } dragged || ViewModel is not { } vm)
        {
            // Yalnızca ağacın kendi öğeleri taşınabilir.
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var target = NodeAt(e.Source);
        var folder = vm.DropFolderFor(target);
        var ok = vm.CanMove(dragged, folder);
        e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;

        // Hedef klasörün satırını vurgula (dosyanın üzerindeyse onun klasörünü).
        var folderNode = target is { IsDirectory: true } ? target
            : vm.Nodes.FirstOrDefault(n => n.IsDirectory && FileOps.SamePath(n.FullPath, folder));
        var folderName = folder is null ? "" : FileOps.SamePath(folder, vm.RootFolder)
            ? $"ana klasör ({Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))})"
            : $"\"{Path.GetFileName(folder)}\"";
        var hint = ok
            ? $"→ {folderName} klasörüne taşınacak"
            : ReferenceEquals(target, dragged) ? "Bırakmak için bir klasörün üzerine gelin"
            : dragged.IsDirectory && folder is not null && FileOps.IsInside(folder, dragged.FullPath) ? "Klasör kendi içine taşınamaz"
            : $"Zaten {folderName} klasöründe";
        SetDropHighlight(ok ? folderNode : null, rootTarget: ok && folderNode is null, hint, ok);

        // Kapalı bir klasörün üzerinde biraz beklenirse açılır.
        if (target is { IsDirectory: true, IsExpanded: false, CanExpand: true } && !ReferenceEquals(target, dragged))
        {
            if (!ReferenceEquals(_hoverFolder, target))
            {
                _hoverFolder = target;
                _hoverExpandTimer.Stop();
                _hoverExpandTimer.Start();
            }
        }
        else
        {
            StopHoverExpand();
        }
    }

    private async void OnHoverExpandTick(object? sender, EventArgs e)
    {
        var folder = _hoverFolder;
        StopHoverExpand();
        if (folder is not null && _draggedNode is not null && ViewModel is { } vm)
            await vm.ExpandAsync(folder);
    }

    private void StopHoverExpand()
    {
        _hoverExpandTimer.Stop();
        _hoverFolder = null;
    }

    private async void OnTreeDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var dragged = _draggedNode;
        SetDropHighlight(null, rootTarget: false, hint: null, valid: true);
        StopHoverExpand();
        if (dragged is null || ViewModel is not { } vm)
            return;
        var folder = vm.DropFolderFor(NodeAt(e.Source));
        if (folder is not null && vm.CanMove(dragged, folder))
        {
            e.DragEffects = DragDropEffects.Move;
            await vm.MoveAsync(dragged, folder);
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void SetDropHighlight(FileNode? node, bool rootTarget, string? hint, bool valid)
    {
        DropHint.Text = hint;
        DropHint.IsVisible = hint is not null;
        DropHint.Classes.Set("invalid", !valid);

        if (!ReferenceEquals(_dropHighlight, node))
        {
            if (_dropHighlight is not null)
                _dropHighlight.IsDropTarget = false;
            _dropHighlight = node;
            if (node is not null)
                node.IsDropTarget = true;
        }

        TreeFrame.Classes.Set("droproot", rootTarget);
    }

    private async void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { SelectedNode: { IsDirectory: true } node } vm)
            return;
        if (e.Key == Key.Right && !node.IsExpanded)
        {
            e.Handled = true;
            await vm.ExpandAsync(node);
        }
        else if (e.Key == Key.Left && node.IsExpanded)
        {
            e.Handled = true;
            vm.Collapse(node);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await vm.ToggleNodeCommand.ExecuteAsync(node);
        }
    }

    private async void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Oka çift tıklama zaten iki kez açıp kapatır; satıra çift tıklama açar/kapatır.
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (ViewModel is { SelectedNode: { IsDirectory: true } node } vm)
            await vm.ToggleNodeCommand.ExecuteAsync(node);
    }
}
