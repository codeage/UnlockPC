using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace UnlockPC;

public sealed partial class MainWindow : WinUIKit.ThemedWindow
{
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _unlockedSignalTimer;
    private readonly TrayIcon? _trayIcon;
    private readonly MainWindowPlacement? _windowPlacement;
    private readonly List<ObservationTrial> _results = [];
    private readonly ObservableCollection<AdvertisementRow> _advertisementRows = [];
    private AdvertisementHistory? _advertisementHistory;
    private PresenceScan? _scan;
    private WatchWake? _watchWake;
    private WatchWake? _directWatchWake;
    private bool _promptingForDirectUnlock;
    private ObservationTrial? _trial;
    private long _lastPoll;
    private int _deviceLoadVersion;
    private bool _removingWatch;
    private bool _readingIrk;
    private string? _lastWatchId;
    private PairedWatch? _selectedWatch;
    private const int ResultLimit = 200;
    private const double NavExpandThreshold = 760;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(nint hwnd, bool invert);

    public MainWindow() : this(showTrayIcon: true) { }

    internal MainWindow(bool showTrayIcon, WatchRuleStore? ruleStore = null)
    {
        InitializeComponent();
        InitializeInformationPages();
        if (_radarUiSettings.AnimationsEnabled)
            RadarMarker.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(300) };
        LockActivityItems.ItemsSource = _lockActivity;
        _watchRuleStore = ruleStore ?? WatchRuleStore.ForCurrentUser();
        LoadWatchRules();
        AdvertisementsList.ItemsSource = _advertisementRows;
        MatchingAdvertisementsSwitch.Toggled += (_, _) =>
        {
            _advertisementRows.Clear();
            RefreshAdvertisements();
        };
        AutoScrollAdvertisementsSwitch.Toggled += (_, _) =>
        {
            UpdateAdvertisementScrollMode();
            ScrollAdvertisementsToLatest();
        };
        RefreshAdvertisements();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.PreferredMinimumWidth = 800;
            presenter.PreferredMinimumHeight = 600;
        }

        InstallCaptionButtons(WindowRootGrid, StandardTitleBarHeight);
        void KeepTitleBarClearOfCaptionButtons() => AppTitleBar.Margin = new Thickness(48, 0, CaptionButtonsWidth, 0);
        KeepTitleBarClearOfCaptionButtons();
        CaptionButtonsChanged += KeepTitleBarClearOfCaptionButtons;

        RootNavigationView.Loaded += (_, _) => FlattenNavContentChrome();
        RootNavigationView.DisplayModeChanged += (_, _) => FlattenNavContentChrome();
        RootNavigationView.SizeChanged += (_, args) =>
        {
            var mode = args.NewSize.Width >= NavExpandThreshold
                ? NavigationViewPaneDisplayMode.Left : NavigationViewPaneDisplayMode.LeftCompact;
            if (RootNavigationView.PaneDisplayMode == mode)
                return;

            RootNavigationView.PaneDisplayMode = mode;
            RootNavigationView.IsPaneOpen = mode == NavigationViewPaneDisplayMode.Left;
        };

        WindowRootGrid.Loaded += (_, _) => ApplyWindowTheme(WindowRootGrid.ActualTheme);
        WindowRootGrid.ActualThemeChanged += (_, _) => ApplyWindowTheme(WindowRootGrid.ActualTheme);
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min((int)Math.Round(1040 * scale), display.WorkArea.Width),
            Math.Min((int)Math.Round(740 * scale), display.WorkArea.Height)));

        RootNavigationView.SelectedItem = showTrayIcon ? DeviceRadarItem : MonitorItem;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += async (_, _) =>
        {
            if (UsesBackgroundMonitor && _scan is null)
                await RefreshBackgroundMonitorAsync();
            else
                RefreshScan();
        };
        _unlockedSignalTimer = DispatcherQueue.CreateTimer();
        _unlockedSignalTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _unlockedSignalTimer.Tick += (_, _) =>
        {
            if (_scan is { } scan && _directWatchWake is { } watcher)
                ProcessDirectSignals(scan, watcher, whileUnlocked: true);
        };
        if (showTrayIcon)
        {
            _windowPlacement = new MainWindowPlacement(AppWindow);
            _trayIcon = new TrayIcon(this, OpenSettings, Close);
            StartScanButton.Visibility = StopScanButton.Visibility = Visibility.Collapsed;
        }
        AppWindow.Changed += OnAppWindowChanged;
        Closed += (_, _) =>
        {
            _isClosing = true;
            StopLockActivitySpeech();
            if (!_skipWindowPlacementSave)
                _windowPlacement?.Save();
            StopWatchConnectionStatus();
            AppWindow.Changed -= OnAppWindowChanged;
            _trayIcon?.Dispose();
            _watchBinding?.Cancel();
            EndScan();
        };
        ShowSnapshot(null);
