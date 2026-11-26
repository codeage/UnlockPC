using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

namespace UnlockPC;

internal sealed class ServiceWatchScan : IDisposable
{
    private const int HeaderSize = 28;
    private const int PacketSize = 96;
    private const int PacketLimit = 128;
    private const uint Magic = 0x31504355;
    private readonly DeviceWatcher _deviceWatcher;
    private int _connection;
    private long _sequence;
    private long _windowsMatches;
    private long _irkMatches;
    private long _lastNear;
    private DateTimeOffset _lastSampleAt;
    private MatchSample? _lastWindowsMatch;
    private MatchSample? _lastIrkMatch;
    private AppleWatchBroadcast _broadcast;
    private readonly HashSet<ulong> _resolved = [];

    internal long Epoch { get; private set; }
    internal AdvertisementHistory Advertisements { get; } = new();

    internal ServiceWatchScan(PairedWatch device)
    {
        _connection = device.IsConnected switch { true => 1, false => 0, _ => -1 };
        _deviceWatcher = DeviceInformation.CreateWatcher(PairedWatch.Selector,
            [PairedWatch.ConnectionProperty], DeviceInformationKind.AssociationEndpoint);
        _deviceWatcher.Added += (_, info) => UpdateConnection(info.Id, info.Properties);
        _deviceWatcher.Updated += (_, info) => UpdateConnection(info.Id, info.Properties);
        _deviceWatcher.Removed += (_, info) =>
        {
            if (SameDevice(info.Id))
                Volatile.Write(ref _connection, -1);
        };
        _deviceWatcher.Start();

        bool SameDevice(string id) => string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase);
        void UpdateConnection(string id, IReadOnlyDictionary<string, object> properties)
        {
            if (SameDevice(id) && PairedWatch.ReadConnection(properties) is { } connected)
                Volatile.Write(ref _connection, connected ? 1 : 0);
        }
    }

    internal async Task<ScanSnapshot?> ReadAsync(ulong boundAddress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(350));
        var token = timeout.Token;
        try
        {
            using var pipe = new NamedPipeClientStream(".", "UnlockPC.Scan.v1",
                PipeAccessRights.ReadData | PipeAccessRights.ReadAttributes | PipeAccessRights.Synchronize,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
            await pipe.ConnectAsync(token);
            if (!BondedIrk.IsConnectionFromService(IrkServiceInstallation.MonitorServiceName, pipe.SafePipeHandle))
                return null;

            byte[] header = new byte[HeaderSize];
            await pipe.ReadExactlyAsync(header, token);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
                return null;

            uint running = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            long epoch = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
            ulong serviceBinding = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
            if (running != 1 || serviceBinding != boundAddress || count > PacketLimit)
                return null;

            byte[] packets = new byte[count * PacketSize];
            await pipe.ReadExactlyAsync(packets, token);
            return ReadPackets(epoch, packets);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal void Clear()
    {
        Epoch = _sequence = _windowsMatches = _irkMatches = _lastNear = 0;
        _lastSampleAt = default;
        _lastWindowsMatch = _lastIrkMatch = null;
        _broadcast = default;
        _resolved.Clear();
        Advertisements.Clear();
    }

    private ScanSnapshot ReadPackets(long epoch, byte[] packets)
    {
        if (Epoch != epoch)
        {
            Clear();
            Epoch = epoch;
            BluetoothLog.Write("service_scan_epoch", new { epoch });
        }

        var now = DateTimeOffset.UtcNow;
        long tick = Stopwatch.GetTimestamp();
        var samples = new List<WatchSignalSample>();
        bool dropped = false;
        for (int offset = 0; offset < packets.Length; offset += PacketSize)
        {
            ReadOnlySpan<byte> packet = packets.AsSpan(offset, PacketSize);
            long sequence = BinaryPrimitives.ReadInt64LittleEndian(packet);
            if (sequence <= _sequence)
                continue;
            dropped |= _sequence != 0 && sequence > _sequence + 1;
            _sequence = sequence;
            var seenAt = DateTimeOffset.FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(packet[8..]));
            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(packet[16..]);
            short rssi = BinaryPrimitives.ReadInt16LittleEndian(packet[24..]);
            bool windowsMatch = (packet[26] & 1) != 0;
            bool irkMatch = (packet[26] & 2) != 0;
            var addressType = (BluetoothAddressType)packet[27];
            var packetType = (BluetoothLEAdvertisementType)packet[28];
            short flags = BinaryPrimitives.ReadInt16LittleEndian(packet[29..]);
            int appleLength = Math.Min((int)packet[31], 64);
            var row = AdvertisementRow.FromService(sequence, address, addressType, packetType, rssi, seenAt,
                windowsMatch, irkMatch, flags, packet.Slice(32, appleLength), out var fields);
            Advertisements.Add(row);

            var sample = new MatchSample(seenAt, rssi);
            if (windowsMatch)
            {
                _windowsMatches++;
                if (_lastWindowsMatch is not { } previous || seenAt >= previous.SeenAt)
                    _lastWindowsMatch = sample;
            }
            if (irkMatch)
            {
                _irkMatches++;
                if (_lastIrkMatch is not { } previous || seenAt >= previous.SeenAt)
                    _lastIrkMatch = sample;
                if (_resolved.Add(address))
                    BluetoothLog.Write("scan_irk_address_resolved", new { address = BluetoothLog.AddressLabel(address), seenAt, rssi });
                _broadcast = _broadcast.Observe(fields, seenAt, irkMatch: true);
            }

            var age = now - seenAt;
            bool queued = irkMatch && seenAt > _lastSampleAt && age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(2);
            if (queued)
            {
                _lastSampleAt = seenAt;
                long observed = tick - (long)(age.TotalSeconds * Stopwatch.Frequency);
                if (rssi >= -65)
                    _lastNear = observed;
                samples.Add(new WatchSignalSample(observed, rssi, fields.Nearby?.Flags));
            }
            if (windowsMatch || irkMatch)
                BluetoothLog.Write("scan_watch_advertisement", new
                {
                    source = "windows_service", address = BluetoothLog.AddressLabel(address), seenAt, rssi,
                    windowsAddressMatch = windowsMatch, irkAddressMatch = irkMatch, validRssi = true,
                    queuedForLockPolicy = queued, nearbyFlags = fields.Nearby?.Flags
                });
        }

        return new ScanSnapshot(BluetoothLEAdvertisementWatcherStatus.Started, null, _sequence, _windowsMatches,
            _lastWindowsMatch, _lastWindowsMatch is { } last ? now - last.SeenAt : null,
            Volatile.Read(ref _connection) switch { 1 => true, 0 => false, _ => null }, false, _lastNear,
            samples.ToArray(), dropped, new IrkSnapshot(_irkMatches, _resolved.Count, _lastIrkMatch), _broadcast);
    }

    public void Dispose()
    {
        if (_deviceWatcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            _deviceWatcher.Stop();
    }
}
