using WebAutomater.Core.Data;
using WebAutomater.Core.Models;
using WebAutomater.Core.Playback;
using WebAutomater.Core.Recording;

namespace WebAutomater.Tests;

public sealed class UploadFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-upl şğü").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void Splits_and_trims_quoted_paths()
    {
        Assert.Equal(["C:\\a b\\x.pdf", "y.pdf", "z.pdf"], UploadFiles.Split("\"C:\\a b\\x.pdf\" | y.pdf;\n 'z.pdf' "));
        Assert.Empty(UploadFiles.Split("  "));
    }

    [Fact]
    public void Resolves_absolute_relative_and_wildcard_paths()
    {
        var a = Touch("ahmet_cv.pdf");
        var b1 = Touch("10000000146_cv.pdf");
        var b2 = Touch("10000000146_ek.pdf");

        var abs = UploadFiles.Resolve(a, null);
        Assert.Equal([a], abs.Files);
        Assert.Empty(abs.Missing);

        Assert.Equal([a], UploadFiles.Resolve("ahmet_cv.pdf", _dir).Files);
        Assert.Equal([b1, b2], UploadFiles.Resolve("10000000146*.pdf", _dir).Files);
    }

    [Fact]
    public void Reports_missing_files_and_relative_paths_without_folder()
    {
        var missing = UploadFiles.Resolve("yok.pdf", _dir);
        Assert.Empty(missing.Files);
        Assert.Equal(Path.Combine(_dir, "yok.pdf"), Assert.Single(missing.Missing));
        Assert.Contains("göreli", Assert.Single(UploadFiles.Resolve("cv.pdf", null).Missing));
        Assert.Contains("desene", Assert.Single(UploadFiles.Resolve("*.docx", _dir).Missing));
        Assert.True(UploadFiles.Resolve("", _dir).IsEmpty);
    }

    [Fact]
    public void Base_directory_defaults_to_data_source_folder()
    {
        var data = new DataTableSource(Path.Combine(_dir, "liste.csv"), ["CV"], []);
        Assert.Equal(_dir, UploadFiles.BaseDirectory(new PlaybackOptions { Data = data }));
        Assert.Equal("/x", UploadFiles.BaseDirectory(new PlaybackOptions { Data = data, FilesDirectory = "/x" }));
    }

    [Fact]
    public void Validator_flags_recorded_file_name_but_not_data_bound_steps()
    {
        var nameOnly = new Step { Type = StepType.Upload, Selectors = ["#cv"], Value = "cv.pdf" };
        var bound = new Step { Type = StepType.Upload, Selectors = ["#cv"], Parameter = "CV" };
        var template = new Step { Type = StepType.Upload, Selectors = [], Value = _dir + "/{{TC}}.pdf" };
        var data = new DataTableSource(Path.Combine(_dir, "liste.csv"), ["CV", "TC"], []);

        var problems = ScenarioValidator.Validate(new Scenario { Steps = [nameOnly, bound, template] }, data);
        Assert.Contains(problems, p => p.StartsWith("Adım 1:") && p.Contains("bulunamadı"));
        Assert.DoesNotContain(problems, p => p.StartsWith("Adım 2:") || p.StartsWith("Adım 3:"));
    }
}

/// <summary>Gerçek tarayıcıyla dosya yükleme: kayıt ve veri kaynağından her satırda farklı dosyayla oynatma.</summary>
public sealed class UploadEndToEndTests : IDisposable
{
    private readonly TestServer _server = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("wa-upl-e2e").FullName;

