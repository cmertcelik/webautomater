using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;

namespace WebAutomater.Core.Data;

public static class DataSourceLoader
{
    public static readonly string[] SupportedExtensions = [".xlsx", ".xlsm", ".csv", ".tsv", ".txt", ".json"];

    static DataSourceLoader()
    {
        // Türkçe Excel'den kaydedilmiş CSV'ler genellikle Windows-1254 kodlamasındadır.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static DataTableSource Load(string path, string? sheet = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Veri dosyası bulunamadı: {path}", path);

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xlsx" or ".xlsm" => LoadExcel(path, sheet),
            ".json" => LoadJson(path),
            ".csv" or ".tsv" or ".txt" => LoadCsv(path),
            ".xls" => throw new NotSupportedException(
                "Eski .xls biçimi desteklenmiyor. Dosyayı Excel'de .xlsx olarak kaydedip tekrar deneyin."),
            var ext => throw new NotSupportedException($"Desteklenmeyen veri dosyası türü: {ext}"),
        };
    }

    public static DataTableSource LoadCsv(string path)
    {
        var text = ReadTextWithEncodingDetection(path);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            DetectDelimiterValues = [",", ";", "\t", "|"],
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        };
        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, config);

        var builder = new TableBuilder();
        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is null)
            return builder.Build(path);

        var columns = builder.SetHeaders(csv.HeaderRecord);
        while (csv.Read())
        {
            var row = new Dictionary<string, string>();
            for (var i = 0; i < columns.Count; i++)
                row[columns[i]] = csv.TryGetField<string>(i, out var v) ? v ?? "" : "";
            builder.AddRow(row);
        }
        return builder.Build(path);
    }

    public static DataTableSource LoadExcel(string path, string? sheet = null)
    {
        // Dosya Excel'de açıkken de okunabilsin diye paylaşımlı açılır.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var workbook = new XLWorkbook(stream);
        var sheets = workbook.Worksheets.Select(w => w.Name).ToList();
        var ws = sheet is null
            ? workbook.Worksheets.First()
            : workbook.Worksheets.FirstOrDefault(w => string.Equals(w.Name, sheet, StringComparison.OrdinalIgnoreCase))
              ?? throw new ArgumentException($"'{sheet}' adlı sayfa bulunamadı. Mevcut sayfalar: {string.Join(", ", sheets)}");

        var builder = new TableBuilder();
        var range = ws.RangeUsed();
        if (range is null)
            return builder.Build(path, sheets, ws.Name);

        var firstRow = range.FirstRow().RowNumber();
        var lastRow = range.LastRow().RowNumber();
        var firstCol = range.FirstColumn().ColumnNumber();
        var lastCol = range.LastColumn().ColumnNumber();

        var columns = builder.SetHeaders(
            Enumerable.Range(firstCol, lastCol - firstCol + 1).Select(c => ws.Cell(firstRow, c).GetFormattedString()));

        for (var r = firstRow + 1; r <= lastRow; r++)
        {
            var row = new Dictionary<string, string>();
            for (var c = firstCol; c <= lastCol; c++)
                row[columns[c - firstCol]] = ws.Cell(r, c).GetFormattedString().Trim();
            builder.AddRow(row);
        }
        return builder.Build(path, sheets, ws.Name);
    }

    /// <summary>
    /// Nesne dizisi bekler. Kök bir nesneyse içindeki ilk nesne dizisi kullanılır; tek nesne tek satırdır.
    /// İç içe alanlar "adres.sehir" biçiminde düzleştirilir.
    /// </summary>
    public static DataTableSource LoadJson(string path)
    {
        using var doc = JsonDocument.Parse(ReadTextWithEncodingDetection(path),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

        var items = FindRecords(doc.RootElement)
            ?? throw new InvalidDataException("JSON içinde nesne dizisi bulunamadı. Örnek: [{\"isim\": \"Ali\"}, ...]");

        var builder = new TableBuilder();
        foreach (var item in items)
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Flatten(item, "", row);
            foreach (var key in row.Keys)
                builder.EnsureColumn(key);
            builder.AddRow(row);
        }
        return builder.Build(path);
    }

    private static List<JsonElement>? FindRecords(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array
                && prop.Value.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.Object))
                return prop.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        }
        return [root];
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> row)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    Flatten(prop.Value, prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}", row);
                break;
            case JsonValueKind.Array:
                var scalars = element.EnumerateArray().All(e => e.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array));
                row[prefix] = scalars
                    ? string.Join("|", element.EnumerateArray().Select(ScalarText))
                    : element.GetRawText();
                break;
            default:
                row[prefix] = ScalarText(element);
                break;
        }
    }

    private static string ScalarText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => e.GetRawText(),
    };

    private static string ReadTextWithEncodingDetection(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1254).GetString(bytes);
        }
    }
}
