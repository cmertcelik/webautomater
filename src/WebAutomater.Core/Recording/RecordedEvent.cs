namespace WebAutomater.Core.Recording;

/// <summary>recorder.js tarafından gönderilen ham olay.</summary>
internal sealed class RecordedEvent
{
    public string Type { get; set; } = "";
    public List<string> Selectors { get; set; } = [];
    public string? Value { get; set; }
    public string? Display { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public bool Multiple { get; set; }
    public long Ts { get; set; }
    public int? Vw { get; set; }
    public int? Vh { get; set; }
}
