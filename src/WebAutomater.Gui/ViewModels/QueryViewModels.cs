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
    private readonly SessionConfigViewModel? _session;
    private readonly CancellationTokenSource _cts = new();

    public QueryRunViewModel(string title, HttpRequestTemplate template, IEnumerable<(string Id, string Kind)> ids,
        string? resultField = null, string emptyText = "Kayıt yok", SessionConfigViewModel? session = null)
    {
        Title = title;
        _template = template;
        _session = session;
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
            var manager = _session?.Manager;
            if (manager is not null && manager.Current is null)
                Status = "Oturum açılıyor...";
            foreach (var row in Results)
            {
                token.ThrowIfCancellationRequested();
                Status = $"Sorgulanıyor... {done + 1}/{Results.Count}: {row.Id}";
                row.IsRunning = true;
                if (manager is null)
                {
                    row.Result = await QuerySender.SendAsync(client, _template, row.Id, token);
                }
                else
                {
                    // Oturum yoksa açılır; sorgu oturum hatası verirse token yenilenip bir kez daha denenir.
                    var (result, failure) = await manager.SendAsync(client, _template, row.Id, token);
                    row.Result = result;
                    if (failure is not null)
                    {
                        row.IsRunning = false;
                        done++;
                        Completed = done;
                        _session!.ReportFailure(failure.Message);
                        Status = $"Oturum açılamadı, sorgulama durdu ({done}/{Results.Count}): {failure.Message}";
                        return;
                    }
                }

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

/// <summary>
/// Oturum yenileme ayarı: platforma girişte giden token isteği ve oturum başlatma isteği.
/// Geçerliyse müşteri/istihbarat sorguları token'ı buradan alır; süresi dolunca kendiliğinden yenilenir.
/// </summary>
public sealed partial class SessionConfigViewModel : ObservableObject, IDisposable
{
    private SessionManager? _manager;

    public SessionConfigViewModel(string? tokenRequest, string? sessionRequest, string? tokenField, bool autoRefresh)
    {
        _tokenRequestText = tokenRequest ?? "";
        _sessionRequestText = sessionRequest ?? "";
        _tokenField = tokenField ?? "";
        _autoRefresh = autoRefresh;
        Rebuild();
    }

    /// <summary>Ayar değişti: kaydedilmeli.</summary>
    public event Action? Changed;

    [ObservableProperty] private string _tokenRequestText;
    [ObservableProperty] private string _sessionRequestText;
    [ObservableProperty] private string _tokenField;
    [ObservableProperty] private bool _autoRefresh;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTokenRequestInfo))] private string _tokenRequestInfo = "";
    [ObservableProperty] private bool _tokenRequestOk;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSessionRequestInfo))] private string _sessionRequestInfo = "";
    [ObservableProperty] private bool _sessionRequestOk;

    /// <summary>Panelde görünen kısa durum: "Oturum açık · 12 dk", "Oturum yenileme kapalı" vb.</summary>
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTestResult))] private string _testResult = "";
    [ObservableProperty] private bool _testResultIsError;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(TestCommand))] private bool _isTesting;

    public bool HasTokenRequestInfo => TokenRequestInfo.Length > 0;

    public bool HasSessionRequestInfo => SessionRequestInfo.Length > 0;

    public bool HasTestResult => TestResult.Length > 0;

    /// <summary>Sorgularda kullanılacak oturum; ayar eksik ya da kapalıysa null (kopyadaki token kullanılır).</summary>
    public SessionManager? Manager => AutoRefresh ? _manager : null;

    partial void OnTokenRequestTextChanged(string value) => OnEdited();
    partial void OnSessionRequestTextChanged(string value) => OnEdited();
    partial void OnTokenFieldChanged(string value) => OnEdited();

    partial void OnAutoRefreshChanged(bool value)
    {
        UpdateStatus();
        Changed?.Invoke();
    }

    private void OnEdited()
    {
        Rebuild();
        Changed?.Invoke();
    }

    private static (HttpRequestTemplate? Template, string Info, bool Ok) Parse(string text, bool optional)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, optional ? "" : "Token isteğini yapıştırın.", false);
        try
        {
            var t = HttpRequestTemplate.Parse(text);
            // Giriş isteklerinde {id} olmaz; o uyarı burada anlamsız.
            var warnings = t.Warnings.Where(w => !w.Contains("{id}")).ToList();
            return (t, $"✓ {t.Method} {t.Url}" + (warnings.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, warnings.Select(w => "• " + w)) : ""), true);
        }
        catch (RequestParseException ex)
        {
            return (null, ex.Message, false);
        }
    }

    private void Rebuild()
    {
        var (token, tokenInfo, tokenOk) = Parse(TokenRequestText, optional: false);
        var (session, sessionInfo, sessionOk) = Parse(SessionRequestText, optional: true);
        TokenRequestInfo = string.IsNullOrWhiteSpace(TokenRequestText) ? "" : tokenInfo;
        TokenRequestOk = tokenOk;
        SessionRequestInfo = sessionInfo;
        SessionRequestOk = sessionOk;

        if (_manager is not null)
            _manager.Changed -= OnManagerChanged;
        _manager?.Dispose();
        _manager = token is not null && (session is not null || string.IsNullOrWhiteSpace(SessionRequestText))
            ? new SessionManager(token, session, string.IsNullOrWhiteSpace(TokenField) ? null : TokenField)
            : null;
        if (_manager is not null)
            _manager.Changed += OnManagerChanged;
        OnPropertyChanged(nameof(Manager));
        UpdateStatus();
    }

    private void OnManagerChanged(LoginResult result) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStatus);

    private void UpdateStatus()
    {
        if (_manager is null)
        {
            Status = string.IsNullOrWhiteSpace(TokenRequestText)
                ? "Oturum yenileme ayarlanmadı: kopyalanan istekteki token kullanılır."
                : "Oturum ayarında hata var; ⚙ ile kontrol edin.";
            StatusIsError = !string.IsNullOrWhiteSpace(TokenRequestText);
            return;
        }

        if (!AutoRefresh)
        {
            Status = "Oturum yenileme kapalı: kopyalanan istekteki token kullanılır.";
            StatusIsError = false;
            return;
        }

        if (_manager.Current is { } current)
        {
            var left = current.ExpiresAt is { } e ? $" · {Math.Max(0, (e - DateTimeOffset.UtcNow).TotalMinutes):0} dk geçerli" : "";
            Status = $"Oturum açık ({DateTime.Now:HH:mm}){left}. Süresi dolunca kendiliğinden yenilenir.";
            StatusIsError = false;
        }
        else
        {
            Status = "Oturum yenileme hazır: ilk sorguda oturum açılır.";
            StatusIsError = false;
        }
    }

    /// <summary>Sorgu sırasında oturum açılamadığında panelde gösterilir.</summary>
    public void ReportFailure(string message)
    {
        Status = "Oturum açılamadı: " + message;
        StatusIsError = true;
    }

    private bool CanTest() => !IsTesting;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        if (_manager is null)
        {
            TestResult = TokenRequestOk ? SessionRequestInfo : TokenRequestInfo.Length > 0 ? TokenRequestInfo : "Önce token isteğini yapıştırın.";
            TestResultIsError = true;
            return;
        }

        IsTesting = true;
        TestResult = "Deneniyor...";
        TestResultIsError = false;
        try
        {
            var result = await _manager.ForceRefreshAsync();
            TestResult = result.Success
                ? $"✓ {result.Message} Token: {TokenFinder.Mask(result.Token!)}"
                : result.Message;
            TestResultIsError = !result.Success;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            TestResult = AppLog.Describe(ex);
            TestResultIsError = true;
            AppLog.Warning("Oturum", "Oturum denemesi hata verdi.", ex);
        }
        finally
        {
            IsTesting = false;
        }
    }

    public void Dispose() => _manager?.Dispose();
}
