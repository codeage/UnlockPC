using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Gaming.Input;

namespace UnlockPC;

internal sealed class SessionInputActivity : IDisposable
{
    private readonly RawHidActivity _hid = new();
    private readonly BackgroundGamepads _gamepads = new();
    private readonly Dictionary<string, ControllerReading> _controllers = new();
    private readonly HashSet<(uint Vendor, uint Product)> _polled = [];
    private nint _window;
    private long _nextHidRefresh;
    private long _lastDeviceInput;

    public void Start(nint window)
    {
        if (_window != 0)
            return;

        _window = window;
        _nextHidRefresh = 0;
        bool backgroundGamepads = _gamepads.Start();
        int hidUsages = _hid.RefreshDevices(window);
        BluetoothLog.Write("session_input_monitor_started", new { backgroundGamepads, hidUsages });
        _nextHidRefresh = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
    }

    public void ReadHid(nint input)
    {
        if (_window != 0 && _hid.Read(input, _polled))
            _lastDeviceInput = Stopwatch.GetTimestamp();
    }

    public void OnHidDeviceChange(nint device, bool arrived)
    {
        if (_window == 0)
            return;
        if (arrived)
            _hid.RefreshDevices(_window);
        else
            _hid.ForgetDevice(device);
    }

    public TimeSpan? GetIdleTime(long now)
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref input))
            return null;

        uint milliseconds = unchecked((uint)Environment.TickCount - input.Tick);
        TimeSpan idle = milliseconds > int.MaxValue ? TimeSpan.Zero : TimeSpan.FromMilliseconds(milliseconds);
        if (_lastDeviceInput != 0)
        {
            var deviceIdle = Stopwatch.GetElapsedTime(_lastDeviceInput, now);
            if (deviceIdle < idle)
                idle = deviceIdle;
        }
        return idle;
    }

    public void SampleControllers()
    {
        if (_window == 0)
            return;

        long now = Stopwatch.GetTimestamp();
        if (now >= _nextHidRefresh)
        {
            _hid.RefreshDevices(_window);
            _nextHidRefresh = now + 2 * Stopwatch.Frequency;
        }

        _polled.Clear();
        if (_gamepads.Sample(_polled))
            _lastDeviceInput = now;

        try
        {
            var devices = RawGameController.RawGameControllers;
            if (devices.Count == 0)
            {
                _controllers.Clear();
                return;
            }

            foreach (var device in devices)
            {
                if (_polled.Contains((device.HardwareVendorId, device.HardwareProductId)))
                    continue;

                string id = device.NonRoamableId;
                if (!_controllers.TryGetValue(id, out var reading))
                    _controllers[id] = reading = new ControllerReading(device);
                if (reading.HasActivity(device))
                    _lastDeviceInput = now;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // The controller may disconnect while its state is being read.
            _controllers.Clear();
        }
    }

    public void Dispose()
    {
        _window = 0;
        _lastDeviceInput = 0;
        _hid.Dispose();
        _gamepads.Dispose();
        _controllers.Clear();
        _polled.Clear();
    }

    private sealed class ControllerReading(RawGameController device)
    {
        private readonly Gamepad? _gamepad = Gamepad.FromGameController(device);
        private readonly bool[] _buttons = new bool[device.ButtonCount];
        private readonly GameControllerSwitchPosition[] _switches = new GameControllerSwitchPosition[device.SwitchCount];
        private readonly double[] _axes = new double[device.AxisCount];
        private readonly double[] _lastAxes = new double[device.AxisCount];
        private readonly double[] _restAxes = new double[device.AxisCount];
        private bool _initialized;

        public bool HasActivity(RawGameController controller)
        {
            if (_gamepad is { } gamepad)
            {
                var reading = gamepad.GetCurrentReading();
                return new GamepadState
                {
                    Buttons = (uint)reading.Buttons,
                    LeftX = (float)reading.LeftThumbstickX, LeftY = (float)reading.LeftThumbstickY,
                    RightX = (float)reading.RightThumbstickX, RightY = (float)reading.RightThumbstickY,
                    LeftTrigger = (float)reading.LeftTrigger, RightTrigger = (float)reading.RightTrigger
                }.IsActive;
            }

            controller.GetCurrentReading(_buttons, _switches, _axes);
            bool active = _buttons.Contains(true) || _switches.Any(direction => direction != GameControllerSwitchPosition.Center);
            for (int index = 0; index < _axes.Length; index++)
            {
                bool moved = _initialized && Math.Abs(_lastAxes[index] - _axes[index]) >= 0.03;
                if (!_initialized)
                    _restAxes[index] = _axes[index];
                if (!_initialized || moved)
                    _lastAxes[index] = _axes[index];
                active |= moved || Math.Abs(_restAxes[index] - _axes[index]) >= 0.08;
            }
            _initialized = true;
            return active;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Tick; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);
}
