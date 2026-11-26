using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Bluetooth.Advertisement;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _watchBinding;
    private PairedWatch? _pendingWatchBinding;

    private async void StartPairing_Click(object sender, RoutedEventArgs args) => await StartPairingAsync();

    internal async Task StartPairingAsync()
    {
        if (_isClosing || _watchBinding is not null || _removingWatch || _promptingForDirectUnlock || _readingIrk)
            return;

        RootNavigationView.SelectedItem = WatchManagementItem;
        ReadSavedUnlock()?.Dispose();
        UpdateWatchManagement();
        if (_hasSavedUnlock || WindowRootGrid.XamlRoot is not { } root)
            return;

        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        _watchBinding = cancellation;
        UpdateWatchManagement();
        WatchManagementStatus.IsOpen = false;

        var view = new WatchBindingView();
        var selector = view.ExistingDevices;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "绑定手表",
            Content = new ScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "取消"
        };
        PairedWatch? watch = null;
        Task operation = Task.CompletedTask;
        bool finished = false;
        bool enableAutoUnlock = false;
        bool retryPairing = false;
        bool chooseExisting = false;
        PairedWatch[] pairedDevices = [];

        void ShowStep(int step, string title, string instructions, params string[] glyphs)
        {
            view.StepLabel.Text = $"第 {step} 步，共 3 步";
            view.StepLabel.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
            view.StepTitle.Text = title;
            view.Directions.Text = instructions;
            view.Status.Text = "";
            view.Failure.IsOpen = false;
            selector.Visibility = Visibility.Collapsed;
            dialog.PrimaryButtonText = dialog.SecondaryButtonText = "";
            view.Activity.Visibility = step == 0 ? Visibility.Collapsed : Visibility.Visible;
            view.Illustration.Children.Clear();
            foreach (string glyph in glyphs)
            {
                bool arrow = glyph == "\uE76C";
                var icon = new FontIcon
                {
                    Style = (Style)view.Resources["BindingIllustrationIconStyle"],
                    Glyph = glyph,
                    FontSize = arrow ? 14 : 28
                };
                view.Illustration.Children.Add(arrow ? icon : new Border
                {
                    Style = (Style)view.Resources["BindingIllustrationTileStyle"],
                    Child = icon
                });
            }
        }

        void ShowExistingDevices()
        {
            retryPairing = false;
            ShowStep(1, "蓝牙配对", "从 Windows 已配对设备中选择你的手表。", "\uE916", "\uE76C", "\uE702");
            selector.ItemsSource = pairedDevices;
            selector.Visibility = Visibility.Visible;
            view.Activity.Visibility = Visibility.Collapsed;
            dialog.PrimaryButtonText = "使用所选手表";
            dialog.IsPrimaryButtonEnabled = selector.SelectedItem is PairedWatch;
            dialog.SecondaryButtonText = "配对新手表";
        }

        void ShowNewPairing()
        {
            ShowStep(1, "蓝牙配对", $"在 Apple Watch 上解锁并打开「设置 → 蓝牙」。\n如果手表曾与这台电脑配对，必须先在手表上忽略此电脑。\n然后选择「{Environment.MachineName}」开始配对。如果手表和 Windows 弹出配对提示，请核对并确认。", "\uE916", "\uE76C", "\uE702", "\uE76C", "\uE977");
            view.Status.Text = "正在启动蓝牙配对广播…";
            retryPairing = true;
            chooseExisting = false;
            if (pairedDevices.Length > 0)
                dialog.SecondaryButtonText = "使用已配对设备";
        }

        void ShowReadKey(PairedWatch device)
        {
            _pendingWatchBinding = device;
            ShowStep(2, "读取配对密钥",
                $"Windows 已配对「{device.Name}」。正在准备由 IRK 服务识别这块手表的蓝牙广播。",
                "\uE7EF", "\uE76C", "\uE8D7");
        }

        void ShowFailure(Exception ex)
        {
            BluetoothLog.Write("watch_binding_failed", new { type = ex.GetType().Name, ex.HResult });
            view.Activity.Visibility = Visibility.Collapsed;
            view.Failure.Message = ex is InvalidOperationException ? ex.Message : $"无法完成手表绑定（0x{ex.HResult:X8}）。";
            view.Failure.IsOpen = true;
            dialog.PrimaryButtonText = "重试";
            dialog.IsPrimaryButtonEnabled = true;
        }

        async Task<bool> PrepareInitialStepAsync()
        {
            try
            {
                pairedDevices = await PairedWatch.ListAsync(token);
                token.ThrowIfCancellationRequested();
                var watches = pairedDevices.Where(device => device.HasWatchName).ToArray();
                var pending = _pendingWatchBinding;
                watch = pending is null ? null : pairedDevices.FirstOrDefault(device => IsSameId(device.Id, pending.Id));
                watch ??= watches.Length == 1 ? watches[0] : null;
                if (watch is not null)
                {
                    ShowReadKey(watch);
                    return true;
                }
                else if (watches.Length > 1)
                {
                    ShowExistingDevices();
                    return false;
                }
                else
                    ShowNewPairing();

                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_isClosing && !token.IsCancellationRequested)
                {
                    ShowStep(1, "蓝牙配对", "无法读取 Windows 中已有的配对。", "\uE916", "\uE76C", "\uE702");
                    ShowFailure(ex);
                }
            }

            return false;
        }

        async Task RunAsync(bool initialize = false)
        {
            try
            {
                dialog.IsPrimaryButtonEnabled = false;
                if (initialize && !await PrepareInitialStepAsync())
                    return;
                token.ThrowIfCancellationRequested();

                if (retryPairing)
                {
                    watch = await PairWatchAsync(message => view.Status.Text = message, token);
                    token.ThrowIfCancellationRequested();
                    if (chooseExisting)
                    {
                        ShowExistingDevices();
                        return;
                    }
                    if (watch is null)
                        throw new InvalidOperationException(view.Status.Text);

                    retryPairing = false;
                }

                if (watch is null)
                    return;

                ShowReadKey(watch);
                await BindWatchAsync(watch, () => ShowStep(3, "验证并保存",
                    "请保持手表在电脑附近。IRK 服务验证手表广播后，UnlockPC 会保存设备绑定。", "\uE702", "\uE76C", "\uE74E"), token);

                _pendingWatchBinding = null;
                finished = true;
                ShowStep(0, "手表已绑定", $"已绑定「{watch.Name}」。要开启自动解锁吗？开启后，需要输入当前 Windows 账户的登录密码。", "\uE916", "\uE76C", "\uE73E");
                dialog.PrimaryButtonText = "开启自动解锁";
                dialog.IsPrimaryButtonEnabled = true;
                dialog.CloseButtonText = "暂不开启";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!_isClosing && !token.IsCancellationRequested)
                    ShowFailure(ex);
            }
        }

        selector.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = selector.SelectedItem is PairedWatch;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (finished)
                return;
            args.Cancel = true;
            if (!operation.IsCompleted)
                return;
            if (selector.Visibility == Visibility.Visible)
            {
                watch = selector.SelectedItem as PairedWatch;
                if (watch is not null)
                    operation = RunAsync();
                return;
            }

            if (retryPairing)
                ShowNewPairing();

            operation = RunAsync(initialize: !retryPairing && watch is null);
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (selector.Visibility == Visibility.Visible && operation.IsCompleted)
            {
                ShowNewPairing();
                operation = RunAsync();
            }
            else if (!operation.IsCompleted)
            {
                chooseExisting = true;
                _pairing?.Cancel();
            }
            else
            {
                ShowExistingDevices();
            }
        };
        dialog.Closing += (_, _) => cancellation.Cancel();

        try
        {
            bool startOnOpen = await PrepareInitialStepAsync();
            if (token.IsCancellationRequested || _isClosing)
                return;

            if (startOnOpen)
                dialog.Opened += (_, _) => operation = RunAsync();
            enableAutoUnlock = await dialog.ShowAsync() == ContentDialogResult.Primary && finished;
        }
        catch (Exception ex)
        {
            if (!_isClosing)
                ShowWatchManagementStatus($"无法打开绑定向导（0x{ex.HResult:X8}）。", InfoBarSeverity.Error);
        }
        finally
        {
            cancellation.Cancel();
            await operation;
            _watchBinding = null;
            if (!_isClosing)
            {
                UpdateDirectUnlockControls();
                if (finished)
                    ShowWatchManagementStatus("手表已绑定。", InfoBarSeverity.Success);
            }
        }

        if (finished && !_isClosing)
        {
            SaveWatchRules(_watchRules with { AutoUnlock = false });
            if (enableAutoUnlock)
                await ConfigureWatchAsync(enableAutoUnlock: true);
        }
    }

    private async Task BindWatchAsync(PairedWatch watch, Action showVerification, CancellationToken token)
    {
        PresenceScan? scan = null;
        bool observing = false;
        bool saved = false;
        try
        {
            if (_scan is not null && (_lastWatchId is null || !IsSameId(_lastWatchId, watch.Id)))
                EndScan();
            await LoadPairedWatchesAsync(watch);
            token.ThrowIfCancellationRequested();
            scan = _scan;
            if (scan is null || _lastWatchId is null || !IsSameId(_lastWatchId, watch.Id))
                throw new InvalidOperationException("无法启动手表检测，请检查 Windows 蓝牙后重试。");

            scan.StopIrkObservation();
            observing = await scan.StartIrkObservationAsync(watch.Address, token);
            if (!observing)
                throw new InvalidOperationException("蓝牙检测已停止，请检查 Windows 蓝牙后重试。");

            showVerification();
            long started = Stopwatch.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_scan, scan) ||
                    scan.Read() is not { DeviceRemoved: false, Status: BluetoothLEAdvertisementWatcherStatus.Started, Irk: not null } snapshot)
                    throw new InvalidOperationException("验证时蓝牙检测中断，请检查配对后重试。");
                if (snapshot.Irk is { Matched: > 0 })
                    break;
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(45))
                    throw new InvalidOperationException("没有收到可验证的手表广播。请唤醒并解锁手表，将它靠近电脑后重试。");
                await Task.Delay(250, token);
            }

            if (_unlockStore.Exists)
                throw new InvalidOperationException("已有一块绑定手表，请先移除后重试。");

            _unlockStore.Save(watch.Id, watch.Address, []);
            saved = true;
            BluetoothLog.Write("direct_unlock_config_saved");
            if (_trayIcon is not null)
            {
                EndScan();
                await LoadPairedWatchesAsync(watch);
            }
            else
            {
                await RestoreDirectUnlockAsync(watch, scan);
                RefreshScan();
            }
        }
        finally
        {
            if (observing && !saved && ReferenceEquals(_scan, scan))
                scan?.StopIrkObservation();
            if (!saved && !_isClosing)
                RefreshScan();
        }
    }

    private void UnlockModeBox_Loaded(object sender, RoutedEventArgs args) =>
        WinUIKit.ComboBoxSizing.AttachBehaviors(UnlockModeBox);

    private void UnlockModeBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingWatchRuleControls || !UnlockModeBox.IsEnabled ||
            UnlockModeBox.SelectedItem is not ComboBoxItem selected)
            return;

        bool windowsHello = ReferenceEquals(selected, WindowsHelloMode);
        SaveWatchRules(_watchRules with
        {
            AutoUnlock = !windowsHello,
            WakeWithWindowsHello = windowsHello,
            PreferredWindowsHello = windowsHello
        });
    }

    private void UpdateWatchManagement()
    {
        var watch = _selectedWatch;
        bool busy = _watchBinding is not null || _removingWatch || _promptingForDirectUnlock || _readingIrk;
        ManagedWatchName.Text = watch?.Name ?? (_hasSavedUnlock ? "已绑定手表暂不可用" : "尚未绑定手表");
        PairButton.IsEnabled = !busy && !_hasSavedUnlock;
        PairButton.Visibility = _hasSavedUnlock ? Visibility.Collapsed : Visibility.Visible;
        RemoveWatchButton.Visibility = watch is not null || _hasSavedUnlock ? Visibility.Visible : Visibility.Collapsed;
        RemoveWatchButton.IsEnabled = !busy && (watch is not null || _hasSavedUnlock);
        UpdateUnlockModeControls();
    }

    private void ShowWatchManagementStatus(string message, InfoBarSeverity severity)
    {
        WatchManagementStatus.Message = message;
        WatchManagementStatus.Severity = severity;
        WatchManagementStatus.IsOpen = true;
    }
}
