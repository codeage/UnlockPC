using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private string? _directUnlockError;
    private readonly SavedUnlockStore _unlockStore = SavedUnlockStore.ForCurrentUser();
    private string? _savedUnlockDeviceId;
    private bool _hasSavedUnlock;
    private bool _needsUnlockCredential;
    private (PresenceScan Scan, Action Notify)? _directSignalSubscription;
    private long _lastDirectSignalPoll;

    private SavedUnlock? ReadSavedUnlock()
    {
        _hasSavedUnlock = _unlockStore.Exists;
        try
        {
            var saved = _unlockStore.Read();
            _savedUnlockDeviceId = saved?.DeviceId;
            _hasSavedUnlock = saved is not null;
            return saved;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            _directUnlockError = "无法读取已保存的自动解锁配置。撤销手表授权后可重新设置。";
            BluetoothLog.Write("direct_unlock_config_read_failed", new { ex.HResult });
            return null;
        }
    }

    private bool RestoreDirectUnlock(PairedWatch watch, PresenceScan scan)
    {
        using var saved = ReadSavedUnlock();
        if (saved is null || !IsSameId(saved.DeviceId, watch.Id))
            return false;

        try
        {
            DirectUnlock? broker = null;
            if (_watchRules.AutoUnlock && saved.Password.Length > 0 && ProviderRegistration.IsInstalled())
            {
                try { broker = DirectUnlock.Restore(saved.Password); }
                catch (Exception ex) { BluetoothLog.Write("direct_unlock_credential_restore_failed", new { ex.HResult }); }
            }
            _needsUnlockCredential = _watchRules.AutoUnlock && broker is null;
            try
            {
                if (scan.Read().Irk is null && !scan.StartIrkObservation(saved.Irk))
                    throw new InvalidOperationException("蓝牙检测未运行。");
                StartDirectUnlock(broker, watch, scan);
            }
            catch
            {
                broker?.Dispose();
                throw;
            }
            BluetoothLog.Write("direct_unlock_config_restored");
        }
        catch (Exception ex)
        {
            _directUnlockError = $"无法恢复手表授权（0x{ex.HResult:X8}）。可撤销授权后重新设置。";
            BluetoothLog.Write("direct_unlock_config_restore_failed", new { ex.HResult });
        }
        return _directWatchWake is not null;
    }

    private async Task ForgetIfUnpairedAsync(string deviceId)
    {
        if (_savedUnlockDeviceId is not { } savedId || !IsSameId(savedId, deviceId))
            return;

        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            var radio = adapter is null ? null : await adapter.GetRadioAsync();
            if (radio?.State != RadioState.On)
                return;

            var device = await DeviceInformation.CreateFromIdAsync(deviceId, PairedWatch.Properties,
                DeviceInformationKind.AssociationEndpoint).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            if (!_isClosing && radio.State == RadioState.On && device?.Pairing.IsPaired != true)
                ForgetSavedUnlockForDevice(deviceId);
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("direct_unlock_pairing_check_failed", new { ex.HResult });
        }
    }

    private void ForgetSavedUnlockForDevice(string deviceId)
    {
        if (_savedUnlockDeviceId is { } savedId && IsSameId(savedId, deviceId))
            ForgetSavedUnlock();
    }

    private void ForgetSavedUnlock()
    {
        StopDirectUnlock();
        try
        {
            _unlockStore.Delete();
            _hasSavedUnlock = false;
            _needsUnlockCredential = false;
            _savedUnlockDeviceId = null;
            _directUnlockError = null;
            BluetoothLog.Write("direct_unlock_config_deleted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _directUnlockError = $"无法删除自动解锁配置（0x{ex.HResult:X8}）。请重试。";
            BluetoothLog.Write("direct_unlock_config_delete_failed", new { ex.HResult });
        }
        UpdateDirectUnlockControls(_scan?.Read().Irk is { Matched: > 0 });
    }

    private void RefreshDirectUnlock(PresenceScan scan, ScanSnapshot snapshot)
    {
        if (_promptingForDirectUnlock)
            return;

        if (_directWatchWake is { } watcher)
        {
            if (snapshot.Irk is not null)
            {
                // Keep the periodic check for session changes and authentication timeouts without an advertisement.
                ProcessDirectSignals(scan, watcher, whileUnlocked: false);
                UpdateCredentialButton(snapshot.Irk is { Matched: > 0 });
                return;
            }
            StopDirectUnlock();
        }

        UpdateDirectUnlockControls(snapshot.Irk is { Matched: > 0 });
    }

    private void ProcessDirectSignals(PresenceScan scan, WatchWake watcher, bool whileUnlocked)
    {
        if (_isClosing || _promptingForDirectUnlock || !ReferenceEquals(scan, _scan) ||
            !ReferenceEquals(watcher, _directWatchWake) || watcher.IsSessionUnlocked != whileUnlocked)
            return;

        try
        {
            if (!watcher.CanReadSamples(Stopwatch.GetTimestamp()) || watcher.IsSessionUnlocked != whileUnlocked)
                return;

            var snapshot = scan.Read(consumeUnlockSamples: true);
            if (snapshot.DeviceRemoved || snapshot.Irk is null ||
                snapshot.Status != BluetoothLEAdvertisementWatcherStatus.Started)
                return;

            long now = Stopwatch.GetTimestamp();
            bool interrupted = Stopwatch.GetElapsedTime(_lastDirectSignalPoll, now) > TimeSpan.FromSeconds(3);
            _lastDirectSignalPoll = now;
            string status = watcher.Update(now, 0, interrupted, snapshot.UnlockSamples, snapshot.UnlockSamplesDropped);
            if (_needsUnlockCredential)
                status = $"请设置解锁凭据。{status}";
            if (DirectUnlockStatus.Text != status)
                DirectUnlockStatus.Text = status;
        }
        catch (Exception ex)
        {
            EndScan();
            ShowScanError($"蓝牙扫描中断（0x{ex.HResult:X8}）。");
        }
    }

    private void UpdateDirectUnlockControls(bool matched)
    {
        DirectUnlockButton.Content = _hasSavedUnlock ? "撤销手表授权" : _watchRules.AutoUnlock ? "开启自动解锁…" : "绑定手表…";
        DirectUnlockButton.IsEnabled = _hasSavedUnlock || matched;
        DirectUnlockStatus.Text = _directUnlockError ?? (_hasSavedUnlock
            ? "已保存自动解锁。请开始检测绑定的手表以恢复。"
            : matched
            ? "已收到 IRK 匹配，可以授权此手表使用高级选项中的规则。"
            : "请先开启 IRK 换址验证并等待手表匹配。");
        UpdateCredentialButton(matched);
    }

    private void UpdateCredentialButton(bool matched)
    {
        SetUnlockCredentialButton.Visibility = _watchRules.AutoUnlock && _needsUnlockCredential && _hasSavedUnlock
            ? Visibility.Visible : Visibility.Collapsed;
        SetUnlockCredentialButton.IsEnabled = matched && !_promptingForDirectUnlock;
    }

    private async void DirectUnlock_Click(object sender, RoutedEventArgs args)
    {
        if (_hasSavedUnlock)
        {
            bool clearActiveIrk = _savedUnlockDeviceId is { } id && _lastWatchId is { } lastId && IsSameId(id, lastId);
            ForgetSavedUnlock();
            if (clearActiveIrk)
                _scan?.StopIrkObservation();
            RefreshScan();
            return;
        }
        await ConfigureWatchAsync();
    }

    private async void SetUnlockCredential_Click(object sender, RoutedEventArgs args) => await ConfigureWatchAsync();

    private async Task ConfigureWatchAsync()
    {
        if (_promptingForDirectUnlock || _scan is not { } scan || PairedWatchBox.SelectedItem is not PairedWatch watch)
            return;

        _promptingForDirectUnlock = true;
        _directUnlockError = null;
        DirectUnlockButton.IsEnabled = false;
        try
        {
            bool useCredential = _watchRules.AutoUnlock;
            if (useCredential)
                DirectUnlock.VerifyAccount();
            bool installProvider = useCredential && !ProviderRegistration.IsInstalled();

            var dialog = new ContentDialog
            {
                XamlRoot = DirectUnlockButton.XamlRoot,
                Title = useCredential ? "授权 Apple Watch 自动解锁？" : "绑定 Apple Watch？",
                Content = (installProvider ? "首次开启时，Windows 会请求管理员确认以安装登录组件。\n\n" : "") +
                    (useCredential
                        ? "蓝牙信号无法确认手表是否佩戴，也无法防止广播重放。手表密钥和密码将为当前 Windows 用户加密保存。"
                        : "UnlockPC 将加密保存手表密钥，并按高级选项中的自动锁定规则检测信号。"),
                PrimaryButtonText = "继续",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _isClosing || _scan != scan ||
                scan.Read().Irk is not { Matched: > 0 })
                return;

            if (installProvider)
            {
                DirectUnlockStatus.Text = "等待 Windows 确认安装登录组件…";
                if (!await ProviderRegistration.EnsureInstalledAsync() || _isClosing || _scan != scan ||
                    scan.Read().Irk is not { Matched: > 0 })
                    return;
            }

            nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            DirectUnlock? broker = useCredential ? DirectUnlock.Prompt(window, persist: true) : null;
            if (useCredential && broker is null)
                return;
            try
            {
                if (_isClosing || _scan != scan || scan.Read() is not { DeviceRemoved: false, Irk.Matched: > 0 } ||
                    scan.CopyIrk() is not { } irk)
                {
                    broker?.Dispose();
                    return;
                }

                try
                {
                    byte[] password = broker?.CopyPassword() ?? [];
                    try { _unlockStore.Save(watch.Id, irk, password); }
                    finally { CryptographicOperations.ZeroMemory(password); }
                }
                finally { CryptographicOperations.ZeroMemory(irk); }

                _savedUnlockDeviceId = watch.Id;
                _hasSavedUnlock = true;
                _needsUnlockCredential = false;
                BluetoothLog.Write("direct_unlock_config_saved");
                StopDirectUnlock();
                StartDirectUnlock(broker, watch, scan);
            }
            catch
            {
                broker?.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            if (!_isClosing)
                DirectUnlockStatus.Text = _directUnlockError = ex.Message;
        }
        finally
        {
            _promptingForDirectUnlock = false;
            if (!_isClosing)
                RefreshScan();
        }
    }

    private void StartDirectUnlock(DirectUnlock? broker, PairedWatch watch, PresenceScan scan)
    {
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WatchWakeSwitch.IsOn = false;
        WatchWakeSwitch.IsEnabled = false;
        void ProcessLockedSignals()
        {
            if (_directWatchWake is { } current)
                ProcessDirectSignals(scan, current, whileUnlocked: false);
        }

        var watcher = new WatchWake(window, broker, _watchRules, async token =>
        {
            if (scan.CopyIrk() is not { } key)
                return false;
            try { return await WatchConnectionVerification.VerifyAsync(watch, key, token, RecordLockActivity).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(key); }
        }, RecordLockActivity, ProcessLockedSignals);
        _directWatchWake = watcher;
        var dispatcher = DispatcherQueue;
        var signalDispatch = new WatchSignalDispatch(() => watcher.RequestsImmediateSamples,
            process => dispatcher.TryEnqueue(() => process()),
            () => ProcessDirectSignals(scan, watcher, whileUnlocked: false));
        _directSignalSubscription = (scan, signalDispatch.Signal);
        scan.UnlockSampleQueued += signalDispatch.Signal;
        _lastDirectSignalPoll = Stopwatch.GetTimestamp();
        _unlockedSignalTimer.Start();
        _directUnlockError = null;
        DirectUnlockButton.Content = "撤销手表授权";
        DirectUnlockButton.IsEnabled = true;
        DirectUnlockStatus.Text = "已绑定手表。";
    }

    private void StopDirectUnlock()
    {
        _unlockedSignalTimer.Stop();
        if (_directSignalSubscription is { } subscription)
        {
            subscription.Scan.UnlockSampleQueued -= subscription.Notify;
            _directSignalSubscription = null;
        }
        _directWatchWake?.Dispose();
        _directWatchWake = null;
        UpdateDirectUnlockControls(matched: false);
    }
}
