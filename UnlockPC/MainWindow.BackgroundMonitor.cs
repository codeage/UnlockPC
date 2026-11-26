using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.Devices.Bluetooth.Advertisement;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private bool _readingMonitor;
    private long _monitorGeneration;
    private long _lastMonitorTrialSequence;
    private bool _monitorScanning;
    private long _lastMonitorActivity;
    private bool UsesBackgroundMonitor => _trayIcon is not null && _hasSavedUnlock;

    private async Task RefreshBackgroundMonitorAsync()
    {
        if (!UsesBackgroundMonitor || _selectedWatch is not { } watch || _scan is not null || _readingMonitor || _isClosing)
            return;

        _readingMonitor = true;
        try
        {
            var status = await WatchMonitorStatus.ReadAsync();
            if (_isClosing || !UsesBackgroundMonitor || _scan is not null ||
                _selectedWatch is not { } current || !IsSameId(watch.Id, current.Id))
                return;

            if (status is null || !IsSameId(watch.Id, status.DeviceId) ||
                DateTimeOffset.UtcNow - status.CapturedAt > TimeSpan.FromSeconds(2))
            {
                if (_monitorScanning || _monitorGeneration != 0)
                    ResetBackgroundDisplay();
                StartScanButton.IsEnabled = StopScanButton.IsEnabled = false;
                StartTrialButton.IsEnabled = false;
                DirectUnlockStatus.Text = "等待后台手表检测。";
                PresenceText.Text = "等待后台手表检测";
                return;
            }

            if (_monitorGeneration != status.Generation)
            {
                ResetBackgroundDisplay();
                _monitorGeneration = status.Generation;
            }
            ScanSnapshot snapshot = status.Snapshot;
            _monitorScanning = snapshot.Status == BluetoothLEAdvertisementWatcherStatus.Started;
            ShowSnapshot(snapshot, _monitorScanning);
            StartIrkButton.IsEnabled = StopIrkButton.IsEnabled = false;
            StartScanButton.IsEnabled = StopScanButton.IsEnabled = false;
            DirectUnlockStatus.Text = status.Message;
            if (_watchRules.WakeWithWindowsHello)
                WatchWakeStatus.Text = status.Message;
            _advertisementHistory ??= new AdvertisementHistory();
            foreach (var row in status.Advertisements)
            {
                if (row.Sequence <= _lastMonitorTrialSequence)
                    continue;
                _advertisementHistory.Add(row);
                _lastMonitorTrialSequence = row.Sequence;
                if (_trial is { } trial && row.WindowsMatch && row.SeenAt >= trial.StartedAt)
                {
                    var age = DateTimeOffset.UtcNow - row.SeenAt;
                    var seenAt = Stopwatch.GetTimestamp() - (long)(age.TotalSeconds * Stopwatch.Frequency);
                    trial.Add(seenAt, new MatchSample(row.SeenAt, row.Rssi));
                }
            }
            foreach (var activity in status.Activity)
            {
                if (activity.Tick <= _lastMonitorActivity)
                    continue;
                _lastMonitorActivity = activity.Tick;
                DisplayLockActivity(activity, speak: false);
            }

            RefreshAdvertisements();
            long now = Stopwatch.GetTimestamp();
            UpdateWatchOfflineReminder(snapshot, now, interrupted: false);
            if (_trial is not { } activeTrial)
            {
                StartTrialButton.IsEnabled = _monitorScanning;
                return;
            }

            double elapsed = Math.Clamp(activeTrial.ElapsedSeconds(now), 0, ObservationTrial.DurationSeconds);
            TrialProgress.Value = elapsed;
            TrialStatusText.Text = $"测量中 · {Math.Ceiling(ObservationTrial.DurationSeconds - elapsed):0} 秒后完成";
            if (elapsed >= ObservationTrial.DurationSeconds)
            {
                _results.Add(activeTrial);
                if (_results.Count > ResultLimit)
                    _results.RemoveAt(0);
                UpdateResults();
                ShowTrialIdle("本轮测量已完成。");
            }
        }
        finally { _readingMonitor = false; }
    }

    private void ResetBackgroundDisplay()
    {
        _monitorScanning = false;
        _monitorGeneration = _lastMonitorTrialSequence = 0;
        _advertisementHistory = null;
        _advertisementRows.Clear();
        ResetWatchOfflineReminder();
        ShowTrialIdle(_trial is null ? "等待后台手表检测。" : "检测已重新开始，本轮未计入结果。", canStart: false);
        ShowSnapshot(null);
    }
}
