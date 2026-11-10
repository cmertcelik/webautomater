using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAutomater.Core.Diagnostics;
using WebAutomater.Core.Documents;

namespace WebAutomater.Gui.ViewModels;

/// <summary>Sayfanın üstüne yalnızca ekranda çizilen vurgu; PDF dosyasına yazılmaz.</summary>
public sealed record PageHighlight(PdfRect Bounds, bool IsValid);

/// <summary>
/// Bulunan bir numara; aynı numara birden çok sayfada (toplu taramada birden çok dosyada) geçse de tek satırdır.
/// </summary>
public sealed partial class IdentifierRow : ObservableObject
{
    private readonly List<(string Path, SortedSet<int> Pages)> _files = [];
    private readonly bool _showFiles;

    public IdentifierRow(IdentifierMatch match, string path, int page, bool showFiles)
    {
        Value = match.Value;
        IsValid = match.IsValid;
        KindText = match.Kind == IdentifierKind.TcKimlik ? "T.C. Kimlik" : "Vergi No";
        _showFiles = showFiles;
        _files.Add((path, [page]));
    }

    public string Value { get; }

    public bool IsValid { get; }

    public string KindText { get; }

    public string ValidityTip => IsValid
        ? "Kontrol hanesi doğru."
        : "Hane sayısı uyuyor ama kontrol hanesi tutmuyor. Telefon, belge numarası vb. olabilir.";

    public string FirstFile => _files[0].Path;

    public int FirstPage => _files[0].Pages.Min;

    public string PagesText
    {
        get
        {
            if (!_showFiles)
                return FormatPages(_files[0].Pages);
            var text = $"{Path.GetFileName(_files[0].Path)} · {FormatPages(_files[0].Pages)}";
            return _files.Count > 1 ? $"{text} · +{_files.Count - 1} dosya" : text;
        }
    }

