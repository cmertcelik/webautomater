using Microsoft.Playwright;
using WebAutomater.Core.Models;
using WebAutomater.Core.Playback;
using WebAutomater.Core.Recording;
using WebAutomater.Core.Storage;

namespace WebAutomater.Tests;

public class ScenarioGraphTests
{
    private static Scenario Chain(int count)
    {
        var scenario = new Scenario();
        for (var i = 0; i < count; i++)
            scenario.Steps.Add(new Step { Type = StepType.Wait, Value = "0" });
        ScenarioGraph.EnsureGraph(scenario);
        return scenario;
    }

    [Fact]
    public void Old_flat_scenario_becomes_same_order_chain()
    {
        var s = Chain(3);
        Assert.True(s.Graph);
        Assert.Equal(s.Steps[0].Id, s.StartStepId);
        Assert.Equal(s.Steps[1].Id, s.Steps[0].Next);
        Assert.Equal(s.Steps[2].Id, s.Steps[1].Next);
        Assert.Null(s.Steps[2].Next);
    }

    [Fact]
    public void Remove_reconnects_all_incoming_links_and_start()
    {
        var s = Chain(3);
        var (a, b, c) = (s.Steps[0], s.Steps[1], s.Steps[2]);
        a.OnError = b.Id;
        ScenarioGraph.Remove(s, b);
        Assert.Equal(c.Id, a.Next);
        Assert.Equal(c.Id, a.OnError);

        ScenarioGraph.Remove(s, a);
        Assert.Equal(c.Id, s.StartStepId);
    }

    [Fact]
    public void Insert_before_and_after_keep_flow()
    {
        var s = Chain(2);
        var (a, b) = (s.Steps[0], s.Steps[1]);
        var before = new Step { Type = StepType.Wait, Value = "0" };
        ScenarioGraph.InsertBefore(s, a, before);
        Assert.Equal(before.Id, s.StartStepId);
        Assert.Equal(a.Id, before.Next);

        var after = new Step { Type = StepType.Wait, Value = "0" };
        ScenarioGraph.InsertAfter(s, a, after);
        Assert.Equal(after.Id, a.Next);
        Assert.Equal(b.Id, after.Next);
    }

    [Fact]
    public void Path_goes_through_branches_and_is_null_when_unreachable()
    {
        var s = Chain(2);
        var (a, b) = (s.Steps[0], s.Steps[1]);
        var branch = new Step { Type = StepType.Wait, Value = "0" };
        var orphan = new Step { Type = StepType.Wait, Value = "0" };
        s.Steps.Add(branch);
        s.Steps.Add(orphan);
        a.OnError = branch.Id;

        var path = ScenarioGraph.PathTo(s, branch, StepPort.Error)!;
        Assert.Equal([(a, StepPort.Error), (branch, StepPort.Error)], path);
        Assert.Null(ScenarioGraph.PathTo(s, orphan));
        Assert.NotNull(ScenarioGraph.PathTo(s, b));
    }

    [Fact]
    public void Layout_places_every_node_uniquely_even_with_a_cycle()
    {
        var s = Chain(4);
        s.Steps[3].Next = s.Steps[0].Id;
        s.Steps[1].OnError = s.Steps[3].Id;
        ScenarioGraph.AutoLayout(s);
        Assert.All(s.Steps, x => Assert.NotNull(x.X));
        Assert.Equal(4, s.Steps.Select(x => (x.X, x.Y)).Distinct().Count());
    }

