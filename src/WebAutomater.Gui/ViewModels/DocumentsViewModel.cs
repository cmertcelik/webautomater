using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAutomater.Core.Documents;

namespace WebAutomater.Gui.ViewModels;

/// <summary>Sayfanın üstüne yalnızca ekranda çizilen vurgu; PDF dosyasına yazılmaz.</summary>
public sealed record PageHighlight(PdfRect Bounds, bool IsValid);

/// <summary>Bulunan bir numara; aynı numara birden çok sayfada geçse de tek satırdır.</summary>
public sealed partial class IdentifierRow(IdentifierMatch match, int firstPage) : ObservableObject
{
    private readonly SortedSet<int> _pages = [firstPage];

    public string Value { get; } = match.Value;

    public bool IsValid { get; } = match.IsValid;

    public string KindText { get; } = match.Kind == IdentifierKind.TcKimlik ? "T.C. Kimlik" : "Vergi No";

    public string ValidityTip => IsValid
        ? "Kontrol hanesi doğru."
        : "Hane sayısı uyuyor ama kontrol hanesi tutmuyor. Telefon, belge numarası vb. olabilir.";

    public int FirstPage => _pages.Min;

    public string PagesText
    {
        get
        {
            // Ardışık sayfalar aralık olarak: "s. 1–4, 9"
            var parts = new List<string>();
            int? start = null, previous = null;
            foreach (var page in _pages.Append(int.MaxValue))
            {
                if (previous is { } last && page != last + 1)
                {
                    parts.Add(start == last ? $"{last}" : $"{start}–{last}");
                    start = null;
                }

                start ??= page;
                previous = page;
            }

            return "s. " + string.Join(", ", parts);
        }
    }

    public void AddPage(int page)
    {
        if (_pages.Add(page))
            OnPropertyChanged(nameof(PagesText));
    }
}

public sealed partial class DocumentsViewModel : ObservableObject
{
    private readonly List<IdentifierRow> _allRows = [];
    private readonly Dictionary<string, IdentifierRow> _rowsByValue = [];
    private CancellationTokenSource? _openCts;
    private bool _saving;

    public DocumentsViewModel()
    {
        var settings = LoadSettings();
        _excelPath = settings.ExcelPath;
        if (!string.IsNullOrEmpty(settings.RootFolder))
            // Dispatcher üzerinden: await sonrası devam da arayüz iş parçacığında olsun.
            Dispatcher.UIThread.Post(() => _ = RestoreRootAsync(settings.RootFolder));
    }

    public TopLevel? Host { get; set; }

    public string WindowTitle => Document is null
        ? "WebAutomater — Belge Tarama"
        : $"WebAutomater — {Path.GetFileName(Document.Path)}";

    // ---------- Dosya ağacı ----------

