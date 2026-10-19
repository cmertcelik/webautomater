# WebAutomater

Web sitelerinde yapılan işlemleri (tıklama, yazma, açılır listeden seçim, onay kutusu, tuş basma) **aralarındaki sürelerle birlikte** kaydeden ve sonra tekrar oynatan bir araç. Oynatmanın iki yolu var:

- **Direkt oynatma:** senaryo 50 ya da 100 kez, istediğiniz kadar tekrarlanır.
- **Veri kaynağıyla oynatma:** Excel, CSV veya JSON dosyasındaki her satır için senaryo bir kez oynatılır. Adımlar dosyadaki kolonlarla eşleştirilir; örneğin "Ad" alanına her seferinde `isim` kolonundaki değer yazılır.

Windows, Linux ve macOS'ta çalışır. Masaüstü arayüzü (teknik olmayan kullanıcılar için) ve terminal komutu (`wa`) var.

## Proje yapısı

```
src/
  WebAutomater.Core/   Kayıt, oynatma, veri kaynakları, senaryo dosyası (arayüzden bağımsız)
    Recording/         Recorder + sayfaya enjekte edilen recorder.js
    Playback/          Player, seçenekler, şablon/parametre çözümleme, doğrulama, CSV rapor
    Data/              Excel (.xlsx), CSV, JSON okuyucu
    Models/            Scenario, Step
    Text/              Adımları Türkçe okunabilir metne çeviren StepDescriber
  WebAutomater.Cli/    Terminal uygulaması: wa
  WebAutomater.Gui/    Avalonia masaüstü uygulaması: WebAutomater
tests/
  WebAutomater.Tests/  Birim testleri + gerçek tarayıcıyla kayıt/oynatma testleri
```

### Neden codegen'in ürettiği kod yerine kendi kaydedicimiz?

Playwright `codegen` C# kodu üretir. Bu kodu çalıştırmak için derlemek gerekir ve kodda adımlar arasındaki süreler yer almaz. Bunun yerine Playwright'ın açtığı tarayıcıya küçük bir betik (`recorder.js`) ekleniyor. Betik her işlemi **veri** olarak yakalıyor ve `.wa.json` dosyasına yazıyor:

- Derleme gerekmiyor. Senaryo bir JSON dosyası, uygulama onu okuyup oynatıyor.
- Her adımın bir önceki adımdan ne kadar sonra yapıldığı kaydediliyor.
- Adımlar "Yaz: Ad Soyad (metin alanı) → Ali" gibi okunabilir biçimde gösteriliyor.
- Her öğe için birden çok seçici saklanıyor (test-id, id, name, rol+ad, etiket, metin, CSS yolu). Oynatma sırasında sayfada **tek bir öğeye** karşılık gelen ilk seçici kullanılıyor. Böylece sayfa biraz değişse bile senaryo bozulmuyor.

Tarayıcıyı başlatma, beklemeler ve tıklama/yazma işlemleri hâlâ Playwright'ın kendisi. C# bu iş için uygun: Playwright'ın resmi .NET sürümü var, Avalonia ile tek kod tabanından üç platformda arayüz yapılabiliyor.

## Gereksinimler

