using System.Net;
using System.Net.Sockets;
using System.Text;
using WebAutomater.Core.Http;

namespace WebAutomater.Tests;

public class HttpRequestTemplateTests
{
    private static string Header(HttpRequestTemplate t, string name) =>
        t.Headers.Single(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void Parses_chrome_curl_bash()
    {
        const string text = """
            curl 'https://api.ornek.com/musteri/sorgu?tc={id}&tip=1' \
              -H 'accept: application/json, text/plain, */*' \
              -H 'accept-language: tr-TR,tr;q=0.9' \
              -b 'oturum=abc123; dil=tr' \
              -H 'content-type: application/json' \
              -H 'x-not: it'"'"'s ok' \
              --data-raw $'{"tc":"{id}","ad":"Şule \'X\'"}' \
              --compressed
            """;
        var t = HttpRequestTemplate.Parse(text);
        Assert.Equal(RequestFormat.Curl, t.Format);
        Assert.Equal("POST", t.Method);
        Assert.Equal("https://api.ornek.com/musteri/sorgu?tc={id}&tip=1", t.Url);
        Assert.Equal("oturum=abc123; dil=tr", Header(t, "Cookie"));
        Assert.Equal("it's ok", Header(t, "x-not"));
        Assert.Equal("{\"tc\":\"{id}\",\"ad\":\"Şule 'X'\"}", t.Body);
        Assert.Equal("adres, gövde", t.PlaceholderLocations);
        Assert.Empty(t.Warnings);
    }

    [Fact]
    public void Parses_chrome_curl_cmd()
    {
        const string text = """
            curl ^"https://api.ornek.com/istihbarat?no={id}^&x=1^" ^
              -H ^"accept: */*^" ^
              -H ^"cookie: s=1^" ^
              --data-raw ^"^{^\^"vkn^\^":^\^"{id}^\^"^}^" ^
              --insecure
            """;
        var t = HttpRequestTemplate.Parse(text);
        Assert.Equal("https://api.ornek.com/istihbarat?no={id}&x=1", t.Url);
        Assert.Equal("s=1", Header(t, "cookie"));
        Assert.Equal("{\"vkn\":\"{id}\"}", t.Body);
        Assert.True(t.AllowInsecure);
    }

    [Fact]
    public void Parses_chrome_fetch_and_warns_about_missing_cookies()
    {
        const string text = """
            fetch("https://api.ornek.com/m/{id}", {
              "headers": {
                "accept": "application/json",
                "authorization": "Bearer eyJ"
              },
              "referrer": "https://ornek.com/",
              "body": null,
              "method": "GET",
              "mode": "cors",
              "credentials": "include"
            });
            """;
        var t = HttpRequestTemplate.Parse(text);
        Assert.Equal(RequestFormat.Fetch, t.Format);
        Assert.Equal("GET", t.Method);
        Assert.Null(t.Body);
        Assert.Equal("Bearer eyJ", Header(t, "authorization"));
        Assert.Equal("https://ornek.com/", Header(t, "Referer"));
        Assert.Contains(t.Warnings, w => w.Contains("çerez"));
    }

    [Fact]
    public void Parses_node_fetch_with_cookie_and_body()
    {
        const string text = """
            await fetch('https://api.ornek.com/sorgu', {
              headers: { 'content-type': 'application/json', cookie: 'a=1' },
              body: '{"tc":"{id}"}',
              method: 'POST', // yorum
            });
            """;
        var t = HttpRequestTemplate.Parse(text);
        Assert.Equal("POST", t.Method);
        Assert.Equal("{\"tc\":\"{id}\"}", t.Body);
        Assert.Equal("a=1", Header(t, "cookie"));
        Assert.Empty(t.Warnings);
    }

    [Fact]
    public void Parses_raw_http_request()
    {
        const string text = "POST /api/q?id={id} HTTP/1.1\r\nHost: ornek.local:8080\r\nContent-Type: text/plain\r\nContent-Length: 9\r\n\r\nno={id}";
        var t = HttpRequestTemplate.Parse(text);
        Assert.Equal("https://ornek.local:8080/api/q?id={id}", t.Url);
        Assert.Equal("no={id}", t.Body);
        Assert.DoesNotContain(t.Headers, h => h.Key is "Host" or "Content-Length");
    }

    [Fact]
    public void Warns_when_placeholder_missing_and_rejects_garbage()
    {
        Assert.Contains(HttpRequestTemplate.Parse("curl https://ornek.com/a").Warnings, w => w.Contains("{id}"));
        Assert.Throws<RequestParseException>(() => HttpRequestTemplate.Parse("merhaba"));
        Assert.Throws<RequestParseException>(() => HttpRequestTemplate.Parse("curl 'https://ornek.com"));
        Assert.Throws<RequestParseException>(() => HttpRequestTemplate.Parse("curl -H 'a: b'"));
    }

    [Fact]
    public void Curl_get_flag_moves_data_to_query_and_attached_options_work()
    {
        var t = HttpRequestTemplate.Parse("curl -G -XGET https://ornek.com/a -d tc={id} -d x=1");
        Assert.Equal("GET", t.Method);
        Assert.Equal("https://ornek.com/a?tc={id}&x=1", t.Url);
        Assert.Null(t.Body);
    }

    [Fact]
    public void Build_substitutes_id_everywhere_and_puts_content_headers_on_body()
    {
        var t = HttpRequestTemplate.Parse("curl https://ornek.com/a/{ID} -H 'X-No: {id}' -H 'Content-Type: application/json' -d '{\"tc\":\"{id}\"}'");
        using var request = t.Build("10000000146");
        Assert.Equal("https://ornek.com/a/10000000146", request.RequestUri!.ToString());
        Assert.Equal("10000000146", request.Headers.GetValues("X-No").Single());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{\"tc\":\"10000000146\"}", request.Content.ReadAsStringAsync().Result);
    }
}

public class QuerySenderTests
{
    [Fact]
    public async Task Sends_requests_and_pretty_prints_json()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? received = null;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\"}"))
                request.Append(Encoding.UTF8.GetString(buffer, 0, await stream.ReadAsync(buffer)));
            received = request.ToString();
            var body = Encoding.UTF8.GetBytes("{\"ad\":\"Şule\",\"tc\":\"10000000146\"}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(body);
        });

        var template = HttpRequestTemplate.Parse(
            $"curl 'http://127.0.0.1:{port}/q' -H 'cookie: s=1' -H 'accept-encoding: gzip, br' -H 'content-type: application/json' --data-raw '{{\"tc\":\"{{id}}\"}}'");
        using var http = QuerySender.CreateClient(template.AllowInsecure);
        var result = await QuerySender.SendAsync(http, template, "10000000146");
        await server;
        listener.Stop();

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("\"ad\": \"Şule\"", result.Body);
        Assert.Contains("\n", result.Body);
        Assert.Contains("POST /q", received);
        Assert.Contains("Cookie: s=1", received, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{\"tc\":\"10000000146\"}", received);
    }

    [Fact]
    public async Task Connection_error_is_returned_not_thrown()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var template = HttpRequestTemplate.Parse($"curl http://127.0.0.1:{port}/{{id}}");
        using var http = QuerySender.CreateClient(false);
        var result = await QuerySender.SendAsync(http, template, "1");
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }
}

