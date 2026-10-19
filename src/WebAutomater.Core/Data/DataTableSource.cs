namespace WebAutomater.Core.Data;

/// <summary>
/// Excel, CSV veya JSON'dan okunmuş tablo. Kolon adları büyük/küçük harf duyarsızdır.
/// </summary>
public sealed class DataTableSource
{
    public DataTableSource(string path, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, string>> rows,
        IReadOnlyList<string>? sheets = null, string? sheet = null)
    {
        Path = path;
        Columns = columns;
        Rows = rows;
        Sheets = sheets ?? [];
        Sheet = sheet;
    }

    public string Path { get; }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; }

    /// <summary>Excel dosyalarında çalışma sayfalarının adları.</summary>
    public IReadOnlyList<string> Sheets { get; }

    public string? Sheet { get; }

    public bool HasColumn(string name) => Columns.Contains(name, StringComparer.OrdinalIgnoreCase);
}

internal sealed class TableBuilder
{
    private readonly List<string> _columns = [];
    private readonly List<IReadOnlyDictionary<string, string>> _rows = [];

    /// <summary>Başlıkları temizler: boş olanlara ad verir, tekrar edenleri numaralandırır.</summary>
    public IReadOnlyList<string> SetHeaders(IEnumerable<string?> headers)
    {
        var result = new List<string>();
        var index = 0;
        foreach (var raw in headers)
        {
            index++;
            var name = string.IsNullOrWhiteSpace(raw) ? $"Kolon{index}" : raw.Trim();
            var unique = name;
            for (var n = 2; result.Contains(unique, StringComparer.OrdinalIgnoreCase); n++)
                unique = $"{name}_{n}";
            result.Add(unique);
        }
        _columns.Clear();
        _columns.AddRange(result);
        return result;
    }

    public void EnsureColumn(string name)
    {
        if (!_columns.Contains(name, StringComparer.OrdinalIgnoreCase))
            _columns.Add(name);
    }

    public void AddRow(IDictionary<string, string> values)
    {
        if (values.Values.All(string.IsNullOrWhiteSpace))
            return;
        _rows.Add(new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase));
    }

    public DataTableSource Build(string path, IReadOnlyList<string>? sheets = null, string? sheet = null) =>
        new(path, _columns.ToList(), _rows, sheets, sheet);
}
