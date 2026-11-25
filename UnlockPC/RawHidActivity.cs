using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace UnlockPC;

internal sealed class RawHidActivity : IDisposable
{
    private const uint Hid = 2;
    private const uint DeviceInfo = 0x2000000B;
    private const uint Input = 0x10000003;
    private const uint InputSink = 0x100;
    private const uint DeviceNotify = 0x2000;
    private const uint Remove = 1;
    private static readonly uint DeviceSize = (uint)Marshal.SizeOf<Device>();
    private static readonly uint RegistrationSize = (uint)Marshal.SizeOf<Registration>();
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<Header>();
    private readonly HashSet<(ushort Page, ushort Usage)> _registrations = [];
    private readonly Dictionary<nint, DeviceIdentity> _devices = [];
    private readonly HidReportActivity _reports = new();
    private byte[] _buffer = new byte[256];

    public unsafe int RefreshDevices(nint window)
    {
        uint count = 0;
        if (GetRawInputDeviceList(null, ref count, DeviceSize) == uint.MaxValue)
            return _registrations.Count;

        var list = new Device[count];
        fixed (Device* pointer = list)
        {
            count = GetRawInputDeviceList(pointer, ref count, DeviceSize);
            if (count == uint.MaxValue || count > list.Length)
                return _registrations.Count;
        }

        var connected = new HashSet<nint>();
        foreach (var device in list.AsSpan(0, (int)count))
        {
            if (device.Type != Hid)
                continue;
            connected.Add(device.Handle);
            if (!_devices.TryGetValue(device.Handle, out var identity))
            {
                var info = new Info { Size = (uint)sizeof(Info) };
                uint bytes = info.Size;
                if (GetRawInputDeviceInfo(device.Handle, DeviceInfo, ref info, ref bytes) == uint.MaxValue ||
                    info.Type != Hid || info.Page == 0 || info.Usage == 0)
                    continue;
                identity = new DeviceIdentity(info.Page, info.Usage, info.Vendor, info.Product);
                _devices.Add(device.Handle, identity);
            }

            var usage = (identity.Page, identity.Usage);
            if (_registrations.Contains(usage))
                continue;

            var registration = new Registration { Page = usage.Page, Usage = usage.Usage, Flags = InputSink | DeviceNotify, Target = window };
            if (RegisterRawInputDevices(in registration, 1, RegistrationSize))
                _registrations.Add(usage);
        }

        foreach (nint handle in _devices.Keys.Where(handle => !connected.Contains(handle)).ToArray())
            ForgetDevice(handle);
        return _registrations.Count;
    }

    public void ForgetDevice(nint device)
    {
        _devices.Remove(device);
        _reports.Remove(device);
    }

    public unsafe bool Read(nint input, HashSet<(uint Vendor, uint Product)> polledControllers)
    {
        uint size = 0;
        if (GetRawInputData(input, Input, null, ref size, HeaderSize) == uint.MaxValue ||
            size < HeaderSize + 8 || size > 1024 * 1024)
            return false;

        if (_buffer.Length < size)
            Array.Resize(ref _buffer, (int)size);
        uint bytes;
        fixed (byte* buffer = _buffer)
            bytes = GetRawInputData(input, Input, buffer, ref size, HeaderSize);
        if (bytes < HeaderSize + 8 || bytes > _buffer.Length)
            return false;

        var data = _buffer.AsSpan(0, (int)bytes);
        var header = MemoryMarshal.Read<Header>(data);
        // Ignore duplicate game reports for controllers sampled with dead zones.
        if (header.Type != Hid || !_devices.TryGetValue(header.Device, out var identity) ||
            (identity.IsGameController && polledControllers.Contains((identity.Vendor, identity.Product))))
            return false;

        var reports = data[(int)HeaderSize..];
        int reportSize = BinaryPrimitives.ReadInt32LittleEndian(reports);
        int reportCount = BinaryPrimitives.ReadInt32LittleEndian(reports[4..]);
        return _reports.Observe(header.Device, reportSize, reportCount, reports[8..]);
    }

    public void Dispose()
    {
        foreach (var (page, usage) in _registrations)
        {
            var registration = new Registration { Page = page, Usage = usage, Flags = Remove };
            RegisterRawInputDevices(in registration, 1, RegistrationSize);
        }
        _registrations.Clear();
        _devices.Clear();
        _reports.Clear();
    }

    private readonly record struct DeviceIdentity(ushort Page, ushort Usage, uint Vendor, uint Product)
    {
        internal bool IsGameController => Page == 1 && Usage is 4 or 5 or 8 || Page is 2 or 5;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Device { public nint Handle; public uint Type; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Registration { public ushort Page, Usage; public uint Flags; public nint Target; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public uint Type, Size; public nint Device, WParam; }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct Info
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public uint Vendor;
        [FieldOffset(12)] public uint Product;
        [FieldOffset(20)] public ushort Page;
        [FieldOffset(22)] public ushort Usage;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint GetRawInputDeviceList(Device* devices, ref uint count, uint entrySize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command, ref Info info, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(in Registration devices, uint count, uint entrySize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe uint GetRawInputData(nint input, uint command, byte* data, ref uint size, uint headerSize);
}

internal sealed class HidReportActivity
{
    private readonly Dictionary<nint, Dictionary<byte, byte[]>> _previous = [];

    public bool Observe(nint device, int size, int count, ReadOnlySpan<byte> data)
    {
        if (size <= 0 || count <= 0 || count > data.Length / size)
            return false;

        if (!_previous.TryGetValue(device, out var reports))
            _previous.Add(device, reports = []);

        bool changed = false;
        for (int index = 0; index < count; index++)
        {
            var report = data.Slice(index * size, size);
            byte id = report[0];
            if (!reports.TryGetValue(id, out var previous) || previous.Length != size)
            {
                reports[id] = report.ToArray();
                changed = true;
            }
            else if (!report.SequenceEqual(previous))
            {
                report.CopyTo(previous);
                changed = true;
            }
        }
        return changed;
    }

    public void Remove(nint device) => _previous.Remove(device);
    public void Clear() => _previous.Clear();
}
