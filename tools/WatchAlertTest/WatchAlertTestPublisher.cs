using System.Buffers.Binary;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

namespace UnlockPC;

internal sealed class WatchAlertTestPublisher : IDisposable
{
    private static readonly Guid ServiceId = new("8A29C90B-A244-4BC2-9DB9-B7CC794471E3");
    private static readonly Guid CharacteristicId = new("C5AEE773-8378-4A75-A218-21505DC61F73");
    private readonly GattServiceProvider _provider;
    private readonly GattLocalCharacteristic _characteristic;

    private WatchAlertTestPublisher(GattServiceProvider provider, GattLocalCharacteristic characteristic)
    {
        _provider = provider;
        _characteristic = characteristic;
        _provider.AdvertisementStatusChanged += OnAdvertisementStatusChanged;
        _characteristic.SubscribedClientsChanged += OnSubscribedClientsChanged;
    }

    public int SubscriberCount => _characteristic.SubscribedClients.Count;
    public GattServiceProviderAdvertisementStatus AdvertisementStatus => _provider.AdvertisementStatus;
    public event EventHandler? StateChanged;

    public static async Task<WatchAlertTestPublisher> StartAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null || !adapter.IsPeripheralRoleSupported)
            throw new InvalidOperationException("当前蓝牙适配器不支持 BLE 外设模式。");

        var service = await GattServiceProvider.CreateAsync(ServiceId);
        if (service.Error != BluetoothError.Success)
            throw new InvalidOperationException($"无法创建蓝牙测试服务（{service.Error}）。");

        var provider = service.ServiceProvider;
        var characteristic = await provider.Service.CreateCharacteristicAsync(
            CharacteristicId,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Notify,
                UserDescription = "UnlockPC test alert"
            });
        if (characteristic.Error != BluetoothError.Success)
            throw new InvalidOperationException($"无法创建蓝牙通知特征（{characteristic.Error}）。");

        var publisher = new WatchAlertTestPublisher(provider, characteristic.Characteristic);
        try
        {
            provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true
            });
            return publisher;
        }
        catch
        {
            publisher.Dispose();
            throw;
        }
    }

    public async Task<(Guid Id, GattCommunicationStatus Status)> SendAsync()
    {
        var subscribers = _characteristic.SubscribedClients.ToArray();
        if (subscribers.Length != 1)
            throw new InvalidOperationException("请确认只有一块手表正在订阅。");
        var subscriber = subscribers[0];
        var payload = new byte[22];
        if (subscriber.MaxNotificationSize < payload.Length)
            throw new InvalidOperationException(
                $"蓝牙通知上限为 {subscriber.MaxNotificationSize} 字节，测试需要 {payload.Length} 字节。");

        var id = Guid.NewGuid();
        payload[0] = 2; // Test protocol version
        payload[1] = 1; // testAlert
        id.TryWriteBytes(payload.AsSpan(2, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(18), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var result = await _characteristic.NotifyValueAsync(CryptographicBuffer.CreateFromByteArray(payload), subscriber);
        return (id, result.Status);
    }

    public void Dispose()
    {
        _provider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
        _characteristic.SubscribedClientsChanged -= OnSubscribedClientsChanged;
        _provider.StopAdvertising();
    }

    private void OnAdvertisementStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args) =>
        StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args) =>
        StateChanged?.Invoke(this, EventArgs.Empty);
}
