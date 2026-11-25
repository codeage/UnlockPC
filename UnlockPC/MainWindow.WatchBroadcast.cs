using Microsoft.UI.Xaml;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private void ShowWatchBroadcast(AppleWatchBroadcast broadcast, DateTimeOffset now, bool scanning)
    {
        var (wrist, lockState, screen) = broadcast.InferState(now);
        WatchBroadcastStoppedText.Visibility = scanning ? Visibility.Collapsed : Visibility.Visible;
        WatchBroadcastSummary.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        WatchWristValue.Text = wrist;
        WatchLockValue.Text = lockState;
        WatchScreenValue.Text = screen;
        WatchNearbyDetails.Visibility = broadcast.Nearby is null ? Visibility.Collapsed : Visibility.Visible;

        if (broadcast.Nearby is { } nearby)
        {
            var info = nearby.Value;
            string tag = !info.HasFlag(0x10) ? "未标记" : info.PayloadLength >= 2 + info.AuthenticationTagLength
                ? $"{info.AuthenticationTagLength} 字节" : "数据不完整";
            string Flag(int mask) => $"0x{mask:X2} · {(info.HasFlag(mask) ? "置位" : "清零")}";
            string otherFlags = (info.HasFlag(0x01) ? "0x01 · AirPods／亮屏线索\n" : "") +
                (info.HasFlag(0x04) ? "0x04 · Wi‑Fi 线索\n" : "") +
                (info.HasFlag(0x08) ? "0x08 · 含义未知\n" : "");
            WatchNearbySourceText.Text = nearby.FormatSource(now);
            WatchNearbyInfoText.Text = $"活动码 0x{info.Action:X2}（原始 0x{info.ActivityByte:X2}）\n" +
                $"论文释义 · {info.ActionDescription}\n数据标志 0x{info.Flags:X2}";
            WatchLockFlagText.Text = Flag(0x20);
            WatchAutoUnlockFlagText.Text = Flag(0x80);
            WatchWatchUnlockFlagText.Text = Flag(0x40);
            WatchAuthTagText.Text = $"0x10 · {tag}";
            WatchOtherFlagsText.Text = otherFlags.TrimEnd('\n');
            WatchOtherFlagsText.Visibility = otherFlags.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            WatchNearbySourceText.Text = "未收到";
        }

        WatchMagicSwitchText.Text = broadcast.MagicSwitch is { } magic
            ? $"0x{magic.Value:X2} · {AppleWatchBroadcast.DescribeWristConfidence(magic.Value)}\n{magic.FormatSource(now)}"
            : "未收到";
    }
}
