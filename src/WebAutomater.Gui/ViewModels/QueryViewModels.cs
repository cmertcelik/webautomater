using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAutomater.Core.Diagnostics;
using WebAutomater.Core.Http;

namespace WebAutomater.Gui.ViewModels;

/// <summary>Bir sorgu butonunun ayarı: Chrome'dan kopyalanan istek ve ayrıştırma sonucu.</summary>
public sealed partial class QueryConfigViewModel : ObservableObject
{
    /// <param name="resultField">Yanıtta gösterilecek alan ("value"); boşsa yanıtın tamamı.</param>
    /// <param name="emptyText">Alan boş gelince yazılacak metin.</param>
    public QueryConfigViewModel(string title, string? text, string? resultField, string emptyText)
    {
        Title = title;
        EmptyText = emptyText;
        _requestText = text ?? "";
        _resultField = resultField ?? "";
        Reparse();
    }

    public string Title { get; }

    public string EmptyText { get; }

    /// <summary>Yanıtta gösterilecek alan; "value" ya da "data.items" gibi noktalı yol. Boşsa yanıtın tamamı görünür.</summary>
    [ObservableProperty]
    private string _resultField;

    partial void OnResultFieldChanged(string value) => Changed?.Invoke();

    public string ResultFieldHint =>
        $"Yalnızca bu alan gösterilir. Boş gelirse \"{EmptyText}\" yazar, doluysa içindeki değerler yazar. Alanı boş bırakırsanız yanıtın tamamı görünür.";

    /// <summary>İstek metni değişti: ayarlar kaydedilmeli.</summary>
    public event Action? Changed;

    [ObservableProperty]
    private string _requestText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(ButtonTip))]
    private HttpRequestTemplate? _template;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string _summary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private string _warnings = "";

    /// <summary>Sorgulanacak numara sayısı (seçili olanlar, seçim yoksa listedekilerin tümü).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private int _targetCount;

    public bool IsReady => Template is not null;

    public bool HasError => Error.Length > 0;

    public bool HasSummary => Summary.Length > 0;

    public bool HasWarnings => Warnings.Length > 0;

    public string ButtonText => TargetCount > 0 ? $"{Title} ({TargetCount})" : Title;

    public string ButtonTip => IsReady
        ? "Seçili numaraları (seçim yoksa listedeki tüm numaraları) sorgular."
        : "Önce yanındaki ⚙ ile Chrome'dan kopyaladığınız isteği yapıştırın.";

    public string SettingsTitle => $"{Title} — istek";

    partial void OnRequestTextChanged(string value)
    {
        Reparse();
        Changed?.Invoke();
    }

    private void Reparse()
    {
        if (string.IsNullOrWhiteSpace(RequestText))
        {
            Template = null;
            Error = "";
            Summary = "";
            Warnings = "";
            return;
        }

        try
        {
            Template = HttpRequestTemplate.Parse(RequestText);
            Error = "";
            Summary = "✓ " + Template.Describe();
            Warnings = string.Join(Environment.NewLine, Template.Warnings.Select(w => "• " + w));
        }
        catch (RequestParseException ex)
        {
            Template = null;
            Error = ex.Message;
            Summary = "";
            Warnings = "";
        }
    }

    [RelayCommand]
    private void Clear() => RequestText = "";
}

/// <summary>Bir numaranın sorgu sonucu satırı.</summary>
public sealed partial class QueryResultRow(string id, string kind, string? field, string emptyText) : ObservableObject
{
    public string Id { get; } = id;

    public string Kind { get; } = kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(IsPending), nameof(IsFound), nameof(IsEmpty), nameof(IsFailure),
        nameof(StatusLine), nameof(ResultText), nameof(RawBody), nameof(Url), nameof(Details), nameof(OutcomeText))]
    private QueryResult? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine), nameof(ResultText))]
    private bool _isRunning;

    public QuerySummary? Summary => Result is null ? null : QueryResultReader.Summarize(Result, field, emptyText);

    public bool IsPending => Result is null;

    public bool IsFound => Summary?.Outcome == QueryOutcome.Found;

    public bool IsEmpty => Summary?.Outcome == QueryOutcome.Empty;

    public bool IsFailure => Summary?.Outcome == QueryOutcome.Failed;

    public string OutcomeText => Summary?.Outcome switch
    {
        QueryOutcome.Found => "Kayıt var",
        QueryOutcome.Empty => "Kayıt yok",
        QueryOutcome.Failed => "Kontrol edin",
        _ => "",
    };

    /// <summary>Listede numaranın altındaki satır: sonuç ya da hata mesajı.</summary>
    public string StatusLine => Summary?.Line ?? (IsRunning ? "Sorgulanıyor..." : "Bekliyor");

    /// <summary>Sağ paneldeki sonuç: alanın değerleri, "kayıt yok" ya da hata mesajı.</summary>
    public string ResultText => Summary?.Text ?? (IsRunning ? "Sorgulanıyor..." : "Bekliyor");

    public string RawBody => Result switch
    {
        null => "",
        { Error: { } error } => error,
        { Body.Length: 0 } => "(yanıt boş)",
        { } r => r.Body,
    };

    public string Url => Result?.Url ?? "";

    public string Details => Result switch
    {
        null => "",
        { Error: not null } => $"{Result.Elapsed.TotalMilliseconds:0} ms",
        { } r => $"HTTP {r.StatusCode} {r.StatusText} · {r.Elapsed.TotalMilliseconds:0} ms",
    };
}