- Geliştirme için [.NET 10 SDK](https://dotnet.microsoft.com/download). Yayınlanmış sürümde .NET kurulu olması gerekmez (self-contained).
- Tarayıcı: ilk kayıt/oynatmada Playwright Chromium'u kendiliğinden indirir (yaklaşık 150 MB, bir kez). İsterseniz önceden indirebilirsiniz: `wa install`. Bilgisayarda yüklü Chrome veya Edge'i de kullanabilirsiniz (arayüzde tarayıcı seçimi, terminalde `--channel chrome`).
- Linux'ta tarayıcının sistem kütüphaneleri için bir kez: `wa install --with-deps` (yönetici yetkisi ister).

## Masaüstü arayüzü

```bash
dotnet run --project src/WebAutomater.Gui
```

1. Üstteki **Adres** kutusuna siteyi yazın, **● Kaydı başlat**'a basın.
2. Açılan tarayıcıda işlemlerinizi yapın. Sağ alttaki kırmızı **● KAYIT** etiketi kaydın sürdüğünü gösterir. Adımlar uygulamada canlı olarak listelenir.
3. **■ Kaydı durdur**'a basın ya da tarayıcıyı kapatın. Senaryo otomatik olarak `Belgeler/WebAutomater/Scenarios` klasörüne kaydedilir ve soldaki listede görünür.
4. Adımları düzenleyin: değeri değiştirin, bekleme süresini ayarlayın, bir adımı devre dışı bırakın ya da silin. **+ Bekleme ekle** menüsünden sabit süre, bir yazı ya da bir öğe görünene kadar bekleme adımı ekleyin. Değişiklikler otomatik kaydedilir.
5. Sağdaki **Oynatma** panelinden:
   - **Direkt oynat:** tekrar sayısını girin.
   - **Veri kaynağıyla oynat:** **Dosya seç...** ile Excel/CSV/JSON dosyasını seçin. Ardından adımların **Veri kolonu** sütununda hangi adıma hangi kolonun geleceğini seçin. Seçtiğiniz kolonun ilk satırdaki değeri adımın yanında "örn: Zeynep" diye görünür.
6. **▶ Oynat**. Çalışan adım mavi, başarılı adım yeşil, hatalı adım kırmızı görünür. Hatanın nedeni adımın altında yazar. Bitince **Raporu kaydet...** ile sonuçları CSV olarak alabilir, **Hata görüntüleri** ile hata anındaki ekran görüntülerine bakabilirsiniz.

## Terminal (`wa`)

```bash
dotnet run --project src/WebAutomater.Cli -- <komut>    # geliştirirken
./wa <komut>                                            # yayınlanmış sürümde
```

| Komut | Ne yapar |
|---|---|
| `wa record https://site.com -o giris.wa.json` | Tarayıcı açar ve kaydeder. Bitirmek için tarayıcıyı kapatın ya da Enter'a basın. |
| `wa show giris.wa.json` | Adımları tablo olarak gösterir. |
| `wa columns kisiler.xlsx [--sheet Sayfa2]` | Veri kaynağının kolonlarını ve ilk satırlarını gösterir. |
| `wa bind giris.wa.json --data kisiler.xlsx` | Adımları kolonlarla **etkileşimli** eşleştirir (listeden seçim). |
| `wa bind giris.wa.json -s 3 -c isim` | 3. adımı `isim` kolonuna bağlar (`--clear` bağlantıyı kaldırır). |
| `wa edit giris.wa.json -s 4 --delay 2000` | Bir adımın değerini (`--value`), bekleme süresini, adıma özel zaman aşımını (`--timeout`), etkinliğini (`--disable`/`--enable`) değiştirir ya da adımı siler (`--delete`). |
| `wa add-wait giris.wa.json --after 5 --text "Sipariş alındı"` | Bekleme adımı ekler: `--ms 2000` sabit süre, `--text` yazı görünene kadar, `--element 7` 7. adımın öğesi görünene kadar (`--timeout` ile en uzun süre). |
| `wa play giris.wa.json -n 100` | 100 kez oynatır. |
| `wa play giris.wa.json --data kisiler.csv` | Her satır için bir kez oynatır. |
| `wa install [chromium\|firefox\|webkit]` | Tarayıcıyı indirir. |

Sık kullanılan `play` seçenekleri:

```
--headless              Tarayıcı penceresi açmadan çalış
--speed 0.5             Kayıttaki sürelerin yarısı kadar bekle (0 = beklemesiz)
--fixed-delay 1000      Kayıttaki süreler yerine her adımdan önce 1 sn bekle
--max-delay 5000        Tek bir bekleme en fazla 5 sn olsun
--from 10 --to 20       Yalnızca 10-20. veri satırları
--map 3=isim            Dosyayı değiştirmeden bu oynatma için adım-kolon eşleştirmesi
--use-saved-data        "bind" ile hatırlanan veri kaynağını kullan
--timeout 30000         Sayfa/öğe geç yüklenirse en fazla 30 sn bekle (varsayılan 15 sn)
--retries 2             Hatalı tekrarı baştan en fazla 2 kez daha dene
--keep-session          Tekrarlar arasında çerezleri koru (bir kez giriş yap, devam et)
--stop-on-error         İlk hatada dur (varsayılan: sonraki tekrara geç)
--type-delay 50         Metni tuş tuş yaz
--report sonuc.csv      Her tekrarın sonucunu CSV'ye yaz
-v                      Her adımı yazdır
```

Çıkış kodu: tüm tekrarlar başarılıysa `0`, hata ya da iptal varsa `2`. Bu sayede zamanlanmış görevlerde ve CI'da kullanılabilir.

## Veri kaynakları ve eşleştirme

- **Excel (.xlsx/.xlsm):** ilk dolu satır başlık satırıdır. Değerler Excel'de göründüğü biçimde okunur. Dosya Excel'de açıkken de okunabilir. Eski `.xls` biçimi desteklenmez, dosyayı `.xlsx` olarak kaydedin.
- **CSV/TSV:** ayırıcı (`,` `;` sekme `|`) kendiliğinden bulunur. UTF-8 değilse Türkçe Windows kodlaması (1254) denenir.
- **JSON:** nesne dizisi (`[{"isim": "Ali"}, ...]`) ya da içinde nesne dizisi olan bir nesne. İç içe alanlar `adres.sehir` biçiminde kolona dönüşür.

Hangi adım bir kolona bağlanınca ne olur:

| Adım | Kolon değeriyle yapılan |
|---|---|
| Yaz | Alana kolondaki metin yazılır. |
| Seç | Değeri **ya da** görünen etiketi kolondaki değer olan seçenek seçilir ("İzmir" de "izm" de olur). |
| İşaretle / İşareti kaldır | `evet`, `1`, `true`, `x` ise işaretlenir, değilse işaret kaldırılır. |
| Tıkla | Metni kolondaki değer olan öğeye tıklanır. Kendi açılır listesini çizen (select kullanmayan) sitelerde işe yarar. |
| Sayfaya git / Dosya yükle / Tuşa bas | Adres, dosya yolu ya da tuş adı kolondan alınır. |

**Şablonlar:** Bir adımın değerine `{{kolon}}` yazabilirsiniz, örneğin `Merhaba {{isim}}, kodunuz {{tekrar}}`. `{{tekrar}}` kaçıncı tekrarda olunduğunu, `{{satir}}` veri satır numarasını verir. Veri kaynağı olmadan direkt oynatmada da çalışır, örneğin `test{{tekrar}}@ornek.com`.

## Yükleme süreleri değişken olduğunda

Oynatma sabit süre beklemez, koşul bekler. Bir şey gelir gelmez devam eder, gelmezse zaman aşımına kadar bekler:

- **Sayfaya git:** sayfa tamamen yüklenene kadar beklenir (en az 30 sn).
- **Her öğe:** sayfada belirene, görünür, sabit ve tıklanabilir olana kadar beklenir. Üstünde "yükleniyor" katmanı varsa o kalkana kadar beklenir. Süre varsayılan 15 sn'dir; arayüzde **Yükleme beklemesi**, terminalde `--timeout` ile değiştirilir.
- **Açılır listeler:** seçenek sonradan yükleniyorsa listeye eklenene kadar beklenir.
- **Sayfa geçişleri:** kayıt sırasında bir tıklamadan hemen sonra yeni sayfa yüklendiyse adım işaretlenir. Arayüzde bu, adımın altında "ardından yeni sayfa beklenir" notu olarak görünür. Oynatmada o adımdan sonra yeni sayfa yüklenene kadar beklenir. Böylece site geçişi gecikmeyle başlatsa bile bir sonraki adım eski sayfada kalan benzer bir butona uygulanmaz. İşaretsiz adımlarda da tıklamanın hemen ardından başlayan geçiş yakalanır.
- **Yazıyı bekle / Öğeyi bekle adımları:** sayfa görünüp verisi sonradan gelen durumlar için (ör. "Sonuçlar" tablosu). Bu adımlar varsayılan olarak 60 sn'ye kadar bekler.
- **Yeniden deneme:** sunucu bir anlığına yanıt vermezse hatalı tekrar baştan yeniden denenir (arayüzde "Hata olursa N kez yeniden dene", terminalde `--retries`). Raporda her tekrarın kaçıncı denemede başarılı olduğu yazar.

## Senaryo dosyası

`.wa.json` düz bir JSON dosyasıdır. Elle de düzenlenebilir, sürüm kontrolüne konabilir:

```json
{
  "name": "Giriş",
  "startUrl": "https://ornek.com/giris",
  "browser": { "engine": "chromium", "viewportWidth": 1280, "viewportHeight": 720 },
  "steps": [
    { "type": "navigate", "value": "https://ornek.com/giris" },
    { "type": "fill", "selectors": ["#email", "internal:label=\"E-posta\"i"], "value": "ali@ornek.com",
      "targetName": "E-posta", "targetKind": "textbox", "delayBeforeMs": 1840, "parameter": "eposta" },
    { "type": "click", "selectors": ["internal:role=button[name=\"Giriş\"i]"], "targetName": "Giriş",
      "targetKind": "button", "delayBeforeMs": 920 }
  ]
}
```

## Geliştirme

```bash
dotnet build
dotnet test          # birim testleri + gerçek tarayıcıyla (headless) kayıt ve oynatma testleri
```

Uçtan uca testler kullanıcının yerine Playwright'ın klavye/fare API'sini kullanır. Bu API gerçek tarayıcı olayları ürettiği için kaydedici bunları kullanıcı işlemi olarak yakalar. Kaydedilen senaryo yerel bir test sunucusuna karşı direkt ve veri kaynağıyla oynatılır, form gönderimleri doğrulanır.

## Yayınlama

```bash
./build/publish.sh                 # win-x64, linux-x64, osx-arm64, osx-x64
./build/publish.sh osx-arm64       # yalnızca bir platform
pwsh ./build/publish.ps1 -Rids win-x64
```

Her platform için `dist/<rid>/` klasöründe `wa` (terminal) ve `WebAutomater` (arayüz) birlikte bulunur. .NET kurulumu gerekmez. Playwright sürücüsü yalnızca o platform için eklenir. Tarayıcı ilk çalıştırmada indirilir.

macOS'ta imzasız uygulamayı ilk açışta Finder'da sağ tık → **Aç** demeniz gerekebilir.

## Bilinen sınırlar

- Dosya yükleme adımında tarayıcı dosyanın tam yolunu vermez, yalnızca adını verir. Kayıttan sonra adımın değerine tam dosya yolunu yazın.
- Sürükle-bırak, fare tekerleğiyle kaydırma, sağ tık ve çizim (canvas) işlemleri kaydedilmez. Playwright tıklamadan önce öğeyi kendisi görünür alana kaydırdığı için kaydırma çoğu zaman gerekmez.
- Kayıt sırasında adres çubuğuna yazılan yeni adresler "Sayfaya git" adımı olur. Bir tıklamadan sonraki 2 sn içinde gerçekleşen sayfa geçişleri ise o tıklamanın sonucu sayılır ve ayrı adım olarak kaydedilmez.
- CAPTCHA ve iki adımlı doğrulama içeren akışlar otomatikleştirilemez.
