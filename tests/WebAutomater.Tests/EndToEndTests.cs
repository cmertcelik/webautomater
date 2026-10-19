using WebAutomater.Core.Data;
using WebAutomater.Core.Models;
using WebAutomater.Core.Playback;
using WebAutomater.Core.Recording;
using WebAutomater.Core.Storage;

namespace WebAutomater.Tests;

/// <summary>
/// Gerçek tarayıcıyla kayıt ve oynatma. Kullanıcının yerine Playwright'ın girdi API'si kullanılır;
/// bunlar gerçek (isTrusted) tarayıcı olayları ürettiği için kaydedici onları kullanıcı işlemi gibi görür.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly TestServer _server = new();
    private readonly string _tempDir = Directory.CreateTempSubdirectory("wa-tests").FullName;

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<Scenario> RecordFormAsync()
    {
        await using var recorder = new Recorder { Headless = true };
        await recorder.StartAsync(_server.BaseUrl);
        var page = recorder.Pages[0];

        await page.ClickAsync("#fullname");
        await page.Keyboard.TypeAsync("Ali Veli");
        await page.SelectOptionAsync("select[name=city]", "ist");
        await page.CheckAsync("input[name=agree]");
        await Task.Delay(300);
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Gönder" }).ClickAsync();
        // WaitForURL eşleşmeden sonra "load" olayını da bekliyor; burada yalnızca adres değişimi yeterli.
        for (var i = 0; i < 50 && !page.Url.Contains("/submit"); i++)
            await Task.Delay(100);
        Assert.Contains("/submit", page.Url);
        await Task.Delay(300);
        await recorder.StopAsync();
        return recorder.Scenario;
    }

    [Fact]
    public async Task Records_user_actions_as_readable_steps()
    {
        var scenario = await RecordFormAsync();
        var types = scenario.Steps.Select(s => s.Type).ToList();

        Assert.Equal(
            [StepType.Navigate, StepType.Click, StepType.Fill, StepType.Select, StepType.Check, StepType.Click],
            types);
        var fill = scenario.Steps[2];
        Assert.Equal("Ali Veli", fill.Value);
        Assert.Equal("Ad Soyad", fill.TargetName);
        Assert.Equal("ist", scenario.Steps[3].Value);
        Assert.Equal("İstanbul", scenario.Steps[3].DisplayValue);
        Assert.Equal("Gönder", scenario.Steps[5].TargetName);
        Assert.True(scenario.Steps[4].DelayBeforeMs >= 0);
        Assert.True(scenario.Steps[5].DelayBeforeMs >= 250, $"Bekleme kaydedilmeli, {scenario.Steps[5].DelayBeforeMs} ms bulundu");
        Assert.All(scenario.Steps.Skip(1), s => Assert.NotEmpty(s.Selectors));
    }

    [Fact]
    public async Task Replays_recording_directly_multiple_times()
    {
        var scenario = await RecordFormAsync();
        while (_server.Submissions.TryDequeue(out _)) { }

        var result = await new Player().RunAsync(scenario, new PlaybackOptions
        {
            Headless = true,
            Iterations = 3,
            SpeedFactor = 0,
        });

        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(3, _server.Submissions.Count);
        Assert.All(_server.Submissions, q => Assert.Equal("fullname=Ali Veli&city=ist&agree=1", q));
    }

    [Fact]
    public async Task Replays_with_data_source_bound_to_steps()
    {
        var scenario = await RecordFormAsync();
        while (_server.Submissions.TryDequeue(out _)) { }

        scenario.Steps.Single(s => s.Type == StepType.Fill).Parameter = "isim";
        scenario.Steps.Single(s => s.Type == StepType.Select).Parameter = "Şehir";
        scenario.Steps.Single(s => s.Type == StepType.Check).Parameter = "onay";

        // Kaydet/yükle döngüsü: oynatma yalnızca dosyadaki veriye dayanmalı.
        var file = Path.Combine(_tempDir, "form" + ScenarioStore.FileExtension);
        await ScenarioStore.SaveAsync(scenario, file);
        var loaded = await ScenarioStore.LoadAsync(file);

        var csv = Path.Combine(_tempDir, "kisiler.csv");
        await File.WriteAllTextAsync(csv, "isim;şehir;onay\nAyşe Yılmaz;Ankara;evet\nMehmet Öz;ist;hayır\n");
        var data = DataSourceLoader.Load(csv);

        var result = await new Player().RunAsync(loaded, new PlaybackOptions
        {
            Headless = true,
            Data = data,
            SpeedFactor = 0,
        });

        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(
            ["fullname=Ayşe Yılmaz&city=ank&agree=1", "fullname=Mehmet Öz&city=ist"],
            _server.Submissions.ToArray());
    }

    [Fact]
    public async Task Reports_failure_with_readable_message_when_element_missing()
    {
        var scenario = new Scenario
        {
            Steps =
            [
                new Step { Type = StepType.Navigate, Value = _server.BaseUrl },
                new Step { Type = StepType.Click, Selectors = ["#yok"], TargetName = "Olmayan", TargetKind = "button" },
            ],
        };
        var result = await new Player().RunAsync(scenario, new PlaybackOptions
        {
            Headless = true,
            ActionTimeoutMs = 1000,
            ScreenshotDirectory = _tempDir,
        });

        var iteration = Assert.Single(result.Iterations);
        Assert.False(iteration.Success);
        Assert.Equal(1, iteration.FailedStepIndex);
        Assert.Contains("Olmayan (buton)", iteration.Error);
        Assert.True(File.Exists(iteration.ScreenshotPath));
    }
}
