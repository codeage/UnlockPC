using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Foundation;
using WinRT.Interop;

namespace WinUIKit
{
    /// <summary>
    /// Common base class for all app windows: a uniform Mica backdrop, uniform title bar colors, and no
    /// background flash when a window opens or closes.
    ///
    /// [What the flash is] Before the XAML content composes its first frame, and after it is torn down on close,
    /// the client area is erased by GDI's WM_ERASEBKGND, which paints black by default. When the app theme differs
    /// from the theme the process started with, this shows up as a black flash on open/close (unrelated to theme
    /// resources: nothing has been drawn yet at that moment).
    ///
    /// [Why this is the only fix] All of the following were measured to have no effect; do not retry them:
    ///   · Changing Application.RequestedTheme at runtime: throws COMException; it can only be set once before content loads
    ///   · DWMWA_USE_IMMERSIVE_DARK_MODE / DWMWA_CAPTION_COLOR: do not reach that client-area frame
    ///   · Changing only the window class background brush (SetClassLongPtr): once this class handles the message, DefWindowProc no longer uses it
    ///   · Switching the backdrop material, recreating the window: unrelated to that phase
    /// The only thing that works is handling WM_ERASEBKGND and filling the color ourselves, which is what this class does.
    /// </summary>
    public class ThemedWindow : Window
    {
        private const int GWLP_WNDPROC = -4;
        private const uint WM_ERASEBKGND = 0x0014;
        private const uint WM_SETICON = 0x0080;
        private const uint WM_NCDESTROY = 0x0082;
        private const uint WM_NCRBUTTONDOWN = 0x00A4;
        private const uint WM_NCRBUTTONUP = 0x00A5;
        private const uint WM_CONTEXTMENU = 0x007B;
        private const uint WM_POPUPSYSTEMMENU = 0x0313;   // Undocumented: the shell/DefWindowProc asks the window to show its system menu at the given screen point
        private const int HTCAPTION = 2;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const uint IMAGE_ICON = 1;
        private const uint LR_SHARED = 0x00008000;
        private const int SM_CXICON = 11;
        private const int SM_CYICON = 12;
        private const int SM_CXSMICON = 49;
        private const int SM_CYSMICON = 50;
        // The .NET SDK embeds <ApplicationIcon> into apphost.exe as resource ID 32512.
        private static readonly IntPtr ApplicationIconResource = new(32512);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(
            IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true)]
        private static extern IntPtr LoadImage(
            IntPtr hInstance, IntPtr name, uint type, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

        [DllImport("user32.dll")]
        private static extern int FillRect(IntPtr hdc, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(uint colorref);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        // All static: the window object may be collected after it closes while the HWND is still processing messages
        // during the close animation; if a delegate held in an instance field were collected, the window procedure
        // pointer would dangle and crash.
        // Original window procedures are stored per HWND: each window has its own, and a single shared field would mix them up.
        private static readonly Dictionary<IntPtr, IntPtr> OriginalProcs = new();
        // Reverse lookup: HWND → derived window instance. Used to route non-client right clicks to the right window.
        // Removed on WM_NCDESTROY so closed windows are not kept alive by a strong reference.
        private static readonly Dictionary<IntPtr, ThemedWindow> Instances = new();
        private static WndProcDelegate? _sharedWndProc;
        private static IntPtr _eraseBrush;
        private static bool _eraseBrushIsLight;

        protected ThemedWindow()
        {
            InstallEraseBackgroundHook();
            InstallWindowIcon();

            // The backdrop is not attached in the constructor: measured, its initialization adds a white frame that is
            // very visible during the open animation (especially in dark mode). Deferring it until the first activation,
            // after the content has rendered, hides that frame behind the already drawn content.
            Activated += OnFirstActivated;
            Closed += (_, _) => _closed = true;
        }

        /// <summary>The window has closed. Deferred first-frame work queued on the dispatcher checks this and bails out instead of touching a closed window.</summary>
        private bool _closed;

        /// <summary>
        /// Sets the window-level big and small icons on the HWND. The EXE's ApplicationIcon only affects the file/shortcut icon;
        /// WinUI 3 does not feed it into WM_GETICON, so Alt+Tab and the taskbar thumbnail would show a blank icon.
        /// The icons are loaded from the main module's resources with LR_SHARED; the system owns the handles, so DestroyIcon is neither needed nor allowed.
        /// </summary>
        private void InstallWindowIcon()
        {
            try
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                IntPtr module = GetModuleHandle(null);
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi == 0)
                    dpi = 96;

                IntPtr bigIcon = LoadImage(
                    module,
                    ApplicationIconResource,
                    IMAGE_ICON,
                    GetSystemMetricsForDpi(SM_CXICON, dpi),
                    GetSystemMetricsForDpi(SM_CYICON, dpi),
                    LR_SHARED);
                IntPtr smallIcon = LoadImage(
                    module,
                    ApplicationIconResource,
                    IMAGE_ICON,
                    GetSystemMetricsForDpi(SM_CXSMICON, dpi),
                    GetSystemMetricsForDpi(SM_CYSMICON, dpi),
                    LR_SHARED);

                if (bigIcon != IntPtr.Zero)
                    SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_BIG), bigIcon);
                if (smallIcon != IntPtr.Zero)
                    SendMessage(hwnd, WM_SETICON, new IntPtr(ICON_SMALL), smallIcon);

