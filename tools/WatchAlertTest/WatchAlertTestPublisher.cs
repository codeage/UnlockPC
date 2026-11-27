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
    private int _advertisementError = (int)BluetoothError.Success;

    private WatchAlertTestPublisher(GattServiceProvider provider, GattLocalCharacteristic characteristic, bool compatibility)
    {
        _provider = provider;
        _characteristic = characteristic;
        UsesCompatibilityService = compatibility;
        _provider.AdvertisementStatusChanged += OnAdvertisementStatusChanged;
        _characteristic.SubscribedClientsChanged += OnSubscribedClientsChanged;
    }

    public int SubscriberCount => _characteristic.SubscribedClients.Count;
    public GattServiceProviderAdvertisementStatus AdvertisementStatus => _provider.AdvertisementStatus;
    public BluetoothError AdvertisementError => (BluetoothError)Volatile.Read(ref _advertisementError);
    public bool UsesCompatibilityService { get; }
    public event EventHandler? StateChanged;

    public static async Task<WatchAlertTestPublisher> StartAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null || !adapter.IsPeripheralRoleSupported)
            throw new InvalidOperationException("当前蓝牙适配器不支持 BLE 外设模式。");

        var publisher = await StartServiceAsync(ServiceId, compatibility: false);
        if (await publisher.WaitForAdvertisementAsync())
            return publisher;

        publisher.Dispose();

        // Some Windows stacks can advertise a 16-bit service but abort a custom 128-bit service.
        // Reuse the discovery service from UnlockPC's pairing flow, with only the test characteristic.
        publisher = await StartServiceAsync(GattServiceUuids.HeartRate, compatibility: true);
        if (await publisher.WaitForAdvertisementAsync())
            return publisher;

        var detail = $"{publisher.AdvertisementStatus} / {publisher.AdvertisementError}";
        publisher.Dispose();
        throw new InvalidOperationException($"Windows 未启动兼容蓝牙广播（{detail}）。");
    }

    private static async Task<WatchAlertTestPublisher> StartServiceAsync(Guid serviceId, bool compatibility)
    {
        var service = await GattServiceProvider.CreateAsync(serviceId);
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

        var publisher = new WatchAlertTestPublisher(provider, characteristic.Characteristic, compatibility);
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

    private async Task<bool> WaitForAdvertisementAsync()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            // An initial Aborted / Success can precede Started.
            if (AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started)
                return true;
            if (AdvertisementError is BluetoothError.DisabledByPolicy or BluetoothError.DisabledByUser or
                BluetoothError.RadioNotAvailable or BluetoothError.ConsentRequired)
                return false;
            await Task.Delay(100);
        }
        return AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started;
    }

    public async Task<(Guid Id, GattCommunicationStatus Status)> SendAsync()
    {
        var subscribers = _characteristic.SubscribedClients.ToArray();
        if (subscribers.Length != 1)
            throw new InvalidOperationException("请确认只有一块手表正在订阅。");
        var subscriber = subscribers[0];
        var id = Guid.NewGuid();
        var payload = CreatePacket(id, DateTimeOffset.UtcNow);
        if (subscriber.MaxNotificationSize < payload.Length)
            throw new InvalidOperationException(
                $"蓝牙通知上限为 {subscriber.MaxNotificationSize} 字节，测试需要 {payload.Length} 字节。");

        var result = await _characteristic.NotifyValueAsync(CryptographicBuffer.CreateFromByteArray(payload), subscriber);
        return (id, result.Status);
    }

    internal static byte[] CreatePacket(Guid id, DateTimeOffset createdAt)
    {
        var payload = new byte[22];
        payload[0] = 2; // Test protocol version
        payload[1] = 1; // testAlert
        id.TryWriteBytes(payload.AsSpan(2, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(18), (uint)createdAt.ToUnixTimeSeconds());
        return payload;
    }

    public void Dispose()
    {
        _provider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
        _characteristic.SubscribedClientsChanged -= OnSubscribedClientsChanged;
        _provider.StopAdvertising();
    }

    private void OnAdvertisementStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        Volatile.Write(ref _advertisementError, (int)args.Error);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSubscribedClientsChanged(GattLocalCharacteristic sender, object args) =>
        StateChanged?.Invoke(this, EventArgs.Empty);
}
