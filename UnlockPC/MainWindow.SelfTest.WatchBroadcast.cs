using System.Diagnostics;
using Microsoft.UI.Xaml;
#if UNLOCKPC_DEBUG_PAGE
using Windows.Foundation;
#endif

namespace UnlockPC;

public sealed partial class MainWindow
{
    internal async Task RunWatchBroadcastSelfTestAsync()
    {
        try
        {
            if (!await WaitForSelfTestAsync(() => WindowRootGrid.XamlRoot is not null))
                throw new InvalidOperationException("测试窗口未加载。");

            RootNavigationView.SelectedItem = DeviceRadarItem;
#if UNLOCKPC_DEBUG_PAGE
            SetDebugDetailsEnabled(true);
#endif
            var now = DateTimeOffset.UtcNow;
            AdvertisementRow.DescribeAppleMessages([0x10, 0x05, 0x29, 0x18, 0x04, 0xD8, 0x7F], out var sample);
            var broadcast = default(AppleWatchBroadcast).Observe(sample, now, irkMatch: true);
            ShowWatchBroadcast(broadcast, now, scanning: true);
            if (WatchLockValue.Text != "未知" ||
                !WatchNearbyInfoText.Text.Contains("活动码 0x09（原始 0x29）", StringComparison.Ordinal) ||
                WatchAuthTagText.Text != "0x10 · 3 字节")
                throw new InvalidOperationException("状态卡未正确显示截图中的 Nearby Info 字段。");

#if UNLOCKPC_DEBUG_PAGE
            if (!await WaitForSelfTestAsync(() => WatchBroadcastCard.ActualHeight > 0))
                throw new InvalidOperationException("未显示调试状态卡。");
            Point origin = LockActivityCard.TransformToVisual(WindowRootGrid).TransformPoint(new Point(0, 0));
            Point watchOrigin = WatchBroadcastCard.TransformToVisual(WindowRootGrid).TransformPoint(new Point(0, 0));
            double rightInset = WindowRootGrid.ActualWidth - origin.X - LockActivityCard.ActualWidth;
            if (origin.X <= WindowRootGrid.ActualWidth / 2 || rightInset < 0 || rightInset > 36 ||
                watchOrigin.Y < 48 || watchOrigin.Y > 88)
                throw new InvalidOperationException($"调试状态区未排在右上角（{watchOrigin.X:F0}, {watchOrigin.Y:F0}）。");

            if (Math.Abs(watchOrigin.X - origin.X) > 1 ||
                watchOrigin.Y + WatchBroadcastCard.ActualHeight >= origin.Y ||
                WatchBroadcastScroll.ActualHeight <= 0)
                throw new InvalidOperationException("手表状态未在流程记录上方独立滚动。");

#else
            if (RadarDebugPanel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("发布版本不应显示调试状态区。");
#endif

            (byte Flags, string LockState)[] lockCases =
            [
                (0x98, "疑似已解锁"),
                (0x90, "疑似已解锁"),
                (0x28, "疑似锁定"),
                (0xB8, "线索冲突")
            ];
            foreach (var (flags, expected) in lockCases)
            {
                AdvertisementRow.DescribeAppleMessages([0x10, 0x05, 0x2D, flags, 1, 2, 3], out var packet);
                ShowWatchBroadcast(broadcast.Observe(packet, now, irkMatch: true), now, scanning: true);
                if (WatchLockValue.Text != expected || WatchWristValue.Text != "未知")
                    throw new InvalidOperationException($"数据标志 0x{flags:X2} 的锁定或在腕状态显示错误。");
            }

            AdvertisementRow.DescribeAppleMessages([0x10, 0x05, 0x1A, 0x10, 1, 2, 3, 0x0B, 0x03, 0, 0, 0x03], out var conflicted);
            broadcast = broadcast.Observe(conflicted, now.AddSeconds(1), irkMatch: true);
            ShowWatchBroadcast(broadcast, now.AddSeconds(1), scanning: true);
            if (WatchWristValue.Text != "线索冲突" || WatchLockValue.Text != "未知" ||
                !WatchMagicSwitchText.Text.Contains("0x03 · 离腕", StringComparison.Ordinal))
                throw new InvalidOperationException("Magic Switch 与活动码冲突时显示错误。");

            ShowWatchBroadcast(broadcast, now.AddSeconds(22), scanning: true);
            if (!WatchNearbySourceText.Text.Contains("已过期", StringComparison.Ordinal) || WatchWristValue.Text != "未知")
                throw new InvalidOperationException("过期字段仍被当作当前状态。");

            AdvertisementRow.DescribeAppleMessages([0x10, 0x06, 0x09, 0xFF, 1, 2, 3, 4, 0x0B, 0x03, 0, 0, 0x3F], out var full);
            ShowWatchBroadcast(broadcast.Observe(full, now.AddSeconds(22), irkMatch: true), now.AddSeconds(22), scanning: true);
            if (WatchWatchUnlockFlagText.Text != "0x40 · 置位")
                throw new InvalidOperationException("手表状态卡未完整显示广播字段。");

#if UNLOCKPC_DEBUG_PAGE
            double cardMaxHeight = WatchBroadcastCard.MaxHeight;
            WatchBroadcastCard.MaxHeight = 125;
            if (!await WaitForSelfTestAsync(() => WatchBroadcastScroll.ScrollableHeight > 0))
                throw new InvalidOperationException("状态面板空间不足时无法滚动查看广播字段。");
            WatchBroadcastCard.MaxHeight = cardMaxHeight;
#endif

            AdvertisementRow.DescribeAppleMessages([0x10, 0x05, 0x1A, 0x90], out var truncated);
            if (truncated != default)
                throw new InvalidOperationException("截断广播被当成有效字段。");

            long tick = Stopwatch.GetTimestamp();
            RecordLockActivity(new LockActivityEvent(now, tick, LockActivityPhase.Start, "强信号达标"));
            RecordLockActivity(new LockActivityEvent(now.AddSeconds(2), tick + 2 * Stopwatch.Frequency, LockActivityPhase.Step, "加密验证通过"));
            RecordLockActivity(new LockActivityEvent(now.AddSeconds(3), tick + 3 * Stopwatch.Frequency, LockActivityPhase.Finish, "Windows 确认解锁"));
            if (_lockActivity[^2].Timestamp != now.AddSeconds(2).ToLocalTime().ToString("HH:mm:ss.fff") ||
                !_lockActivity[^2].Message.Contains($"距上一步 {2.0:F2} 秒") ||
                !_lockActivity[^1].Message.Contains($"距上一步 {1.0:F2} 秒 · 总耗时 {3.0:F2} 秒"))
                throw new InvalidOperationException("过程记录的时间戳或耗时计算错误。");
#if UNLOCKPC_DEBUG_PAGE
            for (int i = 0; i < 24; i++)
                RecordLockActivity(LockActivityEvent.Now(LockActivityPhase.Note, "Windows 会话状态变化"));
            if (!await WaitForSelfTestAsync(() => LockActivityScroll.ScrollableHeight > 0 &&
                Math.Abs(LockActivityScroll.VerticalOffset - LockActivityScroll.ScrollableHeight) <= 12))
                throw new InvalidOperationException("新增过程记录后没有滚动到末尾。");

            RootNavigationView.SelectedItem = MonitorItem;
            if (!await WaitForSelfTestAsync(() => RadarDebugPanel.Visibility == Visibility.Collapsed))
                throw new InvalidOperationException("离开设备检测页后状态面板仍可见。");

#endif
            BluetoothLog.Write("ui_self_test_passed", new { test = "watch-broadcast" });
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("ui_self_test_failed", new { test = "watch-broadcast", ex.Message, ex.HResult });
        }
        finally
        {
            Close();
        }
    }
}
