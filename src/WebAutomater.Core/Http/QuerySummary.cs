using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebAutomater.Core.Http;

public enum QueryOutcome
{
    /// <summary>Alan dolu: kayıt var.</summary>
    Found,

    /// <summary>Alan boş: kayıt yok.</summary>
    Empty,

    /// <summary>İstek başarısız ya da yanıt beklenen biçimde değil: elle kontrol edilmeli.</summary>
    Failed,
}

/// <param name="Text">Gösterilecek metin (çok satırlı olabilir).</param>
public sealed record QuerySummary(QueryOutcome Outcome, string Text)
{
    /// <summary>Listede tek satırda gösterilecek kısa hali.</summary>
    public string Line
    {
        get
        {
            var line = Text.ReplaceLineEndings(" · ");
            return line.Length > 160 ? line[..160] + "…" : line;
        }
    }
}

/// <summary>Sorgu yanıtından yalnızca istenen alanı (ör. "value") çıkarır.</summary>
public static partial class QueryResultReader
{
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <param name="field">Gösterilecek alan; "value" ya da "data.items" gibi noktalı yol. Boşsa yanıtın tamamı.</param>
    /// <param name="emptyText">Alan boşsa yazılacak metin ("Müşteri kaydı yok").</param>
    public static QuerySummary Summarize(QueryResult result, string? field, string emptyText)
    {
        if (result.Error is not null)
            return new QuerySummary(QueryOutcome.Failed, result.Error);

        if (result.StatusCode is not (>= 200 and < 300))
        {
            var message = ServerMessage(result.Body);
            return new QuerySummary(QueryOutcome.Failed,
                $"HTTP {result.StatusCode} {result.StatusText}" + (message is null ? "" : $": {message}"));
        }

        if (string.IsNullOrWhiteSpace(field))
            return new QuerySummary(QueryOutcome.Found, result.Body.Length == 0 ? emptyText : result.Body);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(result.Body);
        }
        catch (JsonException)
        {
            // Oturum süresi dolunca birçok sistem 200 ile giriş sayfası döner: "kayıt yok" sanılmamalı.
            var text = Plain(result.Body);
            return new QuerySummary(QueryOutcome.Failed,
                "Yanıt beklenen biçimde değil (oturum süresi dolmuş olabilir; isteği Chrome'dan yeniden kopyalayın)" +
                (text.Length > 0 ? $": {text}" : "."));
        }

        using (doc)
        {
            if (Find(doc.RootElement, field.Trim()) is not { } value)
                return new QuerySummary(QueryOutcome.Failed, $"Yanıtta \"{field}\" alanı yok; elle kontrol edin.");
            if (IsEmpty(value))
                return new QuerySummary(QueryOutcome.Empty, emptyText);
            return new QuerySummary(QueryOutcome.Found, Format(value));
        }
    }

    /// <summary>"value" ya da "data.items": önce verilen yol, bulunamazsa yanıtın herhangi bir yerindeki aynı adlı ilk alan.</summary>
    internal static JsonElement? Find(JsonElement root, string path)
    {
        var current = root;
        var ok = true;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || Property(current, part) is not { } next)
            {
                ok = false;
                break;
            }

            current = next;
        }

        if (ok)
            return current;
        var last = path.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return last is null ? null : Search(root, last);
    }

    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value;
        }

        return null;
    }

    internal static JsonElement? Search(JsonElement element, string name)
    {
        // Genişlik öncelikli: üstteki alan, içteki aynı adlı alandan önce gelir.
        var queue = new Queue<JsonElement>([element]);
        while (queue.Count > 0)
        {
            var e = queue.Dequeue();
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (Property(e, name) is { } found)
                    return found;
                foreach (var p in e.EnumerateObject())
                    queue.Enqueue(p.Value);
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray())
                    queue.Enqueue(item);
            }
        }

        return null;
    }

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => !value.EnumerateArray().Any(i => !IsEmpty(i)),
        JsonValueKind.Object => !value.EnumerateObject().Any(p => !IsMeta(p.Name) && !IsEmpty(p.Value)),
        _ => false,
    };

    // OData gibi sistemlerin "@odata.etag" türü teknik alanları gösterilmez.
    private static bool IsMeta(string name) => name.StartsWith('@') || name.StartsWith("__");

    /// <summary>Değeri okunur satırlara çevirir: nesne "Ad: Ali · Soyad: Veli", birden çok kayıt ayrı satırlarda.</summary>
    private static string Format(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().Where(i => !IsEmpty(i)).ToList();
            if (items.All(i => i.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
                return string.Join(", ", items.Select(Scalar));
            return string.Join(Environment.NewLine, items.Select((item, n) => (items.Count > 1 ? $"{n + 1}) " : "") + Flatten(item)));
        }

        return value.ValueKind == JsonValueKind.Object ? Flatten(value) : Scalar(value);
    }

    private static string Flatten(JsonElement element)
    {
        var parts = new List<string>();

        void Walk(JsonElement e, string prefix)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject().Where(p => !IsMeta(p.Name) && !IsEmpty(p.Value)))
                        Walk(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}");
                    break;
                case JsonValueKind.Array:
                    var items = e.EnumerateArray().Where(i => !IsEmpty(i)).ToList();
                    if (items.All(i => i.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
                        parts.Add($"{prefix}: {string.Join(", ", items.Select(Scalar))}");
                    else
                        for (var i = 0; i < items.Count; i++)
                            Walk(items[i], $"{prefix}[{i + 1}]");
                    break;
                default:
                    parts.Add(prefix.Length == 0 ? Scalar(e) : $"{prefix}: {Scalar(e)}");
                    break;
            }
        }

        Walk(element, "");
        return string.Join(" · ", parts);
    }

    private static string Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.True => "Evet",
        JsonValueKind.False => "Hayır",
        _ => e.GetRawText(),
    };

    /// <summary>Hata yanıtındaki açıklama: JSON'da message/error/detail/title, değilse sayfanın kısa metni.</summary>
    internal static string? ServerMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var name in new[] { "message", "error_description", "detail", "title", "error", "Message", "errorMessage" })
            {
                if (Search(doc.RootElement, name) is not { } found)
                    continue;
                // OData: {"error": {"code": "...", "message": "..."}}
                if (found.ValueKind == JsonValueKind.Object && Property(found, "message") is { } inner)
                    found = inner;
                if (found.ValueKind is JsonValueKind.String or JsonValueKind.Number && !IsEmpty(found))
                    return Shorten(Scalar(found));
            }

            return Shorten(Plain(body));
        }
        catch (JsonException)
        {
            var text = Plain(body);
            return text.Length == 0 ? null : text;
        }
    }

    private static string Plain(string body)
    {
        var withoutScripts = Regex.Replace(body, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return Shorten(Spaces().Replace(System.Net.WebUtility.HtmlDecode(Tags().Replace(withoutScripts, " ")), " ").Trim());
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