    [Fact]
    public void Duplicate_keeps_settings_but_no_links_and_leaves_flow_untouched()
    {
        var s = Chain(2);
        var (a, b) = (s.Steps[0], s.Steps[1]);
        a.Type = StepType.Fill;
        a.Selectors = ["#ad"];
        a.Value = "doğru";
        a.OnError = b.Id;
        ScenarioGraph.AutoLayout(s);

        var copy = ScenarioGraph.Duplicate(s, a);
        copy.Selectors.Add("#yeni");

        Assert.NotEqual(a.Id, copy.Id);
        Assert.Equal((StepType.Fill, "doğru"), (copy.Type, copy.Value));
        Assert.Null(copy.Next);
        Assert.Null(copy.OnError);
        Assert.Equal(b.Id, a.Next);
        Assert.Equal(["#ad"], a.Selectors); // Kopya düzenlenince asıl düğüm etkilenmez.
        Assert.Same(copy, s.Steps[1]);

        var second = ScenarioGraph.Duplicate(s, a);
        Assert.NotEqual((copy.X, copy.Y), (second.X, second.Y)); // Üst üste binmez.
    }

    [Fact]
    public void Default_loop_start_skips_leading_navigation()
    {
        var s = new Scenario
        {
            Steps =
            [
                new Step { Type = StepType.Navigate, Value = "http://x" },
                new Step { Type = StepType.Click, Selectors = ["#a"] },
            ],
        };
        Assert.Same(s.Steps[1], new ScenarioFlow(s).LoopStart);
        s.LoopStartStepId = s.Steps[0].Id;
        ScenarioGraph.EnsureGraph(s);
        Assert.Same(s.Steps[0], new ScenarioFlow(s).LoopStart);
    }

    [Fact]
    public async Task Graph_survives_save_and_load_and_old_format_still_loads()
    {
        var dir = Directory.CreateTempSubdirectory("wa-graph").FullName;
        try
        {
            var s = Chain(2);
            s.Steps[0].Type = StepType.Condition;
            s.Steps[0].Condition = ConditionKind.TextEquals;
            s.Steps[0].OnFalse = s.Steps[1].Id;
            s.Steps[0].X = 12;
            var path = Path.Combine(dir, "a.wa.json");
            ScenarioStore.Save(s, path);
            var loaded = await ScenarioStore.LoadAsync(path);
            Assert.Equal(ConditionKind.TextEquals, loaded.Steps[0].Condition);
            Assert.Equal(s.Steps[1].Id, loaded.Steps[0].OnFalse);
            Assert.Equal(12, loaded.Steps[0].X);
            Assert.True(loaded.Graph);

            var old = Path.Combine(dir, "old.wa.json");
            File.WriteAllText(old, """{"formatVersion":1,"name":"e","steps":[{"type":"navigate","value":"http://x"}]}""");
            Assert.False((await ScenarioStore.LoadAsync(old)).Graph);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("  ONAYLANDI ", "onaylandı", ConditionKind.TextEquals, true)]
    [InlineData("İzmir", "izmir", ConditionKind.TextEquals, true)]
    [InlineData("Ali  Veli", "Ali Veli", ConditionKind.TextEquals, true)]
    [InlineData("Onaylandı", "Red", ConditionKind.TextEquals, false)]
    [InlineData("Durum: Onaylandı", "onay", ConditionKind.TextContains, true)]
    public void Text_comparison_ignores_spacing_and_turkish_case(string actual, string expected, ConditionKind kind, bool result) =>
        Assert.Equal(result, Player.TextMatches(actual, expected, kind));
}

/// <summary>Gerçek tarayıcıyla dallanma, B planı, sayfayı açık tutma ve kontrol modu.</summary>
public sealed class FlowPlaybackTests : IDisposable
{
    private readonly TestServer _server = new();

    public void Dispose() => _server.Dispose();

    private static PlaybackOptions Options(int iterations = 1) => new()
    {
        Headless = true,
        SpeedFactor = 0,
        Iterations = iterations,
        ActionTimeoutMs = 1500,
    };

    private Step Open(string query = "") => new() { Type = StepType.Navigate, Value = _server.BaseUrl + "flow" + query };

    private static Step Click(string selector) => new() { Type = StepType.Click, Selectors = [selector], WaitForNavigation = true };

    private static Step Check(ConditionKind kind, string selector, string? value = null) =>
        new() { Type = StepType.Condition, Condition = kind, Selectors = [selector], Value = value, TimeoutMs = 400 };