public class QueryResultReaderTests
{
    private static QueryResult Ok(string body, int status = 200, string text = "OK") =>
        new("1", "https://x", status, text, TimeSpan.Zero, "application/json", body, null);

    private static QuerySummary Read(QueryResult r) => QueryResultReader.Summarize(r, "value", "Müşteri kaydı yok");

    [Theory]
    [InlineData("""{"@odata.context":"x","value":[]}""")]
    [InlineData("""{"value":null}""")]
    [InlineData("""{"value":""}""")]
    [InlineData("""{"value":[{"@odata.etag":"1","Ad":null}]}""")]
    public void Empty_value_means_no_customer(string body)
    {
        var s = Read(Ok(body));
        Assert.Equal(QueryOutcome.Empty, s.Outcome);
        Assert.Equal("Müşteri kaydı yok", s.Text);
    }

    [Fact]
    public void Filled_value_shows_only_its_fields()
    {
        var s = Read(Ok("""{"@odata.context":"x","value":[{"@odata.etag":"e","MusteriNo":12345,"Ad":"Şule","Aktif":true,"Adres":{"Il":"İzmir"},"Not":null}]}"""));
        Assert.Equal(QueryOutcome.Found, s.Outcome);
        Assert.Equal("MusteriNo: 12345 · Ad: Şule · Aktif: Evet · Adres.Il: İzmir", s.Text);
    }

    [Fact]
    public void Multiple_records_are_numbered_lines_and_scalars_join()
    {
        var s = Read(Ok("""{"value":[{"No":1},{"No":2}]}"""));
        Assert.Equal($"1) No: 1{Environment.NewLine}2) No: 2", s.Text);
        Assert.Equal("A, B", Read(Ok("""{"value":["A","B"]}""")).Text);
        Assert.Equal("Var", Read(Ok("""{"sonuc":{"Value":"Var"}}""")).Text);
    }

    [Fact]
    public void Failures_show_status_and_server_message()
    {
        var odata = Read(Ok("""{"error":{"code":"401","message":"Oturum geçersiz"}}""", 401, "Unauthorized"));
        Assert.Equal(QueryOutcome.Failed, odata.Outcome);
        Assert.Equal("HTTP 401 Unauthorized: Oturum geçersiz", odata.Text);

        var html = Read(Ok("<html><head><style>x{}</style></head><body><h1>Sunucu hatası</h1></body></html>", 500, "Internal Server Error"));
        Assert.Equal("HTTP 500 Internal Server Error: Sunucu hatası", html.Text);

        var network = Read(new QueryResult("1", "u", null, "Hata", TimeSpan.Zero, null, "", "Bağlantı reddedildi"));
        Assert.Equal(("Bağlantı reddedildi", QueryOutcome.Failed), (network.Text, network.Outcome));
    }

    [Fact]
    public void Login_page_with_200_or_missing_field_is_failure_not_empty()
    {
        var login = Read(Ok("<html><body><form>Giriş yapın</form></body></html>"));
        Assert.Equal(QueryOutcome.Failed, login.Outcome);
        Assert.Contains("oturum", login.Text);
        Assert.Contains("Giriş yapın", login.Text);

        Assert.Equal(QueryOutcome.Failed, Read(Ok("""{"items":[]}""")).Outcome);
    }
}
