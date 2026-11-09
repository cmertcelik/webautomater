using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace WebAutomater.Core.Http;

public sealed class RequestParseException(string message) : Exception(message);

public enum RequestFormat
{
    Curl,
    Fetch,
    RawHttp,
}

/// <summary>
/// Tarayıcının geliştirici araçlarından kopyalanan bir istek (cURL, fetch ya da ham HTTP).
/// İçindeki <c>{id}</c> yer tutucusu her sorguda numarayla değiştirilir.
/// </summary>
public sealed partial record HttpRequestTemplate(
    RequestFormat Format,
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body,
    bool AllowInsecure,
    IReadOnlyList<string> Warnings)
{
    public const string Placeholder = "{id}";

    // HttpClient bunları kendisi yönetir; kopyadakiler gönderilirse istek bozulur.
    private static readonly HashSet<string> DroppedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "connection", "accept-encoding", "transfer-encoding", "keep-alive", "upgrade", "te",
    };

    [GeneratedRegex(@"\{\s*id\s*\}|%7B\s*id\s*%7D", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderPattern();

    /// <summary>Metnin türünü kendisi anlar. Anlaşılamazsa <see cref="RequestParseException"/> atar.</summary>
    public static HttpRequestTemplate Parse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            throw new RequestParseException("İstek boş. Chrome'da isteğe sağ tıklayıp Copy > Copy as cURL ya da Copy as fetch seçin ve buraya yapıştırın.");

        var template = trimmed switch
        {
            _ when CurlParser.Looks(trimmed) => CurlParser.Parse(trimmed),
            _ when FetchParser.Looks(trimmed) => FetchParser.Parse(trimmed),
            _ when RawHttpParser.Looks(trimmed) => RawHttpParser.Parse(trimmed),
            _ => throw new RequestParseException("İstek tanınmadı. cURL (\"curl ...\"), fetch (\"fetch(...)\") ya da ham HTTP isteği yapıştırın."),
        };
        return template.Normalize();
    }

    private HttpRequestTemplate Normalize()
    {
        if (!Uri.TryCreate(Substitute(Url, "0"), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new RequestParseException($"Adres geçersiz: {Url}");

        var warnings = Warnings.ToList();
        var headers = Headers
            .Where(h => !h.Key.StartsWith(':') && !DroppedHeaders.Contains(h.Key))
            .ToList();
        if (Format == RequestFormat.Fetch && !headers.Any(h => h.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("Tarayıcının \"Copy as fetch\" kopyası çerezleri içermez. Sistem oturum çerezi istiyorsa \"Copy as cURL\" ya da \"Copy as fetch (Node.js)\" kullanın.");
        if (!ContainsPlaceholderIn(Url) && !headers.Any(h => ContainsPlaceholderIn(h.Value)) && !ContainsPlaceholderIn(Body))
            warnings.Add("İstekte {id} yok: numara yerine konacak yeri {id} yazarak işaretleyin, yoksa her numara için aynı istek gider.");
        return this with { Method = Method.ToUpperInvariant(), Headers = headers, Warnings = warnings };
    }

    public bool HasPlaceholder =>
        ContainsPlaceholderIn(Url) || Headers.Any(h => ContainsPlaceholderIn(h.Value)) || ContainsPlaceholderIn(Body);

    private static bool ContainsPlaceholderIn(string? text) => text is not null && PlaceholderPattern().IsMatch(text);

    /// <summary>{id}'nin nerede geçtiği, kullanıcıya göstermek için.</summary>
    public string PlaceholderLocations
    {
        get
        {
            var places = new List<string>();
            if (ContainsPlaceholderIn(Url))
                places.Add("adres");
            if (Headers.Any(h => ContainsPlaceholderIn(h.Value)))
                places.Add("başlık");
            if (ContainsPlaceholderIn(Body))
                places.Add("gövde");
            return string.Join(", ", places);
        }
    }

    public string ResolveUrl(string id) => Substitute(Url, Uri.EscapeDataString(id));

    private static string Substitute(string text, string id) => PlaceholderPattern().Replace(text, id);

    /// <summary>Numarayı yerine koyup gönderilecek isteği hazırlar.</summary>
    public HttpRequestMessage Build(string id)
    {
        var request = new HttpRequestMessage(new HttpMethod(Method), ResolveUrl(id));
        var body = Body is null ? null : Substitute(Body, id);
        if (body is not null)
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));

        foreach (var (name, rawValue) in Headers)
        {
            var value = Substitute(rawValue, id);
            if (request.Headers.TryAddWithoutValidation(name, value))
                continue;
            // İçerik başlıkları (Content-Type vb.) yalnızca gövdeye eklenebilir.
            if (request.Content is null)
                continue;
            request.Content.Headers.Remove(name);
            request.Content.Headers.TryAddWithoutValidation(name, value);
        }

        if (request.Content is not null && request.Content.Headers.ContentType is null)
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        return request;
    }

    /// <summary>Kısa özet: "POST https://ornek/api · 9 başlık · gövde var".</summary>
    public string Describe()
    {
        var parts = new List<string> { $"{Method} {Url}", $"{Headers.Count} başlık" };
        if (Body is not null)
            parts.Add("gövde var");
        if (HasPlaceholder)
            parts.Add($"{{id}}: {PlaceholderLocations}");
        if (AllowInsecure)
            parts.Add("sertifika denetimi kapalı (-k)");
        return string.Join(" · ", parts);
    }

    internal static KeyValuePair<string, string> SplitHeader(string line)
    {
        var colon = line.IndexOf(':', line.StartsWith(':') ? 1 : 0);
        if (colon <= 0)
            throw new RequestParseException($"Başlık anlaşılamadı: {line}");
        return new(line[..colon].Trim(), line[(colon + 1)..].Trim());
    }
}

/// <summary>"Copy as cURL" — bash (macOS/Linux) ve cmd (Windows) biçimleri.</summary>
internal static class CurlParser
{
    // Değer alan ama isteği değiştirmeyen seçenekler: değerleri atlanır.
    private static readonly HashSet<string> IgnoredWithValue =
    [
        "-o", "--output", "-m", "--max-time", "--connect-timeout", "--retry", "-w", "--write-out", "-x", "--proxy",
        "--cacert", "--cert", "--key", "-c", "--cookie-jar", "--resolve", "--limit-rate", "-r", "--range", "-D", "--dump-header",
    ];

    public static bool Looks(string text) => text.StartsWith("curl ", StringComparison.OrdinalIgnoreCase) ||
                                             text.StartsWith("curl.exe ", StringComparison.OrdinalIgnoreCase) ||
                                             text.StartsWith("curl\t", StringComparison.OrdinalIgnoreCase);

    public static HttpRequestTemplate Parse(string text)
    {
        var tokens = IsCmd(text) ? Tokenize(UnescapeCmd(text), cmd: true) : Tokenize(text, cmd: false);
        string? url = null, method = null;
        var headers = new List<KeyValuePair<string, string>>();
        var data = new List<string>();
        var warnings = new List<string>();
        bool insecure = false, get = false, head = false;

        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            string Next(string option) => ++i < tokens.Count ? tokens[i] : throw new RequestParseException($"{option} seçeneğinin değeri yok.");

            if (!token.StartsWith('-') || token == "-")
            {
                url ??= token;
                continue;
            }

            // "-XPOST" gibi bitişik kısa seçenekler.
            if (token.Length > 2 && token[1] != '-' && token[1] is 'X' or 'H' or 'd' or 'b' or 'A' or 'e' or 'u')
            {
                tokens.Insert(i + 1, token[2..]);
                token = token[..2];
            }

            switch (token)
            {
                case "-X" or "--request":
                    method = Next(token);
                    break;
                case "-H" or "--header":
                    var header = Next(token);
                    // "-H 'X-Bos;'" curl'de değeri boş başlıktır.
                    headers.Add(header.EndsWith(';') && !header.Contains(':')
                        ? new(header.TrimEnd(';'), "")
                        : HttpRequestTemplate.SplitHeader(header));
                    break;
                case "-b" or "--cookie":
                    headers.Add(new("Cookie", Next(token)));
                    break;
                case "-A" or "--user-agent":
                    headers.Add(new("User-Agent", Next(token)));
                    break;
                case "-e" or "--referer":
                    headers.Add(new("Referer", Next(token)));
                    break;
                case "-u" or "--user":
                    headers.Add(new("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Next(token)))));
                    break;
                case "-d" or "--data" or "--data-raw" or "--data-binary" or "--data-ascii":
                    var value = Next(token);
                    if (value.StartsWith('@') && token != "--data-raw")
                        throw new RequestParseException($"Gövde dosyadan okunuyor ({value}); dosya içeriği isteğe yazılmalı.");
                    data.Add(token is "--data-binary" or "--data-raw" ? value : value.Replace("\r", "").Replace("\n", ""));
                    break;
                case "--data-urlencode":
                    var pair = Next(token);
                    var eq = pair.IndexOf('=');
                    data.Add(eq < 0 ? Uri.EscapeDataString(pair) : pair[..(eq + 1)] + Uri.EscapeDataString(pair[(eq + 1)..]));
                    break;
                case "--json":
                    data.Add(Next(token));
                    if (!headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                        headers.Add(new("Content-Type", "application/json"));
                    if (!headers.Any(h => h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)))
                        headers.Add(new("Accept", "application/json"));
                    break;
                case "-F" or "--form":
                    throw new RequestParseException("Dosya yüklemeli (multipart, -F) istekler desteklenmiyor.");
                case "--url":
                    url = Next(token);
                    break;
                case "-G" or "--get":
                    get = true;
                    break;
                case "-I" or "--head":
                    head = true;
                    break;
                case "-k" or "--insecure":
                    insecure = true;
                    break;
                default:
                    if (IgnoredWithValue.Contains(token))
                        Next(token);
                    // Diğerleri (--compressed, -s, -L, -i, --http2 ...) yalnızca bayrak.
                    break;
            }
        }

        if (url is null)
            throw new RequestParseException("cURL komutunda adres bulunamadı.");
        string? body = data.Count > 0 ? string.Join("&", data) : null;
        if (get && body is not null)
        {
            url += (url.Contains('?') ? "&" : "?") + body;
            body = null;
        }

        method ??= head ? "HEAD" : body is not null ? "POST" : "GET";
        return new HttpRequestTemplate(RequestFormat.Curl, method, url, headers, body, insecure, warnings);
    }

    /// <summary>Chrome'un "Copy as cURL (cmd)" çıktısı: özel karakterler ^ ile kaçırılır, satırlar " ^" ile biter.</summary>
    private static bool IsCmd(string text) =>
        text.Contains("^\"", StringComparison.Ordinal) || Regex.IsMatch(text, @"\s\^\r?\n");

    private static string UnescapeCmd(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '^')
            {
                sb.Append(text[i]);
                continue;
            }

            // "^" + satır sonu: satır devam ediyor.
            if (i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            else if (i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n')
                i += 2;
            else if (i + 1 < text.Length)
                sb.Append(text[++i]);
        }

        // cmd'de %% tek % demektir.
        return sb.ToString().Replace("%%", "%");
    }

    /// <summary>Kabuk gibi sözcüklere ayırır: '...', "...", $'...', ters bölü ve satır devamı.</summary>
    internal static List<string> Tokenize(string text, bool cmd)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var i = 0;

        void End()
        {
            if (inToken)
                tokens.Add(current.ToString());
            current.Clear();
            inToken = false;
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                End();
                i++;
                continue;
            }

            inToken = true;
            if (c == '\\' && !cmd)
            {
                // Satır devamı ya da kaçırılmış karakter.
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i += 2;
                else if (i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n')
                    i += 3;
                else
                {
                    if (i + 1 < text.Length)
                        current.Append(text[i + 1]);
                    i += 2;
                }

                if (current.Length == 0)
                    inToken = false;
                continue;
            }

            if (c == '$' && !cmd && i + 1 < text.Length && text[i + 1] == '\'')
            {
                i = ReadAnsiC(text, i + 2, current);
                continue;
            }

            if (c == '\'' && !cmd)
            {
                var end = text.IndexOf('\'', i + 1);
                if (end < 0)
                    throw new RequestParseException("Kapanmayan tek tırnak var; komutun tamamını kopyalayın.");
                current.Append(text, i + 1, end - i - 1);
                i = end + 1;
                continue;
            }

            if (c == '"')
            {
                i++;
                while (true)
                {
                    if (i >= text.Length)
                        throw new RequestParseException("Kapanmayan çift tırnak var; komutun tamamını kopyalayın.");
                    var d = text[i];
                    if (d == '"')
                    {
                        i++;
                        break;
                    }

                    if (d == '\\' && i + 1 < text.Length && (cmd ? text[i + 1] is '"' or '\\' : text[i + 1] is '"' or '\\' or '$' or '`' or '\n'))
                    {
                        if (text[i + 1] != '\n')
                            current.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }

                    current.Append(d);
                    i++;
                }

                continue;
            }

            current.Append(c);
            i++;
        }

        End();
        return tokens;
    }

    /// <summary>bash $'...' dizgisi: \n, \t, \', \\, \xHH, \uHHHH kaçışları.</summary>
    private static int ReadAnsiC(string text, int i, StringBuilder sb)
    {
        var bytes = new List<byte>();

        void FlushBytes()
        {
            if (bytes.Count == 0)
                return;
            sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\'')
            {
                FlushBytes();
                return i + 1;
            }

            if (c != '\\' || i + 1 >= text.Length)
            {
                FlushBytes();
                sb.Append(c);
                i++;
                continue;
            }

            var e = text[i + 1];
            i += 2;
            if (e == 'x')
            {
                var hex = TakeHex(text, ref i, 2);
                bytes.Add((byte)hex);
                continue;
            }

            FlushBytes();
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '0': sb.Append('\0'); break;
                case 'u': sb.Append(char.ConvertFromUtf32(TakeHex(text, ref i, 4))); break;
                case 'U': sb.Append(char.ConvertFromUtf32(TakeHex(text, ref i, 8))); break;
                default: sb.Append(e); break; // \' \\ \" \?
            }
        }

        throw new RequestParseException("Kapanmayan $'...' dizgisi var; komutun tamamını kopyalayın.");
    }

    private static int TakeHex(string text, ref int i, int max)
    {
        var start = i;
        while (i < text.Length && i - start < max && Uri.IsHexDigit(text[i]))
            i++;
        return i == start ? 0 : Convert.ToInt32(text[start..i], 16);
    }
}

