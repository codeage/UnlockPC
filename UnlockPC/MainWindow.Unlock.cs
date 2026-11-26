using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Security.Principal;
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
    private bool _hasUnlockPassword;
    private bool _needsUnlockCredential;
    private bool _unlockPasswordPromptPending;
    private (PresenceScan Scan, Action Notify)? _directSignalSubscription;
    private long _lastDirectSignalPoll;
    private int _directRestoreVersion;

    private SavedUnlock? ReadSavedUnlock()
    {
        _hasSavedUnlock = _unlockStore.Exists;
        try
        {
            var saved = _unlockStore.Read();
            _savedUnlockDeviceId = saved?.DeviceId;
            _hasSavedUnlock = saved is not null;
            _hasUnlockPassword = saved?.Password.Length > 0;
            return saved;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or FormatException)
        {
            _hasUnlockPassword = false;
            _directUnlockError = "无法读取已保存的自动解锁配置。撤销手表授权后可重新设置。";
            BluetoothLog.Write("direct_unlock_config_read_failed", new { ex.HResult });
            return null;
        }
    }

    private async Task<bool> RestoreDirectUnlockAsync(PairedWatch watch, PresenceScan scan)
    {
        int version = ++_directRestoreVersion;
        using var saved = ReadSavedUnlock();
        if (saved is null || !IsSameId(saved.DeviceId, watch.Id))
            return false;

        try
        {
            ulong address = saved.BondAddress == 0 ? watch.Address : saved.BondAddress;
            if (!await scan.StartIrkObservationAsync(address))
                return false;
            if (_isClosing || !ReferenceEquals(scan, _scan) || version != _directRestoreVersion)
                return false;
            if (saved.BondAddress == 0)
            {
                using var current = ReadSavedUnlock();
                if (current is null || !IsSameId(current.DeviceId, watch.Id))
                    return false;
                _unlockStore.Save(watch.Id, address, current.Password);
            }
            DirectUnlock? broker = null;
            if (_watchRules.AutoUnlock && saved.Password.Length > 0 && ProviderRegistration.IsInstalled())
            {
                try { broker = DirectUnlock.Restore(saved.Password); }
                catch (Exception ex) { BluetoothLog.Write("direct_unlock_credential_restore_failed", new { ex.HResult }); }
            }
            _needsUnlockCredential = _watchRules.AutoUnlock && _hasUnlockPassword && broker is null;
            try
            {
                StartDirectUnlock(broker, watch, scan);
            }
            catch
            {
                broker?.Dispose();
                throw;
            }
            BluetoothLog.Write("direct_unlock_config_restored");
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (!_isClosing && ReferenceEquals(scan, _scan) && version == _directRestoreVersion)
            {
                _directUnlockError = ex is InvalidOperationException ? ex.Message : $"无法恢复手表授权（0x{ex.HResult:X8}）。";
                UpdateDirectUnlockControls();
            }
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
            _hasUnlockPassword = false;
            _needsUnlockCredential = false;
            _unlockPasswordPromptPending = false;
            _savedUnlockDeviceId = null;
            _directUnlockError = null;
            BluetoothLog.Write("direct_unlock_config_deleted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _directUnlockError = $"无法删除自动解锁配置（0x{ex.HResult:X8}）。请重试。";
            BluetoothLog.Write("direct_unlock_config_delete_failed", new { ex.HResult });
        }
        UpdateDirectUnlockControls();
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
                UpdateUnlockModeControls();
                if (_unlockPasswordPromptPending && watcher.IsSessionUnlocked && _watchRules.AutoUnlock &&
                    _watchBinding is null && !_removingWatch && !_isClosing)
                {
                    _unlockPasswordPromptPending = false;
                    OpenSettings();
                    RootNavigationView.SelectedItem = WatchManagementItem;
                    _ = ConfigureWatchAsync(passwordRejected: true);
                }
                return;
            }
            StopDirectUnlock();
            UpdateWindowsHelloWake();
        }

        UpdateDirectUnlockControls();
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
            string status = watcher.Update(now, snapshot.LastNear, interrupted, snapshot.UnlockSamples, snapshot.UnlockSamplesDropped);
            if (_watchRules.WakeWithWindowsHello)
                WatchWakeStatus.Text = whileUnlocked ? "等待本机控制台锁屏。" : status;
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

    private void UpdateDirectUnlockControls()
    {
        DirectUnlockStatus.Text = _directUnlockError ?? (_hasSavedUnlock
            ? _directWatchWake is null ? "已保存手表绑定，等待开始检测。" : "已绑定，正在通过 IRK 服务检测。"
            : _selectedWatch is null ? "" : "Windows 已配对，等待绑定。");
        UpdateWatchManagement();
    }

    private void UpdateUnlockModeControls()
    {
        bool canConfigure = _hasSavedUnlock && _watchBinding is null && !_removingWatch && !_promptingForDirectUnlock;
        bool isOn = _hasSavedUnlock && (_watchRules.AutoUnlock || _watchRules.WakeWithWindowsHello);
        bool windowsHello = _watchRules.PreferredWindowsHello;

        bool updating = _updatingWatchRuleControls;
        _updatingWatchRuleControls = true;
        try
        {
            AutoUnlockSwitch.IsEnabled = canConfigure;
            AutoUnlockSwitch.IsOn = isOn;
            UnlockModeBox.IsEnabled = canConfigure && isOn;
            UnlockModeBox.SelectedItem = windowsHello ? WindowsHelloMode : PasswordUnlockMode;
            PasswordUnlockSettings.Visibility = isOn && !windowsHello ? Visibility.Visible : Visibility.Collapsed;
            WindowsHelloSettings.Visibility = isOn && windowsHello ? Visibility.Visible : Visibility.Collapsed;
            SetUnlockCredentialButton.IsEnabled = canConfigure;
            UnlockCredentialDescription.Text = _hasUnlockPassword ? "输入你当前用于登录 Windows 的密码。"
                : "输入当前 Windows 登录密码后，才能使用此方式。";
        }
        finally { _updatingWatchRuleControls = updating; }
    }

    private async void SetUnlockCredential_Click(object sender, RoutedEventArgs args) => await ConfigureWatchAsync();

    private async Task ConfigureWatchAsync(bool enableAutoUnlock = false, bool passwordRejected = false)
    {
        if (_promptingForDirectUnlock || _watchBinding is not null || _removingWatch || _isClosing ||
            _savedUnlockDeviceId is not { } id || WindowRootGrid.XamlRoot is not { } root)
            return;

        _promptingForDirectUnlock = true;
        _directUnlockError = null;
        UpdateWatchManagement();
        try
        {
            DirectUnlock.VerifyAccount();
            bool shouldEnable = enableAutoUnlock || _watchRules.AutoUnlock;
            bool installProvider = !ProviderRegistration.IsInstalled();
            using var identity = WindowsIdentity.GetCurrent();
            var password = new PasswordBox { Header = "Windows 登录密码", MaxLength = 255 };
            var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
            string instructions = passwordRejected
                ? "Windows 未接受 UnlockPC 保存的密码。请输入当前密码以恢复自动解锁。"
                : "输入当前 Windows 账户的登录密码。验证后，UnlockPC 会为当前用户加密保存。";
            if (installProvider)
                instructions += "\n\n首次设置时，Windows 会要求管理员确认安装登录组件。";
            if (enableAutoUnlock)
                instructions += "\n\n蓝牙信号无法确认手表是否佩戴，也无法防止广播重放。";

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "输入 Windows 登录密码",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = instructions, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = identity.Name, TextWrapping = TextWrapping.Wrap },
                        password,
                        error
                    }
                },
                PrimaryButtonText = "验证并保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            DirectUnlock? entered = null;
            dialog.Opened += (_, _) => password.Focus(FocusState.Programmatic);
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try { entered = DirectUnlock.Create(password.Password); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    args.Cancel = true;
                    error.Message = ex is Win32Exception win32 && DirectUnlock.IsPasswordRejected(win32)
                        ? "Windows 未接受此密码。请输入当前 Windows 账户的登录密码。"
                        : ex.Message;
                    error.IsOpen = true;
                    password.Focus(FocusState.Programmatic);
                }
                finally { password.Password = ""; }
            };

            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); }
            catch
            {
                entered?.Dispose();
                throw;
            }
            finally { password.Password = ""; }

            using var broker = entered;
            if (result != ContentDialogResult.Primary || broker is null || _isClosing)
                return;

            if (installProvider)
            {
                DirectUnlockStatus.Text = "等待 Windows 确认安装登录组件…";
                if (!await ProviderRegistration.EnsureInstalledAsync() || _isClosing)
                    return;
            }

            using var saved = ReadSavedUnlock();
            if (saved is null || !IsSameId(id, saved.DeviceId))
                return;

            byte[] clearPassword = broker.CopyPassword();
            try { _unlockStore.Save(id, saved.BondAddress, clearPassword); }
            finally { CryptographicOperations.ZeroMemory(clearPassword); }

            _hasUnlockPassword = true;
            _needsUnlockCredential = false;
            _unlockPasswordPromptPending = false;
            BluetoothLog.Write("direct_unlock_config_saved");
            if (_watchRules.AutoUnlock != shouldEnable)
                SaveWatchRules(_watchRules with
                {
                    AutoUnlock = shouldEnable,
                    WakeWithWindowsHello = !shouldEnable && _watchRules.WakeWithWindowsHello,
                    PreferredWindowsHello = !shouldEnable && _watchRules.PreferredWindowsHello
                });
            else if (_scan is { } scan && _selectedWatch is { } watch && IsSameId(watch.Id, id))
            {
                StopDirectUnlock();
                await RestoreDirectUnlockAsync(watch, scan);
            }
            ShowWatchManagementStatus("已保存 Windows 登录密码。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                DirectUnlockStatus.Text = _directUnlockError = ex.Message;
                ShowWatchManagementStatus(ex.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            _promptingForDirectUnlock = false;
            if (!_isClosing)
            {
                UpdateWatchManagement();
                RefreshScan();
            }
        }
    }

    private void StartDirectUnlock(DirectUnlock? broker, PairedWatch watch, PresenceScan scan)
    {
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _watchWake?.Dispose();
        _watchWake = null;
        void ProcessLockedSignals()
        {
            if (_directWatchWake is { } current)
                ProcessDirectSignals(scan, current, whileUnlocked: false);
        }

        WatchWake? currentWatcher = null;
        var watcher = new WatchWake(window, broker, _watchRules,
            token => WatchConnectionVerification.VerifyAsync(watch, scan.ResolvesAsync, token, RecordLockActivity),
            RecordLockActivity, ProcessLockedSignals, (since, timedOut) =>
        {
            if (broker is not null && currentWatcher is not null)
                _ = CheckUnlockPasswordAsync(broker, currentWatcher, watch, scan, since, timedOut);
        });
        currentWatcher = watcher;
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
        UpdateDirectUnlockControls();
    }

    private async Task CheckUnlockPasswordAsync(DirectUnlock broker, WatchWake watcher, PairedWatch watch,
        PresenceScan scan, DateTime since, bool timedOut)
    {
        try
        {
            await Task.Run(() =>
            {
                if (timedOut || DirectUnlock.WindowsReportedAuthenticationFailure(since))
                    broker.VerifyPassword();
            });
        }
        catch (Win32Exception ex) when (DirectUnlock.IsPasswordRejected(ex))
        {
            if (!_isClosing && _watchRules.AutoUnlock && ReferenceEquals(_directWatchWake, watcher) && ReferenceEquals(_scan, scan))
            {
                BluetoothLog.Write("direct_unlock_password_rejected", new { ex.NativeErrorCode });
                StopDirectUnlock();
                _needsUnlockCredential = true;
                _unlockPasswordPromptPending = true;
                StartDirectUnlock(null, watch, scan);
                ShowWatchManagementStatus("Windows 未接受已保存的密码。请更新自动解锁密码。", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("direct_unlock_password_check_failed", new { ex.HResult });
        }
    }

    private void StopDirectUnlock()
    {
        _directRestoreVersion++;
        _unlockedSignalTimer.Stop();
        if (_directSignalSubscription is { } subscription)
        {
            subscription.Scan.UnlockSampleQueued -= subscription.Notify;
            _directSignalSubscription = null;
        }
        _directWatchWake?.Dispose();
        _directWatchWake = null;
        UpdateDirectUnlockControls();
    }
}
