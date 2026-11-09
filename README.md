# WebAutomater

Web sitelerinde yapılan işlemleri (tıklama, yazma, açılır listeden seçim, onay kutusu, tuş basma) **aralarındaki sürelerle birlikte** kaydeden ve sonra tekrar oynatan bir araç. Oynatmanın iki yolu var:

- **Direkt oynatma:** senaryo 50 ya da 100 kez, istediğiniz kadar tekrarlanır.
- **Veri kaynağıyla oynatma:** Excel, CSV veya JSON dosyasındaki her satır için senaryo bir kez oynatılır. Adımlar dosyadaki kolonlarla eşleştirilir; örneğin "Ad" alanına her seferinde `isim` kolonundaki değer yazılır.

Masaüstü arayüzünde ayrıca **Belge Tarama** sekmesi var: PDF'leri görüntüler, içindeki T.C. kimlik ve vergi numaralarını bulur, belge bilgilerini Excel'e satır olarak ekler.

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
    Documents/         PDF (PDFium), numara bulma, Excel'e satır ekleme
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

- Geliştirme için [.NET 8 SDK](https://dotnet.microsoft.com/download) ya da daha yenisi (9, 10). Proje .NET 8'i hedefler ve C# 12 ile yazılır; daha yeni bir SDK ile derlense de .NET 8 SDK'sında açılmayacak bir şey eklenmez. Yalnızca daha yeni bir .NET kuruluysa uygulama onunla çalışır. Çözüm dosyası `WebAutomater.sln` (Visual Studio 2022 ve `dotnet` komutu açar). Yayınlanmış sürümde .NET kurulu olması gerekmez (self-contained).
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

## Belge Tarama sekmesi

Soldan sağa dört panel vardır; aralarındaki çizgiler sürüklenerek genişlikler değiştirilebilir.

1. **Dosyalar:** **Klasör seç...** ile klasörü seçin. Alt klasörler ok ya da çift tıklamayla açılır (klavyede sağ/sol ok, Enter). Seçilen klasör sonraki açılışta hatırlanır.
   - **Yeni klasör:** Seçili klasörün içinde (dosya seçiliyse onun klasöründe, hiçbir şey seçili değilse ana klasörde) yeni klasör oluşturur.
   - **Taşıma:** Dosya ya da klasörü sürükleyip bir klasörün üzerine bırakın. Bir dosyanın üzerine bırakınca o dosyanın klasörüne, listedeki boş alana bırakınca ana klasöre taşınır. Kapalı bir klasörün üzerinde biraz bekleyince klasör açılır. Hedefte aynı adda bir öğe varsa taşıma yapılmaz. Açık PDF taşınırsa görüntüleyici açık kalır, yolu güncellenir.
   - **Etiketler:** Etiket adını (ör. *vesayet*) ve PDF içinde aranacak kelimeleri virgülle ayırarak girin (ör. *vesayet kararı, vasi tayini*). Büyük/küçük harf ve Türkçe karakter farkı gözetilmez, kelimenin ekli halleri de bulunur. Aynı menüde dosya numarası hane aralığı (varsayılan 5–7) ve tek dosyalık numaralar için klasör açılıp açılmayacağı ayarlanır.
   - **Otomatik grupla:** Seçili klasörde (klasör seçili değilse ana klasörde) iki aşamada çalışır. Taşımadan önce hangi dosyanın hangi klasöre gideceğini ve hangi etiketin nerede bulunduğunu gösteren bir önizleme açılır.
     1. **Dosya numarasına göre gruplama:** Adında aynı dosya numarası geçen dosyalar (ör. *34765 karar.pdf*, *34765 tebligat.udf*) `34765` klasöründe toplanır. Etiket tanımlı olmasa da yapılır ve yalnızca dosya adlarına bakıldığı için hızlıdır. Sonradan gelen dosyalar var olan numara klasörüne eklenir. Numarası başka hiçbir dosyada geçmeyen dosya yerinde kalır (ayarla değiştirilebilir).
     2. **Etiket arama:** Yalnızca gruplanan dosyaların içinde etiket kelimeleri aranır. Bulunan etiket klasör adının sonuna eklenir: `34765` → `34765-vesayet`. Birden çok etiket bulunursa sırayla eklenir (`34765-vesayet-iflas`); etiket için ayrıca klasör açılmaz. Etiketi zaten yazılmış klasörlerde yalnızca yeni gelen dosyalar taranır. Etiketsiz klasörler yeniden taranır, böylece sonradan eklenen etiketler de bulunur. Bir grupta tüm etiketler bulununca o grubun kalan dosyalarına bakılmaz.
     - Aynı adlı dosyanın üzerine yazılmaz, adına sayı eklenir. Aynı numaralı birden çok klasör varsa etiketli olanda birleştirilir.
     - Okunamayan, bozuk, metin katmanı olmayan ya da 300 MB'tan büyük PDF'ler gruplamayı durdurmaz: yalnızca adlarındaki numaraya göre gruplanır ve önizlemede listelenir. Bir PDF'te en fazla 100 sayfaya bakılır. 60 saniyede okunamayan dosya atlanır.
2. **PDF görüntüleyici:** PDF'e tıklayınca açılır. Ctrl + tekerlek yakınlaştırır; **Sığdır** sayfayı alan genişliğine oturtur. PageUp/PageDown/Home/End çalışır.
   - **Yazı seçip kopyalama:** Fareyle sürükleyerek yazıyı seçin (sayfalar arasında da; fare alanın dışına çıkınca sayfa kendiliğinden kayar). Çift tıklama kelimeyi, üç kez tıklama satırı seçer; Shift + tıklama seçimi uzatır. **Ctrl+C** kopyalar, **Ctrl+A** tüm belgeyi seçer, **Esc** seçimi kaldırır. Sağ tık menüsünde de **Kopyala** ve **Tümünü seç** var. Taranmış (görüntü) sayfalarda seçilecek yazı yoktur.
3. **Bulunan Numaralar:** 11 haneli sayılar T.C. kimlik, 10 haneli sayılar vergi numarası olarak listelenir. Aynı numara tek satırda, geçtiği sayfalarla birlikte görünür; çift tıklama o sayfaya gider.
   - **✓** kontrol hanesi doğru, **?** hane sayısı uyuyor ama kontrol hanesi tutmuyor (telefon, belge numarası olabilir). **Yalnızca doğrulananlar** bunları gizler.
   - Bulunan numaralar sayfada renklendirilir: sarı doğrulanmış, turuncu doğrulanmamış. Bu yalnızca ekrandaki görünümdür, PDF dosyası değişmez. Görüntüleyicideki **Vurgula** düğmesi kapatıp açar.
   - **Hepsini bul:** Yalnızca açık PDF'te değil, seçilen klasördeki (alt klasörler dahil) tüm PDF'lerde arar. Her numara bir kez listelenir; altında geçtiği ilk dosya ve kaç dosyada daha geçtiği yazar. Tüm dosyaları görmek için satırın üzerinde bekleyin; çift tıklama ilk dosyayı o sayfada açar. Tarama sürerken düğme **Durdur** olur. **✕** toplu sonuçları kapatıp açık belgenin numaralarına döner. Açılamayan ve metin katmanı olmayan PDF'ler uyarı satırında sayılır.
   - **Tümünü kopyala** ya da seçip Ctrl+C. Numaralar alt alta kopyalanır, Excel'e yapıştırınca her biri ayrı satıra gelir. Excel baştaki sıfırı siler (`0123456789` gibi vergi numaraları); korumak için hedef sütunu önce Metin biçimine alın.
   - **Müşteri sorgulama / İstihbarat sorgulama:** Seçili numaraları (seçim yoksa listedeki tüm numaraları) bir web servisine sorgular. Yanındaki **⚙** ile isteği tanımlayın: Chrome'da F12 > Network sekmesinde sorgu isteğine sağ tıklayıp **Copy > Copy as cURL** (bash ya da cmd), **Copy as fetch** ya da **Copy as fetch (Node.js)** seçin, kutuya yapıştırın ve numaranın geçtiği yeri `{id}` ile değiştirin (adreste, başlıkta ya da gövdede olabilir). Kutunun altında isteğin nasıl anlaşıldığı (yöntem, adres, `{id}`'nin yeri) ve varsa uyarılar görünür. Ham HTTP isteği de yapıştırılabilir. Numaralar sırayla sorgulanır ve sonuçlar ayrı bir pencerede listelenir. Pencere kapanınca kalan sorgular iptal edilir.
     - **Sonuç alanı** (⚙ menüsünde; iki sorguda da varsayılan `value`): Yanıtın tamamı yerine yalnızca bu alan gösterilir. Alan boşsa (`[]`, `null`, boş metin) **Müşteri kaydı yok** / **İstihbarat kaydı yok** yazar. Doluysa içindeki değerler yazar (`Ad: Şule · MusteriNo: 12345`, birden çok kayıt ayrı satırlarda; `@odata...` gibi teknik alanlar atlanır). Alan bulunamazsa yanıtın içinde aynı adlı ilk alana bakılır. Alanı boş bırakırsanız yanıtın tamamı görünür.
     - **Başarısız istekler** kırmızıyla işaretlenir ve elle kontrol için mesajı yazar: `HTTP 401 Unauthorized: Oturum geçersiz` (sunucunun JSON ya da sayfa mesajı) ya da bağlantı hatası. 200 dönse de yanıt JSON değilse (oturum düşünce gelen giriş sayfası) ya da alan yoksa "kayıt yok" sayılmaz, kontrol edilecekler arasına girer.
     - Üstte kayıt var / kayıt yok / kontrol edilecek sayıları görünür. **Sonuçları kopyala** numara, tür, durum ve sonucu Excel'e yapıştırılacak biçimde kopyalar. **Ham yanıtı göster** gerekirse sunucunun yanıtının tamamını açar.
     - Tarayıcının **Copy as fetch** kopyası çerezleri içermez; sistem oturum çerezi istiyorsa cURL ya da Node.js fetch kopyasını kullanın. Oturum süresi dolunca istek hata verir (genellikle 401/403 ya da giriş sayfası); isteği yeniden kopyalayın.
     - Kopyalanan istek çerez ve oturum bilgisi içerebilir; ayar dosyasında (`belge-tarama.json`) düz metin olarak saklanır.