/// <summary>"Copy as fetch" ve "Copy as fetch (Node.js)".</summary>
internal static class FetchParser
{
    public static bool Looks(string text) => Regex.IsMatch(text, @"(^|[\s=;(])fetch\s*\(");

    public static HttpRequestTemplate Parse(string text)
    {
        var start = Regex.Match(text, @"fetch\s*\(");
        var reader = new JsReader(text, start.Index + start.Length);
        var urlValue = reader.ReadValue();
        if (urlValue is not string url)
            throw new RequestParseException("fetch'in ilk değeri adres olmalı.");

        var method = "GET";
        string? body = null;
        var headers = new List<KeyValuePair<string, string>>();
        reader.SkipSpace();
        if (reader.TryConsume(','))
        {
            reader.SkipSpace();
            if (!reader.Peek(')') && reader.ReadValue() is Dictionary<string, object?> options)
            {
                if (options.TryGetValue("method", out var m) && m is string ms)
                    method = ms;
                if (options.TryGetValue("body", out var b))
                    body = b switch
                    {
                        null => null,
                        string s => s,
                        _ => throw new RequestParseException("fetch gövdesi metin olmalı (JSON.stringify gibi ifadeler desteklenmiyor; Chrome'un kopyasını kullanın)."),
                    };
                if (options.TryGetValue("headers", out var h) && h is Dictionary<string, object?> map)
                {
                    foreach (var (name, value) in map)
                        headers.Add(new(name, value?.ToString() ?? ""));
                }

                if (options.TryGetValue("referrer", out var r) && r is string referrer &&
                    !headers.Any(x => x.Key.Equals("referer", StringComparison.OrdinalIgnoreCase)))
                    headers.Add(new("Referer", referrer));
            }
        }

        if (body is not null && method == "GET")
            method = "POST";
        return new HttpRequestTemplate(RequestFormat.Fetch, method, url, headers, body, false, []);
    }