/// <summary>Sonuç penceresi: numaralar sırayla sorgulanır, pencere kapanınca kalanlar iptal edilir.</summary>
public sealed partial class QueryRunViewModel : ObservableObject
{
    private readonly HttpRequestTemplate _template;
    private readonly CancellationTokenSource _cts = new();

    public QueryRunViewModel(string title, HttpRequestTemplate template, IEnumerable<(string Id, string Kind)> ids,
        string? resultField = null, string emptyText = "Kayıt yok")
    {
        Title = title;
        _template = template;
        foreach (var (id, kind) in ids)
            Results.Add(new QueryResultRow(id, kind, resultField, emptyText));
        _selectedResult = Results.FirstOrDefault();
        _status = $"{Results.Count} numara sorgulanacak.";
    }

    public string Title { get; }

    public string WindowTitle => $"{Title} — {Results.Count} numara";

    public ObservableCollection<QueryResultRow> Results { get; } = [];

    [ObservableProperty]
    private QueryResultRow? _selectedResult;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Counts))]
    private int _completed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    /// <summary>Ham yanıt yerine yalnızca sonuç gösterilir; kontrol için ham yanıt açılabilir.</summary>
    [ObservableProperty]
    private bool _showRaw;

    [ObservableProperty]
    private bool _wrapLines = true;

    public string Counts
    {
        get
        {
            var found = Results.Count(r => r.IsFound);
            var empty = Results.Count(r => r.IsEmpty);
            var failed = Results.Count(r => r.IsFailure);
            return $"Kayıt var: {found}   ·   Kayıt yok: {empty}   ·   Kontrol edilecek: {failed}";
        }
    }

    public async Task RunAsync()
    {
        IsRunning = true;
        var token = _cts.Token;
        var done = 0;
        // Günlüğe numara, çerez ya da yanıt yazılmaz: yalnızca hangi adrese gidildiği ve hata.
        var endpoint = Uri.TryCreate(_template.ResolveUrl("0"), UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : "?";
        AppLog.Info("Sorgulama", $"{Title}: {Results.Count} numara sorgulanıyor ({_template.Method} {endpoint}).");
        try
        {
            using var client = QuerySender.CreateClient(_template.AllowInsecure);
            foreach (var row in Results)
            {
                token.ThrowIfCancellationRequested();
                Status = $"Sorgulanıyor... {done + 1}/{Results.Count}: {row.Id}";
                row.IsRunning = true;
                row.Result = await QuerySender.SendAsync(client, _template, row.Id, token);
                row.IsRunning = false;
                if (row.IsFailure)
                    AppLog.Warning("Sorgulama", $"{Title}: {done + 1}. numara kontrol edilmeli: {row.Summary?.Line}");
                done++;
                Completed = done;
            }

            var failed = Results.Count(r => r.IsFailure);
            Status = failed == 0
                ? $"{Results.Count} numara sorgulandı."
                : $"{Results.Count} numara sorgulandı; {failed} tanesi elle kontrol edilmeli. Oturum süresi dolduysa isteği Chrome'dan yeniden kopyalayın.";
        }
        catch (OperationCanceledException)
        {
            foreach (var row in Results)
                row.IsRunning = false;
            Status = $"Durduruldu: {done}/{Results.Count} numara sorgulandı.";
        }
        catch (Exception ex)
        {
            foreach (var row in Results)
                row.IsRunning = false;
            Status = $"Sorgulama yarıda kaldı ({done}/{Results.Count}): {AppLog.Describe(ex)}";
            AppErrors.Report("Sorgulama", $"{Title} yarıda kaldı.", ex);
        }
        finally
        {
            IsRunning = false;
            OnPropertyChanged(nameof(Counts));
        }
    }

    /// <summary>Excel'e yapıştırmak için: numara, tür, durum ve sonuç sekmeyle ayrılmış, her numara bir satır.</summary>
    public string ResultsAsTable() =>
        string.Join(Environment.NewLine, Results.Select(r =>
            string.Join('\t', r.Id, r.Kind, r.OutcomeText, (r.Summary?.Text ?? r.StatusLine).ReplaceLineEndings(" | "))));

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts.Cancel();

    public void Cancel() => _cts.Cancel();
}
