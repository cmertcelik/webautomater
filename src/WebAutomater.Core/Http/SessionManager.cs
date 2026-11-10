using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebAutomater.Core.Diagnostics;

namespace WebAutomater.Core.Http;

/// <summary>Kopyalanan istekteki Bearer token ve çerezlerin yerine konacak güncel değerler.</summary>
public sealed record AuthOverride(string? Token, IReadOnlyDictionary<string, string> Cookies)
{
    public IEnumerable<KeyValuePair<string, string>> ApplyTo(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var tokenDone = Token is null;
        var cookieDone = Cookies.Count == 0;
        foreach (var header in headers)
        {
            if (!tokenDone && header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                header.Value.TrimStart().StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                tokenDone = true;
                yield return new(header.Key, "Bearer " + Token);
            }
            else if (!cookieDone && header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                cookieDone = true;
                yield return new(header.Key, MergeCookies(header.Value, Cookies));
            }
            else
            {
                yield return header;
            }
        }

        if (!tokenDone && !headers.Any(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
            yield return new("Authorization", "Bearer " + Token);
        if (!cookieDone)
            yield return new("Cookie", MergeCookies("", Cookies));
    }

    /// <summary>Kopyadaki çerezlerden aynı adlı olanlar güncelleriyle değişir, diğerleri kalır.</summary>
    public static string MergeCookies(string existing, IReadOnlyDictionary<string, string> fresh)
    {
        var merged = new List<(string Name, string Value)>();
        foreach (var part in existing.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            if (!fresh.ContainsKey(name))
                merged.Add((name, eq < 0 ? "" : part[(eq + 1)..]));
        }

        merged.AddRange(fresh.Select(c => (c.Key, c.Value)));
        return string.Join("; ", merged.Select(c => $"{c.Name}={c.Value}"));
    }
}

/// <summary>Yanıttan token çıkarır ve JWT ise geçerlilik süresini okur.</summary>
public static partial class TokenFinder
{
    // Sık kullanılan adlar; yoksa JWT'ye benzeyen ilk metin alınır.
    private static readonly string[] Names = ["access_token", "accessToken", "token", "id_token", "idToken", "jwt", "sessionToken", "authToken", "bearer"];

    private static readonly string[] HeaderNames = ["Authorization", "X-Auth-Token", "X-Access-Token", "Token", "Access-Token"];

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$")]
    private static partial Regex JwtPattern();

    /// <param name="field">Doluysa yalnızca bu alana bakılır ("data.token" gibi noktalı yol).</param>
    public static string? Find(string body, IEnumerable<KeyValuePair<string, string>> headers, string? field = null)
    {
        if (!string.IsNullOrWhiteSpace(body) && body.TrimStart() is ['{' or '[', ..])
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!string.IsNullOrWhiteSpace(field))
                    return QueryResultReader.Find(doc.RootElement, field.Trim()) is { ValueKind: JsonValueKind.String } f ? Clean(f.GetString()) : null;
                foreach (var name in Names)
                {
                    if (QueryResultReader.Search(doc.RootElement, name) is { ValueKind: JsonValueKind.String } found &&
                        Clean(found.GetString()) is { Length: > 10 } token)
                        return token;
                }

                if (FindJwt(doc.RootElement) is { } jwt)
                    return jwt;
            }
            catch (JsonException)
            {
            }
        }
        else if (Clean(body) is { } plain && JwtPattern().IsMatch(plain))
        {
            return plain; // Yanıt yalnızca token'dan ibaret.
        }

        foreach (var name in HeaderNames)
        {
            var value = headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (Clean(value) is { Length: > 10 } token)
                return token;
        }

