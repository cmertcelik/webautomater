using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WebAutomater.Gui.ViewModels;

namespace WebAutomater.Gui.Views;

public partial class DocumentsView : UserControl
{
    private const double ZoomStep = 1.2;

    private DocumentsViewModel? _highlightSource;

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

        DateCalendar.SelectedDatesChanged += (_, _) => DateButton.Flyout?.Hide();
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

    private void OnIdentifierDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Identifiers.SelectedItem is IdentifierRow row)
            Pages.ScrollToPage(row.FirstPage);
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
