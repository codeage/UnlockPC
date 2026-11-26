using System.Numerics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private static readonly TimeSpan RadarSignalTimeout = TimeSpan.FromSeconds(10);
    private readonly UISettings _radarUiSettings = new();
    private Storyboard? _radarPulseAnimation;
    private bool _radarPulseRunning;
    private MatchSample? _radarSample;
    private double _radarSignal;
    private double _radarDistance;

    private void UpdateDeviceRadar(ScanSnapshot? snapshot, bool scanning)
    {
        UpdateRadarPulse(scanning);
        if (!scanning || snapshot is not { Irk: not null } scan || _selectedWatch is not PairedWatch watch ||
            !_hasSavedUnlock || _savedUnlockDeviceId is not { } id || !IsSameId(watch.Id, id))
        {
            ClearRadarMarker();
            return;
        }

        var match = scan.LatestMatchedSample;
        var age = DateTimeOffset.UtcNow - match?.SeenAt;
        if (match is not { } sample || age is null || age < TimeSpan.Zero || age > RadarSignalTimeout)
        {
            ClearRadarMarker();
            return;
        }

        if (_radarSample != sample)
        {
            bool previousIsFresh = _radarSample is { } previous && sample.SeenAt >= previous.SeenAt &&
                sample.SeenAt - previous.SeenAt <= RadarSignalTimeout;
            _radarSignal = previousIsFresh ? _radarSignal + (sample.Rssi - _radarSignal) * 0.45 : sample.Rssi;
            // The one-metre reference is a display calibration, not a ranging or unlock input.
            _radarDistance = Math.Pow(10, (-48 - _radarSignal) / 22);
            _radarSample = sample;
            LayoutDeviceRadar();
        }

        string strength = _radarSignal >= -60 ? "信号强" : _radarSignal >= -75 ? "信号适中" : "信号弱";
#if UNLOCKPC_DEBUG_PAGE
        if (_debugDetailsEnabled)
        {
            string distance = _radarDistance switch
            {
                < 1 => "不足 1 米",
                < 3 => "约 1–3 米",
                < 6 => "约 3–6 米",
                < 12 => "约 6–12 米",
                _ => "约 12 米以上"
            };
            strength = $"{strength} · {Math.Round(_radarSignal):0} dBm · {distance}";
        }
#endif
        RadarDeviceName.Text = watch.Name;
        RadarDeviceSignal.Text = strength;
        RadarMarker.Visibility = Visibility.Visible;
    }

    private void ClearRadarMarker()
    {
        _radarSample = null;
        RadarMarker.Visibility = Visibility.Collapsed;
    }

    private void RadarCanvas_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        LayoutDeviceRadar();
        UpdateRadarDebugPanelSize();
    }

    private void LayoutDeviceRadar()
    {
        double width = RadarCanvas.ActualWidth;
        double height = RadarCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        const double visibleBelowCenter = 0.42;
        const double topInset = 12;
        const double maximumLabelHalfWidth = 90;
        const double outerMarkerFraction = 0.94;
        const double markerHorizontalFactor = 0.5;
        double radius = Math.Min(width * 0.46, (height - topInset) / (1 + visibleBelowCenter));
        if (radius <= 0)
            return;

        double visibleHeight = radius * (1 + visibleBelowCenter);
        double centerX = width / 2;
        double centerY = height - visibleHeight + radius;
        RadarDial.Width = RadarDial.Height = 2 * radius;
        RadarDialClip.Rect = new Rect(0, 0, 2 * radius, visibleHeight);
        Canvas.SetLeft(RadarDial, centerX - radius);
        Canvas.SetTop(RadarDial, centerY - radius);
        Canvas.SetLeft(RadarComputer, centerX - RadarComputer.Width / 2);
        Canvas.SetTop(RadarComputer, centerY - RadarComputer.Height / 2);
        Canvas.SetLeft(RadarMarker, centerX);
        Canvas.SetTop(RadarMarker, centerY);

        // The angle is fixed because a single Bluetooth receiver cannot determine direction.
        double signalFraction = Math.Log(1 + Math.Min(_radarDistance, 15)) / Math.Log(16);
        // Reserve the inner area for the computer and the label beneath the watch.
        double innerRadius = Math.Clamp(radius * 0.28, Math.Min(116, radius * 0.86), radius * 0.86);
        double markerRadius = innerRadius + (radius * outerMarkerFraction - innerRadius) * signalFraction;
        double offsetX = markerRadius * markerHorizontalFactor;
        double offsetY = -markerRadius * Math.Sqrt(0.75);
        RadarMarker.Translation = new Vector3((float)offsetX, (float)offsetY, 0);

        double markerX = centerX + offsetX;
        double halfLabelWidth = Math.Clamp(width - markerX - 8, 0, maximumLabelHalfWidth);
        RadarDeviceLabel.Width = 2 * halfLabelWidth;
        Canvas.SetLeft(RadarDeviceLabel, -halfLabelWidth);
    }

    private void UpdateRadarPulse(bool scanning)
    {
        bool animate = !_isClosing && scanning && DeviceRadarPage.Visibility == Visibility.Visible && AppWindow.IsVisible &&
            AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized } &&
            _radarUiSettings.AnimationsEnabled;
        if (animate == _radarPulseRunning)
            return;

        _radarPulseRunning = animate;
        RadarPulse.Visibility = animate ? Visibility.Visible : Visibility.Collapsed;
        if (animate)
        {
            _radarPulseAnimation ??= CreateRadarPulseAnimation();
            _radarPulseAnimation.Begin();
        }
        else
        {
            _radarPulseAnimation?.Stop();
        }
    }

    private Storyboard CreateRadarPulseAnimation()
    {
        var duration = TimeSpan.FromSeconds(3.6);
        var pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        foreach (string axis in new[] { nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY) })
        {
            var expansion = new DoubleAnimation { From = 0.18, To = 1, Duration = duration };
            Storyboard.SetTarget(expansion, RadarPulseScale);
            Storyboard.SetTargetProperty(expansion, axis);
            pulse.Children.Add(expansion);
        }

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8)), Value = 0.24 });
        fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(duration), Value = 0 });
        Storyboard.SetTarget(fade, RadarPulse);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
        pulse.Children.Add(fade);
        return pulse;
    }
}
