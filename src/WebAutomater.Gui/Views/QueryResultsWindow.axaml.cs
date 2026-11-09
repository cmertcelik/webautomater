using Avalonia.Controls;
using Avalonia.Media;
using WebAutomater.Gui.ViewModels;

namespace WebAutomater.Gui.Views;

/// <summary>Sorgu sonuçları; kapanınca bekleyen sorgular iptal edilir.</summary>
public partial class QueryResultsWindow : Window
{
    public QueryResultsWindow()
    {
        InitializeComponent();
        CopyButton.Click += async (_, _) =>
        {
            if (ViewModel?.SelectedResult is { Body.Length: > 0 } row && Clipboard is { } clipboard)
                await clipboard.SetTextAsync(row.Body);
        };
        Closed += (_, _) => ViewModel?.Cancel();
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is not { } vm)
                return;
            ApplyWrap(vm.WrapLines);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(QueryRunViewModel.WrapLines))
                    ApplyWrap(vm.WrapLines);
            };
        };
    }

    private QueryRunViewModel? ViewModel => DataContext as QueryRunViewModel;

    private void ApplyWrap(bool wrap) => BodyBox.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
}
