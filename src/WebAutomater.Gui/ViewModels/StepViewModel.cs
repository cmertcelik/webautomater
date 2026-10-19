using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAutomater.Core.Models;
using WebAutomater.Core.Text;

namespace WebAutomater.Gui.ViewModels;

public enum StepRunState { None, Running, Ok, Failed }

public sealed partial class StepViewModel : ObservableObject
{
    private static readonly IBrush NavigateBrush = Brush.Parse("#5C6BC0");
    private static readonly IBrush ClickBrush = Brush.Parse("#1E88E5");
    private static readonly IBrush InputBrush = Brush.Parse("#43A047");
    private static readonly IBrush ChoiceBrush = Brush.Parse("#8E24AA");
    private static readonly IBrush OtherBrush = Brush.Parse("#757575");

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

    public bool IsRunning => State == StepRunState.Running;
    public bool IsOk => State == StepRunState.Ok;
    public bool IsFailed => State == StepRunState.Failed;

    public string ActionText => StepDescriber.Action(Step.Type);

    public string TargetName => Step.Type switch
    {
        StepType.Navigate or StepType.Wait => "",
        StepType.Press when Step.Selectors.Count == 0 => "sayfa",
        _ => string.IsNullOrWhiteSpace(Step.TargetName) ? Step.Selectors.FirstOrDefault() ?? "" : Step.TargetName,
    };

    public string TargetKindText => Step.Type is StepType.Navigate or StepType.Wait || Step.Selectors.Count == 0
        ? ""
        : StepDescriber.Kind(Step.TargetKind);

    public IBrush ActionBrush => Step.Type switch
    {
        StepType.Navigate => NavigateBrush,
        StepType.Click or StepType.DoubleClick or StepType.Hover => ClickBrush,
        StepType.Fill or StepType.Press or StepType.Upload => InputBrush,
        StepType.Select or StepType.Check or StepType.Uncheck => ChoiceBrush,
        _ => OtherBrush,
    };

    public string Tooltip => string.Join("\n", new[]
    {
        StepDescriber.Describe(Step),
        Step.Selectors.Count > 0 ? "Seçiciler:\n  " + string.Join("\n  ", Step.Selectors) : null,
        Step.FrameUrl is not null ? $"Çerçeve: {Step.FrameUrl}" : null,
        Step.PageIndex > 0 ? $"{Step.PageIndex + 1}. sekme" : null,
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
        or StepType.Press or StepType.Upload or StepType.Wait;

    public bool IsValueEditable => HasValue && !IsBound;

    /// <summary>Şifre alanlarına yazılan değer ekranda gizlenir.</summary>
    public char PasswordChar => Step.TargetKind == "password" ? '•' : '\0';

    public string ValueWatermark => Step.Type switch
    {
        StepType.Wait => "milisaniye",
        StepType.Upload => "tam dosya yolu",
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

    public bool CanBind => StepDescriber.IsParameterizable(Step.Type);

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
    private void Duplicate() => _owner.DuplicateStep(this);
}
