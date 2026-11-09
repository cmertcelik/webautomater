using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebAutomater.Core.Diagnostics;
using WebAutomater.Core.Http;

namespace WebAutomater.Gui.ViewModels;

/// <summary>Bir sorgu butonunun ayarı: Chrome'dan kopyalanan istek ve ayrıştırma sonucu.</summary>
public sealed partial class QueryConfigViewModel : ObservableObject
{
    public QueryConfigViewModel(string title, string? text)
    {
        Title = title;
        _requestText = text ?? "";
        Reparse();
    }

    public string Title { get; }

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
public sealed partial class QueryResultRow(string id, string kind) : ObservableObject
{
    public string Id { get; } = id;

    public string Kind { get; } = kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsSuccess), nameof(IsFailure), nameof(StatusLine), nameof(Body), nameof(Url), nameof(Details))]
    private QueryResult? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    private bool _isRunning;

    public bool IsPending => Result is null;

    public bool IsSuccess => Result?.IsSuccess == true;

    public bool IsFailure => Result is { IsSuccess: false };

    public string StatusLine => Result switch
    {
        null => IsRunning ? "Sorgulanıyor..." : "Bekliyor",
        { Error: not null } => "Hata",
        { } r => $"{r.StatusCode} {r.StatusText} · {r.Elapsed.TotalMilliseconds:0} ms",
    };

    public string Url => Result?.Url ?? "";

    public string Details => Result switch
    {
        null => "",
        { Error: { } error } => error,
        { } r => $"{r.StatusCode} {r.StatusText} · {r.Elapsed.TotalMilliseconds:0} ms" +
                 (r.ContentType is null ? "" : $" · {r.ContentType}") + $" · {r.Body.Length:N0} karakter",
    };

    public string Body => Result switch
    {
        null => "",
        { Error: { } error } => error,
        { Body.Length: 0 } => "(yanıt boş)",
        { } r => r.Body,
    };
}

/// <summary>Sonuç penceresi: numaralar sırayla sorgulanır, pencere kapanınca kalanlar iptal edilir.</summary>
public sealed partial class QueryRunViewModel : ObservableObject
{
    private readonly HttpRequestTemplate _template;
    private readonly CancellationTokenSource _cts = new();

    public QueryRunViewModel(string title, HttpRequestTemplate template, IEnumerable<(string Id, string Kind)> ids)
    {
        Title = title;
        _template = template;
        foreach (var (id, kind) in ids)
            Results.Add(new QueryResultRow(id, kind));
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
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _wrapLines = true;

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
                if (row.Result is { IsSuccess: false } failed)
                    AppLog.Warning("Sorgulama", $"{Title}: {done + 1}. numara başarısız: {failed.Error ?? $"HTTP {failed.StatusCode} {failed.StatusText}"}");
                done++;
            }

            var ok = Results.Count(r => r.IsSuccess);
            Status = ok == Results.Count
                ? $"{Results.Count} numara sorgulandı."
                : $"{Results.Count} numaradan {ok} tanesi başarılı, {Results.Count - ok} tanesi hatalı. Oturum süresi dolduysa isteği Chrome'dan yeniden kopyalayın.";
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
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts.Cancel();

    public void Cancel() => _cts.Cancel();
}
