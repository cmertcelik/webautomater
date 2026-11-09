using CommunityToolkit.Mvvm.ComponentModel;
using WebAutomater.Core.Documents;

namespace WebAutomater.Gui.ViewModels;

/// <summary>Ayar dosyasında saklanan etiket.</summary>
public sealed record GroupTagDefinition(string Name, IReadOnlyList<string> Keywords);

/// <summary>Etiket ayar menüsündeki bir etiket.</summary>
public sealed partial class GroupTagViewModel : ObservableObject
{
    public GroupTagViewModel(GroupTagDefinition definition)
    {
        _name = definition.Name;
        _keywordsText = string.Join(", ", definition.Keywords);
    }

    /// <summary>Ad ya da kelimeler değişti: ayarlar kaydedilmeli.</summary>
    public event Action? DefinitionChanged;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _keywordsText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = "";

    public bool HasError => Error.Length > 0;

    public IReadOnlyList<string> Keywords => GroupingTag.ParseKeywords(KeywordsText);

    partial void OnNameChanged(string value) => DefinitionChanged?.Invoke();

    partial void OnKeywordsTextChanged(string value) => DefinitionChanged?.Invoke();

    public GroupTagDefinition ToDefinition() => new(Name.Trim(), Keywords);

    public GroupingTag ToTag() => new(Name.Trim(), Keywords);
}

public enum PreviewRowKind
{
    Folder,
    Label,
    File,
    Section,
}

/// <summary>Önizleme listesinin bir satırı. Liste düz ve sanallaştırılmıştır: binlerce dosyada da donmaz.</summary>
public sealed record GroupingPreviewRow(PreviewRowKind Kind, string Text, string Detail = "", string Badge = "", string? FullPath = null)
{
    public bool IsFolder => Kind == PreviewRowKind.Folder;

    public bool IsLabel => Kind == PreviewRowKind.Label;

    public bool IsFile => Kind == PreviewRowKind.File;

    public bool IsSection => Kind == PreviewRowKind.Section;

    public bool HasBadge => Badge.Length > 0;

    public bool HasDetail => Detail.Length > 0;
}

/// <summary>"Otomatik grupla" sonrası, dosyalar taşınmadan önce gösterilen özet.</summary>
public sealed class GroupingPreviewViewModel
{
    private const int MaxUnchangedRows = 2000;

    public GroupingPreviewViewModel(GroupingPlan plan, IReadOnlyList<string> warnings)
    {
        Plan = plan;
        Warnings = warnings;
        var rows = new List<GroupingPreviewRow>();
        foreach (var folder in plan.Folders)
        {
            var finalName = Path.GetFileName(folder.FinalFolder);
            var title = folder.Renames ? $"{Path.GetFileName(folder.Group.Folder!.Path)}  →  {finalName}" : finalName;
            var badge = folder.IsNew ? "yeni klasör" : folder.Renames ? "etiket eklenecek" : "";
            var existing = folder.Group.Folder?.Files.Count ?? 0;
            var detail = existing > 0
                ? $"{folder.Group.IncomingFiles.Count} dosya taşınacak · klasörde {existing} dosya var"
                : $"{folder.Group.IncomingFiles.Count} dosya taşınacak";
            rows.Add(new GroupingPreviewRow(PreviewRowKind.Folder, title, detail, badge, folder.FinalFolder));
            foreach (var label in folder.NewLabels)
                rows.Add(new GroupingPreviewRow(PreviewRowKind.Label, $"{label.TagName}: \"{label.Keyword}\"",
                    $"{Path.GetFileName(label.File)} dosyasında geçiyor", FullPath: label.File));
            foreach (var file in folder.Group.IncomingFiles)
                rows.Add(new GroupingPreviewRow(PreviewRowKind.File, Path.GetFileName(file), FullPath: file));
        }

        if (plan.Unchanged.Count > 0)
        {
            rows.Add(new GroupingPreviewRow(PreviewRowKind.Section, $"Yerinde kalacak dosyalar ({plan.Unchanged.Count})"));
            rows.AddRange(plan.Unchanged.Take(MaxUnchangedRows)
                .Select(u => new GroupingPreviewRow(PreviewRowKind.File, Path.GetFileName(u.Path), u.Reason, FullPath: u.Path)));
            if (plan.Unchanged.Count > MaxUnchangedRows)
                rows.Add(new GroupingPreviewRow(PreviewRowKind.File, $"... ve {plan.Unchanged.Count - MaxUnchangedRows} dosya daha"));
        }

        Rows = rows;
    }

    public GroupingPlan Plan { get; }

    public IReadOnlyList<GroupingPreviewRow> Rows { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public string WarningsTitle => $"{Warnings.Count} dosyanın içi okunamadı; bu dosyalar yalnızca adlarındaki numaraya göre gruplanır";

    public string WarningsText => string.Join(Environment.NewLine, Warnings.Take(200)) +
                                  (Warnings.Count > 200 ? $"{Environment.NewLine}... ve {Warnings.Count - 200} dosya daha" : "");

    public bool HasMoves => Plan.HasChanges;

    public string RootText => $"Klasör: {Plan.Root}";

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            var created = Plan.Folders.Count(f => f.IsNew);
            if (Plan.MoveCount > 0)
                parts.Add($"{Plan.MoveCount} dosya taşınacak");
            if (created > 0)
                parts.Add($"{created} numara klasörü oluşturulacak");
            if (Plan.RenameCount > 0)
                parts.Add($"{Plan.RenameCount} klasörün adına etiket eklenecek");
            if (Plan.Unchanged.Count > 0)
                parts.Add($"{Plan.Unchanged.Count} dosya yerinde kalacak");
            return string.Join(", ", parts) + ". Aynı adlı dosya varsa üzerine yazılmaz, adına sayı eklenir.";
        }
    }

    public string ApplyText => Plan.MoveCount > 0 ? $"Uygula ({Plan.MoveCount} dosya)" : "Uygula";
}
