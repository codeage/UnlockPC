using System.Diagnostics;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private static readonly TimeSpan WatchOfflineReminderDelay = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _offlineReminderLastMatch;
    private long _watchOfflineSince;
    private bool _watchOfflineReminderSent;

    private void ResetWatchOfflineReminder()
    {
        _offlineReminderLastMatch = null;
        _watchOfflineSince = 0;
        _watchOfflineReminderSent = false;
    }

    private void UpdateWatchOfflineReminder(ScanSnapshot snapshot, long now, bool interrupted)
    {
        if (_trayIcon is null)
            return;
        if (!_watchRules.AutoLock || snapshot.LatestMatchedSample is not { } match)
        {
            ResetWatchOfflineReminder();
            return;
        }

        if (_offlineReminderLastMatch != match.SeenAt)
        {
            _offlineReminderLastMatch = match.SeenAt;
            _watchOfflineSince = 0;
            _watchOfflineReminderSent = false;
            return;
        }

        if (interrupted || snapshot.IsConnected == true)
        {
            _watchOfflineSince = 0;
            return;
        }

        if (_watchOfflineReminderSent)
            return;
        if (_watchOfflineSince == 0)
            _watchOfflineSince = now;
        TimeSpan offlineFor = Stopwatch.GetElapsedTime(_watchOfflineSince, now);
        var watcher = _directWatchWake ?? _watchWake;
        if (offlineFor < WatchOfflineReminderDelay || watcher is { IsSessionUnlocked: false })
            return;

        var idleTime = watcher is null ? SessionInputActivity.GetWindowsIdleTime() : watcher.GetInputIdleTime(now);
        if (idleTime is not { } idle || idle >= offlineFor)
            return;

        _watchOfflineReminderSent = true;
        bool submitted = _trayIcon.ShowNotification("UnlockPC", "你的手表似乎已经离线，请确认你是否还在佩戴。");
        BluetoothLog.Write("watch_offline_reminder", new
        {
            seconds = offlineFor.TotalSeconds, inputIdleSeconds = idle.TotalSeconds, lastMatch = match.SeenAt, submitted
        });
    }
}
