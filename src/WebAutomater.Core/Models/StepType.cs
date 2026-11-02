namespace WebAutomater.Core.Models;

public enum StepType
{
    Navigate,
    Click,
    DoubleClick,
    Fill,
    Press,
    Select,
    Check,
    Uncheck,
    Upload,
    Hover,
    Wait,
    WaitForText,
    WaitForElement,

    /// <summary>Bir öğenin ya da sayfanın durumunu sınar; sonuca göre Evet/Hayır çıkışından devam eder.</summary>
    Condition,

    /// <summary>Tekrarı burada bitirir.</summary>
    End,
}

/// <summary>Kontrol düğümünün neyi sınadığı.</summary>
public enum ConditionKind
{
    Visible,
    Enabled,
    Disabled,
    Checked,
    Unchecked,
    TextEquals,
    TextContains,
    PageHasText,
}