    public void Dispose()
    {
        _server.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static PlaybackOptions Fast(DataTableSource? data = null) =>
        new() { Headless = true, SpeedFactor = 0, ActionTimeoutMs = 5000, Data = data };

    [Fact]
    public async Task Recording_captures_full_paths_for_visible_hidden_and_detached_inputs()
    {
        var cv = Write("cv.txt", "merhaba");
        var a = Write("a.txt", "1");
        var b = Write("b.txt", "22");
        var photo = Write("foto.txt", "333");
        var requests = new List<FileChooserRequest>();
        var answers = new Queue<IReadOnlyList<string>>([[cv], [a, b], [photo]]);

        await using var recorder = new Recorder
        {
            Headless = true,
            ChooseFiles = request =>
            {
                lock (requests)
                    requests.Add(request);
                return Task.FromResult<IReadOnlyList<string>?>(answers.Dequeue());
            },
        };
        await recorder.StartAsync(_server.BaseUrl + "upload");
        var page = recorder.Pages[0];

        await page.ClickAsync("#cv");
        await WaitForTextAsync(page, "cv: cv.txt (7)");
        await page.ClickAsync("#pick");
        await WaitForTextAsync(page, "gizli: a.txt (1), b.txt (2)");
        await page.ClickAsync("#dyn");
        await WaitForTextAsync(page, "anlik: foto.txt (3)");
        // Anlık girdinin adımı sayfadan olay gelmediği anlaşılınca eklenir.
        await Task.Delay(2000);
        await recorder.StopAsync();

        Assert.Equal("CV", requests[0].TargetName);
        Assert.Equal(".pdf,.txt", requests[0].Accept);
        Assert.False(requests[0].Multiple);
        Assert.True(requests[1].Multiple);

        var uploads = recorder.Scenario.Steps.Where(s => s.Type == StepType.Upload).ToList();
        Assert.Equal(3, uploads.Count);
        Assert.Equal(cv, uploads[0].Value);
        Assert.NotEmpty(uploads[0].Selectors);
        Assert.Equal($"{a}|{b}", uploads[1].Value);
        Assert.True(uploads[1].Multiple);
        Assert.NotEmpty(uploads[1].Selectors);
        Assert.Equal(photo, uploads[2].Value);
        Assert.Empty(uploads[2].Selectors);

        // Kaydedilen senaryo aynen oynatılabilir: gizli ve anlık girdiler için önceki tıklamalar da kayıtlı.
        var result = await new Player().RunAsync(recorder.Scenario, Fast());
        Assert.True(result.SuccessCount == 1, result.Iterations.FirstOrDefault()?.Error);
    }

    [Fact]
    public async Task Uploads_a_different_file_for_each_data_row_and_submits_it()
    {
        Write("ahmet_cv.txt", "AHMET-ICERIK");
        Write("ayse_cv.txt", "AYSE-ICERIK");
        var csv = Write("adaylar.csv", "Ad,CV\nAhmet,ahmet_cv.txt\nAyşe,ayse_cv.txt\n");
        var data = DataSourceLoader.Load(csv);

        var scenario = new Scenario
        {
            Steps =
            [
                new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "upload" },
                // Göreli yollar veri dosyasının klasöründe aranır.
                new Step { Type = StepType.Upload, Selectors = ["#cv"], Parameter = "CV", TargetName = "CV" },
                new Step { Type = StepType.WaitForText, Value = "cv: {{CV}}" },
                new Step { Type = StepType.Click, Selectors = ["button[type=submit]"], WaitForNavigation = true },
                new Step { Type = StepType.WaitForText, Value = "yuklendi" },
            ],
        };

        var result = await new Player().RunAsync(scenario, Fast(data));

        Assert.True(result.SuccessCount == 2, string.Join(" / ", result.Iterations.Select(i => i.Error)));
        var bodies = _server.Uploads.ToArray();
        Assert.Equal(2, bodies.Length);
        Assert.Contains("filename=\"ahmet_cv.txt\"", bodies[0]);
        Assert.Contains("AHMET-ICERIK", bodies[0]);
        Assert.Contains("filename=\"ayse_cv.txt\"", bodies[1]);
        Assert.Contains("AYSE-ICERIK", bodies[1]);
    }

    [Fact]
    public async Task Missing_file_fails_only_that_row()
    {
        Write("var.txt", "x");
        var csv = Write("liste.csv", "CV\nvar.txt\nyok.txt\n");
        var scenario = new Scenario
        {
            Steps =
            [
                new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "upload" },
                new Step { Type = StepType.Upload, Selectors = ["#cv"], Parameter = "CV" },
            ],
        };

        var result = await new Player().RunAsync(scenario, Fast(DataSourceLoader.Load(csv)));

        Assert.True(result.Iterations[0].Success);
        Assert.False(result.Iterations[1].Success);
        Assert.Contains("yok.txt", result.Iterations[1].Error);
        Assert.Contains("bulunamadı", result.Iterations[1].Error);
    }

    [Fact]
    public async Task Too_many_files_for_single_input_gives_clear_error()
    {
        var a = Write("a.txt", "1");
        var b = Write("b.txt", "2");
        var scenario = new Scenario
        {
            Steps =
            [
                new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "upload" },
                new Step { Type = StepType.Upload, Selectors = ["#cv"], Value = $"{a}|{b}" },
            ],
        };

        var result = await new Player().RunAsync(scenario, Fast());

        Assert.Contains("tek dosya", Assert.Single(result.Iterations).Error);
    }

    private static async Task WaitForTextAsync(Microsoft.Playwright.IPage page, string text)
    {
        for (var i = 0; i < 50; i++)
        {
            if ((await page.TextContentAsync("#out"))?.Contains(text) == true)
                return;
            await Task.Delay(100);
        }

        Assert.Fail($"Sayfada görünmedi: {text}. Görünen: {await page.TextContentAsync("#out")}");
    }
}
