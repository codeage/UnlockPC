using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace UnlockPC;

public sealed partial class MainWindow
{
    internal async Task RunLockCycleSelfTestAsync()
    {
        Environment.ExitCode = 1;
        int currentRound = 0;
        try
        {
            bool domainAccount = DirectUnlock.VerifyAccount();
            if (!ProviderRegistration.IsInstalled(domainAccount))
                throw new InvalidOperationException("请先在 UnlockPC 中安装 Windows 登录组件。");
            if (!await WaitForSelfTestAsync(() => WindowRootGrid.XamlRoot is not null))
                throw new InvalidOperationException("测试窗口未加载。");

            var consent = new ContentDialog
            {
                XamlRoot = WindowRootGrid.XamlRoot,
                Title = "开始 Windows 锁屏与解锁自检？",
                Content = "自检会真实锁屏 5 次。每轮用模拟广播确认手表离开并返回，再发起解锁。请勿手动锁屏或解锁。如果某轮失败，请使用常规 Windows 登录方式进入桌面。",
                PrimaryButtonText = "开始测试",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await consent.ShowAsync() != ContentDialogResult.Primary || _isClosing)
            {
                Environment.ExitCode = 2;
                return;
            }

            nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            using var broker = DirectUnlock.Prompt(window);
            if (broker is null || _isClosing)
            {
                Environment.ExitCode = 2;
                return;
            }

            using var watcher = new WatchWake(window, broker);
            await LockCycleTest.RunAsync(LockRoundAsync, UnlockRoundAsync, Task.Delay);
            BluetoothLog.Write("ui_self_test_passed", new { test = "lock-cycle", rounds = LockCycleTest.Rounds });
            Environment.ExitCode = 0;

            async Task LockRoundAsync(int round)
            {
                currentRound = round;
                if (_isClosing)
                    throw new OperationCanceledException("测试窗口已关闭。");

                DirectUnlockStatus.Text = $"锁屏自检 {round}/{LockCycleTest.Rounds}。";
                long beforeRequest = Stopwatch.GetTimestamp();
                if (!WatchWake.LockWorkStation())
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                if (!await WaitForSelfTestAsync(() => watcher.LastLockNotification >= beforeRequest))
                    throw new InvalidOperationException("未收到 Windows 锁屏通知。");

                long firstWeakAt = Stopwatch.GetTimestamp();
                watcher.Update(firstWeakAt, 0, interrupted: false, [new(firstWeakAt, -90)]);

                double? intervalMs = round > 1
                    ? Stopwatch.GetElapsedTime(watcher.LastUnlockNotification, beforeRequest).TotalMilliseconds
                    : null;
                BluetoothLog.Write("lock_cycle_locked", new { round, intervalMs });
            }

            async Task UnlockRoundAsync(int round)
            {
                if (_isClosing)
                    throw new OperationCanceledException("测试窗口已关闭。");

                long lockedAt = watcher.LastLockNotification;
                if (watcher.LastUnlockNotification >= lockedAt)
                    throw new InvalidOperationException("系统在测试发起解锁前已经解锁。");

                // Leave room for the timer's rounding at the exact three-second boundary.
                await Task.Delay(100);
                long secondWeakAt = Stopwatch.GetTimestamp();
                watcher.Update(secondWeakAt, 0, interrupted: false, [new(secondWeakAt, -90)]);
                long requestedAt = Stopwatch.GetTimestamp();
                string status = watcher.Update(requestedAt, 0, interrupted: false, [new(requestedAt, -40)]);
                double lockedWaitMs = Stopwatch.GetElapsedTime(lockedAt, requestedAt).TotalMilliseconds;
                BluetoothLog.Write("lock_cycle_unlock_requested", new { round, lockedWaitMs, status });

                if (!await WaitForSelfTestAsync(() =>
                {
                    if (watcher.LastUnlockNotification >= requestedAt)
                        return true;
                    watcher.Update(Stopwatch.GetTimestamp(), 0, interrupted: false);
                    return false;
                }, timeoutSeconds: 15))
                {
                    string reason = broker.LastCredentialSent >= requestedAt
                        ? "Windows 登录组件已领取凭据，但未收到解锁通知。"
                        : "Windows 登录组件未领取本轮凭据。";
                    throw new InvalidOperationException(reason);
                }
                if (!await WaitForSelfTestAsync(() => broker.LastCredentialSent >= requestedAt, timeoutSeconds: 1))
                    throw new InvalidOperationException("收到解锁通知，但测试凭据未交给 Windows 登录组件。");

                double unlockTimeMs = Stopwatch.GetElapsedTime(requestedAt, watcher.LastUnlockNotification).TotalMilliseconds;
                BluetoothLog.Write("lock_cycle_round_passed", new { round, lockedWaitMs, unlockTimeMs });
            }
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("ui_self_test_failed", new { test = "lock-cycle", round = currentRound, ex.Message, ex.HResult });
        }
        finally
        {
            if (!_isClosing)
                Close();
        }
    }
}