    /// <summary>fetch seçeneklerindeki JavaScript nesnesini okuyan küçük ayrıştırıcı (tırnaksız anahtarlar, ' " ` dizgileri).</summary>
    private sealed class JsReader(string text, int position)
    {
        private int _pos = position;

        public void SkipSpace()
        {
            while (_pos < text.Length)
            {
                if (char.IsWhiteSpace(text[_pos]))
                    _pos++;
                else if (text.AsSpan(_pos).StartsWith("//"))
                    _pos = text.IndexOf('\n', _pos) is var n and >= 0 ? n : text.Length;
                else if (text.AsSpan(_pos).StartsWith("/*"))
                    _pos = text.IndexOf("*/", _pos + 2, StringComparison.Ordinal) is var e and >= 0 ? e + 2 : text.Length;
                else
                    break;
            }
        }

        public bool Peek(char c)
        {
            SkipSpace();
            return _pos < text.Length && text[_pos] == c;
        }

        public bool TryConsume(char c)
        {
            if (!Peek(c))
                return false;
            _pos++;
            return true;
        }

        public object? ReadValue()
        {
            SkipSpace();
            if (_pos >= text.Length)
                throw Error("beklenmedik son");
            var c = text[_pos];
            if (c is '"' or '\'' or '`')
                return ReadString();
            if (c == '{')
                return ReadObject();
            if (c == '[')
                return ReadArray();
            var word = ReadWord();
            return word switch
            {
                "null" or "undefined" => null,
                "true" => true,
                "false" => false,
                _ when double.TryParse(word, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => word,
                _ => throw Error($"desteklenmeyen ifade: {word}"),
            };
        }

