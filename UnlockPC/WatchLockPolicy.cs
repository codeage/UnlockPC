using System.Diagnostics;

namespace UnlockPC;

internal sealed class WatchLockPolicy
{
    private static readonly TimeSpan WeakWindow = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan WeakGap = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan WeakSampleAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequiredIdle = TimeSpan.FromSeconds(10);
    private readonly Queue<WatchSignalSample> _weak = new();
    private readonly Queue<long> _strong = new();
    private WatchRuleSettings _rules = new();
    private long _lastSample;

    public WatchReturnReason? ReturnReason { get; private set; }
    public WatchUnlockBlock? LastStrongSignalBlock { get; private set; }

    public void SetRules(WatchRuleSettings rules, long now)
    {
        _rules = rules;
        Reset(now);
    }

    public void Reset(long now, WatchReturnReason? returnReason = null)
    {
        ResetSamples(now);
        ReturnReason = returnReason;
    }

    public void ResetSamples(long now)
    {
        _lastSample = now;
        _weak.Clear();
        _strong.Clear();
    }

    public TimeSpan ReturnDelayRemaining(long? lockedAt, long now) => lockedAt is { } start
        ? TimeSpan.FromSeconds(_rules.UnlockDelaySeconds) - Stopwatch.GetElapsedTime(start, now) : TimeSpan.Zero;

    public WatchSignalDecision? Observe(long now, ReadOnlySpan<WatchSignalSample> samples, bool locked, long? lockedAt = null)
    {
        LastStrongSignalBlock = null;
        TimeSpan returnWindow = TimeSpan.FromSeconds(_rules.ReturnWindowSeconds);
        while (_strong.TryPeek(out long oldest) && Stopwatch.GetElapsedTime(oldest, now) > returnWindow)
            _strong.Dequeue();

        foreach (var sample in samples)
        {
            if (sample.Timestamp <= _lastSample || sample.Timestamp > now)
                continue;

            TimeSpan age = Stopwatch.GetElapsedTime(sample.Timestamp, now);
            bool strongWhileLocked = locked && sample.Rssi >= _rules.ReturnRssi && age <= returnWindow;
            if (strongWhileLocked)
            {
                LastStrongSignalBlock = ReturnReason is null ? WatchUnlockBlock.WaitingForDeparture
                    : ReturnDelayRemaining(lockedAt, sample.Timestamp) > TimeSpan.Zero ? WatchUnlockBlock.WaitingForDelay
                    : GetWatchUnlockBlock(sample.NearbyFlags);
            }

            if (locked && ReturnReason is not null)
            {
                _lastSample = sample.Timestamp;
                if (strongWhileLocked && LastStrongSignalBlock is null)
                {
                    _strong.Enqueue(sample.Timestamp);
                    if (_strong.Count >= _rules.ReturnPacketCount)
                        return new WatchSignalDecision(_strong.Count,
                            Stopwatch.GetElapsedTime(_strong.Peek(), sample.Timestamp), sample.Rssi);
                    LastStrongSignalBlock = WatchUnlockBlock.WaitingForBroadcastCount;
                }
                continue;
            }

            if (age > WeakWindow)
                continue;

            TimeSpan gap = Stopwatch.GetElapsedTime(_lastSample, sample.Timestamp);
            _lastSample = sample.Timestamp;
            if (sample.Rssi > _rules.WeakRssi || gap > WeakGap)
                _weak.Clear();
            if (sample.Rssi > _rules.WeakRssi)
                continue;

            Expire(sample.Timestamp);
            _weak.Enqueue(sample);
            if (locked && ConfirmDeparture(now, sample) is not null)
            {
                ReturnReason = WatchReturnReason.ObservedDeparture;
                _weak.Clear();
            }
        }

        Expire(now);
        if (locked || _weak.Count == 0)
            return null;

        // A newer non-weak packet clears the queue, so its tail is the latest accepted sample.
        return ConfirmDeparture(now, _weak.Last());
    }

    public static bool InputAllowsLock(TimeSpan? idleTime) => idleTime >= RequiredIdle;

    private WatchUnlockBlock? GetWatchUnlockBlock(byte? flags)
    {
        if (!_rules.RequireWatchUnlocked)
            return null;
        return (flags & 0xA0) switch
        {
            0x80 => null,
            0x20 => WatchUnlockBlock.WatchAppearsLocked,
            _ => WatchUnlockBlock.WatchUnlockUnconfirmed
        };
    }

    private WatchSignalDecision? ConfirmDeparture(long now, WatchSignalSample latest)
    {
        if (Stopwatch.GetElapsedTime(latest.Timestamp, now) > WeakSampleAge)
            return null;

        // The entire sequence must also be inside the window at decision time.
        Expire(now);
        if (!_weak.TryPeek(out var first))
            return null;

        int veryWeakRssi = _rules.VeryWeakRssi;
        if (latest.Rssi <= veryWeakRssi)
        {
            foreach (var sample in _weak)
            {
                if (sample.Rssi > veryWeakRssi)
                    continue;

                var duration = Stopwatch.GetElapsedTime(sample.Timestamp, latest.Timestamp);
                if (duration >= TimeSpan.FromSeconds(3))
                    return new WatchSignalDecision(_weak.Count, duration, latest.Rssi);
                break;
            }
        }

        var weakDuration = Stopwatch.GetElapsedTime(first.Timestamp, latest.Timestamp);
        return _weak.Count >= 3 && weakDuration >= TimeSpan.FromSeconds(6)
            ? new WatchSignalDecision(_weak.Count, weakDuration, latest.Rssi)
            : null;
    }

    private void Expire(long now)
    {
        while (_weak.TryPeek(out var first) && Stopwatch.GetElapsedTime(first.Timestamp, now) > WeakWindow)
            _weak.Dequeue();
    }
}

internal enum WatchReturnReason { ObservedDeparture, AutomaticLock, SystemResume }
internal enum WatchUnlockBlock
{
    WaitingForDeparture, WaitingForDelay, WatchAppearsLocked, WatchUnlockUnconfirmed, WaitingForBroadcastCount
}

internal readonly record struct WatchSignalDecision(int SampleCount, TimeSpan Duration, short Rssi);

internal readonly record struct WatchSignalSample(long Timestamp, short Rssi, byte? NearbyFlags = null);
