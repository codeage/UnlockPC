using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UnlockPC;

// The inbox System32 GameInput.dll exposes the v0 COM interfaces in the Windows SDK.
internal sealed unsafe class BackgroundGamepads : IDisposable
{
    private const uint Gamepad = 0x00040000;
    private const uint Connected = 1;
    private const uint AsyncEnumeration = 1;
    private const uint EnableBackgroundInput = 0x40;
    private const int GetCurrentReadingSlot = 4;
    private const int RegisterDeviceCallbackSlot = 9;
    private const int UnregisterCallbackSlot = 13;
    private const int SetFocusPolicySlot = 21;
    private const int GetDeviceInfoSlot = 3;
    private const int GetGamepadStateSlot = 22;
    private readonly HashSet<nint> _devices = [];
    private nint _input;
    private GCHandle _context;
    private ulong _token;

    public bool Start()
    {
        if (_input != 0)
            return true;

        nint input;
        try
        {
            if (GameInputCreate(out input) < 0 || input == 0)
                return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        _input = input;
        _context = GCHandle.Alloc(this);
        var setFocus = (delegate* unmanaged[Stdcall]<nint, uint, void>)Method(input, SetFocusPolicySlot);
        setFocus(input, EnableBackgroundInput);
        var register = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, nint,
            delegate* unmanaged[Stdcall]<ulong, nint, nint, ulong, uint, uint, void>, ulong*, int>)Method(input, RegisterDeviceCallbackSlot);
        ulong token = 0;
        if (register(input, 0, Gamepad, Connected, AsyncEnumeration, GCHandle.ToIntPtr(_context), &OnDeviceChanged, &token) < 0)
        {
            Dispose();
            return false;
        }

        _token = token;
        return true;
    }

    public bool Sample(HashSet<(uint Vendor, uint Product)> polled)
    {
        if (_input == 0)
            return false;

        nint[] devices;
        lock (_devices)
        {
            if (_devices.Count == 0)
                return false;

            devices = _devices.ToArray();
            foreach (nint device in devices)
                Marshal.AddRef(device);
        }

        bool active = false;
        var read = (delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)Method(_input, GetCurrentReadingSlot);
        try
        {
            foreach (nint device in devices)
            {
                nint reading = 0;
                if (read(_input, Gamepad, device, &reading) < 0 || reading == 0)
                    continue;
                try
                {
                    var getState = (delegate* unmanaged[Stdcall]<nint, GamepadState*, byte>)Method(reading, GetGamepadStateSlot);
                    GamepadState state;
                    if (getState(reading, &state) == 0)
                        continue;

                    var getInfo = (delegate* unmanaged[Stdcall]<nint, DeviceInfo*>)Method(device, GetDeviceInfoSlot);
                    var info = getInfo(device);
                    if (info != null && info->Size >= sizeof(DeviceInfo) && (info->Vendor != 0 || info->Product != 0))
                        polled.Add((info->Vendor, info->Product));
                    active |= state.IsActive;
                }
                finally
                {
                    Marshal.Release(reading);
                }
            }
        }
        finally
        {
            foreach (nint device in devices)
                Marshal.Release(device);
        }
        return active;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnDeviceChanged(ulong token, nint context, nint device, ulong timestamp, uint current, uint previous)
    {
        var owner = (BackgroundGamepads)GCHandle.FromIntPtr(context).Target!;
        lock (owner._devices)
        {
            if ((current & Connected) != 0)
            {
                if (owner._devices.Add(device))
                    Marshal.AddRef(device);
            }
            else if (owner._devices.Remove(device))
            {
                Marshal.Release(device);
            }
        }
    }

    public void Dispose()
    {
        if (_input == 0)
            return;

        if (_token != 0)
        {
            var unregister = (delegate* unmanaged[Stdcall]<nint, ulong, ulong, byte>)Method(_input, UnregisterCallbackSlot);
            if (unregister(_input, _token, ulong.MaxValue) == 0)
            {
                // The native callback may still have access to the context.
                return;
            }
            _token = 0;
        }
        foreach (nint device in _devices)
            Marshal.Release(device);
        _devices.Clear();
        _context.Free();
        Marshal.Release(_input);
        _input = 0;
    }

    private static nint Method(nint instance, int slot) => (*(nint**)instance)[slot];

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public ushort Vendor, Product; }

    [DllImport("GameInput.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GameInputCreate(out nint input);
}

[StructLayout(LayoutKind.Sequential)]
internal struct GamepadState
{
    private const double StickDeadZoneSquared = 0.10 * 0.10;
    private const double TriggerDeadZone = 0.05;

    public uint Buttons;
    public float LeftTrigger, RightTrigger, LeftX, LeftY, RightX, RightY;

    public readonly bool IsActive => Buttons != 0 || LeftTrigger >= TriggerDeadZone || RightTrigger >= TriggerDeadZone ||
        LeftX * LeftX + LeftY * LeftY >= StickDeadZoneSquared ||
        RightX * RightX + RightY * RightY >= StickDeadZoneSquared;
}