                if (bigIcon == IntPtr.Zero || smallIcon == IntPtr.Zero)
                    UiHost.WriteLog($"[Window] Window icon only partially loaded (big={bigIcon != IntPtr.Zero}, small={smallIcon != IntPtr.Zero})");
            }
            catch (Exception ex)
            {
                // A failure to set the icon must not break window creation; fall back to the WinUI default behavior.
                UiHost.WriteLog($"[Window] Failed to set window icon: {ex.Message}");
            }
        }

        // ───────────── Modal owned windows (shared by all modal windows) ─────────────

        private const int GWLP_HWNDPARENT = -8;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", EntryPoint = "GetWindow")] private static extern IntPtr GetRelatedWindow(IntPtr hWnd, uint cmd);
        private const uint GW_OWNER = 4;

        /// <summary>The foreground window is <paramref name="self"/>, or <paramref name="self"/> is found by walking up its owner chain (an owned window attached to it).</summary>
        private static bool ForegroundIsSelfOrOwned(IntPtr self)
        {
            for (IntPtr w = GetForegroundWindow(); w != IntPtr.Zero; w = GetRelatedWindow(w, GW_OWNER))
            {
                if (w == self) return true;
            }
            return false;
        }

        /// <summary>A modal owned window is currently open over this window (this window is disabled). Drag and drop ignores the window's enabled state, so derived windows use this to block it themselves.</summary>
        protected bool HasModalChild => ModalOwners.TryGetValue(WindowNative.GetWindowHandle(this), out ModalOwner? state) && state.Children.Count > 0;

        /// <summary>
        /// An owner (any Window, not necessarily a ThemedWindow) and the modal owned windows attached to it, in opening order (the last one is on top).
        /// Registered in <see cref="ModalOwners"/> by the owner's window handle; read and written only on the UI thread.
        /// </summary>
        private sealed class ModalOwner(Window window)
        {
            public Window Window { get; } = window;
            public List<ThemedWindow> Children { get; } = new();
        }

        private static readonly Dictionary<IntPtr, ModalOwner> ModalOwners = new();

        /// <summary>
        /// Walks up the chain of modal owned windows starting at <paramref name="window"/> and returns the topmost one (the window itself when it has none).
        /// A new dialog must be attached here to end up on top while disabling only one window: if it were attached to a window already
        /// disabled by another modal window, whichever closes first would re-enable it.
        /// Public because an app bringing itself to the foreground should activate the topmost modal window
        /// (e.g. a single-instance app's BringToForeground when it is launched again).
        /// </summary>
        public static Window TopmostModalOver(Window window)
        {
            // Skip windows that have started closing but are not yet removed from the list (their Closed handler has not run yet): attaching to a closing window fails (seen in self-tests)
            while (LastOpenModalChild(WindowNative.GetWindowHandle(window)) is { } open)
                window = open;
            return window;
        }

        /// <summary>
        /// Like <see cref="TopmostModalOver"/>, but returns what the topmost modal window shows: the <see cref="DialogWindow"/> or
        /// <see cref="ModalNavWindow"/> for a window borrowed from the pool, otherwise the window itself.
        /// </summary>
        public static object TopmostModalContentOver(Window window)
        {
            Window top = TopmostModalOver(window);
            return top is ModalHostWindow { Guest: { } guest } ? guest : top;
        }

        private static ThemedWindow? LastOpenModalChild(IntPtr ownerHwnd)
        {
            if (!ModalOwners.TryGetValue(ownerHwnd, out ModalOwner? state))
                return null;
            for (int i = state.Children.Count - 1; i >= 0; i--)
            {
                if (state.Children[i].IsModalOpen)
                    return state.Children[i];
            }
            return null;
        }

        /// <summary>A modal session is open on this window: attached to an owner and neither ended (<see cref="DetachModalOwner"/>) nor closed.</summary>
        internal bool IsModalOpen => _modal is not null && !_closed;

        // ───────────── Activation redirect for the owner (works for any Window used as owner) ─────────────
        // When an owner disabled by a modal window is activated by the system (restored from the taskbar, switched to with Alt+Tab),
        // the activation must be passed on to the topmost modal window.
        // The system does this for a mouse click on a disabled window, but not for the taskbar or Alt+Tab: those redirect to the
        // "last active popup", and a modal window that popped up while the owner was minimized was never activated, so activation
        // lands on the owner: the owner's title bar looks active while the dialog looks inactive.
        // This used to live in ThemedWindow's own window procedure, which did not work when the owner is not a ThemedWindow
        // (e.g. a plain Window). Now the modal window installs a system subclass on the owner when it opens (SetWindowSubclass,
        // coexisting with any subclassing the owner does itself), removed when the last modal window closes, or in the owner's
        // WM_NCDESTROY if the owner is destroyed first.

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr idSubclass, UIntPtr refData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr idSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>The subclass procedure delegate must stay alive for good (the system only stores the function pointer).</summary>
        private static readonly SubclassProc OwnerSubclass = OwnerSubclassProc;
        private static readonly UIntPtr OwnerSubclassId = new(0x574B4D4F);   // 'WKMO': this library's owner subclass, distinct from the owner's own subclasses

        /// <summary>Registers a modal owned window; installs the activation redirect on the owner when this is its first one.</summary>
        private static void AddModalChild(Window owner, IntPtr ownerHwnd, ThemedWindow child)
        {
            if (!ModalOwners.TryGetValue(ownerHwnd, out ModalOwner? state))
            {
                state = new ModalOwner(owner);
                ModalOwners[ownerHwnd] = state;
#if !SHIPPING
                // For A/B testing (non-shipping builds only): when set, the activation redirect is not installed, to reproduce the pre-fix behavior
                if (Environment.GetEnvironmentVariable("WINUIKIT_NO_OWNER_REDIRECT") == "1")
                    UiHost.WriteTrace("[Window] A/B test: not installing the activation redirect on the owner");
                else
#endif
                if (!SetWindowSubclass(ownerHwnd, OwnerSubclass, OwnerSubclassId, UIntPtr.Zero))
                    UiHost.WriteLog($"[Window] Failed to install the activation redirect on the owner (err={Marshal.GetLastWin32Error()})");
            }
            state.Children.Add(child);
        }

        /// <summary>Unregisters an owned window (when it closes; every close path goes through its Closed); removes the activation redirect once the owner has no modal windows left.</summary>
        private static void RemoveModalChild(IntPtr ownerHwnd, ThemedWindow child)
        {
            if (!ModalOwners.TryGetValue(ownerHwnd, out ModalOwner? state))
                return;   // The owner was destroyed first and the subclass went with it (see WM_NCDESTROY in OwnerSubclassProc)
            state.Children.Remove(child);
            if (state.Children.Count > 0)
                return;
            ModalOwners.Remove(ownerHwnd);
            if (!RemoveWindowSubclass(ownerHwnd, OwnerSubclass, OwnerSubclassId))
                UiHost.WriteLog($"[Window] Failed to remove the activation redirect from the owner (err={Marshal.GetLastWin32Error()})");
        }

        private static IntPtr OwnerSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData)
        {
            // When activated while minimized, the owned window is still hidden; redirect once the owner is restored and still in the foreground.
            // Restoration is detected with WM_WINDOWPOSCHANGED, not WM_SIZE: WM_SIZE is generated by DefWindowProc while handling
            // WM_WINDOWPOSCHANGED, so it never arrives when the owner handles WM_WINDOWPOSCHANGED itself without passing it on
            // (measured on such an owner: restoring from the taskbar and Alt+Tab only delivered WM_ACTIVATE with the "minimized" flag, and no WM_SIZE afterwards)
            if ((msg == WM_ACTIVATE && (wParam.ToInt64() & 0xFFFF) != WA_INACTIVE && (wParam.ToInt64() >> 16 & 0xFFFF) == 0
                 || msg == WM_WINDOWPOSCHANGED && !IsIconic(hWnd) && GetForegroundWindow() == hWnd && !IsWindowEnabled(hWnd))
                && ModalOwners.TryGetValue(hWnd, out ModalOwner? state) && LastOpenModalChild(hWnd) is not null)
            {
                state.Window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    // Do not redirect if focus moved elsewhere meanwhile, the modal window closed, or the owner was re-enabled
                    // (a closing modal window ends modality before activating the owner, see EndModal)
                    if (GetForegroundWindow() != hWnd || LastOpenModalChild(hWnd) is not { } child || IsWindowEnabled(hWnd)) return;
                    IntPtr top = WindowNative.GetWindowHandle(TopmostModalOver(child));
                    if (top != hWnd && !SetForegroundWindow(top))
                        UiHost.WriteLog("[Window] Disabled owner was activated; failed to pass activation to the modal window (SetForegroundWindow)");
                });
            }

            if (msg == WM_NCDESTROY)
            {
                // The owner is destroyed before its owned windows (e.g. destroyed directly from outside): remove the subclass and the
                // registration; when the owned window closes later, RemoveModalChild finds no registration and simply returns
                ModalOwners.Remove(hWnd);
                RemoveWindowSubclass(hWnd, OwnerSubclass, OwnerSubclassId);
            }
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// Finds the window a XamlRoot belongs to (works for window content, pages and elements in popups); returns null if not found.
        /// Compares the XamlRoot itself and its root element first (the same native object maps to the same managed object in the
        /// projection), then falls back to the ContentIslandEnvironment's window id: comparing only the window id fails for some
        /// secondary windows (measured in self-tests; the main window is found).
        /// </summary>
        internal static ThemedWindow? FromXamlRoot(XamlRoot? root)
        {
            if (root is null)
                return null;
            UIElement? rootContent = root.Content;
            foreach (ThemedWindow window in Instances.Values)
            {
                if (window._closed || window.Content is not { } content)
                    continue;
                if (ReferenceEquals(content, rootContent) || ReferenceEquals(content.XamlRoot, root))
                    return window;
            }
            if (root.ContentIslandEnvironment is { } environment)
            {
                ulong id = environment.AppWindowId.Value;
                foreach (ThemedWindow window in Instances.Values)
                {
                    if (!window._closed && window.AppWindow.Id.Value == id)
                        return window;
                }
            }
            return null;
        }

#if !SHIPPING
        /// <summary>For AOT self-tests: how each comparison step of <see cref="FromXamlRoot"/> went (to diagnose why the owning window was not found).</summary>
        internal static string DescribeFromXamlRootForSelfTest(XamlRoot? root)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"root={(root is null ? "null" : "set")} root.Content={root?.Content?.GetType().Name ?? "null"} ");
            sb.Append($"islandWindowId={(root?.ContentIslandEnvironment is { } env ? env.AppWindowId.Value.ToString("X") : "null")}; ");
            foreach (ThemedWindow w in Instances.Values)
            {
                if (w._closed) { sb.Append($"[{w.GetType().Name} closed]"); continue; }
                UIElement? c = w.Content;
                sb.Append($"[{w.GetType().Name} windowId={w.AppWindow.Id.Value:X} content={c?.GetType().Name ?? "null"} sameContent={ReferenceEquals(c, root?.Content)} sameRoot={ReferenceEquals(c?.XamlRoot, root)}]");
            }
            return sb.ToString();
        }
