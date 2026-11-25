using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace UnlockPC;

internal static class Program
{
    private const uint AsfwAny = uint.MaxValue;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is [ProviderRegistration.InstallArgument])
        {
            Environment.ExitCode = ProviderRegistration.Install();
            return;
        }
        if (args is ["--presence-self-test"])
        {
            Environment.ExitCode = WatchLockPolicySelfTest.Run();
            return;
        }
        if (args is ["--ui-self-test", "signin-options" or "advanced-options" or "watch-broadcast" or "watch-encryption"])
        {
            Environment.ExitCode = 1;
            RunApp(() => new App(args[1]));
            return;
        }
        bool lockCycleTest = args is ["--ui-self-test", "lock-cycle"];

        using var user = WindowsIdentity.GetCurrent();
        string prefix = $@"Local\UnlockPC.{user.User!.Value}";
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Activate");
        using var pair = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Pair");
        using var mutex = new Mutex(false, prefix + ".Instance");
        bool ownsInstance;
        try { ownsInstance = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsInstance = true; }
        if (!ownsInstance)
        {
            if (lockCycleTest)
            {
                Environment.ExitCode = 5; // The normal instance may already be monitoring this session.
                return;
            }
            // Transfer the launcher's foreground permission before waking the running instance.
            bool allowed = AllowSetForegroundWindow(AsfwAny);
            (args.Contains("--pair", StringComparer.Ordinal) ? pair : activate).Set();
            if (!allowed)
                BluetoothLog.Write("instance_foreground_grant_failed");
            return;
        }

        try
        {
            if (lockCycleTest)
            {
                Environment.ExitCode = 1;
                RunApp(() => new App("lock-cycle"));
                return;
            }
            using var exit = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + ".Exit." + Environment.ProcessId);
            RunApp(() => new App(activate, pair, exit));
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void RunApp(Func<App> createApp)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            createApp();
        });
    }
}
