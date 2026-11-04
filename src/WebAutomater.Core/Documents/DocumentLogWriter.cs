using ClosedXML.Excel;

namespace WebAutomater.Core.Documents;

/// <summary>
/// Excel'e yazılacak bir alan. <see cref="Value"/>: metin (<see cref="string"/>), tarih (<see cref="DateTime"/>),
/// onay (<see cref="bool"/>) ya da boş (<c>null</c>). <see cref="Width"/>: sütun yeni açılırsa genişliği.
/// </summary>
public sealed record DocumentLogField(string Header, object? Value, double? Width = null);

/// <summary>Excel'e eklenecek bir satır: kullanıcının seçtiği alanlar, formdaki sırayla.</summary>
public sealed record DocumentLogEntry(IReadOnlyList<DocumentLogField> Fields);

public static class DocumentDate
{
    private static readonly string[] Formats =
        ["d.M.yyyy", "d/M/yyyy", "d-M-yyyy", "yyyy-M-d", "ddMMyyyy", "d.M.yy", "d/M/yy"];

    /// <summary>Elle yazılan tarihi okur; biçimler gün önce olacak şekilde sabittir, bilgisayarın diline bağlı değildir.</summary>
    public static bool TryParse(string text, out DateTime date) =>
        DateTime.TryParseExact(text.Trim(), Formats, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date);
}

/// <summary>Excel dosyası başka bir uygulamada açık olduğu için yazılamadı.</summary>
public sealed class ExcelFileLockedException(string path, Exception? inner = null)
    : IOException($"\"{Path.GetFileName(path)}\" başka bir uygulamada açık. Excel'de kapatıp yeniden deneyin.", inner);

/// <summary>Belge bilgilerini bir Excel dosyasına satır olarak ekler.</summary>
public static class DocumentLogWriter
{
    /// <summary>
    /// Satırı ilk çalışma sayfasının sonuna ekler; dosya yoksa başlıklarla oluşturur.
    /// Değerler ilk satırdaki başlığa göre ilgili sütuna yazılır (büyük/küçük harf ve baştaki/sondaki boşluk önemsiz).
    /// Başlığı olmayan alan için ilk satırın sonuna yeni sütun açılır; böylece alan sırası değişse ya da
    /// yeni alan eklense de eski kayıtlar yerinde kalır.
    /// Önce geçici dosyaya yazılıp sonra yer değiştirilir: yazma yarıda kalırsa mevcut dosya bozulmaz.
    /// </summary>
    /// <returns>Eklenen satırın numarası.</returns>
    public static int Append(string excelPath, DocumentLogEntry entry)
    {
        var exists = File.Exists(excelPath);
        if (exists)
            EnsureNotLocked(excelPath);

        // ClosedXML kaydederken kaynağa yeniden başvurur; akış çalışma kitabından uzun yaşamalı.
        using var source = new MemoryStream();
        using var workbook = exists ? OpenWorkbook(excelPath, source) : new XLWorkbook();
        var sheet = workbook.Worksheets.FirstOrDefault() ?? workbook.AddWorksheet("Belgeler");

        var last = sheet.LastRowUsed()?.RowNumber() ?? 0;
        var columns = ReadHeaders(sheet);
        var row = Math.Max(last, 1) + 1;

        foreach (var field in entry.Fields)
            Write(sheet, columns, row, field);
        if (last == 0)
            sheet.SheetView.FreezeRows(1);

        SaveAtomically(workbook, excelPath);
        return row;
    }

    private static Dictionary<string, int> ReadHeaders(IXLWorksheet sheet)
    {
        var columns = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var lastColumn = sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (var c = 1; c <= lastColumn; c++)
        {
            var header = sheet.Cell(1, c).GetString().Trim();
            if (header.Length > 0)
                columns.TryAdd(header, c);
        }

        return columns;
    }

    private static void Write(IXLWorksheet sheet, Dictionary<string, int> columns, int row, DocumentLogField field)
    {
        var header = field.Header.Trim();
        if (!columns.TryGetValue(header, out var column))
        {
            column = (sheet.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0) + 1;
            columns[header] = column;
            var headerCell = sheet.Cell(1, column);
            headerCell.SetValue(header);
            headerCell.Style.Font.Bold = true;
            sheet.Column(column).Width = field.Width ?? (field.Value is DateTime or bool ? 14 : 30);
        }

        var cell = sheet.Cell(row, column);
        switch (field.Value)
        {
            case DateTime date:
                cell.SetValue(date.Date);
                cell.Style.DateFormat.Format = "dd.mm.yyyy";
                break;
            case bool flag:
                cell.SetValue(flag ? "Evet" : "Hayır");
                break;
            case string text:
                // Metinler metin olarak yazılır: "0012" gibi numaralar sıfırını kaybetmez, "=" formüle dönüşmez.
                cell.Style.NumberFormat.Format = "@";
                cell.SetValue(text);
                break;
        }
    }

    private static XLWorkbook OpenWorkbook(string path, MemoryStream memory)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            stream.CopyTo(memory);
        memory.Position = 0;
        return new XLWorkbook(memory);
    }

    private static void EnsureNotLocked(string path)
    {
        // Windows'ta Excel dosyayı açıkken kilitler, bu deneme güvenilirdir.
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new ExcelFileLockedException(path, ex);
        }

        // Windows dışında kilit yoktur; Excel'in bıraktığı ~$ dosyasına bakılır. Windows'ta bakılmaz:
        // Excel çökerse bu dosya artakalır ve kaydı boş yere engellerdi.
        if (OperatingSystem.IsWindows())
            return;

        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileName(path);
        // Excel adın ilk iki harfini "~$" ile değiştirir.
        if (name.Length > 2 && File.Exists(Path.Combine(dir, "~$" + name[2..])))
            throw new ExcelFileLockedException(path);
    }

    private static void SaveAtomically(XLWorkbook workbook, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        // Uzantı korunmalı: ClosedXML biçimi (.xlsx/.xlsm) dosya adından belirler.
        var temp = Path.Combine(dir, $".{Guid.NewGuid():N}.tmp{Path.GetExtension(path)}");
        try
        {
            workbook.SaveAs(temp);
            try
            {
                if (File.Exists(path))
                    File.Replace(temp, path, null, ignoreMetadataErrors: true);
                else
                    File.Move(temp, path);
            }
            catch (PlatformNotSupportedException)
            {
                // Bazı ağ sürücülerinde File.Replace yok.
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException && IsSharingViolation(ex))
        {
            throw new ExcelFileLockedException(path, ex);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ERROR_SHARING_VIOLATION = 32, ERROR_LOCK_VIOLATION = 33, ERROR_UNABLE_TO_REMOVE_REPLACED = 1175
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33 or 1175;
}
