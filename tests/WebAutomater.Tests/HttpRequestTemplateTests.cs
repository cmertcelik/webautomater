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
