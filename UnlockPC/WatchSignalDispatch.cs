namespace UnlockPC;

internal sealed class WatchSignalDispatch(Func<bool> enabled, Func<Action, bool> enqueue, Action process)
{
    private int _queued;

    public void Signal()
    {
        if (!enabled() || Interlocked.Exchange(ref _queued, 1) != 0)
            return;

        if (!enqueue(Drain))
            Interlocked.Exchange(ref _queued, 0);
    }

    private void Drain()
    {
        // Allow a new notification while this drain is in progress to avoid losing a racing sample.
        Interlocked.Exchange(ref _queued, 0);
        process();
    }
}
