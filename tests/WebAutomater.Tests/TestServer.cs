using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WebAutomater.Tests;

/// <summary>Test sayfasını sunan ve form gönderimlerini kaydeden küçük HTTP sunucusu.</summary>
public sealed class TestServer : IDisposable
{
    public const string FormPage = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Form</title></head>
        <body>
          <form action="/submit" method="get">
            <label for="fullname">Ad Soyad</label>
            <input id="fullname" name="fullname">
            <label>Şehir
              <select name="city">
                <option value="">Seçiniz</option>
                <option value="ist">İstanbul</option>
                <option value="ank">Ankara</option>
              </select>
            </label>
            <label><input type="checkbox" name="agree" value="1"> Kabul ediyorum</label>
            <button type="submit">Gönder</button>
          </form>
        </body></html>
        """;

    // Sihirbaz: ilk sayfadaki "İleri" ikinci sayfayı yavaş yükler; ikinci sayfada da "İleri" vardır.
    // Yeni sayfa beklenmezse ikinci tıklama hâlâ ekranda duran ilk sayfadaki butona gider.
    public const string WizardPage = """
        <!doctype html><html><head><meta charset="utf-8"></head><body>
          <h1>Adım 1</h1>
          <form action="/wizard2" method="get"><button type="submit">İleri</button></form>
        </body></html>
        """;

    // Aynı sihirbaz, ama geçişi sitenin betiği 600 ms gecikmeyle başlatıyor (önce istek atıp sonra
    // yönlendiren siteler gibi). Playwright tıklama sırasında başlamayan geçişi kendisi beklemez.
    public const string WizardJsPage = """
        <!doctype html><html><head><meta charset="utf-8"></head><body>
          <h1>Adım 1</h1>
          <form id="f" action="/wizard2" method="get"></form>
          <button type="button" onclick="setTimeout(() => document.getElementById('f').submit(), 600)">İleri</button>
        </body></html>
        """;

    public const string Wizard2Page = """
        <!doctype html><html><head><meta charset="utf-8"></head><body>
          <h1>Adım 2</h1>
          <form action="/submit" method="get"><input type="hidden" name="from" value="wizard2"><button type="submit">İleri</button></form>
        </body></html>
        """;

    // Buton ve "Hazır" yazısı sayfa açıldıktan 2,5 sn sonra belirir (sunucudan geç gelen içerik gibi).
    public const string LatePage = """
        <!doctype html><html><head><meta charset="utf-8"></head><body>
          <div id="area">Yükleniyor...</div>
          <script>
            setTimeout(() => {
              document.getElementById('area').innerHTML =
                '<p>Hazır</p><form action="/submit" method="get"><input type="hidden" name="late" value="1"><button type="submit">Gönder</button></form>';
            }, 2500);
          </script>
        </body></html>
        """;

    public const string FlakyGoodPage = """
        <!doctype html><html><head><meta charset="utf-8"></head><body>
          <form action="/submit" method="get"><input type="hidden" name="flaky" value="1"><button type="submit">Gönder</button></form>
        </body></html>
        """;

    // Sayfa içi işlem yapılan ekran: adres değişmez. "kapali" sorgusu butonu pasif açar.
    public const string FlowPageTemplate = """
        <!doctype html><html><head><meta charset="utf-8"></head><body>
          <table><tr><td id="durum">Onaylandı</td><td>Ali Veli</td></tr></table>
          <div>Toplam: <span id="sayac">0</span></div>
          <button id="islem" DISABLED onclick="document.getElementById('sayac').textContent = +document.getElementById('sayac').textContent + 1">İşlem yap</button>
          <button id="plan" onclick="location.href='/submit?yol=' + this.dataset.yol" data-yol="b">B planı</button>
          <button id="plan2" onclick="location.href='/submit?yol=a'">A planı</button>
          <input id="metin" value="">
        </body></html>
        """;

    private readonly HttpListener _listener = new();
    private int _flakyHits;
    private readonly CancellationTokenSource _cts = new();

    public TestServer()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    public int Wizard2DelayMs { get; set; } = 1500;

    public ConcurrentQueue<string> Submissions { get; } = new();

    public ConcurrentDictionary<string, int> Hits { get; } = new();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        {
            var path = ctx.Request.Url!.AbsolutePath;
            string body;
            Hits.AddOrUpdate(path, 1, (_, n) => n + 1);
            switch (path)
            {
                case "/flow":
                    body = FlowPageTemplate.Replace("DISABLED", ctx.Request.QueryString["kapali"] is null ? "" : "disabled");
                    break;
                case "/submit":
                    Submissions.Enqueue(WebUtility.UrlDecode(ctx.Request.Url.Query.TrimStart('?')));
                    body = "<html><body>ok</body></html>";
                    break;
                case "/wizard":
                    body = WizardPage;
                    break;
                case "/wizardjs":
                    body = WizardJsPage;
                    break;
                case "/wizard2":
                    await Task.Delay(Wizard2DelayMs);
                    body = Wizard2Page;
                    break;
                case "/late":
                    body = LatePage;
                    break;
                case "/flaky":
                    // İlk istek bozuk sayfa döner (geçici sunucu hatası gibi), sonrakiler düzgün.
                    body = Interlocked.Increment(ref _flakyHits) == 1 ? "<html><body>Hata</body></html>" : FlakyGoodPage;
                    break;
                default:
                    body = FormPage;
                    break;
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
    }
}
