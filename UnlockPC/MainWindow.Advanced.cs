using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private readonly WatchRuleStore _watchRuleStore;
    private WatchRuleSettings _watchRules = new();
    private bool _updatingWatchRuleControls = true;
    private DeviceWatcher? _watchConnectionStatusWatcher;

    private void UpdateWatchConnectionStatus()
    {
        StopWatchConnectionStatus();
        if (_isClosing)
            return;

        if (PairedWatchBox.SelectedItem is not PairedWatch watch)
        {
            WatchConnectionLiveStatus.Text = "当前连接（Windows）：未选择手表";
            return;
        }

        string label = $"当前连接（Windows） · {watch.Name}：";
        WatchConnectionLiveStatus.Text = label + "正在读取";
        try
        {
            var watcher = DeviceInformation.CreateWatcher(PairedWatch.Selector,
                [PairedWatch.ConnectionProperty], DeviceInformationKind.AssociationEndpoint);
            _watchConnectionStatusWatcher = watcher;
            int address = BluetoothLog.AddressLabel(watch.Address);
            bool? previousConnection = null;

            void UpdateStatus(string id, IReadOnlyDictionary<string, object> properties)
            {
                if (!IsSameId(id, watch.Id) || PairedWatch.ReadConnection(properties) is not { } connected)
                    return;
                var observedAt = DateTimeOffset.Now;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_watchConnectionStatusWatcher, watcher) || previousConnection == connected)
                        return;
                    BluetoothLog.Write(previousConnection is null ? "watch_connection_state_initialized" : "watch_connection_changed",
                        new { address, previousConnected = previousConnection, connected, observedAt });
                    previousConnection = connected;
                    WatchConnectionLiveStatus.Text = label + (connected ? "已连接" : "未连接");
                });
            }

            watcher.Added += (_, device) => UpdateStatus(device.Id, device.Properties);
            watcher.Updated += (_, update) => UpdateStatus(update.Id, update.Properties);
            watcher.Removed += (_, update) =>
            {
                if (!IsSameId(update.Id, watch.Id))
                    return;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_watchConnectionStatusWatcher, watcher))
                        return;
                    BluetoothLog.Write("watch_connection_device_removed", new { address, previousConnected = previousConnection });
                    previousConnection = null;
                    WatchConnectionLiveStatus.Text = label + "设备不可用";
                });
            };
            watcher.EnumerationCompleted += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_watchConnectionStatusWatcher, watcher) &&
                    WatchConnectionLiveStatus.Text == label + "正在读取")
                    WatchConnectionLiveStatus.Text = label + "状态未知";
            });
            watcher.Stopped += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_watchConnectionStatusWatcher, watcher))
                    return;
                BluetoothLog.Write("watch_connection_monitor_stopped", new { address, status = watcher.Status.ToString() });
                WatchConnectionLiveStatus.Text = label + "状态监测已停止";
            });
            watcher.Start();
        }
        catch (Exception ex)
        {
            StopWatchConnectionStatus();
            WatchConnectionLiveStatus.Text = label + "无法读取";
            BluetoothLog.Write("watch_connection_status_unavailable", new { ex.HResult });
        }
    }

    private void StopWatchConnectionStatus()
    {
        var watcher = _watchConnectionStatusWatcher;
        _watchConnectionStatusWatcher = null;
        if (watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            watcher.Stop();
    }

    private async void TestWatchConnection_Click(object sender, RoutedEventArgs args)
    {
        if (PairedWatchBox.SelectedItem is not PairedWatch watch)
        {
            ShowWatchConnectionTestStatus("未选择手表", "请先在「在场检测」页选择已配对的手表。", InfoBarSeverity.Warning);
            return;
        }

        TestWatchConnectionButton.IsEnabled = false;
        ShowWatchConnectionTestStatus("正在测试", $"{watch.Name} · 正在请求并观察蓝牙连接。", InfoBarSeverity.Informational);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        long timerStart = Stopwatch.GetTimestamp();
        var timer = Stopwatch.StartNew();
        bool alreadyConnected = false;
        long connectionRequestedAt = 0;
        var firstConnection = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        BluetoothConnectionStatus? finalConnectionStatus = null;
        GattSessionStatus? sessionStatus = null;
        long? requestLatencyMs = null;
        int? errorCode = null;
        string outcome = "device_unavailable";
        string message = "Windows 无法访问该配对设备。";
        var severity = InfoBarSeverity.Warning;
        BluetoothLog.Write("watch_connection_test_started", new { method = "maintain_connection", deviceAddress = BluetoothLog.AddressLabel(watch.Address) });

        try
        {
            using var device = await BluetoothLEDevice.FromIdAsync(watch.Id).AsTask(deadline.Token);
            if (device is not null)
            {
                void ObserveConnection(BluetoothLEDevice changedDevice, object? _)
                {
                    if (Volatile.Read(ref connectionRequestedAt) != 0 &&
                        changedDevice.ConnectionStatus == BluetoothConnectionStatus.Connected)
                        firstConnection.TrySetResult(Stopwatch.GetTimestamp());
                }

                device.ConnectionStatusChanged += ObserveConnection;
                try
                {
                    alreadyConnected = device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    using var session = alreadyConnected ? null :
                        await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(deadline.Token);
                    alreadyConnected |= device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    if (alreadyConnected)
                    {
                        finalConnectionStatus = BluetoothConnectionStatus.Connected;
                    }
                    else if (session?.CanMaintainConnection != true)
                    {
                        outcome = "maintain_unavailable";
                        message = "Windows 无法为这块设备请求维持连接。";
                    }
                    else
                    {
                        requestLatencyMs = timer.ElapsedMilliseconds;
                        BluetoothLog.Write("watch_connection_test_request", new { method = "maintain_connection", alreadyConnected });
                        Volatile.Write(ref connectionRequestedAt, Stopwatch.GetTimestamp());
                        session.MaintainConnection = true;
                        try
                        {
                            // Check immediately as well as listening for changes during the connection request.
                            ObserveConnection(device, null);
                            try { await firstConnection.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                            catch (TimeoutException) { }
                            sessionStatus = session.SessionStatus;
                        }
                        finally
                        {
                            finalConnectionStatus = device.ConnectionStatus;
                            session.MaintainConnection = false;
                        }
                    }
                    if (alreadyConnected || connectionRequestedAt != 0)
                    {
                        bool connected = alreadyConnected || firstConnection.Task.IsCompletedSuccessfully ||
                            finalConnectionStatus == BluetoothConnectionStatus.Connected;
                        outcome = connected ? "connected" : "no_connection_observed";
                        message = connected ? "已观察到 Windows 连接状态。链路加密结果由 HCI 测试确认。"
                            : "10 秒内未观察到连接。";
                        severity = connected ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
                    }
                }
                finally { device.ConnectionStatusChanged -= ObserveConnection; }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            outcome = "timeout";
            message = "等待 Windows 打开配对设备或连接会话超时。";
        }
        catch (Exception ex)
        {
            outcome = "error";
            errorCode = ex.HResult;
            message = $"蓝牙操作失败（0x{errorCode:X8}）。";
            severity = InfoBarSeverity.Error;
        }
        finally
        {
            timer.Stop();
            long connectedAt = firstConnection.Task.IsCompletedSuccessfully ? firstConnection.Task.Result : 0;
            long requestedAt = Volatile.Read(ref connectionRequestedAt);
            TimeSpan? connectionLatency = !alreadyConnected && requestedAt != 0 && connectedAt != 0
                ? Stopwatch.GetElapsedTime(requestedAt, connectedAt) : null;
            bool observedConnection = alreadyConnected || connectedAt != 0 ||
                finalConnectionStatus == BluetoothConnectionStatus.Connected;
            BluetoothLog.Write("watch_connection_test_finished", new
            {
                method = "maintain_connection", outcome, alreadyConnected, observedConnection,
                finalConnectionStatus = finalConnectionStatus?.ToString(), sessionStatus = sessionStatus?.ToString(),
                requestLatencyMs, connectedMs = connectedAt == 0 ? (double?)null : Stopwatch.GetElapsedTime(timerStart, connectedAt).TotalMilliseconds,
                connectionLatencyMs = connectionLatency?.TotalMilliseconds, elapsedMs = timer.ElapsedMilliseconds, errorCode
            });
            if (!_isClosing)
            {
                string link = finalConnectionStatus switch
                {
                    BluetoothConnectionStatus.Connected => "Windows 报告已连接。",
                    BluetoothConnectionStatus.Disconnected => observedConnection
                        ? "Windows 曾报告已连接，测试结束时报告已断开。"
                        : "Windows 在测试期间未报告已连接。",
                    _ => "未能读取 Windows 连接状态。"
                };
                string title = outcome == "connected" ? "已观察到连接" : "连接测试未通过";
                string timing = connectionLatency is { } latency ? $"建立连接用时 {latency.TotalSeconds:F2} 秒。"
                    : alreadyConnected ? "手表已连接，无需发起新连接。" : "无新建连接用时。";
                ShowWatchConnectionTestStatus(title,
                    $"{watch.Name} · {timing} 整轮用时 {timer.Elapsed.TotalSeconds:F2} 秒。{link} {message}", severity);
                TestWatchConnectionButton.IsEnabled = true;
            }
        }
    }

    private void ShowWatchConnectionTestStatus(string title, string message, InfoBarSeverity severity)
    {
        WatchConnectionTestStatus.Title = title;
        WatchConnectionTestStatus.Message = message;
        WatchConnectionTestStatus.Severity = severity;
        WatchConnectionTestStatus.IsOpen = true;
    }

    private void LoadWatchRules()
    {
        try { _watchRules = _watchRuleStore.Read(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _watchRules = new WatchRuleSettings(AutoUnlock: false, AutoLock: false);
            ShowWatchRulesStatus("高级选项读取失败。修改选项后会重新保存。", InfoBarSeverity.Warning);
        }

        UpdateWatchRuleControls();
    }

    private void UpdateWatchRuleControls()
    {
        _updatingWatchRuleControls = true;
        try
        {
            AutoUnlockSwitch.IsOn = _watchRules.AutoUnlock;
            WatchWakeSwitch.IsOn = _watchRules.WakeWithWindowsHello;
            AutoLockSwitch.IsOn = _watchRules.AutoLock;
            ReturnRssiBox.Value = _watchRules.ReturnRssi;
            ReturnWindowBox.Value = _watchRules.ReturnWindowSeconds;
            ReturnPacketCountBox.Value = _watchRules.ReturnPacketCount;
            WeakRssiBox.Value = _watchRules.WeakRssi;
            VeryWeakRssiBox.Value = _watchRules.VeryWeakRssi;
            UnlockDelayBox.Value = _watchRules.UnlockDelaySeconds;
            RequireWatchUnlockedSwitch.IsOn = _watchRules.RequireWatchUnlocked;
            RequireEncryptedConnectionSwitch.IsOn = _watchRules.RequireEncryptedConnection;
            SpeakLockActivitySwitch.IsOn = _watchRules.SpeakLockActivity;
            UpdateAutoLockRuleDescription();
        }
        finally { _updatingWatchRuleControls = false; }
    }

    private void WatchRuleToggleChanged(object sender, RoutedEventArgs args)
    {
        if (_updatingWatchRuleControls || sender is not ToggleSwitch toggle)
            return;

        var rules = toggle.Name switch
        {
            nameof(AutoUnlockSwitch) => _watchRules with
            {
                AutoUnlock = toggle.IsOn,
                WakeWithWindowsHello = !toggle.IsOn && _watchRules.WakeWithWindowsHello
            },
            nameof(WatchWakeSwitch) => _watchRules with
            {
                WakeWithWindowsHello = toggle.IsOn,
                AutoUnlock = !toggle.IsOn && _watchRules.AutoUnlock
            },
            nameof(AutoLockSwitch) => _watchRules with { AutoLock = toggle.IsOn },
            nameof(RequireWatchUnlockedSwitch) => _watchRules with { RequireWatchUnlocked = toggle.IsOn },
            nameof(RequireEncryptedConnectionSwitch) => _watchRules with { RequireEncryptedConnection = toggle.IsOn },
            nameof(SpeakLockActivitySwitch) => _watchRules with { SpeakLockActivity = toggle.IsOn },
            _ => _watchRules
        };
        SaveWatchRules(rules);
    }

    private void WatchRuleValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingWatchRuleControls)
            return;
        if (!double.IsInteger(args.NewValue) || args.NewValue < sender.Minimum || args.NewValue > sender.Maximum)
        {
            UpdateWatchRuleControls();
            ShowWatchRulesStatus("请输入范围内的整数。", InfoBarSeverity.Warning);
            return;
        }

        int value = (int)args.NewValue;
        var rules = sender.Name switch
        {
            nameof(ReturnRssiBox) => _watchRules with { ReturnRssi = value },
            nameof(ReturnWindowBox) => _watchRules with { ReturnWindowSeconds = value },
            nameof(ReturnPacketCountBox) => _watchRules with { ReturnPacketCount = value },
            nameof(WeakRssiBox) => _watchRules with { WeakRssi = value },
            nameof(VeryWeakRssiBox) => _watchRules with { VeryWeakRssi = value },
            nameof(UnlockDelayBox) => _watchRules with { UnlockDelaySeconds = value },
            _ => _watchRules
        };
        SaveWatchRules(rules);
    }

    private void SaveWatchRules(WatchRuleSettings rules)
    {
        if (_watchRules == rules)
            return;
        if (!rules.IsValid())
        {
            UpdateWatchRuleControls();
            ShowWatchRulesStatus("极弱信号阈值不能高于弱信号阈值。", InfoBarSeverity.Warning);
            return;
        }

        try { _watchRuleStore.Save(rules); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UpdateWatchRuleControls();
            string message = $"无法保存高级选项（0x{ex.HResult:X8}）。";
            ShowWatchRulesStatus(message, InfoBarSeverity.Error);
            if (rules.WakeWithWindowsHello != _watchRules.WakeWithWindowsHello)
                WatchWakeStatus.Text = message;
            return;
        }

        var previous = _watchRules;
        _watchRules = rules;
        if (previous.AutoUnlock != rules.AutoUnlock || previous.WakeWithWindowsHello != rules.WakeWithWindowsHello)
            UpdateWatchRuleControls();
        if (previous.WeakRssi != rules.WeakRssi || previous.VeryWeakRssi != rules.VeryWeakRssi)
            UpdateAutoLockRuleDescription();
        if (!rules.SpeakLockActivity)
            StopLockActivitySpeech();
        WatchRulesStatus.IsOpen = false;
        if ((previous with { SpeakLockActivity = rules.SpeakLockActivity }) == rules)
            return;

        if (previous.AutoUnlock != rules.AutoUnlock && _scan is { } scan && PairedWatchBox.SelectedItem is PairedWatch watch)
        {
            StopDirectUnlock();
            RestoreDirectUnlock(watch, scan);
        }
        else
        {
            _directWatchWake?.SetRules(rules);
        }
        if (previous.WakeWithWindowsHello != rules.WakeWithWindowsHello)
            UpdateWindowsHelloWake();
        RefreshScan();
    }

    private void UpdateAutoLockRuleDescription()
    {
        AutoLockRuleDescription.Text = $"在 18 秒内，至少收到两条间隔达到 3 秒的极弱广播（≤ {_watchRules.VeryWeakRssi} dBm），" +
            $"或至少三条首尾间隔达到 6 秒的弱广播（≤ {_watchRules.WeakRssi} dBm），且本机连续 10 秒无输入，才会请求锁屏。" +
            "手表暂时没有广播时保持当前状态。";
    }

    private void ShowWatchRulesStatus(string message, InfoBarSeverity severity)
    {
        WatchRulesStatus.Message = message;
        WatchRulesStatus.Severity = severity;
        WatchRulesStatus.IsOpen = true;
    }
}
