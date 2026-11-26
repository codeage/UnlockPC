using Microsoft.UI.Xaml;
using WinUIKit;

namespace UnlockPC;

public partial class App : Application
{
    private readonly (EventWaitHandle Activate, EventWaitHandle Pair, EventWaitHandle Exit)? _signals;
    private readonly string? _selfTest;
    private MainWindow? _window;

    internal App(string? selfTest = null)
    {
        _selfTest = selfTest;
        InitializeComponent();
    }

    internal App(EventWaitHandle activate, EventWaitHandle pair, EventWaitHandle exit) : this() =>
        _signals = (activate, pair, exit);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UiHost.Language = () => "zh-CN";
        string? testStorePath = _selfTest == "advanced-options"
            ? Path.Combine(Path.GetTempPath(), $"unlockpc-rules-{Guid.NewGuid():N}.json") : null;
        if (testStorePath is not null)
            File.WriteAllText(testStorePath, """{"AutoUnlock":true,"AutoLock":true,"UnlockRssi":-57,"UnlockSeconds":3,"UnlockCount":2}""");
        _window = new MainWindow(showTrayIcon: _signals is not null,
            ruleStore: testStorePath is null ? null : new WatchRuleStore(testStorePath));
        _window.RestoreWindowPlacement();
        _window.Activate();
        if (_signals is not { } signals)
        {
            _window.DispatcherQueue.TryEnqueue(async () =>
            {
                if (testStorePath is not null)
                {
                    try { await _window.RunAdvancedOptionsSelfTestAsync(); }
                    finally { File.Delete(testStorePath); }
                }
                else if (_selfTest == "lock-cycle")
                    await _window.RunLockCycleSelfTestAsync();
                else if (_selfTest == "watch-broadcast")
                    await _window.RunWatchBroadcastSelfTestAsync();
                else if (_selfTest == "watch-encryption")
                    await _window.RunWatchEncryptionSelfTestAsync();
            });
            return;
        }

        var dispatcher = _window.DispatcherQueue;
        RegisteredWaitHandle Listen(WaitHandle signal, Microsoft.UI.Dispatching.DispatcherQueueHandler action) =>
            ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => dispatcher.TryEnqueue(action), null, Timeout.Infinite, false);
        var activateWait = Listen(signals.Activate, _window.OpenSettings);
        var pairWait = Listen(signals.Pair, async () => { _window.OpenSettings(); await _window.StartPairingAsync(); });
        var exitWait = Listen(signals.Exit, () =>
        {
            BluetoothLog.Write("instance_exit_requested");
            _window.Close();
        });
        _window.Closed += (_, _) =>
        {
            activateWait.Unregister(null);
            pairWait.Unregister(null);
            exitWait.Unregister(null);
        };
        _window.DispatcherQueue.TryEnqueue(async () =>
        {
            await _window.LoadPairedWatchesAsync();
            if (Environment.GetCommandLineArgs().Contains("--pair", StringComparer.Ordinal))
                await _window.StartPairingAsync();
        });
    }
}
