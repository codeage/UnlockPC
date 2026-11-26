using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Windows.Storage;
using Windows.System;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private bool _skipWindowPlacementSave;

    private void InitializeInformationPages()
    {
        var assembly = typeof(MainWindow).Assembly;
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        AboutVersionText.Text = $"版本 {version?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3)}";
        InitializeDebugPage();
    }

    partial void InitializeDebugPage();

    private async void OpenLogs_Click(object sender, RoutedEventArgs args) =>
        await OpenLogDirectoryAsync(AppDataPaths.Logs, "应用日志", createIfMissing: true);

    private async void OpenServiceLogs_Click(object sender, RoutedEventArgs args) =>
        await OpenLogDirectoryAsync(IrkServiceInstallation.MonitorLogDirectory, "服务日志", createIfMissing: false);

    private async Task OpenLogDirectoryAsync(string path, string label, bool createIfMissing)
    {
        DiagnosticsStatus.IsOpen = false;
        try
        {
            if (createIfMissing)
                Directory.CreateDirectory(path);

            var folder = await StorageFolder.GetFolderFromPathAsync(path);
            if (!await Launcher.LaunchFolderAsync(folder))
                ShowDiagnosticsError($"Windows 未能打开{label}目录。");
        }
        catch (Exception error) when (!createIfMissing &&
            (error is FileNotFoundException or DirectoryNotFoundException || error.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003)))
        {
            ShowDiagnosticsError("服务日志目录尚未生成，请确认 UnlockPC Watch Monitor 服务已安装并启动。");
        }
        catch (Exception error)
        {
            ShowDiagnosticsError($"无法打开{label}目录（0x{error.HResult:X8}）。");
        }
    }

    private async void ResetData_Click(object sender, RoutedEventArgs args)
    {
        ResetDataButton.IsEnabled = false;
        DiagnosticsStatus.IsOpen = false;
        try
        {
            var rules = new CheckBox { Content = $"锁定与解锁设置 · {FormatDataSize([AppDataPaths.WatchRules])}", IsChecked = true };
            var layout = new CheckBox { Content = $"窗口位置和大小 · {FormatDataSize([AppDataPaths.WindowPlacement])}" };
            var authorization = new CheckBox { Content = $"手表授权和保存的凭据 · {FormatDataSize([AppDataPaths.WatchAuthorization])}" };
            var logs = new CheckBox { Content = $"蓝牙诊断日志 · {FormatDataSize(BluetoothLog.ListFiles())}" };
            CheckBox[] categories = [rules, layout, authorization, logs];
            var content = new StackPanel { Spacing = 6 };
            content.Children.Add(new TextBlock
            {
                Text = "清除所选内容后会重启 UnlockPC。Windows 的蓝牙配对会保留。若保留手表授权，重启后会按恢复后的规则使用它。",
                TextWrapping = TextWrapping.Wrap
            });
            var dialog = new ContentDialog
            {
                XamlRoot = DiagnosticsPage.XamlRoot,
                Title = "清除应用数据并重启？",
                Content = content,
                PrimaryButtonText = "清除并重启",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            void UpdateConfirmation(object sender, RoutedEventArgs args) =>
                dialog.IsPrimaryButtonEnabled = categories.Any(category => category.IsChecked == true);
            foreach (var category in categories)
            {
                content.Children.Add(category);
                category.Checked += UpdateConfirmation;
                category.Unchecked += UpdateConfirmation;
            }

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _isClosing)
                return;

            EndScan();
            StopWatchConnectionStatus();
            try
            {
                if (rules.IsChecked == true)
                    File.Delete(AppDataPaths.WatchRules);
                if (layout.IsChecked == true)
                {
                    File.Delete(AppDataPaths.WindowPlacement);
                    _skipWindowPlacementSave = true;
                }
                else
                {
                    _windowPlacement?.Save();
                }
                if (authorization.IsChecked == true)
                    _unlockStore.Delete();
                if (logs.IsChecked == true)
                    BluetoothLog.ClearFiles();

                var reason = AppInstance.Restart(string.Empty);
                ShowDiagnosticsError($"已清除所选数据，但 Windows 未能重启 UnlockPC（{reason}）。");
            }
            catch (Exception error)
            {
                ShowDiagnosticsError($"清除或重启未完成（0x{error.HResult:X8}）。已清除的内容不会恢复。");
            }

            LoadWatchRules();
            await LoadPairedWatchesAsync();
            UpdateWatchConnectionStatus();
        }
        catch (Exception error)
        {
            ShowDiagnosticsError($"无法打开清除选项（0x{error.HResult:X8}）。");
        }
        finally
        {
            ResetDataButton.IsEnabled = true;
        }
    }

    private void ShowDiagnosticsError(string message)
    {
        DiagnosticsStatus.Severity = InfoBarSeverity.Error;
        DiagnosticsStatus.Message = message;
        DiagnosticsStatus.IsOpen = true;
    }

    private static string FormatDataSize(string[] paths)
    {
        long bytes = paths.Select(path => new FileInfo(path)).Where(file => file.Exists).Sum(file => file.Length);
        return bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
            : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";
    }
}
