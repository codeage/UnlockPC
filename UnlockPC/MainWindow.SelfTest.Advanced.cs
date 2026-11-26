using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace UnlockPC;

public sealed partial class MainWindow
{
    internal async Task RunAdvancedOptionsSelfTestAsync()
    {
        try
        {
            if (!await WaitForSelfTestAsync(() => RootNavigationView.XamlRoot is not null))
                throw new InvalidOperationException("测试窗口未加载。");

            RootNavigationView.SelectedItem = AdvancedItem;
            if (!await WaitForSelfTestAsync(() => AdvancedPage.Visibility == Visibility.Visible) || MonitorPage.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("导航未切换至高级选项。");

            var legacy = new WatchRuleSettings(true, true, -57, 3, 2);
            if (_watchRules != legacy || SpeakLockActivitySwitch.IsOn)
                throw new InvalidOperationException("无法恢复旧版已保存的阈值。");
#if UNLOCKPC_DEBUG_PAGE
            if (_returnRssiBox.Value != -57 || _returnWindowBox.Value != 3 || _returnPacketCountBox.Value != 2 ||
                _weakRssiBox.Value != -75 || _veryWeakRssiBox.Value != -85 || _unlockDelayBox.Value != 0)
                throw new InvalidOperationException("调试页未加载已保存的阈值。");
#endif

            RootNavigationView.SelectedItem = WatchManagementItem;
            _hasSavedUnlock = true;
            UpdateUnlockModeControls();
            ((IToggleProvider)new ToggleSwitchAutomationPeer(RequireWatchUnlockedSwitch)).Toggle();
            var firstChange = legacy with { RequireWatchUnlocked = true };
            if (!await WaitForSelfTestAsync(() => _watchRules == firstChange) || _watchRuleStore.Read() != firstChange)
                throw new InvalidOperationException("手表解锁线索开关未自动保存。");

            ((IToggleProvider)new ToggleSwitchAutomationPeer(RequireEncryptedConnectionSwitch)).Toggle();
            var secondChange = firstChange with { RequireEncryptedConnection = true };
            if (!await WaitForSelfTestAsync(() => _watchRules == secondChange) || _watchRuleStore.Read() != secondChange)
                throw new InvalidOperationException("加密连接开关未自动保存。");

            RootNavigationView.SelectedItem = AdvancedItem;
            ((IToggleProvider)new ToggleSwitchAutomationPeer(SpeakLockActivitySwitch)).Toggle();
            var speechChange = secondChange with { SpeakLockActivity = true };
            if (!await WaitForSelfTestAsync(() => _watchRules == speechChange) || _watchRuleStore.Read() != speechChange)
                throw new InvalidOperationException("语音播报开关未自动保存。");

            AutoUnlockSwitch.IsOn = false;
            AutoLockSwitch.IsOn = false;
            var expected = speechChange with { AutoUnlock = false, AutoLock = false };
#if UNLOCKPC_DEBUG_PAGE
            ShowDebugPage();
            _returnRssiBox.Value = -55;
            _returnWindowBox.Value = 4;
            _returnPacketCountBox.Value = 3;
            _weakRssiBox.Value = -82;
            _veryWeakRssiBox.Value = -91;
            _unlockDelayBox.Value = 5;
            expected = expected with { ReturnRssi = -55, ReturnWindowSeconds = 4, ReturnPacketCount = 3,
                WeakRssi = -82, VeryWeakRssi = -91, UnlockDelaySeconds = 5 };
#endif
            if (!await WaitForSelfTestAsync(() => _watchRules == expected) || _watchRuleStore.Read() != expected)
                throw new InvalidOperationException("无法自动保存锁定、解锁和数字选项。");

            var reopened = new MainWindow(showTrayIcon: false, ruleStore: _watchRuleStore);
            try
            {
                reopened.Activate();
                if (reopened._watchRules != expected ||
                    !reopened.RequireWatchUnlockedSwitch.IsOn || !reopened.RequireEncryptedConnectionSwitch.IsOn ||
                    !reopened.SpeakLockActivitySwitch.IsOn)
                    throw new InvalidOperationException("重新打开时未恢复高级设置。");
#if UNLOCKPC_DEBUG_PAGE
                if (reopened._returnRssiBox.Value != -55 || reopened._returnWindowBox.Value != 4 ||
                    reopened._returnPacketCountBox.Value != 3 || reopened._weakRssiBox.Value != -82 ||
                    reopened._veryWeakRssiBox.Value != -91 || reopened._unlockDelayBox.Value != 5 ||
                    !reopened._debugLockRuleDescription.Text.Contains("-91 dBm", StringComparison.Ordinal))
                    throw new InvalidOperationException("重新打开时未恢复调试参数。");
#endif
            }
            finally { reopened.Close(); }

            BluetoothLog.Write("ui_self_test_passed", new { test = "advanced-options" });
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("ui_self_test_failed", new { test = "advanced-options", ex.Message, ex.HResult });
        }
        finally
        {
            Close();
        }
    }

    internal async Task RunWatchEncryptionSelfTestAsync()
    {
        try
        {
            using var saved = ReadSavedUnlock() ?? throw new InvalidOperationException("没有已授权的手表。");
            var watches = await PairedWatch.ListAsync();
            var watch = watches.FirstOrDefault(candidate => IsSameId(candidate.Id, saved.DeviceId))
                ?? throw new InvalidOperationException("找不到已授权的配对手表。");
            using var monitor = await BondedIrk.OpenAsync(saved.BondAddress == 0 ? watch.Address : saved.BondAddress);
            var result = await WatchConnectionVerification.VerifyAsync(watch, monitor.ResolvesAsync, CancellationToken.None, waitForCleanup: true);
            if (result == WatchConnectionVerificationResult.Unconfirmed)
                throw new InvalidOperationException("未通过已配对手表的蓝牙连接验证。");
            BluetoothLog.Write("ui_self_test_passed", new { test = "watch-encryption", verification = result.ToString() });
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("ui_self_test_failed", new { test = "watch-encryption", ex.Message, ex.HResult });
        }
        finally { Close(); }
    }
}
