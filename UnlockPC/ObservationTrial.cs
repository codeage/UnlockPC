namespace UnlockPC;

internal readonly record struct MatchSample(DateTimeOffset SeenAt, short Rssi);
internal readonly record struct TrialWindow(int MatchCount, MatchSample? LastMatch);

internal enum WatchScenario
{
    AwakeUnlocked,
    ScreenOffUnlocked,
    Relocked,
    RestartedNotUnlocked
}

internal sealed class ObservationTrial(WatchScenario scenario, DateTimeOffset startedAt, long startedTimestamp)
{
    public const int WindowSeconds = 10;
    public const int WindowCount = 6;
    public const int DurationSeconds = WindowSeconds * WindowCount;

    public WatchScenario Scenario { get; } = scenario;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public TrialWindow[] Windows { get; } = new TrialWindow[WindowCount];

    public double ElapsedSeconds(long timestamp) =>
        System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp, timestamp).TotalSeconds;

    public void Add(long timestamp, MatchSample sample)
    {
        double elapsed = ElapsedSeconds(timestamp);
        if (elapsed < 0 || elapsed >= DurationSeconds)
            return;

        int index = (int)(elapsed / WindowSeconds);
        TrialWindow previous = Windows[index];
        MatchSample latest = previous.LastMatch is { } last && last.SeenAt > sample.SeenAt ? last : sample;
        Windows[index] = new TrialWindow(previous.MatchCount + 1, latest);
    }
}
