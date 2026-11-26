using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Windows.Devices.Bluetooth.Advertisement;

namespace UnlockPC;

internal sealed record WatchMonitorStatus(string DeviceId, long Generation, DateTimeOffset CapturedAt,
    ScanSnapshot Snapshot, string Message, AdvertisementRow[] Advertisements, LockActivityEvent[] Activity)
{
    internal static string PipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return $"UnlockPC.Monitor.{identity.User!.Value}.{process.SessionId}";
    }

    internal static async Task<WatchMonitorStatus?> ReadAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            using var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.In,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<WatchMonitorStatus>(pipe, cancellationToken: timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

internal sealed class WatchMonitor : IDisposable
{
    internal const string Argument = "--watch-monitor";
    private readonly nint _window;
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _timer;
    private readonly SavedUnlockStore _store = SavedUnlockStore.ForCurrentUser();
    private readonly CancellationTokenSource _stop = new();
    private WatchMonitorStatus? _published;
    private ServiceWatchScan? _scan;
    private ulong _boundAddress;
    private long _epochBeforeResume;
    private WatchWake? _watch;
    private DirectUnlock? _credential;
    private WatchRuleSettings? _rules;
    private DateTime _bindingWriteTime;
    private long _generation = Stopwatch.GetTimestamp();
    private string? _deviceId;
    private string? _credentialError;
    private long _retryAfter;
    private LockActivitySpeech? _speech;
    private readonly Queue<LockActivityEvent> _activity = new(80);
    private DateTime _rejectedBindingTime;
    private long _lastUpdate;
    private bool _updating;
    private bool _resuming;
    private string _message = "";

    private WatchMonitor(nint window, DispatcherQueue queue)
    {
        _window = window;
        _queue = queue;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(350);
        _timer.Tick += async (_, _) => await UpdateAsync();
        _timer.Start();
        _ = Task.Run(() => PublishAsync(_stop.Token));
    }

    internal static int Run()
    {
        using var mutex = new Mutex(true, WatchMonitorStatus.PipeName(), out bool first);
        if (!first)
            return 0;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        var controller = DispatcherQueueController.CreateOnCurrentThread();
        var queue = controller.DispatcherQueue;
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
        nint window = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (window == 0)
            return Marshal.GetLastPInvokeError();
        try
        {
            using var monitor = new WatchMonitor(window, queue);
            BluetoothLog.Write("background_monitor_started");
            while (GetMessageW(out var message, 0, 0, 0) > 0)
                DispatchMessageW(in message);
            return 0;
        }
        finally
        {
            DestroyWindow(window);
            GC.KeepAlive(controller);
        }
    }

    private async Task UpdateAsync()
    {
        if (_updating || Stopwatch.GetTimestamp() < _retryAfter)
            return;
        _updating = true;
        try
        {
            // Re-read the saved options so closing the settings UI does not stop monitoring.
            var rules = WatchRuleStore.ForCurrentUser().Read();
            DateTime bindingTime = File.GetLastWriteTimeUtc(AppDataPaths.WatchAuthorization);
            if (!_store.Exists)
            {
                Reset();
                return;
            }

            if (_scan is null || _rules != rules || _bindingWriteTime != bindingTime)
            {
                Reset();
                using var saved = _store.Read();
                if (saved is null)
                    return;
                var devices = await PairedWatch.ListAsync(_stop.Token);
                var device = devices.FirstOrDefault(item => string.Equals(item.Id, saved.DeviceId, StringComparison.OrdinalIgnoreCase));
                if (device is null)
                {
                    _retryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
                    return;
                }
                _deviceId = device.Id;

                _scan = new ServiceWatchScan(device);
                _rules = rules;
                _bindingWriteTime = bindingTime;
                ulong boundAddress = saved.BondAddress == 0 ? device.Address : saved.BondAddress;
                _boundAddress = boundAddress;

                if (rules.AutoUnlock && _rejectedBindingTime == bindingTime)
                    _credentialError = "Windows 未接受已保存的密码，请更新自动解锁密码。";
                else if (rules.AutoUnlock && saved.Password.Length != 0 && ProviderRegistration.IsInstalled())
                {
                    try { _credential = DirectUnlock.Restore(saved.Password); }
                    catch (Win32Exception ex) when (DirectUnlock.IsPasswordRejected(ex))
                    {
                        _rejectedBindingTime = bindingTime;
                        _credentialError = "Windows 未接受已保存的密码，请更新自动解锁密码。";
                        BluetoothLog.Write("direct_unlock_password_rejected", new { ex.NativeErrorCode });
                    }
                    catch (Exception ex)
                    {
                        _credentialError = "无法恢复自动解锁凭据。";
                        BluetoothLog.Write("direct_unlock_credential_restore_failed", new { ex.HResult });
                    }
                }
                _watch = new WatchWake(_window, _credential, rules,
                    token => WatchConnectionVerification.VerifyAsync(device,
                        address => ResolveForVerificationAsync(boundAddress, address, token), token, Record),
                    Record, onSubmittedUnlockFinished: (since, timedOut) =>
                    {
                        if (_credential is { } credential)
                            _ = CheckPasswordAsync(credential, since, timedOut);
                    }, onPowerResume: Resume, onPowerSuspend: ClearPublishedStatus);
                _generation++;
                BluetoothLog.Write("background_watch_bound");
            }

            if (_scan is not { } active || _watch is not { } wake || wake.IsSuspended)
                return;

            long now = Stopwatch.GetTimestamp();
            if (!wake.CanReadSamples(now))
                return;

            long generation = _generation;
            long priorEpoch = active.Epoch;
            var telemetry = await active.ReadAsync(_boundAddress, _stop.Token);
            if (generation != _generation || wake.IsSuspended)
                return;
            if (telemetry is not { } snapshot)
            {
                if (priorEpoch != 0)
                {
                    active.Clear();
                    wake.ResetDetection();
                    _generation++;
                    _lastUpdate = 0;
                }
                ClearPublishedStatus();
                return;
            }

            if (_resuming)
            {
                if (active.Epoch == _epochBeforeResume)
                {
                    active.Clear();
                    return;
                }
                _resuming = false;
            }
            if (priorEpoch != 0 && priorEpoch != active.Epoch)
            {
                wake.ResetDetection();
                _generation++;
                _lastUpdate = 0;
            }

            now = Stopwatch.GetTimestamp();
            bool interrupted = _lastUpdate != 0 && Stopwatch.GetElapsedTime(_lastUpdate, now) > TimeSpan.FromSeconds(3);
            _lastUpdate = now;
            _message = wake.Update(now, snapshot.LastNear, interrupted, snapshot.UnlockSamples, snapshot.UnlockSamplesDropped);
            if (_deviceId is { } id)
                Volatile.Write(ref _published, new WatchMonitorStatus(id, _generation, DateTimeOffset.UtcNow,
                    snapshot with { UnlockSamples = [], UnlockSamplesDropped = false }, _credentialError ?? _message,
                    active.Advertisements.ReadNew(matchesOnly: false, afterSequence: Math.Max(0, snapshot.Total - 128)),
                    _activity.ToArray()));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            BluetoothLog.Write("background_watch_failed", new { ex.HResult, ex.Message });
            Reset();
            _retryAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        }
        finally { _updating = false; }
    }

    private static async Task<bool> ResolveForVerificationAsync(ulong boundAddress, ulong address, CancellationToken token)
    {
        try
        {
            using var resolver = await BondedIrk.OpenAsync(boundAddress, token).ConfigureAwait(false);
            return await resolver.ResolvesAsync(address).WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }

    private void Resume()
    {
        _lastUpdate = 0;
        _generation++;
        _resuming = true;
        _epochBeforeResume = _scan?.Epoch ?? 0;
        ClearPublishedStatus();
    }

    private void ClearPublishedStatus() => Volatile.Write(ref _published, null);

    private async Task CheckPasswordAsync(DirectUnlock credential, DateTime since, bool timedOut)
    {
        try
        {
            await Task.Run(() =>
            {
                if (timedOut || DirectUnlock.WindowsReportedAuthenticationFailure(since))
                    credential.VerifyPassword();
            });
        }
        catch (Win32Exception ex) when (DirectUnlock.IsPasswordRejected(ex))
        {
            if (ReferenceEquals(credential, _credential))
            {
                _rejectedBindingTime = _bindingWriteTime;
                BluetoothLog.Write("direct_unlock_password_rejected", new { ex.NativeErrorCode });
                Reset();
            }
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("direct_unlock_password_check_failed", new { ex.HResult });
        }
    }

    private void Record(LockActivityEvent activity)
    {
        if (!_queue.HasThreadAccess)
        {
            _queue.TryEnqueue(() => Record(activity));
            return;
        }
        if (_stop.IsCancellationRequested)
            return;

        BluetoothLog.Write("background_watch_activity", new { phase = activity.Phase.ToString(), activity.Message });
        if (_activity.Count == 80)
            _activity.Dequeue();
        _activity.Enqueue(activity);
        if (_rules?.SpeakLockActivity == true)
            (_speech ??= new LockActivitySpeech()).SpeakLatest(activity.Speech ?? activity.Message);
    }

    private async Task PublishAsync(CancellationToken token)
    {
        string name = WatchMonitorStatus.PipeName();
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
                await JsonSerializer.SerializeAsync(pipe, Volatile.Read(ref _published),
                    cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(350, token).ConfigureAwait(false);
            }
        }
    }

    private void Reset()
    {
        Volatile.Write(ref _published, null);
        if (_watch is { } watcher)
            watcher.Dispose();
        else
            _credential?.Dispose();
        _watch = null;
        _deviceId = _credentialError = null;
        _speech?.Dispose();
        _speech = null;
        _credential = null;
        _scan?.Dispose();
        _scan = null;
        _lastUpdate = 0;
        _resuming = false;
    }

    public void Dispose()
    {
        _timer.Stop();
        _stop.Cancel();
        Reset();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetMessageW(out Message message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DispatchMessageW(in Message message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
