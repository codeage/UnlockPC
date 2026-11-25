using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace UnlockPC;

internal static class WatchConnectionVerification
{
    private static readonly Guid BluetoothPort = new("8a1f9517-3a8c-4a9e-a018-4f17a200f277");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TraceFlushInterval = TimeSpan.FromMilliseconds(100);

    internal static Task<bool> VerifyAsync(PairedWatch watch, byte[] irk, CancellationToken cancellationToken,
        Action<LockActivityEvent>? recordActivity = null, bool waitForCleanup = false)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = VerifyAndReleaseAsync(watch, irk, cancellationToken, recordActivity, result);
        return waitForCleanup ? AwaitCleanupAsync() : result.Task;

        async Task<bool> AwaitCleanupAsync()
        {
            await cleanup.ConfigureAwait(false);
            return await result.Task.ConfigureAwait(false);
        }
    }

    private static async Task VerifyAndReleaseAsync(PairedWatch watch, byte[] irk, CancellationToken cancellationToken,
        Action<LockActivityEvent>? recordActivity, TaskCompletionSource<bool> result)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);
        var token = deadline.Token;
        var clock = Stopwatch.StartNew();
        string outcome = "unavailable";
        long encryptedAt = 0;
        try
        {
            // BTHPORT's packet event exposes a connection handle, without an adapter identifier.
            var adapters = await DeviceInformation.FindAllAsync(BluetoothAdapter.GetDeviceSelector()).AsTask(token);
            if (adapters.Count != 1)
            {
                outcome = "adapter_ambiguous";
                return;
            }

            using var device = await BluetoothLEDevice.FromIdAsync(watch.Id).AsTask(token);
            if (device is null || !device.DeviceInformation.Pairing.IsPaired)
                return;
            using var gatt = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token);
            if (gatt?.CanMaintainConnection != true)
                return;

            using var resolver = new IrkResolver(irk);
            string traceName = $"UnlockPC-WatchVerification-{Environment.ProcessId}-{Guid.NewGuid():N}";
            using var trace = new TraceEventSession(traceName);
            trace.EnableProvider(BluetoothPort, TraceEventLevel.Informational);
            var source = trace.Source;
            var completed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            long requestTimestamp = 0;
            ushort? watchHandle = null;
            source.Dynamic.All += OnTraceEvent;

            var consumer = Task.Run(() =>
            {
                try { source.Process(); }
                catch (Exception ex)
                {
                    BluetoothLog.Write("watch_encryption_trace_failed", new { ex.HResult });
                }
                finally { completed.TrySetResult(0); }
            });

            using var flushCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task flush = Task.CompletedTask;
            AppDomain.CurrentDomain.ProcessExit += StopTraceOnExit;
            try
            {
                token.ThrowIfCancellationRequested();
                if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                {
                    outcome = "already_connected";
                    return;
                }

                Volatile.Write(ref requestTimestamp, Stopwatch.GetTimestamp());
                recordActivity?.Invoke(LockActivityEvent.Now(LockActivityPhase.Step, "请求 Windows 建立 BLE 连接。"));
                gatt.MaintainConnection = true;
                flush = FlushWhileWaitingAsync(trace, flushCancellation.Token);
                encryptedAt = await completed.Task.WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                bool verified = encryptedAt != 0 && HasCompleteTrace(traceName) &&
                    device.ConnectionStatus == BluetoothConnectionStatus.Connected;
                token.ThrowIfCancellationRequested();
                outcome = verified ? "verified" : "unconfirmed";
                Complete(verified);
            }
            finally
            {
                Volatile.Write(ref requestTimestamp, 0);
                flushCancellation.Cancel();
                // Verification has its result before session cleanup or the ETW consumer drains.
                try
                {
                    await Task.Run(async () =>
                    {
                        await flush.ConfigureAwait(false);
                        try { gatt.MaintainConnection = false; }
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
                long requested = Volatile.Read(ref requestTimestamp);
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
                    if (!completed.Task.IsCompleted && !token.IsCancellationRequested)
                        recordActivity?.Invoke(new LockActivityEvent(data.TimeStamp, timestamp,
                            LockActivityPhase.Step, "控制器确认 BLE 链路已加密。"));
                    completed.TrySetResult(timestamp);
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
            Complete(false);
        }

        void Complete(bool verified)
        {
            if (result.Task.IsCompleted)
                return;
            BluetoothLog.Write("watch_encryption_verification_finished", new
            {
                outcome, elapsedMs = clock.ElapsedMilliseconds,
                controllerToDecisionMs = encryptedAt == 0 ? (double?)null : Stopwatch.GetElapsedTime(encryptedAt).TotalMilliseconds
            });
            result.TrySetResult(verified);
        }
    }

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

    private static unsafe bool HasCompleteTrace(string traceName)
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
            return ControlTraceW(0, traceName, statistics, query) == 0 &&
                statistics->EventsLost == 0 && statistics->RealTimeBuffersLost == 0;
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
