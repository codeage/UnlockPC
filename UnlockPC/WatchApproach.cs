using System.Diagnostics;

namespace UnlockPC;

internal sealed class WatchApproach(int requiredSamples)
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SampleLifetime = TimeSpan.FromSeconds(4);
    private long _lastNear;
    private long _lastSample;
    private long _firstCandidate;
    private int _candidateCount;

    public bool WaitingForReturn { get; private set; }

    public void Reset(long now)
    {
        _lastNear = now;
        _lastSample = now;
        _firstCandidate = 0;
        _candidateCount = 0;
        WaitingForReturn = false;
    }

    public bool Update(long now, long sample, bool isNear = true)
    {
        if (sample <= _lastSample || sample > now || Stopwatch.GetElapsedTime(sample, now) > SampleLifetime)
        {
            if (Stopwatch.GetElapsedTime(_lastNear, now) >= QuietPeriod)
                WaitingForReturn = true;
            return false;
        }

        _lastSample = sample;
        if (Stopwatch.GetElapsedTime(_lastNear, sample) >= QuietPeriod)
            WaitingForReturn = true;
        if (!isNear)
        {
            _firstCandidate = 0;
            _candidateCount = 0;
            return false;
        }

        _lastNear = sample;
        if (!WaitingForReturn)
            return false;

        if (_firstCandidate == 0 || Stopwatch.GetElapsedTime(_firstCandidate, sample) > SampleLifetime)
        {
            _firstCandidate = sample;
            _candidateCount = 0;
        }
        if (++_candidateCount < requiredSamples)
            return false;

        Reset(sample);
        return true;
    }
}

