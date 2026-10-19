using WebAutomater.Core.Data;

namespace WebAutomater.Core.Playback;

public sealed class PlaybackOptions
{
    /// <summary>
    /// Veri kaynağı yoksa senaryonun kaç kez oynatılacağı;
    /// veri kaynağı varsa tüm satırların kaç tur döneceği.
    /// </summary>
    public int Iterations { get; set; } = 1;

    public DataTableSource? Data { get; set; }

    /// <summary>1'den başlayan ilk veri satırı (dahil).</summary>
    public int? FirstRow { get; set; }

    /// <summary>1'den başlayan son veri satırı (dahil).</summary>
    public int? LastRow { get; set; }

    public bool Headless { get; set; }

    /// <summary>Kayıttaki bekleme sürelerinin çarpanı: 1 = kaydedildiği hızda, 0.5 = iki kat hızlı, 0 = beklemesiz.</summary>
    public double SpeedFactor { get; set; } = 1.0;

    /// <summary>Doluysa kayıttaki süreler yerine her adımdan önce bu kadar beklenir.</summary>
    public int? FixedDelayMs { get; set; }

    /// <summary>Tek bir beklemenin üst sınırı; kayıt sırasında uzun düşünme aralarını kısaltır.</summary>
    public int? MaxDelayMs { get; set; }

    /// <summary>Bir öğenin görünmesi/işlem yapılabilir olması için beklenecek en uzun süre.</summary>
    public int ActionTimeoutMs { get; set; } = 15_000;

    /// <summary>Bir tekrarda hata olursa sonraki tekrara geçilir; false ise oynatma durur.</summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>Her tekrarda çerezleri/oturumu sıfırlamak için yeni tarayıcı bağlamı açılır.</summary>
    public bool FreshSessionPerIteration { get; set; } = true;

    /// <summary>0'dan büyükse metinler tek tek tuşlanır (bazı siteler tuş olaylarını bekler).</summary>
    public int TypeDelayMs { get; set; }

    public int DelayBetweenIterationsMs { get; set; }

    /// <summary>Hatalı adımda ekran görüntüsünün kaydedileceği klasör.</summary>
    public string? ScreenshotDirectory { get; set; }
}