    public BulkObservableCollection<FileNode> Nodes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoot))]
    private string? _rootFolder;

    public bool HasRoot => RootFolder is not null;

    [ObservableProperty]
    private FileNode? _selectedNode;

    [ObservableProperty]
    private string _treeMessage = "";

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (Host is null)
            return;
        var folders = await Host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Belge klasörünü seçin",
            AllowMultiple = false,
            SuggestedStartLocation = RootFolder is null ? null : await Host.StorageProvider.TryGetFolderFromPathAsync(RootFolder),
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            await LoadRootAsync(path, []);
            SaveSettings();
        }
    }

    [RelayCommand]
    private async Task RefreshTreeAsync()
    {
        if (RootFolder is null)
            return;
        var expanded = Nodes.Where(n => n.IsExpanded).Select(n => n.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await LoadRootAsync(RootFolder, expanded);
    }

    [RelayCommand]
    private async Task ToggleNodeAsync(FileNode? node)
    {
        if (node is null || !node.IsDirectory)
            return;
        if (node.IsExpanded)
            Collapse(node);
        else
            await ExpandAsync(node);
    }

    public void Collapse(FileNode node)
    {
        node.IsExpanded = false;
        var index = Nodes.IndexOf(node);
        if (index < 0)
            return;
        var end = index + 1;
        while (end < Nodes.Count && Nodes[end].Depth > node.Depth)
            end++;
        Nodes.RemoveRangeAt(index + 1, end - index - 1);
    }

    public async Task ExpandAsync(FileNode node)
    {
        if (node.IsExpanded)
            return;
        node.IsExpanded = true;
        TreeMessage = "";
        try
        {
            var children = await Task.Run(() => FileTree.ListChildren(node.FullPath, node.Depth + 1));
            var index = Nodes.IndexOf(node);
            // Beklerken kapatılmış ya da ağaç yenilenmiş olabilir.
            if (index < 0 || !node.IsExpanded)
                return;
            node.IsEmpty = children.Count == 0;
            Nodes.InsertRange(index + 1, children);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            node.IsExpanded = false;
            TreeMessage = $"\"{node.Name}\" açılamadı: {ex.Message}";
        }
    }

    private async Task RestoreRootAsync(string path)
    {
        // Ağ sürücüsü erişilemezse Directory.Exists uzun sürebilir; arayüzü bekletmeyelim.
        if (await Task.Run(() => Directory.Exists(path)))
            await LoadRootAsync(path, []);
    }

    private async Task LoadRootAsync(string path, HashSet<string> expanded)
    {
        TreeMessage = "";
        try
        {
            var top = await Task.Run(() => FileTree.ListChildren(path, 0));
            RootFolder = path;
            Nodes.ReplaceAll(top);
            TreeMessage = top.Count == 0 ? "Bu klasör boş." : "";

            // Yenilemede açık klasörleri geri aç. Üst klasör önce geldiği için sırayla gezmek yeterli.
            for (var i = 0; i < Nodes.Count && expanded.Count > 0; i++)
            {
                if (Nodes[i].IsDirectory && expanded.Remove(Nodes[i].FullPath))
                    await ExpandAsync(Nodes[i]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            TreeMessage = $"Klasör okunamadı: {ex.Message}";
        }
    }

    partial void OnSelectedNodeChanged(FileNode? value)
    {
        if (value is null || value.IsDirectory)
            return;
        if (value.IsPdf)
            _ = OpenPdfAsync(value.FullPath);
        else
            TreeMessage = "Yalnızca PDF dosyaları görüntülenebilir.";
    }

    // ---------- PDF ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasDocument), nameof(OpenPdfPath))]
    [NotifyCanExecuteChangedFor(nameof(SaveToExcelCommand))]
    private PdfDocument? _document;

    public bool HasDocument => Document is not null;

    public string OpenPdfPath => Document?.Path ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _zoom = 1;

    [ObservableProperty]
    private bool _fitWidth = true;

    public string ZoomText => $"%{Zoom * 100:0}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowViewerMessage))]
    private string _viewerMessage = "Soldan bir klasör seçip bir PDF dosyasına tıklayın.";

    public bool ShowViewerMessage => !string.IsNullOrEmpty(ViewerMessage);

    private async Task OpenPdfAsync(string path)
    {
        TreeMessage = "";
        _openCts?.Cancel();
        var cts = _openCts = new CancellationTokenSource();
        var token = cts.Token;
        try
        {
            // Klavyeyle listede hızlı gezerken ara dosyalar boşuna açılmasın.
            await Task.Delay(120, token);
            ViewerMessage = Document is null ? "Açılıyor..." : ViewerMessage;
            var document = await Task.Run(() => PdfDocument.Open(path), CancellationToken.None);
            if (token.IsCancellationRequested)
            {
                _ = Task.Run(document.Dispose);
                return;
            }

            ReplaceDocument(document);
            ViewerMessage = document.PageCount == 0 ? "Bu PDF'te sayfa yok." : "";
            await ScanAsync(document, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (PdfOpenException ex)
        {
            if (!token.IsCancellationRequested)
            {
                ReplaceDocument(null);
                ViewerMessage = ex.Message;
            }
        }
    }

    private void ReplaceDocument(PdfDocument? document)
    {
        var old = Document;
        Document = document;
        ClearIdentifiers();
        // Kapatma, süren bir sayfa çiziminin bitmesini bekleyebilir; arayüz iş parçacığında yapılmaz.
        if (old is not null)
            _ = Task.Run(old.Dispose);
    }

    // ---------- Vurgular ----------

    private readonly Dictionary<int, List<PageHighlight>> _highlights = [];

    /// <summary>Sayfa dizini (0'dan) → o sayfadaki vurgular. Yalnızca arayüz iş parçacığında değişir.</summary>
    public IReadOnlyDictionary<int, List<PageHighlight>> Highlights => _highlights;

    /// <summary>Tarama yeni vurgu ekledi ya da liste temizlendi.</summary>
    public event Action? HighlightsChanged;

    [ObservableProperty]
    private bool _showHighlights = true;

    // ---------- Numara tarama ----------

    public ObservableCollection<IdentifierRow> VisibleRows { get; } = [];

    [ObservableProperty]
    private bool _onlyValid;

    [ObservableProperty]
    private string _scanStatus = "";

    [ObservableProperty]
    private string _scanWarning = "";

    public bool HasVisibleRows => VisibleRows.Count > 0;

    partial void OnOnlyValidChanged(bool value) => RebuildVisibleRows();

    private void ClearIdentifiers()
    {
        _allRows.Clear();
        _highlights.Clear();
        HighlightsChanged?.Invoke();
        _rowsByValue.Clear();
        RebuildVisibleRows();
        ScanStatus = "";
        ScanWarning = "";
    }

    private void RebuildVisibleRows()
    {
        VisibleRows.Clear();
        foreach (var row in _allRows.Where(r => !OnlyValid || r.IsValid))
            VisibleRows.Add(row);
        NotifyRowsChanged();
    }

    private void NotifyRowsChanged()
    {
        OnPropertyChanged(nameof(HasVisibleRows));
        CopyAllCommand.NotifyCanExecuteChanged();
    }

    private async Task ScanAsync(PdfDocument document, CancellationToken token)
    {
        var pagesWithoutText = 0;
        try
        {
            for (var i = 0; i < document.PageCount; i++)
            {
                token.ThrowIfCancellationRequested();
                var page = i;
                // Her sayfa ayrı işlem: PDF kilidi araya çizim girebilsin diye sayfalar arasında bırakılır.
                var (matches, bounds, hasText) = await Task.Run(() =>
                {
                    try
                    {
                        var text = document.ExtractText(page);
                        var found = IdentifierFinder.Find(text).ToList();
                        var rects = found.Count == 0
                            ? []
                            : document.GetTextBounds(page, found.Select(m => (m.Start, m.Length)).ToList());
                        return (found, rects, !string.IsNullOrWhiteSpace(text));
                    }
                    catch (InvalidOperationException)
                    {
                        // Bozuk tek sayfa taramayı durdurmasın.
                        return (new List<IdentifierMatch>(), (IReadOnlyList<IReadOnlyList<PdfRect>>)[], false);
                    }
                }, token);

                token.ThrowIfCancellationRequested();
                if (!hasText)
                    pagesWithoutText++;
                AddMatches(matches, page + 1);
                AddHighlights(matches, bounds, page);
                if (i % 20 == 0)
                    ScanStatus = $"Taranıyor... {i + 1}/{document.PageCount}";
            }

            ScanStatus = $"{_allRows.Count} numara bulundu ({document.PageCount} sayfa).";
            if (pagesWithoutText > 0)
            {
                ScanWarning = pagesWithoutText == document.PageCount
                    ? "Bu PDF'te metin katmanı yok (taranmış görüntü olabilir). Numaralar okunamadı."
                    : $"{pagesWithoutText} sayfada metin katmanı yok; bu sayfalar taranamadı.";
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void AddHighlights(List<IdentifierMatch> matches, IReadOnlyList<IReadOnlyList<PdfRect>> bounds, int page)
    {
        if (matches.Count == 0)
            return;
        var list = new List<PageHighlight>();
        for (var i = 0; i < matches.Count && i < bounds.Count; i++)
            list.AddRange(bounds[i].Select(rect => new PageHighlight(rect, matches[i].IsValid)));
        _highlights[page] = list;
        HighlightsChanged?.Invoke();
    }

    private void AddMatches(List<IdentifierMatch> matches, int page)
    {
        var added = false;
        foreach (var match in matches)
        {
            if (_rowsByValue.TryGetValue(match.Value, out var row))
            {
                row.AddPage(page);
                continue;
            }

            row = new IdentifierRow(match, page);
            _rowsByValue[match.Value] = row;
            _allRows.Add(row);
            if (!OnlyValid || row.IsValid)
            {
                VisibleRows.Add(row);
                added = true;
            }
        }

        if (added)
            NotifyRowsChanged();
    }

    [RelayCommand(CanExecute = nameof(HasVisibleRows))]
    private Task CopyAllAsync() => CopyRowsAsync(VisibleRows);

    /// <summary>Numaraları alt alta panoya yazar; Excel'e yapıştırınca her biri ayrı satıra gelir.</summary>
    public async Task CopyRowsAsync(IReadOnlyCollection<IdentifierRow> rows)
    {
        if (rows.Count == 0 || Host?.Clipboard is not { } clipboard)
            return;
        var text = string.Join(Environment.NewLine, rows.Select(r => r.Value));

        // Windows'ta pano başka bir uygulamada anlık kilitli olabilir.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await clipboard.SetTextAsync(text);
                ScanStatus = $"{rows.Count} numara panoya kopyalandı.";
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == 4)
                {
                    ScanStatus = "Panoya kopyalanamadı, yeniden deneyin.";
                    return;
                }

                await Task.Delay(100);
            }
        }
    }

    // ---------- Excel'e kaydet ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExcelFileName))]
    [NotifyCanExecuteChangedFor(nameof(SaveToExcelCommand))]
    private string? _excelPath;

    public string ExcelFileName => string.IsNullOrEmpty(ExcelPath) ? "Excel dosyası seçilmedi" : ExcelPath;

    [ObservableProperty] private string _documentDateText = "";
    [ObservableProperty] private DateTime? _pickedDate;
    [ObservableProperty] private string _documentSubject = "";
    [ObservableProperty] private string _documentNumber = "";
    [ObservableProperty] private string _destisNumber = "";
    [ObservableProperty] private string _documentDescription = "";
    [ObservableProperty] private string _excelMessage = "";
    [ObservableProperty] private bool _excelMessageIsError;

    partial void OnPickedDateChanged(DateTime? value)
    {
        if (value is { } date)
            DocumentDateText = date.ToString("dd.MM.yyyy");
    }

    [RelayCommand]
    private async Task ChooseExcelAsync()
    {
        if (Host is null)
            return;
        var files = await Host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Excel dosyasını seçin",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Excel") { Patterns = ["*.xlsx", "*.xlsm"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            SetExcelPath(path);
    }

    [RelayCommand]
    private async Task NewExcelAsync()
    {
        if (Host is null)
            return;
        var file = await Host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Yeni Excel dosyası",
            SuggestedFileName = "Belgeler.xlsx",
            DefaultExtension = "xlsx",
            FileTypeChoices = [new FilePickerFileType("Excel") { Patterns = ["*.xlsx"] }],
        });
        if (file?.TryGetLocalPath() is { } path)
            SetExcelPath(path);
    }

    private void SetExcelPath(string path)
    {
        ExcelPath = path;
        ExcelMessage = "";
        SaveSettings();
    }

    private bool CanSaveToExcel() => Document is not null && !string.IsNullOrEmpty(ExcelPath);

    [RelayCommand(CanExecute = nameof(CanSaveToExcel))]
    private async Task SaveToExcelAsync()
    {
        if (_saving || Document is null || string.IsNullOrEmpty(ExcelPath))
            return;

        DateTime? date = null;
        if (!string.IsNullOrWhiteSpace(DocumentDateText))
        {
            if (!DocumentDate.TryParse(DocumentDateText, out var parsed))
            {
                ShowExcelMessage("Belge tarihi anlaşılamadı. Örnek: 31.12.2025", isError: true);
                return;
            }

            date = parsed;
        }

        var entry = new DocumentLogEntry(Path.GetFullPath(Document.Path), date, DocumentSubject.Trim(),
            DocumentNumber.Trim(), DestisNumber.Trim(), DocumentDescription.Trim());
        var excel = ExcelPath;

        _saving = true;
        try
        {
            var row = await Task.Run(() => DocumentLogWriter.Append(excel, entry));
            ShowExcelMessage($"{row}. satır olarak eklendi.", isError: false);
            DocumentDateText = "";
            PickedDate = null;
            DocumentSubject = "";
            DocumentNumber = "";
            DestisNumber = "";
            DocumentDescription = "";
        }
        catch (Exception ex)
        {
            // Girilen bilgiler silinmez; kullanıcı sorunu giderip yeniden kaydedebilir.
            ShowExcelMessage(ex is ExcelFileLockedException ? ex.Message : $"Kaydedilemedi: {ex.Message}", isError: true);
        }
        finally
        {
            _saving = false;
        }
    }

    private void ShowExcelMessage(string message, bool isError)
    {
        ExcelMessage = message;
        ExcelMessageIsError = isError;
    }

    // ---------- Ayarlar ----------

    private sealed record Settings(string? RootFolder = null, string? ExcelPath = null);

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebAutomater", "belge-tarama.json");

    private static Settings LoadSettings()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Settings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(RootFolder, ExcelPath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ayar kaydedilemezse yalnızca bir sonraki açılışta hatırlanmaz.
        }
    }

    public void Shutdown()
    {
        _openCts?.Cancel();
        Document?.Dispose();
    }
}
