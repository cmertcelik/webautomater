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

    private readonly HttpListener _listener = new();
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

    public ConcurrentQueue<string> Submissions { get; } = new();

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
            var body = "<html><body>ok</body></html>";
            if (path == "/submit")
                Submissions.Enqueue(WebUtility.UrlDecode(ctx.Request.Url.Query.TrimStart('?')));
            else
                body = FormPage;
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
