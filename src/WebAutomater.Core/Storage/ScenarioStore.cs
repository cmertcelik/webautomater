using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WebAutomater.Core.Models;

namespace WebAutomater.Core.Storage;

public static class ScenarioStore
{
    public const string FileExtension = ".wa.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Arayüzün senaryoları listelediği varsayılan klasör.</summary>
    public static string DefaultDirectory
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs))
                docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(docs, "WebAutomater", "Scenarios");
        }
    }

    public static async Task SaveAsync(Scenario scenario, string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Yarım yazılmış dosya kalmaması için önce geçici dosyaya yazılır.
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
            await JsonSerializer.SerializeAsync(stream, scenario, JsonOptions, ct);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Senkron kaydetme; uygulama kapanırken beklenemeyen durumlar için.</summary>
    public static void Save(Scenario scenario, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(scenario, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Var olan bir dosyanın üzerine yazmamak için "ad (2).wa.json" gibi boş bir yol bulur.</summary>
    public static string UniquePath(string directory, string scenarioName)
    {
        var baseName = SuggestFileName(scenarioName)[..^FileExtension.Length];
        var path = Path.Combine(directory, baseName + FileExtension);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"{baseName} ({n}){FileExtension}");
        return path;
    }

    public static async Task<Scenario> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var scenario = await JsonSerializer.DeserializeAsync<Scenario>(stream, JsonOptions, ct)
            ?? throw new InvalidDataException($"Senaryo dosyası okunamadı: {path}");
        if (scenario.FormatVersion > Scenario.CurrentFormatVersion)
            throw new InvalidDataException(
                $"Bu senaryo daha yeni bir sürümle kaydedilmiş (biçim {scenario.FormatVersion}). Uygulamayı güncelleyin.");
        return scenario;
    }

    public static string SuggestFileName(string scenarioName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(scenarioName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return (clean.Length == 0 ? "senaryo" : clean) + FileExtension;
    }
}
