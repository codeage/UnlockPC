using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace UnlockPC;

internal enum WatchConnectionVerificationResult { Unconfirmed, AlreadyConnected, NewlyEncrypted }

internal static class WatchConnectionVerification
{
    private static readonly Guid BluetoothPort = new("8a1f9517-3a8c-4a9e-a018-4f17a200f277");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TraceFlushInterval = TimeSpan.FromMilliseconds(100);

    internal static Task<WatchConnectionVerificationResult> VerifyAsync(PairedWatch watch, byte[] irk, CancellationToken cancellationToken,
        Action<LockActivityEvent>? recordActivity = null, bool waitForCleanup = false)
    {
        var result = new TaskCompletionSource<WatchConnectionVerificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = VerifyAndReleaseAsync(watch, irk, cancellationToken, recordActivity, result);
        return waitForCleanup ? AwaitCleanupAsync() : result.Task;

        async Task<WatchConnectionVerificationResult> AwaitCleanupAsync()
        {
            await cleanup.ConfigureAwait(false);
            return await result.Task.ConfigureAwait(false);
        }
    }

    private static async Task VerifyAndReleaseAsync(PairedWatch watch, byte[] irk, CancellationToken cancellationToken,
        Action<LockActivityEvent>? recordActivity, TaskCompletionSource<WatchConnectionVerificationResult> result)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        var token = deadline.Token;
        var clock = Stopwatch.StartNew();
        string outcome = "unavailable";
        string stage = "opening_paired_device";
        double? enumerateAdaptersMs = null;
        int? adapterCount = null;
        double? openPairedDeviceMs = null;
        double? openGattSessionMs = null;
        double? startTraceMs = null;
        double? initialConnectionCheckMs = null;
        BluetoothConnectionStatus? initialConnectionStatus = null;
        double? preRequestConnectionCheckMs = null;
        BluetoothConnectionStatus? preRequestConnectionStatus = null;
        long requestTimestamp = 0;
        double? requestConnectionMs = null;
        long connectedAt = 0;
        long connectionEventReceivedAt = 0;
        long encryptedAt = 0;
        long encryptionEventReceivedAt = 0;
        double? traceQueryMs = null;
        (uint Status, uint EventsLost, uint RealTimeBuffersLost)? traceStatistics = null;
        double? finalConnectionCheckMs = null;
        BluetoothConnectionStatus? finalConnectionStatus = null;
        try
        {
            long started = Stopwatch.GetTimestamp();
            using var device = await BluetoothLEDevice.FromIdAsync(watch.Id).AsTask(token);
            bool paired = device is not null && device.DeviceInformation.Pairing.IsPaired;
            openPairedDeviceMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!paired || device is null)
                return;

            stage = "checking_initial_connection";
            started = Stopwatch.GetTimestamp();
            initialConnectionStatus = device.ConnectionStatus;
            initialConnectionCheckMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (AcceptExistingConnection(initialConnectionStatus.Value))
                return;

            // BTHPORT's packet event exposes a connection handle, without an adapter identifier.
            stage = "enumerating_adapters";
            started = Stopwatch.GetTimestamp();
            var adapters = await DeviceInformation.FindAllAsync(BluetoothAdapter.GetDeviceSelector()).AsTask(token);
            enumerateAdaptersMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            adapterCount = adapters.Count;
            if (adapters.Count != 1)
            {
                outcome = "adapter_ambiguous";
                return;
            }