#if UNLOCKPC_DEBUG_PAGE
        if (showTrayIcon && File.Exists(AppDataPaths.DebugPageUnlocked))
            ShowDebugPage(selectPage: false);
#endif
    }

    internal void RestoreWindowPlacement() => _windowPlacement?.Restore();

    protected override void OnCaptionCloseClicked()
    {
        if (_trayIcon is null)
            Close();
        else if (_trayIcon.EnsureVisible())
            AppWindow.Hide();
        else if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
            _windowPlacement?.Capture();

        if (args.DidPresenterChange && sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosing && sender.IsVisible &&
                    sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } &&
                    _trayIcon?.EnsureVisible() == true)
                {
                    sender.Hide();
                }
            });
        }
    }

    internal void OpenSettings()
    {
        if (_isClosing)
            return;

        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            bool wasMaximized = _windowPlacement?.IsMaximized == true;
            presenter.Restore();
            if (wasMaximized && presenter.State != OverlappedPresenterState.Maximized)
                presenter.Maximize();
        }

        if (!AppWindow.IsVisible)
            AppWindow.Show(activateWindow: false);

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(TopmostModalOver(this));
        if (GetForegroundWindow() != hwnd && !SetForegroundWindow(hwnd))
        {
            FlashWindow(hwnd, true);
            BluetoothLog.Write("settings_foreground_denied");
        }
    }

    private void FlattenNavContentChrome()
    {
        var host = FindDescendantByName(RootNavigationView, "ContentRoot");
        if (host is not null && FindDescendantByName(host, "ContentGrid") is Grid grid)
        {
            grid.BorderThickness = new Thickness(0);
            grid.CornerRadius = new CornerRadius(0);
            grid.Background = null;
        }

        if (FindDescendantByName(RootNavigationView, "RootSplitView") is SplitView paneHost)
        {
            paneHost.BorderThickness = new Thickness(0);
            paneHost.CornerRadius = new CornerRadius(0);
        }

        if (FindDescendantByName(RootNavigationView, "ShadowCaster") is Grid shadowCaster)
            shadowCaster.Visibility = Visibility.Collapsed;
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && element.Name == name)
                return element;
            if (FindDescendantByName(child, name) is { } found)
                return found;
        }

        return null;
    }

    private void ContentScrollViewer_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        double width = args.NewSize.Width - ContentScrollViewer.Padding.Left - ContentScrollViewer.Padding.Right;
        if (width > 0)
            ContentColumn.Width = Math.Min(960, width);
    }

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        bool radar = ReferenceEquals(args.SelectedItem, DeviceRadarItem);
        bool management = ReferenceEquals(args.SelectedItem, WatchManagementItem);
        bool monitor = ReferenceEquals(args.SelectedItem, MonitorItem);
        bool trials = ReferenceEquals(args.SelectedItem, TrialsItem);
        bool advanced = ReferenceEquals(args.SelectedItem, AdvancedItem);
        bool diagnostics = ReferenceEquals(args.SelectedItem, DiagnosticsItem);
        bool about = ReferenceEquals(args.SelectedItem, AboutItem);
