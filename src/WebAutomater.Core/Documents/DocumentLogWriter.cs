using ClosedXML.Excel;

namespace WebAutomater.Core.Documents;

public sealed record DocumentLogEntry(
    string PdfPath,
    DateTime? DocumentDate,
    string Subject,
    string DocumentNumber,
    string DestisNumber,
    string Description);

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
    public static readonly string[] Headers =
        ["PDF Dosya Yolu", "Belge Tarihi", "Belge Konusu", "Belge Numarası", "Destis Numarası", "Açıklama"];

    /// <summary>
    /// Satırı ilk çalışma sayfasının sonuna ekler; dosya yoksa başlıklarla oluşturur.
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
        if (last == 0)
        {
            WriteHeaders(sheet);
            last = 1;
        }

        var row = last + 1;
        // Metinler metin olarak yazılır: "0012" gibi numaralar sıfırını kaybetmez, "=" formüle dönüşmez.
        sheet.Cell(row, 1).SetValue(entry.PdfPath);
        if (entry.DocumentDate is { } date)
        {
            sheet.Cell(row, 2).SetValue(date.Date);
            sheet.Cell(row, 2).Style.DateFormat.Format = "dd.mm.yyyy";
        }

        SetText(sheet.Cell(row, 3), entry.Subject);
        SetText(sheet.Cell(row, 4), entry.DocumentNumber);
        SetText(sheet.Cell(row, 5), entry.DestisNumber);
        SetText(sheet.Cell(row, 6), entry.Description);

        SaveAtomically(workbook, excelPath);
        return row;
    }

    private static void SetText(IXLCell cell, string value)
    {
        cell.Style.NumberFormat.Format = "@";
        cell.SetValue(value);
    }

    private static void WriteHeaders(IXLWorksheet sheet)
    {
        int[] widths = [70, 14, 40, 20, 20, 50];
        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.SetValue(Headers[i]);
            cell.Style.Font.Bold = true;
            sheet.Column(i + 1).Width = widths[i];
        }

        sheet.SheetView.FreezeRows(1);
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