            stage = "opening_gatt_session";
            started = Stopwatch.GetTimestamp();
            using var gatt = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token);
            bool canMaintainConnection = gatt?.CanMaintainConnection == true;
            openGattSessionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!canMaintainConnection || gatt is null)
                return;

            stage = "starting_trace";
            started = Stopwatch.GetTimestamp();
            using var resolver = new IrkResolver(irk);
            string traceName = $"UnlockPC-WatchVerification-{Environment.ProcessId}-{Guid.NewGuid():N}";
            using var trace = new TraceEventSession(traceName);
            trace.EnableProvider(BluetoothPort, TraceEventLevel.Informational);
            var source = trace.Source;
            var completed = new TaskCompletionSource<(long EncryptedAt, long ReceivedAt)>(TaskCreationOptions.RunContinuationsAsynchronously);
            long activeRequestTimestamp = 0;
            ushort? watchHandle = null;
            source.Dynamic.All += OnTraceEvent;

            var consumer = Task.Run(() =>
            {
                try { source.Process(); }
                catch (Exception ex)
                {
                    BluetoothLog.Write("watch_encryption_trace_failed", new { ex.HResult });
                }
                finally { completed.TrySetResult(default); }
            });

            using var flushCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task flush = Task.CompletedTask;
            AppDomain.CurrentDomain.ProcessExit += StopTraceOnExit;
            startTraceMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            try
            {
                stage = "checking_connection_before_request";
                token.ThrowIfCancellationRequested();
                started = Stopwatch.GetTimestamp();
                preRequestConnectionStatus = device.ConnectionStatus;
                preRequestConnectionCheckMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (AcceptExistingConnection(preRequestConnectionStatus.Value))
                    return;

                stage = "requesting_connection";
                requestTimestamp = Stopwatch.GetTimestamp();
                Volatile.Write(ref activeRequestTimestamp, requestTimestamp);
                recordActivity?.Invoke(LockActivityEvent.Now(LockActivityPhase.Step, "请求 Windows 建立 BLE 连接。"));
                started = Stopwatch.GetTimestamp();
                gatt.MaintainConnection = true;
                requestConnectionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                stage = "waiting_for_encryption_event";
                flush = FlushWhileWaitingAsync(trace, flushCancellation.Token);
                (encryptedAt, encryptionEventReceivedAt) = await completed.Task.WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (encryptedAt != 0)
                {
                    stage = "checking_trace_integrity";
                    started = Stopwatch.GetTimestamp();
                    traceStatistics = QueryTraceStatistics(traceName);
                    traceQueryMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (traceStatistics is { Status: 0, EventsLost: 0, RealTimeBuffersLost: 0 })
                    {
                        stage = "checking_final_connection";
                        started = Stopwatch.GetTimestamp();
                        finalConnectionStatus = device.ConnectionStatus;
                        finalConnectionCheckMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }
                }
                bool verified = finalConnectionStatus == BluetoothConnectionStatus.Connected;
                token.ThrowIfCancellationRequested();
                outcome = verified ? "verified" : "unconfirmed";
                Complete(verified ? WatchConnectionVerificationResult.NewlyEncrypted : WatchConnectionVerificationResult.Unconfirmed);
            }
            finally
            {
                Volatile.Write(ref activeRequestTimestamp, 0);
                flushCancellation.Cancel();
                // Verification has its result before session cleanup or the ETW consumer drains.
                try
                {
                    await Task.Run(async () =>
                    {
                        await flush.ConfigureAwait(false);
                        try
                        {
                            if (requestTimestamp != 0)
                                gatt.MaintainConnection = false;
                        }
                        finally
                        {
                            trace.Stop();
                            await consumer.ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
                }
                finally { AppDomain.CurrentDomain.ProcessExit -= StopTraceOnExit; }
            }

            void StopTraceOnExit(object? sender, EventArgs args) => trace.Stop(noThrow: true);

            void OnTraceEvent(TraceEvent data)
            {
                long requested = Volatile.Read(ref activeRequestTimestamp);
                // This session uses QPC timestamps, matching Stopwatch even if the wall clock is adjusted.
#pragma warning disable CS0618
                long timestamp = data.TimeStampQPC;
#pragma warning restore CS0618
                if (requested == 0 || timestamp < requested || completed.Task.IsCompleted || data.ProviderGuid != BluetoothPort ||
                    (int)data.ID != 402 || data.Version != 0 || data.EventDataLength < 14)
                    return;

                // Packed BTHPORT event 402 v0: command credits, ACL credits, packet type, length, packet.
                ReadOnlySpan<byte> bytes = data.EventData();
                if (bytes[3] != 2 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length - 8)
                    return;
                var packet = bytes[8..];
                if (packet[1] != packet.Length - 2)
                    return;

                if (packet[0] == 0x3E && packet[2] is 0x01 or 0x0A or 0x29 && packet[3] == 0 && packet.Length >= 14)
                {
                    ushort handle = BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]);
                    if (watchHandle == handle)
                        watchHandle = null;

                    ulong address = BinaryPrimitives.ReadUInt64LittleEndian(packet[6..]) >> 16;
                    bool matches = packet[7] == 1 ? resolver.Resolves(address) || address == watch.Address
                        : packet[7] is 0 or 2 or 3 && address == watch.Address;
                    if (packet[6] == 0 && matches)
                    {
                        watchHandle = handle;
                        Volatile.Write(ref connectionEventReceivedAt, Stopwatch.GetTimestamp());
                        Volatile.Write(ref connectedAt, timestamp);
                        if (!token.IsCancellationRequested)
                            recordActivity?.Invoke(new LockActivityEvent(data.TimeStamp, timestamp,
                                LockActivityPhase.Step, "控制器确认已连上配对手表。"));
                    }
                    return;
                }

                if (watchHandle != BinaryPrimitives.ReadUInt16LittleEndian(packet[3..]))
                    return;
                if (packet[0] == 0x05 && packet[2] == 0)
                    watchHandle = null;
                else if (packet[0] is 0x08 or 0x59 && packet[2] == 0 && packet[5] == 1)
                {
                    long receivedAt = Stopwatch.GetTimestamp();
                    if (!completed.Task.IsCompleted && !token.IsCancellationRequested)
                        recordActivity?.Invoke(new LockActivityEvent(data.TimeStamp, timestamp,
                            LockActivityPhase.Step, "控制器确认 BLE 链路已加密。"));
                    completed.TrySetResult((timestamp, receivedAt));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
        }
        catch (Exception ex)
        {
            outcome = "error";
            BluetoothLog.Write("watch_encryption_verification_error", new { ex.HResult });
        }
        finally
        {
            Complete(WatchConnectionVerificationResult.Unconfirmed);
        }

        bool AcceptExistingConnection(BluetoothConnectionStatus status)
        {
            token.ThrowIfCancellationRequested();
            if (status != BluetoothConnectionStatus.Connected)
                return false;
            outcome = "already_connected";
            Complete(WatchConnectionVerificationResult.AlreadyConnected);
            return true;
        }

        void Complete(WatchConnectionVerificationResult verification)
        {
            if (result.Task.IsCompleted)
                return;
            long decisionAt = Stopwatch.GetTimestamp();
            long controllerConnectedAt = Volatile.Read(ref connectedAt);
            BluetoothLog.Write("watch_encryption_verification_finished", new
            {
                outcome, verification = verification.ToString(), stage, elapsedMs = clock.ElapsedMilliseconds,
                enumerateAdaptersMs, adapterCount, openPairedDeviceMs, openGattSessionMs, startTraceMs,
                initialConnectionCheckMs, initialConnectionStatus = initialConnectionStatus?.ToString(),
                preRequestConnectionCheckMs, preRequestConnectionStatus = preRequestConnectionStatus?.ToString(), requestConnectionMs,
                requestToControllerConnectedMs = ElapsedBetween(requestTimestamp, controllerConnectedAt),
                connectionEventDeliveryMs = ElapsedBetween(controllerConnectedAt, Volatile.Read(ref connectionEventReceivedAt)),
                controllerConnectedToEncryptedMs = ElapsedBetween(controllerConnectedAt, encryptedAt),
                encryptionEventDeliveryMs = ElapsedBetween(encryptedAt, encryptionEventReceivedAt),
                encryptionEventReceivedToDecisionMs = ElapsedBetween(encryptionEventReceivedAt, decisionAt),
                traceQueryMs, traceQueryStatus = traceStatistics?.Status, eventsLost = traceStatistics?.EventsLost,
                realTimeBuffersLost = traceStatistics?.RealTimeBuffersLost,
                finalConnectionCheckMs, finalConnectionStatus = finalConnectionStatus?.ToString(),
                controllerToDecisionMs = ElapsedBetween(encryptedAt, decisionAt)
            });
            result.TrySetResult(verification);
        }
    }

    private static double? ElapsedBetween(long start, long end) =>
        start != 0 && end >= start ? Stopwatch.GetElapsedTime(start, end).TotalMilliseconds : null;

    private static async Task FlushWhileWaitingAsync(TraceEventSession trace, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TraceFlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                trace.Flush();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            BluetoothLog.Write("watch_encryption_trace_flush_failed", new { ex.HResult });
        }
    }

    private static unsafe (uint Status, uint EventsLost, uint RealTimeBuffersLost) QueryTraceStatistics(string traceName)
    {
        // ControlTraceW requires room after the x64 EVENT_TRACE_PROPERTIES for both UTF-16 names.
        const int nameBytes = 1024 * sizeof(char);
        Span<byte> buffer = stackalloc byte[sizeof(TraceStatistics) + 2 * nameBytes];
        buffer.Clear();
        fixed (byte* address = buffer)
        {
            var statistics = (TraceStatistics*)address;
            statistics->BufferSize = (uint)buffer.Length;
            statistics->LoggerNameOffset = (uint)sizeof(TraceStatistics);
            statistics->LogFileNameOffset = statistics->LoggerNameOffset + nameBytes;
            const uint query = 0;
            uint status = ControlTraceW(0, traceName, statistics, query);
            return (status, statistics->EventsLost, statistics->RealTimeBuffersLost);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct TraceStatistics
    {
        [FieldOffset(0)] public uint BufferSize;
        [FieldOffset(88)] public uint EventsLost;
        [FieldOffset(100)] public uint RealTimeBuffersLost;
        [FieldOffset(112)] public uint LogFileNameOffset;
        [FieldOffset(116)] public uint LoggerNameOffset;
    }

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern unsafe uint ControlTraceW(ulong traceHandle, string sessionName, TraceStatistics* statistics, uint controlCode);
}
