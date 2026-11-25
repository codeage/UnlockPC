namespace UnlockPC;

internal static class LockCycleTest
{
    internal const int Rounds = 5;
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    internal static async Task RunAsync(Func<int, Task> lockSession, Func<int, Task> unlockSession, Func<TimeSpan, Task> delay)
    {
        for (int round = 1; round <= Rounds; round++)
        {
            if (round > 1)
                await delay(Interval);
            await lockSession(round);
            await delay(Interval);
            await unlockSession(round);
        }
    }
}
