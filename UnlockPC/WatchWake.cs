using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace UnlockPC;

internal sealed class WatchWake : IDisposable
{
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmSessionChange = 0x02B1;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmInput = 0x00FF;
    private const uint WmInputDeviceChange = 0x00FE;
    private const nuint PowerSuspend = 0x0004;
    private const nuint PowerResume = 0x0012;
    private const nuint SubclassId = 2;
    private const int WtsSessionInfoEx = 25;
    private static readonly Guid DisplayStatus = new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    private static readonly TimeSpan UnlockObservation = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromSeconds(10);
    private readonly nint _window;
    private readonly uint _session;
    private readonly SubclassProc _proc;
    private readonly WatchApproach _approach = new(2);
    private readonly WatchLockPolicy _lockPolicy = new();
    private readonly WatchResumeState _resume = new();
    private readonly Action? _onPowerResume;
    private readonly Action? _onPowerSuspend;
    private readonly DirectUnlock? _directUnlock;
    private readonly Func<CancellationToken, Task<WatchConnectionVerificationResult>>? _verifyConnection;
    private readonly Action<LockActivityEvent>? _recordActivity;
    private readonly Action? _onVerificationCompleted;
    private readonly Action<DateTime, bool>? _onSubmittedUnlockFinished;
    private readonly bool _directMode;
    private readonly SessionInputActivity _inputActivity = new();
    private DispatcherQueueTimer? _controllerTimer;
    private nint _displayRegistration;
    private nint _suspendResumeRegistration;
    private bool _sessionRegistered;
    private bool _subclassInstalled;
    private bool? _locked;
    private long? _lockedAt;
    private bool _immediateSamples;
    private bool _displayOff;
    private string? _wakeResult;
    private long _lastWake;
    private bool _directAttempted;
    private CancellationTokenSource? _verificationCancellation;
    private (Task<WatchConnectionVerificationResult> Task, WatchSignalDecision Signal)? _pendingVerification;
    private long _lockRequestedAt;
    private bool _automaticLockPending;
    private bool _activityInProgress;
    private string? _lockInputStatus;
    private string? _lastReportedSignalBand;
    private WatchUnlockBlock? _lastReportedUnlockBlock;
    private WatchRuleSettings _rules = new();

    internal long LastLockNotification { get; private set; }
    internal long LastUnlockNotification { get; private set; }
    internal bool IsSuspended => _resume.IsSuspended;
    internal bool IsSessionUnlocked => _locked == false && !_resume.IsPending;
    internal bool RequestsImmediateSamples => Volatile.Read(ref _immediateSamples);
    internal TimeSpan? GetInputIdleTime(long now) => _inputActivity.GetIdleTime(now);

