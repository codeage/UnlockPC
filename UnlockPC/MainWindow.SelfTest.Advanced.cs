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
            if (_watchRules != legacy || SpeakLockActivitySwitch.IsOn ||
                ReturnRssiBox.Value != -57 || ReturnWindowBox.Value != 3 || ReturnPacketCountBox.Value != 2 ||
                WeakRssiBox.Value != -75 || VeryWeakRssiBox.Value != -85 || UnlockDelayBox.Value != 0)
                throw new InvalidOperationException("无法恢复旧版已保存的阈值。");

            ((IToggleProvider)new ToggleSwitchAutomationPeer(RequireWatchUnlockedSwitch)).Toggle();
            var firstChange = legacy with { RequireWatchUnlocked = true };
            if (!await WaitForSelfTestAsync(() => _watchRules == firstChange) || _watchRuleStore.Read() != firstChange)
                throw new InvalidOperationException("手表解锁线索开关未自动保存。");

            ((IToggleProvider)new ToggleSwitchAutomationPeer(RequireEncryptedConnectionSwitch)).Toggle();
            var secondChange = firstChange with { RequireEncryptedConnection = true };
            if (!await WaitForSelfTestAsync(() => _watchRules == secondChange) || _watchRuleStore.Read() != secondChange)
                throw new InvalidOperationException("加密连接开关未自动保存。");

            ((IToggleProvider)new ToggleSwitchAutomationPeer(SpeakLockActivitySwitch)).Toggle();
            var speechChange = secondChange with { SpeakLockActivity = true };
            if (!await WaitForSelfTestAsync(() => _watchRules == speechChange) || _watchRuleStore.Read() != speechChange)
                throw new InvalidOperationException("语音播报开关未自动保存。");

            AutoUnlockSwitch.IsOn = false;
            AutoLockSwitch.IsOn = false;
            ReturnRssiBox.Value = -55;
            ReturnWindowBox.Value = 4;
            ReturnPacketCountBox.Value = 3;
            WeakRssiBox.Value = -82;
            VeryWeakRssiBox.Value = -91;
            UnlockDelayBox.Value = 5;
            var expected = new WatchRuleSettings(false, false, -55, 4, 3, true, true, true,
                WeakRssi: -82, VeryWeakRssi: -91, UnlockDelaySeconds: 5);
            if (!await WaitForSelfTestAsync(() => _watchRules == expected) || _watchRuleStore.Read() != expected)
                throw new InvalidOperationException("无法自动保存锁定、解锁和数字选项。");

            var reopened = new MainWindow(showTrayIcon: false, ruleStore: _watchRuleStore);
            try
            {
                reopened.Activate();
                if (reopened._watchRules != expected || reopened.ReturnRssiBox.Value != -55 ||
                    reopened.ReturnWindowBox.Value != 4 || reopened.ReturnPacketCountBox.Value != 3 ||
                    !reopened.RequireWatchUnlockedSwitch.IsOn || !reopened.RequireEncryptedConnectionSwitch.IsOn ||
                    !reopened.SpeakLockActivitySwitch.IsOn || reopened.WeakRssiBox.Value != -82 ||
                    reopened.VeryWeakRssiBox.Value != -91 || reopened.UnlockDelayBox.Value != 5 ||
                    !reopened.AutoLockRuleDescription.Text.Contains("-91 dBm", StringComparison.Ordinal))
                    throw new InvalidOperationException("重新打开时未恢复高级设置。");
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
            if (!await WatchConnectionVerification.VerifyAsync(watch, saved.Irk, CancellationToken.None, waitForCleanup: true))
                throw new InvalidOperationException("未确认已配对手表的新链路加密。");
            BluetoothLog.Write("ui_self_test_passed", new { test = "watch-encryption" });
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("ui_self_test_failed", new { test = "watch-encryption", ex.Message, ex.HResult });
        }
        finally { Close(); }
    }
}
