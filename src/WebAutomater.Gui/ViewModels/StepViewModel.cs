using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAutomater.Core.Models;
using WebAutomater.Core.Text;

namespace WebAutomater.Gui.ViewModels;

public enum StepRunState { None, Running, Ok, Failed, No }

public sealed record ConditionChoice(string Label, ConditionKind Kind);

public sealed partial class StepViewModel : ObservableObject
{
    private static readonly IBrush NavigateBrush = Brush.Parse("#5C6BC0");
    private static readonly IBrush ClickBrush = Brush.Parse("#1E88E5");
    private static readonly IBrush InputBrush = Brush.Parse("#43A047");
    private static readonly IBrush ChoiceBrush = Brush.Parse("#8E24AA");
    private static readonly IBrush OtherBrush = Brush.Parse("#757575");
    private static readonly IBrush ConditionBrush = Brush.Parse("#EF8E00");
    private static readonly IBrush EndBrush = Brush.Parse("#37474F");

    public static ConditionChoice[] ConditionChoices { get; } =
        Enum.GetValues<ConditionKind>().Select(k => new ConditionChoice(StepDescriber.ConditionText(k), k)).ToArray();

    private readonly MainWindowViewModel _owner;

    public StepViewModel(Step step, MainWindowViewModel owner)
    {
        Step = step;
        _owner = owner;
    }

