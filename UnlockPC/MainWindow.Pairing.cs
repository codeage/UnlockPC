using Microsoft.UI.Xaml;
using System.Diagnostics.Eventing.Reader;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace UnlockPC;

public sealed partial class MainWindow
{
    private static readonly TimeSpan PairingDuration = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private CancellationTokenSource? _pairing;
    private bool _isClosing;

    private async void StartPairing_Click(object sender, RoutedEventArgs args) => await StartPairingAsync();

    internal async Task StartPairingAsync()
    {
        if (_pairing is not null || _isClosing || _removingWatch)
            return;

        DateTime startedUtc = DateTime.UtcNow;
        using var lifetime = new CancellationTokenSource();
        _pairing = lifetime;
        PairButton.IsEnabled = false;
        UpdateRemoveWatchButton();
        CancelPairButton.Visibility = Visibility.Visible;
        PairingStatus.Text = "正在启动蓝牙配对广播…";

        string attempt = Guid.NewGuid().ToString("N");
        BluetoothLog.Write("pairing_started", new { attempt, windowSeconds = PairingDuration.TotalSeconds });
        PairedWatch? pairedWatch = null;
        GattServiceProvider? provider = null;
        GattLocalCharacteristic? measurement = null;
        GattLocalCharacteristic? location = null;
        int advertisingError = -1;
        int advertisingStatus = (int)GattServiceProviderAdvertisementStatus.Created;
        var peers = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        EventLogWatcher? resultWatcher = null;
        WindowsPairingEvent? systemResult = null;
        bool resultFeedAvailable = false;
        var selected = PairedWatchBox.SelectedItem as PairedWatch;
        try
        {
            var baseline = await ReadPairedWatchesAsync(attempt, "baseline", lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (baseline is null)
            {
                PairingStatus.Text = "无法读取 Windows 配对记录，未能启动配对确认。";
                return;
            }

            using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            startup.CancelAfter(StartupTimeout);
            var token = startup.Token;
            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(token);
            BluetoothLog.Write("pairing_adapter", new { attempt, available = adapter is not null, ble = adapter?.IsLowEnergySupported, peripheral = adapter?.IsPeripheralRoleSupported });
            if (adapter is null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported)
            {
                PairingStatus.Text = "当前蓝牙适配器不可用或不支持 BLE 外设模式。";
                return;
            }

            try
            {
                // Subscribe before advertising, to future records only.
                var query = new EventLogQuery("System", PathType.LogName, WindowsPairingEvent.Query);
                resultWatcher = new EventLogWatcher(query, null, readExistingEvents: false);
                resultWatcher.EventRecordWritten += (_, args) =>
                {
                    using var record = args.EventRecord;
                    try
                    {
                        if (args.EventException is { } error)
                            throw error;
                        if (record is not null && WindowsPairingEvent.Read(record) is { } result)
                            DispatcherQueue.TryEnqueue(() => OnWindowsResult(result, baseline));
                    }
                    catch (Exception ex)
                    {
                        BluetoothLog.Write("pairing_result_monitor_error", new { attempt, type = ex.GetType().Name, ex.HResult });
                        DispatcherQueue.TryEnqueue(() => resultFeedAvailable = false);
                    }
                };
                resultWatcher.Enabled = true;
                resultFeedAvailable = true;
                BluetoothLog.Write("pairing_result_monitor_started", new { attempt });
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                BluetoothLog.Write("pairing_result_monitor_error", new { attempt, type = ex.GetType().Name, ex.HResult });
            }

            if (!await OpenAdvertisingAsync(discoverable: true, token))
                return;

            bool triedCompatibilityMode = false;
            string? firstFailure = null;

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            long attemptStarted = started;
            long? interruptedSince = null;
            bool wasAdvertising = false;
            Task<PairedWatch[]?>? windowsLookup = null;
            long? lastLookup = null;
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                var status = (GattServiceProviderAdvertisementStatus)Volatile.Read(ref advertisingStatus);
                if (windowsLookup is { IsCompleted: true })
                {
                    var current = await windowsLookup;
                    if (current is not null)
                        pairedWatch = PairedWatch.FindPairingResult(baseline, current, peers.Keys);

                    if (pairedWatch is not null)
                    {
                        BluetoothLog.Write("pairing_new_windows_record", new { attempt, address = BluetoothLog.AddressLabel(pairedWatch.Address) });
                        PairingStatus.Text = $"Windows 已完成配对：{pairedWatch.Name}。";
                        return;
                    }

                    windowsLookup = null;
                }

                if (elapsed >= PairingDuration)
                {
                    // Windows may finish pairing just before the discovery window closes.
                    CloseAdvertising();
                    var current = await ReadPairedWatchesAsync(attempt, "deadline", lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (current is null)
                    {
                        PairingStatus.Text = "配对窗口已关闭，暂时无法确认 Windows 配对状态。";
                        return;
                    }

                    pairedWatch = PairedWatch.FindPairingResult(baseline, current, peers.Keys);
                    if (pairedWatch is not null)
                    {
                        BluetoothLog.Write("pairing_new_windows_record", new { attempt, address = BluetoothLog.AddressLabel(pairedWatch.Address) });
                        PairingStatus.Text = $"Windows 已完成配对：{pairedWatch.Name}。";
                        return;
                    }

                    bool hasExistingWatch = current.Any(watch => watch.HasWatchName);
                    BluetoothLog.Write("pairing_confirmation_timeout", new { attempt, hasExistingWatch, gattPeers = peers.Count });
                    PairingStatus.Text = hasExistingWatch
                        ? "本轮未确认新配对。Windows 仍有手表的配对记录，可在已配对设备中选择。"
                        : "本轮未确认新配对。请完成 Windows 的配对提示后重试。";
                    return;
                }

                if (status is GattServiceProviderAdvertisementStatus.Started or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
                {
                    wasAdvertising = true;
                    interruptedSince = null;
                    PairingStatus.Text = $"请在手表「设置 → 蓝牙」里选择这台电脑（{Environment.MachineName}），然后完成 Windows 的配对提示。剩余 {Math.Ceiling((PairingDuration - elapsed).TotalSeconds):0} 秒。";
                    if (!resultFeedAvailable)
                        PairingStatus.Text += " 当前无法读取 Windows 配对结果事件。";
                }
                else
                {
                    // Windows can report Aborted with no error while the GATT advertisement is still starting.
                    int error = Volatile.Read(ref advertisingError);
                    bool blocked = (BluetoothError)error is BluetoothError.DisabledByPolicy or BluetoothError.DisabledByUser
                        or BluetoothError.ConsentRequired or BluetoothError.RadioNotAvailable;
                    interruptedSince ??= System.Diagnostics.Stopwatch.GetTimestamp();
                    bool expired = wasAdvertising
                        ? System.Diagnostics.Stopwatch.GetElapsedTime(interruptedSince.Value) >= TimeSpan.FromSeconds(3)
                        : System.Diagnostics.Stopwatch.GetElapsedTime(attemptStarted) >= StartupTimeout;
                    if (blocked || expired)
                    {
                        string detail = error < 0 ? status.ToString() : ((BluetoothError)error).ToString();
                        if (blocked || triedCompatibilityMode)
                        {
                            PairingStatus.Text = $"Windows 未能开启配对广播（{(blocked ? detail : firstFailure ?? detail)}）。";
                            return;
                        }

                        BluetoothLog.Write("pairing_advertising_retry", new { attempt, previousStatus = status.ToString(), error, wasAdvertising });
                        triedCompatibilityMode = true;
                        firstFailure = detail;
                        CloseAdvertising();
                        await Task.Delay(500, lifetime.Token);
                        Volatile.Write(ref advertisingError, -1);
                        Volatile.Write(ref advertisingStatus, (int)GattServiceProviderAdvertisementStatus.Created);
                        using var retry = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        retry.CancelAfter(StartupTimeout);
                        if (!await OpenAdvertisingAsync(discoverable: false, retry.Token))
                            return;

                        attemptStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        wasAdvertising = false;
                        interruptedSince = null;
                    }

                    PairingStatus.Text = "正在启动蓝牙配对广播…";
                }

                // A watch can complete bonding without reading or subscribing to a GATT characteristic.
                if (windowsLookup is null && (lastLookup is null ||
                    System.Diagnostics.Stopwatch.GetElapsedTime(lastLookup.Value) >= TimeSpan.FromSeconds(2)))
                {
                    lastLookup = System.Diagnostics.Stopwatch.GetTimestamp();
                    windowsLookup = ReadPairedWatchesAsync(attempt, "poll", lifetime.Token);
                }

                await Task.Delay(500, lifetime.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (systemResult is null)
            {
                BluetoothLog.Write("pairing_cancelled", new { attempt, byUser = lifetime.IsCancellationRequested, closing = _isClosing });
                if (!_isClosing)
                    PairingStatus.Text = lifetime.IsCancellationRequested ? "配对窗口已关闭。" : "启动蓝牙配对广播超时。";
            }
        }
        catch (Exception ex)
        {
            BluetoothLog.Write("pairing_exception", new { attempt, type = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}" });
            if (!_isClosing)
                PairingStatus.Text = $"无法开始蓝牙配对（0x{ex.HResult:X8}）。";
        }
        finally
        {
            resultWatcher?.Dispose();
            CloseAdvertising();
            if (systemResult?.Succeeded == true && pairedWatch is null && !_isClosing)
            {
                // The association event can arrive before the paired endpoint exposes its address.
                var devices = await ReadPairedWatchesAsync(attempt, "confirmed", CancellationToken.None);
                pairedWatch = devices?.FirstOrDefault(watch => systemResult.MatchesId(watch.Id));
            }
            lifetime.Cancel();
            _pairing = null;
            BluetoothLog.Write("pairing_finished", new { attempt, newPairing = pairedWatch is not null || systemResult?.Succeeded == true });
            if (!_isClosing)
            {
                if (systemResult is not null)
                    PairingStatus.Text = systemResult.Succeeded
                        ? pairedWatch is null ? "Windows 已完成配对。暂未读到手表地址，请刷新已配对设备。" : $"Windows 已完成配对：{pairedWatch.Name}。"
                        : systemResult.FailureMessage();
                PairButton.IsEnabled = true;
                UpdateRemoveWatchButton();
                CancelPairButton.Visibility = Visibility.Collapsed;
                if (pairedWatch is not null)
                {
                    if (_scan is not null)
                        EndScan();
                    await LoadPairedWatchesAsync(pairedWatch);
                }
                else if (_scan is null && PairedWatchBox.ItemsSource is null)
                {
                    await LoadPairedWatchesAsync(autoStart: false);
                }
            }
        }

        async void OnWindowsResult(WindowsPairingEvent result, PairedWatch[] baseline)
        {
            if (_pairing != lifetime || lifetime.IsCancellationRequested)
                return;
            if (result.CreatedUtc is not { } createdUtc || createdUtc < startedUtc)
            {
                BluetoothLog.Write("pairing_windows_result_ignored", new { attempt, result.EventId, result.RecordId, result.CreatedUtc });
                return;
            }
            var known = baseline.FirstOrDefault(watch => result.MatchesId(watch.Id));
            var target = known;
            bool matches = result.MatchesId(selected?.Id) || peers.Keys.Any(result.MatchesId) ||
                (known?.HasWatchName == true && (selected is null || baseline.Count(w => w.HasWatchName) == 1));
            if (known is null && (!matches || result.Succeeded))
            {
                // A first pairing can fail before there is a paired record or an encrypted GATT read.
                try
                {
                    var info = await DeviceInformation.CreateFromIdAsync(result.Endpoint, PairedWatch.Properties,
                        DeviceInformationKind.AssociationEndpoint).AsTask(lifetime.Token)
                        .WaitAsync(TimeSpan.FromSeconds(3), lifetime.Token);
                    target = PairedWatch.FromDevice(info);
                    matches |= info?.Name.Contains("Apple Watch", StringComparison.OrdinalIgnoreCase) == true ||
                        peers.Keys.Any(result.MatchesId);
                }
                catch (Exception ex)
                {
                    BluetoothLog.Write("pairing_result_endpoint_lookup_error", new { attempt, ex.HResult });
                }
            }
            if (_pairing != lifetime || lifetime.IsCancellationRequested)
                return;
            BluetoothLog.Write(result.Succeeded ? "pairing_windows_success" : "pairing_windows_failure",
                new { attempt, matches, result.EventId, result.RecordId, existing = known is not null,
                    protocolError = $"0x{result.ProtocolError:X8}", associationError = $"0x{result.AssociationError:X8}" });
            if (matches)
            {
                systemResult = result;
                if (result.Succeeded)
                    pairedWatch = target;
                PairingStatus.Text = result.Succeeded ? "Windows 已完成配对。" : result.FailureMessage();
                lifetime.Cancel();
            }
        }

        async Task<bool> OpenAdvertisingAsync(bool discoverable, CancellationToken token)
        {
            // Apple Watch discovers a heart rate accessory. Reads require an encrypted link.
            var service = await GattServiceProvider.CreateAsync(GattServiceUuids.HeartRate).AsTask(token);
            BluetoothLog.Write("pairing_gatt_service", new { attempt, discoverable, result = service.Error.ToString() });
            if (service.Error != BluetoothError.Success)
            {
                PairingStatus.Text = $"无法打开蓝牙配对服务（{service.Error}）。";
                return false;
            }

            provider = service.ServiceProvider;
            token.ThrowIfCancellationRequested();
            var measurementResult = await provider.Service.CreateCharacteristicAsync(
                GattCharacteristicUuids.HeartRateMeasurement, new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                    ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                    WriteProtectionLevel = GattProtectionLevel.EncryptionRequired
                }).AsTask(token);
            token.ThrowIfCancellationRequested();
            var locationResult = await provider.Service.CreateCharacteristicAsync(
                GattCharacteristicUuids.BodySensorLocation, new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Read,
                    ReadProtectionLevel = GattProtectionLevel.EncryptionRequired
                }).AsTask(token);
            token.ThrowIfCancellationRequested();
            BluetoothLog.Write("pairing_gatt_characteristics", new { attempt, measurement = measurementResult.Error.ToString(), location = locationResult.Error.ToString() });
            if (measurementResult.Error != BluetoothError.Success || locationResult.Error != BluetoothError.Success)
            {
                PairingStatus.Text = "无法初始化蓝牙配对服务的特征。";
                return false;
            }

            measurement = measurementResult.Characteristic;
            location = locationResult.Characteristic;
            measurement.ReadRequested += ReadRequested;
            measurement.SubscribedClientsChanged += SubscribedClientsChanged;
            location.ReadRequested += ReadRequested;
            provider.AdvertisementStatusChanged += AdvertisementStatusChanged;
            StartAdvertising(discoverable);
            return true;
        }

        void CloseAdvertising()
        {
            if (measurement is not null)
            {
                measurement.ReadRequested -= ReadRequested;
                measurement.SubscribedClientsChanged -= SubscribedClientsChanged;
            }
            if (location is not null)
                location.ReadRequested -= ReadRequested;
            if (provider is not null)
            {
                provider.AdvertisementStatusChanged -= AdvertisementStatusChanged;
                try { provider.StopAdvertising(); }
                catch (System.Runtime.InteropServices.COMException) { }
            }

            measurement = null;
            location = null;
            provider = null;
        }

        void StartAdvertising(bool discoverable) =>
            provider!.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = discoverable
            });

        void AdvertisementStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
        {
            Volatile.Write(ref advertisingStatus, (int)args.Status);
            BluetoothLog.Write("pairing_advertisement", new { attempt, status = args.Status.ToString(), error = args.Error.ToString() });
            if (args.Error != BluetoothError.Success)
                Volatile.Write(ref advertisingError, (int)args.Error);
        }

        async void ReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                var request = await args.GetRequestAsync();
                if (request is null)
                    return;

                using var writer = new DataWriter();
                writer.WriteByte(0);
                if (sender.Uuid == GattCharacteristicUuids.HeartRateMeasurement)
                    writer.WriteByte(0);
                request.RespondWithValue(writer.DetachBuffer());
                if (peers.TryAdd(args.Session.DeviceId.Id, 0))
                    BluetoothLog.Write("pairing_gatt_peer_observed", new { attempt, source = "read" });
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // A disconnected GATT client cannot receive the response.
            }
            finally
            {
                deferral.Complete();
            }
        }

        void SubscribedClientsChanged(GattLocalCharacteristic sender, object args)
        {
            foreach (var client in sender.SubscribedClients)
                if (peers.TryAdd(client.Session.DeviceId.Id, 0))
                    BluetoothLog.Write("pairing_gatt_peer_observed", new { attempt, source = "subscription" });
        }
    }

    private static async Task<PairedWatch[]?> ReadPairedWatchesAsync(string attempt, string phase, CancellationToken token)
    {
        try
        {
            var devices = await PairedWatch.ListAsync(token);
            BluetoothLog.Write("pairing_windows_snapshot", new
            {
                attempt,
                phase,
                devices = devices.Select(device => new
                {
                    address = BluetoothLog.AddressLabel(device.Address),
                    watch = device.HasWatchName,
                    connected = device.IsConnected
                }).ToArray()
            });
            return devices;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            BluetoothLog.Write("pairing_windows_query_failed", new { attempt, phase, type = ex.GetType().Name, hresult = $"0x{ex.HResult:X8}" });
            return null;
        }
    }

    private void StopPairing_Click(object sender, RoutedEventArgs args) => _pairing?.Cancel();
}
