using WebAutomater.Core.Models;
using WebAutomater.Core.Playback;
using WebAutomater.Core.Recording;
using WebAutomater.Core.Text;

namespace WebAutomater.Tests;

/// <summary>Yükleme süreleri değişken olan sitelerde oynatmanın doğru beklediğini doğrular.</summary>
public sealed class SlowSiteTests : IDisposable
{
    private readonly TestServer _server = new();

    public void Dispose() => _server.Dispose();

    private static PlaybackOptions Fast(int timeoutMs = 15_000) => new()
    {
        Headless = true,
        SpeedFactor = 0, // Kayıttaki beklemeler kapalı: yalnızca koşullu beklemeler sınanır.
        ActionTimeoutMs = timeoutMs,
    };

    private static Step Click(string name) => new()
    {
        Type = StepType.Click,
        Selectors = [$"internal:role=button[name=\"{name}\"i]"],
        TargetName = name,
        TargetKind = "button",
    };

    private void ClearSubmissions()
    {
        while (_server.Submissions.TryDequeue(out _)) { }
    }

    [Fact]
    public async Task Recorder_marks_clicks_that_load_a_new_page_even_when_server_is_slow()
    {
        _server.Wizard2DelayMs = 2500; // Bağlama süresinden (2 sn) uzun: başlangıç anı esas alınmalı.
        await using var recorder = new Recorder { Headless = true };
        await recorder.StartAsync(_server.BaseUrl + "wizardjs");
        var page = recorder.Pages[0];

        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "İleri" }).ClickAsync();
        await page.GetByRole(Microsoft.Playwright.AriaRole.Heading, new() { Name = "Adım 2" }).WaitForAsync();
        await Task.Delay(300);
        await recorder.StopAsync();

        var steps = recorder.Scenario.Steps;
        Assert.Equal([StepType.Navigate, StepType.Click], steps.Select(s => s.Type));
        Assert.True(steps[1].WaitForNavigation);
    }

    [Fact]
    public async Task Waits_for_delayed_new_page_before_next_step_when_same_button_exists_on_both_pages()
    {
        var first = Click("İleri");
        first.WaitForNavigation = true;
        var scenario = new Scenario
        {
            Steps = [new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "wizardjs" }, first, Click("İleri")],
        };
        ClearSubmissions();

        var result = await new Player().RunAsync(scenario, Fast());

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(["from=wizard2"], _server.Submissions.ToArray());
    }

    [Fact]
    public async Task Detects_navigation_for_unflagged_click_too()
    {
        // Eski kayıtlarda işaret yoktur; tıklamanın hemen ardından başlayan geçiş yine beklenmeli.
        var scenario = new Scenario
        {
            Steps = [new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "wizard" }, Click("İleri"), Click("İleri")],
        };
        ClearSubmissions();

        var result = await new Player().RunAsync(scenario, Fast());

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(["from=wizard2"], _server.Submissions.ToArray());
    }

    [Fact]
    public async Task Waits_for_element_that_appears_later()
    {
        var scenario = new Scenario
        {
            Steps = [new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "late" }, Click("Gönder")],
        };
        ClearSubmissions();

        var result = await new Player().RunAsync(scenario, Fast());

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(["late=1"], _server.Submissions.ToArray());
    }

    [Fact]
    public async Task Wait_for_text_step_uses_its_own_longer_timeout()
    {
        var scenario = new Scenario
        {
            Steps =
            [
                new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "late" },
                new Step { Type = StepType.WaitForText, Value = "Hazır", TimeoutMs = 10_000 },
                Click("Gönder"),
            ],
        };
        ClearSubmissions();

        // Genel zaman aşımı 1 sn: tek başına yetmez, bekleme adımı 10 sn tanır.
        var result = await new Player().RunAsync(scenario, Fast(timeoutMs: 1000));

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(["late=1"], _server.Submissions.ToArray());
    }

    [Fact]
    public async Task Wait_for_text_reports_readable_error_on_timeout()
    {
        var scenario = new Scenario
        {
            Steps =
            [
                new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "late" },
                new Step { Type = StepType.WaitForText, Value = "Asla gelmeyecek", TimeoutMs = 1000 },
            ],
        };

        var result = await new Player().RunAsync(scenario, Fast());

        var iteration = Assert.Single(result.Iterations);
        Assert.False(iteration.Success);
        Assert.Contains("\"Asla gelmeyecek\" yazısı 1 sn içinde sayfada görünmedi", iteration.Error);
    }

    [Fact]
    public async Task Wait_for_element_step_waits_for_target_of_another_step()
    {
        var click = Click("Gönder");
        var scenario = new Scenario
        {
            Steps =
            [
                new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "late" },
                StepDescriber.WaitForElementOf(click, 10_000),
                click,
            ],
        };
        ClearSubmissions();

        var result = await new Player().RunAsync(scenario, Fast(timeoutMs: 1000));

        Assert.Equal(1, result.SuccessCount);
    }

    [Fact]
    public async Task Retries_failed_iteration_from_scratch()
    {
        var scenario = new Scenario
        {
            Steps = [new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "flaky" }, Click("Gönder")],
        };
        ClearSubmissions();
        var infos = new List<string>();
        var options = Fast(timeoutMs: 1500);
        options.RetryCount = 2;

        var result = await new Player().RunAsync(scenario, options, e =>
        {
            if (e.Kind == PlaybackEventKind.Info && e.Message is not null)
                lock (infos) infos.Add(e.Message);
        });

        var iteration = Assert.Single(result.Iterations);
        Assert.True(iteration.Success);
        Assert.Equal(2, iteration.Attempts);
        Assert.Equal(["flaky=1"], _server.Submissions.ToArray());
        Assert.Contains(infos, m => m.Contains("yeniden deneniyor (1/2)"));
    }
}