    private static Scenario Build(params Step[] steps) =>
        new() { Graph = true, Steps = [.. steps], StartStepId = steps[0].Id };

    private string[] Yol() => _server.Submissions.ToArray();

    [Theory]
    [InlineData("", "yol=a")]
    [InlineData("?kapali=1", "yol=b")]
    public async Task Button_state_chooses_the_branch(string query, string expected)
    {
        var (open, check, a, b) = (Open(query), Check(ConditionKind.Enabled, "#islem"), Click("#plan2"), Click("#plan"));
        open.Next = check.Id;
        check.Next = a.Id;
        check.OnFalse = b.Id;

        var result = await new Player().RunAsync(Build(open, check, a, b), Options());

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal([expected], Yol());
    }

    [Theory]
    [InlineData(ConditionKind.TextEquals, "onaylandı", "yol=a")]
    [InlineData(ConditionKind.TextEquals, "Reddedildi", "yol=b")]
    [InlineData(ConditionKind.TextContains, "Onay", "yol=a")]
    public async Task Table_text_chooses_the_branch(ConditionKind kind, string value, string expected)
    {
        var (open, check, a, b) = (Open(), Check(kind, "#durum", value), Click("#plan2"), Click("#plan"));
        open.Next = check.Id;
        check.Next = a.Id;
        check.OnFalse = b.Id;

        await new Player().RunAsync(Build(open, check, a, b), Options());

        Assert.Equal([expected], Yol());
    }

    [Fact]
    public async Task Page_text_check_catches_a_site_message()
    {
        var (open, check, b) = (Open(), new Step { Type = StepType.Condition, Condition = ConditionKind.PageHasText, Value = "Ali Veli", TimeoutMs = 400 }, Click("#plan"));
        open.Next = check.Id;
        check.Next = b.Id;

        Assert.Equal(1, (await new Player().RunAsync(Build(open, check, b), Options())).SuccessCount);
        Assert.Equal(["yol=b"], Yol());
    }

    [Fact]
    public async Task False_without_a_link_fails_with_a_clear_message()
    {
        var (open, check) = (Open("?kapali=1"), Check(ConditionKind.Enabled, "#islem"));
        check.TargetName = "İşlem";
        check.TargetKind = "button";
        open.Next = check.Id;

        var iteration = Assert.Single((await new Player().RunAsync(Build(open, check), Options())).Iterations);

        Assert.False(iteration.Success);
        Assert.Contains("Kontrol sağlanmadı", iteration.Error);
        Assert.Contains("öğe pasif", iteration.Error);
    }

    [Theory]
    [InlineData("Ali Veli", "fullname=doğru")]
    [InlineData("Olmayan yazı", "fullname=yanlış")]
    public async Task Cloned_node_edited_and_wired_to_no_port_runs_on_that_branch(string text, string expected)
    {
        var open = new Step { Type = StepType.Navigate, Value = _server.BaseUrl };
        var check = new Step { Type = StepType.Condition, Condition = ConditionKind.PageHasText, Value = text == "Ali Veli" ? "Ad Soyad" : text, TimeoutMs = 300 };
        var fill = new Step { Type = StepType.Fill, Selectors = ["#fullname"], Value = "doğru" };
        var send = new Step { Type = StepType.Click, Selectors = ["button[type=submit]"], WaitForNavigation = true };
        open.Next = check.Id;
        check.Next = fill.Id;
        fill.Next = send.Id;
        var scenario = Build(open, check, fill, send);

        var clone = ScenarioGraph.Duplicate(scenario, fill);
        clone.Value = "yanlış";
        check.OnFalse = clone.Id;
        clone.Next = send.Id;

        Assert.Equal(1, (await new Player().RunAsync(scenario, Options())).SuccessCount);
        Assert.StartsWith(expected, Assert.Single(Yol()));
    }

