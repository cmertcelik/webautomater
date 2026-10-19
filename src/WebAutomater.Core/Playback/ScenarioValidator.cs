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
