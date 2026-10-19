using System.Text.RegularExpressions;
using WebAutomater.Core.Models;

namespace WebAutomater.Core.Playback;

public static partial class ValueResolver
{
    private static readonly HashSet<string> TrueWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "1", "true", "yes", "y", "evet", "e", "x", "on", "checked", "✓", "✔",
    };

    /// <summary>
    /// Adıma bağlı kolon varsa satırdaki değeri, yoksa kayıtlı değeri döndürür.
    /// Ardından {{kolon}}, {{tekrar}} ve {{satir}} şablonlarını doldurur.
    /// </summary>
    public static string? Resolve(Step step, IReadOnlyDictionary<string, string>? row, int iteration, int? rowNumber)
    {
        var raw = step.Value;
        if (!string.IsNullOrWhiteSpace(step.Parameter) && row is not null && row.TryGetValue(step.Parameter, out var bound))
            raw = bound;
        return ApplyTemplate(raw, row, iteration, rowNumber);
    }

    public static string? ApplyTemplate(string? text, IReadOnlyDictionary<string, string>? row, int iteration, int? rowNumber)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{"))
            return text;
        return TemplatePattern().Replace(text, m =>
        {
            var key = m.Groups[1].Value.Trim();
            if (row is not null && row.TryGetValue(key, out var v))
                return v;
            return key.ToLowerInvariant() switch
            {
                "tekrar" or "iteration" => iteration.ToString(),
                "satir" or "satır" or "row" => (rowNumber ?? iteration).ToString(),
                _ => m.Value,
            };
        });
    }

    public static bool IsTruthy(string? value) => value is not null && TrueWords.Contains(value.Trim());

    public static IEnumerable<string> TemplateKeys(string? text) =>
        string.IsNullOrEmpty(text) ? [] : TemplatePattern().Matches(text).Select(m => m.Groups[1].Value.Trim());

    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}")]
    private static partial Regex TemplatePattern();
}