#if UNLOCKPC_DEBUG_PAGE
        var debugVisibility = _debugItem is not null && ReferenceEquals(args.SelectedItem, _debugItem)
            ? Visibility.Visible : Visibility.Collapsed;
        _debugLockButton.Visibility = debugVisibility;
        if (_debugPage is not null)
            _debugPage.Visibility = debugVisibility;
#endif
        PageTitleText.Text = args.SelectedItem is NavigationViewItem { Content: string title }
            ? monitor ? "Apple Watch 在场检测" : title : string.Empty;
        PageDescriptionText.Visibility = monitor || trials || advanced ? Visibility.Visible : Visibility.Collapsed;
        PageDescriptionText.Text = advanced ? "自动锁定和解锁的设置会在修改后自动保存。" : trials
            ? "按手表状态比较已知在场时的广播漏检率。"
            : "通过蓝牙广播匹配指定手表，查看最后见到时间和信号强度。";
        DeviceRadarPage.Visibility = radar ? Visibility.Visible : Visibility.Collapsed;
        UpdateRadarDebugPanelVisibility();
        TrialsPage.Visibility = trials ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        WatchManagementPage.Visibility = management ? Visibility.Visible : Visibility.Collapsed;
        MonitorPage.Visibility = monitor ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = diagnostics ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
        UpdateRadarPulse(_scan is not null || _monitorScanning);
        ContentScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    internal async Task LoadPairedWatchesAsync(PairedWatch? newlyPaired = null, bool autoStart = true)
    {
        int version = ++_deviceLoadVersion;
        var previous = _selectedWatch;
        ReadSavedUnlock()?.Dispose();
        UpdateDirectUnlockControls();
        try
        {
            PairedWatch[] watches = await PairedWatch.ListAsync();
            if (_isClosing || version != _deviceLoadVersion)
                return;

            ScanError.IsOpen = false;
            if (newlyPaired is not null && !watches.Any(watch => IsSameId(watch.Id, newlyPaired.Id)))
                watches = [newlyPaired, ..watches];

            var savedWatch = watches.FirstOrDefault(watch => _savedUnlockDeviceId is { } id && IsSameId(watch.Id, id));
            if (savedWatch is null && _savedUnlockDeviceId is { } missingId)
                _ = ForgetIfUnpairedAsync(missingId);

            var appleWatches = watches.Where(watch => watch.HasWatchName).ToArray();
            var preferred = newlyPaired ?? previous;
            var selected = _hasSavedUnlock ? savedWatch
                : watches.FirstOrDefault(watch => preferred is not null && IsSameId(watch.Id, preferred.Id))
                    ?? (appleWatches.Length == 1 ? appleWatches[0] : null);
            if (_scan is not null && (_lastWatchId is null || selected is null || !IsSameId(_lastWatchId, selected.Id)))
                EndScan();
            SetSelectedWatch(selected);
            if (selected is null)
                ConnectionStatusText.Text = _hasSavedUnlock ? "当前未找到已绑定的 Windows 蓝牙设备。"
                    : appleWatches.Length > 1 ? "检测到多块已配对手表，请在绑定向导中选择。"
                    : "尚未选择 Windows 蓝牙设备。";

            if (UsesBackgroundMonitor)
            {
                StartScanButton.IsEnabled = StopScanButton.IsEnabled = false;
                _timer.Start();
                await RefreshBackgroundMonitorAsync();
            }
            else if (autoStart && selected is not null && (_trayIcon is null || _watchBinding is not null))
                StartScan(selected);
        }
        catch (Exception ex)
        {
            if (!_isClosing && version == _deviceLoadVersion)
            {
                if (newlyPaired is not null)
                {
                    SetSelectedWatch(newlyPaired);
                    StartScan(newlyPaired);
                }
                else
                {
                    ConnectionStatusText.Text = "无法读取 Windows 已配对设备。";
                    ShowWatchManagementStatus($"读取已配对设备失败（0x{ex.HResult:X8}）。", InfoBarSeverity.Error);
                }
            }
        }
        finally
        {
            if (!_isClosing && version == _deviceLoadVersion)
                UpdateWatchManagement();
        }
    }

    private static bool IsSameId(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);


    private async void RemoveWatch_Click(object sender, RoutedEventArgs args)
    {
        if (_isClosing || _removingWatch || _watchBinding is not null || _promptingForDirectUnlock || _readingIrk ||
            _selectedWatch is null && !_hasSavedUnlock)
            return;

        string? id = _selectedWatch?.Id ?? _savedUnlockDeviceId;
        string name = _selectedWatch?.Name ?? "已绑定手表";
        _removingWatch = true;
        UpdateWatchManagement();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RemoveWatchButton.XamlRoot,
                Title = "移除手表？",
                Content = $"将解除 Windows 与「{name}」的蓝牙配对，并清除 UnlockPC 中保存的手表密钥和解锁凭据。",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _isClosing)
                return;

            if (id is not null)
            {
                var device = await DeviceInformation.CreateFromIdAsync(id, PairedWatch.Properties,
                    DeviceInformationKind.AssociationEndpoint).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                if (_isClosing)
                    return;
                if (device?.Pairing.IsPaired == true)
                {
                    var result = await device.Pairing.UnpairAsync();
                    if (_isClosing)
                        return;
                    if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
                    {
                        ShowWatchManagementStatus($"Windows 未能移除「{name}」（{result.Status}）。", InfoBarSeverity.Error);
                        return;
                    }
                }
            }

            if (_scan is not null)
                EndScan();
            _pendingWatchBinding = null;
            if (_hasSavedUnlock)
                ForgetSavedUnlock();
            ResetBackgroundDisplay();
            SetSelectedWatch(null);
            await LoadPairedWatchesAsync(autoStart: false);
            if (!_isClosing)
                ShowWatchManagementStatus(_hasSavedUnlock ? "Windows 配对已解除，本地授权清除失败，请重试。" : "已移除手表。",
                    _hasSavedUnlock ? InfoBarSeverity.Error : InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            if (!_isClosing)
                ShowWatchManagementStatus($"无法移除「{name}」（0x{ex.HResult:X8}）。", InfoBarSeverity.Error);
        }
        finally
        {
            _removingWatch = false;
            if (!_isClosing)
                UpdateWatchManagement();
        }
    }

    private void SetSelectedWatch(PairedWatch? watch)
    {
        bool changed = _selectedWatch?.Id != watch?.Id;
        _selectedWatch = watch;
        if (changed)
            UpdateWatchConnectionStatus();
        StartScanButton.IsEnabled = _scan is null && watch is not null && !UsesBackgroundMonitor;
        UpdateWatchManagement();
        if (_scan is null)
            ShowConnection(watch?.IsConnected);
    }

    private void StartScan_Click(object sender, RoutedEventArgs args)
    {
        if (_selectedWatch is PairedWatch watch)
            StartScan(watch);
    }

    private void StartScan(PairedWatch watch)
    {
        if (_scan is not null || _isClosing || _removingWatch || UsesBackgroundMonitor)
            return;

        try
        {
            if (_lastWatchId is not null && !IsSameId(_lastWatchId, watch.Id))
            {
                _results.Clear();
                SummaryText.Text = "暂无完整测量。";
                CopyResultsButton.IsEnabled = false;
                CopyStatus.IsOpen = false;
            }

            _lastWatchId = watch.Id;
            ScanError.IsOpen = false;
            _scan = new PresenceScan(watch);
            _scan.Start();
            _ = RestoreDirectUnlockAsync(watch, _scan);
            UpdateWindowsHelloWake();
            _advertisementHistory = _scan.Advertisements;
            _advertisementRows.Clear();
            StartScanButton.IsEnabled = false;
            StopScanButton.IsEnabled = true;
            _lastPoll = Stopwatch.GetTimestamp();
            ShowTrialIdle("请选择手表状态并开始测量。");
            _timer.Start();
            RefreshScan();
        }
        catch (Exception ex)
        {
            EndScan();
            ShowScanError($"无法启动蓝牙扫描（0x{ex.HResult:X8}）。");
        }
    }

    private void StopScan_Click(object sender, RoutedEventArgs args) => EndScan();

    private async void StartIrk_Click(object sender, RoutedEventArgs args)
    {
        if (_readingIrk || _watchBinding is not null || _scan is not { } scan || _selectedWatch is not PairedWatch watch)
            return;

        if (_savedUnlockDeviceId is not { } id || !IsSameId(id, watch.Id))
        {
            IrkError.Message = "请先在「手表管理」绑定这块手表。";
            IrkError.IsOpen = true;
            return;
        }

        _readingIrk = true;
        UpdateWatchManagement();
        IrkError.IsOpen = false;
        RefreshScan();
        try
        {
            if (!await RestoreDirectUnlockAsync(watch, scan) && !_isClosing && ReferenceEquals(_scan, scan))
            {
                IrkError.Message = _directUnlockError ?? "蓝牙扫描已停止。请重新开始检测。";
                IrkError.IsOpen = true;
            }
        }
        finally
        {
            _readingIrk = false;
            if (!_isClosing)
            {
                UpdateWatchManagement();
                RefreshScan();
            }
        }
    }

    private void StopIrk_Click(object sender, RoutedEventArgs args)
    {
        StopDirectUnlock();
        _scan?.StopIrkObservation();
        UpdateWindowsHelloWake();
        RefreshScan();
    }

    private void EndScan(ScanSnapshot? snapshot = null)
    {
        _timer.Stop();
        ResetWatchOfflineReminder();
        StopDirectUnlock();
        var scan = _scan;
        _scan = null;
        UpdateWindowsHelloWake();
        try
        {
            snapshot ??= scan?.Read();
        }
        catch (Exception)
        {
            // Bluetooth can disappear while the scan is stopping.
        }
        finally
        {
            try
            {
                scan?.Dispose();
            }
            catch (Exception ex)
            {
                ShowScanError($"蓝牙扫描停止时发生错误（0x{ex.HResult:X8}）。");
            }
        }

        string message = _trial is null ? "请先在「在场检测」开启检测。" : "扫描已停止，本轮未计入结果。";
        ShowTrialIdle(message, canStart: false);
        StartScanButton.IsEnabled = _selectedWatch is PairedWatch;
        StopScanButton.IsEnabled = false;
        IrkError.IsOpen = false;
        ShowSnapshot(snapshot, scanning: false);
        RefreshAdvertisements();
    }

    private void RefreshScan()
    {
        if (_scan is not { } scan)
            return;

        try
        {
            var snapshot = scan.Read();
            if (snapshot.DeviceRemoved)
            {
                EndScan(snapshot);
                if (_lastWatchId is { } removedId)
                    _ = ForgetIfUnpairedAsync(removedId);
                SetSelectedWatch(null);
                ShowScanError("Windows 已移除这个配对设备。请在「手表管理」重新绑定。");
                return;
            }

            if (_directWatchWake is { IsSuspended: true })
                return;

            if (snapshot.Status != BluetoothLEAdvertisementWatcherStatus.Started)
            {
                long recoveryNow = Stopwatch.GetTimestamp();
                if (_directWatchWake is { } directWatch && directWatch.IsRecoveringScan(recoveryNow))
                {
                    DirectUnlockStatus.Text = "正在恢复蓝牙检测。";
                    if (snapshot.Status != BluetoothLEAdvertisementWatcherStatus.Stopping && directWatch.CanRetryScan(recoveryNow))
                    {
                        try { scan.RestartAfterResume(); }
                        catch (Exception ex)
                        {
                            BluetoothLog.Write("scan_resume_restart_failed", new { ex.HResult });
                        }
                    }
                    return;
                }

                EndScan(snapshot);
                ShowScanError(snapshot.Error == BluetoothError.RadioNotAvailable
                    ? "蓝牙无线电不可用。请检查 Windows 蓝牙和适配器状态。"
                    : $"蓝牙扫描未运行（{snapshot.Error?.ToString() ?? snapshot.Status.ToString()}）。");
                return;
            }

            ShowSnapshot(snapshot, scanning: true);
            RefreshAdvertisements();
            long now = Stopwatch.GetTimestamp();
            var pollGap = Stopwatch.GetElapsedTime(_lastPoll, now);
            _lastPoll = now;
            UpdateWatchOfflineReminder(snapshot, now, interrupted: pollGap > TimeSpan.FromSeconds(3));
            if (_watchWake is { } watchWake)
                WatchWakeStatus.Text = watchWake.Update(now, snapshot.LastNear, pollGap > TimeSpan.FromSeconds(3));
            RefreshDirectUnlock(scan, snapshot);
            if (_trial is null)
            {
                StartTrialButton.IsEnabled = true;
                return;
            }

            if (pollGap > TimeSpan.FromSeconds(3))
            {
                scan.CancelTrial();
                ShowTrialIdle("扫描监测中断，本轮未计入结果。");
                return;
            }

            if (scan.FinishTrial() is { } completed)
            {
                _results.Add(completed);
                if (_results.Count > ResultLimit)
                    _results.RemoveAt(0);

                UpdateResults();
                ShowTrialIdle("本轮测量已完成。");
                return;
            }

            double elapsed = Math.Clamp(_trial.ElapsedSeconds(now), 0, ObservationTrial.DurationSeconds);
            TrialProgress.Value = elapsed;
            TrialStatusText.Text = $"测量中 · {Math.Ceiling(ObservationTrial.DurationSeconds - elapsed):0} 秒后完成";
        }
        catch (Exception ex)
        {
            EndScan();
            ShowScanError($"蓝牙扫描中断（0x{ex.HResult:X8}）。");
        }
    }

    private void UpdateWindowsHelloWake()
    {
        if (!_watchRules.WakeWithWindowsHello || _scan is null || _directWatchWake is not null || _isClosing)
        {
            _watchWake?.Dispose();
            _watchWake = null;
            WatchWakeStatus.Text = !_watchRules.WakeWithWindowsHello ? "已关闭。"
                : _scan is null ? "已开启，等待开始手表检测。" : "等待本机控制台锁屏。";
            return;
        }

        if (_watchWake is not null)
            return;

        try
        {
            _watchWake = new WatchWake(WinRT.Interop.WindowNative.GetWindowHandle(this), recordActivity: RecordLockActivity);
            WatchWakeStatus.Text = "等待本机控制台锁屏。";
        }
        catch (Exception ex)
        {
            WatchWakeStatus.Text = $"无法监听 Windows 会话或显示状态（0x{ex.HResult:X8}）。";
            BluetoothLog.Write("watch_wake_registration_failed", new { ex.HResult });
        }
    }

    private void ShowScanError(string message)
    {
        ScanError.Message = message;
        ScanError.IsOpen = true;
    }

    private void RefreshAdvertisements()
    {
        bool matchesOnly = MatchingAdvertisementsSwitch.IsOn;
        long after = _advertisementRows.Count == 0 ? 0 : _advertisementRows[0].Sequence;
        if (_advertisementHistory?.ReadNew(matchesOnly, after) is { Length: > 0 } newRows)
        {
            UpdateAdvertisementScrollMode();
            foreach (var row in newRows)
            {
                if (_advertisementRows.Count == AdvertisementHistory.Limit)
                    _advertisementRows.RemoveAt(_advertisementRows.Count - 1);
                _advertisementRows.Insert(0, row);
            }
            ScrollAdvertisementsToLatest();
        }

        AdvertisementsStatusText.Text = _advertisementRows.Count switch
        {
            > 0 => $"最近 {_advertisementRows.Count:N0} 条{(matchesOnly ? "匹配" : "")}广播 · 最多保留 {AdvertisementHistory.Limit:N0} 条",
            _ when _scan is null => "开启在场检测后可查看广播。",
            _ when matchesOnly => "尚未收到匹配广播。",
            _ => "尚未收到蓝牙广播。"
        };
    }

    private void UpdateAdvertisementScrollMode()
    {
        if (AdvertisementsList.ItemsPanelRoot is ItemsStackPanel panel)
        {
            var mode = AutoScrollAdvertisementsSwitch.IsOn
                ? ItemsUpdatingScrollMode.KeepScrollOffset
                : ItemsUpdatingScrollMode.KeepItemsInView;
            if (panel.ItemsUpdatingScrollMode != mode)
                panel.ItemsUpdatingScrollMode = mode;
        }
    }

    private void ScrollAdvertisementsToLatest()
    {
        if (!AutoScrollAdvertisementsSwitch.IsOn)
            return;

        for (DependencyObject? element = AdvertisementsList.ItemsPanelRoot;
             element is not null && !ReferenceEquals(element, AdvertisementsList);
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is ScrollViewer viewer)
            {
                if (viewer.VerticalOffset > 0)
                    viewer.ChangeView(null, 0, null, disableAnimation: true);
                return;
            }
        }
    }

    private void ShowSnapshot(ScanSnapshot? snapshot, bool scanning = false)
    {
        UpdateDeviceRadar(snapshot, scanning);
        ShowWatchBroadcast(scanning ? snapshot?.WatchBroadcast ?? default : default, DateTimeOffset.UtcNow, scanning);

        if (!scanning)
            PresenceText.Text = "检测已停止";
        else if (snapshot?.MatchAge is null)
            PresenceText.Text = "等待匹配广播";
        else
            PresenceText.Text = snapshot.Value.MatchAge <= TimeSpan.FromSeconds(ObservationTrial.WindowSeconds)
                ? "最近 10 秒内检测到手表" : "最近 10 秒内未收到匹配广播";

        if (snapshot is { } scan)
            ShowConnection(scan.IsConnected);

        LastSeenText.Text = snapshot?.LastMatch is { } last
            ? $"最后见到  {last.SeenAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "最后见到  —";
#if UNLOCKPC_DEBUG_PAGE
        RssiText.Text = snapshot?.LastMatch is { } sample ? $"RSSI  {sample.Rssi} dBm" : "RSSI  —";
#endif
        PacketCountText.Text = $"匹配 {snapshot?.Matched ?? 0:N0} 条 · 共收到 {snapshot?.Total ?? 0:N0} 条 BLE 广播";

        var irk = scanning ? snapshot?.Irk : null;
        StartIrkButton.IsEnabled = scanning && irk is null && _hasSavedUnlock && !_readingIrk;
        StopIrkButton.IsEnabled = irk is not null;
        if (irk is { } observation)
        {
            string lastSeen = observation.LastSeen?.ToLocalTime().ToString("HH:mm:ss") ?? "—";
            IrkStatusText.Text = $"IRK 命中 {observation.Matched:N0} 条 · 不同 RPA {observation.DistinctAddresses:N0} 个 · 最近命中 {lastSeen}";
        }
        else
        {
            IrkStatusText.Text = _readingIrk
                ? "请确认 Windows 管理员提示。正在读取所选设备的配对密钥…"
                : scanning ? "从 Windows 配对记录自动读取。密钥仅用于本轮扫描。" : "请先开启在场检测。";
        }
    }

    private void ShowConnection(bool? connected) => ConnectionStatusText.Text = connected switch
    {
        true => "Windows 蓝牙：已连接",
        false => "Windows 蓝牙：未连接",
        null => "Windows 蓝牙：连接状态未知"
    };

    private void StartTrial_Click(object sender, RoutedEventArgs args)
    {
        if (ScenarioBox.SelectedItem is not ComboBoxItem)
            return;
        var scenario = (WatchScenario)ScenarioBox.SelectedIndex;
        var trial = UsesBackgroundMonitor && _monitorScanning
            ? new ObservationTrial(scenario, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp())
            : _scan?.StartTrial(scenario);
        if (trial is null)
            return;

        _trial = trial;
        _lastPoll = Stopwatch.GetTimestamp();
        ScenarioBox.IsEnabled = StartTrialButton.IsEnabled = false;
        CancelTrialButton.IsEnabled = true;
        TrialProgress.Value = 0;
        TrialProgress.Visibility = Visibility.Visible;
        TrialStatusText.Text = "测量中 · 60 秒后完成";
    }

    private void CancelTrial_Click(object sender, RoutedEventArgs args)
    {
        _scan?.CancelTrial();
        ShowTrialIdle("本轮已取消。");
    }

    private void ShowTrialIdle(string message, bool canStart = true)
    {
        _trial = null;
        ScenarioBox.IsEnabled = true;
        StartTrialButton.IsEnabled = canStart;
        CancelTrialButton.IsEnabled = false;
        TrialProgress.Visibility = Visibility.Collapsed;
        TrialStatusText.Text = message;
    }

    private static string ScenarioLabel(WatchScenario scenario) => scenario switch
    {
        WatchScenario.AwakeUnlocked => "亮屏且已解锁",
        WatchScenario.ScreenOffUnlocked => "熄屏且已解锁",
        WatchScenario.Relocked => "重新锁定",
        WatchScenario.RestartedNotUnlocked => "重启后尚未解锁",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    private void UpdateResults()
    {
        SummaryText.Text = string.Join(Environment.NewLine, _results.GroupBy(trial => trial.Scenario)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                int windows = group.Count() * ObservationTrial.WindowCount;
                int misses = group.Sum(trial => trial.Windows.Count(window => window.MatchCount == 0));
                return $"{ScenarioLabel(group.Key)}  ·  漏检 {misses}/{windows}（{(double)misses / windows:P1}）";
            }));
        CopyResultsButton.IsEnabled = true;
    }

    private void CopyResults_Click(object sender, RoutedEventArgs args)
    {
        var csv = new StringBuilder("手表状态,轮次开始时间,窗口起始秒,匹配次数,最后匹配时间,RSSI_dBm\r\n");
        foreach (var trial in _results)
        {
            for (int index = 0; index < trial.Windows.Length; index++)
            {
                var window = trial.Windows[index];
                csv.Append(CultureInfo.InvariantCulture,
                    $"{ScenarioLabel(trial.Scenario)},{trial.StartedAt:O},{index * ObservationTrial.WindowSeconds},{window.MatchCount},{window.LastMatch?.SeenAt:O},{window.LastMatch?.Rssi}\r\n");
            }
        }

        try
        {
            var data = new DataPackage();
            data.SetText(csv.ToString());
            Clipboard.SetContentWithOptions(data, new ClipboardContentOptions
            {
                IsAllowedInHistory = false,
                IsRoamable = false
            });
            Clipboard.Flush();
            CopyStatus.Severity = InfoBarSeverity.Success;
            CopyStatus.Message = "已复制 CSV。";
        }
        catch (Exception)
        {
            CopyStatus.Severity = InfoBarSeverity.Error;
            CopyStatus.Message = "复制失败，请重试。";
        }

        CopyStatus.IsOpen = true;
    }
}
