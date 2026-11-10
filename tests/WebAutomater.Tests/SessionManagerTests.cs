using System.Net;
using System.Net.Sockets;
using System.Text;
using WebAutomater.Core.Http;

namespace WebAutomater.Tests;

public class TokenFinderTests
{
    internal static string Jwt(DateTimeOffset exp, string extra = "")
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"HS256"}""")}.{B64($$"""{"exp":{{exp.ToUnixTimeSeconds()}},"x":"{{extra}}"}""")}.imza";
    }

    [Fact]
    public void Finds_token_in_common_fields_nested_jwt_headers_and_plain_body()
    {
        Assert.Equal("abcdefghijklmnop", TokenFinder.Find("""{"access_token":"abcdefghijklmnop","expires_in":300}""", []));
        Assert.Equal("abcdefghijklmnop", TokenFinder.Find("""{"data":{"accessToken":"Bearer abcdefghijklmnop"}}""", []));
        var jwt = Jwt(DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(jwt, TokenFinder.Find($$$"""{"sonuc":{"anahtar":"{{{jwt}}}"}}""", []));
        Assert.Equal(jwt, TokenFinder.Find(jwt, []));
        Assert.Equal("abcdefghijklmnop", TokenFinder.Find("", [new("X-Auth-Token", "abcdefghijklmnop")]));
        Assert.Equal("ozel-alandaki-deger", TokenFinder.Find("""{"a":{"b":"ozel-alandaki-deger"},"token":"baskasi-baskasi"}""", [], "a.b"));
        Assert.Null(TokenFinder.Find("""{"ok":true}""", []));
    }

    [Fact]
    public void Reads_jwt_expiry_and_masks_token()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(15);
        Assert.Equal(exp.ToUnixTimeSeconds(), TokenFinder.ExpiresAt(Jwt(exp))!.Value.ToUnixTimeSeconds());
        Assert.Null(TokenFinder.ExpiresAt("jwt-olmayan-token"));
        Assert.Equal("eyJhbG…imza", TokenFinder.Mask(Jwt(exp)));
    }

    [Fact]
    public void Override_replaces_bearer_and_merges_cookies()
    {
        var auth = new AuthOverride("YENI", new Dictionary<string, string> { ["SESSION"] = "s2" });
        var headers = auth.ApplyTo([new("authorization", "Bearer ESKI"), new("cookie", "SESSION=s1; dil=tr"), new("accept", "*/*")]).ToList();
        Assert.Equal("Bearer YENI", headers.Single(h => h.Key == "authorization").Value);
        Assert.Equal("dil=tr; SESSION=s2", headers.Single(h => h.Key == "cookie").Value);
        Assert.Contains(new KeyValuePair<string, string>("accept", "*/*"), headers);

        var added = auth.ApplyTo([new("accept", "*/*")]).ToList();
        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer YENI"), added);
        Assert.Contains(new KeyValuePair<string, string>("Cookie", "SESSION=s2"), added);
    }
}

/// <summary>
/// Giriş akışını taklit eden sunucu: /auth/token yenileme anahtarıyla token verir ve yük dengeleme çerezi koyar;
/// /session/initiate o token ve çerezle oturum açar (oturum çerezi); /q ikisini birden ister, yoksa 401 döner.
/// </summary>
public sealed class FakePlatform : IDisposable
{
    private readonly HttpListener _listener = new();
    private int _issued;

    public FakePlatform()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    public string? ValidToken { get; private set; }

    public string? ValidSession { get; private set; }

    public int TokenRequests => _issued;

    public int InitiateRequests { get; private set; }

    public bool RejectRefresh { get; set; }

    /// <summary>Token'ın süresinin dolması: sonraki sorgu 401 alır.</summary>
    public void Expire() => ValidToken = null;

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var req = ctx.Request;
            var body = await new StreamReader(req.InputStream).ReadToEndAsync();
            var bearer = req.Headers["Authorization"]?.Replace("Bearer ", "");
            var cookie = req.Headers["Cookie"] ?? "";
            var (status, text) = (404, "{}");
            switch (req.Url!.AbsolutePath)
            {
                case "/auth/token" when !RejectRefresh && body.Contains("refresh_token=R1"):
                    var n = Interlocked.Increment(ref _issued);
                    ValidToken = TokenFinderTests.Jwt(DateTimeOffset.UtcNow.AddMinutes(10), $"t{n}");
                    ctx.Response.AppendHeader("Set-Cookie", "lb=node1; Path=/; HttpOnly");
                    (status, text) = (200, $$"""{"access_token":"{{ValidToken}}","token_type":"Bearer","expires_in":600}""");
                    break;
                case "/auth/token":
                    (status, text) = (400, """{"error":"invalid_grant","error_description":"Yenileme anahtarı geçersiz"}""");
                    break;
                case "/session/initiate" when bearer == ValidToken && cookie.Contains("lb=node1"):
                    InitiateRequests++;
                    ValidSession = $"s{InitiateRequests}";
                    ctx.Response.AppendHeader("Set-Cookie", $"SESSION={ValidSession}; Path=/");
                    (status, text) = (200, """{"ok":true}""");
                    break;
                case "/session/initiate":
                    (status, text) = (401, """{"message":"Token geçersiz"}""");
                    break;
                case "/q" when bearer is not null && bearer == ValidToken && cookie.Contains($"SESSION={ValidSession}"):
                    (status, text) = (200, $$"""{"value":[{"TC":"{{req.QueryString["tc"]}}","Ad":"Ali"}]}""");
                    break;
                case "/q":
                    (status, text) = (401, """{"error":{"message":"Oturum süresi doldu"}}""");
                    break;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose() => _listener.Close();
}

public sealed class SessionManagerTests : IDisposable
{
    private readonly FakePlatform _platform = new();

    public void Dispose() => _platform.Dispose();

    private SessionManager Manager() => new(
        HttpRequestTemplate.Parse($"curl '{_platform.BaseUrl}auth/token' -H 'content-type: application/x-www-form-urlencoded' --data-raw 'grant_type=refresh_token&refresh_token=R1'"),
        HttpRequestTemplate.Parse($"curl '{_platform.BaseUrl}session/initiate' -X POST -H 'authorization: Bearer ESKI' -H 'cookie: dil=tr'"));

    // Chrome'dan kopyalanmış, token'ı çoktan geçersiz olmuş sorgu isteği.
    private HttpRequestTemplate Query() =>
        HttpRequestTemplate.Parse($"curl '{_platform.BaseUrl}q?tc={{id}}' -H 'authorization: Bearer COK-ESKI-TOKEN' -H 'cookie: SESSION=eski; dil=tr'");

    [Fact]
    public async Task Logs_in_with_both_requests_and_uses_fresh_token_and_cookies()
    {
        using var manager = Manager();
        using var client = QuerySender.CreateClient(false);

        var (result, failure) = await manager.SendAsync(client, Query(), "10000000146");

        Assert.Null(failure);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("10000000146", result.Body);
        Assert.Equal(1, _platform.TokenRequests);
        Assert.Equal(1, _platform.InitiateRequests);
        Assert.NotNull(manager.Current!.ExpiresAt);
    }

    [Fact]
    public async Task Expired_token_is_refreshed_once_and_query_retried()
    {
        using var manager = Manager();
        using var client = QuerySender.CreateClient(false);
        Assert.Equal(200, (await manager.SendAsync(client, Query(), "1")).Result.StatusCode);

        _platform.Expire();
        var (result, failure) = await manager.SendAsync(client, Query(), "2");

        Assert.Null(failure);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, _platform.TokenRequests);
        Assert.Equal(2, _platform.InitiateRequests);
    }

    [Fact]
    public async Task Failed_refresh_reports_server_message_and_is_not_hammered()
    {
        _platform.RejectRefresh = true;
        using var manager = Manager();
        using var client = QuerySender.CreateClient(false);

        var (result, failure) = await manager.SendAsync(client, Query(), "1");
        var (_, again) = await manager.SendAsync(client, Query(), "2");

        Assert.Equal(401, result.StatusCode);
        Assert.NotNull(failure);
        Assert.Contains("HTTP 400", failure.Message);
        Assert.Contains("Yenileme anahtarı geçersiz", failure.Message);
        Assert.NotNull(again);
        // İkinci sorgu hemen ardından geldi: sunucuya yeniden giriş denemesi yapılmaz.
        Assert.Equal(0, _platform.TokenRequests);
    }

    [Fact]
    public async Task Concurrent_queries_share_a_single_refresh()
    {
        using var manager = Manager();
        using var client = QuerySender.CreateClient(false);
        await manager.EnsureAsync();

        _platform.Expire();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => manager.SendAsync(client, Query(), i.ToString())));

        Assert.All(results, r => Assert.Equal(200, r.Result.StatusCode));
        Assert.Equal(2, _platform.TokenRequests);
    }
}
