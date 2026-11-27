using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.Text.Json;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace UnlockPC;

internal sealed class WatchAlertTestPanel
{
    private readonly Button _sendPush = new() { Content = "发送手表推送" };
    private readonly TextBlock _pushResult = new() { TextWrapping = TextWrapping.Wrap };
    private readonly string _pushDirectory = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ByteReset", "UnlockPC", "signing");
    private WatchPushSender? _pushSender;
    private string? _pushLog;
    private int _pushSequence;
    private readonly Button _start = new() { Content = "开启测试发送" };
    private readonly Button _send = new() { Content = "发送测试提醒" };
    private readonly Button _sendDelayed = new() { Content = "15 秒后发送" };
    private readonly TextBlock _connection = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _sendBroadcast = new() { Content = "发送广播唤醒测试" };
    private readonly TextBlock _broadcastResult = new() { TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _broadcastTest;
    private WatchAlertTestPublisher? _publisher;
    private bool _starting;
    private bool _sending;
    private bool _closed;
    private int _startVersion;

    public WatchAlertTestPanel()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = "Apple Watch APNs 测试" });
        panel.Children.Add(_sendPush);
        panel.Children.Add(_pushResult);
        panel.Children.Add(new TextBlock { Text = "Apple Watch 蓝牙测试" });
        panel.Children.Add(new TextBlock
        {
            Text = "在手表的 UnlockPC 中搜索并选择本电脑。手表订阅后即可发送。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(_connection);
        panel.Children.Add(_start);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _send, _sendDelayed }
        });
        panel.Children.Add(_result);
        panel.Children.Add(_sendBroadcast);
        panel.Children.Add(_broadcastResult);
        View = panel;
        _sendPush.Click += async (_, _) => await SendPushAsync();
        _start.Click += async (_, _) => await ToggleAsync();
        _send.Click += async (_, _) => await SendAsync(false);
        _sendDelayed.Click += async (_, _) => await SendAsync(true);
        _sendBroadcast.Click += async (_, _) => await SendBroadcastAsync();
        UpdateControls();
    }

    public UIElement View { get; }

    public void Stop()
    {
        _broadcastTest?.Cancel();
        _startVersion++;
        if (_publisher is { } publisher)
        {
            _publisher = null;
            publisher.StateChanged -= OnStateChanged;
            try
            {
                publisher.Dispose();
            }
            catch (Exception error)
            {
                _result.Text = $"结束测试服务时蓝牙返回错误。{error.Message}";
            }
        }
        UpdateControls();
    }

    public void Close()
    {
        _closed = true;
        _pushSender?.Dispose();
        Stop();
    }

    private async Task SendPushAsync()
    {
        var started = Stopwatch.GetTimestamp();
        var eventUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var eventId = Guid.NewGuid();
        var sequence = ++_pushSequence;
        _sendPush.IsEnabled = false;
        _pushResult.Text = $"正在发送 #{sequence}…";
        try
        {
            if (_pushSender is null)
            {
                var config = Path.Join(_pushDirectory, "apns-test.json");
                var settings = JsonSerializer.Deserialize<PushSettings>(File.ReadAllText(config))
                    ?? throw new InvalidDataException("Missing APNs test settings.");
                _pushSender = new WatchPushSender(settings.KeyPath, settings.KeyId, settings.DeviceTokenPath);
                _pushLog = Path.Join(_pushDirectory, $"WatchPushButton-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            }

            WritePushLog(new { stage = "button", sequence, eventId, eventUtc });
            var response = await _pushSender.SendAsync(eventId, eventUtc, started, $"Windows 推送测试 #{sequence}");
            WritePushLog(new { stage = "apns", sequence, eventId, response.Status, response.ResponseMs, response.Reason });
            if (!_closed)
                _pushResult.Text = response.Status == 200
                    ? $"#{sequence} APNs 已接收（{response.ResponseMs:F0} 毫秒）"
                    : $"#{sequence} APNs 拒绝（{response.Status} {response.Reason}）";
        }
        catch (Exception error)
        {
            if (!_closed)
                _pushResult.Text = $"#{sequence} 无法完成测试（{error.GetType().Name}）";
        }
        finally
        {
            if (!_closed)
                _sendPush.IsEnabled = true;
        }
    }

    private void WritePushLog<T>(T entry) =>
        File.AppendAllText(_pushLog!, JsonSerializer.Serialize(entry) + Environment.NewLine);

    private sealed record PushSettings(string KeyPath, string KeyId, string DeviceTokenPath);

    private async Task SendBroadcastAsync()
    {
        var started = Stopwatch.GetTimestamp();
        var clicked = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        using var operation = new CancellationTokenSource();
        _broadcastTest = operation;
        _broadcastResult.Text = "正在发广播并等待手表回执…";
        UpdateControls();
        BluetoothLog.Write("watch_broadcast_test_start", new { id, clicked });
        try
        {
            var result = await WatchBroadcastWakeTest.RunAsync(id, clicked, started, operation.Token);
            BluetoothLog.Write("watch_broadcast_test_result", new { id, result });
            _broadcastResult.Text = result.ReplyMs is { } elapsed
                ? $"手表回执 {elapsed:F0} 毫秒 · {(result.NewWatchProcess == true ? "新进程" : "原进程")}"
                : result.BroadcastMs is { } advertised
                    ? $"广播启动 {advertised:F0} 毫秒，25 秒内未收到手表回执。"
                    : $"未确认广播启动（{result.Status} / {result.Error}）。";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception error)
        {
            BluetoothLog.Write("watch_broadcast_test_failed", new { id, error = error.GetType().Name });
            _broadcastResult.Text = $"广播测试失败：{error.Message}";
        }
        finally
        {
            _broadcastTest = null;
            UpdateControls();
        }
    }

    private async Task ToggleAsync()
    {
        _result.Text = "";
        if (_publisher is not null)
        {
            Stop();
            return;
        }

        _starting = true;
        UpdateControls();
        int startVersion = _startVersion;
        try
        {
            var publisher = await WatchAlertTestPublisher.StartAsync();
            if (_startVersion != startVersion)
            {
                publisher.Dispose();
                return;
            }
            _publisher = publisher;
            publisher.StateChanged += OnStateChanged;
        }
        catch (Exception error)
        {
            _result.Text = $"无法开启测试发送。{error.Message}";
        }
        finally
        {
            _starting = false;
            UpdateControls();
        }
    }

    private async Task SendAsync(bool delayed)
    {
        if (_publisher is not { } publisher)
            return;
        _sending = true;
        UpdateControls();
        try
        {
            if (delayed)
            {
                _result.Text = "15 秒后发送。现在可以按 Win+L 锁屏。";
                await Task.Delay(TimeSpan.FromSeconds(15));
            }
            if (!ReferenceEquals(_publisher, publisher))
                return;
            var (id, status) = await publisher.SendAsync();
            _result.Text = status == GattCommunicationStatus.Success
                ? $"{DateTime.Now:T} 已发送。事件 ID {id:D}"
                : $"{DateTime.Now:T} 发送失败（{status}）。事件 ID {id:D}";
        }
        catch (Exception error)
        {
            _result.Text = $"发送失败。{error.Message}";
        }
        finally
        {
            _sending = false;
            UpdateControls();
        }
    }

    private void OnStateChanged(object? sender, EventArgs args) =>
        View.DispatcherQueue.TryEnqueue(UpdateControls);

    private void UpdateControls()
    {
        if (_closed)
            return;
        var publisher = _publisher;
        int subscribers = publisher?.SubscriberCount ?? 0;
        _start.IsEnabled = !_starting && _broadcastTest is null;
        _sendBroadcast.IsEnabled = !_starting && publisher is null && _broadcastTest is null;
        _start.Content = publisher is null ? "开启测试发送" : "停止测试发送";
        _send.IsEnabled = _sendDelayed.IsEnabled = !_sending && subscribers == 1;
        _connection.Text = publisher is null
            ? (_starting ? "正在开启蓝牙测试服务…" : "测试发送已关闭")
            : $"蓝牙广播 {publisher.AdvertisementStatus}"
                + (publisher.UsesCompatibilityService ? "（兼容模式）" : "")
                + (publisher.AdvertisementError != Windows.Devices.Bluetooth.BluetoothError.Success ? $"（{publisher.AdvertisementError}）" : "")
                + $" · 手表订阅 {subscribers}";
    }
}
