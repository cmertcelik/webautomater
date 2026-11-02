using WebAutomater.Core.Data;
using WebAutomater.Core.Models;

namespace WebAutomater.Core.Playback;

public static class ScenarioValidator
{
    private static readonly HashSet<string> BuiltInKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "tekrar", "iteration", "satir", "satır", "row",
    };

    /// <summary>Oynatmadan önce tespit edilebilen sorunları döndürür.</summary>
    public static List<string> Validate(Scenario scenario, DataTableSource? data)
    {
        var problems = new List<string>();
        var enabled = scenario.Steps.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0)
            problems.Add("Senaryoda etkin adım yok.");

        if (scenario.Graph && enabled.Count > 0 && new ScenarioFlow(scenario).Start is null)
            problems.Add("Başlangıç düğümü belirlenmemiş.");

        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];
            if (!step.Enabled)
                continue;
            var no = i + 1;

            if (data is not null && !string.IsNullOrWhiteSpace(step.Parameter) && !data.HasColumn(step.Parameter))
                problems.Add($"Adım {no}: '{step.Parameter}' kolonu veri kaynağında yok.");

            if (data is not null)
            {
                foreach (var key in ValueResolver.TemplateKeys(step.Value).Concat(step.Selectors.SelectMany(ValueResolver.TemplateKeys)))
                {
                    if (!BuiltInKeys.Contains(key) && !data.HasColumn(key))
                        problems.Add($"Adım {no}: {{{{{key}}}}} şablonundaki kolon veri kaynağında yok.");
                }
            }

            if (scenario.Graph)
            {
                foreach (var port in Enum.GetValues<StepPort>())
                {
                    if (ScenarioGraph.GetLink(step, port) is { } link && scenario.Steps.All(s => s.Id != link))
                        problems.Add($"Adım {no}: bağlantısı silinmiş bir düğümü gösteriyor.");
                }
            }

            if (step.Type == StepType.Condition)
            {
                var kind = step.Condition ?? ConditionKind.Visible;
                if (kind != ConditionKind.PageHasText && step.Selectors.Count == 0)
                    problems.Add($"Adım {no}: kontrol edilecek öğe yok. Kontrol modunda bir öğe seçin ya da türü \"Sayfada yazı var mı\" yapın.");
                if (kind is ConditionKind.TextEquals or ConditionKind.TextContains or ConditionKind.PageHasText
                    && string.IsNullOrWhiteSpace(step.Value) && string.IsNullOrWhiteSpace(step.Parameter))
                    problems.Add($"Adım {no}: kontrol edilecek metin boş.");
                continue;
            }

            if (step.Type == StepType.End)
                continue;

            var needsTarget = step.Type is not (StepType.Navigate or StepType.Wait or StepType.Press or StepType.WaitForText);
            var clickByText = step.Type is StepType.Click or StepType.DoubleClick or StepType.Hover
                              && !string.IsNullOrWhiteSpace(step.Parameter);
            if (needsTarget && step.Selectors.Count == 0 && !clickByText)
                problems.Add($"Adım {no}: hedef öğe seçicisi yok.");

            if (step.Type == StepType.WaitForText && string.IsNullOrWhiteSpace(step.Value) && string.IsNullOrWhiteSpace(step.Parameter))
                problems.Add($"Adım {no}: beklenecek yazı boş.");

            if (step.Type == StepType.Wait && !int.TryParse(step.Value, out _))
                problems.Add($"Adım {no}: bekleme süresi milisaniye cinsinden sayı olmalı.");
        }
        return problems;
    }
}
