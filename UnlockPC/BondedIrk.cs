using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Bluetooth;

namespace UnlockPC;

internal sealed class BondedIrk(NamedPipeClientStream pipe) : IDisposable
{
    internal const string ServiceName = "UnlockPCIrk";
    private readonly object _gate = new();
    private readonly SemaphoreSlim _request = new(1);
    private readonly Dictionary<ulong, (long Started, Task<bool> Match)> _matches = [];
    private volatile bool _disposed;
    public bool IsActive => !_disposed;

    public static async Task<BondedIrk> OpenAsync(ulong deviceAddress, CancellationToken cancellationToken = default)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (adapter is null)
            throw new InvalidOperationException("Windows 没有可用的蓝牙适配器。");

        string request = Guid.NewGuid().ToString("N");
        using var manager = OpenSCManagerW(null, null, 1); // SC_MANAGER_CONNECT
        if (manager.IsInvalid)
            throw ServiceError(Marshal.GetLastPInvokeError());
        using var service = OpenServiceW(manager, ServiceName, 0x14); // SERVICE_START | SERVICE_QUERY_STATUS
        if (service.IsInvalid)
            throw ServiceError(Marshal.GetLastPInvokeError());
        await Task.Run(() => StartMonitor(service, request, cancellationToken), cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        var connection = new NamedPipeClientStream(".", $"UnlockPC.Irk.Match.{request}",
            PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes | PipeAccessRights.Synchronize,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
        try
        {
            await connection.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(connection.SafePipeHandle, out uint server) ||
                !QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _) ||
                server == 0 || server != status.ProcessId)
                throw new InvalidOperationException("无法确认 IRK 服务连接。");

            byte[] addresses = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(addresses, adapter.BluetoothAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(addresses.AsSpan(8), deviceAddress);
            await connection.WriteAsync(addresses, timeout.Token).ConfigureAwait(false);
            byte[] reply = new byte[1];
            await connection.ReadExactlyAsync(reply, timeout.Token).ConfigureAwait(false);
            if (reply[0] == 1)
                return new BondedIrk(connection);

            await connection.WriteAsync(reply, timeout.Token).ConfigureAwait(false);
            throw new InvalidOperationException(reply[0] switch
            {
                2 => "Windows 没有找到此设备的 IRK。请确认手表已通过当前适配器完成配对。",
                4 => "IRK 服务已授权另一块手表。请先在 Windows 中解除原手表的配对。",
                _ => "IRK 服务无法读取此设备的配对密钥。"
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            connection.Dispose();
            throw new InvalidOperationException("连接 IRK 服务超时，请重试。");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static bool IsConnectionFromService(string serviceName, SafePipeHandle pipe)
    {
        using var manager = OpenSCManagerW(null, null, 1);
        if (manager.IsInvalid)
            return false;
        using var service = OpenServiceW(manager, serviceName, 4); // SERVICE_QUERY_STATUS
        return !service.IsInvalid && GetNamedPipeServerProcessId(pipe, out uint server) && server != 0 &&
            QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _) &&
            server == status.ProcessId;
    }

    public Task<bool> ResolvesAsync(ulong address)
    {
        ulong random = address >> 24;
        if (random >> 22 != 1 || (random & 0x3fffff) is 0 or 0x3fffff)
            return Task.FromResult(false);

        lock (_gate)
        {
            if (_disposed)
                return Task.FromResult(false);
            long now = Stopwatch.GetTimestamp();
            if (_matches.TryGetValue(address, out var cached) &&
                (!cached.Match.IsCompleted || Stopwatch.GetElapsedTime(cached.Started, now) < TimeSpan.FromSeconds(30)))
                return cached.Match;

            if (_matches.Count >= 256)
            {
                var completed = _matches.Where(entry => entry.Value.Match.IsCompleted).MinBy(entry => entry.Value.Started);
                if (completed.Key == 0)
                    return Task.FromResult(false);
                _matches.Remove(completed.Key);
            }
            var match = QueryAsync(address);
            _matches[address] = (now, match);
            return match;
        }
    }

    private async Task<bool> QueryAsync(ulong address)
    {
        await _request.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            byte[] request = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(request, address);
            await pipe.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            byte[] result = new byte[1];
            await pipe.ReadExactlyAsync(result, timeout.Token).ConfigureAwait(false);
            if (result[0] <= 1)
                return result[0] == 1;
            BluetoothLog.Write("irk_monitor_binding_failed");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!_disposed)
                BluetoothLog.Write("irk_monitor_connection_failed", new { ex.HResult });
        }
        finally { _request.Release(); }

        Dispose();
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            pipe.Dispose();
            _matches.Clear();
        }
    }

    private static void StartMonitor(ServiceHandle service, string request, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (StartServiceW(service, 1, [request]))
                return;
            int error = Marshal.GetLastPInvokeError();
            if (error is not (1056 or 1061) || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(12))
                throw ServiceError(error);
            token.WaitHandle.WaitOne(100);
        }
    }

    private static InvalidOperationException ServiceError(int error) => new(error switch
    {
        5 => "当前 Windows 用户没有访问 IRK 服务的权限。",
        1060 => "尚未安装 UnlockPC IRK 服务。请完成服务安装后重试。",
        _ => $"无法启动 UnlockPC IRK 服务（{error}）。"
    });

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, ProcessId, Flags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(ServiceHandle service, uint count,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] args);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatus status, uint size, out uint needed);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
