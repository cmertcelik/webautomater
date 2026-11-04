using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using WebAutomater.Core.Documents;

namespace WebAutomater.Gui.ViewModels;

[JsonConverter(typeof(JsonStringEnumConverter<ExcelFieldKind>))]
public enum ExcelFieldKind
{
    Text,
    Date,
    Choice,
    Checkbox,

    /// <summary>Açık PDF'in tam yolu; elle girilmez, kendiliğinden doldurulur. Silinemez, kapatılabilir.</summary>
    PdfPath,
}

/// <summary>Ayar dosyasında saklanan alan tanımı.</summary>
public sealed record ExcelFieldDefinition(
    string Name,
    ExcelFieldKind Kind,
    bool IsEnabled = true,
    bool Multiline = false,
    IReadOnlyList<string>? Options = null);

/// <summary>Excel'e kaydet panelindeki bir alan: hem tanımı (ad, tür, seçenekler) hem de girilen değer.</summary>
public sealed partial class ExcelFieldViewModel : ObservableObject
{
    public ExcelFieldViewModel(ExcelFieldDefinition definition)
    {
        Kind = definition.Kind;
        _name = definition.Name;
        _isEnabled = definition.IsEnabled;
        _multiline = definition.Multiline;
        _optionsText = string.Join(Environment.NewLine, definition.Options ?? []);
        Options = ParseOptions(_optionsText);
    }

    /// <summary>Tanım değişti (ad, açık/kapalı, seçenekler); ayarlar kaydedilmeli.</summary>
    public event Action? DefinitionChanged;

    public ExcelFieldKind Kind { get; }

    public bool IsText => Kind == ExcelFieldKind.Text;
    public bool IsDate => Kind == ExcelFieldKind.Date;
    public bool IsChoice => Kind == ExcelFieldKind.Choice;
    public bool IsCheckbox => Kind == ExcelFieldKind.Checkbox;
    public bool IsPdfPath => Kind == ExcelFieldKind.PdfPath;

    /// <summary>PDF yolu alanı silinemez (yeniden eklenemeyeceği için); yalnızca kapatılır.</summary>
    public bool CanRemove => !IsPdfPath;

    public string KindText => Kind switch
    {
        ExcelFieldKind.Date => "Tarih",
        ExcelFieldKind.Choice => "Seçim listesi",
        ExcelFieldKind.Checkbox => "Onay kutusu",
        ExcelFieldKind.PdfPath => "PDF dosya yolu (kendiliğinden doldurulur)",
        _ => "Metin",
    };

    // ---------- Tanım ----------

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _multiline;

    /// <summary>Seçim listesi seçenekleri, her satıra bir tane.</summary>
    [ObservableProperty]
    private string _optionsText;

    [ObservableProperty]
    private IReadOnlyList<string> _options;

    [ObservableProperty]
    private string _nameError = "";

    partial void OnNameChanged(string value) => DefinitionChanged?.Invoke();
    partial void OnIsEnabledChanged(bool value) => DefinitionChanged?.Invoke();
    partial void OnMultilineChanged(bool value) => DefinitionChanged?.Invoke();

    partial void OnOptionsTextChanged(string value)
    {
        var selected = SelectedOption;
        Options = ParseOptions(value);
        // Seçili seçenek listeden çıkarıldıysa seçimi kaldır.
        SelectedOption = selected is not null && Options.Contains(selected) ? selected : null;
        DefinitionChanged?.Invoke();
    }

    private static List<string> ParseOptions(string text) =>
        text.Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();

    public ExcelFieldDefinition ToDefinition() =>
        new(Name.Trim(), Kind, IsEnabled, Multiline, IsChoice ? Options : null);

    // ---------- Değer ----------

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private DateTime? _pickedDate;
    [ObservableProperty] private string? _selectedOption;
    [ObservableProperty] private bool _isChecked;

    partial void OnPickedDateChanged(DateTime? value)
    {
        if (value is { } date)
            Text = date.ToString("dd.MM.yyyy");
    }

    /// <summary>Girilen değeri Excel alanına çevirir. Tarih anlaşılamazsa hata metni döner.</summary>
    public bool TryGetValue(string pdfPath, out DocumentLogField field, out string error)
    {
        error = "";
        var header = Name.Trim();
        switch (Kind)
        {
            case ExcelFieldKind.PdfPath:
                field = new DocumentLogField(header, pdfPath, Width: 70);
                return true;
            case ExcelFieldKind.Date:
                if (string.IsNullOrWhiteSpace(Text))
                {
                    field = new DocumentLogField(header, null);
                    return true;
                }

                if (DocumentDate.TryParse(Text, out var date))
                {
                    field = new DocumentLogField(header, date);
                    return true;
                }

                field = new DocumentLogField(header, null);
                error = $"\"{header}\" tarihi anlaşılamadı. Örnek: 31.12.2025";
                return false;
            case ExcelFieldKind.Choice:
                field = new DocumentLogField(header, SelectedOption ?? "");
                return true;
            case ExcelFieldKind.Checkbox:
                field = new DocumentLogField(header, IsChecked);
                return true;
            default:
                field = new DocumentLogField(header, Text.Trim());
                return true;
        }
    }

    public void ClearValue()
    {
        Text = "";
        PickedDate = null;
        SelectedOption = null;
        IsChecked = false;
    }

    public static ExcelFieldDefinition PdfPathDefault { get; } = new("PDF Dosya Yolu", ExcelFieldKind.PdfPath);

    public static IReadOnlyList<ExcelFieldDefinition> Defaults { get; } =
    [
        PdfPathDefault,
        new("Belge Tarihi", ExcelFieldKind.Date),
        new("Belge Konusu", ExcelFieldKind.Text),
        new("Belge Numarası", ExcelFieldKind.Text),
        new("Destis Numarası", ExcelFieldKind.Text),
        new("Açıklama", ExcelFieldKind.Text, Multiline: true),
    ];
}
