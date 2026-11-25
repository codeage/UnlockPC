using System.Diagnostics;

namespace UnlockPC;

internal sealed class WatchResumeState
{
    private static readonly TimeSpan SessionStateTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ScanRecoveryTimeout = TimeSpan.FromSeconds(10);
    private const int MaxScanRetries = 4;
    private long _resumedAt;
    private long _nextScanRetry;
    private int _scanRetries;
    private bool _firstRead;

    public bool IsSuspended { get; private set; }
    public bool IsPending { get; private set; }

    public void Suspend()
    {
        IsSuspended = true;
        _resumedAt = 0;
        ClearSession();
    }

    public bool Resume(long now)
    {
        if (_resumedAt != 0)
            return false;

        IsSuspended = false;
        IsPending = true;
        _resumedAt = _nextScanRetry = now;
        _scanRetries = 0;
        return true;
    }

    public bool TryResolve(long now, bool? locked, long lockNotification, out long? sampleFloor)
    {
        sampleFloor = null;
        if (!IsPending || (locked is null && Stopwatch.GetElapsedTime(_resumedAt, now) < SessionStateTimeout))
            return false;

        IsPending = false;
        _firstRead = locked.HasValue;
        if (locked == true)
            sampleFloor = Math.Max(_resumedAt, lockNotification);
        return true;
    }

    public bool TakeFirstRead()
    {
        bool first = _firstRead;
        _firstRead = false;
        return first;
    }

    public void ClearSession()
    {
        IsPending = false;
        _firstRead = false;
    }

    public bool IsRecoveringScan(long now) =>
        _resumedAt != 0 && Stopwatch.GetElapsedTime(_resumedAt, now) <= ScanRecoveryTimeout;

    public bool CanRetryScan(long now)
    {
        if (!IsRecoveringScan(now) || now < _nextScanRetry || _scanRetries >= MaxScanRetries)
            return false;

        _scanRetries++;
        _nextScanRetry = now + Stopwatch.Frequency;
        return true;
    }
}
