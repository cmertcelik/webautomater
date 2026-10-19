using WebAutomater.Core.Data;
using WebAutomater.Core.Models;
using WebAutomater.Core.Playback;
using WebAutomater.Core.Text;

namespace WebAutomater.Tests;

public class ValueResolverTests
{
    private static readonly Dictionary<string, string> Row = new(StringComparer.OrdinalIgnoreCase)
    {
        ["isim"] = "Ali",
        ["telefon"] = "555",
    };

    [Fact]
    public void Bound_parameter_overrides_recorded_value()
    {
        var step = new Step { Type = StepType.Fill, Value = "kayıtlı", Parameter = "ISIM" };
        Assert.Equal("Ali", ValueResolver.Resolve(step, Row, 1, 1));
    }

    [Fact]
    public void Without_data_recorded_value_is_used()
    {
        var step = new Step { Type = StepType.Fill, Value = "kayıtlı", Parameter = "isim" };
        Assert.Equal("kayıtlı", ValueResolver.Resolve(step, null, 1, null));
    }

    [Fact]
    public void Templates_are_filled_from_row_and_counters()
    {
        var step = new Step { Type = StepType.Fill, Value = "{{isim}} - {{ telefon }} #{{tekrar}} {{bilinmeyen}}" };
        Assert.Equal("Ali - 555 #7 {{bilinmeyen}}", ValueResolver.Resolve(step, Row, 7, 2));
    }

    [Theory]
    [InlineData("evet", true)]
    [InlineData("X", true)]
    [InlineData("1", true)]
    [InlineData("hayır", false)]
    [InlineData("", false)]
    public void Truthy_values(string value, bool expected) => Assert.Equal(expected, ValueResolver.IsTruthy(value));

    [Fact]
    public void Validator_reports_missing_columns()
    {
        var scenario = new Scenario
        {
            Steps =
            [
                new Step { Type = StepType.Navigate, Value = "https://example.com" },
                new Step { Type = StepType.Fill, Selectors = ["#a"], Parameter = "soyad" },
            ],
        };
        var data = new DataTableSource("x.csv", ["isim"], []);

        var problems = ScenarioValidator.Validate(scenario, data);

        Assert.Contains(problems, p => p.Contains("'soyad'"));
    }

    [Fact]
    public void Describer_masks_passwords_and_shows_bindings()
    {
        var password = new Step { Type = StepType.Fill, Value = "gizli123", TargetName = "Şifre", TargetKind = "password", Selectors = ["#p"] };
        Assert.Equal("Yaz: Şifre (şifre alanı) → ••••••••", StepDescriber.Describe(password));

        var bound = new Step { Type = StepType.Fill, Value = "x", TargetName = "Ad", TargetKind = "textbox", Selectors = ["#a"], Parameter = "isim" };
        Assert.Equal("Yaz: Ad (metin alanı) → [isim]", StepDescriber.Describe(bound));
    }
}
