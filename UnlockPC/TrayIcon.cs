using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace UnlockPC;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const ushort WmContextMenu = 0x007B;
    private const ushort WmLeftButtonUp = 0x0202;
    private const ushort WmLeftButtonDoubleClick = 0x0203;
    private const ushort NinSelect = 0x0400;
    private const ushort NinKeySelect = 0x0401;
    private const ushort NinBalloonUserClick = 0x0405;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetFocus = 3;
    private const uint NimSetVersion = 4;
    private const uint IconFlags = 0x01 | 0x02 | 0x04 | 0x80; // MESSAGE, ICON, TIP, SHOWTIP
    private const uint MenuFlags = 0x0002 | 0x0080 | 0x0100; // RIGHTBUTTON, NONOTIFY, RETURNCMD
    private const uint OpenSettingsCommand = 1;
    private const uint ExitCommand = 2;

    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private readonly SubclassProc _subclass;
    private readonly uint _taskbarCreated;
    private NotifyIconData _icon;
    private bool _subclassInstalled;
    private bool _registered;

    public TrayIcon(Window window, Action openSettings, Action exit)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _appWindow = window.AppWindow;
        _dispatcher = window.DispatcherQueue;
        _openSettings = openSettings;
        _exit = exit;
        _subclass = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _icon = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = _hwnd,
            Id = 1,
            Flags = IconFlags,
            Callback = CallbackMessage,
            Icon = LoadIcon(nint.Zero, 32512), // IDI_APPLICATION, shared by Windows
            Tip = "UnlockPC",
            Version = 4
        };

        _subclassInstalled = SetWindowSubclass(_hwnd, _subclass, 1, 0);
        EnsureVisible();
    }

    public bool EnsureVisible()
    {
        if (_registered || !_subclassInstalled || _icon.Icon == nint.Zero)
            return _registered;

        if (!Shell_NotifyIcon(NimAdd, ref _icon))
            return false;

        _registered = Shell_NotifyIcon(NimSetVersion, ref _icon);
        if (!_registered)
            Shell_NotifyIcon(NimDelete, ref _icon);

        return _registered;
    }

    public bool ShowNotification(string title, string message)
    {
        if (!EnsureVisible())
            return false;

        var data = _icon;
        data.Flags = 0x10 | 0x40; // NIF_INFO | NIF_REALTIME
        data.InfoTitle = title;
        data.Info = message;
        data.InfoFlags = 0x01; // NIIF_INFO
        return Shell_NotifyIcon(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_registered)
        {
            Shell_NotifyIcon(NimDelete, ref _icon);
            _registered = false;
        }

        if (_subclassInstalled)
        {
            RemoveWindowSubclass(_hwnd, _subclass, 1);
            _subclassInstalled = false;
        }
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == CallbackMessage && _registered)
        {
            switch (unchecked((ushort)lParam))
            {
                case WmContextMenu:
                {
                    int x = unchecked((short)wParam);
                    int y = unchecked((short)(wParam >> 16));
                    if (x == -1 && y == -1 && GetCursorPos(out var point))
                    {
                        x = point.X;
                        y = point.Y;
                    }
                    _dispatcher.TryEnqueue(() => ShowMenu(x, y));
                    break;
                }
                case NinSelect:
                case NinKeySelect:
                case NinBalloonUserClick:
                case WmLeftButtonUp:
                case WmLeftButtonDoubleClick:
                    // Open during the shell callback, while the user's tray input is current.
                    _openSettings();
                    break;
            }
            return nint.Zero;
        }

        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            bool wasRegistered = _registered;
            _registered = false;
            if (!EnsureVisible() && wasRegistered)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (_subclassInstalled && !_registered && !_appWindow.IsVisible)
                        _openSettings();
                });
            }
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        if (!_registered)
            return;

        nint menu = CreatePopupMenu();
        if (menu == nint.Zero)
            return;

        uint command;
        try
        {
            if (!AppendMenu(menu, 0, OpenSettingsCommand, "打开设置") ||
                !AppendMenu(menu, 0, ExitCommand, "退出"))
                return;

            SetForegroundWindow(_hwnd);
            command = TrackPopupMenu(menu, MenuFlags, x, y, 0, _hwnd, nint.Zero);
            // Let Windows dismiss the menu correctly on subsequent invocations.
            PostMessage(_hwnd, 0, 0, 0); // WM_NULL
        }
        finally
        {
            DestroyMenu(menu);
        }

        switch (command)
        {
            case OpenSettingsCommand:
                _openSettings();
                break;
            case ExitCommand:
                _exit();
                break;
            default:
                if (_registered)
                    Shell_NotifyIcon(NimSetFocus, ref _icon);
                break;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
