namespace WebAutomater.Core.Models;

/// <summary>Bir düğümün çıkışı: normal devam (kontrolde "Evet"), kontrolde "Hayır", hata durumunda B planı.</summary>
public enum StepPort
{
    Next,
    False,
    Error,
}

/// <summary>
/// Senaryonun düğüm-bağlantı yapısı üzerindeki işlemler. <see cref="Scenario.Graph"/> false olan (eski ya da elle
/// yazılmış) senaryolarda sıra listedeki sıradır; bu yardımcılar her iki biçimde de doğru davranır.
/// </summary>
public static class ScenarioGraph
{
    public const double NodeWidth = 230;
    public const double NodeHeight = 76;
    public const double SpacingX = 280;
    public const double SpacingY = 120;

    public static Guid? GetLink(Step step, StepPort port) => port switch
    {
        StepPort.Next => step.Next,
        StepPort.False => step.OnFalse,
        _ => step.OnError,
    };

    public static void SetLink(Step step, StepPort port, Guid? target)
    {
        switch (port)
        {
            case StepPort.Next:
                step.Next = target;
                break;
            case StepPort.False:
                step.OnFalse = target;
                break;
            default:
                step.OnError = target;
                break;
        }
    }

    /// <summary>Hangi çıkışların o düğümde bulunduğu.</summary>
    public static IEnumerable<StepPort> PortsOf(Step step) => step.Type switch
    {
        StepType.End => [],
        StepType.Condition => [StepPort.Next, StepPort.False],
        // Bekleme bir hata üretmez; diğer tüm işlemler başarısız olabilir.
        StepType.Wait => [StepPort.Next],
        _ => [StepPort.Next, StepPort.Error],
    };

    public static string PortLabel(Step step, StepPort port) => port switch
    {
        StepPort.False => "Hayır",
        StepPort.Error => "Hata",
        _ => step.Type == StepType.Condition ? "Evet" : "",
    };

    /// <summary>Düz listeyi bağlantılı yapıya çevirir: her adım sıradakine bağlanır, davranış aynı kalır.</summary>
    public static void EnsureGraph(Scenario scenario)
    {
        scenario.FormatVersion = Scenario.CurrentFormatVersion;
        if (scenario.Graph)
            return;
        for (var i = 0; i < scenario.Steps.Count; i++)
            scenario.Steps[i].Next = i + 1 < scenario.Steps.Count ? scenario.Steps[i + 1].Id : null;
        scenario.StartStepId = scenario.Steps.FirstOrDefault()?.Id;
        scenario.Graph = true;
    }

    /// <summary>Yeni düğümü verilen düğümün çıkışına ekler; o çıkışın eski hedefi yeni düğümün devamı olur.</summary>
    public static void InsertAfter(Scenario scenario, Step? anchor, Step step, StepPort port = StepPort.Next)
    {
        anchor ??= scenario.Steps.LastOrDefault();
        var index = anchor is null ? 0 : scenario.Steps.IndexOf(anchor) + 1;
        scenario.Steps.Insert(index, step);
        if (!scenario.Graph)
            return;

        if (anchor is null)
        {
            step.Next = scenario.StartStepId;
            scenario.StartStepId = step.Id;
            return;
        }

        step.Next = GetLink(anchor, port);
        SetLink(anchor, port, step.Id);
    }

    /// <summary>Yeni düğümü hedefin önüne ekler: hedefe gelen tüm bağlantılar önce yeni düğüme uğrar.</summary>
    public static void InsertBefore(Scenario scenario, Step target, Step step)
    {
        scenario.Steps.Insert(scenario.Steps.IndexOf(target), step);
        if (!scenario.Graph)
            return;

        Redirect(scenario, target.Id, step.Id, except: step);
        step.Next = target.Id;
    }

    /// <summary>Düğümü siler. Ona gelen bağlantılar normal devamına aktarılır, böylece akış kopmaz.</summary>
    public static void Remove(Scenario scenario, Step step)
    {
        scenario.Steps.Remove(step);
        if (!scenario.Graph)
            return;

        Redirect(scenario, step.Id, step.Next, except: null);
        if (scenario.LoopStartStepId == step.Id)
            scenario.LoopStartStepId = null;
    }

    private static void Redirect(Scenario scenario, Guid from, Guid? to, Step? except)
    {
        foreach (var other in scenario.Steps)
        {
            if (other == except)
                continue;
            foreach (var port in Enum.GetValues<StepPort>())
            {
                if (GetLink(other, port) == from)
                    SetLink(other, port, to);
            }
        }

        if (scenario.StartStepId == from)
            scenario.StartStepId = to;
    }

    /// <summary>
    /// Düğümün kopyasını yanına koyar. Kopyanın hiç bağlantısı yoktur: mevcut akış değişmez,
    /// kullanıcı düzenleyip istediği çıkışa bağlar.
    /// </summary>
    public static Step Duplicate(Scenario scenario, Step step)
    {
        var copy = step.Clone();
        var steps = scenario.Steps.ToArray();
        var x = (step.X ?? 0) + SpacingX;
        var y = step.Y ?? 0;
        while (steps.Any(s => s.X is { } sx && s.Y is { } sy && Math.Abs(sx - x) < NodeWidth && Math.Abs(sy - y) < NodeHeight))
            x += SpacingX;
        copy.X = x;
        copy.Y = y;
        scenario.Steps.Insert(scenario.Steps.IndexOf(step) + 1, copy);
        return copy;
    }

