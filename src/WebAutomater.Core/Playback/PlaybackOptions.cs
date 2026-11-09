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

    /// <summary>Sayfa yüklemeleri için en uzun bekleme; zaman aşımı daha büyükse o kullanılır.</summary>
    public int NavigationTimeoutMs => Math.Max(ActionTimeoutMs, 30_000);

    /// <summary>Bir tekrarda hata olursa sonraki tekrara geçilir; false ise oynatma durur.</summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>Her tekrarda çerezleri/oturumu sıfırlamak için yeni tarayıcı bağlamı açılır.</summary>
    public bool FreshSessionPerIteration { get; set; } = true;

    /// <summary>
    /// Tarayıcı ve sayfa tekrarlar arasında kapanmaz. İlk tekrar baştan çalışır, sonrakiler döngü başlangıcından
    /// devam eder. Sayfa başka bir adrese geçmişse ya da önceki tekrar hatalı bittiyse döngü sayfasına dönülür.
    /// </summary>
    public bool KeepPageOpen { get; set; }

    /// <summary>0'dan büyükse metinler tek tek tuşlanır (bazı siteler tuş olaylarını bekler).</summary>
    public int TypeDelayMs { get; set; }

    public int DelayBetweenIterationsMs { get; set; }

    /// <summary>Hatalı bir tekrar baştan kaç kez daha denensin (geçici ağ/sunucu aksaklıkları için).</summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Yükleme adımlarındaki göreli dosya yollarının (ör. veri kaynağındaki "ahmet_cv.pdf") arandığı klasör.
    /// Boşsa veri kaynağı dosyasının klasörü kullanılır.
    /// </summary>
    public string? FilesDirectory { get; set; }

    /// <summary>Hatalı adımda ekran görüntüsünün kaydedileceği klasör.</summary>
    public string? ScreenshotDirectory { get; set; }
}