    [Fact]
    public async Task Missing_element_is_a_no_not_an_error()
    {
        var (open, check, b) = (Open(), Check(ConditionKind.Visible, "#yok"), Click("#plan"));
        open.Next = check.Id;
        check.OnFalse = b.Id;

        Assert.Equal(1, (await new Player().RunAsync(Build(open, check, b), Options())).SuccessCount);
        Assert.Equal(["yol=b"], Yol());
    }

    [Fact]
    public async Task Error_port_is_plan_b_and_iteration_stays_successful()
    {
        var (open, broken, b) = (Open(), Click("#yok"), Click("#plan"));
        open.Next = broken.Id;
        broken.OnError = b.Id;
        var events = new List<PlaybackEventKind>();

        var result = await new Player().RunAsync(Build(open, broken, b), Options(), e => { lock (events) events.Add(e.Kind); });

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(["yol=b"], Yol());
        Assert.Contains(PlaybackEventKind.StepHandledError, events);
        Assert.DoesNotContain(PlaybackEventKind.StepFailed, events);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task End_node_stops_with_chosen_outcome(bool asFailure, bool success)
    {
        var (open, end, never) = (Open(), new Step { Type = StepType.End, EndAsFailure = asFailure }, Click("#plan"));
        open.Next = end.Id;
        end.Next = never.Id;

        var result = await new Player().RunAsync(Build(open, end, never), Options());

        Assert.Equal(success, Assert.Single(result.Iterations).Success);
        Assert.Empty(Yol());
    }

    [Fact]
    public async Task Endless_cycle_is_stopped_with_a_message()
    {
        var (open, wait) = (Open(), new Step { Type = StepType.Wait, Value = "0" });
        open.Next = wait.Id;
        wait.Next = wait.Id;

        var iteration = Assert.Single((await new Player().RunAsync(Build(open, wait), Options())).Iterations);

        Assert.False(iteration.Success);
        Assert.Contains("döngü", iteration.Error);
    }

    [Fact]
    public async Task Keep_page_runs_setup_once_and_repeats_without_reloading()
    {
        var (open, click) = (Open(), new Step { Type = StepType.Click, Selectors = ["#islem"] });
        var final = Check(ConditionKind.TextEquals, "#sayac", "{{tekrar}}");
        open.Next = click.Id;
        click.Next = final.Id;
        var options = Options(3);
        options.KeepPageOpen = true;

        var result = await new Player().RunAsync(Build(open, click, final), options);

        // Sayaç sayfa yenilenseydi her tekrarda 1 olurdu: 2. ve 3. kontrol hata verirdi.
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(1, _server.Hits["/flow"]);
    }

    [Fact]
    public async Task Without_keep_page_every_iteration_reopens_the_page()
    {
        var open = Open();
        open.Next = null;
        var result = await new Player().RunAsync(Build(open), Options(3));
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(3, _server.Hits["/flow"]);
    }

    [Fact]
    public async Task Keep_page_returns_to_loop_page_when_flow_ends_elsewhere()
    {
        var (open, go) = (Open(), Click("#plan2"));
        open.Next = go.Id;
        var options = Options(3);
        options.KeepPageOpen = true;

        var result = await new Player().RunAsync(Build(open, go), options);

        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(["yol=a", "yol=a", "yol=a"], Yol());
        Assert.Equal(3, _server.Hits["/flow"]);
    }

    [Fact]
    public async Task Keep_page_reloads_after_a_failed_iteration_and_continues()
    {
        var (open, click) = (Open(), new Step { Type = StepType.Click, Selectors = ["#islem"] });
        var first = Check(ConditionKind.TextEquals, "#sayac", "1");
        open.Next = click.Id;
        click.Next = first.Id;
        var options = Options(2);
        options.KeepPageOpen = true;

        // İkinci turda sayaç sayfa yenilendiği için yine 1: ilk tur başarılı ise yenileme olmaz, 2 olur ve hata verir.
        // Üçüncü turda (hata sonrası yenilenince) yeniden 1 olur.
        options.Iterations = 3;
        var result = await new Player().RunAsync(Build(open, click, first), options);

        Assert.Equal([true, false, true], result.Iterations.Select(i => i.Success));
        Assert.Equal(2, _server.Hits["/flow"]);
    }

    [Fact]
    public async Task User_chosen_loop_start_is_respected()
    {
        var (open, setup, repeat) = (Open(), new Step { Type = StepType.Click, Selectors = ["#islem"] }, new Step { Type = StepType.Click, Selectors = ["#islem"] });
        var (check, go, wait) = (Check(ConditionKind.TextEquals, "#sayac", "3"), Click("#plan2"), new Step { Type = StepType.Wait, Value = "0" });
        open.Next = setup.Id;
        setup.Next = repeat.Id;
        repeat.Next = check.Id;
        check.Next = go.Id;
        check.OnFalse = wait.Id;
        var scenario = Build(open, setup, repeat, check, go, wait);
        scenario.LoopStartStepId = repeat.Id;
        var options = Options(2);
        options.KeepPageOpen = true;

        var result = await new Player().RunAsync(scenario, options);

        // 1. tur: hazırlık + döngü = 2, Hayır. 2. tur yalnızca döngü = 3, Evet. Hazırlık yeniden çalışsaydı 4 olurdu.
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(["yol=a"], Yol());
        Assert.Equal(1, _server.Hits["/flow"]);
    }
}

public sealed class CheckModeTests : IDisposable
{
    private readonly TestServer _server = new();