        private Dictionary<string, object?> ReadObject()
        {
            _pos++;
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                SkipSpace();
                if (TryConsume('}'))
                    return result;
                var key = Peek('"') || Peek('\'') || Peek('`') ? ReadString() : ReadWord();
                if (!TryConsume(':'))
                    throw Error("':' bekleniyordu");
                result[key] = ReadValue();
                if (!TryConsume(',') && !Peek('}'))
                    throw Error("',' ya da '}' bekleniyordu");
            }
        }

        private List<object?> ReadArray()
        {
            _pos++;
            var result = new List<object?>();
            while (true)
            {
                if (TryConsume(']'))
                    return result;
                result.Add(ReadValue());
                if (!TryConsume(',') && !Peek(']'))
                    throw Error("',' ya da ']' bekleniyordu");
            }
        }

        private string ReadWord()
        {
            SkipSpace();
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '$' or '.' or '-' or '+'))
                _pos++;
            if (start == _pos)
                throw Error($"beklenmedik karakter '{text[_pos]}'");
            return text[start.._pos];
        }

        private string ReadString()
        {
            var quote = text[_pos++];
            var sb = new StringBuilder();
            while (_pos < text.Length)
            {
                var c = text[_pos++];
                if (c == quote)
                    return sb.ToString();
                if (quote == '`' && c == '$' && _pos < text.Length && text[_pos] == '{')
                    throw Error("şablon dizgisindeki ${...} ifadeleri desteklenmiyor");
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (_pos >= text.Length)
                    break;
                var e = text[_pos++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '0': sb.Append('\0'); break;
                    case '\n': break;
                    case 'x':
                        sb.Append((char)Convert.ToInt32(text.Substring(_pos, 2), 16));
                        _pos += 2;
                        break;
                    case 'u' when _pos < text.Length && text[_pos] == '{':
                        var close = text.IndexOf('}', _pos);
                        sb.Append(char.ConvertFromUtf32(Convert.ToInt32(text[(_pos + 1)..close], 16)));
                        _pos = close + 1;
                        break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(text.Substring(_pos, 4), 16));
                        _pos += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }

            throw Error("kapanmayan tırnak; kodun tamamını kopyalayın");
        }

        private RequestParseException Error(string what) => new($"fetch kodu okunamadı: {what}.");
    }
}

