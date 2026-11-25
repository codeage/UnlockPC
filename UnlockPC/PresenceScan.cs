using System.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Foundation;

namespace UnlockPC;

internal sealed class PresenceScan : IDisposable
{
    private const int UnlockSampleLimit = 128;
    private readonly object _gate = new();
    private readonly string _deviceId;
    private readonly DeviceWatcher _deviceWatcher;
    private ulong _deviceAddress;
    private bool? _connected;
    private bool _removed;
    private BluetoothLEAdvertisementWatcher _watcher = CreateWatcher();
    private TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs>? _receivedHandler;
    private TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs>? _stoppedHandler;
    private bool _disposed;
    private BluetoothError? _stopError;
    private long _lastReceived;
    private long _lastNear;
    private DateTimeOffset _lastNearSeenAt;
    private bool _lastNearWasIrkOnly;
    private DateTimeOffset _lastUnlockSampleSeenAt;
    private readonly List<WatchSignalSample> _unlockSamples = [];
    private bool _unlockSamplesDropped;
    private MatchSample? _lastMatch;
    private long _total;
    private long _matched;
    private ObservationTrial? _trial;
    private IrkResolver? _irk;
    private readonly HashSet<ulong> _resolvedAddresses = [];
    private long _irkMatched;
    private DateTimeOffset? _lastIrkMatch;
    private AppleWatchBroadcast _watchBroadcast;

    public AdvertisementHistory Advertisements { get; } = new();
    public event Action? UnlockSampleQueued;

    private static BluetoothLEAdvertisementWatcher CreateWatcher() =>
        new() { ScanningMode = BluetoothLEScanningMode.Passive };

    public PresenceScan(PairedWatch watch)
    {
        _deviceId = watch.Id;
        _deviceAddress = watch.Address;
        _connected = watch.IsConnected;
        _deviceWatcher = DeviceInformation.CreateWatcher(PairedWatch.Selector, PairedWatch.Properties,
            DeviceInformationKind.AssociationEndpoint);
    }

    public void Start()
    {
        SubscribeWatcher();
        _deviceWatcher.Added += OnDeviceAdded;
        _deviceWatcher.Updated += OnDeviceUpdated;
        _deviceWatcher.Removed += OnDeviceRemoved;
        _deviceWatcher.Start();
        _watcher.Start();
        BluetoothLog.Write("scan_started", new { address = BluetoothLog.AddressLabel(_deviceAddress), connected = _connected, status = _watcher.Status.ToString() });
    }

    public void RestartAfterResume()
    {
        lock (_gate)
        {
            if (_disposed || _removed || _watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started or
                BluetoothLEAdvertisementWatcherStatus.Stopping)
                return;

            UnsubscribeWatcher();
            _watcher = CreateWatcher();
            SubscribeWatcher();
            _stopError = null;
            _unlockSamples.Clear();
            _unlockSamplesDropped = false;
            _watcher.Start();
            BluetoothLog.Write("scan_resume_restart", new { status = _watcher.Status.ToString() });
        }
    }

    private void SubscribeWatcher()
    {
        var watcher = _watcher;
        _receivedHandler = (_, args) => OnReceived(watcher, args);
        _stoppedHandler = (_, args) => OnStopped(watcher, args);
        watcher.Received += _receivedHandler;
        watcher.Stopped += _stoppedHandler;
    }

    private void UnsubscribeWatcher()
    {
        if (_receivedHandler is { } received)
            _watcher.Received -= received;
        if (_stoppedHandler is { } stopped)
            _watcher.Stopped -= stopped;
    }

    public ScanSnapshot Read(bool consumeUnlockSamples = false)
    {
        lock (_gate)
        {
            var status = _watcher.Status;
            WatchSignalSample[] unlockSamples = [];
            bool unlockSamplesDropped = false;
            if (consumeUnlockSamples)
            {
                unlockSamples = _unlockSamples.ToArray();
                unlockSamplesDropped = _unlockSamplesDropped;
                _unlockSamples.Clear();
                _unlockSamplesDropped = false;
            }

            return new ScanSnapshot(status, _stopError, _total, _matched, _lastMatch,
                _lastMatch is null ? null : Stopwatch.GetElapsedTime(_lastReceived), _connected, _removed, _lastNear,
                unlockSamples, unlockSamplesDropped,
                _irk is null ? null : new IrkSnapshot(_irkMatched, _resolvedAddresses.Count, _lastIrkMatch), _watchBroadcast);
        }
    }