    /// <summary>Başlangıçtan hedefe, hedefin kendisi de dahil, en kısa yol. Ulaşılamıyorsa null.</summary>
    public static List<(Step Step, StepPort Port)>? PathTo(Scenario scenario, Step target, StepPort lastPort = StepPort.Next)
    {
        var flow = new ScenarioFlow(scenario);
        if (flow.Start is not { } start)
            return null;

        var parents = new Dictionary<Step, (Step Parent, StepPort Port)>();
        var seen = new HashSet<Step> { start };
        var queue = new Queue<Step>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == target)
                break;
            foreach (var port in Enum.GetValues<StepPort>())
            {
                if (flow.Next(current, port) is { } next && seen.Add(next))
                {
                    parents[next] = (current, port);
                    queue.Enqueue(next);
                }
            }
        }

        if (!seen.Contains(target))
            return null;

        var path = new List<(Step, StepPort)> { (target, lastPort) };
        for (var cursor = target; parents.TryGetValue(cursor, out var parent); cursor = parent.Parent)
            path.Insert(0, (parent.Parent, parent.Port));
        return path;
    }

    /// <summary>Tüm düğümlere yeniden yerleşim verir: ana yol aynı sütunda, her dal kendi sütununda.</summary>
    public static void AutoLayout(Scenario scenario)
    {
        var flow = new ScenarioFlow(scenario);
        var placed = new HashSet<Step>();
        var nextColumn = 0;
        var stack = new Stack<(Step Step, int Column, int Row)>();

        void PlaceFrom(Step root, int column, int row)
        {
            stack.Push((root, column, row));
            while (stack.Count > 0)
            {
                var (step, col, r) = stack.Pop();
                if (!placed.Add(step))
                    continue;
                if (col < 0)
                    col = nextColumn++;
                step.X = col * SpacingX;
                step.Y = r * SpacingY;

                // Yığına en son giren önce işlenir: ana yol, dallardan önce yerleşsin.
                if (flow.Next(step, StepPort.Error) is { } error)
                    stack.Push((error, -1, r + 1));
                if (flow.Next(step, StepPort.False) is { } no)
                    stack.Push((no, -1, r + 1));
                if (flow.Next(step, StepPort.Next) is { } next)
                    stack.Push((next, col, r + 1));
            }
        }

        if (flow.Start is { } start)
            PlaceFrom(start, nextColumn++, 0);
        foreach (var orphan in scenario.Steps.ToArray().Where(s => !placed.Contains(s)))
            PlaceFrom(orphan, nextColumn++, 0);
    }

    /// <summary>Konumu olmayan düğümlere geldiği düğümün yanında yer verir; hiçbirinde konum yoksa tümünü dizer.</summary>
    public static void EnsurePositions(Scenario scenario)
    {
        // Kayıt sürerken liste arka planda büyüyebilir; anlık kopya üzerinden çalışılır.
        var steps = scenario.Steps.ToArray();
        if (steps.All(s => s.X is null))
        {
            AutoLayout(scenario);
            return;
        }

        foreach (var step in steps.Where(s => s.X is null))
        {
            var parent = steps
                .SelectMany(p => Enum.GetValues<StepPort>().Select(port => (Step: p, Port: port)))
                .FirstOrDefault(x => x.Step.X is not null && GetLink(x.Step, x.Port) == step.Id);
            if (parent.Step is not null)
            {
                step.X = parent.Step.X + (parent.Port == StepPort.Next ? 0 : SpacingX);
                step.Y = parent.Step.Y + SpacingY;
            }
            else
            {
                // Bağlantısız yeni düğüm: mevcut düğümlerin sağına.
                step.X = steps.Where(s => s.X is not null).Max(s => s.X!.Value) + SpacingX;
                step.Y = 0;
            }
        }
    }
}

/// <summary>Oynatma ve yol bulma için senaryonun çözümlenmiş hali.</summary>
public sealed class ScenarioFlow
{
    private readonly Scenario _scenario;
    private readonly Step[] _steps;
    private readonly Dictionary<Guid, Step> _byId = [];
    private readonly Dictionary<Step, int> _index = [];

    public ScenarioFlow(Scenario scenario)
    {
        _scenario = scenario;
        // Kayıt sürerken liste arka planda büyüyebilir; anlık kopya üzerinden çalışılır.
        _steps = scenario.Steps.ToArray();
        for (var i = 0; i < _steps.Length; i++)
        {
            _byId[_steps[i].Id] = _steps[i];
            _index[_steps[i]] = i;
        }
    }

    public Step? Start => _scenario.Graph
        ? Find(_scenario.StartStepId)
        : _steps.FirstOrDefault();

    public int IndexOf(Step step) => _index[step];

    public Step? Next(Step step, StepPort port)
    {
        if (_scenario.Graph)
            return Find(ScenarioGraph.GetLink(step, port));

        if (port != StepPort.Next)
            return null;
        var next = _index[step] + 1;
        return next < _steps.Length ? _steps[next] : null;
    }

    /// <summary>
    /// "Sayfayı açık tut" modunda tekrarın başladığı düğüm: kullanıcı seçtiyse o,
    /// yoksa baştaki "Sayfaya git" adımlarından sonraki ilk düğüm.
    /// </summary>
    public Step? LoopStart
    {
        get
        {
            if (Find(_scenario.LoopStartStepId) is { } chosen)
                return chosen;

            var seen = new HashSet<Step>();
            var cursor = Start;
            while (cursor is not null && seen.Add(cursor) && (cursor.Type == StepType.Navigate || !cursor.Enabled))
                cursor = Next(cursor, StepPort.Next);
            return cursor ?? Start;
        }
    }

    private Step? Find(Guid? id) => id is { } value && _byId.TryGetValue(value, out var step) ? step : null;
}