/// <summary>Ham HTTP isteği: "POST /yol HTTP/1.1", başlıklar, boş satır, gövde. Adres Host başlığından kurulur.</summary>
internal static class RawHttpParser
{
    private static readonly Regex RequestLine = new(@"^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\s+(\S+)(\s+HTTP/[\d.]+)?\s*$",
        RegexOptions.IgnoreCase);

    public static bool Looks(string text) => RequestLine.IsMatch(text.Split('\n')[0].TrimEnd('\r'));

    public static HttpRequestTemplate Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var split = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var head = split < 0 ? normalized : normalized[..split];
        var body = split < 0 ? null : normalized[(split + 2)..].TrimEnd('\n');
        var lines = head.Split('\n');
        var match = RequestLine.Match(lines[0]);
        var headers = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(HttpRequestTemplate.SplitHeader).ToList();

        var target = match.Groups[2].Value;
        if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var host = headers.FirstOrDefault(h => h.Key.Equals("host", StringComparison.OrdinalIgnoreCase) || h.Key == ":authority").Value;
            if (string.IsNullOrEmpty(host))
                throw new RequestParseException("Ham HTTP isteğinde Host başlığı yok; adres kurulamadı.");
            var scheme = headers.FirstOrDefault(h => h.Key == ":scheme").Value ?? "https";
            target = $"{scheme}://{host}{target}";
        }

        return new HttpRequestTemplate(RequestFormat.RawHttp, match.Groups[1].Value, target, headers,
            string.IsNullOrEmpty(body) ? null : body, false, []);
    }
}