    public bool StartIrkObservation(byte[] key)
    {
        lock (_gate)
        {
            if (_disposed || _removed || _irk is not null ||
                _watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
                return false;

            _irk = new IrkResolver(key);
            _watchBroadcast = default;
            BluetoothLog.Write("scan_irk_started", new { pairedAddress = BluetoothLog.AddressLabel(_deviceAddress) });
            return true;
        }
    }

    public byte[]? CopyIrk()
    {
        lock (_gate)
            return _irk?.CopyKey();
    }

    public void StopIrkObservation()
    {
        lock (_gate)
        {
            if (_irk is null)
                return;

            _irk.Dispose();
            _irk = null;
            BluetoothLog.Write("scan_irk_finished", new { matched = _irkMatched, distinctAddresses = _resolvedAddresses.Count });
            _resolvedAddresses.Clear();
            _irkMatched = 0;
            _lastIrkMatch = null;
            _watchBroadcast = default;
            _lastUnlockSampleSeenAt = default;
            _unlockSamples.Clear();
            _unlockSamplesDropped = false;
            if (_lastNearWasIrkOnly)
                _lastNear = 0;
        }
    }

    public ObservationTrial? StartTrial(WatchScenario scenario)
    {
        lock (_gate)
        {
            if (_disposed || _watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started || _trial is not null)
                return null;

            return _trial = new ObservationTrial(scenario, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
        }
    }

    public void CancelTrial()
    {
        lock (_gate)
            _trial = null;
    }

    public ObservationTrial? FinishTrial()
    {
        lock (_gate)
        {
            if (_disposed || _watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started ||
                _trial is null || _trial.ElapsedSeconds(Stopwatch.GetTimestamp()) < ObservationTrial.DurationSeconds)
                return null;

            var completed = _trial;
            _trial = null;
            return completed;
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        Action? notify = null;
        object? diagnostic = null;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher))
                return;

            _total++;
            ulong address = args.BluetoothAddress;
            bool hasAddress = !_removed && !args.IsAnonymous;
            bool windowsAddressMatch = hasAddress && address == _deviceAddress;
            bool irkAddressMatch = hasAddress && _irk is { } resolver &&
                args.BluetoothAddressType == BluetoothAddressType.Random && resolver.Resolves(address);
            bool validRssi = args.RawSignalStrengthInDBm is > -127 and not 127;
            bool windowsMatch = windowsAddressMatch && validRssi;
            bool irkMatch = irkAddressMatch && validRssi;

            if (irkMatch)
            {
                _irkMatched++;
                if (_lastIrkMatch is not { } last || args.Timestamp > last)
                    _lastIrkMatch = args.Timestamp;

                if (_resolvedAddresses.Add(address))
                    BluetoothLog.Write("scan_irk_address_resolved", new
                    {
                        address = BluetoothLog.AddressLabel(address),
                        distinctAddresses = _resolvedAddresses.Count,
                        matchesWindowsAddress = windowsMatch,
                        hasAppleManufacturerData = args.Advertisement.ManufacturerData.Any(data => data.CompanyId == 0x004C),
                        seenAt = args.Timestamp,
                        rssi = args.RawSignalStrengthInDBm
                    });
            }

            var row = AdvertisementRow.Create(_total, args, windowsMatch, irkMatch, out var watchFields);
            Advertisements.Add(row);
            if (irkMatch || (_irk is null && windowsMatch))
                _watchBroadcast = _watchBroadcast.Observe(watchFields, args.Timestamp, irkMatch);

            if (windowsAddressMatch || irkAddressMatch)
            {
                var age = DateTimeOffset.UtcNow - args.Timestamp;
                bool queuedForLockPolicy = false;
                if (validRssi && age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(2))
                {
                    long observedAt = Stopwatch.GetTimestamp() - (long)(age.TotalSeconds * Stopwatch.Frequency);
                    if (args.RawSignalStrengthInDBm >= -65 && args.Timestamp > _lastNearSeenAt)
                    {
                        _lastNearSeenAt = args.Timestamp;
                        _lastNear = observedAt;
                        _lastNearWasIrkOnly = !windowsMatch;
                    }
                    if (irkMatch && args.Timestamp > _lastUnlockSampleSeenAt)
                    {
                        _lastUnlockSampleSeenAt = args.Timestamp;
                        if (_unlockSamples.Count == UnlockSampleLimit)
                        {
                            _unlockSamples.Clear();
                            _unlockSamplesDropped = true;
                        }
                        _unlockSamples.Add(new WatchSignalSample(observedAt, args.RawSignalStrengthInDBm, watchFields.Nearby?.Flags));
                        queuedForLockPolicy = true;
                        notify = UnlockSampleQueued;
                    }
                }

                diagnostic = new
                {
                    address = BluetoothLog.AddressLabel(address),
                    addressType = args.BluetoothAddressType.ToString(),
                    advertisementType = args.AdvertisementType.ToString(),
                    seenAt = args.Timestamp,
                    ageMs = age.TotalMilliseconds,
                    rssi = args.RawSignalStrengthInDBm,
                    windowsAddressMatch,
                    irkAddressMatch,
                    validRssi,
                    queuedForLockPolicy,
                    nearbyFlags = watchFields.Nearby?.Flags,
                    connected = _connected,
                    payload = row.Payload
                };
            }

            if (windowsMatch)
            {
                long timestamp = Stopwatch.GetTimestamp();
                var match = new MatchSample(args.Timestamp, args.RawSignalStrengthInDBm);
                _matched++;
                if (_lastMatch is null || match.SeenAt >= _lastMatch.Value.SeenAt)
                {
                    _lastReceived = timestamp;
                    _lastMatch = match;
                }
                _trial?.Add(timestamp, match);
            }
        }

        notify?.Invoke();
        if (diagnostic is not null)
            BluetoothLog.Write("scan_watch_advertisement", diagnostic);
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher))
                return;

            _stopError = args.Error;
            BluetoothLog.Write("scan_stopped", new { error = args.Error.ToString(), matched = _matched, total = _total });
        }
    }

    private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation device) =>
        UpdatePairedDevice(device.Id, device.Properties);

    private void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update) =>
        UpdatePairedDevice(update.Id, update.Properties);

    private void UpdatePairedDevice(string id, IReadOnlyDictionary<string, object> properties)
    {
        if (!id.Equals(_deviceId, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            if (_disposed || _removed)
                return;

            if (PairedWatch.TryReadAddress(properties, out ulong address) && address != _deviceAddress)
            {
                BluetoothLog.Write("scan_windows_address_changed", new
                {
                    previous = BluetoothLog.AddressLabel(_deviceAddress),
                    current = BluetoothLog.AddressLabel(address)
                });
                _deviceAddress = address;
                if (_irk is null)
                    _watchBroadcast = default;
            }

            if (PairedWatch.ReadConnection(properties) is { } connected && connected != _connected)
            {
                _connected = connected;
                BluetoothLog.Write("scan_windows_connection_changed", new { connected, matched = _matched });
            }
        }
    }

    private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (!update.Id.Equals(_deviceId, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            if (!_disposed)
            {
                _removed = true;
                BluetoothLog.Write("scan_paired_device_removed", new { address = BluetoothLog.AddressLabel(_deviceAddress), matched = _matched });
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            StopIrkObservation();
            BluetoothLog.Write("scan_closed", new { matched = _matched, total = _total, connected = _connected });
            _trial = null;
        }

        UnsubscribeWatcher();
        _deviceWatcher.Added -= OnDeviceAdded;
        _deviceWatcher.Updated -= OnDeviceUpdated;
        _deviceWatcher.Removed -= OnDeviceRemoved;
        if (_deviceWatcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            _deviceWatcher.Stop();
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            _watcher.Stop();
    }
}

internal readonly record struct ScanSnapshot(
    BluetoothLEAdvertisementWatcherStatus Status, BluetoothError? Error, long Total, long Matched,
    MatchSample? LastMatch, TimeSpan? MatchAge, bool? IsConnected, bool DeviceRemoved, long LastNear,
    WatchSignalSample[] UnlockSamples, bool UnlockSamplesDropped,
    IrkSnapshot? Irk, AppleWatchBroadcast WatchBroadcast);

internal readonly record struct IrkSnapshot(long Matched, int DistinctAddresses, DateTimeOffset? LastSeen);