    public void Dispose() => _server.Dispose();

    private static async Task PickAsync(IPage page, string selector, string kind, string? text = null)
    {
        // Pasif butonda Playwright'ın tıklaması "aktif olana kadar" bekler; gerçek fare olayı kullanılır.
        var box = await page.Locator(selector).BoundingBoxAsync();
        await page.Mouse.ClickAsync(box!.X + box.Width / 2, box.Y + box.Height / 2);
        var panel = page.Locator("wa-check-panel");
        await panel.Locator($"input[value={kind}]").CheckAsync();
        if (text is not null)
            await panel.Locator("input[type=text]").FillAsync(text);
        await panel.Locator("button[data-action=add]").ClickAsync();
    }

    private static async Task WaitForStepsAsync(Recorder recorder, int count)
    {
        for (var i = 0; i < 50 && recorder.Scenario.Steps.Count < count; i++)
            await Task.Delay(50);
    }

    [Fact]
    public async Task Disabled_button_can_be_inspected_and_site_is_not_touched()
    {
        await using var recorder = new Recorder { Headless = true };
        await recorder.StartAsync(_server.BaseUrl + "flow?kapali=1");
        var page = recorder.Pages[0];

        await recorder.SetModeAsync(RecorderMode.Check);
        await PickAsync(page, "#islem", "disabled");
        await WaitForStepsAsync(recorder, 2);

        var step = recorder.Scenario.Steps[1];
        Assert.Equal(StepType.Condition, step.Type);
        Assert.Equal(ConditionKind.Disabled, step.Condition);
        Assert.Contains("#islem", step.Selectors);
        Assert.Equal(step.Id, recorder.Scenario.Steps[0].Next);
        Assert.Equal(RecorderMode.Record, recorder.Mode);
    }

    [Fact]
    public async Task Clicks_in_check_mode_do_not_reach_the_site_and_are_not_recorded()
    {
        await using var recorder = new Recorder { Headless = true };
        await recorder.StartAsync(_server.BaseUrl + "flow");
        var page = recorder.Pages[0];

        await recorder.SetModeAsync(RecorderMode.Check);
        await page.Locator("#islem").ClickAsync(new LocatorClickOptions { Force = true });
        await Task.Delay(200);
        Assert.Equal("0", await page.Locator("#sayac").InnerTextAsync());
        await page.Locator("wa-check-panel button[data-action=cancel]").ClickAsync();
        Assert.Single(recorder.Scenario.Steps);

        // Kayıt moduna dönünce aynı tıklama hem siteye ulaşır hem kaydedilir.
        await recorder.SetModeAsync(RecorderMode.Record);
        await page.Locator("#islem").ClickAsync();
        await WaitForStepsAsync(recorder, 2);
        Assert.Equal("1", await page.Locator("#sayac").InnerTextAsync());
        Assert.Equal(StepType.Click, recorder.Scenario.Steps[1].Type);
    }