    /// <summary>Toplu taramada numaranın geçtiği tüm dosyalar.</summary>
    public string? LocationsTip
    {
        get
        {
            if (!_showFiles)
                return null;
            const int max = 25;
            var lines = _files.Take(max).Select(f => $"{f.Path}  ({FormatPages(f.Pages)})").ToList();
            if (_files.Count > max)
                lines.Add($"... ve {_files.Count - max} dosya daha");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public void Add(string path, int page)
    {
        var index = _files.FindIndex(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            _files.Add((path, [page]));
        else if (!_files[index].Pages.Add(page))
            return;
        OnPropertyChanged(nameof(PagesText));
        OnPropertyChanged(nameof(LocationsTip));
    }

    /// <summary>Dosya ya da klasör taşındıktan sonra yolları günceller.</summary>
    public void Relocate(string oldPath, string newPath)
    {
        var changed = false;
        for (var i = 0; i < _files.Count; i++)
        {
            if (FileOps.Remap(_files[i].Path, oldPath, newPath) is { } moved)
            {
                _files[i] = (moved, _files[i].Pages);
                changed = true;
            }
        }

        if (changed)
        {
            OnPropertyChanged(nameof(PagesText));
            OnPropertyChanged(nameof(LocationsTip));
        }
    }

    private static string FormatPages(SortedSet<int> pages)
    {
        // Ardışık sayfalar aralık olarak: "s. 1–4, 9"
        var parts = new List<string>();
        int? start = null, previous = null;
        foreach (var page in pages.Append(int.MaxValue))
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

public static class FileOps
{
    public static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), PathComparison);

    /// <summary><paramref name="path"/> taşınan öğenin kendisi ya da içindeyse yeni yolu, değilse null.</summary>
    public static string? Remap(string path, string oldPath, string newPath)
    {
        if (SamePath(path, oldPath))
            return newPath;
        var prefix = Path.TrimEndingDirectorySeparator(oldPath) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison) ? Path.Combine(newPath, path[prefix.Length..]) : null;
    }

    /// <summary><paramref name="path"/>, <paramref name="folder"/> klasörünün kendisi ya da içinde mi.</summary>
    public static bool IsInside(string path, string folder) => Remap(path, folder, folder) is not null;
}

public sealed partial class DocumentsViewModel : ObservableObject
{
    private const string LogArea = "Belge Tarama";

    // Açık belgenin numaraları (toplu modda da tutulur; toplu sonuçlar kapatılınca geri gelir).
    private readonly List<IdentifierRow> _docRows = [];
    private readonly Dictionary<string, IdentifierRow> _docRowsByValue = [];
    // "Hepsini bul" sonuçları.
    private readonly List<IdentifierRow> _bulkRows = [];
    private readonly Dictionary<string, IdentifierRow> _bulkRowsByValue = [];

    private CancellationTokenSource? _openCts;
    private CancellationTokenSource? _bulkCts;
    private CancellationTokenSource? _groupCts;
    private string? _requestedPath;
    private bool _saving;
    private bool _loadingFields;

    public DocumentsViewModel()
    {
        var settings = LoadSettings();
        _excelPath = settings.ExcelPath;
        LoadFields(settings.Fields is { Count: > 0 } fields ? WithPdfPath(fields) : ExcelFieldViewModel.Defaults);
        _caseMinDigits = Math.Clamp(settings.CaseMinDigits, 1, 20);
        _caseMaxDigits = Math.Clamp(settings.CaseMaxDigits, _caseMinDigits, 20);
        _singleFileFolders = settings.SingleFileFolders;
        foreach (var tag in settings.Tags ?? [])
            AttachTag(new GroupTagViewModel(tag));
        ValidateTags();
        Session = new SessionConfigViewModel(SecretStore.Unprotect(settings.LoginTokenRequest), SecretStore.Unprotect(settings.LoginSessionRequest),
            settings.LoginTokenField, settings.LoginAutoRefresh);
        Session.Changed += SaveSettings;
        CustomerQuery = new QueryConfigViewModel("Müşteri sorgulama", SecretStore.Unprotect(settings.CustomerQuery),
            settings.CustomerQueryField ?? "value", "Müşteri kaydı yok");
        IntelQuery = new QueryConfigViewModel("İstihbarat sorgulama", SecretStore.Unprotect(settings.IntelQuery),
            settings.IntelResultField ?? "value", "İstihbarat kaydı yok");
        foreach (var query in Queries)
        {
            query.Changed += SaveSettings;
            query.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(QueryConfigViewModel.Template))
                    RunQueryCommand.NotifyCanExecuteChanged();
            };
        }

        VisibleRows.CollectionChanged += (_, _) => UpdateQueryTargets();
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
    [NotifyPropertyChangedFor(nameof(HasRoot), nameof(NewFolderLocation), nameof(GroupLocation), nameof(AutoGroupTip))]
    [NotifyCanExecuteChangedFor(nameof(FindAllCommand), nameof(AutoGroupCommand))]
    private string? _rootFolder;

    public bool HasRoot => RootFolder is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewFolderLocation), nameof(GroupLocation), nameof(AutoGroupTip))]
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
            CloseBulk();
            AppLog.Info(LogArea, $"Klasör seçildi: {path}");
            await LoadRootAsync(path, []);
            SaveSettings();
        }
    }

    [RelayCommand]
    private Task RefreshTreeAsync() => ReloadTreeAsync();

    /// <summary>Ağacı yeniden okur; açık klasörler açık kalır, seçim korunur.</summary>
    private async Task ReloadTreeAsync(IEnumerable<string>? alsoExpand = null, string? select = null,
        Func<string, string>? remap = null)
    {
        if (RootFolder is null)
            return;
        select ??= SelectedNode?.FullPath;
        var expanded = Nodes.Where(n => n.IsExpanded).Select(n => remap?.Invoke(n.FullPath) ?? n.FullPath)
            .Concat(alsoExpand ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await LoadRootAsync(RootFolder, expanded);
        if (select is not null && Nodes.FirstOrDefault(n => FileOps.SamePath(n.FullPath, select)) is { } node)
        {
            // Aynı PDF yeniden açılmasın diye istek yolu önceden ayarlanır.
            if (node.IsPdf && FileOps.SamePath(Document?.Path, node.FullPath))
                _requestedPath = node.FullPath;
            SelectedNode = node;
        }
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
        catch (Exception ex)
        {
            node.IsExpanded = false;
            TreeMessage = $"\"{node.Name}\" açılamadı: {AppLog.Describe(ex)}";
            AppLog.Warning(LogArea, $"Klasör açılamadı: {node.FullPath}", ex);
        }
    }

    private async Task RestoreRootAsync(string path)
    {
        // Ağ sürücüsü erişilemezse Directory.Exists uzun sürebilir; arayüzü bekletmeyelim.
        if (await Task.Run(() => Directory.Exists(path)))
            await LoadRootAsync(path, []);
        else
            AppLog.Warning(LogArea, $"Son kullanılan klasör bulunamadı: {path}");
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
        catch (Exception ex)
        {
            TreeMessage = $"Klasör okunamadı: {AppLog.Describe(ex)}";
            AppLog.Warning(LogArea, $"Klasör okunamadı: {path}", ex);
        }
    }

    partial void OnSelectedNodeChanged(FileNode? value)
    {
        if (value is null || value.IsDirectory)
            return;
        if (!value.IsPdf)
            TreeMessage = "Yalnızca PDF dosyaları görüntülenebilir.";
        else if (!FileOps.SamePath(value.FullPath, _requestedPath))
            _ = OpenPdfAsync(value.FullPath);
    }

    // ---------- Yeni klasör ----------

    /// <summary>Yeni klasörün oluşturulacağı yer: seçili klasör, seçili dosyanın klasörü ya da kök.</summary>
    public string? NewFolderLocation => SelectedNode switch
    {
        { IsDirectory: true } dir => dir.FullPath,
        { } file => Path.GetDirectoryName(file.FullPath),
        _ => RootFolder,
    };

    [ObservableProperty]
    private string _newFolderName = "";

    [ObservableProperty]
    private string _newFolderError = "";

    public void PrepareNewFolder()
    {
        NewFolderName = "Yeni klasör";
        NewFolderError = "";
        OnPropertyChanged(nameof(NewFolderLocation));
    }

    /// <summary>Klasörü oluşturur; başarılıysa true (açılır pencere kapanabilir).</summary>
    public async Task<bool> CreateFolderAsync()
    {
        var parent = NewFolderLocation;
        var name = NewFolderName.Trim();
        if (parent is null)
            return false;
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            NewFolderError = "Geçerli bir ad yazın. Şu karakterler kullanılamaz: \\ / : * ? \" < > |";
            return false;
        }

        var path = Path.Combine(parent, name);
        try
        {
            var created = await Task.Run(() =>
            {
                if (Directory.Exists(path) || File.Exists(path))
                    return false;
                Directory.CreateDirectory(path);
                return true;
            });
            if (!created)
            {
                NewFolderError = $"\"{name}\" adında bir öğe zaten var.";
                return false;
            }
        }
        catch (Exception ex)
        {
            NewFolderError = $"Klasör oluşturulamadı: {AppLog.Describe(ex)}";
            AppLog.Warning(LogArea, $"Klasör oluşturulamadı: {path}", ex);
            return false;
        }

        await ReloadTreeAsync(alsoExpand: [parent], select: path);
        return true;
    }

    // ---------- Sürükleyerek taşıma ----------

    /// <summary>Bırakılan yere göre hedef klasör: klasörün kendisi, dosyanın klasörü ya da (boş alan) kök.</summary>
    public string? DropFolderFor(FileNode? target) => target switch
    {
        null => RootFolder,
        { IsDirectory: true } => target.FullPath,
        _ => Path.GetDirectoryName(target.FullPath),
    };

    public bool CanMove(FileNode source, string? targetFolder)
    {
        if (targetFolder is null || RootFolder is null)
            return false;
        // Zaten orada.
        if (FileOps.SamePath(Path.GetDirectoryName(source.FullPath), targetFolder))
            return false;
        // Klasör kendi içine taşınamaz.
        return !(source.IsDirectory && FileOps.IsInside(targetFolder, source.FullPath));
    }

    public async Task MoveAsync(FileNode source, string targetFolder)
    {
        if (!CanMove(source, targetFolder))
            return;
        TreeMessage = "";
        var destination = Path.Combine(targetFolder, source.Name);
        try
        {
            var error = await Task.Run(() =>
            {
                if (File.Exists(destination) || Directory.Exists(destination))
                    return $"\"{Path.GetFileName(targetFolder)}\" klasöründe \"{source.Name}\" adında bir öğe zaten var.";
                if (source.IsDirectory)
                    Directory.Move(source.FullPath, destination);
                else
                    File.Move(source.FullPath, destination);
                return null;
            });
            if (error is not null)
            {
                TreeMessage = error;
                return;
            }
        }
        catch (Exception ex)
        {
            TreeMessage = $"\"{source.Name}\" taşınamadı: {AppLog.Describe(ex)}";
            AppLog.Warning(LogArea, $"Taşınamadı: {source.FullPath} → {destination}", ex);
            return;
        }

        AppLog.Info(LogArea, $"Taşındı: {source.FullPath} → {destination}");

        var oldPath = source.FullPath;
        Relocate(oldPath, destination);

        var select = SelectedNode?.FullPath is { } selected
            ? FileOps.Remap(selected, oldPath, destination) ?? selected
            : null;
        string[] expand = FileOps.SamePath(targetFolder, RootFolder) ? [] : [targetFolder];
        await ReloadTreeAsync(expand, select, p => FileOps.Remap(p, oldPath, destination) ?? p);
    }

    /// <summary>Taşınan öğeden sonra açık belge ve toplu sonuçlar yeni yolu göstersin.</summary>
    private void Relocate(string oldPath, string newPath)
    {
        if (Document is { } doc && FileOps.Remap(doc.Path, oldPath, newPath) is { } newDocPath)
        {
            doc.UpdatePath(newDocPath);
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(OpenPdfPath));
        }

        if (_requestedPath is not null && FileOps.Remap(_requestedPath, oldPath, newPath) is { } newRequested)
            _requestedPath = newRequested;
        foreach (var row in _bulkRows.Concat(_docRows))
            row.Relocate(oldPath, newPath);
    }

    // ---------- Otomatik gruplama ----------

    /// <summary>Etiketler; sıra önceliktir (bir dosya birden çok etikete uyarsa üstteki seçilir).</summary>
    public ObservableCollection<GroupTagViewModel> Tags { get; } = [];

    /// <summary>Taramadan sonra, taşımadan önce önizlemeyi gösterir; kullanıcı onaylarsa true. Görünüm tarafından atanır.</summary>
    public Func<GroupingPreviewViewModel, Task<bool>>? ConfirmGrouping { get; set; }

    [ObservableProperty]
    private int _caseMinDigits = 5;

    [ObservableProperty]
    private int _caseMaxDigits = 7;

    /// <summary>Numarası başka hiçbir dosyada geçmeyen dosya için de numara klasörü açılsın.</summary>
    [ObservableProperty]
    private bool _singleFileFolders;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoGroupText))]
    private bool _isGrouping;

    [ObservableProperty]
    private string _groupStatus = "";

    [ObservableProperty]
    private string? _groupStatusTip;

    public string AutoGroupText => IsGrouping ? "Durdur" : "Otomatik grupla";

    public bool HasNoTags => Tags.Count == 0;

    /// <summary>Gruplanacak klasör: seçili klasör, yoksa ana klasör (seçili PDF'in klasörü değil; o bir etiket klasörü olabilir).</summary>
    public string? GroupLocation => SelectedNode is { IsDirectory: true } dir ? dir.FullPath : RootFolder;

    public string AutoGroupTip => GroupLocation is { } location
        ? $"Adında aynı dosya numarası geçen dosyaları numara klasöründe toplar, sonra bu dosyalarda etiket arar ve bulunan etiketi klasör adına ekler (34765-vesayet). Taşımadan önce önizleme gösterilir.\nKlasör: {location}"
        : "Önce bir klasör seçin.";

    public string TagsButtonText => Tags.Count == 0 ? "Etiketler" : $"Etiketler ({Tags.Count})";

    partial void OnCaseMinDigitsChanged(int value)
    {
        if (CaseMaxDigits < value)
            CaseMaxDigits = value;
        SaveSettings();
    }

    partial void OnCaseMaxDigitsChanged(int value)
    {
        if (CaseMinDigits > value)
            CaseMinDigits = value;
        SaveSettings();
    }

    partial void OnSingleFileFoldersChanged(bool value) => SaveSettings();

    private void AttachTag(GroupTagViewModel tag)
    {
        tag.DefinitionChanged += OnTagChanged;
        Tags.Add(tag);
    }

    private void OnTagChanged()
    {
        ValidateTags();
        SaveSettings();
    }

    private void OnTagsListChanged()
    {
        ValidateTags();
        OnPropertyChanged(nameof(HasNoTags));
        OnPropertyChanged(nameof(TagsButtonText));
        SaveSettings();
    }

    /// <summary>Etiket adı klasör adı olacağı için boş, geçersiz ya da yinelenen olamaz.</summary>
    private bool ValidateTags()
    {
        var ok = true;
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var tag in Tags)
        {
            var name = tag.Name.Trim();
            tag.Error = name.Length == 0 ? "Etiket adı boş olamaz."
                : name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
                  name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    ? "Klasör adında şu karakterler kullanılamaz: \\ / : * ? \" < > |"
                : name.All(char.IsAsciiDigit) ? "Etiket adı yalnızca rakam olamaz (dosya numarası klasörleriyle karışır)."
                : !seen.Add(name) ? "Bu adda başka bir etiket var."
                : tag.Keywords.Count == 0 ? "En az bir aranacak kelime yazın."
                : "";
            ok &= !tag.HasError;
        }

        return ok;
    }

    [RelayCommand]
    private void AddTag()
    {
        var name = "Yeni etiket";
        for (var i = 2; Tags.Any(t => string.Equals(t.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase)); i++)
            name = $"Yeni etiket {i}";
        AttachTag(new GroupTagViewModel(new GroupTagDefinition(name, [])));
        OnTagsListChanged();
    }

    [RelayCommand]
    private void RemoveTag(GroupTagViewModel? tag)
    {
        if (tag is null)
            return;
        tag.DefinitionChanged -= OnTagChanged;
        Tags.Remove(tag);
        OnTagsListChanged();
    }

    [RelayCommand]
    private void MoveTagUp(GroupTagViewModel? tag) => MoveTag(tag, -1);

    [RelayCommand]
    private void MoveTagDown(GroupTagViewModel? tag) => MoveTag(tag, 1);

    private void MoveTag(GroupTagViewModel? tag, int delta)
    {
        if (tag is null)
            return;
        var index = Tags.IndexOf(tag);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Tags.Count)
            return;
        Tags.Move(index, target);
        OnTagsListChanged();
    }

    private bool CanAutoGroup() => RootFolder is not null;

    // Tek bir PDF'in etiket araması bundan uzun sürerse beklenmez, sonraki dosyaya geçilir.
    private static readonly TimeSpan FileScanTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 1) Dosya adlarına göre aynı numaralı dosyaları numara klasörlerinde toplar (etiket olmasa da).
    /// 2) Yalnızca bu grupların PDF'lerinde etiket arar; bulunanları klasör adına ekler.
    /// Önizleme onaylanınca uygular. Tarama sürerken basılırsa durdurur. Hiçbir hata uygulamayı kapatmaz.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAutoGroup), AllowConcurrentExecutions = true)]
    private async Task AutoGroupAsync()
    {
        if (_groupCts is not null)
        {
            _groupCts.Cancel();
            return;
        }

        if (GroupLocation is not { } folder)
            return;
        GroupStatusTip = null;
        if (!ValidateTags())
        {
            GroupStatus = "Etiket ayarlarındaki hataları düzeltin.";
            return;
        }

        var tags = Tags.Select(t => t.ToTag()).ToList();
        var options = new GroupingOptions(CaseMinDigits, CaseMaxDigits, SingleFileFolders);
        var matcher = new TagMatcher(tags);
        var cts = _groupCts = new CancellationTokenSource();
        var token = cts.Token;
        IsGrouping = true;
        TreeMessage = "";
        var warnings = new List<string>();
        var stage = "dosya adları okunurken";
        AppLog.Info(LogArea, $"Otomatik gruplama başladı: {folder} ({tags.Count} etiket)");
        try
        {
            // 1. aşama: yalnızca dosya adları; PDF açılmaz, hızlıdır.
            GroupStatus = "Dosya adları inceleniyor...";
            var grouping = await Task.Run(() => GroupingPlanner.ScanFolder(folder, options, token), token);
            if (grouping.Groups.Count == 0)
            {
                GroupStatus = grouping.Unchanged.Count == 0
                    ? "Bu klasörde gruplanacak dosya yok."
                    : $"Aynı dosya numarasını taşıyan dosya bulunamadı ({grouping.Unchanged.Count} dosya incelendi).";
                return;
            }

            // 2. aşama: yalnızca gruplanan dosyaların içinde etiket aranır.
            stage = "etiketler aranırken";
            var hits = new Dictionary<string, List<TagEvidence>>();
            if (!matcher.IsEmpty)
            {
                var toScan = grouping.Groups.Where(g => g.FilesToScan.Count > 0).ToList();
                var total = toScan.Sum(g => g.FilesToScan.Count);
                var done = 0;
                foreach (var group in toScan)
                {
                    var found = hits[group.Number] = [];
                    foreach (var file in group.FilesToScan)
                    {
                        token.ThrowIfCancellationRequested();
                        done++;
                        // Grupta tüm etiketler bulunduysa kalan dosyalara bakmaya gerek yok.
                        if (found.Select(h => h.TagIndex).Distinct().Count() == tags.Count)
                            continue;
                        GroupStatus = $"Etiketler aranıyor... {done}/{total} · {group.Number} · {Path.GetFileName(file)}";
                        var scan = await ScanWithTimeoutAsync(matcher, file, token);
                        if (scan.Error is not null)
                            warnings.Add($"{Path.GetFileName(file)}: {scan.Error}");
                        else if (scan.HasNoText)
                            warnings.Add($"{Path.GetFileName(file)}: metin katmanı yok (taranmış görüntü olabilir), etiket aranamadı.");
                        found.AddRange(scan.Hits
                            .Where(h => found.All(f => f.TagIndex != h.TagIndex))
                            .Select(h => new TagEvidence(h.TagIndex, tags[h.TagIndex].Name, file, h.Keyword)));
                    }
                }
            }

            stage = "önizleme hazırlanırken";
            var plan = GroupingPlanner.BuildPlan(grouping, hits, tags);
            GroupStatus = "";
            if (!plan.HasChanges)
            {
                GroupStatus = "Değişiklik yok: dosyalar zaten numara klasörlerinde ve yeni etiket bulunamadı.";
                return;
            }

            if (ConfirmGrouping is null || !await ConfirmGrouping(new GroupingPreviewViewModel(plan, warnings)))
            {
                GroupStatus = "Gruplama iptal edildi; hiçbir dosya taşınmadı.";
                return;
            }

            stage = "dosyalar taşınırken";
            GroupStatus = $"{plan.MoveCount} dosya taşınıyor...";
            var progress = new Progress<string>(name => GroupStatus = $"Taşınıyor: {name}");
            var result = await Task.Run(() => GroupingPlanner.Apply(plan, progress, CancellationToken.None));
            foreach (var (source, destination) in result.Renamed.Concat(result.Moved))
                Relocate(source, destination);

            GroupStatus = $"{result.Moved.Count} dosya taşındı, {plan.Folders.Count(f => f.IsNew)} klasör oluşturuldu, {result.Renamed.Count} klasöre etiket eklendi." +
                          (result.Failed.Count > 0 ? $" {result.Failed.Count} işlem yapılamadı; ayrıntı için üzerinde bekleyin." : "");
            GroupStatusTip = result.Failed.Count > 0 ? string.Join(Environment.NewLine, result.Failed.Take(30)) : null;
            AppLog.Info(LogArea, $"Otomatik gruplama bitti: {GroupStatus}");
            if (result.Failed.Count > 0)
                AppLog.Warning(LogArea, "Yapılamayan işlemler: " + string.Join(" / ", result.Failed));

            // Yüzlerce numara klasörünü tek tek açmak ağacı yavaşlatır; yalnızca gruplanan klasör açık gelsin.
            await ReloadTreeAsync(FileOps.SamePath(folder, RootFolder) ? [] : [folder]);
        }
        catch (OperationCanceledException)
        {
            GroupStatus = "Gruplama durduruldu; hiçbir dosya taşınmadı.";
            AppLog.Info(LogArea, "Otomatik gruplama kullanıcı tarafından durduruldu.");
        }
        catch (Exception ex)
        {
            GroupStatus = $"Gruplama {stage} yarıda kaldı: {AppLog.Describe(ex)}";
            AppErrors.Report(LogArea, $"Otomatik gruplama {stage} hata verdi. Hiçbir dosya kaybolmadı; ayrıntı günlükte.", ex);
        }
        finally
        {
            _groupCts = null;
            IsGrouping = false;
            cts.Dispose();
        }
    }

    /// <summary>Bir dosyanın etiket aramasını arka planda yapar; çok uzun sürerse beklemeyi bırakır.</summary>
    private static async Task<FileTagScan> ScanWithTimeoutAsync(TagMatcher matcher, string file, CancellationToken token)
    {
        var task = Task.Run(() => matcher.ScanFile(file, token), token);
        try
        {
            return await task.WaitAsync(FileScanTimeout, token);
        }
        catch (TimeoutException)
        {
            AppLog.Warning(LogArea, $"Etiket arama zaman aşımı ({FileScanTimeout.TotalSeconds:0} sn): {file}");
            // Arka plandaki okuma bitince sonucu sessizce atılır.
            _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return new FileTagScan(file, [], false, $"{FileScanTimeout.TotalSeconds:0} saniyede okunamadı, atlandı");
        }
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

    /// <summary>
    /// Toplu sonuçtan bir dosyayı açar. Belge görüntüleyiciye yüklenince true döner (tarama sürerken).
    /// </summary>
    public async Task<bool> ShowFileAsync(string path)
    {
        if (FileOps.SamePath(Document?.Path, path))
            return true;
        if (!File.Exists(path))
        {
            TreeMessage = $"\"{Path.GetFileName(path)}\" bulunamadı; taşınmış ya da silinmiş olabilir.";
            return false;
        }

        var opened = new TaskCompletionSource<bool>();
        _ = OpenPdfAsync(path, opened);
        // Ağaçta görünüyorsa onu seç (aynı dosya ikinci kez açılmaz).
        SelectedNode = Nodes.FirstOrDefault(n => FileOps.SamePath(n.FullPath, path));
        return await opened.Task;
    }

    private async Task OpenPdfAsync(string path, TaskCompletionSource<bool>? opened = null)
    {
        TreeMessage = "";
        _requestedPath = path;
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
            opened?.TrySetResult(true);
            await ScanAsync(document, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (PdfOpenException ex)
        {
            AppLog.Warning(LogArea, $"PDF açılamadı: {path}: {ex.Message}");
            if (!token.IsCancellationRequested)
            {
                ReplaceDocument(null);
                _requestedPath = null;
                ViewerMessage = ex.Message;
            }
        }
        catch (Exception ex)
        {
            // Bozuk ya da çok büyük PDF uygulamayı kapatmasın.
            AppLog.Error(LogArea, $"PDF açılırken/taranırken hata: {path}", ex);
            if (!token.IsCancellationRequested && FileOps.SamePath(_requestedPath, path))
            {
                if (Document is null || !FileOps.SamePath(Document.Path, path))
                {
                    ReplaceDocument(null);
                    _requestedPath = null;
                    ViewerMessage = $"PDF açılamadı: {AppLog.Describe(ex)}";
                }
                else
                {
                    DocWarning = $"Tarama yarıda kaldı: {AppLog.Describe(ex)}";
                }
            }
        }
        finally
        {
            opened?.TrySetResult(false);
        }
    }

    private void ReplaceDocument(PdfDocument? document)
    {
        var old = Document;
        Document = document;
        ClearDocIdentifiers();
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

    // ---------- Numara listesi ----------

    public ObservableCollection<IdentifierRow> VisibleRows { get; } = [];

    [ObservableProperty]
    private bool _onlyValid;

    /// <summary>Liste açık belge yerine "Hepsini bul" sonuçlarını gösteriyor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanStatus), nameof(ScanWarning), nameof(ScanWarningTip), nameof(IdentifiersTitle),
        nameof(IdentifiersHint))]
    private bool _isBulkMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindAllText))]
    private bool _isBulkScanning;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScanStatus))] private string _docStatus = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScanWarning))] private string _docWarning = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScanStatus))] private string _bulkStatus = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScanWarning))] private string _bulkWarning = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ScanWarningTip))] private string? _bulkWarningTip;

    public string ScanStatus => IsBulkMode ? BulkStatus : DocStatus;

    public string ScanWarning => IsBulkMode ? BulkWarning : DocWarning;

    public string? ScanWarningTip => IsBulkMode ? BulkWarningTip : null;

    public string IdentifiersTitle => IsBulkMode ? "Tüm PDF'lerdeki Numaralar" : "Bulunan Numaralar";

    public string IdentifiersHint => IsBulkMode
        ? "Ctrl ya da Shift ile seçip Ctrl+C ile kopyalayın. Çift tıklama numaranın geçtiği ilk dosyayı açar; tüm dosyalar için satırın üzerinde bekleyin."
        : "Ctrl ya da Shift ile seçip Ctrl+C ile kopyalayın. Çift tıklama o sayfaya gider.";

    public string FindAllText => IsBulkScanning ? "Durdur" : "Hepsini bul";

    public bool HasVisibleRows => VisibleRows.Count > 0;

    partial void OnOnlyValidChanged(bool value) => RebuildVisibleRows();

    partial void OnIsBulkModeChanged(bool value) => RebuildVisibleRows();

    private void ClearDocIdentifiers()
    {
        _docRows.Clear();
        _docRowsByValue.Clear();
        _highlights.Clear();
        HighlightsChanged?.Invoke();
        if (!IsBulkMode)
            RebuildVisibleRows();
        DocStatus = "";
        DocWarning = "";
    }

    private void RebuildVisibleRows()
    {
        VisibleRows.Clear();
        foreach (var row in (IsBulkMode ? _bulkRows : _docRows).Where(r => !OnlyValid || r.IsValid))
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
                AddMatches(_docRows, _docRowsByValue, matches, document.Path, page + 1, showFiles: false, visible: !IsBulkMode);
                AddHighlights(matches, bounds, page);
                if (i % 20 == 0)
                    DocStatus = $"Taranıyor... {i + 1}/{document.PageCount}";
            }

            DocStatus = $"{_docRows.Count} numara bulundu ({document.PageCount} sayfa).";
            if (pagesWithoutText > 0)
            {
                DocWarning = pagesWithoutText == document.PageCount
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

    private void AddMatches(List<IdentifierRow> rows, Dictionary<string, IdentifierRow> byValue,
        IEnumerable<IdentifierMatch> matches, string path, int page, bool showFiles, bool visible)
    {
        var added = false;
        foreach (var match in matches)
        {
            if (byValue.TryGetValue(match.Value, out var row))
            {
                row.Add(path, page);
                continue;
            }

            row = new IdentifierRow(match, path, page, showFiles);
            byValue[match.Value] = row;
            rows.Add(row);
            if (visible && (!OnlyValid || row.IsValid))
            {
                VisibleRows.Add(row);
                added = true;
            }
        }

        if (added)
            NotifyRowsChanged();
    }

    // ---------- Hepsini bul ----------

    private bool CanFindAll() => RootFolder is not null;

    /// <summary>Kök klasördeki (alt klasörler dahil) tüm PDF'leri tarar. Tarama sürerken basılırsa durdurur.</summary>
    [RelayCommand(CanExecute = nameof(CanFindAll), AllowConcurrentExecutions = true)]
    private async Task FindAllAsync()
    {
        if (_bulkCts is not null)
        {
            _bulkCts.Cancel();
            return;
        }

        if (RootFolder is not { } root)
            return;

        _bulkRows.Clear();
        _bulkRowsByValue.Clear();
        BulkWarning = "";
        BulkWarningTip = null;
        BulkStatus = "PDF dosyaları aranıyor...";
        if (IsBulkMode)
            RebuildVisibleRows();
        else
            IsBulkMode = true;

        var cts = _bulkCts = new CancellationTokenSource();
        var token = cts.Token;
        IsBulkScanning = true;
        var files = new List<string>();
        var done = 0;
        var failed = new List<string>();
        var noText = new List<string>();
        try
        {
            files = await Task.Run(() => PdfFolderScanner.FindPdfs(root), token);
            if (files.Count == 0)
            {
                BulkStatus = "Bu klasörde PDF dosyası yok.";
                return;
            }

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                BulkStatus = $"Taranıyor... {done + 1}/{files.Count}: {Path.GetFileName(file)}";
                var result = await Task.Run(() => PdfFolderScanner.ScanFile(file, token), token);
                if (result.Error is not null)
                    AppLog.Warning(LogArea, $"Hepsini bul: PDF açılamadı: {file}: {result.Error}");
                token.ThrowIfCancellationRequested();
                if (result.Error is not null)
                    failed.Add($"{Path.GetFileName(file)}: {result.Error}");
                else if (result.HasNoText)
                    noText.Add(Path.GetFileName(file));
                foreach (var group in result.Matches.GroupBy(m => m.Page))
                    AddMatches(_bulkRows, _bulkRowsByValue, group.Select(m => m.Match), file, group.Key, showFiles: true, visible: IsBulkMode);
                done++;
            }

            BulkStatus = $"{_bulkRows.Count} numara bulundu ({files.Count} PDF).";
        }
        catch (OperationCanceledException) when (_bulkCts != cts)
        {
            // "Kapat" ya da yeni tarama: bu taramanın sonucu artık gösterilmiyor.
        }
        catch (OperationCanceledException)
        {
            BulkStatus = $"Durduruldu: {done}/{files.Count} PDF tarandı, {_bulkRows.Count} numara bulundu.";
        }
        catch (Exception ex)
        {
            BulkStatus = $"Tarama yarıda kaldı ({done}/{files.Count} PDF): {AppLog.Describe(ex)}";
            AppErrors.Report(LogArea, "\"Hepsini bul\" taraması hata verdi.", ex);
        }
        finally
        {
            if (_bulkCts == cts)
            {
                _bulkCts = null;
                IsBulkScanning = false;
                SetBulkWarning(failed, noText);
            }

            cts.Dispose();
        }
    }

    private void SetBulkWarning(List<string> failed, List<string> noText)
    {
        var parts = new List<string>();
        if (failed.Count > 0)
            parts.Add($"{failed.Count} PDF açılamadı");
        if (noText.Count > 0)
            parts.Add($"{noText.Count} PDF'te metin katmanı yok (taranmış görüntü olabilir)");
        BulkWarning = parts.Count == 0 ? "" : string.Join(", ", parts) + ". Ayrıntı için üzerinde bekleyin.";
        var lines = failed.Concat(noText.Select(n => $"{n}: metin katmanı yok")).ToList();
        BulkWarningTip = lines.Count == 0
            ? null
            : string.Join(Environment.NewLine, lines.Take(30).Append(lines.Count > 30 ? $"... ve {lines.Count - 30} dosya daha" : "")).TrimEnd();
    }

    /// <summary>Toplu sonuçları kapatıp açık belgenin numaralarına döner.</summary>
    [RelayCommand]
    private void CloseBulk()
    {
        _bulkCts?.Cancel();
        _bulkCts = null;
        IsBulkScanning = false;
        _bulkRows.Clear();
        _bulkRowsByValue.Clear();
        BulkStatus = "";
        BulkWarning = "";
        BulkWarningTip = null;
        IsBulkMode = false;
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
                SetStatus($"{rows.Count} numara panoya kopyalandı.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == 4)
                {
                    SetStatus("Panoya kopyalanamadı, yeniden deneyin.");
                    return;
                }

                await Task.Delay(100);
            }
        }
    }

    private void SetStatus(string text)
    {
        // Toplu tarama sürerken ilerleme yazısını ezmeyelim.
        if (!IsBulkMode)
            DocStatus = text;
        else if (!IsBulkScanning)
            BulkStatus = text;
    }

    // ---------- Müşteri / istihbarat sorgulama ----------

    private IReadOnlyList<IdentifierRow> _selectedIdentifiers = [];

    public QueryConfigViewModel CustomerQuery { get; }

    public QueryConfigViewModel IntelQuery { get; }

    /// <summary>Token'ın kendiliğinden yenilenmesi için giriş istekleri; iki sorgu da bunu kullanır.</summary>
    public SessionConfigViewModel Session { get; }

    private IEnumerable<QueryConfigViewModel> Queries => [CustomerQuery, IntelQuery];

    /// <summary>Sorgu sonuçları penceresini açar. Görünüm tarafından atanır.</summary>
    public Action<QueryRunViewModel>? ShowQueryResults { get; set; }

    /// <summary>Listede seçili numaralar; görünüm seçim değişince bildirir.</summary>
    public void SetSelectedIdentifiers(IEnumerable<IdentifierRow> rows)
    {
        _selectedIdentifiers = rows.ToList();
        UpdateQueryTargets();
    }

    /// <summary>Seçili numaralar (listedeki sırayla); seçim yoksa listedekilerin tümü.</summary>
    private List<IdentifierRow> QueryTargets()
    {
        var selected = VisibleRows.Where(r => _selectedIdentifiers.Contains(r)).ToList();
        return selected.Count > 0 ? selected : VisibleRows.ToList();
    }

    private void UpdateQueryTargets()
    {
        var count = QueryTargets().Count;
        foreach (var query in Queries)
            query.TargetCount = count;
        RunQueryCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunQuery(QueryConfigViewModel? query) => query is { IsReady: true, TargetCount: > 0 };

    [RelayCommand(CanExecute = nameof(CanRunQuery))]
    private void RunQuery(QueryConfigViewModel? query)
    {
        if (query?.Template is not { } template || ShowQueryResults is null)
            return;
        var targets = QueryTargets();
        if (targets.Count == 0)
            return;
        var run = new QueryRunViewModel(query.Title, template, targets.Select(r => (r.Value, r.KindText)),
            query.ResultField, query.EmptyText, Session);
        ShowQueryResults(run);
        _ = run.RunAsync();
    }

    // ---------- Excel'e kaydet ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExcelFileName))]
    [NotifyCanExecuteChangedFor(nameof(SaveToExcelCommand))]
    private string? _excelPath;

    public string ExcelFileName => string.IsNullOrEmpty(ExcelPath) ? "Excel dosyası seçilmedi" : ExcelPath;

    [ObservableProperty] private string _excelMessage = "";
    [ObservableProperty] private bool _excelMessageIsError;

    public ObservableCollection<ExcelFieldViewModel> Fields { get; } = [];

    /// <summary>Alanları düzenleme görünümü açık.</summary>
    [ObservableProperty]
    private bool _isEditingFields;

    public bool HasNoEnabledFields => Fields.All(f => !f.IsEnabled);

    /// <summary>Eski sürümün ayarlarında PDF yolu alan olarak yoktu (hep ilk sütundu); başa eklenir.</summary>
    private static IEnumerable<ExcelFieldDefinition> WithPdfPath(IReadOnlyList<ExcelFieldDefinition> fields) =>
        fields.Any(f => f.Kind == ExcelFieldKind.PdfPath) ? fields : fields.Prepend(ExcelFieldViewModel.PdfPathDefault);

    private void LoadFields(IEnumerable<ExcelFieldDefinition> definitions)
    {
        _loadingFields = true;
        foreach (var field in Fields)
            field.DefinitionChanged -= OnFieldDefinitionChanged;
        Fields.Clear();
        foreach (var definition in definitions)
            AttachField(new ExcelFieldViewModel(definition));
        _loadingFields = false;
        ValidateFields();
    }

    private void AttachField(ExcelFieldViewModel field)
    {
        field.DefinitionChanged += OnFieldDefinitionChanged;
        Fields.Add(field);
    }

    private void OnFieldDefinitionChanged()
    {
        if (_loadingFields)
            return;
        ValidateFields();
        SaveSettings();
    }

    /// <summary>Boş ya da yinelenen adları işaretler: Excel sütunları başlığa göre eşlendiği için ad tek olmalı.</summary>
    private bool ValidateFields()
    {
        var ok = true;
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var field in Fields)
        {
            var name = field.Name.Trim();
            field.NameError = name.Length == 0 ? "Alan adı boş olamaz."
                : !seen.Add(name) ? "Bu adda başka bir alan var."
                : "";
            ok &= field.NameError.Length == 0;
        }

        OnPropertyChanged(nameof(HasNoEnabledFields));
        return ok;
    }

    [RelayCommand]
    private void AddField(ExcelFieldKind kind)
    {
        var baseName = kind switch
        {
            ExcelFieldKind.Date => "Yeni tarih",
            ExcelFieldKind.Choice => "Yeni seçim",
            ExcelFieldKind.Checkbox => "Yeni onay",
            _ => "Yeni alan",
        };
        var name = baseName;
        for (var i = 2; Fields.Any(f => string.Equals(f.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase)); i++)
            name = $"{baseName} {i}";
        AttachField(new ExcelFieldViewModel(new ExcelFieldDefinition(name, kind)));
        IsEditingFields = true;
        OnFieldDefinitionChanged();
    }

    [RelayCommand]
    private void RemoveField(ExcelFieldViewModel? field)
    {
        if (field is not { CanRemove: true })
            return;
        field.DefinitionChanged -= OnFieldDefinitionChanged;
        Fields.Remove(field);
        OnFieldDefinitionChanged();
    }

    [RelayCommand]
    private void MoveFieldUp(ExcelFieldViewModel? field) => MoveField(field, -1);

    [RelayCommand]
    private void MoveFieldDown(ExcelFieldViewModel? field) => MoveField(field, 1);

    private void MoveField(ExcelFieldViewModel? field, int delta)
    {
        if (field is null)
            return;
        var index = Fields.IndexOf(field);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Fields.Count)
            return;
        Fields.Move(index, target);
        OnFieldDefinitionChanged();
    }

    [RelayCommand]
    private void ResetFields()
    {
        LoadFields(ExcelFieldViewModel.Defaults);
        SaveSettings();
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
        if (!ValidateFields())
        {
            IsEditingFields = true;
            ShowExcelMessage("Alan adlarını düzeltin: boş ya da aynı adlı alan olamaz.", isError: true);
            return;
        }

        var enabled = Fields.Where(f => f.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            ShowExcelMessage("Kaydedilecek alan yok. \"Alanları düzenle\"den en az bir alanı açın.", isError: true);
            return;
        }

        var pdfPath = Path.GetFullPath(Document.Path);
        var values = new List<DocumentLogField>();
        foreach (var field in enabled)
        {
            if (!field.TryGetValue(pdfPath, out var value, out var error))
            {
                ShowExcelMessage(error, isError: true);
                return;
            }

            values.Add(value);
        }

        var entry = new DocumentLogEntry(values);
        var excel = ExcelPath;

        _saving = true;
        try
        {
            var row = await Task.Run(() => DocumentLogWriter.Append(excel, entry));
            ShowExcelMessage($"{row}. satır olarak eklendi.", isError: false);
            AppLog.Info(LogArea, $"Excel'e {row}. satır eklendi: {excel}");
            foreach (var field in enabled)
                field.ClearValue();
        }
        catch (Exception ex)
        {
            AppLog.Error(LogArea, $"Excel'e kaydedilemedi: {excel}", ex);
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

    private sealed record Settings(
        string? RootFolder = null,
        string? ExcelPath = null,
        IReadOnlyList<ExcelFieldDefinition>? Fields = null,
        IReadOnlyList<GroupTagDefinition>? Tags = null,
        int CaseMinDigits = 5,
        int CaseMaxDigits = 7,
        bool SingleFileFolders = false,
        string? CustomerQuery = null,
        string? IntelQuery = null,
        string? CustomerQueryField = null,
        // Önceki sürümde istihbarat için alan boş (yanıtın tamamı) kaydediliyordu; yeni varsayılan "value"
        // eski kayıtla ezilmesin diye ayar yeni adla tutulur.
        string? IntelResultField = null,
        // Giriş istekleri ve sorgu istekleri token/çerez/giriş bilgisi taşır: Windows'ta kullanıcıya özel şifreli saklanır.
        string? LoginTokenRequest = null,
        string? LoginSessionRequest = null,
        string? LoginTokenField = null,
        bool LoginAutoRefresh = true);

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WebAutomater", "belge-tarama.json");

    private static Settings LoadSettings()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch (FileNotFoundException)
        {
            return new Settings();
        }
        catch (DirectoryNotFoundException)
        {
            return new Settings();
        }
        catch (Exception ex)
        {
            // Bozuk ayar dosyası açılışı engellemesin; varsayılanlarla devam edilir.
            AppLog.Warning(LogArea, $"Ayarlar okunamadı, varsayılanlar kullanılıyor: {SettingsPath}", ex);
            return new Settings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var settings = new Settings(RootFolder, ExcelPath, Fields.Select(f => f.ToDefinition()).ToList(),
                Tags.Select(t => t.ToDefinition()).ToList(), CaseMinDigits, CaseMaxDigits, SingleFileFolders,
                SecretStore.Protect(NullIfEmpty(CustomerQuery?.RequestText)), SecretStore.Protect(NullIfEmpty(IntelQuery?.RequestText)),
                CustomerQuery?.ResultField ?? "value", IntelQuery?.ResultField ?? "value",
                SecretStore.Protect(NullIfEmpty(Session?.TokenRequestText)), SecretStore.Protect(NullIfEmpty(Session?.SessionRequestText)),
                NullIfEmpty(Session?.TokenField), Session?.AutoRefresh ?? true);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // Ayar kaydedilemezse yalnızca bir sonraki açılışta hatırlanmaz.
            AppLog.Warning(LogArea, "Ayarlar kaydedilemedi.", ex);
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    public void Shutdown()
    {
        _openCts?.Cancel();
        _bulkCts?.Cancel();
        _groupCts?.Cancel();
        Session.Dispose();
        Document?.Dispose();
    }
}
