using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace UnlockPC;

internal sealed class WatchAlertTestPanel
{
    private readonly Button _start = new() { Content = "开启测试发送" };
    private readonly Button _send = new() { Content = "发送测试提醒" };
    private readonly Button _sendDelayed = new() { Content = "15 秒后发送" };
    private readonly TextBlock _connection = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    private WatchAlertTestPublisher? _publisher;
    private bool _starting;
    private bool _sending;
    private bool _closed;
    private int _startVersion;

    public WatchAlertTestPanel()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = "Apple Watch 测试提醒" });
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
        View = panel;
        _start.Click += async (_, _) => await ToggleAsync();
        _send.Click += async (_, _) => await SendAsync(false);
        _sendDelayed.Click += async (_, _) => await SendAsync(true);
        UpdateControls();
    }

    public UIElement View { get; }

    public void Stop()
    {
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
        Stop();
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
        _start.IsEnabled = !_starting;
        _start.Content = publisher is null ? "开启测试发送" : "停止测试发送";
        _send.IsEnabled = _sendDelayed.IsEnabled = !_sending && subscribers == 1;
        _connection.Text = publisher is null
            ? (_starting ? "正在开启蓝牙测试服务…" : "测试发送已关闭")
            : $"蓝牙广播 {publisher.AdvertisementStatus} · 手表订阅 {subscribers}";
    }
}
