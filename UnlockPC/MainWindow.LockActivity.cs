using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace UnlockPC;

internal enum LockActivityPhase { Note, Start, Step, Finish }

internal readonly record struct LockActivityEvent(DateTimeOffset Time, long Tick, LockActivityPhase Phase, string Message, string? Speech = null)
{
    public static LockActivityEvent Now(LockActivityPhase phase, string message, string? speech = null) =>
        new(DateTimeOffset.Now, Stopwatch.GetTimestamp(), phase, message, speech);
}

internal sealed record LockActivityEntry(string Timestamp, string Message);

public sealed partial class MainWindow
{
    private const int LockActivityLimit = 80;
    private readonly ObservableCollection<LockActivityEntry> _lockActivity = new();
    private long? _lockActivityStart;
    private long _lockActivityPrevious;
    private bool _lockActivityScrollPending;
    private LockActivitySpeech? _lockActivitySpeech;

    private void UpdateRadarDebugPanelVisibility()
    {
#if UNLOCKPC_DEBUG_PAGE
        RadarDebugPanel.Visibility = _debugDetailsEnabled && DeviceRadarPage.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        if (RadarDebugPanel.Visibility == Visibility.Visible)
        {
            UpdateRadarDebugPanelSize();
            QueueLockActivityScroll();
        }
#endif
    }

    private void UpdateRadarDebugPanelSize()
    {
        if (RadarDebugPanel.Visibility != Visibility.Visible || RadarCanvas.ActualWidth <= 0 || RadarCanvas.ActualHeight <= 0)
            return;

        double width = RadarCanvas.ActualWidth;
        RadarDebugPanel.Width = Math.Clamp(width * 0.30, Math.Min(232, width * 0.5), 440);
        double availableHeight = RadarCanvas.ActualHeight + DeviceRadarPage.Margin.Bottom -
            RadarDebugPanel.Margin.Top - RadarDebugPanel.Margin.Bottom;
        RadarDebugPanel.MaxHeight = Math.Clamp(availableHeight, 0, 720);
        WatchBroadcastCard.MaxHeight = Math.Min(480, RadarDebugPanel.MaxHeight * 0.76);
    }

    private void RecordLockActivity(LockActivityEvent activity) => DisplayLockActivity(activity, speak: true);

    private void DisplayLockActivity(LockActivityEvent activity, bool speak)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => DisplayLockActivity(activity, speak));
            return;
        }
        if (_isClosing)
            return;

        string message = activity.Message;
        string? announcement = speak && _watchRules.SpeakLockActivity ? activity.Speech ?? activity.Message : null;
        if (activity.Phase == LockActivityPhase.Start)
        {
            _lockActivityStart = _lockActivityPrevious = activity.Tick;
            message += "\n开始计时";
            if (announcement is not null)
                announcement += "开始计时。";
        }
        else if (activity.Phase is LockActivityPhase.Step or LockActivityPhase.Finish)
        {
            if (_lockActivityStart is not { } startedAt || activity.Tick < _lockActivityPrevious)
                return;
            double stepSeconds = Stopwatch.GetElapsedTime(_lockActivityPrevious, activity.Tick).TotalSeconds;
            message += $"\n距上一步 {stepSeconds:F2} 秒";
            if (announcement is not null)
                announcement += $"本步用时 {stepSeconds:F2} 秒。";
            _lockActivityPrevious = activity.Tick;
            if (activity.Phase == LockActivityPhase.Finish)
            {
                double totalSeconds = Stopwatch.GetElapsedTime(startedAt, activity.Tick).TotalSeconds;
                message += $" · 总耗时 {totalSeconds:F2} 秒";
                if (announcement is not null)
                    announcement += $"总耗时 {totalSeconds:F2} 秒。";
                _lockActivityStart = null;
            }
        }

        bool follow = _lockActivityScrollPending ||
            LockActivityScroll.ScrollableHeight - LockActivityScroll.VerticalOffset <= 12;
        _lockActivity.Add(new LockActivityEntry(activity.Time.ToLocalTime().ToString("HH:mm:ss.fff"), message));
        if (_lockActivity.Count > LockActivityLimit)
            _lockActivity.RemoveAt(0);
        LockActivityEmptyText.Visibility = Visibility.Collapsed;
        if (follow)
            QueueLockActivityScroll();
        if (announcement is not null)
            (_lockActivitySpeech ??= new LockActivitySpeech()).SpeakLatest(announcement);
    }

    private void StopLockActivitySpeech()
    {
        _lockActivitySpeech?.Dispose();
        _lockActivitySpeech = null;
    }

    private void QueueLockActivityScroll()
    {
        if (_lockActivityScrollPending)
            return;
        _lockActivityScrollPending = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _lockActivityScrollPending = false;
            if (_isClosing || RadarDebugPanel.Visibility != Visibility.Visible || LockActivityScroll.XamlRoot is null)
                return;
            WindowRootGrid.UpdateLayout();
            LockActivityScroll.ChangeView(null, LockActivityScroll.ScrollableHeight, null, disableAnimation: true);
        });
    }
}
