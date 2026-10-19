namespace WebAutomater.Core.Playback;

public enum PlaybackEventKind
{
    Info,
    IterationStarted,
    StepStarted,
    StepCompleted,
    StepFailed,
    IterationCompleted,
}

public sealed record PlaybackEvent(
    PlaybackEventKind Kind,
    int Iteration,
    int TotalIterations,
    int StepIndex = -1,
    int TotalSteps = 0,
    string? Message = null,
    int? RowNumber = null);

public sealed record IterationResult(
    int Iteration,
    int? RowNumber,
    bool Success,
    int? FailedStepIndex,
    string? Error,
    TimeSpan Duration,
    string? ScreenshotPath,
    int Attempts = 1);

public sealed class PlaybackResult
{
    public List<IterationResult> Iterations { get; } = [];

    public bool Cancelled { get; set; }

    public int SuccessCount => Iterations.Count(i => i.Success);

    public int FailureCount => Iterations.Count(i => !i.Success);
}