    public Step Step { get; }

    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsOk), nameof(IsFailed))]
    private StepRunState _state;

    [ObservableProperty]
    private string? _error;

    partial void OnStateChanged(StepRunState value) => _owner.NotifyFlowChanged();

    public bool IsCondition => Step.Type == StepType.Condition;

    public bool IsEnd => Step.Type == StepType.End;

    public ConditionChoice? SelectedCondition
    {
        get => ConditionChoices.FirstOrDefault(c => c.Kind == (Step.Condition ?? ConditionKind.Visible));
        set
        {
            if (value is null || value.Kind == Step.Condition)
                return;
            Step.Condition = value.Kind;
            OnPropertyChanged(string.Empty);
            _owner.MarkChanged();
        }
    }

    public bool EndAsFailure
    {
        get => Step.EndAsFailure;
        set
        {
            if (SetProperty(Step.EndAsFailure, value, Step, (s, v) => s.EndAsFailure = v))
                _owner.MarkChanged();
        }
    }

    /// <summary>Adıma özel en uzun bekleme; boş bırakılırsa genel ayar (kontrolde 3 sn) geçerlidir.</summary>
    public decimal? TimeoutSeconds
    {
        get => Step.TimeoutMs / 1000m;
        set
        {
            int? ms = value is { } v && v > 0 ? (int)Math.Round(v * 1000) : null;
            if (SetProperty(Step.TimeoutMs, ms, Step, (s, x) => s.TimeoutMs = x))
                _owner.MarkChanged();
        }
    }

    public bool HasTimeout => !IsEnd && Step.Type is not (StepType.Wait or StepType.Navigate);

    /// <summary>Düğüm kartındaki alt satır: ne ile çalıştığı. Şifreler maskeli.</summary>
    public string FlowDetail
    {
        get
        {
            if (IsEnd)
                return Step.EndAsFailure ? "hatalı say" : "";
            if (IsCondition)
            {
                var text = StepDescriber.ConditionText(Step.Condition ?? ConditionKind.Visible);
                var value = StepDescriber.Value(Step);
                return value.Length > 0 ? $"{text}: {value}" : text + "?";
            }

            return StepDescriber.Value(Step);
        }
    }

    public bool CanRecordNext => CanRecordFrom(StepPort.Next);
    public bool CanRecordFalse => IsCondition && CanRecordFrom(StepPort.False);
    public bool CanRecordError => ScenarioGraph.PortsOf(Step).Contains(StepPort.Error) && CanRecordFrom(StepPort.Error);
    public string RecordNextText => IsCondition ? "Evet çıkışından kaydet" : "Sonrasından kaydet";

    private bool CanRecordFrom(StepPort port) => !IsEnd && ScenarioGraph.GetLink(Step, port) is null;

    public bool IsNo => State == StepRunState.No;

    public bool IsRunning => State == StepRunState.Running;
    public bool IsOk => State == StepRunState.Ok;
    public bool IsFailed => State == StepRunState.Failed;

    public string ActionText => StepDescriber.Action(Step.Type);

    public string TargetName => Step.Type switch
    {
        StepType.Navigate or StepType.Wait or StepType.WaitForText or StepType.End => "",
        StepType.Condition when Step.Condition == ConditionKind.PageHasText => "Sayfa",
        StepType.Press when Step.Selectors.Count == 0 => "sayfa",
        _ => string.IsNullOrWhiteSpace(Step.TargetName) ? Step.Selectors.FirstOrDefault() ?? "" : Step.TargetName,
    };

    public string TargetKindText
    {
        get
        {
            var parts = new List<string>();
            if (Step.Type is not (StepType.Navigate or StepType.Wait or StepType.WaitForText or StepType.End) && Step.Selectors.Count > 0)
                parts.Add(StepDescriber.Kind(Step.TargetKind));
            if (Step.WaitForNavigation)
                parts.Add("ardından yeni sayfa beklenir");
            if (Step.TimeoutMs is { } t)
                parts.Add($"en fazla {t / 1000.0:0.#} sn");
            return string.Join(" · ", parts);
        }
    }

    public IBrush ActionBrush => Step.Type switch
    {
        StepType.Navigate => NavigateBrush,
        StepType.Click or StepType.DoubleClick or StepType.Hover => ClickBrush,
        StepType.Fill or StepType.Press or StepType.Upload => InputBrush,
        StepType.Select or StepType.Check or StepType.Uncheck => ChoiceBrush,
        StepType.Condition => ConditionBrush,
        StepType.End => EndBrush,
        _ => OtherBrush,
    };

    public string Tooltip => string.Join("\n", new[]
    {
        StepDescriber.Describe(Step),
        Step.Selectors.Count > 0 ? "Seçiciler:\n  " + string.Join("\n  ", Step.Selectors) : null,
        Step.FrameUrl is not null ? $"Çerçeve: {Step.FrameUrl}" : null,
        Step.PageIndex > 0 ? $"{Step.PageIndex + 1}. sekme" : null,
        Step.WaitForNavigation ? "Bu adımdan sonra yeni sayfanın yüklenmesi beklenir." : null,
        Step.TimeoutMs is { } t ? $"En fazla {t / 1000.0:0.#} sn beklenir." : null,
    }.Where(s => s is not null));

    public bool Enabled
    {
        get => Step.Enabled;
        set
        {
            if (SetProperty(Step.Enabled, value, Step, (s, v) => s.Enabled = v))
                _owner.MarkChanged();
        }
    }

    public bool HasValue => Step.Type is StepType.Navigate or StepType.Fill or StepType.Select
        or StepType.Press or StepType.Upload or StepType.Wait or StepType.WaitForText
        || (IsCondition && StepDescriber.ConditionUsesText(Step.Condition));

    public bool IsValueEditable => HasValue && !IsBound;

    /// <summary>Şifre alanlarına yazılan değer ekranda gizlenir.</summary>
    public char PasswordChar => Step.TargetKind == "password" ? '•' : '\0';

    public string ValueWatermark => Step.Type switch
    {
        StepType.Wait => "milisaniye",
        StepType.Upload => "tam dosya yolu",
        StepType.WaitForText => "beklenecek yazı",
        StepType.Condition => "aranan metin",
        _ => "",
    };

    public string? EditableValue
    {
        get => Step.DisplayValue ?? Step.Value;
        set
        {
            if (value == EditableValue)
                return;
            Step.Value = value;
            Step.DisplayValue = null;
            OnPropertyChanged();
            _owner.MarkChanged();
        }
    }

    public decimal? DelaySeconds
    {
        get => Step.DelayBeforeMs / 1000m;
        set
        {
            var ms = (int)Math.Round((value ?? 0) * 1000);
            if (SetProperty(Step.DelayBeforeMs, Math.Max(0, ms), Step, (s, v) => s.DelayBeforeMs = v))
                _owner.MarkChanged();
        }
    }

    public bool CanBind => StepDescriber.IsParameterizable(Step.Type) && (!IsCondition || HasValue);

    public bool IsBound => !string.IsNullOrWhiteSpace(Step.Parameter);

    public IReadOnlyList<string> ColumnChoices => _owner.ColumnChoices;

    public string BindingHint => StepDescriber.ParameterHint(Step.Type);

    public string? SelectedColumn
    {
        get => Step.Parameter ?? MainWindowViewModel.NoColumn;
        set
        {
            // Liste yenilenirken ComboBox geçici olarak boş seçim bildirir; bağlantı silinmemeli.
            if (value is null)
                return;
            var parameter = value == MainWindowViewModel.NoColumn ? null : value;
            if (parameter == Step.Parameter)
                return;
            Step.Parameter = parameter;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBound));
            OnPropertyChanged(nameof(IsValueEditable));
            OnPropertyChanged(nameof(SampleText));
            _owner.MarkChanged();
        }
    }

    /// <summary>Bağlı kolonun ilk satırdaki değeri: kullanıcı eşleştirmenin doğru olduğunu görsün.</summary>
    public string? SampleText => IsBound ? _owner.SampleFor(Step.Parameter!) : null;

    /// <summary>Kayıt sırasında adım güncellendiğinde ya da veri kaynağı değiştiğinde ekranı tazeler.</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void MoveUp() => _owner.MoveStep(this, -1);

    [RelayCommand]
    private void MoveDown() => _owner.MoveStep(this, +1);

    [RelayCommand]
    private void Delete() => _owner.DeleteStep(this);

    [RelayCommand]
    private void RecordFromNext() => _owner.StartBranchRecording(this, StepPort.Next);

    [RelayCommand]
    private void RecordFromFalse() => _owner.StartBranchRecording(this, StepPort.False);

    [RelayCommand]
    private void RecordFromError() => _owner.StartBranchRecording(this, StepPort.Error);

    [RelayCommand]
    private void MakeStart() => _owner.SetStart(this);

    [RelayCommand]
    private void MakeLoopStart() => _owner.SetLoopStart(this);

    [RelayCommand]
    private void Duplicate() => _owner.DuplicateStep(this);
}
