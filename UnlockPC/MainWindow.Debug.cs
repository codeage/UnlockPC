using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private NavigationViewItem? _debugItem;
    private StackPanel? _debugPage;
    private readonly Button _debugLockButton = new()
    {
        Content = "锁定",
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed
    };
    private readonly InfoBar _debugRuleStatus = new() { IsClosable = false };
    private readonly TextBlock _debugLockRuleDescription = new() { TextWrapping = TextWrapping.Wrap };
    private StackPanel _debugRulePanel = null!;
    private NumberBox _unlockDelayBox = null!;
    private NumberBox _returnRssiBox = null!;
    private NumberBox _returnWindowBox = null!;
    private NumberBox _returnPacketCountBox = null!;
    private NumberBox _weakRssiBox = null!;
    private NumberBox _veryWeakRssiBox = null!;
    private bool _debugDetailsEnabled;
    private int _versionClicks;
    private long _lastVersionClick;

    partial void InitializeDebugPage()
    {
        _debugRulePanel = new StackPanel { Spacing = 12 };
        _debugRulePanel.Children.Add(_debugRuleStatus);
        _debugRulePanel.Children.Add(new TextBlock { Text = "锁定与解锁参数" });
        _unlockDelayBox = AddRuleNumberBox("锁屏后最短解锁等待（秒）", 0, 300,
            (rules, value) => rules with { UnlockDelaySeconds = value });
        _returnRssiBox = AddRuleNumberBox("强信号最低 RSSI（dBm）", -126, -1,
            (rules, value) => rules with { ReturnRssi = value });
        _returnWindowBox = AddRuleNumberBox("强信号统计窗口（秒）", 1, 30,
            (rules, value) => rules with { ReturnWindowSeconds = value });
        _returnPacketCountBox = AddRuleNumberBox("窗口内所需广播数", 1, 10,
            (rules, value) => rules with { ReturnPacketCount = value });
        _weakRssiBox = AddRuleNumberBox("弱信号阈值（dBm）", -126, -1,
            (rules, value) => rules with { WeakRssi = value });
        _veryWeakRssiBox = AddRuleNumberBox("极弱信号阈值（dBm）", -126, -1,
            (rules, value) => rules with { VeryWeakRssi = value });
        _debugRulePanel.Children.Add(_debugLockRuleDescription);

        Grid.SetColumn(_debugLockButton, 1);
        PageTitleRow.Children.Add(_debugLockButton);
        _debugLockButton.Click += async (_, _) => await SetDebugPageUnlockedAsync(false);

        AboutVersionText.IsDoubleTapEnabled = false;
        AboutVersionText.Tapped += async (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastVersionClick == 0 || Stopwatch.GetElapsedTime(_lastVersionClick, now) > TimeSpan.FromSeconds(2))
                _versionClicks = 0;
            _lastVersionClick = now;
            if (++_versionClicks < 7)
                return;
            _versionClicks = 0;
            await SetDebugPageUnlockedAsync(true);
        };
    }

    private async Task SetDebugPageUnlockedAsync(bool unlocked)
    {
        try
        {
            string path = AppDataPaths.DebugPageUnlocked;
            if (unlocked)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var marker = File.Open(path, FileMode.OpenOrCreate, FileAccess.Write);
            }
            else
                File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = PageTitleRow.XamlRoot,
                Title = "无法保存调试页状态",
                Content = $"请检查应用数据目录的访问权限（0x{error.HResult:X8}）。",
                CloseButtonText = "确定"
            };
            await dialog.ShowAsync();
            return;
        }

        if (unlocked)
            ShowDebugPage();
        else if (_debugItem is not null)
        {
            SetDebugDetailsEnabled(false);
            RootNavigationView.SelectedItem = AboutItem;
            _debugItem.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowDebugPage(bool selectPage = true)
    {
        if (_debugItem is null)
        {
            _debugItem = new NavigationViewItem
            {
                Content = "调试",
                Icon = new FontIcon { FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"), Glyph = "\uEBE8" }
            };
            _debugPage = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
            _debugPage.Children.Add(new TextBlock
            {
                Text = "已显示雷达的 RSSI 和估算距离、设备检测页右上角的手表状态和锁定与解锁流程及原始广播。",
                TextWrapping = TextWrapping.Wrap
            });
            _debugPage.Children.Add(_debugRulePanel);
            Grid.SetRow(_debugPage, 1);
            ContentColumn.Children.Add(_debugPage);
            RootNavigationView.MenuItems.Insert(RootNavigationView.MenuItems.IndexOf(DiagnosticsItem), _debugItem);
        }

        SetDebugDetailsEnabled(true);
        _debugItem.Visibility = Visibility.Visible;
        if (selectPage)
            RootNavigationView.SelectedItem = _debugItem;
    }

    private void SetDebugDetailsEnabled(bool enabled)
    {
        _debugDetailsEnabled = enabled;
        var visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        RssiText.Visibility = RawAdvertisementsCard.Visibility = visibility;
        UpdateRadarDebugPanelVisibility();
        UpdateDeviceRadar(_scan?.Read(), _scan is not null);
    }

    private NumberBox AddRuleNumberBox(string header, int minimum, int maximum,
        Func<WatchRuleSettings, int, WatchRuleSettings> change)
    {
        var box = new NumberBox
        {
            Header = header,
            Minimum = minimum,
            Maximum = maximum,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        box.ValueChanged += (_, args) =>
        {
            if (_updatingWatchRuleControls)
                return;
            if (!double.IsInteger(args.NewValue) || args.NewValue < minimum || args.NewValue > maximum)
            {
                UpdateWatchRuleControls();
                ShowWatchRulesStatus("请输入范围内的整数。", InfoBarSeverity.Warning);
                return;
            }
            SaveWatchRules(change(_watchRules, (int)args.NewValue));
        };
        _debugRulePanel.Children.Add(box);
        return box;
    }

    partial void UpdateDebugRuleControls()
    {
        _unlockDelayBox.Value = _watchRules.UnlockDelaySeconds;
        _returnRssiBox.Value = _watchRules.ReturnRssi;
        _returnWindowBox.Value = _watchRules.ReturnWindowSeconds;
        _returnPacketCountBox.Value = _watchRules.ReturnPacketCount;
        _weakRssiBox.Value = _watchRules.WeakRssi;
        _veryWeakRssiBox.Value = _watchRules.VeryWeakRssi;
        _debugLockRuleDescription.Text = $"在 18 秒内，至少收到两条间隔达到 3 秒的极弱广播（≤ {_watchRules.VeryWeakRssi} dBm），" +
            $"或至少三条首尾间隔达到 6 秒的弱广播（≤ {_watchRules.WeakRssi} dBm），且本机连续 10 秒无输入，才会请求锁屏。";
    }

    partial void SyncDebugRuleStatus()
    {
        _debugRuleStatus.Message = WatchRulesStatus.Message;
        _debugRuleStatus.Severity = WatchRulesStatus.Severity;
        _debugRuleStatus.IsOpen = WatchRulesStatus.IsOpen;
    }
}
