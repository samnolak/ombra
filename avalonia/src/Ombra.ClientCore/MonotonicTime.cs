namespace Ombra.ClientCore;

/// <summary>A process-local duration; wall-clock timestamps remain separate for chronology.</summary>
public readonly struct MonotonicTime
{
    private readonly TimeProvider? _clock;
    private readonly long _started;
    private readonly TimeSpan _elapsed;

    private MonotonicTime(TimeProvider? clock, long started, TimeSpan elapsed) =>
        (_clock, _started, _elapsed) = (clock, started, elapsed);

    public bool IsRunning => _clock is not null;
    public TimeSpan Elapsed => _elapsed + (_clock?.GetElapsedTime(_started) ?? TimeSpan.Zero);
    public static MonotonicTime Start(TimeProvider clock, TimeSpan elapsed = default) => new(clock, clock.GetTimestamp(), elapsed);
    public MonotonicTime Stop() => new(null, 0, Elapsed);
}