        return null;
    }

    private static string? FindJwt(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String when Clean(e.GetString()) is { } s && JwtPattern().IsMatch(s) => s,
        JsonValueKind.Object => e.EnumerateObject().Select(p => FindJwt(p.Value)).FirstOrDefault(t => t is not null),
        JsonValueKind.Array => e.EnumerateArray().Select(FindJwt).FirstOrDefault(t => t is not null),
        _ => null,
    };

    private static string? Clean(string? value)
    {
        var v = value?.Trim().Trim('"');
        if (v is not null && v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            v = v[7..].Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    /// <summary>JWT'nin "exp" alanı; JWT değilse ya da okunamazsa null.</summary>
    public static DateTimeOffset? ExpiresAt(string? token)
    {
        if (token is null || !JwtPattern().IsMatch(token))
            return null;
        try
        {
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Ekranda gösterilecek kısaltılmış hali: "eyJhbG…x9Zk". Token günlüğe hiç yazılmaz.</summary>
    public static string Mask(string token) => token.Length <= 14 ? new string('•', token.Length) : $"{token[..6]}…{token[^4..]}";
}

/// <summary>Oturum açma sonucu.</summary>
public sealed record LoginResult(bool Success, string Message, string? Token = null,
    IReadOnlyDictionary<string, string>? Cookies = null, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// Platforma girişte tarayıcının yaptığı istekleri tekrarlar: 1) token isteği, 2) (varsa) oturum başlatma isteği.
/// Bulunan token ve sunucunun verdiği çerezler sorgu isteklerine uygulanır. Süre dolunca ya da 401/403 gelince
/// yeniden çalışır. Token ve çerezler yalnızca bellekte tutulur, günlüğe yazılmaz.
/// </summary>
public sealed class SessionManager(HttpRequestTemplate tokenRequest, HttpRequestTemplate? sessionRequest, string? tokenField = null)
    : IDisposable
{
    private const string LogArea = "Oturum";
    private const int MaxRedirects = 5;
    // Süresi bu kadar içinde dolacak token önceden yenilenir.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    // Art arda başarısız yenilemeler sunucuyu yormasın.
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _client = CreateClient(tokenRequest.AllowInsecure || sessionRequest?.AllowInsecure == true);
    private LoginResult? _current;
    private int _version;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    public HttpRequestTemplate TokenRequest { get; } = tokenRequest;

    public HttpRequestTemplate? SessionRequest { get; } = sessionRequest;

    /// <summary>Son başarılı oturum; yoksa null.</summary>
    public LoginResult? Current => _current is { Success: true } c ? c : null;

    /// <summary>Oturum yenilendi ya da yenilenemedi; arka plandan çağrılabilir.</summary>
    public event Action<LoginResult>? Changed;

    private static HttpClient CreateClient(bool allowInsecure)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            // Yönlendirmeler elle izlenir: aradaki yanıtların verdiği çerezler (Set-Cookie) kaybolmasın.
            AllowAutoRedirect = false,
            UseDefaultCredentials = true,
        };
        if (allowInsecure)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public AuthOverride? Override => Current is { } c ? new AuthOverride(c.Token, c.Cookies ?? new Dictionary<string, string>()) : null;

    private bool IsFresh(LoginResult c) => c.ExpiresAt is not { } exp || exp - DateTimeOffset.UtcNow > RefreshMargin;

    /// <summary>Geçerli oturum yoksa ya da süresi dolmak üzereyse oturum açar.</summary>
    public async Task<LoginResult> EnsureAsync(CancellationToken token = default)
    {
        if (Current is { } c && IsFresh(c))
            return c;
        return await RefreshAsync(_version, token);
    }

    /// <summary>
    /// Oturumu yeniler. <paramref name="seenVersion"/> eskiyse (başka bir sorgu o arada yenilediyse) yeniden açılmaz.
    /// </summary>
    public Task<LoginResult> RefreshAsync(int seenVersion, CancellationToken token = default) =>
        RefreshCoreAsync(seenVersion, force: false, token);

    /// <summary>Her durumda yeniden giriş yapar (ayar ekranındaki "Şimdi dene").</summary>
    public Task<LoginResult> ForceRefreshAsync(CancellationToken token = default) => RefreshCoreAsync(_version, force: true, token);

    private async Task<LoginResult> RefreshCoreAsync(int seenVersion, bool force, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!force && seenVersion != _version && Current is { } newer)
                return newer;
            if (!force && _current is { Success: false } failed && DateTimeOffset.UtcNow - _lastAttempt < MinRefreshInterval)
                return failed;
            _lastAttempt = DateTimeOffset.UtcNow;
            var result = await LoginAsync(token);
            _current = result;
            if (result.Success)
                _version++;
            if (result.Success)
                AppLog.Info(LogArea, result.Message);
            else
                AppLog.Warning(LogArea, "Oturum açılamadı: " + result.Message);
            Changed?.Invoke(result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public int Version => _version;

    /// <summary>Giriş isteklerini dener, sonucu döndürür; durumu değiştirmez (ayar ekranındaki "Dene" için de kullanılır).</summary>
    public async Task<LoginResult> LoginAsync(CancellationToken token = default)
    {
        var cookies = new Dictionary<string, string>();
        try
        {
            var (status, body, headers) = await SendAsync(TokenRequest, null, cookies, token);
            if (status is not (>= 200 and < 300))
                return Fail("Token isteği", status, body);
            var bearer = TokenFinder.Find(body, headers, tokenField);
            if (bearer is null)
                return new LoginResult(false, "Token isteğinin yanıtında token bulunamadı. Ayarda token alanının adını yazın (ör. access_token).");
            var source = "token isteğinden";

            if (SessionRequest is not null)
            {
                var (sStatus, sBody, sHeaders) = await SendAsync(SessionRequest, bearer, cookies, token);
                if (sStatus is not (>= 200 and < 300))
                    return Fail("Oturum başlatma isteği", sStatus, sBody);
                // Bazı platformlar oturum başlatınca yeni (oturuma bağlı) bir token verir.
                if (TokenFinder.Find(sBody, sHeaders, tokenField) is { } sessionToken && sessionToken != bearer &&
                    (TokenFinder.ExpiresAt(sessionToken) is not null || TokenFinder.ExpiresAt(bearer) is null))
                {
                    bearer = sessionToken;
                    source = "oturum başlatma isteğinden";
                }
            }

            var expires = TokenFinder.ExpiresAt(bearer);
            var validity = expires is { } e ? $", {Math.Max(0, (e - DateTimeOffset.UtcNow).TotalMinutes):0} dk geçerli" : "";
            var cookieText = cookies.Count > 0 ? $", {cookies.Count} çerez" : "";
            return new LoginResult(true, $"Oturum açıldı (token {source}{validity}{cookieText}).", bearer, cookies, expires);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LoginResult(false, ex is TaskCanceledException ? "Zaman aşımı: sunucu 60 saniyede yanıt vermedi." : AppLog.Describe(ex));
        }
    }

    private static LoginResult Fail(string step, int status, string body)
    {
        var message = QueryResultReader.ServerMessage(body);
        return new LoginResult(false, $"{step} başarısız: HTTP {status}" + (message is null ? "" : $": {message}") +
                                      (status is 400 or 401 or 403 ? ". Giriş bilgileri ya da yenileme anahtarı geçersiz olabilir; isteği Chrome'dan yeniden kopyalayın." : ""));
    }

    private async Task<(int Status, string Body, List<KeyValuePair<string, string>> Headers)> SendAsync(
        HttpRequestTemplate template, string? bearer, Dictionary<string, string> cookies, CancellationToken token)
    {
        var request = template.Build("", new AuthOverride(bearer, new Dictionary<string, string>(cookies)));
        for (var redirect = 0; ; redirect++)
        {
            var requestUri = request.RequestUri!;
            var method = request.Method;
            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, token);
            }
            finally
            {
                request.Dispose();
            }

            using (response)
            {
                Collect(response, cookies);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } location && redirect < MaxRedirects)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(requestUri, location);
                    request = new HttpRequestMessage(status is 307 or 308 ? method : HttpMethod.Get, next);
                    foreach (var (name, value) in new AuthOverride(bearer, cookies).ApplyTo(
                                 template.Headers.Where(h => !h.Key.StartsWith("content-", StringComparison.OrdinalIgnoreCase))))
                        request.Headers.TryAddWithoutValidation(name, value);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(token);
                var headers = response.Headers.Concat(response.Content.Headers)
                    .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))).ToList();
                return (status, body, headers);
            }
        }
    }

    private static void Collect(HttpResponseMessage response, Dictionary<string, string> cookies)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return;
        foreach (var value in values)
        {
            var pair = value.Split(';', 2)[0];
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = pair[..eq].Trim();
            var cookie = pair[(eq + 1)..].Trim();
            // Silme amaçlı (süresi geçmiş/boş) çerezler kaldırılır.
            if (cookie.Length == 0 || value.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase))
                cookies.Remove(name);
            else
                cookies[name] = cookie;
        }
    }

    /// <summary>Oturum sona ermiş görünüyor mu: 401/403, ya da JSON beklenirken gelen giriş sayfası.</summary>
    public static bool LooksExpired(QueryResult result) =>
        result.StatusCode is 401 or 403 ||
        result.StatusCode is >= 200 and < 300 && result.Body.TrimStart().StartsWith('<');

    /// <summary>
    /// Sorgu isteğini güncel oturumla gönderir. Oturum yoksa açar; yanıt oturumun düştüğünü gösteriyorsa
    /// oturumu yenileyip aynı isteği bir kez daha gönderir.
    /// </summary>
    public async Task<(QueryResult Result, LoginResult? Failure)> SendAsync(HttpClient client, HttpRequestTemplate template, string id,
        CancellationToken token = default)
    {
        var login = await EnsureAsync(token);
        if (!login.Success)
            return (await QuerySender.SendAsync(client, template, id, token), login);
        var seen = _version;
        var result = await QuerySender.SendAsync(client, template, id, token, Override);
        if (!LooksExpired(result))
            return (result, null);

        AppLog.Info(LogArea, $"Sorgu oturum hatası verdi (HTTP {result.StatusCode}); oturum yenileniyor.");
        var renewed = await RefreshAsync(seen, token);
        if (!renewed.Success)
            return (result, renewed);
        return (await QuerySender.SendAsync(client, template, id, token, Override), null);
    }

    public void Dispose()
    {
        _client.Dispose();
        _gate.Dispose();
    }
}
