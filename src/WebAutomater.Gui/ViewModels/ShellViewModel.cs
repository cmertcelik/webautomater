using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WebAutomater.Gui.ViewModels;

/// <summary>Ana pencere: sekmeleri ve her sekmenin kendi görünüm modelini tutar.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel()
    {
        Automation.PropertyChanged += OnTabPropertyChanged;
        Documents.PropertyChanged += OnTabPropertyChanged;
    }

    public MainWindowViewModel Automation { get; } = new();

    public DocumentsViewModel Documents { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutomationTab), nameof(IsDocumentsTab), nameof(WindowTitle))]
    private int _selectedTabIndex;

    public bool IsAutomationTab => SelectedTabIndex == 0;

    public bool IsDocumentsTab => SelectedTabIndex == 1;

    public string WindowTitle => IsDocumentsTab ? Documents.WindowTitle : Automation.WindowTitle;

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WindowTitle))
            OnPropertyChanged(nameof(WindowTitle));
    }

    public void Shutdown()
    {
        Automation.Shutdown();
        Documents.Shutdown();
    }
}