#endif

        /// <summary>A modal owned window attached to this window has just closed (already removed from the list; the SDK re-enables this window afterwards).</summary>
        protected virtual void OnModalChildClosed() { }

        /// <summary>
        /// Makes this window a [modal] owned window of <paramref name="owner"/>: it always floats above the owner and minimizes/restores with it.
        /// Modality follows the standard WinUI 3 Gallery approach (ModalWindow.xaml.cs): set GWLP_HWNDPARENT to the owner →
        /// <c>OverlappedPresenter.CreateForDialog()</c> + <c>IsModal = true</c> → SetPresenter, and when showing, AppWindow.Show() then Activate()
        /// (see ModalNavWindow.ShowModal); the Windows App SDK disables and re-enables the owner. Closing the owner also closes this window.
        /// <b>Must be called before the first Activate</b>, otherwise the Z-order relationship does not take effect.
        /// <para>
        /// [Slider value jumping back and forth while held: fix it in WinUI, do not work around it here] Once the owner is disabled it
        /// never receives "pointer exited", and the stale mouse message XAML cached for it is periodically replayed to the control in this
        /// window that has pointer capture (microsoft-ui-xaml#10758). A patched Microsoft.ui.xaml.dll fixes this (whenever any window
        /// receives a mouse message, the caches of all windows are cleared), so this method sets IsModal immediately, as the Gallery does.
        /// An app-level workaround that "deferred disabling the owner until the cursor leaves it" was tried before: the gap had to
        /// block activation, keyboard input, the title bar menu and the close flow, kept needing more patches, and introduced new bugs
        /// (double clicks leaking into the owner, the owner getting stuck disabled). It was removed entirely in favor of the patched WinUI;
        /// do not bring it back.
        /// </para>
        /// <para>
        /// Initial focus (no focus rectangle when opened via a keyboard shortcut, and a focused element when opened with the mouse) and
        /// the caption buttons (drawn by this base class, see InstallCaptionButtons) are also handled here in the base class.
        /// </para>
        /// <para>
        /// [End modality before closing] The Gallery calls owner.Activate() in Closed, but at that point the SDK has not re-enabled the owner
        /// yet (that happens one tick later): activating a disabled window puts system keyboard focus on the top-level window instead of
        /// WinUI's input child window, and all keyboard shortcuts stop working; not activating it hands the foreground to another app
        /// (measured; the Gallery loses focus the same way).
        /// Following the Win32 EndDialog order: while this window is still visible, end modality first (re-enabling the owner), then
        /// explicitly activate the owner (see EndModal).
        /// Clicking ✕ reaches AppWindow.Closing first; a programmatic Close() (Esc, Cancel) skips Closing, and this window is still in the
        /// foreground when Closed fires, so it is done there (measured: focus returns to the input child window on both paths).
        /// </para>
        /// No dimming overlay is added.
        /// </summary>
        protected void AttachModalOwner(Window owner)
        {
            if (_modal is not null)
                throw new InvalidOperationException("This window is already attached to a modal owner; end that session first");
            IntPtr ownerHwnd = WindowNative.GetWindowHandle(owner);
            IntPtr selfHwnd = WindowNative.GetWindowHandle(this);
            var session = new ModalSession(owner, ownerHwnd, owner as ThemedWindow);

            // Close this window together with the owner (system shutdown, external exit request); since the owner is closing,
            // there is no need to end modality and hand activation back
            session.CloseWithOwner = (_, _) =>
            {
                session.OwnerClosing = true;
                try { Close(); } catch { /* ignore if already closed */ }
            };

            SetWindowLongPtr(selfHwnd, GWLP_HWNDPARENT, ownerHwnd);
            // The only differences from the Gallery: the window is resizable and has a minimum size (derived windows set these on
            // the default presenter in InitializeShell; they are copied over here)
            var dialog = OverlappedPresenter.CreateForDialog();
            if (AppWindow.Presenter is OverlappedPresenter current)
            {
                dialog.IsResizable = current.IsResizable;
                dialog.PreferredMinimumWidth = current.PreferredMinimumWidth;
                dialog.PreferredMinimumHeight = current.PreferredMinimumHeight;
            }
            // No system title bar: the system caption buttons are a separate Windows App SDK child window (ReunionWindowingCaptionControls)
            // with two confirmed bugs: a tooltip left behind after closing the window via keyboard, and the hover background getting stuck
            // after the window is deactivated (microsoft-ui-xaml#10383). The buttons are drawn by this class instead (InstallCaptionButtons).
            dialog.SetBorderAndTitleBar(true, false);
            dialog.IsModal = true;
            // A topmost owner (e.g. a window kept above an app's own topmost menu) must stay topmost. Win32 makes a window's owners
            // non-topmost along with it ("when a topmost window is made non-topmost, its owners and owned windows are too", measured),
            // so a non-topmost presenter here would drop the owner below the other topmost windows. Make the dialog topmost instead.
            bool ownerTopmost = IsTopmost(ownerHwnd);
            if (ownerTopmost)
                dialog.IsAlwaysOnTop = true;
            AppWindow.SetPresenter(dialog);
            if (ownerTopmost)
                UiHost.WriteTrace($"[Window] Modal window over a topmost owner: owner still topmost after presenter switch = {IsTopmost(ownerHwnd)}");

            // End modality and hand activation back to the owner, in Win32 EndDialog order: re-enable the owner first, then explicitly
            // activate it before this window is hidden.
            // If modality is only ended without activating, the system activates the "next window" in Z order as soon as this window
            // hides: after switching back and forth with Alt+Tab that is another app, and the owner drops to the background
            // (logged: at Closed the foreground was still this window and the owner was enabled; one tick later another app was in front).
            // The Gallery's owner.Activate() in Closed has the same problem.
            // Activating the owner while it is still disabled puts keyboard focus on the top-level window and shortcuts stop working
            // (measured), so modality must be ended before activating (see EndModal).
            // The Closing / Closed handlers are installed once per window, on the first attach: a reused window (see ModalHostWindow) goes
            // through many sessions. Registering them here rather than in the constructor keeps them after the derived window's own
            // Closing handler, which may cancel the close (a dialog routing Alt+F4 through its own close flow); modality must not end then.
            if (!_modalHandlersInstalled)
            {
                _modalHandlersInstalled = true;
                AppWindow.Closing += (_, e) =>
                {
                    if (_modal is { OwnerClosing: false } m && !e.Cancel) EndModal(m);
                };
                Closed += (_, _) =>
                {
                    if (_modal is { } m) FinishModalSession(m, hide: false);
                };
            }
            _modal = session;
            owner.Closed += session.CloseWithOwner;

            // Initial focus: when opened via a keyboard shortcut the last input came from the keyboard, so WinUI draws a keyboard focus
            // rectangle on the initial focus. After the first activation, re-record that focus as pointer focus: the focused element stays
            // the same but no rectangle is drawn; pressing Tab afterwards still shows the focus rectangle as usual. If nothing has initial
            // focus, one is assigned (see below).
            TypedEventHandler<object, WindowActivatedEventArgs>? clearInitialFocusVisual = null;
            clearInitialFocusVisual = (_, e) =>
            {
                if (e.WindowActivationState == WindowActivationState.Deactivated) return;
                Activated -= clearInitialFocusVisual;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    // When the window is closed right after opening, this tick lands after Closed: reading Content then throws
                    // "window closed" (0x800710DD), which nobody catches in a dispatcher callback → the process crashes with
                    // 0xC000027B (found by an AOT self-test; JIT has the same issue). Also skip if this session already ended.
                    if (_closed || !ReferenceEquals(_modal, session) || Content?.XamlRoot is not { } root)
                        return;
                    object? focused = FocusManager.GetFocusedElement(root);
                    if (focused is Control { FocusState: FocusState.Keyboard } keyboardFocused)
                        keyboardFocused.Focus(FocusState.Pointer);
                    // When opened with the mouse, no element may have focus: key events have no origin and Esc etc. are never
                    // received (measured: a window could not be closed with Esc because of this). Put focus on the first focusable
                    // element, recorded as pointer focus so no rectangle is drawn.
                    else if (focused is null && FocusManager.FindFirstFocusableElement(Content) is Control first)
                        first.Focus(FocusState.Pointer);
                });
            };
            Activated += clearInitialFocusVisual;

            if (ownerTopmost)
            {
                TypedEventHandler<object, WindowActivatedEventArgs>? traceTopmost = null;
                traceTopmost = (_, e) =>
                {
                    if (e.WindowActivationState == WindowActivationState.Deactivated) return;
                    Activated -= traceTopmost;
                    UiHost.WriteTrace($"[Window] Modal window over a topmost owner, first activation: owner topmost = {IsTopmost(ownerHwnd)}, self topmost = {IsTopmost(selfHwnd)}");
                };
                Activated += traceTopmost;
            }

            AddModalChild(owner, ownerHwnd, this);
        }

        /// <summary>One modal session: the owner this window is attached to, from <see cref="AttachModalOwner"/> until it ends or the window closes.</summary>
        private sealed class ModalSession(Window owner, IntPtr ownerHwnd, ThemedWindow? ownerThemed)
        {
            public Window Owner { get; } = owner;
            public IntPtr OwnerHwnd { get; } = ownerHwnd;
            public ThemedWindow? OwnerThemed { get; } = ownerThemed;
            public bool OwnerClosing { get; set; }
            public bool HandedBack { get; set; }
            public TypedEventHandler<object, WindowEventArgs>? CloseWithOwner { get; set; }
        }

        private ModalSession? _modal;
        private bool _modalHandlersInstalled;

        /// <summary>
        /// Ends modality and hands activation back to the owner, in Win32 EndDialog order: re-enable the owner first, then explicitly
        /// activate it while this window is still visible.
        /// If modality is only ended without activating, the system activates the "next window" in Z order as soon as this window
        /// hides: after switching back and forth with Alt+Tab that is another app, and the owner drops to the background
        /// (logged: at Closed the foreground was still this window and the owner was enabled; one tick later another app was in front).
        /// The Gallery's owner.Activate() in Closed has the same problem.
        /// </summary>
        private void EndModal(ModalSession session)
        {
            if (AppWindow.Presenter is OverlappedPresenter { IsModal: true } p)
                p.IsModal = false;
            if (session.HandedBack) return;
            session.HandedBack = true;
            // Hand back if the foreground is this window or a window owned by it (when this window is closed by code, a dialog on
            // top of it may still be in the foreground and is closed along with it): checking only this window let the foreground
            // fall to another app in the latter case (seen in self-tests: a window closed while its confirmation dialog was open)
            // Never hand back to a hidden owner (e.g. an app whose main window stays in the tray): Window.Activate() would show it.
            // The system then activates the next window in Z order, which is what the user had before the dialog appeared
            IntPtr self = WindowNative.GetWindowHandle(this);
            if (ForegroundIsSelfOrOwned(self) && IsWindowVisible(session.OwnerHwnd))
                session.Owner.Activate();
            // The app is in the background: don't take the foreground, but still move this thread's active window back to the owner.
            // A window that is only hidden (a reused window, see ModalHostWindow) would otherwise stay the thread's active window, and
            // activating it again on its next use sends no activation at all (measured: the reused dialog's Activated never fired).
            else if (IsWindowVisible(session.OwnerHwnd) && ActiveIsSelfOrOwned(self))
                SetActiveWindow(session.OwnerHwnd);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        /// <summary>This thread's active window is <paramref name="self"/>, or a window owned by it.</summary>
        private static bool ActiveIsSelfOrOwned(IntPtr self)
        {
            for (IntPtr w = GetActiveWindow(); w != IntPtr.Zero; w = GetRelatedWindow(w, GW_OWNER))
            {
                if (w == self) return true;
            }
            return false;
        }

        /// <summary>
        /// Before a hidden window is shown again: if it is still this thread's active window (left that way when it was hidden while the
        /// owner was hidden too), hand the active state to <paramref name="owner"/> first, so that activating this window sends a real
        /// activation (Activated, initial focus).
        /// </summary>
        internal void ReleaseStaleActivation(Window owner)
        {
            IntPtr self = WindowNative.GetWindowHandle(this);
            if (GetActiveWindow() == self)
                SetActiveWindow(WindowNative.GetWindowHandle(owner));
        }

        private void FinishModalSession(ModalSession session, bool hide)
        {
            if (!ReferenceEquals(_modal, session)) return;
            _modal = null;
            if (session.CloseWithOwner is { } closeWithOwner)
                session.Owner.Closed -= closeWithOwner;
            if (!session.OwnerClosing) EndModal(session);
            if (hide) AppWindow.Hide();
            RemoveModalChild(session.OwnerHwnd, this);
            session.OwnerThemed?.OnModalChildClosed();
        }

        /// <summary>
        /// Ends the current modal session without closing the window: ends modality and hands activation back to the owner (same as on
        /// close), then hides the window, so it can be attached again later (a reused window, see ModalHostWindow). The window stays owned
        /// by that owner while hidden. Does nothing when no session is open.
        /// </summary>
        protected void DetachModalOwner()
        {
            if (_modal is { } session)
                FinishModalSession(session, hide: true);
        }

        /// <summary>
        /// Closes the modal windows attached to this window, as closing this window would (Win32 destroys owned windows with their owner):
        /// used when a reused window ends its session without closing.
        /// </summary>
        internal void CloseModalChildren()
        {
            if (!ModalOwners.TryGetValue(WindowNative.GetWindowHandle(this), out ModalOwner? state))
                return;
            foreach (ThemedWindow child in state.Children.ToArray())
            {
                if (child is ModalHostWindow { Guest: { } guest })
                    guest.OnOwnerClosing();
                else if (!child._closed)
                    child.Close();
            }
        }

        /// <summary>The owner of the current modal session (null when none is open).</summary>
        protected Window? ModalOwnerWindow => _modal?.Owner;

        /// <summary>
        /// Changes the caption button height (DIP) after <see cref="InstallCaptionButtons"/>: a reused window shows content with a standard
        /// or a tall title bar in turn.
        /// </summary>
        protected void SetCaptionButtonHeight(double height)
        {
            _captionButtonHeight = height;
            foreach (Button? button in new[] { _minimizeButton, _maximizeButton, _closeButton })
                if (button is not null) button.Height = height;
            RefreshCaptionButtons();
        }

        // ───────────── Custom-drawn caption buttons (shared by all windows) ─────────────
        // The system title bar is removed (SetBorderAndTitleBar(true, false), so the system caption button child window no longer appears),
        // and depending on the presenter's IsMinimizable / IsMaximizable either all three buttons or only the close button are shown.
        // Minimize and close are plain XAML buttons; the maximize button is registered as a Windows App SDK non-client Maximize region
        // (InputNonClientPointerSource.SetRegionRects), so the system treats it as the maximize button: hovering shows the Windows 11
        // snap layouts, clicking maximizes/restores.
        // Once registered, pointer input in that region no longer reaches XAML; hover/pressed visuals are switched from
        // InputNonClientPointerSource's pointer events.

        /// <summary>Caption button width (DIP), same as the system buttons.</summary>
        protected const double CaptionButtonWidth = 46;

        /// <summary>
        /// Title bar heights (DIP), same as the system's <c>TitleBarHeightOption.Tall</c> / <c>Standard</c>.
        /// Per the Windows title bar guidance, use the tall height when the title bar hosts interactive content (back/pane button, search box, menus)
        /// and the standard height when it only shows a title and the caption buttons.
        /// </summary>
        protected const double TallTitleBarHeight = 48, StandardTitleBarHeight = 32;

        private double _captionButtonHeight = TallTitleBarHeight;

        private const uint WM_SYSCOMMAND = 0x0112;
        private const int SC_CLOSE = 0xF060;

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private StackPanel? _captionPanel;
        private Button? _minimizeButton, _maximizeButton, _closeButton;
        private FontIcon? _maximizeIcon;
        private InputNonClientPointerSource? _nonClientSource;
        private NonClientRegionKind? _pointerNonClientRegion;   // Kind of non-client region the pointer is currently in (null after leaving the non-client area)

        /// <summary>Width currently taken by the caption buttons (DIP): 138 for three buttons, 46 for close only, 0 without a title bar (e.g. full screen).</summary>
        protected double CaptionButtonsWidth { get; private set; }

        /// <summary>The number or position of the buttons changed (derived windows use this to keep the right side of the title bar clear and recompute drag regions).</summary>
        protected event Action? CaptionButtonsChanged;

        private bool _captionButtonsHidden;

        /// <summary>
        /// Set to true when a derived window enters a title-bar-less state on its own (e.g. a self-implemented full screen where the
        /// presenter is still Overlapped): no caption buttons are shown and no maximize region is registered.
        /// </summary>
        protected bool CaptionButtonsHidden
        {
            get => _captionButtonsHidden;
            set
            {
                if (_captionButtonsHidden == value) return;
                _captionButtonsHidden = value;
                if (value) HideCaptionTip();
                RefreshCaptionButtons();
            }
        }

        /// <summary>
        /// Places the caption buttons in <paramref name="host"/> (aligned top-right) and returns the button strip so the derived window can set its row/column.
        /// Call once; when the presenter changes (modal, entering/leaving full screen) the visible buttons are recomputed automatically.
        /// <paramref name="height"/> is the button height, which should equal the title bar height
        /// (<see cref="TallTitleBarHeight"/> or <see cref="StandardTitleBarHeight"/>).
        /// </summary>
        protected FrameworkElement InstallCaptionButtons(Panel host, double height = TallTitleBarHeight)
        {
            _captionButtonHeight = height;
            _minimizeButton = CreateCaptionButton("\uE921", "ms-appx:///WinUIKit/CaptionButtonResources.xaml", () => UiText.Get("Caption_Minimize"), tooltip: true, out _);
            _maximizeButton = CreateCaptionButton("\uE922", "ms-appx:///WinUIKit/CaptionButtonResources.xaml", () => UiText.Get("Caption_Maximize"), tooltip: false, out _maximizeIcon);
            _closeButton = CreateCaptionButton("\uE8BB", "ms-appx:///WinUIKit/CaptionCloseButtonResources.xaml", () => UiText.Get("Common_Close"), tooltip: true, out _);
            _minimizeButton.Click += (_, _) => (AppWindow.Presenter as OverlappedPresenter)?.Minimize();
            // No Click handler on the maximize button: clicks are handled only by the system (the registered Maximize region, see UpdateMaximizeRegion).
            // A XAML fallback handler used to be attached; while the region exists XAML sometimes receives the same click, so the system and the
            // fallback each toggled once → "maximizes and immediately restores" (confirmed in logs: system press region=Maximize → XAML Click →
            // system SC_MAXIMIZE). Removed; do not add it back.
            _closeButton.Click += (_, _) => OnCaptionCloseClicked();

            _captionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            _captionPanel.Children.Add(_minimizeButton);
            _captionPanel.Children.Add(_maximizeButton);
            _captionPanel.Children.Add(_closeButton);
            Canvas.SetZIndex(_captionPanel, 100);
            host.Children.Add(_captionPanel);

            _nonClientSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            _nonClientSource.PointerEntered += (_, e) => OnNonClientPointer(e, "PointerOver");
            _nonClientSource.PointerExited += (_, e) => OnNonClientPointer(e, "Normal");
            _nonClientSource.PointerPressed += (_, e) => OnNonClientPointer(e, "Pressed");
            _nonClientSource.PointerReleased += (_, e) => OnNonClientPointer(e, "PointerOver");

            // The registered maximize region gets silently cleared by the SDK (microsoft-ui-xaml#10379, unfixed; observed both after startup and
            // after the mouse moves across the title bar), and with it the snap layouts flyout disappears. So whenever the regions change,
            // the maximize region is registered again.
            // [Coalesce into the next tick, check before re-registering] While resizing, the SDK fires this event about 7 times per step.
            // Re-registering unconditionally every time (each SetRegionRects takes about 1 ms), plus repeated drag region registration, meant
            // a dozen-plus registrations per layout pass; most of the UI thread went into this and resizing ran at only 30-40 fps
            // (timing probe: 1000 maximize region and 277 drag region registrations per 2 seconds, for only 86 window layouts).
            // Now a burst of events queues a single check on the UI thread: read the current region and re-register only if it was really
            // cleared. Missing the maximize region for one tick does not matter; during a border drag nothing is registered at all, see
            // _inSizeMove. When the region is empty GetRegionRects throws a null reference in the projection; CurrentRegionRects treats that as an empty array.
            // This event fires synchronously inside SetRegionRects; the one caused by our own maximize region registration is filtered out
            // by _settingRegions, so there is no loop.
            _nonClientSource.RegionsChanged += (_, _) =>
            {
                if (_closed || _settingRegions || _inSizeMove || _maximizeRegion.Width <= 0 || _maximizeCheckQueued) return;
                _maximizeCheckQueued = DispatcherQueue.TryEnqueue(() =>
                {
                    _maximizeCheckQueued = false;
                    if (_closed || _inSizeMove || _maximizeRegion.Width <= 0 || MaximizeRegionIntact()) return;
                    SetMaximizeRegion(_maximizeRegion);
                });
            };
            _captionPanel.LayoutUpdated += (_, _) => UpdateMaximizeRegion();
            AppWindow.Changed += (_, e) =>
            {
                if (e.DidPresenterChange || e.DidSizeChange) RefreshCaptionButtons();
            };
            RefreshCaptionButtons();
            InstallTitleBarActivation();
            return _captionPanel;
        }

        // ── Gray title bar while the window is inactive (consistent with the system caption buttons and the WinUI 3 Gallery) ──
        // Modeled on the WinUI TitleBar control (microsoft-ui-xaml controls/dev/TitleBar/TitleBar.cpp): the deactivation signal comes from
        // InputActivationListener, which also reports Deactivated when the window is disabled by a modal child (focus is in the child);
        // title text is drawn in its active color at TitleBarDeactivatedOpacity alpha, and other content's opacity becomes TitleBarDeactivatedOpacity
        // (value in TitleBarResources.xaml).
        // The caption buttons' hover/pressed colors come from the button template's own states; hovering still lights them up while
        // inactive, like the system caption buttons.

        private static ResourceDictionary? _titleBarResources;
        private InputActivationListener? _activationListener;

        /// <summary>Whether the window is currently inactive (the title bar should look grayed out).</summary>
        protected bool TitleBarDeactivated { get; private set; }

        private static ResourceDictionary TitleBarResources =>
            _titleBarResources ??= new ResourceDictionary { Source = new Uri("ms-appx:///WinUIKit/TitleBarResources.xaml") };

        /// <summary>Opacity of title bar content other than text (buttons, combo boxes, etc.) while inactive.</summary>
        protected static double TitleBarDeactivatedOpacity =>
            TitleBarResources.TryGetValue("TitleBarDeactivatedOpacity", out object value) && value is double opacity ? opacity : 0.5;

        /// <summary>
        /// Brush for the title text while inactive: the active color at <see cref="TitleBarDeactivatedOpacity"/> alpha
        /// (high contrast uses the system's gray text color instead). Returns null when there is nothing to derive from,
        /// and the caller then leaves the active color in place.
        /// </summary>
        /// <param name="activeForeground">The brush the title text is painted with while the window is active.</param>
        protected static Brush? TitleBarDeactivatedForeground(Brush? activeForeground)
        {
            if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                return new SolidColorBrush(new Windows.UI.ViewManagement.UISettings()
                    .UIElementColor(Windows.UI.ViewManagement.UIElementType.GrayText));
            if (activeForeground is not SolidColorBrush solid) return null;
            Windows.UI.Color color = solid.Color;
            return new SolidColorBrush(Windows.UI.Color.FromArgb(
                (byte)Math.Round(color.A * TitleBarDeactivatedOpacity), color.R, color.G, color.B));
        }

        // [Why the inactive color is derived from the active one instead of read from a framework brush] The WinUI TitleBar control
        // paints inactive title text with TextFillColorTertiaryBrush, which exists once per theme - so the right instance has to be
        // picked by the theme the title bar is drawn in. Neither route to it works from code: a StaticResource inside a resource
        // dictionary binds when that dictionary loads, and looking the name up in the framework's ThemeDictionaries returns the entry
        // for the application theme whichever theme key is asked for (measured: asking for the light and the dark entry both returned
        // #72000000, the light one). The application theme is fixed for the life of the process - Application.RequestedTheme cannot be
        // changed at runtime, so windows switch themes through their root element's RequestedTheme - and the title text was therefore
        // left in the start-up theme's color after a switch: dark text on a dark title bar, and nearly invisible until a restart.
        // Only an element in the live tree resolves ThemeResource by its own theme, and the active title brush is exactly such a value.
        // Halving its alpha matches the framework in the light theme (#72000000 is #E4000000 at half alpha) and is within a few percent
        // of it in the dark theme (50% vs 54%), and it is the same factor the rest of the title bar already dims by.


        /// <summary>
        /// The inactive state or the theme changed; derived windows switch their own title text and title bar controls to / from the
        /// inactive look here (based on <see cref="TitleBarDeactivated"/>, using the brush from <see cref="TitleBarDeactivatedForeground"/>).
        /// </summary>
        protected virtual void OnTitleBarActivationChanged() { }

        private void InstallTitleBarActivation()
        {
            _activationListener = InputActivationListener.GetForWindowId(AppWindow.Id);
            _activationListener.InputActivationChanged += OnInputActivationChanged;
            Closed += (_, _) =>
            {
                if (_activationListener is null) return;
                _activationListener.InputActivationChanged -= OnInputActivationChanged;
                _activationListener = null;
            };
            TitleBarDeactivated = _activationListener.State == InputActivationState.Deactivated;
            ApplyTitleBarActivation();
        }

        private void OnInputActivationChanged(InputActivationListener sender, InputActivationListenerActivationChangedEventArgs args)
        {
            SetTitleBarDeactivated(sender.State == InputActivationState.Deactivated);
        }

        // [Second signal: WM_NCACTIVATE] Clicking the taskbar right at startup: the window is activated within its thread when shown
        // (WM_ACTIVATE active) but the taskbar is the foreground, so the title bar is drawn inactive; when the window is then clicked and
        // becomes foreground, the system only sends WM_NCACTIVATE(TRUE) and no WM_ACTIVATE (by its bookkeeping the window was already
        // active), InputActivationListener stays silent, and the title bar stays gray (confirmed in logs). The WinUI TitleBar control uses
        // the same signal and has the same problem.
        // WM_NCACTIVATE is exactly the message by which the system decides whether to draw the title bar active or inactive; whichever
        // signal arrives last wins. An owner disabled by a modal child also receives NCACTIVATE(FALSE).
        private void SetTitleBarDeactivated(bool deactivated)
        {
            if (_closed || deactivated == TitleBarDeactivated) return;
            TitleBarDeactivated = deactivated;
            ApplyTitleBarActivation();
        }

        private void ApplyTitleBarActivation()
        {
            if (_closed) return;
            foreach (Button? button in new[] { _minimizeButton, _maximizeButton, _closeButton })
            {
                if (button is null) continue;
                // Clear first, so the color read back is the template's own (active) one rather than the dimmed brush set last time.
                button.ClearValue(Control.ForegroundProperty);
                if (TitleBarDeactivated && TitleBarDeactivatedForeground(button.Foreground) is Brush gray)
                    button.Foreground = gray;
            }
            OnTitleBarActivationChanged();
        }

        /// <summary>The close button was clicked. By default takes the same path as the system ✕ (WM_SYSCOMMAND/SC_CLOSE → AppWindow.Closing → close).</summary>
        protected virtual void OnCaptionCloseClicked() =>
            PostMessage(WindowNative.GetWindowHandle(this), WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero);

        /// <summary>The pointer is currently in the system's non-client area (border, drag region, maximize region); maintained from non-client pointer events.</summary>
        private bool _pointerInNonClient;

        private Button CreateCaptionButton(string glyph, string resources, Func<string> name, bool tooltip, out FontIcon icon)
        {
            icon = new FontIcon { Glyph = glyph, FontSize = 10 };
            var button = new Button
            {
                Width = CaptionButtonWidth,
                Height = _captionButtonHeight,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                IsTabStop = false,
                Content = icon,
            };
            // Hover/pressed colors use lightweight styling pointing at theme resources (see the comments in each resource file); no hard-coded colors
            button.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(resources) });
            UiHost.Track(button, Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, name);
            // The resize area at the window edge overlaps the buttons by a few pixels: with the pointer there the system reports "entered border"
            // (the cursor is a resize arrow), and then XAML reports "entered button" and lights the button up (seen in logs). While the pointer is
            // still in the non-client area the button is forced back to normal; when it moves onto the button the system first reports
            // "left border", after which hover works as usual.
            button.PointerEntered += (_, _) =>
            {
                // Only force back to normal when the pointer is in some other non-client region (resize border, etc.). With the pointer on the
                // maximize region, XAML also reports "entered maximize button", sometimes twice per hover and a few milliseconds after the
                // non-client highlight; forcing normal unconditionally turned off the just-lit maximize button, so hover worked only
                // intermittently (confirmed in logs).
                if (_pointerInNonClient && !(button == _maximizeButton && _pointerNonClientRegion == NonClientRegionKind.Maximize))
                    VisualStateManager.GoToState(button, "Normal", true);
                else if (tooltip) ScheduleCaptionTip(name);
            };
            if (tooltip)
            {
                // The button handles presses itself, so also listen for already-handled events
                button.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => HideCaptionTip()), true);
                button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => HideCaptionTip()), true);
                button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => HideCaptionTip()), true);
                // The tooltip is transparent to the mouse (see ShowCaptionTip), so moving onto it does not send "exited" and the button stays
                // highlighted; like the system tooltip, hide it as soon as the cursor touches it
                button.PointerMoved += (_, _) => { if (CursorOnCaptionTip()) HideCaptionTip(); };
            }
            return button;
        }

        // ── Caption button tooltips: the Win32 tooltip control (tooltips_class32), the same one the system caption buttons use ──
        // Not WinUI ToolTip: its popup window (PopupWindowSiteBridge) captures the mouse, and per Win11 guidance the tooltip stays visible
        // when the mouse moves onto it (microsoft-ui-xaml#6327); since the buttons sit against the window's top and right edges, the tooltip
        // would cover the resize border outside the window. The Win32 tooltip hides as soon as the mouse touches it (the moment it covers
        // the button, the button receives "pointer exited").
        // Tracking mode (TTF_TRACK | TTF_ABSOLUTE) driven by the buttons' pointer events: show delay and auto-pop time use the tooltip
        // control's own system defaults; the tooltip is placed below the cursor.
        // Requires Common Controls 6 (app.manifest).

        private const uint WS_POPUP = 0x80000000, WS_EX_TOPMOST = 0x8, TTS_ALWAYSTIP = 0x1, TTS_NOPREFIX = 0x2;
        private const uint TTF_TRACK = 0x20, TTF_ABSOLUTE = 0x80;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TRANSPARENT = 0x20;
        private const uint TTM_TRACKACTIVATE = 0x0411, TTM_TRACKPOSITION = 0x0412, TTM_GETDELAYTIME = 0x0415,
            TTM_ADDTOOLW = 0x0432, TTM_UPDATETIPTEXTW = 0x0439;
        private const int TTDT_AUTOPOP = 2, TTDT_INITIAL = 3, SM_CYCURSOR = 14;

        [StructLayout(LayoutKind.Sequential)]
        private struct TOOLINFOW
        {
            public uint cbSize, uFlags;
            public IntPtr hwnd, uId;
            public RECT rect;
            public IntPtr hinst, lpszText, lParam, lpReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INITCOMMONCONTROLSEX { public uint dwSize, dwICC; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO { public int fIcon, xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO { public uint cbSize, flags; public IntPtr hCursor; public POINT pt; }

        [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint exStyle, string className, string? windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr SendToolInfo(IntPtr hWnd, uint msg, IntPtr wParam, ref TOOLINFOW info);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);

        private IntPtr _captionTip;
        private TOOLINFOW _captionTipInfo;
        private Func<string>? _captionTipText;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _captionTipShowTimer, _captionTipPopTimer;

        private bool EnsureCaptionTip()
        {
            if (_captionTip != IntPtr.Zero) return true;
            var icc = new INITCOMMONCONTROLSEX { dwSize = (uint)Marshal.SizeOf<INITCOMMONCONTROLSEX>(), dwICC = 0xFF /* ICC_WIN95_CLASSES */ };
            InitCommonControlsEx(ref icc);
            IntPtr owner = WindowNative.GetWindowHandle(this);
            _captionTip = CreateWindowEx(WS_EX_TOPMOST, "tooltips_class32", null, WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
                0, 0, 0, 0, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_captionTip == IntPtr.Zero)
            {
                UiHost.WriteLog($"[Window] Failed to create the caption button tooltip (err={Marshal.GetLastWin32Error()})");
                return false;
            }
            _captionTipInfo = new TOOLINFOW
            {
                cbSize = (uint)Marshal.SizeOf<TOOLINFOW>(),
                uFlags = TTF_TRACK | TTF_ABSOLUTE,
                hwnd = owner,
                uId = new IntPtr(1),
            };
            SetCaptionTipText(" ", TTM_ADDTOOLW);
            Closed += (_, _) =>
            {
                _captionTipShowTimer?.Stop();
                _captionTipPopTimer?.Stop();
                DestroyWindow(_captionTip);
                _captionTip = IntPtr.Zero;
            };
            Activated += (_, e) =>
            {
                if (e.WindowActivationState == WindowActivationState.Deactivated) HideCaptionTip();
            };
            var dq = DispatcherQueue;
            _captionTipShowTimer = dq.CreateTimer();
            _captionTipShowTimer.IsRepeating = false;
            _captionTipShowTimer.Interval = TimeSpan.FromMilliseconds(SendToolInfo(_captionTip, TTM_GETDELAYTIME, new IntPtr(TTDT_INITIAL), ref _captionTipInfo).ToInt64());
            _captionTipShowTimer.Tick += (_, _) => ShowCaptionTip();
            _captionTipPopTimer = dq.CreateTimer();
            _captionTipPopTimer.IsRepeating = false;
            _captionTipPopTimer.Interval = TimeSpan.FromMilliseconds(SendToolInfo(_captionTip, TTM_GETDELAYTIME, new IntPtr(TTDT_AUTOPOP), ref _captionTipInfo).ToInt64());
            _captionTipPopTimer.Tick += (_, _) => HideCaptionTip();
            return true;
        }

        private void SetCaptionTipText(string text, uint message)
        {
            IntPtr buffer = Marshal.StringToHGlobalUni(text);
            try
            {
                _captionTipInfo.lpszText = buffer;
                SendToolInfo(_captionTip, message, IntPtr.Zero, ref _captionTipInfo);   // The tooltip control keeps its own copy of the text
            }
            finally
            {
                _captionTipInfo.lpszText = IntPtr.Zero;
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void ScheduleCaptionTip(Func<string> text)
        {
            if (_closed || !EnsureCaptionTip()) return;
            HideCaptionTip();
            _captionTipText = text;
            _captionTipShowTimer!.Start();
        }

        private void ShowCaptionTip()
        {
            if (_closed || _captionTipText is null || _pointerInNonClient) return;
            SetCaptionTipText(_captionTipText(), TTM_UPDATETIPTEXTW);   // Fetch the text in the current language at show time
            GetCursorPos(out POINT pt);
            int below = pt.Y + CursorHeightBelowHotspot();
            SendMessage(_captionTip, TTM_TRACKPOSITION, IntPtr.Zero, new IntPtr((pt.X & 0xFFFF) | ((below & 0xFFFF) << 16)));
            // Make the tooltip transparent to the mouse: with the mouse over the tooltip, messages go to the button underneath and the button
            // stays highlighted, consistent with the system tooltip.
            // TTF_TRANSPARENT has no effect in tracking mode (measured: hit testing the center still returns HTCLIENT); the tooltip window is
            // already a layered window, so adding WS_EX_TRANSPARENT makes it mouse-transparent.
            // Reapplied before every show in case the tooltip control resets it.
            long exStyle = GetWindowLongPtr(_captionTip, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_TRANSPARENT) == 0)
                SetWindowLongPtr(_captionTip, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TRANSPARENT));
            SendToolInfo(_captionTip, TTM_TRACKACTIVATE, new IntPtr(1), ref _captionTipInfo);
            // After showing, clamp using the actual window rectangle (adjusted in the same message round, so it is in place before composition).
            // Measuring with TTM_GETBUBBLESIZE before showing still let it pop up off screen (measured).
            if (GetWindowRect(_captionTip, out RECT shown))
            {
                (int x, int y) = FitCaptionTip(pt, below, shown.Right - shown.Left, shown.Bottom - shown.Top);
                if (x != shown.Left || y != shown.Top)
                    SendMessage(_captionTip, TTM_TRACKPOSITION, IntPtr.Zero, new IntPtr((x & 0xFFFF) | ((y & 0xFFFF) << 16)));
            }
            _captionTipPopTimer!.Start();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>
        /// Tracking mode uses absolute positioning, and the tooltip control does not move itself onto the screen: with the button near a
        /// screen edge the tooltip would pop up out of sight.
        /// Clamp to the work area of the monitor under the cursor: shift left if it overflows on the right; place it above the cursor if
        /// it does not fit below (consistent with the system tooltip).
        /// </summary>
        private static (int X, int Y) FitCaptionTip(POINT cursor, int below, int width, int height)
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (width <= 0 || height <= 0 || !GetMonitorInfo(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST), ref info))
                return (cursor.X, below);
            RECT work = info.rcWork;
            int x = Math.Max(work.Left, Math.Min(cursor.X, work.Right - width));
            int y = below + height <= work.Bottom ? below : Math.Max(work.Top, cursor.Y - height);
            return (x, y);
        }

        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        private static bool IsTopmost(IntPtr hWnd) => (GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        private bool CursorOnCaptionTip()
        {
            if (_captionTip == IntPtr.Zero || !IsWindowVisible(_captionTip) || !GetWindowRect(_captionTip, out RECT r) || !GetCursorPos(out POINT pt))
                return false;
            return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        }

        private void HideCaptionTip()
        {
            _captionTipText = null;
            _captionTipShowTimer?.Stop();
            _captionTipPopTimer?.Stop();
            if (_captionTip != IntPtr.Zero)
                SendToolInfo(_captionTip, TTM_TRACKACTIVATE, IntPtr.Zero, ref _captionTipInfo);
        }

        [DllImport("gdi32.dll")] private static extern int GetBitmapBits(IntPtr bitmap, int size, byte[] bits);

        /// <summary>
        /// Distance (physical pixels) from the hotspot down to the lowest visible row of the cursor image, so the tooltip sits right under
        /// the cursor's bottom edge, consistent with the system tooltip.
        /// The cursor bitmap is usually much larger than the arrow (the lower part is transparent), so the bitmap height cannot be used:
        /// that would place the tooltip far from the cursor.
        /// Monochrome cursor: the upper half of the bitmap is the AND mask, the lower half the XOR mask; a pixel is visible where AND=0 or
        /// XOR=1. Color cursor: use the alpha of the color bitmap (falling back to the AND mask when alpha is all 0).
        /// </summary>
        private int CursorHeightBelowHotspot()
        {
            var info = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(ref info) && info.hCursor != IntPtr.Zero && GetIconInfo(info.hCursor, out ICONINFO icon))
            {
                try
                {
                    int bottom = LowestVisibleRow(icon);
                    if (bottom >= 0)
                        return Math.Max(1, bottom + 1 - icon.yHotspot);
                }
                finally
                {
                    if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
                    if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
                }
            }
            uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            return GetSystemMetricsForDpi(SM_CYCURSOR, dpi == 0 ? 96 : dpi);
        }

        /// <summary>Row index of the lowest visible pixel row of the cursor image, or -1 if none is found.</summary>
        private static int LowestVisibleRow(ICONINFO icon)
        {
            if (GetObject(icon.hbmMask, Marshal.SizeOf<BITMAP>(), out BITMAP mask) == 0)
                return -1;
            byte[] maskBits = new byte[mask.bmWidthBytes * mask.bmHeight];
            if (GetBitmapBits(icon.hbmMask, maskBits.Length, maskBits) == 0)
                return -1;
            bool MaskBit(int x, int y) => (maskBits[y * mask.bmWidthBytes + x / 8] & (0x80 >> (x % 8))) != 0;

            if (icon.hbmColor == IntPtr.Zero)
            {
                int h = mask.bmHeight / 2;   // Upper half AND, lower half XOR
                for (int y = h - 1; y >= 0; y--)
                    for (int x = 0; x < mask.bmWidth; x++)
                        if (!MaskBit(x, y) || MaskBit(x, y + h)) return y;
                return -1;
            }

            if (GetObject(icon.hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP color) != 0 && color.bmBitsPixel == 32)
            {
                byte[] colorBits = new byte[color.bmWidthBytes * color.bmHeight];
                if (GetBitmapBits(icon.hbmColor, colorBits.Length, colorBits) != 0)
                {
                    bool anyAlpha = false;
                    for (int i = 3; i < colorBits.Length; i += 4)
                        if (colorBits[i] != 0) { anyAlpha = true; break; }
                    if (anyAlpha)
                    {
                        for (int y = color.bmHeight - 1; y >= 0; y--)
                            for (int x = 0; x < color.bmWidth; x++)
                                if (colorBits[y * color.bmWidthBytes + x * 4 + 3] != 0) return y;
                        return -1;
                    }
                }
            }
            for (int y = mask.bmHeight - 1; y >= 0; y--)   // Color without alpha: AND mask 0 means visible
                for (int x = 0; x < mask.bmWidth; x++)
                    if (!MaskBit(x, y)) return y;
            return -1;
        }

        /// <summary>Decides from the current presenter which buttons to show and the maximize button's glyph, and makes sure there is no system title bar.</summary>
        private void RefreshCaptionButtons()
        {
            if (_captionPanel is null) return;
            double width = 0;
            if (AppWindow.Presenter is OverlappedPresenter p && !_captionButtonsHidden)
            {
                if (p.HasTitleBar) p.SetBorderAndTitleBar(true, false);
                _minimizeButton!.Visibility = p.IsMinimizable ? Visibility.Visible : Visibility.Collapsed;
                _maximizeButton!.Visibility = p.IsMaximizable ? Visibility.Visible : Visibility.Collapsed;
                bool maximized = p.State == OverlappedPresenterState.Maximized;
                _maximizeIcon!.Glyph = maximized ? "\uE923" : "\uE922";
                Func<string> maximizeName = maximized ? () => UiText.Get("Caption_Restore") : () => UiText.Get("Caption_Maximize");
                UiHost.Track(_maximizeButton, Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty, maximizeName);
                _captionPanel.Visibility = Visibility.Visible;
                width = CaptionButtonWidth * (1 + (p.IsMinimizable ? 1 : 0) + (p.IsMaximizable ? 1 : 0));
            }
            else
            {
                _captionPanel.Visibility = Visibility.Collapsed;   // Full screen etc.: no caption buttons
            }
            if (width != CaptionButtonsWidth)
            {
                CaptionButtonsWidth = width;
                CaptionButtonsChanged?.Invoke();
            }
            UpdateMaximizeRegion();
        }

        private RectInt32 _maximizeRegion;

        /// <summary>The button strip's rectangle in client-area physical pixels (empty when there are no buttons). Derived windows must cut it out of their drag regions.</summary>
        protected RectInt32 CaptionButtonsBounds => ElementBounds(_captionPanel);

        /// <summary>
        /// The element's layout position (DIP) relative to <paramref name="ancestor"/> (null for the UI root): sums ActualOffset up the visual tree.
        /// Title bar drag regions and button regions are computed from this, not from TransformToVisual: the latter includes the composition
        /// Translation and only syncs on the next rendered frame.
        /// For example, a window that slides its top bar off screen in full screen and sets the translation back to 0 on exit still gets -74
        /// from TransformToVisual, even after layout completes; regions registered from that are shifted up, leaving part of a button
        /// unclickable after leaving full screen (confirmed in logs).
        /// In windowed mode the title bar has no translation, so its layout position is where it really is.
        /// </summary>
        protected static Point LayoutOrigin(UIElement element, UIElement? ancestor)
        {
            double x = 0, y = 0;
            for (DependencyObject? current = element; current is UIElement e && !ReferenceEquals(e, ancestor);
                 current = VisualTreeHelper.GetParent(e))
            {
                x += e.ActualOffset.X;
                y += e.ActualOffset.Y;
            }
            return new Point(x, y);
        }

        private static RectInt32 ElementBounds(FrameworkElement? element)
        {
            if (element?.XamlRoot is not { } root || element.Visibility != Visibility.Visible || element.ActualWidth <= 0)
                return default;
            double scale = root.RasterizationScale;
            Point origin = LayoutOrigin(element, null);
            int left = (int)Math.Round(origin.X * scale), top = (int)Math.Round(origin.Y * scale);
            return new RectInt32(left, top,
                (int)Math.Round((origin.X + element.ActualWidth) * scale) - left,
                (int)Math.Round((origin.Y + element.ActualHeight) * scale) - top);
        }

        /// <summary>Registers the maximize button's rectangle as the system Maximize region (only when it changes).</summary>
        private void UpdateMaximizeRegion()
        {
            if (_nonClientSource is null || _closed || _inSizeMove) return;
            RectInt32 rect = _captionPanel?.Visibility == Visibility.Visible ? ElementBounds(_maximizeButton) : default;
            if (rect.Equals(_maximizeRegion)) return;
            _maximizeRegion = rect;
            SetMaximizeRegion(rect);
            CaptionButtonsChanged?.Invoke();   // Position changed; derived windows recompute their drag regions
        }

        private bool _settingRegions;   // Currently registering the maximize region
        private bool _maximizeCheckQueued;   // A "check the maximize region" pass is already queued

        // [No title bar region registration while resizing by the border / moving by the title bar; register once on release]
        // Each SetRegionRects takes about 1 ms, and while dragging the border the SDK clears the maximize region on every step while the
        // buttons and drag regions also change every step, so each step needed two or three registrations (measured: 7.5 ms of window
        // message handling per step, about 2 ms of it region registration).
        // During the drag the mouse is holding the border or title bar and can reach neither the maximize button nor the drag regions,
        // so registering late, on release, does no harm.
        private bool _inSizeMove;
        private RectInt32[]? _pendingCaptionRects;   // Last drag regions requested during the drag

        private void OnExitSizeMove()
        {
            _inSizeMove = false;
            if (_closed) return;
            if (_pendingCaptionRects is { } rects)
            {
                _pendingCaptionRects = null;
                SetCaptionDragRegions(rects);
            }
            UpdateMaximizeRegion();
            if (_maximizeRegion.Width > 0 && _nonClientSource != null && !MaximizeRegionIntact())
                SetMaximizeRegion(_maximizeRegion);
        }

        private void SetMaximizeRegion(RectInt32 rect)
        {
            _settingRegions = true;
            try { _nonClientSource!.SetRegionRects(NonClientRegionKind.Maximize, rect.Width > 0 ? [rect] : []); }
            finally { _settingRegions = false; }
        }

        /// <summary>Whether the maximize region registered with the system is still <see cref="_maximizeRegion"/> (false when the SDK cleared it).</summary>
        private bool MaximizeRegionIntact()
        {
            RectInt32[] current = CurrentRegionRects(NonClientRegionKind.Maximize);
            return current.Length == 1 && current[0].Equals(_maximizeRegion);
        }

        /// <summary>The regions of one kind currently registered with the system. When empty, GetRegionRects throws a null reference in the projection (instead of returning an empty array); this returns an empty array.</summary>
        private RectInt32[] CurrentRegionRects(NonClientRegionKind kind)
        {
            try { return _nonClientSource!.GetRegionRects(kind) ?? []; }
            catch (NullReferenceException) { return []; }
        }

        private static bool SameRects(RectInt32[] a, RectInt32[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!a[i].Equals(b[i])) return false;
            return true;
        }

        /// <summary>
        /// Sets the title bar drag regions (client-area physical pixels). InputNonClientPointerSource.SetRegionRects(Caption) changes only
        /// the drag region kind; AppWindow.TitleBar.SetDragRectangles is not used because it also clears the registered maximize region
        /// (measured: cleared 5 ms after registration).
        /// </summary>
        protected void SetCaptionDragRegions(RectInt32[] rects)
        {
            _nonClientSource ??= InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            if (_inSizeMove) { _pendingCaptionRects = rects; return; }   // Registered on release (see OnExitSizeMove)
            // Skip if identical to what the system currently has: within one layout pass the title bar, the button size and the caption
            // button position paths each compute the drag regions.
            // The comparison is against the system's current regions, not what we registered last, so it still re-registers if the SDK changed them.
            if (SameRects(rects, CurrentRegionRects(NonClientRegionKind.Caption))) return;
            // The RegionsChanged raised synchronously here is not filtered: should the SDK touch the maximize region while changing drag
            // regions, that handler checks and re-registers it.
            _nonClientSource.SetRegionRects(NonClientRegionKind.Caption, rects);
        }

        /// <summary>
        /// Pointer input in the maximize region belongs to the system and never reaches XAML, so the button's look is switched from non-client pointer events.
        /// When the pointer slides from the minimize/close button into the resize area at the window edge (also owned by the system), XAML
        /// never receives "pointer exited", so the button stays in hover and the tooltip stays up (the cursor is already a resize arrow
        /// while the close button is still red and its tooltip still shown); entering any non-client region resets them to normal and hides the tooltip.
        /// </summary>
        private void OnNonClientPointer(NonClientPointerEventArgs e, string state)
        {
            if (_maximizeButton is null) return;
            bool onMaximize = e.RegionKind == NonClientRegionKind.Maximize;
            VisualStateManager.GoToState(_maximizeButton, onMaximize ? state : "Normal", true);
            if (state == "Normal") { _pointerInNonClient = false; _pointerNonClientRegion = null; return; }   // Left the non-client area
            _pointerNonClientRegion = e.RegionKind;
            _pointerInNonClient = true;
            VisualStateManager.GoToState(_minimizeButton!, "Normal", true);
            VisualStateManager.GoToState(_closeButton!, "Normal", true);
            HideCaptionTip();
        }

        private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            Activated -= OnFirstActivated;
            // Defer one more tick past rendering so the content is really on screen.
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    // When the window is closed right after opening (e.g. Esc right after an accidental click), this tick lands after
                    // Closed: attaching Mica to a closed window causes an access violation in Microsoft.UI.Xaml.dll on app exit
                    // (0xC0000005, reproduces under both JIT and AOT; found by an AOT self-test).
                    if (_closed) return;
                    if (SystemBackdrop is null)
                        SystemBackdrop = new MicaBackdrop();
                });
        }

        /// <summary>
        /// Called by derived windows once the theme is known: refreshes the erase color for the first/last frame and the system title bar colors.
        /// The argument must be the [resolved] Light/Dark, never Default.
        /// </summary>
        protected void ApplyWindowTheme(ElementTheme theme)
        {
            UpdateEraseBrush(theme);
            AppTitleBarTheme.Apply(AppWindow, theme);
            if (TitleBarDeactivated) ApplyTitleBarActivation();   // The inactive brush depends on the theme, so re-fetch it on theme change
        }

        private void InstallEraseBackgroundHook()
        {
            try
            {
                // The brush must be ready [before] installing the hook: measured, several WM_ERASEBKGND messages arrive during window
                // construction, a dozen-plus milliseconds before the derived class calls ApplyWindowTheme, and those frames are exactly
                // the visible moment the window opens.
                if (_eraseBrush == IntPtr.Zero)
                    UpdateEraseBrush(ElementTheme.Dark);

                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                Instances[hwnd] = this;
                _sharedWndProc ??= SharedWndProc;
                IntPtr original = SetWindowLongPtr(
                    hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_sharedWndProc));
                if (original != IntPtr.Zero)
                    OriginalProcs[hwnd] = original;
            }
            catch (Exception ex)
            {
                // If the hook cannot be installed, fall back to the original behavior (first frame still black); the window remains usable.
                UiHost.WriteLog($"[Window] Failed to install the erase-background hook: {ex.Message}");
            }
        }

        private static void UpdateEraseBrush(ElementTheme theme)
        {
            bool light = theme == ElementTheme.Light;
            if (_eraseBrush != IntPtr.Zero && _eraseBrushIsLight == light)
                return;

            // COLORREF is 0x00BBGGRR; the values match the window background of each theme so the transition has no color shift.
            IntPtr brush = CreateSolidBrush(light ? 0x00F3F3F3u : 0x00202020u);
            if (brush == IntPtr.Zero)
                return;

            IntPtr old = _eraseBrush;
            _eraseBrush = brush;
            _eraseBrushIsLight = light;
            if (old != IntPtr.Zero)
                DeleteObject(old);   // GDI objects manage their own lifetime: delete the old one after swapping, or every theme switch leaks one
        }

        /// <summary>
        /// Called on right button up over the title bar (non-client area). Return true when handled, to suppress the system window menu.
        /// Coordinates are in screen pixels. Override in derived classes to show a custom menu.
        /// </summary>
        protected virtual bool OnCaptionRightButtonUp(int screenX, int screenY) => false;

        /// <summary>
        /// Whether the screen point falls in the title bar area where the derived class takes over right clicks. Used on button down to
        /// swallow the press as well, so the system does not start processing the window menu already at that stage. Not taken over by default.
        /// </summary>
        protected virtual bool IsCaptionMenuHit(int screenX, int screenY) => false;

        // [Title bar right-click messages do not necessarily reach the top-level window] With content extended into the title bar
        // (ExtendsContentIntoTitleBar + SetDragRectangles), WinUI covers the title bar strip with a private child window (class name
        // InputNonClientPointerSource, measured 0-48 px tall, full width), and all non-client mouse messages for the drag regions go to it;
        // the top-level window never gets WM_NCRBUTTONUP, so the custom menu does not appear while the system menu still does.
        // So that child window is hooked too; the top-level window additionally intercepts WM_CONTEXTMENU / 0x0313 as a fallback, so the
        // menu is caught whichever path the system takes. The child window may be recreated; derived classes just call
        // EnsureCaptionInputHook when the title bar layout changes (idempotent).
        private const string CaptionInputClassName = "InputNonClientPointerSource";
        private static readonly Dictionary<IntPtr, IntPtr> CaptionSinkProcs = new();
        private static readonly Dictionary<IntPtr, ThemedWindow> CaptionSinkOwners = new();
        private static WndProcDelegate? _captionSinkWndProc;
        private IntPtr _captionSinkHwnd;
        // A single right click may deliver NCRBUTTONUP, WM_CONTEXTMENU and 0x0313 in turn: once the menu has been shown, just swallow the rest
        // instead of showing it again, so the menu does not close and reopen.
        private long _captionMenuShownTick;

        /// <summary>Hooks right clicks on the input child window WinUI places over the title bar. Idempotent; does nothing if the child window is not found.</summary>
        protected void EnsureCaptionInputHook()
        {
            try
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                IntPtr sink = FindWindowEx(hwnd, IntPtr.Zero, CaptionInputClassName, null);
                if (sink == IntPtr.Zero || sink == _captionSinkHwnd || CaptionSinkProcs.ContainsKey(sink))
                    return;

                _captionSinkWndProc ??= CaptionSinkWndProc;
                IntPtr original = SetWindowLongPtr(
                    sink, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_captionSinkWndProc));
                if (original == IntPtr.Zero)
                {
                    UiHost.WriteLog($"[Window] Failed to hook the title bar input child window (err={Marshal.GetLastWin32Error()})");
                    return;
                }
                CaptionSinkProcs[sink] = original;
                CaptionSinkOwners[sink] = this;
                _captionSinkHwnd = sink;
                UiHost.WriteTrace($"[Window] Took over right clicks on title bar input child window {sink}");
            }
            catch (Exception ex)
            {
                UiHost.WriteLog($"[Window] Exception while hooking the title bar input child window: {ex.Message}");
            }
        }

        private static (int X, int Y) UnpackScreenPoint(IntPtr lParam)
        {
            long packed = lParam.ToInt64();
            return ((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        }

        /// <summary>Button down: swallowed if inside the taken-over area. Button up / menu request: the derived class shows its custom menu; if already shown, just swallowed. Returns true = handled.</summary>
        private bool HandleCaptionRightClick(uint msg, IntPtr lParam, string source)
        {
            (int x, int y) = UnpackScreenPoint(lParam);
            if (msg == WM_NCRBUTTONDOWN)
            {
                _captionMenuShownTick = 0;
                return IsCaptionMenuHit(x, y);
            }

            if (_captionMenuShownTick != 0 && Environment.TickCount64 - _captionMenuShownTick < 500)
                return true;
            if (!OnCaptionRightButtonUp(x, y))
                return false;
            _captionMenuShownTick = Environment.TickCount64;
            UiHost.WriteTrace($"[Window] Title bar right click shows the custom menu instead ({source} msg=0x{msg:X})");
            return true;
        }

        private static IntPtr CaptionSinkWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if ((msg == WM_NCRBUTTONDOWN || msg == WM_NCRBUTTONUP ||
                 (msg == WM_CONTEXTMENU && lParam.ToInt64() != -1)) &&
                CaptionSinkOwners.TryGetValue(hWnd, out ThemedWindow? win) &&
                win.HandleCaptionRightClick(msg, lParam, "child window"))
            {
                return IntPtr.Zero;
            }

            IntPtr original = CaptionSinkProcs.TryGetValue(hWnd, out IntPtr proc) ? proc : IntPtr.Zero;
            IntPtr result = original != IntPtr.Zero
                ? CallWindowProc(original, hWnd, msg, wParam, lParam)
                : IntPtr.Zero;

            if (msg == WM_NCDESTROY)
            {
                CaptionSinkProcs.Remove(hWnd);
                if (CaptionSinkOwners.Remove(hWnd, out ThemedWindow? owner) && owner._captionSinkHwnd == hWnd)
                    owner._captionSinkHwnd = IntPtr.Zero;
            }
            return result;
        }

        private const uint WM_NCACTIVATE = 0x0086;
        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_WINDOWPOSCHANGED = 0x0047;
        private const long WA_INACTIVE = 0;
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(IntPtr hWnd);
        private const uint WM_ENTERSIZEMOVE = 0x0231;
        private const uint WM_EXITSIZEMOVE = 0x0232;

        private static IntPtr SharedWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // Second signal for graying out the title bar (see SetTitleBarDeactivated): WM_NCACTIVATE is the message by which the system
            // decides whether the title bar is drawn active or inactive.
            if (msg == WM_NCACTIVATE && Instances.TryGetValue(hWnd, out ThemedWindow? ncWin))
            {
                bool deactivated = wParam == IntPtr.Zero;
                ncWin.DispatcherQueue.TryEnqueue(() => ncWin.SetTitleBarDeactivated(deactivated));
            }
            if (msg == WM_ERASEBKGND && _eraseBrush != IntPtr.Zero)
            {
                GetClientRect(hWnd, out RECT rect);
                FillRect(wParam, ref rect, _eraseBrush);   // wParam is the HDC
                return 1;
            }

            // Title bar right click: by default the system shows the Move/Size/Close window menu on a right click in the drag region.
            // Intercept right clicks in the drag region (HTCAPTION) and the WM_CONTEXTMENU / 0x0313 that may follow (the system's request
            // to show the window menu), and let the derived class decide whether to show a custom menu instead; when it returns true the
            // message is not passed on and the system menu is suppressed.
            // Left-button dragging, double-click to maximize, etc. keep the system default, since only these right-click messages are intercepted.
            if (((msg == WM_NCRBUTTONDOWN || msg == WM_NCRBUTTONUP) && wParam.ToInt64() == HTCAPTION ||
                 msg == WM_POPUPSYSTEMMENU ||
                 msg == WM_CONTEXTMENU && lParam.ToInt64() != -1) &&
                Instances.TryGetValue(hWnd, out ThemedWindow? win) &&
                win.HandleCaptionRightClick(msg, lParam, "top-level window"))
            {
                return IntPtr.Zero;
            }

            if (msg == WM_ENTERSIZEMOVE && Instances.TryGetValue(hWnd, out ThemedWindow? enterWin))
                enterWin._inSizeMove = true;

            // The activation redirect while disabled by a modal window is not handled here: the subclass installed on the owner when a
            // modal window opens handles it (OwnerSubclassProc), so the owner need not be a ThemedWindow.

            IntPtr original = OriginalProcs.TryGetValue(hWnd, out IntPtr proc) ? proc : IntPtr.Zero;
            IntPtr result = original != IntPtr.Zero
                ? CallWindowProc(original, hWnd, msg, wParam, lParam)
                : IntPtr.Zero;

            if (msg == WM_EXITSIZEMOVE && Instances.TryGetValue(hWnd, out ThemedWindow? exitWin))
                exitWin.OnExitSizeMove();

            // The subclassing goes away naturally with the HWND, so there is no need (and it would be wrong) to restore it manually when
            // the window closes: the close animation plays after Closed, and restoring early would bring back the default erase for those frames.
            // Only the table entries are removed here, so repeatedly opening and closing windows does not grow the tables.
            if (msg == WM_NCDESTROY)
            {
                OriginalProcs.Remove(hWnd);
                Instances.Remove(hWnd);
            }

            // [Cursor fallback] The WinUI content child window has a null class cursor: when no control under the pointer specifies a cursor,
            // the child window passes WM_SETCURSOR to its parent, and if the original handler does not take it either (returns 0), the system
            // keeps the previous shape: the "busy" cursor the system shows at startup stays until some control sets a cursor explicitly
            // (so it is only visible while no such control is under the pointer; reading the cursor externally confirmed IDC_WAIT).
            // The arrow is restored only when "forwarded by a child window, client area, original handler did not take it"; when a control
            // specifies a cursor, the child window handles it itself and never gets here.
            // [Tried, does not work, do not retry] Declaring a default cursor on the content root through XAML APIs (root container replaced
            // by a Grid subclass with ProtectedCursor = arrow): still a busy cursor at startup (measured). WinUI does not take over the cursor
            // until it receives its first pointer input, so nothing declared at the XAML level helps.
            if (msg == WM_SETCURSOR && wParam != hWnd && (lParam.ToInt64() & 0xFFFF) == HTCLIENT && result == IntPtr.Zero)
            {
                SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
                if (!_loggedCursorFallback)
                {
                    _loggedCursorFallback = true;
                    UiHost.WriteTrace("[Window] Content child window specified no cursor → restored the arrow (further occurrences are not logged)");
                }
                return 1;
            }

            return result;
        }

        private const uint WM_SETCURSOR = 0x0020;
        private const int HTCLIENT = 1;
        private const int IDC_ARROW = 32512;
        private static bool _loggedCursorFallback;

        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr cursor);

        [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
        private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

        private static IntPtr LoadCursor(IntPtr instance, int id) => LoadCursor(instance, new IntPtr(id));
    }
}