4. **Excel'e Kaydet:** **Seç...** ile mevcut dosyayı ya da **Yeni...** ile yeni dosyayı belirleyin, alanları doldurup **Excel'e kaydet**'e basın. Satır ilk çalışma sayfasının sonuna eklenir. PDF dosya yolu da bir alandır: kendiliğinden doldurulur, silinemez ama istemiyorsanız kapatabilir, yeniden adlandırabilir ya da sırasını değiştirebilirsiniz. Kayıttan sonra form temizlenir; hata olursa girilen bilgiler korunur.
   - **Alanları düzenle:** Alanları açıp kapatın, yeniden adlandırın, ▲▼ ile sıralayın ya da silin. **+ Alan ekle** ile dört türde alan eklenir: **Metin** (istenirse çok satırlı), **Tarih** (elle ya da takvimden), **Seçim listesi** (seçenekleri her satıra bir tane yazın) ve **Onay kutusu** (Excel'e "Evet"/"Hayır" yazılır). **Varsayılan** ilk alanlara döner. Alanlar sonraki açılışta hatırlanır.
   - Değerler, Excel'in ilk satırındaki **aynı adlı sütuna** yazılır (büyük/küçük harf önemsiz); o adda sütun yoksa sona yeni sütun eklenir. Böylece alan sırasını değiştirmek ya da yeni alan eklemek eski kayıtları bozmaz. Bir alanı yeniden adlandırırsanız Excel'de yeni bir sütun açılır.

Sınırlar:

- Numaralar PDF'in metin katmanından okunur. **Taranmış (görüntü) PDF'lerde numara bulunamaz** (OCR yok); uygulama bu durumda uyarı gösterir.
- Parola korumalı PDF'ler açılamaz.
- Excel dosyası Excel'de açıkken kayıt yapılamaz; dosyayı kapatıp yeniden deneyin. Yazma geçici dosya üzerinden yapıldığı için yarıda kalan kayıt dosyayı bozmaz.
- Mevcut dosya ClosedXML ile yeniden yazılır. Dosyada grafik, makro, resim gibi özel içerik varsa kayıt için ayrı bir dosya kullanın.

## Hata yönetimi ve günlük

Beklenmeyen bir hata uygulamayı kapatmaz. İşlem yarıda kalır ve pencerenin sağ alt köşesinde kısa bir hata bildirimi çıkar; bildirim birkaç saniye sonra kendiliğinden kapanır. Hatanın ayrıntısı günlük dosyasına yazılır:

- Windows: `%AppData%\WebAutomater\logs\webautomater-YYYYMMDD.log`
- macOS: `~/Library/Application Support/WebAutomater/logs/`
- Linux: `~/.config/WebAutomater/logs/`

Bildirimdeki **Günlük klasörünü aç** klasörü açar. Her gün için ayrı dosya tutulur, 30 günden eski dosyalar silinir. Günlüğe uygulamanın açılıp kapanması, Belge Tarama işlemleri (klasör açma, PDF açma hataları, gruplama, taşıma, Excel kaydı), sorgulamalar ve web otomasyonundaki alt panel satırları, kayıt ve oynatma hataları yazılır. Terminal (`wa`) de aynı dosyaya yazar. Sorgulamalarda numara, çerez ve yanıt içeriği günlüğe yazılmaz.

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
--keep-page             Sayfayı kapatmadan tekrarla (bkz. "Sayfayı açık tutarak tekrarlama")
--stop-on-error         İlk hatada dur (varsayılan: sonraki tekrara geç)
--type-delay 50         Metni tuş tuş yaz
--report sonuc.csv      Her tekrarın sonucunu CSV'ye yaz
--files C:\CVler        Yükleme adımlarındaki göreli dosya adlarını bu klasörde ara
-v                      Her adımı yazdır
```

Çıkış kodu: tüm tekrarlar başarılıysa `0`, hata ya da iptal varsa `2`. Bu sayede zamanlanmış görevlerde ve CI'da kullanılabilir.

## Akış: kontroller, dallar ve B planı

Senaryo düğümlerden oluşur. Orta alandaki **Akış / Liste** seçimi iki görünüm arasında geçirir; eski senaryolar açıldığında düz bir zincir olarak görünür ve aynı şekilde çalışır.

**Akış ekranında**

- Daha geniş alan için **Senaryolarım**, **Oynatma** ve **Günlük** panellerini başlıklarındaki düğmeyle kapatın; akış ekranı kalan alana kendiliğinden yayılır. Kapalı yan panel kenarda ince bir şerit olarak kalır, tıklayınca açılır. Boş alana tıklamak seçimi kaldırır ve sağdaki özellik panelini de kapatır.
- Sağ alttaki **+** ve **−** düğmeleri yakınlaştırıp uzaklaştırır.
- Düğümü sürükleyin. Boş alanı sürüklemek ya da tekerlek ekranı kaydırır, Ctrl + tekerlek yakınlaştırır. **Düzenle** tümünü yeniden yerleştirir.
- Bağlantı kurmak için bir düğümün çıkış noktasından başka bir düğümün üzerine sürükleyin. Bağlantıya tıklayıp Delete ile silin. Seçili düğüm de Delete ile silinir; gelen bağlantılar devamına aktarılır.
- **Klonlama:** düğümü seçip sağ paneldeki **Klonla**'ya ya da Ctrl+D'ye basın. Kopya aynı hedef, değer ve ayarlarla yanına eklenir ama bağlantısı yoktur, mevcut akış değişmez. Değerini düzenleyip bir çıkıştan ona sürükleyin; devam edecekse kendi çıkışını da bağlayın, yoksa tekrar o düğümde biter.
- Çıkışlar: alt nokta normal devamdır (kontrolde **Evet**), kontroldeki ikinci nokta **Hayır**, işlem düğümlerinin sağındaki kırmızı nokta **Hata** (B planı).
- Çıkışı boş olan düğümde tekrar başarıyla biter. **+ Bitir** düğümü tekrarı açıkça bitirir; "Tekrarı hatalı say" ile hatalı sayılabilir.
- Döngü kurulabilir (ör. hata varsa geri dön); bir tekrar içinde 5000 adımı geçerse tekrar hata ile durdurulur.

**Kontrol modu.** Kayıt sırasında uygulamadaki **◎ Kontrol modu** düğmesine ya da tarayıcının sağ altındaki **◎ KONTROL**'e basın. Üzerine geldiğiniz öğe renklenir. Tıkladığınızda site tıklamayı almaz, işlem yapılmaz; açılan panelden kontrolü seçip **Kontrolü ekle** dersiniz: aktif mi, pasif mi, işaretli mi, görünüyor mu, metni eşit mi, metni içeriyor mu, bu yazı sayfada var mı. Sonra kayıt moduna kendiliğinden dönülür. Esc iptal eder. Beklenen metin `{{kolon}}` şablonu ya da **Veri kolonu** ile veri kaynağına bağlanabilir.

**Kontrolün sonucu.** Metin karşılaştırmasında boşluk ve Türkçe büyük/küçük harf farkı önemsenmez. Durum en fazla 3 sn yoklanır (düğümde değiştirilebilir), sağlanmazsa sonuç Hayır'dır; öğenin bulunamaması da Hayır sayılır. **Hayır çıkışı bağlı değilse tekrar hatalı biter** ve neden sağlanmadığı günlüğe yazılır.

**Dal kaydetme.** Bir düğümü seçip sağdaki panelden "Hayır çıkışından kaydet" ya da "Hata çıkışından kaydet"e basın. Tarayıcı açılır, başlangıçtan o düğüme kadar otomatik gidilir (kontroller sınanmadan, en kısa yoldan), sonra yaptığınız işlemler o çıkışa bağlanır.

**B planı.** Bir işlem hata verir ve Hata çıkışı bağlıysa tekrar hatalı sayılmaz, o yoldan devam edilir. Düğümün **En fazla (sn)** süresini kısaltırsanız B planına daha çabuk geçilir. Sitenin gösterdiği hata mesajı için işlemden sonra bir kontrol düğümü koyun: işlem teknik olarak başarılı olduğundan Hata çıkışı bunu yakalamaz.

## Sayfayı açık tutarak tekrarlama

Oynatma panelinde **Sayfayı açık tut** (terminalde `--keep-page`) seçilince tarayıcı tekrarlar arasında kapanmaz:

- İlk tekrar baştan çalışır (giriş yapma gibi hazırlıklar dahil), sonrakiler **döngü başı** düğümünden devam eder. Varsayılan döngü başı, baştaki "Sayfaya git" adımlarından sonraki ilk düğümdür; başka bir düğümü sağdaki panelden **Döngü başı yap** ile seçebilirsiniz. Akış ekranında döngü başı `↻` ile işaretlidir.
- Akış, döngü başındaki adresten farklı bir adreste bittiyse sonraki tekrardan önce o adrese dönülür. Adres aynıysa sayfa yenilenmez.
- Tekrar hatalı bittiyse sayfa yarım kalmış olabileceği için yenilenir. Sayfa kapanmışsa senaryo baştan başlar.
- Bu seçenek açıkken çerezler de korunur.

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

## Dosya yükleme

Kayıtta bir dosya alanına ya da sitenin "Dosya seç" butonuna tıklayınca tarayıcının penceresi yerine uygulamanın dosya seçicisi açılır (site belirli türleri kabul ediyorsa süzgeç olarak gelir). Seçilen dosyanın **tam yolu** "Dosya yükle" adımına yazılır. Tarayıcı güvenlik nedeniyle siteye yalnızca dosya adını verdiği için bu yolu uygulama yakalar.

Üç tür alan desteklenir:

- **Görünür dosya alanı** (`<input type="file">`): Dosya doğrudan alana yazılır.
- **Gizli alanı açan özel buton** ("CV Yükle" gibi): Butona tıklama ayrı adım olarak kaydedilir. Oynatmada dosya gizli alana doğrudan yazılır; tıklamanın açtırdığı pencere ekranda asılı kalmaz.
- **Tıklamayla anlık oluşturulan alan** (sayfada hiç bulunmayan): Adım seçicisiz kaydedilir ve oynatmada bir önceki tıklamanın açtırdığı dosya seçme penceresi doldurulur.

Her tekrarda farklı dosya (ör. her aday için ayrı CV):

- Adımı veri kaynağındaki dosya yolu kolonuna bağlayın. Kolonda tam yol (`C:\CVler\ahmet.pdf`) ya da yalnızca ad (`ahmet.pdf`) olabilir. Yalnızca ad varsa dosya, **veri dosyasının bulunduğu klasörde** aranır (terminalde `--files` ile başka klasör verilebilir).
- Ya da adımın değerine kolon adını şablon olarak yazın: `C:\CVler\{{TC}}.pdf`. Adı tam bilinmiyorsa `*` kullanılabilir: `C:\CVler\{{TC}}*.pdf`.
- Birden çok dosya `|` ile ayrılır (alan çoklu seçime izin vermiyorsa açık bir hata verilir). Windows'un "Yol olarak kopyala"sındaki tırnaklar ve `%USERPROFILE%` gibi değişkenler de çalışır.
- Bir satırda dosya bulunamazsa yalnızca o tekrar hatalı sayılır; hata mesajında aranan yol yazar. Değer boşsa dosya seçimi temizlenir ve devam edilir.
- Adım düzenleyicideki **Gözat...** ile adımın dosyası sonradan değiştirilebilir. Her tekrarda aynı dosya yükleniyorsa ve dosya bulunamıyorsa oynatma hiç başlamadan uyarılır.

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

- Terminalden (`wa record`) kayıtta dosya seçme penceresi tarayıcının kendisidir ve tarayıcı yalnızca dosya adını verir; adımın değerine tam yolu `wa edit --value` ile yazın. Masaüstü arayüzünde tam yol kendiliğinden kaydedilir.
- Sürükleyip bırakarak dosya yüklenen alanlar (input olmadan yalnızca bırakma bölgesi olan siteler) kaydedilmez.
- Kontrol modunda pasif butonun yakalanması Chromium ile test edildi. Firefox ve WebKit'te pasif öğeler tıklama olayı üretmeyebilir; bu durumda "Sayfada yazı var mı" kontrolünü kullanın ya da Chromium ile kaydedin.
- Sürükle-bırak, fare tekerleğiyle kaydırma, sağ tık ve çizim (canvas) işlemleri kaydedilmez. Playwright tıklamadan önce öğeyi kendisi görünür alana kaydırdığı için kaydırma çoğu zaman gerekmez.
- Kayıt sırasında adres çubuğuna yazılan yeni adresler "Sayfaya git" adımı olur. Bir tıklamadan sonraki 2 sn içinde gerçekleşen sayfa geçişleri ise o tıklamanın sonucu sayılır ve ayrı adım olarak kaydedilmez.
- CAPTCHA ve iki adımlı doğrulama içeren akışlar otomatikleştirilemez.