    public WatchWake(nint window, DirectUnlock? directUnlock = null, WatchRuleSettings? rules = null,
        Func<CancellationToken, Task<WatchConnectionVerificationResult>>? verifyConnection = null, Action<LockActivityEvent>? recordActivity = null,
        Action? onVerificationCompleted = null, Action<DateTime, bool>? onSubmittedUnlockFinished = null,
        Action? onPowerResume = null, Action? onPowerSuspend = null)
    {
        _window = window;
        _directUnlock = directUnlock;
        _verifyConnection = verifyConnection;
        _recordActivity = recordActivity;
        _onVerificationCompleted = onVerificationCompleted;
        _onPowerResume = onPowerResume;
        _onPowerSuspend = onPowerSuspend;
        _onSubmittedUnlockFinished = onSubmittedUnlockFinished;
        _directMode = directUnlock is not null || rules is not null;
        _lockPolicy.Reset(Stopwatch.GetTimestamp());
        _proc = WindowProc;
        using var process = Process.GetCurrentProcess();
        _session = (uint)process.SessionId;
        try
        {
            if (!SetWindowSubclass(window, _proc, SubclassId, 0))
                throw new InvalidOperationException("无法订阅窗口消息。");
            _subclassInstalled = true;

            if (!WTSRegisterSessionNotification(window, 0)) // NOTIFY_FOR_THIS_SESSION
                throw new Win32Exception();
            _sessionRegistered = true;
            _locked = ReadSessionLockState();
            if (_locked == true)
                _lockedAt = Stopwatch.GetTimestamp();
            BluetoothLog.Write("watch_wake_session_state_initialized", new { locked = _locked });

            _displayRegistration = RegisterPowerSettingNotification(window, in DisplayStatus, 0);
            if (_displayRegistration == 0)
                throw new Win32Exception();

            if (_directMode)
            {
                _suspendResumeRegistration = RegisterSuspendResumeNotification(window, 0); // DEVICE_NOTIFY_WINDOW_HANDLE
                if (_suspendResumeRegistration == 0)
                    BluetoothLog.Write("direct_power_registration_failed", new { error = Marshal.GetLastPInvokeError() });
                _controllerTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
                _controllerTimer.Interval = TimeSpan.FromMilliseconds(50);
                _controllerTimer.Tick += (_, _) =>
                {
                    if (_locked == false && WTSGetActiveConsoleSessionId() == _session)
                        _inputActivity.SampleControllers();
                };
                SetRules(rules ?? new WatchRuleSettings());
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SetRules(WatchRuleSettings rules)
    {
        FinishActivity("规则已更新，本轮结束。");
        CancelAuthentication();
        _rules = rules;
        long now = Stopwatch.GetTimestamp();
        _lockPolicy.SetRules(rules, now);
        _approach.Reset(now);
        _lockRequestedAt = 0;
        _automaticLockPending = false;
        _lockInputStatus = null;
        _lastReportedUnlockBlock = null;
        _resume.ClearSession();
        _wakeResult = null;
        PublishSampleMode();
        if (rules.AutoLock)
        {
            _inputActivity.Start(_window);
            _controllerTimer?.Start();
        }
        else
        {
            _controllerTimer?.Stop();
            _inputActivity.Dispose();
        }
    }

    internal void ResetDetection()
    {
        FinishActivity("检测数据已重新初始化，本轮结束。");
        CancelAuthentication();
        long now = Stopwatch.GetTimestamp();
        _lockPolicy.Reset(now);
        _approach.Reset(now);
        _automaticLockPending = false;
        _lockRequestedAt = 0;
        _directAttempted = false;
        _lastWake = 0;
        _wakeResult = null;
        PublishSampleMode();
    }

    public string Update(long now, long nearSample, bool interrupted, ReadOnlySpan<WatchSignalSample> unlockSamples = default,
        bool samplesDropped = false)
    {
        if (WTSGetActiveConsoleSessionId() != _session)
        {
            FinishActivity("本机控制台已切换，本轮结束。");
            CancelAuthentication();
            _approach.Reset(now);
            _lockPolicy.Reset(now);
            _automaticLockPending = false;
            _resume.ClearSession();
            PublishSampleMode();
            return "等待本机控制台恢复。";
        }

        if (_directMode && !CanReadSamples(now))
            return IsSuspended ? "等待 Windows 恢复。" : "等待 Windows 确认恢复后的锁屏状态。";

        if (_locked is null)
        {
            _locked = ReadSessionLockState();
            if (_locked is null)
                return "等待 Windows 确认会话锁定状态。";
            _lockedAt = _locked == true ? now : null;
            _approach.Reset(now);
            _lockPolicy.Reset(now);
            PublishSampleMode();
            BluetoothLog.Write("watch_wake_session_state_initialized", new { locked = _locked });
        }

        if (_directMode)
        {
            bool firstRead = _resume.TakeFirstRead();
            if (!_rules.WakeWithWindowsHello || _locked != true)
                return UpdateDirectUnlock(now, samplesDropped || (interrupted && !firstRead), unlockSamples);
        }

        if (_locked == false || interrupted)
        {
            _approach.Reset(now);
            if (_locked == false)
                return _wakeResult ?? "等待本机控制台锁屏。";
        }

        if (_lastWake != 0 && Stopwatch.GetElapsedTime(_lastWake, now) > UnlockObservation)
        {
            FinishActivity("未收到 Windows 解锁通知。");
            _lastWake = 0;
            _wakeResult = null;
        }

        if (_approach.Update(now, nearSample) && _displayOff)
        {
            // Reset the display idle timer once. Windows owns the lock screen and authentication.
            RecordActivity(LockActivityPhase.Start, "手表靠近，开始亮屏解锁流程。");
            bool requested = SetThreadExecutionState(0x00000002) != 0; // ES_DISPLAY_REQUIRED
            _lastWake = requested ? now : 0;
            _wakeResult = requested ? "已请求亮屏，等待 Windows 登录。" : "Windows 未接受亮屏请求。";
            BluetoothLog.Write("watch_wake_requested", new { requested });
            if (requested)
                RecordActivity(LockActivityPhase.Step, "已请求 Windows 亮屏。");
            else
                FinishActivity("Windows 未接受亮屏请求。");
        }

        return _wakeResult ?? (_approach.WaitingForReturn
            ? "等待手表重新靠近。"
            : "已锁屏。连续 15 秒未收到近距离信号后，才会等待手表返回。");
    }

    internal bool CanReadSamples(long now)
    {
        if (IsSuspended)
            return false;
        if (!_resume.IsPending)
            return true;
        if (WTSGetActiveConsoleSessionId() != _session)
            return false;

        bool? confirmedLocked = ReadSessionLockState();
        if (!_resume.TryResolve(now, confirmedLocked, LastLockNotification, out long? sampleFloor))
            return false;

        _locked = confirmedLocked;
        _lockedAt = confirmedLocked == true ? _lockedAt ?? now : null;
        if (sampleFloor is long timestamp)
            _lockPolicy.Reset(timestamp, WatchReturnReason.SystemResume);
        PublishSampleMode();
        BluetoothLog.Write("direct_resume_session_state", new { locked = confirmedLocked });
        return true;
    }

    private string UpdateDirectUnlock(long now, bool interrupted, ReadOnlySpan<WatchSignalSample> samples)
    {
        if (interrupted)
        {
            FinishActivity("蓝牙检测中断，本轮结束。");
            _lockInputStatus = null;
            CancelAuthentication();
            _lockPolicy.ResetSamples(now);
            _automaticLockPending = false;
            samples = default;
        }

        if (_rules.AutoLock || _rules.AutoUnlock)
            ReportSignalChange(now, samples);

        if (_locked == false)
        {
            if (!_rules.AutoLock)
                return "自动锁定已关闭。";
            var departure = _lockPolicy.Observe(now, samples, locked: false);
            if (_lockRequestedAt != 0 && Stopwatch.GetElapsedTime(_lockRequestedAt, now) <= LockRetryInterval)
                return _wakeResult ?? "等待 Windows 锁屏。";

            if (_automaticLockPending)
                FinishActivity("未收到 Windows 锁屏通知。");
            _automaticLockPending = false;
            if (departure is not { } evidence)
            {
                FinishActivity("弱信号条件已解除，本轮未锁定。");
                _lockInputStatus = null;
                return "正在检测手表。持续弱信号时自动锁屏。";
            }

            if (!_activityInProgress)
            {
                RecordActivity(LockActivityPhase.Start,
                    $"自动锁定：弱信号达标（{evidence.Rssi} dBm，{evidence.SampleCount} 条）。", "开始自动锁定。");
                _lockInputStatus = null;
            }

            _inputActivity.SampleControllers();
            var idle = _inputActivity.GetIdleTime(Stopwatch.GetTimestamp());
            if (!WatchLockPolicy.InputAllowsLock(idle))
            {
                string status = idle is null ? "无法确认本机输入状态，暂停自动锁定。" : "检测到近期本机输入，暂不锁屏。";
                if (_lockInputStatus != status)
                    RecordActivity(LockActivityPhase.Step, status);
                return _lockInputStatus = status;
            }
            RecordActivity(LockActivityPhase.Step, "本机无输入时长达标。");
            _lockInputStatus = null;

            _lockRequestedAt = now;
            _automaticLockPending = true;
            bool requested = LockWorkStation();
            if (!requested)
                _automaticLockPending = false;
            int error = requested ? 0 : Marshal.GetLastPInvokeError();
            _wakeResult = requested ? "已请求 Windows 锁屏。" : "Windows 未接受锁屏请求。";
            BluetoothLog.Write("direct_lock_requested", new
            {
                reason = "WeakSignal", requested, error, rssi = evidence.Rssi, samples = evidence.SampleCount,
                _rules.WeakRssi, _rules.VeryWeakRssi,
                durationMs = evidence.Duration.TotalMilliseconds
            });
            if (requested)
                RecordActivity(LockActivityPhase.Step, _wakeResult);
            else
                FinishActivity(_wakeResult);
            return _wakeResult;
        }

        if (!_rules.AutoUnlock || _directUnlock is not { } directUnlock)
            return "已锁屏。自动解锁已关闭。";
        if (_pendingVerification is { } verification)
        {
            if (!verification.Task.IsCompleted)
                return "已锁屏。正在验证手表的蓝牙连接。";

            CancelAuthentication();
            if (!verification.Task.IsCompletedSuccessfully || verification.Task.Result == WatchConnectionVerificationResult.Unconfirmed)
            {
                _wakeResult = "手表蓝牙连接验证失败，请手动解锁。";
                FinishActivity(_wakeResult);
                return _wakeResult;
            }
            var connection = verification.Task.Result;
            RecordActivity(LockActivityPhase.Step, connection == WatchConnectionVerificationResult.AlreadyConnected
                ? "Windows 确认配对手表已连接。" : "配对手表的新链路加密验证通过。");
            return OfferUnlock(now, directUnlock, verification.Signal, connection);
        }
        if (_directAttempted)
        {
            if (_lastWake != 0 && Stopwatch.GetElapsedTime(_lastWake, now) > UnlockObservation)
            {
                ReportSubmittedUnlockFinished(now, timedOut: true);
                _lastWake = 0;
                _wakeResult = "未收到 Windows 解锁通知。";
                FinishActivity(_wakeResult);
            }
            return _wakeResult ?? "本次锁屏已尝试自动解锁。";
        }
        var previousReason = _lockPolicy.ReturnReason;
        var approach = _lockPolicy.Observe(now, samples, locked: true, _lockedAt);
        if (previousReason is null && _lockPolicy.ReturnReason == WatchReturnReason.ObservedDeparture)
        {
            BluetoothLog.Write("direct_locked_departure_confirmed");
            RecordActivity(LockActivityPhase.Note, "已检测到手表离开，等待手表返回。");
        }
        if (approach is not { } matched)
        {
            if (_lockPolicy.LastStrongSignalBlock is { } block && block != _lastReportedUnlockBlock)
            {
                _lastReportedUnlockBlock = block;
                RecordActivity(LockActivityPhase.Note, block switch
                {
                    WatchUnlockBlock.WaitingForDeparture => "尚未检测到手表离开，暂不解锁。",
                    WatchUnlockBlock.WaitingForDelay => "锁定后的等待时间未到，暂不解锁。",
                    WatchUnlockBlock.WatchAppearsLocked => "手表广播提示仍处于锁定状态，暂不解锁。",
                    WatchUnlockBlock.WatchUnlockUnconfirmed => "无法确认手表已解锁，暂不解锁。",
                    WatchUnlockBlock.WaitingForBroadcastCount => "符合条件的广播数量还不够，暂不解锁。",
                    _ => "暂未满足解锁条件。"
                });
            }
            TimeSpan remaining = _lockPolicy.ReturnDelayRemaining(_lockedAt, now);
            if (remaining > TimeSpan.Zero)
                return $"已锁屏。距可开始自动解锁还有 {Math.Ceiling(remaining.TotalSeconds):F0} 秒。";
            return _lockPolicy.ReturnReason is null ? "已锁屏。等待手表离开。"
                : _rules.RequireWatchUnlocked ? "已锁屏。等待带解锁线索的强信号。"
                : "已锁屏。等待手表的强信号。";
        }

        _directAttempted = true;
        string condition = _rules.RequireWatchUnlocked ? "强信号及手表解锁线索达标" : "强信号达标";
        RecordActivity(LockActivityPhase.Start, $"自动解锁：{condition}（{matched.Rssi} dBm，{matched.SampleCount} 条）。",
            "开始自动解锁。");
        if (_rules.RequireEncryptedConnection)
        {
            if (_verifyConnection is null)
            {
                _wakeResult = "无法验证手表的蓝牙连接，请手动解锁。";
                FinishActivity(_wakeResult);
                return _wakeResult;
            }

            RecordActivity(LockActivityPhase.Step, "开始验证手表的蓝牙连接。");
            _verificationCancellation = new CancellationTokenSource();
            var verificationTask = _verifyConnection(_verificationCancellation.Token);
            _pendingVerification = (verificationTask, matched);
            if (_onVerificationCompleted is { } notify)
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                _ = verificationTask.ContinueWith(_ => dispatcher.TryEnqueue(() =>
                {
                    if (_pendingVerification?.Task == verificationTask)
                        notify();
                }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return "已锁屏。正在验证手表的蓝牙连接。";
        }
        return OfferUnlock(now, directUnlock, matched);
    }

    private string OfferUnlock(long now, DirectUnlock directUnlock, WatchSignalDecision matched,
        WatchConnectionVerificationResult? connection = null)
    {
        try
        {
            directUnlock.Offer();
            _lastWake = now;
            _wakeResult = "已发起一次 Windows 解锁认证。";
            RecordActivity(LockActivityPhase.Step, _wakeResult);
            BluetoothLog.Write("direct_unlock_requested", new
            {
                reason = _lockPolicy.ReturnReason.ToString(), rssi = matched.Rssi, samples = matched.SampleCount,
                _rules.ReturnRssi, _rules.ReturnWindowSeconds, _rules.ReturnPacketCount, _rules.RequireWatchUnlocked,
                connectionVerification = connection?.ToString(),
                encryptedConnectionVerified = connection == WatchConnectionVerificationResult.NewlyEncrypted, _rules.UnlockDelaySeconds
            });
            if (_displayOff)
                SetThreadExecutionState(0x00000002); // ES_DISPLAY_REQUIRED
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _wakeResult = "无法向 Windows 登录组件发起认证。";
            FinishActivity(_wakeResult);
            BluetoothLog.Write("direct_unlock_offer_failed", new { ex.HResult });
        }
        return _wakeResult;
    }

    private void CancelAuthentication()
    {
        _pendingVerification = null;
        _verificationCancellation?.Cancel();
        _verificationCancellation?.Dispose();
        _verificationCancellation = null;
        _directUnlock?.Cancel();
    }

    private void ReportSubmittedUnlockFinished(long now, bool timedOut)
    {
        if (_directAttempted && _lastWake != 0 && _directUnlock is { } broker && broker.LastCredentialSent >= _lastWake)
            _onSubmittedUnlockFinished?.Invoke(DateTime.UtcNow - Stopwatch.GetElapsedTime(_lastWake, now), timedOut);
    }

    private void ReportSignalChange(long now, ReadOnlySpan<WatchSignalSample> samples)
    {
        if (samples.IsEmpty)
            return;
        var sample = samples[^1];
        if (sample.Timestamp > now || Stopwatch.GetElapsedTime(sample.Timestamp, now) > TimeSpan.FromSeconds(2))
            return;

        string? band = (_locked == true && sample.Rssi >= _rules.ReturnRssi) ? "强信号"
            : sample.Rssi <= _rules.VeryWeakRssi ? "极弱信号"
            : sample.Rssi <= _rules.WeakRssi ? "弱信号"
            : sample.Rssi >= _rules.ReturnRssi ? "强信号" : null;
        if (band is null || band == _lastReportedSignalBand)
            return;
        _lastReportedSignalBand = band;
        if (band != "强信号")
            _lastReportedUnlockBlock = null;
        RecordActivity(LockActivityPhase.Note, $"检测到{band}（{sample.Rssi} dBm）。", $"检测到{band}。");
    }

    private void RecordActivity(LockActivityPhase phase, string message, string? speech = null)
    {
        if (phase is LockActivityPhase.Start or LockActivityPhase.Finish)
            _activityInProgress = phase == LockActivityPhase.Start;
        _recordActivity?.Invoke(LockActivityEvent.Now(phase, message, speech));
    }

    private void FinishActivity(string message)
    {
        if (_activityInProgress)
            RecordActivity(LockActivityPhase.Finish, message);
    }

    internal bool IsRecoveringScan(long now) => _resume.IsRecoveringScan(now);

    internal bool CanRetryScan(long now) => _resume.CanRetryScan(now);

    private void PublishSampleMode() => Volatile.Write(ref _immediateSamples,
        _directMode && _rules.AutoUnlock && _directUnlock is not null && !IsSuspended && !IsSessionUnlocked);

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == WmSessionChange && unchecked((uint)lParam) == _session)
        {
            long now = Stopwatch.GetTimestamp();
            if (wParam == 7 && _locked != true) // WTS_SESSION_LOCK
            {
                LastLockNotification = now;
                _locked = true;
                _lockedAt = now;
                _wakeResult = null;
                _lastWake = 0;
                _directAttempted = false;
                _lastReportedUnlockBlock = null;
                CancelAuthentication();
                _approach.Reset(now);
                bool automatic = _automaticLockPending &&
                    Stopwatch.GetElapsedTime(_lockRequestedAt, now) <= LockRetryInterval;
                _lockPolicy.Reset(now, automatic ? WatchReturnReason.AutomaticLock : null);
                _lockRequestedAt = 0;
                _automaticLockPending = false;
                BluetoothLog.Write("watch_wake_session_locked");
                if (_activityInProgress)
                    FinishActivity("Windows 确认会话已锁定。");
                else
                    RecordActivity(LockActivityPhase.Note, "Windows 会话已锁定。");
            }
            else if (wParam == 8) // WTS_SESSION_UNLOCK
            {
                LastUnlockNotification = now;
                _locked = false;
                _lockedAt = null;
                _lockPolicy.Reset(now);
                _lockRequestedAt = 0;
                _automaticLockPending = false;
                _resume.ClearSession();
                CancelAuthentication();
                ReportSubmittedUnlockFinished(now, timedOut: false);
                if (_activityInProgress)
                    FinishActivity("Windows 确认会话已解锁。");
                else
                    RecordActivity(LockActivityPhase.Note, "Windows 会话已解锁。");
                if (_directAttempted && _lastWake != 0 && Stopwatch.GetElapsedTime(_lastWake) <= UnlockObservation)
                {
                    _wakeResult = "已收到 Windows 会话解锁通知。";
                    BluetoothLog.Write("direct_unlock_session_unlocked");
                }
                else if (_lastWake != 0 && Stopwatch.GetElapsedTime(_lastWake) <= UnlockObservation)
                {
                    _wakeResult = "请求亮屏后，已收到 Windows 会话解锁通知。";
                    BluetoothLog.Write("watch_wake_session_unlocked");
                }
                else
                {
                    _wakeResult = null;
                }
                _lastWake = 0;
            }
            else if (wParam is 2 or 3 or 4 or 6) // Console disconnect, remote connect/disconnect, or logoff.
            {
                FinishActivity("Windows 会话已切换，本轮结束。");
                _locked = null;
                _lockedAt = null;
                _lockPolicy.Reset(now);
                _lockRequestedAt = 0;
                _automaticLockPending = false;
                _resume.ClearSession();
                CancelAuthentication();
                _lastWake = 0;
                _wakeResult = null;
                _directAttempted = false;
            }
            PublishSampleMode();
        }
        else if (message == WmInput && _directMode && _rules.AutoLock && _locked == false && !IsSuspended)
        {
            _inputActivity.ReadHid(lParam);
        }
        else if (message == WmInputDeviceChange && _directMode && _rules.AutoLock)
        {
            _inputActivity.OnHidDeviceChange(lParam, arrived: wParam == 1);
        }
        else if (_directMode && message == WmPowerBroadcast && wParam == PowerSuspend)
        {
            FinishActivity("Windows 进入睡眠，本轮结束。");
            _resume.Suspend();
            _onPowerSuspend?.Invoke();
            _automaticLockPending = false;
            _lockRequestedAt = 0;
            CancelAuthentication();
            _lockPolicy.Reset(Stopwatch.GetTimestamp());
            PublishSampleMode();
        }
        else if (_directMode && message == WmPowerBroadcast && wParam == PowerResume)
        {
            long now = Stopwatch.GetTimestamp();
            if (_resume.Resume(now))
            {
                FinishActivity("Windows 从睡眠恢复，本轮结束。");
                _automaticLockPending = false;
                _lockRequestedAt = 0;
                _directAttempted = false;
                _wakeResult = null;
                _lastWake = 0;
                CancelAuthentication();
                _lockPolicy.Reset(now);
                _approach.Reset(now);
                _onPowerResume?.Invoke();
                PublishSampleMode();
                BluetoothLog.Write("direct_system_resumed");
            }
        }
        else if (message == WmPowerBroadcast && wParam == 0x8013 && lParam != 0) // PBT_POWERSETTINGCHANGE
        {
            // POWERBROADCAST_SETTING stores a GUID, a DWORD length, then the data bytes.
            if (Marshal.PtrToStructure<Guid>(lParam) == DisplayStatus && Marshal.ReadInt32(lParam, 16) == sizeof(int))
            {
                bool wasOff = _displayOff;
                _displayOff = Marshal.ReadInt32(lParam, 20) == 0;
                if (wasOff && !_displayOff && _lastWake != 0)
                    BluetoothLog.Write("watch_wake_display_on");
            }
        }
        else if (message == WmNcDestroy)
        {
            Dispose();
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private bool? ReadSessionLockState()
    {
        if (!WTSQuerySessionInformationW(0, _session, WtsSessionInfoEx, out nint buffer, out uint bytesReturned))
            return null;

        try
        {
            if (buffer == 0 || bytesReturned < Marshal.SizeOf<SessionInfoPrefix>())
                return null;
            var information = Marshal.PtrToStructure<SessionInfoPrefix>(buffer);
            if (information.Level != 1 || information.SessionId != _session)
                return null;
            return information.SessionFlags switch
            {
                0 => true,  // WTS_SESSIONSTATE_LOCK
                1 => false, // WTS_SESSIONSTATE_UNLOCK
                _ => null
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    public void Dispose()
    {
        FinishActivity("检测已停止，本轮结束。");
        CancelAuthentication();
        _controllerTimer?.Stop();
        _inputActivity.Dispose();
        _directUnlock?.Dispose();
        if (_suspendResumeRegistration != 0)
        {
            UnregisterSuspendResumeNotification(_suspendResumeRegistration);
            _suspendResumeRegistration = 0;
        }
        if (_displayRegistration != 0)
        {
            UnregisterPowerSettingNotification(_displayRegistration);
            _displayRegistration = 0;
        }
        if (_sessionRegistered)
        {
            WTSUnRegisterSessionNotification(_window);
            _sessionRegistered = false;
        }
        if (_subclassInstalled)
        {
            RemoveWindowSubclass(_window, _proc, SubclassId);
            _subclassInstalled = false;
        }
    }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    // The WTSINFOEXW data union is 8-byte aligned. Only the level 1 header is needed.
    [StructLayout(LayoutKind.Explicit)]
    private struct SessionInfoPrefix
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(8)] public uint SessionId;
        [FieldOffset(16)] public int SessionFlags;
    }

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(nint window, uint flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(nint window);

    [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint sessionId, int informationClass,
        out nint buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint buffer);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterPowerSettingNotification(nint recipient, in Guid setting, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterSuspendResumeNotification(nint recipient, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterSuspendResumeNotification(nint registration);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(nint registration);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool LockWorkStation();
}
