using Avalonia.Controls;
using WebAutomater.Gui.ViewModels;

namespace WebAutomater.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel vm)
            {
                vm.Automation.Host = this;
                vm.Documents.Host = this;
            }
        };
        Closing += (_, _) => (DataContext as ShellViewModel)?.Shutdown();
    }
}
