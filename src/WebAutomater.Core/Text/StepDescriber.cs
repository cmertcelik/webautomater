using WebAutomater.Core.Models;

namespace WebAutomater.Core.Text;

/// <summary>Adımları teknik olmayan kullanıcılar için okunabilir Türkçe metne çevirir.</summary>
public static class StepDescriber
{
    public static string Action(StepType type) => type switch
    {
        StepType.Navigate => "Sayfaya git",
        StepType.Click => "Tıkla",
        StepType.DoubleClick => "Çift tıkla",
        StepType.Fill => "Yaz",
        StepType.Press => "Tuşa bas",
        StepType.Select => "Seç",
        StepType.Check => "İşaretle",
        StepType.Uncheck => "İşareti kaldır",
        StepType.Upload => "Dosya yükle",
        StepType.Hover => "Üzerine gel",
        StepType.Wait => "Bekle",
        StepType.WaitForText => "Yazıyı bekle",
        StepType.WaitForElement => "Öğeyi bekle",
        _ => type.ToString(),
    };

    public static string Kind(string? kind) => kind switch
    {
        "button" => "buton",
        "link" => "bağlantı",
        "textbox" or "searchbox" => "metin alanı",
        "password" => "şifre alanı",
        "spinbutton" => "sayı alanı",
        "combobox" or "listbox" => "açılır liste",
        "checkbox" => "onay kutusu",
        "radio" => "seçenek düğmesi",
        "switch" => "anahtar",
        "option" => "seçenek",
        "tab" => "sekme",
        "menuitem" or "menuitemcheckbox" or "menuitemradio" => "menü öğesi",
        "img" or "svg" => "görsel",
        "heading" => "başlık",
        "listitem" => "liste öğesi",
        "file" => "dosya alanı",
        null or "" => "öğe",
        _ => "öğe",
    };

    /// <summary>Örn: "Giriş Yap (buton)".</summary>
    public static string Target(Step step)
    {
        if (step.Type is StepType.Navigate or StepType.Wait or StepType.WaitForText)
            return "";
        if (step.Selectors.Count == 0 && string.IsNullOrEmpty(step.TargetName))
            return step.Type == StepType.Press ? "sayfa" : "";
        var name = string.IsNullOrWhiteSpace(step.TargetName) ? step.Selectors.FirstOrDefault() ?? "?" : step.TargetName;
        return $"{name} ({Kind(step.TargetKind)})";
    }

    /// <summary>Değer sütununda gösterilecek metin; şifreler maskelenir.</summary>
    public static string Value(Step step, string? resolved = null)
    {
        var value = resolved ?? step.DisplayValue ?? step.Value ?? "";
        if (resolved is null && !string.IsNullOrWhiteSpace(step.Parameter))
            return $"[{step.Parameter}]";
        return step.Type switch
        {
            StepType.Fill when step.TargetKind == "password" => new string('•', Math.Min(Math.Max(value.Length, 4), 12)),
            StepType.Wait => int.TryParse(value, out var ms) ? $"{ms / 1000.0:0.#} sn" : value,
            StepType.Check or StepType.Uncheck when string.IsNullOrWhiteSpace(step.Parameter) => "",
            StepType.Click or StepType.DoubleClick or StepType.Hover or StepType.WaitForElement
                when string.IsNullOrWhiteSpace(step.Parameter) => "",
            _ => value,
        };
    }

    public static string Describe(Step step, string? resolved = null)
    {
        var value = Value(step, resolved);
        if (step.Type is StepType.Click or StepType.DoubleClick or StepType.Hover && !string.IsNullOrWhiteSpace(step.Parameter))
            return $"{Action(step.Type)}: metni \"{value}\" olan öğe";

        var text = Action(step.Type);
        var target = Target(step);
        if (target.Length > 0)
            text += ": " + target;
        if (value.Length > 0)
            text += (target.Length > 0 ? " → " : ": ") + value;
        return text;
    }

    /// <summary>Bekleme adımı oluşturur: seçili adımın öğesi görünene kadar.</summary>
    public static Step WaitForElementOf(Step target, int timeoutMs) => new()
    {
        Type = StepType.WaitForElement,
        Selectors = [.. target.Selectors],
        TargetName = target.TargetName,
        TargetKind = target.TargetKind,
        PageIndex = target.PageIndex,
        FrameName = target.FrameName,
        FrameUrl = target.FrameUrl,
        TimeoutMs = timeoutMs,
    };

    /// <summary>Hangi adım türlerinin veri kaynağına bağlanabileceği.</summary>
    public static bool IsParameterizable(StepType type) => type is
        StepType.Fill or StepType.Select or StepType.Check or StepType.Uncheck or StepType.Navigate
        or StepType.Click or StepType.DoubleClick or StepType.Hover or StepType.Press or StepType.Upload
        or StepType.WaitForText;

    public static string ParameterHint(StepType type) => type switch
    {
        StepType.Fill => "Alana bu kolondaki metin yazılır.",
        StepType.Select => "Bu kolondaki değer ya da etikete sahip seçenek seçilir.",
        StepType.Check or StepType.Uncheck => "Kolon değeri evet/1/true/x ise işaretlenir, değilse işaret kaldırılır.",
        StepType.Click or StepType.DoubleClick or StepType.Hover => "Metni bu kolondaki değer olan öğeye tıklanır (özel açılır listeler için).",
        StepType.Navigate => "Bu kolondaki adrese gidilir.",
        StepType.Press => "Bu kolondaki tuşa basılır.",
        StepType.Upload => "Bu kolondaki dosya yolu yüklenir.",
        StepType.WaitForText => "Bu kolondaki yazı sayfada görünene kadar beklenir.",
        _ => "",
    };
}
