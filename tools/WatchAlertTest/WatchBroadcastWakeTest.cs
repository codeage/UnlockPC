using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

namespace UnlockPC;

internal static class WatchBroadcastWakeTest
{
    private static readonly Guid ServiceId = new("D037E924-FA37-4F34-B1C5-46A2484A4810");
    private static readonly Guid EventId = new("D037E925-FA37-4F34-B1C5-46A2484A4810");

    internal sealed record Result(double? BroadcastMs, double? ReadMs, double? ReplyMs, bool? NewWatchProcess,
        GattServiceProviderAdvertisementStatus Status, BluetoothError Error);

    internal static async Task<Result> RunAsync(Guid eventId, DateTimeOffset createdAt, long started, CancellationToken token)
    {
        var service = await GattServiceProvider.CreateAsync(ServiceId).AsTask(token);
        if (service.Error != BluetoothError.Success)
            throw new InvalidOperationException($"无法创建广播测试服务（{service.Error}）。");

        var provider = service.ServiceProvider;
        var packet = WatchAlertTestPublisher.CreatePacket(eventId, createdAt);
        var characteristic = await provider.Service.CreateCharacteristicAsync(EventId,
            new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Write,
            }).AsTask(token);
        if (characteristic.Error != BluetoothError.Success)
            throw new InvalidOperationException($"无法创建广播测试特征（{characteristic.Error}）。");

        var completion = new TaskCompletionSource<(double Ms, bool NewProcess)>(TaskCreationOptions.RunContinuationsAsynchronously);
        long broadcast = 0;
        long read = 0;
        int error = (int)BluetoothError.Success;
        var value = characteristic.Characteristic;
        value.ReadRequested += ReadRequested;
        value.WriteRequested += WriteRequested;
        provider.AdvertisementStatusChanged += AdvertisementChanged;
        try
        {
            provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true
            });
            (double Ms, bool NewProcess)? reply = null;
            try
            {
                reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(25), token);
            }
            catch (TimeoutException)
            {
                // Keep the observed advertisement status for a useful failure report.
            }
            var advertisedAt = Interlocked.Read(ref broadcast);
            var readAt = Interlocked.Read(ref read);
            return new Result(Elapsed(advertisedAt), Elapsed(readAt), reply?.Ms, reply?.NewProcess,
                provider.AdvertisementStatus, (BluetoothError)Volatile.Read(ref error));
        }
        finally
        {
            value.ReadRequested -= ReadRequested;
            value.WriteRequested -= WriteRequested;
            provider.AdvertisementStatusChanged -= AdvertisementChanged;
            provider.StopAdvertising();
        }

        double? Elapsed(long ticks) => ticks == 0 ? null : Stopwatch.GetElapsedTime(started, ticks).TotalMilliseconds;

        void AdvertisementChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
        {
            Volatile.Write(ref error, (int)args.Error);
            if (args.Status == GattServiceProviderAdvertisementStatus.Started)
                Interlocked.CompareExchange(ref broadcast, Stopwatch.GetTimestamp(), 0);
        }

        async void ReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
        {
            var requestAt = Stopwatch.GetTimestamp();
            var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync();
                if (request is null)
                    return;
                request.RespondWithValue(CryptographicBuffer.CreateFromByteArray(packet));
                Interlocked.CompareExchange(ref read, requestAt, 0);
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // The peer may disconnect before the request can be serviced.
            }
            finally
            {
                deferral.Complete();
            }
        }

        async void WriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
        {
            var responseAt = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync();
                if (request is null)
                    return;
                CryptographicBuffer.CopyToByteArray(request.Value, out var reply);
                if (reply.Length != 17 || reply[16] > 1 || !reply.AsSpan(0, 16).SequenceEqual(packet.AsSpan(2, 16)))
                {
                    request.RespondWithProtocolError(0x13);
                    return;
                }
                request.Respond();
                completion.TrySetResult((responseAt, reply[16] == 1));
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // The request may close before a response if the peer disconnects.
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
