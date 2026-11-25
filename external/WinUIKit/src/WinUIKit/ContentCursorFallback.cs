using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WinUIKit;

/// <summary>
/// Content-area cursor fallback that can be attached to any WinUI window (it need not be a <see cref="ThemedWindow"/>;
/// ThemedWindow's own window procedure already does the same thing).
/// <para>
/// WinUI's content child window has a null class cursor: when no control under the pointer specifies a cursor, the child
/// passes WM_SETCURSOR to its parent, and WinUI's top-level window procedure doesn't handle it either (returns 0), so the
/// system keeps the previous shape — the "busy" cursor from startup, or the resize arrow left over from dragging a window
/// edge, stays until some control sets a cursor (measured: an external read of the cursor returned IDC_WAIT).
/// The arrow is restored only when "forwarded by the child, client area, and the original handler didn't take it"; when
/// a control specifies a cursor the child handles it itself and this code is never reached.
/// Tried and ineffective: declaring a default cursor on the content root via XAML APIs (ProtectedCursor = arrow): WinUI
/// doesn't take over the cursor until it receives its first pointer input.
/// </para>
/// Attached with comctl32's SetWindowSubclass, coexisting with the window's own subclassing. If the window itself swaps
/// its window procedure with SetWindowLongPtr and swaps it back on close, attach before that swap (the "original window
/// procedure" it saves is then this subclass, and the chain stays intact when it swaps back). Removed automatically when
/// the window is destroyed.
/// </summary>
// The subclass procedure is an unmanaged function pointer rather than a delegate: a delegate is an instantiated class,
// and the native binary would carry its name and its enclosing class's name.
public static unsafe class ContentCursorFallback
{
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, UIntPtr, UIntPtr, IntPtr> proc, UIntPtr idSubclass, UIntPtr refData);
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, UIntPtr, UIntPtr, IntPtr> proc, UIntPtr idSubclass);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    private const uint WM_SETCURSOR = 0x0020, WM_NCDESTROY = 0x0082;
    private const int HTCLIENT = 1;
    private static readonly IntPtr IdcArrow = 32512;
    private static readonly UIntPtr SubclassId = 0x57434346;   // 'WCCF'
    private static bool s_logged;

    /// <summary>For A/B testing: when true, the fallback stays attached but does nothing (the app sets it from its own diagnostic switch).</summary>
    public static bool Disabled { get; set; }

    public static void Attach(Window window) => Attach(WindowNative.GetWindowHandle(window));

    public static void Attach(IntPtr hwnd)
    {
        if (!SetWindowSubclass(hwnd, &OnMessage, SubclassId, UIntPtr.Zero))
            UiHost.WriteLog($"[Cursor] Failed to attach the content-area cursor fallback (err={Marshal.GetLastWin32Error()})");
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData)
    {
        if (msg == WM_NCDESTROY)
        {
            RemoveWindowSubclass(hWnd, &OnMessage, SubclassId);
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }
        IntPtr result = DefSubclassProc(hWnd, msg, wParam, lParam);
        if (msg == WM_SETCURSOR && wParam != hWnd && (lParam.ToInt64() & 0xFFFF) == HTCLIENT && result == IntPtr.Zero)
        {
            // This log line is itself evidence that "this window has the symptom": it's only reached when nobody took the cursor
            if (!s_logged)
            {
                s_logged = true;
                UiHost.WriteTrace(Disabled
                    ? "[Cursor] A/B: content child window specified no cursor; fallback disabled, not handled (cursor keeps its previous shape)"
                    : "[Cursor] Content child window specified no cursor -> reset to arrow (first time this run; not logged again)");
            }
            if (Disabled) return result;
            SetCursor(LoadCursor(IntPtr.Zero, IdcArrow));
            return 1;
        }
        return result;
    }
}
