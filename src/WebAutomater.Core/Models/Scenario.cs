namespace WebAutomater.Core.Models;

public sealed class Scenario
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string Name { get; set; } = "Yeni senaryo";

    public string? StartUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public BrowserSettings Browser { get; set; } = new();

    public List<Step> Steps { get; set; } = [];

    /// <summary>Son kullanılan veri kaynağı; arayüzde kolaylık için hatırlanır.</summary>
    public string? DataSourcePath { get; set; }

    public string? DataSourceSheet { get; set; }

    public IEnumerable<string> BoundParameters =>
        Steps.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Parameter))
             .Select(s => s.Parameter!)
             .Distinct(StringComparer.OrdinalIgnoreCase);
}

public sealed class BrowserSettings
{
    /// <summary>chromium, firefox veya webkit.</summary>
    public string Engine { get; set; } = "chromium";

    /// <summary>Yüklü tarayıcıyı kullanmak için: chrome, msedge. Boşsa Playwright'ın tarayıcısı.</summary>
    public string? Channel { get; set; }

    public int? ViewportWidth { get; set; }

    public int? ViewportHeight { get; set; }
}
