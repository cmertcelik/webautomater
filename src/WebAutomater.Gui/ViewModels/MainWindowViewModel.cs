using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAutomater.Core.Data;
using WebAutomater.Core.Models;
using WebAutomater.Core.Playback;
using WebAutomater.Core.Recording;
using WebAutomater.Core.Storage;
using WebAutomater.Core.Text;

namespace WebAutomater.Gui.ViewModels;

public sealed record ScenarioFile(string Name, string Path, DateTime Modified)
{
    public string ModifiedText => Modified.ToString("g");
}

public sealed record BrowserChoice(string Label, string Engine, string? Channel);

public sealed record SpeedChoice(string Label, double Factor, bool Fixed = false);

public sealed partial class MainWindowViewModel : ObservableObject
{
    public const string NoColumn = "— kayıtlı değer —";
    private const int MaxLogLines = 500;

    private readonly DispatcherTimer _saveTimer;
    private Recorder? _recorder;
    private CancellationTokenSource? _playCts;
    private bool _loading;

    public MainWindowViewModel()
    {
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveNowAsync();
        };
        _selectedBrowser = Browsers[0];
        _selectedSpeed = Speeds[0];
        RefreshLibrary();
        LoadScenario(new Scenario(), null);
    }

    /// <summary>Dosya diyalogları ve klasör açma için pencere tarafından atanır.</summary>
    public TopLevel? Host { get; set; }

    // ---------- Senaryo ----------

    public Scenario Scenario { get; private set; } = new();

    public ObservableCollection<StepViewModel> Steps { get; } = [];

    public ObservableCollection<ScenarioFile> Library { get; } = [];

    public ObservableCollection<string> ColumnChoices { get; } = [NoColumn];

    public ObservableCollection<string> Log { get; } = [];

    public BrowserChoice[] Browsers { get; } =
    [
        new("Chromium (dahili)", "chromium", null),
        new("Google Chrome (yüklü)", "chromium", "chrome"),
        new("Microsoft Edge (yüklü)", "chromium", "msedge"),
        new("Firefox", "firefox", null),
        new("WebKit (Safari motoru)", "webkit", null),
    ];

    public SpeedChoice[] Speeds { get; } =
    [
        new("Kaydedildiği hızda", 1.0),
        new("2 kat hızlı", 0.5),
        new("4 kat hızlı", 0.25),
        new("Beklemeden (en hızlı)", 0),
        new("Sabit bekleme", 0, Fixed: true),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(FileText))]
    private string? _currentPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _scenarioName = "";

    [ObservableProperty]
    private BrowserChoice _selectedBrowser;

    [ObservableProperty]
    private ScenarioFile? _selectedLibraryItem;

    [ObservableProperty]
    private StepViewModel? _selectedStep;

    [ObservableProperty]
    private string _statusText = "Hazır. Bir adres yazıp \"Kaydı başlat\"a basın.";

    public string WindowTitle => $"WebAutomater — {(string.IsNullOrWhiteSpace(ScenarioName) ? "Yeni senaryo" : ScenarioName)}";

    public string FileText => CurrentPath ?? "Henüz kaydedilmedi";

    public bool HasSteps => Steps.Count > 0;

    // ---------- Kayıt ----------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand))]
    private string _recordUrl = "https://";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand), nameof(StopRecordingCommand), nameof(PlayCommand))]
    private bool _isRecording;

    // ---------- Oynatma ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseDirect))]
    private bool _useDataSource;

    [ObservableProperty]
    private decimal? _repeatCount = 1;

    [ObservableProperty]
    private decimal? _dataPasses = 1;

    [ObservableProperty]
    private decimal? _firstRow = 1;

    [ObservableProperty]
    private decimal? _lastRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixedDelay))]
    private SpeedChoice _selectedSpeed;

    [ObservableProperty]
    private decimal? _fixedDelaySeconds = 1;

    [ObservableProperty]
    private bool _limitLongDelays = true;

    [ObservableProperty]
    private decimal? _actionTimeoutSeconds = 15;

    [ObservableProperty]
    private decimal? _retryCount = 0;

    [ObservableProperty]
    private bool _headless;

    [ObservableProperty]
    private bool _continueOnError = true;

    [ObservableProperty]
    private bool _freshSession = true;

    [ObservableProperty]
    private bool _keepPageOpen;

    /// <summary>Orta alanda akış (düğümler) mı liste mi gösterilsin.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    private bool _showFlow = true;

    public bool ShowList
    {
        get => !ShowFlow;
        set => ShowFlow = !value;
    }

    [ObservableProperty]
    private bool _isLibraryOpen = true;

    [ObservableProperty]
    private bool _isPlaybackOpen = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogToggleText))]
    private bool _isLogOpen = true;

    public string LogToggleText => IsLogOpen ? "▾ Günlük" : "▸ Günlük";

    [RelayCommand]
    private void ToggleLibrary() => IsLibraryOpen = !IsLibraryOpen;

    [RelayCommand]
    private void TogglePlayback() => IsPlaybackOpen = !IsPlaybackOpen;

    [RelayCommand]
    private void ToggleLog() => IsLogOpen = !IsLogOpen;

    /// <summary>Kayıt sırasında kontrol modu açık mı. Tarayıcıdaki çubukla da değişebilir.</summary>
    [ObservableProperty]
    private bool _isCheckMode;

    /// <summary>Düğümler ya da bağlantılar değişti, akış ekranı yeniden çizilmeli.</summary>
    public event Action? FlowChanged;

    /// <summary>Yeni senaryo yüklendi, akış ekranı görünümünü sıfırlamalı.</summary>
    public event Action? ScenarioLoaded;

    public void NotifyFlowChanged() => FlowChanged?.Invoke();

    [ObservableProperty]
    private bool _typeSlowly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartRecordingCommand), nameof(PlayCommand), nameof(StopPlaybackCommand))]
    private bool _isPlaying;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private double _progressMax = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveReportCommand), nameof(OpenErrorFolderCommand))]
    private PlaybackResult? _lastResult;

    private string? _lastScreenshotDir;

    public bool UseDirect
    {
        get => !UseDataSource;
        set => UseDataSource = !value;
    }

    public bool IsFixedDelay => SelectedSpeed.Fixed;

    public bool IsBusy => IsRecording || IsPlaying;

    public bool IsIdle => !IsBusy;

    // ---------- Veri kaynağı ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataInfo), nameof(HasData), nameof(HasMultipleSheets))]
    private DataTableSource? _data;

    [ObservableProperty]
    private string? _selectedSheet;

    public ObservableCollection<string> Sheets { get; } = [];

    public bool HasData => Data is not null;

    public bool HasMultipleSheets => Sheets.Count > 1;

    public string DataInfo => Data is null
        ? "Veri kaynağı seçilmedi. Excel (.xlsx), CSV veya JSON dosyası seçin."
        : $"{System.IO.Path.GetFileName(Data.Path)}: {Data.Rows.Count} satır\nKolonlar: {string.Join(", ", Data.Columns)}";

    public string? SampleFor(string column) =>
        Data is { Rows.Count: > 0 } && Data.Rows[0].TryGetValue(column, out var v) ? $"örn: {v}" : null;

    // =====================================================================

    partial void OnScenarioNameChanged(string value)
    {
        if (_loading)
            return;
        Scenario.Name = value;
        MarkChanged();
    }

    partial void OnSelectedBrowserChanged(BrowserChoice value)
    {
        if (_loading)
            return;
        Scenario.Browser.Engine = value.Engine;
        Scenario.Browser.Channel = value.Channel;
        MarkChanged();
    }

    partial void OnSelectedLibraryItemChanged(ScenarioFile? value)
    {
        if (value is not null && !IsBusy && value.Path != CurrentPath)
            _ = OpenPathAsync(value.Path);
    }

    partial void OnSelectedSheetChanged(string? value)
    {
        if (_loading || Data is null || value is null || value == Data.Sheet)
            return;
        LoadData(Data.Path, value);
    }

    public void MarkChanged()
    {
        NotifyFlowChanged();
        if (_loading)
            return;
        if (CurrentPath is not null)
        {
            // Teknik olmayan kullanıcı "kaydetmeyi unuttum" yaşamasın diye değişiklikler otomatik kaydedilir.
            _saveTimer.Stop();
            _saveTimer.Start();
        }
    }

    private void LoadScenario(Scenario scenario, string? path)
    {
        _loading = true;
        try
        {
            ScenarioGraph.EnsureGraph(scenario);
            ScenarioGraph.EnsurePositions(scenario);
            Scenario = scenario;
            CurrentPath = path;
            ScenarioName = scenario.Name;
            SelectedBrowser = Browsers.FirstOrDefault(b => b.Engine == scenario.Browser.Engine && b.Channel == scenario.Browser.Channel)
                              ?? Browsers[0];
            RecordUrl = scenario.StartUrl ?? RecordUrl;
            Steps.Clear();
            foreach (var step in scenario.Steps)
                Steps.Add(new StepViewModel(step, this));
            Renumber();
            LastResult = null;
        }
        finally
        {
            _loading = false;
        }

        ScenarioLoaded?.Invoke();

        if (scenario.DataSourcePath is { } dataPath && File.Exists(dataPath))
        {
            LoadData(dataPath, scenario.DataSourceSheet);
            UseDataSource = scenario.BoundParameters.Any();
        }
        else
        {
            RebuildColumnChoices();
        }
    }

    private void Renumber()
    {
        for (var i = 0; i < Steps.Count; i++)
            Steps[i].Number = i + 1;
        OnPropertyChanged(nameof(HasSteps));
        NotifyFlowChanged();
    }

    public void RefreshLibrary()
    {
        Library.Clear();
        var dir = ScenarioStore.DefaultDirectory;
        if (!Directory.Exists(dir))
            return;
        foreach (var file in new DirectoryInfo(dir).GetFiles("*" + ScenarioStore.FileExtension).OrderByDescending(f => f.LastWriteTime))
            Library.Add(new ScenarioFile(file.Name[..^ScenarioStore.FileExtension.Length], file.FullName, file.LastWriteTime));
    }

    private async Task OpenPathAsync(string path)
    {
        try
        {
            await FlushSaveAsync();
            var scenario = await ScenarioStore.LoadAsync(path);
            LoadScenario(scenario, path);
            StatusText = $"Açıldı: {scenario.Name} ({scenario.Steps.Count} adım)";
        }
        catch (Exception ex)
        {
            AddLog($"Senaryo açılamadı: {ex.Message}");
        }
    }

    private async Task SaveNowAsync()
    {
        if (CurrentPath is null)
            return;
        try
        {
            await ScenarioStore.SaveAsync(Scenario, CurrentPath);
        }
        catch (Exception ex)
        {
            AddLog($"Kaydedilemedi: {ex.Message}");
        }
    }

    private async Task FlushSaveAsync()
    {
        if (!_saveTimer.IsEnabled)
            return;
        _saveTimer.Stop();
        await SaveNowAsync();
    }

    public void AddLog(string message)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > MaxLogLines)
            Log.RemoveAt(0);
    }

    // ---------- Dosya komutları ----------

    [RelayCommand]
    private async Task NewScenarioAsync()
    {
        await FlushSaveAsync();
        LoadScenario(new Scenario(), null);
        SelectedLibraryItem = null;
        StatusText = "Yeni senaryo. Adres yazıp \"Kaydı başlat\"a basın.";
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (Host is null)
            return;
        var files = await Host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Senaryo aç",
            AllowMultiple = false,
            SuggestedStartLocation = await Host.StorageProvider.TryGetFolderFromPathAsync(EnsureLibraryDirectory()),
            FileTypeFilter = [new FilePickerFileType("WebAutomater senaryosu") { Patterns = ["*.wa.json", "*.json"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (Host is null)
            return;
        var file = await Host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Senaryoyu farklı kaydet",
            SuggestedFileName = ScenarioStore.SuggestFileName(Scenario.Name),
            SuggestedStartLocation = await Host.StorageProvider.TryGetFolderFromPathAsync(EnsureLibraryDirectory()),
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("WebAutomater senaryosu") { Patterns = ["*.wa.json"] }],
        });
        if (file?.TryGetLocalPath() is not { } path)
            return;
        CurrentPath = path;
        await SaveNowAsync();
        RefreshLibrary();
        StatusText = $"Kaydedildi: {path}";
    }

    [RelayCommand]
    private async Task DeleteScenarioAsync(ScenarioFile? item)
    {
        if (item is null || IsBusy)
            return;
        try
        {
            File.Delete(item.Path);
        }
        catch (Exception ex)
        {
            AddLog($"Silinemedi: {ex.Message}");
            return;
        }
        if (item.Path == CurrentPath)
        {
            _saveTimer.Stop();
            await NewScenarioAsync();
        }
        RefreshLibrary();
    }

    private static string EnsureLibraryDirectory()
    {
        Directory.CreateDirectory(ScenarioStore.DefaultDirectory);
        return ScenarioStore.DefaultDirectory;
    }

    // ---------- Kayıt komutları ----------

    private bool CanStartRecording() => !IsBusy && !string.IsNullOrWhiteSpace(RecordUrl) && RecordUrl.Trim() != "https://";

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private async Task StartRecordingAsync()
    {
        await FlushSaveAsync();
        var url = Recorder.NormalizeUrl(RecordUrl);
        var name = Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : "Yeni senaryo";
        var scenario = new Scenario
        {
            Name = name,
            Browser = new BrowserSettings { Engine = SelectedBrowser.Engine, Channel = SelectedBrowser.Channel },
        };
        LoadScenario(scenario, null);
        SelectedLibraryItem = null;

        var recorder = new Recorder(scenario);
        AttachRecorder(recorder);
        IsRecording = true;
        StatusText = "Kayıt yapılıyor... Tarayıcıda işlemlerinizi yapın, bitince \"Kaydı durdur\"a basın ya da tarayıcıyı kapatın.";
        AddLog($"Kayıt başladı: {url}");

        try
        {
            await recorder.StartAsync(url);
            await recorder.Completion;
        }
        catch (Exception ex)
        {
            AddLog($"Kayıt başlatılamadı: {ex.Message}");
            StatusText = "Kayıt başlatılamadı. Ayrıntılar aşağıdaki günlükte.";
        }
        finally
        {
            await recorder.DisposeAsync();
            _recorder = null;
            IsRecording = false;
            IsCheckMode = false;
        }

        if (scenario.Steps.Count <= 1)
        {
            StatusText = "Kayıtta işlem yakalanmadı.";
            return;
        }

        // Kayıt bitince kitaplığa otomatik kaydedilir.
        var path = ScenarioStore.UniquePath(EnsureLibraryDirectory(), scenario.Name);
        CurrentPath = path;
        await SaveNowAsync();
        RefreshLibrary();
        SelectedLibraryItem = Library.FirstOrDefault(f => f.Path == path);
        StatusText = $"Kayıt tamamlandı: {scenario.Steps.Count} adım. Adımları düzenleyebilir ya da sağdan oynatabilirsiniz.";
        AddLog($"Kaydedildi: {path}");
    }

    private void AttachRecorder(Recorder recorder)
    {
        recorder.Log = msg => Dispatcher.UIThread.Post(() => AddLog(msg));
        recorder.StepChanged += (_, e) => Dispatcher.UIThread.Post(() => ApplyRecorderChange(e));
        recorder.ModeChanged += mode => Dispatcher.UIThread.Post(() => IsCheckMode = mode == RecorderMode.Check);
        _recorder = recorder;
    }

    partial void OnIsCheckModeChanged(bool value)
    {
        if (_recorder is { } recorder)
            _ = recorder.SetModeAsync(value ? RecorderMode.Check : RecorderMode.Record);
        if (IsRecording)
        {
            StatusText = value
                ? "Kontrol modu: tarayıcıda kontrol edilecek öğeye tıklayın. Tıklama siteye ulaşmaz, işlem yapılmaz."
                : "Kayıt yapılıyor... Tarayıcıda işlemlerinizi yapın, bitince \"Kaydı durdur\"a basın ya da tarayıcıyı kapatın.";
        }
    }

    /// <summary>
    /// Bir düğümün boş çıkışından yeni dal kaydeder: tarayıcı açılır, başlangıçtan o düğüme kadar otomatik gidilir,
    /// sonra kullanıcı işlemleri o çıkışa bağlanır.
    /// </summary>
    public void StartBranchRecording(StepViewModel from, StepPort port) => _ = RecordBranchAsync(from.Step, port);

    private async Task RecordBranchAsync(Step from, StepPort port)
    {
        if (IsBusy)
            return;
        if (ScenarioGraph.PathTo(Scenario, from, port) is not { } path)
        {
            StatusText = "Bu düğüme başlangıçtan ulaşan bir yol yok. Önce bağlantıları kurun.";
            return;
        }

        await FlushSaveAsync();
        var recorder = new Recorder(Scenario, (from, port)) { CapturePaused = true };
        AttachRecorder(recorder);
        IsRecording = true;
        StatusText = "Tarayıcı açılıyor, bu noktaya kadar otomatik gidiliyor...";
        AddLog($"Dal kaydı: {StepDescriber.Describe(from)} düğümüne gidiliyor ({path.Count} adım).");
        try
        {
            await recorder.StartAsync("about:blank");
            var options = new PlaybackOptions
            {
                SpeedFactor = 0,
                ActionTimeoutMs = (int)((ActionTimeoutSeconds ?? 15) * 1000),
                TypeDelayMs = TypeSlowly ? 60 : 0,
            };
            var problem = await new Player().ReplayPathAsync(recorder.Pages[0], path, options,
                msg => Dispatcher.UIThread.Post(() => AddLog(msg)));
            recorder.CapturePaused = false;
            StatusText = problem is null
                ? "Bu noktaya gelindi. Dalın işlemlerini tarayıcıda yapın, bitince \"Kaydı durdur\"a basın."
                : "Otomatik gidiş yarıda kaldı. Kalanı tarayıcıda elle yapın; bu sırada yaptıklarınız da kaydedilir.";
            if (problem is not null)
                AddLog($"Otomatik gidiş durdu: {problem}");
            await recorder.Completion;
            StatusText = "Dal kaydı tamamlandı.";
        }
        catch (Exception ex)
        {
            AddLog($"Dal kaydı başlatılamadı: {ex.Message}");
            StatusText = "Dal kaydı başlatılamadı. Ayrıntılar aşağıdaki günlükte.";
        }
        finally
        {
            await recorder.DisposeAsync();
            _recorder = null;
            IsRecording = false;
            IsCheckMode = false;
        }

        MarkChanged();
        await FlushSaveAsync();
    }

    public void SetStart(StepViewModel vm)
    {
        if (IsBusy)
            return;
        Scenario.StartStepId = vm.Step.Id;
        MarkChanged();
    }

    public void SetLoopStart(StepViewModel vm)
    {
        if (IsBusy)
            return;
        Scenario.LoopStartStepId = vm.Step.Id;
        StatusText = "\"Sayfayı açık tut\" ile oynatırken sonraki tekrarlar bu düğümden başlar.";
        MarkChanged();
    }

    public bool IsStart(Step step) => Scenario.StartStepId == step.Id;

    /// <summary>Otomatik belirlenen de dahil, tekrarların başlayacağı düğüm.</summary>
    public Step? LoopStartStep => new ScenarioFlow(Scenario).LoopStart;

    public void Connect(Step from, StepPort port, Step to)
    {
        if (IsBusy || from == to)
            return;
        ScenarioGraph.SetLink(from, port, to.Id);
        RefreshStep(from);
        MarkChanged();
    }

    public void Disconnect(Step from, StepPort port)
    {
        if (IsBusy)
            return;
        ScenarioGraph.SetLink(from, port, null);
        RefreshStep(from);
        MarkChanged();
    }

    private void RefreshStep(Step step) => Steps.FirstOrDefault(s => s.Step == step)?.Refresh();

    [RelayCommand]
    private void ArrangeFlow()
    {
        ScenarioGraph.AutoLayout(Scenario);
        MarkChanged();
        ScenarioLoaded?.Invoke();
    }

    [RelayCommand]
    private void AddConditionStep()
    {
        if (IsBusy)
            return;
        InsertStepAfter(SelectedStep, new Step { Type = StepType.Condition, Condition = ConditionKind.PageHasText, Value = "" });
        StatusText = "Sağdaki panelden aranacak yazıyı girin. Öğe kontrolü için kayıt sırasında kontrol modunu kullanın.";
    }

    [RelayCommand]
    private void AddEndStep()
    {
        if (IsBusy)
            return;
        InsertStepAfter(SelectedStep, new Step { Type = StepType.End });
    }

    private bool CanStopRecording() => IsRecording;

    [RelayCommand(CanExecute = nameof(CanStopRecording))]
    private async Task StopRecordingAsync()
    {
        if (_recorder is not null)
            await _recorder.StopAsync();
    }

    private void ApplyRecorderChange(StepChangedEventArgs e)
    {
        switch (e.Kind)
        {
            case StepChangeKind.Added:
                var vm = new StepViewModel(e.Step, this);
                Steps.Insert(Math.Min(e.Index, Steps.Count), vm);
                ScenarioGraph.EnsurePositions(Scenario);
                SelectedStep = vm;
                break;
            case StepChangeKind.Updated:
                Steps.FirstOrDefault(s => s.Step == e.Step)?.Refresh();
                break;
            case StepChangeKind.Removed:
                if (Steps.FirstOrDefault(s => s.Step == e.Step) is { } removed)
                    Steps.Remove(removed);
                break;
        }
        Renumber();
    }

    // ---------- Adım düzenleme ----------

    public void MoveStep(StepViewModel vm, int delta)
    {
        if (IsBusy)
            return;
        var index = Steps.IndexOf(vm);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Steps.Count)
            return;
        Steps.Move(index, target);
        Scenario.Steps.RemoveAt(index);
        Scenario.Steps.Insert(target, vm.Step);
        Renumber();
        MarkChanged();
    }

    public void DeleteStep(StepViewModel vm)
    {
        if (IsBusy)
            return;
        Steps.Remove(vm);
        ScenarioGraph.Remove(Scenario, vm.Step);
        if (SelectedStep == vm)
            SelectedStep = null;
        foreach (var other in Steps)
            other.Refresh();
        Renumber();
        MarkChanged();
    }

    public void DuplicateStep(StepViewModel vm)
    {
        if (IsBusy)
            return;
        ShowInserted(ScenarioGraph.Duplicate(Scenario, vm.Step));
        StatusText = "Düğüm klonlandı. Sağdaki panelden düzenleyin, sonra bir çıkıştan sürükleyerek bağlayın.";
    }

    [RelayCommand]
    private void AddWaitStep()
    {
        if (IsBusy)
            return;
        InsertStepAfter(SelectedStep, new Step { Type = StepType.Wait, Value = "1000" });
    }

    [RelayCommand]
    private void AddWaitForTextStep()
    {
        if (IsBusy)
            return;
        InsertStepAfter(SelectedStep, new Step { Type = StepType.WaitForText, Value = "", TimeoutMs = WaitStepTimeoutMs });
        StatusText = "Eklenen adımın \"Değer\" kutusuna, sayfada görünmesini beklediğiniz yazıyı girin.";
    }

    [RelayCommand]
    private void AddWaitForElementStep()
    {
        if (IsBusy)
            return;
        if (SelectedStep is not { Step.Selectors.Count: > 0 } selected)
        {
            StatusText = "Önce listeden beklenecek öğeye ait bir adımı (ör. bir tıklama) seçin.";
            return;
        }
        var wait = StepDescriber.WaitForElementOf(selected.Step, WaitStepTimeoutMs);
        ScenarioGraph.InsertBefore(Scenario, selected.Step, wait);
        ShowInserted(wait);
    }

    // Bekleme adımları yavaş işlemler için eklendiğinden genel zaman aşımından uzun süre tanınır.
    private int WaitStepTimeoutMs => Math.Max(60_000, (int)((ActionTimeoutSeconds ?? 15) * 1000));

    private void InsertStepAfter(StepViewModel? anchor, Step step)
    {
        ScenarioGraph.InsertAfter(Scenario, anchor?.Step, step);
        ShowInserted(step);
    }

    private void ShowInserted(Step step)
    {
        ScenarioGraph.EnsurePositions(Scenario);
        var vm = new StepViewModel(step, this);
        Steps.Insert(Scenario.Steps.IndexOf(step), vm);
        foreach (var other in Steps)
            other.Refresh();
        SelectedStep = vm;
        Renumber();
        MarkChanged();
    }

    // ---------- Veri kaynağı ----------

    [RelayCommand]
    private async Task ChooseDataSourceAsync()
    {
        if (Host is null)
            return;
        var files = await Host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Veri kaynağı seç",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Excel, CSV veya JSON") { Patterns = ["*.xlsx", "*.xlsm", "*.csv", "*.tsv", "*.txt", "*.json"] },
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            LoadData(path, null);
            if (Data is not null)
                UseDataSource = true;
        }
    }

    [RelayCommand]
    private void ReloadDataSource()
    {
        if (Data is not null)
            LoadData(Data.Path, Data.Sheet);
    }

    [RelayCommand]
    private void ClearDataSource()
    {
        Data = null;
        Sheets.Clear();
        Scenario.DataSourcePath = null;
        Scenario.DataSourceSheet = null;
        UseDataSource = false;
        RebuildColumnChoices();
        MarkChanged();
    }

    private void LoadData(string path, string? sheet)
    {
        try
        {
            var data = DataSourceLoader.Load(path, sheet);
            _loading = true;
            try
            {
                Sheets.Clear();
                foreach (var s in data.Sheets)
                    Sheets.Add(s);
                SelectedSheet = data.Sheet;
                Data = data;
                LastRow = data.Rows.Count;
                FirstRow = 1;
            }
            finally
            {
                _loading = false;
            }
            OnPropertyChanged(nameof(HasMultipleSheets));
            Scenario.DataSourcePath = data.Path;
            Scenario.DataSourceSheet = data.Sheet;
            RebuildColumnChoices();
            MarkChanged();
            AddLog($"Veri kaynağı yüklendi: {System.IO.Path.GetFileName(path)} ({data.Rows.Count} satır, {data.Columns.Count} kolon)");
        }
        catch (Exception ex)
        {
            AddLog($"Veri kaynağı okunamadı: {ex.Message}");
            StatusText = "Veri kaynağı okunamadı. Ayrıntılar günlükte.";
        }
    }

    private void RebuildColumnChoices()
    {
        var wanted = new List<string> { NoColumn };
        if (Data is not null)
            wanted.AddRange(Data.Columns);
        // Veri kaynağında olmayan ama adıma bağlı kolonlar da görünmeli ki kullanıcı fark edebilsin.
        wanted.AddRange(Scenario.BoundParameters.Where(p => !wanted.Contains(p, StringComparer.OrdinalIgnoreCase)));

        ColumnChoices.Clear();
        foreach (var c in wanted)
            ColumnChoices.Add(c);
        foreach (var step in Steps)
            step.Refresh();
    }

    // ---------- Oynatma ----------

    private bool CanPlay() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        await FlushSaveAsync();
        if (UseDataSource && Data is null)
        {
            StatusText = "Veri kaynağıyla oynatmak için önce bir dosya seçin.";
            return;
        }

        _lastScreenshotDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(ScenarioStore.DefaultDirectory)!, "Hatalar", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var options = new PlaybackOptions
        {
            Data = UseDataSource ? Data : null,
            Iterations = (int)((UseDataSource ? DataPasses : RepeatCount) ?? 1),
            FirstRow = UseDataSource ? (int?)FirstRow : null,
            LastRow = UseDataSource ? (int?)LastRow : null,
            Headless = Headless,
            SpeedFactor = SelectedSpeed.Factor,
            FixedDelayMs = SelectedSpeed.Fixed ? (int)((FixedDelaySeconds ?? 0) * 1000) : null,
            MaxDelayMs = LimitLongDelays ? 5000 : null,
            ActionTimeoutMs = (int)((ActionTimeoutSeconds ?? 15) * 1000),
            RetryCount = (int)(RetryCount ?? 0),
            ContinueOnError = ContinueOnError,
            FreshSessionPerIteration = FreshSession,
            KeepPageOpen = KeepPageOpen,
            TypeDelayMs = TypeSlowly ? 60 : 0,
            ScreenshotDirectory = _lastScreenshotDir,
        };

        foreach (var s in Steps)
            s.State = StepRunState.None;
        ProgressValue = 0;
        ProgressMax = 1;
        LastResult = null;
        IsPlaying = true;
        _playCts = new CancellationTokenSource();
        StatusText = "Oynatılıyor...";
        AddLog($"Oynatma başladı: {Scenario.Name}");

        try
        {
            var scenario = Scenario;
            var token = _playCts.Token;
            var result = await Task.Run(() => new Player().RunAsync(scenario, options,
                e => Dispatcher.UIThread.Post(() => OnPlaybackEvent(e)), token));
            LastResult = result;
            var summary = $"{result.SuccessCount} başarılı, {result.FailureCount} hatalı";
            StatusText = result.Cancelled ? $"Durduruldu. {summary}" : $"Bitti: {summary}";
            AddLog(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = "Oynatılamadı. Ayrıntılar günlükte.";
            AddLog(ex.Message);
        }
        finally
        {
            IsPlaying = false;
            _playCts.Dispose();
            _playCts = null;
        }
    }

    private bool CanStopPlayback() => IsPlaying;

    [RelayCommand(CanExecute = nameof(CanStopPlayback))]
    private void StopPlayback()
    {
        _playCts?.Cancel();
        StatusText = "Durduruluyor...";
    }

    private void OnPlaybackEvent(PlaybackEvent e)
    {
        ProgressMax = Math.Max(1, e.TotalIterations);
        var row = e.RowNumber is { } r ? $" (satır {r})" : "";
        switch (e.Kind)
        {
            case PlaybackEventKind.Info:
                AddLog(e.Message ?? "");
                break;
            case PlaybackEventKind.IterationStarted:
                foreach (var s in Steps)
                    s.State = StepRunState.None;
                StatusText = $"Tekrar {e.Iteration}/{e.TotalIterations}{row}";
                break;
            case PlaybackEventKind.StepStarted when e.StepIndex < Steps.Count:
                Steps[e.StepIndex].State = StepRunState.Running;
                break;
            case PlaybackEventKind.StepCompleted when e.StepIndex < Steps.Count:
                Steps[e.StepIndex].State = e.Port == StepPort.False ? StepRunState.No : StepRunState.Ok;
                break;
            case PlaybackEventKind.StepHandledError:
                if (e.StepIndex < Steps.Count)
                {
                    Steps[e.StepIndex].State = StepRunState.Failed;
                    Steps[e.StepIndex].Error = e.Message;
                }
                AddLog($"Tekrar {e.Iteration}{row}: adım {e.StepIndex + 1} hatalı, B planından devam ediliyor — {e.Message}");
                break;
            case PlaybackEventKind.StepFailed:
                if (e.StepIndex < Steps.Count)
                {
                    Steps[e.StepIndex].State = StepRunState.Failed;
                    Steps[e.StepIndex].Error = e.Message;
                }
                AddLog($"Tekrar {e.Iteration}{row}: adım {e.StepIndex + 1} hatalı — {e.Message}");
                break;
            case PlaybackEventKind.IterationCompleted:
                ProgressValue = e.Iteration;
                break;
        }
    }

    private bool HasResult() => LastResult is not null;

    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task SaveReportAsync()
    {
        if (Host is null || LastResult is null)
            return;
        var file = await Host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Sonuç raporunu kaydet",
            SuggestedFileName = $"rapor-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await ReportWriter.WriteCsvAsync(LastResult, path);
            AddLog($"Rapor kaydedildi: {path}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task OpenErrorFolderAsync()
    {
        if (Host is null || _lastScreenshotDir is null || !Directory.Exists(_lastScreenshotDir))
        {
            AddLog("Bu oynatmada hata ekran görüntüsü yok.");
            return;
        }
        await Host.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(_lastScreenshotDir));
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    /// <summary>Pencere kapanırken çağrılır: oynatma/kayıt durdurulur, bekleyen değişiklik yazılır.</summary>
    public void Shutdown()
    {
        _playCts?.Cancel();
        _ = _recorder?.StopAsync();
        if (_saveTimer.IsEnabled && CurrentPath is not null)
        {
            _saveTimer.Stop();
            try
            {
                ScenarioStore.Save(Scenario, CurrentPath);
            }
            catch (IOException)
            {
            }
        }
    }
}
