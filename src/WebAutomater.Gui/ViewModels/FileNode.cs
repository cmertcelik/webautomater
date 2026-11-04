using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO.Enumeration;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WebAutomater.Gui.ViewModels;

/// <summary>
/// Düzleştirilmiş ağacın bir satırı. Ağaç sanallaştırılmış tek bir liste olarak gösterilir:
/// binlerce dosya içeren klasörler de hızlı açılır, alt klasörler yalnızca açılınca okunur.
/// </summary>
public sealed partial class FileNode(string name, string fullPath, bool isDirectory, int depth) : ObservableObject
{
    public string Name { get; } = name;

    public string FullPath { get; } = fullPath;

    public bool IsDirectory { get; } = isDirectory;

    public bool IsFile => !IsDirectory;

    public int Depth { get; } = depth;

    public Thickness Indent => new(Depth * 14, 0, 0, 0);

    public bool IsPdf { get; } = !isDirectory && name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    public double RowOpacity => IsDirectory || IsPdf ? 1 : 0.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronAngle))]
    private bool _isExpanded;

    /// <summary>Açıldı ve içinde hiçbir şey yok: ok gizlenir. Boş olup olmadığını önceden sormak ağ sürücülerinde yavaş olurdu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExpand))]
    private bool _isEmpty;

    public bool CanExpand => IsDirectory && !IsEmpty;

    public double ChevronAngle => IsExpanded ? 90 : 0;

    /// <summary>Sürüklenen öğe bu klasöre bırakılacak: satır vurgulanır.</summary>
    [ObservableProperty]
    private bool _isDropTarget;
}

public static class FileTree
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true, // Erişim izni olmayan klasörler listeyi bozmasın.
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        ReturnSpecialDirectories = false,
        RecurseSubdirectories = false,
    };

    // "Belge2" "Belge10"dan önce gelsin.
    private static readonly IComparer<string> NameComparer =
        new WebAutomater.Core.Text.NaturalStringComparer(CultureInfo.GetCultureInfo("tr-TR"));

    /// <summary>Bir klasörün doğrudan içeriği: önce klasörler, sonra dosyalar. Kullanıcı arayüzü dışında çağrılmalı.</summary>
    public static List<FileNode> ListChildren(string directory, int depth)
    {
        var entries = new FileSystemEnumerable<(string Name, string Path, bool IsDir)>(
            directory,
            (ref FileSystemEntry e) => (e.FileName.ToString(), e.ToFullPath(), e.IsDirectory),
            Options);

        return entries
            .OrderBy(e => e.IsDir ? 0 : 1)
            .ThenBy(e => e.Name, NameComparer)
            .Select(e => new FileNode(e.Name, e.Path, e.IsDir, depth))
            .ToList();
    }
}

/// <summary>Tek seferde çok öğe ekleyip silebilen koleksiyon; her öğe için ayrı arayüz güncellemesi yapmaz.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
            return;
        CheckReentrancy();
        for (var i = 0; i < items.Count; i++)
            Items.Insert(index + i, items[i]);
        Notify(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, (IList)items.ToList(), index));
    }

    public void RemoveRangeAt(int index, int count)
    {
        if (count == 0)
            return;
        CheckReentrancy();
        var removed = new List<T>(count);
        for (var i = 0; i < count; i++)
            removed.Add(Items[index + i]);
        ((List<T>)Items).RemoveRange(index, count);
        Notify(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        Notify(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void Notify(NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(args);
    }
}
