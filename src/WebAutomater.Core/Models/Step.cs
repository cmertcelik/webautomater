namespace WebAutomater.Core.Models;

/// <summary>
/// Kaydedilmiş tek bir kullanıcı işlemi. Kod üretmek yerine veri olarak saklanır,
/// böylece uygulama yeniden derlenmeden düzenlenip oynatılabilir.
/// </summary>
public sealed class Step
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public StepType Type { get; set; }

    /// <summary>Hedef öğe için aday seçiciler; oynatmada ilk tekil eşleşen kullanılır.</summary>
    public List<string> Selectors { get; set; } = [];

    /// <summary>Yazılan metin, adres, tuş, seçenek değer(ler)i, dosya yolu ya da bekleme süresi (ms).</summary>
    public string? Value { get; set; }

    /// <summary>Kullanıcının ekranda gördüğü değer (ör. açılır listede seçeneğin etiketi).</summary>
    public string? DisplayValue { get; set; }

    /// <summary>Hedef öğenin insan tarafından okunabilir adı: "E-posta", "Giriş Yap".</summary>
    public string? TargetName { get; set; }

    /// <summary>Hedef öğenin türü: button, link, textbox, password, combobox...</summary>
    public string? TargetKind { get; set; }

    /// <summary>Bir önceki adımdan bu adıma kadar geçen süre.</summary>
    public int DelayBeforeMs { get; set; }

    /// <summary>Bu adımın değerini sağlayan veri kaynağı kolonu. Boşsa kayıtlı değer kullanılır.</summary>
    public string? Parameter { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Çoklu seçimli açılır listelerde değerler '|' ile ayrılır.</summary>
    public bool Multiple { get; set; }

    /// <summary>Adımın gerçekleştiği sekme (0 = ilk sekme, açılan pencereler sırayla).</summary>
    public int PageIndex { get; set; }

    public string? FrameName { get; set; }

    public string? FrameUrl { get; set; }

    /// <summary>Kullanıcının adıma eklediği serbest açıklama.</summary>
    public string? Note { get; set; }

    public Step Clone()
    {
        var copy = (Step)MemberwiseClone();
        copy.Id = Guid.NewGuid();
        copy.Selectors = [.. Selectors];
        return copy;
    }
}
