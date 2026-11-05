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

/// <summary>Önizlemede bir hedef klasör ve oraya gidecek dosyalar.</summary>
public sealed record GroupingPreviewFolder(string Title, bool IsNew, IReadOnlyList<GroupingPreviewFile> Files)
{
    public string CountText => $"{Files.Count} dosya";
}

public sealed record GroupingPreviewFile(string Name, string Detail, string FullPath);

/// <summary>"Otomatik grupla" sonrası, dosyalar taşınmadan önce gösterilen özet.</summary>
public sealed class GroupingPreviewViewModel
{
    public GroupingPreviewViewModel(GroupingPlan plan, IReadOnlyList<string> warnings)
    {
        Plan = plan;
        Folders = plan.Moves
            .GroupBy(m => m.DestinationFolder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupingPreviewFolder(
                Relative(plan.Root, g.Key),
                !Directory.Exists(g.Key),
                g.Select(m => new GroupingPreviewFile(Path.GetFileName(m.Source), $"{Relative(plan.Root, Path.GetDirectoryName(m.Source)!)} · {m.Reason}", m.Source)).ToList()))
            .ToList();
        Unchanged = plan.Unchanged
            .Select(u => new GroupingPreviewFile(Path.GetFileName(u.Path), $"{Relative(plan.Root, Path.GetDirectoryName(u.Path)!)} · {u.Reason}", u.Path))
            .ToList();
        Warnings = warnings;
    }

    public GroupingPlan Plan { get; }

    public IReadOnlyList<GroupingPreviewFolder> Folders { get; }

    public IReadOnlyList<GroupingPreviewFile> Unchanged { get; }

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasMoves => Plan.Moves.Count > 0;

    public bool HasUnchanged => Unchanged.Count > 0;

    public string RootText => $"Klasör: {Plan.Root}";

    public string Summary => Plan.Moves.Count == 0
        ? "Taşınacak dosya yok. Etiketlerin kelimelerini ya da dosya numarası ayarını kontrol edin."
        : $"{Plan.Moves.Count} dosya {Folders.Count} klasöre taşınacak" +
          (Unchanged.Count > 0 ? $", {Unchanged.Count} dosya yerinde kalacak." : ".") +
          " Aynı adlı dosya varsa üzerine yazılmaz, adına sayı eklenir.";

    public string UnchangedTitle => $"Yerinde kalacak dosyalar ({Unchanged.Count})";

    public string ApplyText => $"{Plan.Moves.Count} dosyayı taşı";

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? "(ana klasör)" : relative.Replace(Path.DirectorySeparatorChar, '/').Replace("/", " / ");
    }
}
