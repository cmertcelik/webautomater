using System.Diagnostics;
using Microsoft.Playwright;
using WebAutomater.Core.Models;

namespace WebAutomater.Core.Playback;

/// <summary>
/// Bir işlemin yol açtığı sayfa geçişini izler ve yeni sayfa yüklenene kadar bekletir.
/// İşlemden önce oluşturulur ki çok hızlı gerçekleşen geçişler de kaçırılmasın.
/// </summary>
internal sealed class NavigationWatcher : IDisposable
{
    // İşaretsiz adımlarda (eski kayıtlar, elle eklenen adımlar) geçişin başlaması için tanınan süre.
    private const int UnflaggedGraceMs = 150;

    private readonly IPage _page;
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _started;

    public NavigationWatcher(IPage page)
    {
        _page = page;
        _page.Request += OnRequest;
        _page.DOMContentLoaded += OnDomContentLoaded;
    }

    private void OnRequest(object? sender, IRequest request)
    {
        if (request.IsNavigationRequest && request.Frame == _page.MainFrame)
            _started = true;
    }

    private void OnDomContentLoaded(object? sender, IPage page)
    {
        if (_started)
            _loaded.TrySetResult();
    }

    public async Task WaitAsync(Step step, PlaybackOptions options, Action<string> info, CancellationToken ct)
    {
        if (!step.WaitForNavigation)
        {
            var watch = Stopwatch.StartNew();
            while (!_started && watch.ElapsedMilliseconds < UnflaggedGraceMs)
                await Task.Delay(25, ct);
            if (!_started)
                return;
        }

        var finished = await Task.WhenAny(_loaded.Task, Task.Delay(options.NavigationTimeoutMs, ct));
        ct.ThrowIfCancellationRequested();
        if (finished != _loaded.Task && step.WaitForNavigation)
        {
            // Geçiş olmadıysa hata verilmez; sonraki adım kendi öğesini bekleyip gerekirse hata verir.
            info(_started
                ? $"Yeni sayfa {options.NavigationTimeoutMs / 1000} sn içinde yüklenmedi, devam ediliyor."
                : "Kayıttaki gibi yeni sayfa açılmadı, devam ediliyor.");
        }
    }

    public void Dispose()
    {
        _page.Request -= OnRequest;
        _page.DOMContentLoaded -= OnDomContentLoaded;
    }
}