    [Fact]
    public async Task Table_cell_text_check_uses_position_not_its_text_and_plays_back()
    {
        await using var recorder = new Recorder { Headless = true };
        await recorder.StartAsync(_server.BaseUrl + "flow");
        var page = recorder.Pages[0];

        await recorder.SetModeAsync(RecorderMode.Check);
        await PickAsync(page, "#durum", "textEquals");
        await WaitForStepsAsync(recorder, 2);
        await recorder.StopAsync();

        var step = recorder.Scenario.Steps[1];
        Assert.Equal(ConditionKind.TextEquals, step.Condition);
        Assert.Equal("Onaylandı", step.Value);
        Assert.DoesNotContain(step.Selectors, s => s.StartsWith("internal:text"));

        var result = await new Player().RunAsync(recorder.Scenario, new PlaybackOptions { Headless = true, SpeedFactor = 0 });
        Assert.Equal(1, result.SuccessCount);
    }

    [Fact]
    public async Task Toolbar_in_page_switches_mode_and_mode_survives_navigation()
    {
        await using var recorder = new Recorder { Headless = true };
        var changes = new List<RecorderMode>();
        recorder.ModeChanged += m => { lock (changes) changes.Add(m); };
        await recorder.StartAsync(_server.BaseUrl + "flow");
        var page = recorder.Pages[0];

        await page.Locator("wa-recording-badge button[data-mode=check]").ClickAsync();
        for (var i = 0; i < 40 && recorder.Mode != RecorderMode.Check; i++)
            await Task.Delay(50);
        Assert.Equal(RecorderMode.Check, recorder.Mode);
        Assert.Single(recorder.Scenario.Steps); // Çubuk tıklaması adım olmaz.

        await page.GotoAsync(_server.BaseUrl + "flow");
        await page.Locator("#islem").ClickAsync(new LocatorClickOptions { Force = true });
        await page.Locator("wa-check-panel").WaitForAsync();
        Assert.Equal("0", await page.Locator("#sayac").InnerTextAsync());
    }

    [Fact]
    public async Task Branch_recording_replays_path_without_capturing_and_attaches_new_steps()
    {
        var open = new Step { Type = StepType.Navigate, Value = _server.BaseUrl + "flow" };
        var click = new Step { Type = StepType.Click, Selectors = ["#islem"] };
        var check = new Step { Type = StepType.Condition, Condition = ConditionKind.Visible, Selectors = ["#islem"] };
        open.Next = click.Id;
        click.Next = check.Id;
        var scenario = new Scenario { Graph = true, Steps = [open, click, check], StartStepId = open.Id };

        await using var recorder = new Recorder(scenario, (check, StepPort.False)) { Headless = true, CapturePaused = true };
        await recorder.StartAsync("about:blank");
        var page = recorder.Pages[0];

        var error = await new Player().ReplayPathAsync(page, ScenarioGraph.PathTo(scenario, check, StepPort.False)!,
            new PlaybackOptions { SpeedFactor = 0, ActionTimeoutMs = 2000 }, _ => { });
        Assert.Null(error);
        Assert.Equal("1", await page.Locator("#sayac").InnerTextAsync());
        await Task.Delay(200);
        Assert.Equal(3, scenario.Steps.Count); // Otomatik gidiş kaydedilmedi.

        recorder.CapturePaused = false;
        await page.Locator("#plan2").ClickAsync();
        await WaitForStepsAsync(recorder, 4);
        Assert.Equal(4, scenario.Steps.Count);
        Assert.Equal(scenario.Steps[3].Id, check.OnFalse);
        Assert.Null(check.Next);
    }
}
