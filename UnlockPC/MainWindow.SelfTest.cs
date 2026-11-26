using System.Diagnostics;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private static async Task<bool> WaitForSelfTestAsync(Func<bool> done, int timeoutSeconds = 6)
    {
        var timer = Stopwatch.StartNew();
        while (!done())
        {
            if (timer.Elapsed >= TimeSpan.FromSeconds(timeoutSeconds))
                return false;
            await Task.Delay(50);
        }
        return true;
    }
}
