using Avalonia.Controls;

namespace WebAutomater.Gui.Views;

/// <summary>Otomatik gruplamada taşınacak dosyaları gösterir; "Taşı"ya basılırsa true ile kapanır.</summary>
public partial class GroupingPreviewWindow : Window
{
    public GroupingPreviewWindow()
    {
        InitializeComponent();
        ApplyButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
