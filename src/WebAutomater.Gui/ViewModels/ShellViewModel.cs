using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WebAutomater.Gui.ViewModels;

/// <summary>Ana pencere: sekmeleri ve her sekmenin kendi görünüm modelini tutar.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel()
    {
        Automation.PropertyChanged += OnTabPropertyChanged;
        Documents.PropertyChanged += OnTabPropertyChanged;
        AppErrors.Raised += ShowNotice;
    }

    // ---------- Hata bildirimleri ----------

    private const int MaxNotices = 3;
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(12);

    /// <summary>Sağ alt köşedeki kısa hata bildirimleri; birkaç saniye sonra kendiliğinden kapanır.</summary>
    public ObservableCollection<ErrorNotice> Notices { get; } = [];

    public bool HasNotices => Notices.Count > 0;

    private void ShowNotice(ErrorNotice notice)
    {
        // Aynı hata art arda yağıyorsa ekranı doldurmasın.
        if (Notices.Any(n => n.Message == notice.Message))
            return;
        Notices.Add(notice);
        while (Notices.Count > MaxNotices)
            Notices.RemoveAt(0);
        OnPropertyChanged(nameof(HasNotices));
        DispatcherTimer.RunOnce(() => DismissNotice(notice), NoticeLifetime);
    }

    [RelayCommand]
    private void DismissNotice(ErrorNotice? notice)
    {
        if (notice is not null && Notices.Remove(notice))
            OnPropertyChanged(nameof(HasNotices));
    }

    [RelayCommand]
    private void OpenLogFolder() => AppErrors.OpenLogFolder();

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