public sealed record QueryResult(
    string Id,
    string Url,
    int? StatusCode,
    string StatusText,
    TimeSpan Elapsed,
    string? ContentType,
    string Body,
    string? Error)
{
    public bool IsSuccess => Error is null && StatusCode is >= 200 and < 300;
}

/// <summary>Şablonla istek gönderir.</summary>
public static class QuerySender
{
    private const int MaxBodyChars = 2_000_000;

    public static HttpClient CreateClient(bool allowInsecure)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // Çerezler kopyalanan Cookie başlığından gelir; kendi çerez kabı başlığı ezmesin.
            UseCookies = false,
            AllowAutoRedirect = true,
            // Kurum içi sistemler Windows oturumuyla (NTLM/Kerberos) kimlik isteyebilir.
            UseDefaultCredentials = true,
        };
        if (allowInsecure)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public static async Task<QueryResult> SendAsync(HttpClient client, HttpRequestTemplate template, string id,
        CancellationToken token = default)
    {
        var url = template.ResolveUrl(id);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var request = template.Build(id);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, token);
            var body = await response.Content.ReadAsStringAsync(token);
            watch.Stop();
            var contentType = response.Content.Headers.ContentType?.MediaType;
            return new QueryResult(id, url, (int)response.StatusCode, response.ReasonPhrase ?? response.StatusCode.ToString(),
                watch.Elapsed, contentType, Format(body, contentType), null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException)
        {
            var message = ex is TaskCanceledException ? "Zaman aşımı: sunucu 60 saniyede yanıt vermedi." : Describe(ex);
            return new QueryResult(id, url, null, "Hata", watch.Elapsed, null, "", message);
        }
    }

    private static string Describe(Exception ex)
    {
        var messages = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (!messages.Contains(e.Message))
                messages.Add(e.Message);
        }

        return string.Join(" → ", messages);
    }

    /// <summary>JSON ise okunaklı girintilenir; Türkçe karakterler kaçırılmaz.</summary>
    public static string Format(string body, string? contentType)
    {
        if (body.Length > MaxBodyChars)
            body = body[..MaxBodyChars] + "\n\n... (yanıt çok uzun, kısaltıldı)";
        var trimmed = body.TrimStart();
        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true && !(trimmed.StartsWith('{') || trimmed.StartsWith('[')))
            return body;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (System.Text.Json.JsonException)
        {
            return body;
        }
    }
}
