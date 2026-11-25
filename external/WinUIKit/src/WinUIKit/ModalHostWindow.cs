using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRT.Interop;

namespace WinUIKit
{
    /// <summary>Content shown in a pooled <see cref="ModalHostWindow"/> (<see cref="DialogWindow"/>, <see cref="ModalNavWindow"/>).</summary>
    internal interface IModalGuest
    {
        /// <summary>The system asked the window to close (Alt+F4, the window menu). The host cancels the system close; the guest decides.</summary>
        void OnSystemCloseRequested();

        /// <summary>The title bar close button was clicked.</summary>
        void OnCaptionCloseClicked();

        /// <summary>The window became active or inactive (the title should be grayed out or restored).</summary>
        void OnTitleBarActivationChanged();

        /// <summary>A modal window attached to the host has closed.</summary>
        void OnModalChildClosed();

        /// <summary>The host window is being destroyed while showing this guest (its owner closed, the app is exiting).</summary>
        void OnHostClosed();

        /// <summary>The window this guest is shown over is ending its own session: close now, as an owned window closes with its owner.</summary>
        void OnOwnerClosing();
    }

    /// <summary>
    /// A reusable native window for modal content. Every window whose content is extended into the title bar leaves about 30 GDI
    /// objects behind when it is destroyed (Windows App SDK; the same with Window.ExtendsContentIntoTitleBar,
    /// AppWindow.TitleBar.ExtendsContentIntoTitleBar or OverlappedPresenter.SetBorderAndTitleBar, measured from 1.8 to 2.5.4).
    /// After a few hundred dialogs the process reaches the per-process GDI limit and no window can be created any more.
    /// Hiding and showing the same window does not leak, so dialogs and navigation windows borrow one of these instead of creating
    /// a window each time: <see cref="ModalHostPool"/> hands out an idle one, the guest mounts its content, attaches to its owner,
    /// and when it is done the window ends modality, hides and goes back to the pool.
    /// An idle window stays owned by the window it was last shown over, and is closed for good when that window closes, so it never
    /// keeps the process alive.
    /// </summary>
    internal sealed partial class ModalHostWindow : ThemedWindow
    {
        private readonly Grid _root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        private UIElement? _guestRoot;
        private Window? _mountOwner;

        internal ModalHostWindow()
        {
            ExtendsContentIntoTitleBar = true;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }
            base.Content = _root;
            InstallCaptionButtons(_root, StandardTitleBarHeight);
            // A system close (Alt+F4, the window menu) while showing a guest: cancel it and let the guest decide. Registered before the
            // base class's modal Closing handler (installed on the first attach), which then sees Cancel and keeps modality.
            // An idle window may close (its owner closed, the app is exiting).
            AppWindow.Closing += (_, e) =>
            {
                if (Guest is not { } guest) return;
                e.Cancel = true;
                guest.OnSystemCloseRequested();
            };
            Closed += (_, _) =>
            {
                Destroyed = true;
                ModalHostPool.Forget(this);
                IModalGuest? guest = Guest;
                Guest = null;
                guest?.OnHostClosed();
            };
        }

        /// <summary>The guest currently shown (null while idle in the pool).</summary>
        internal IModalGuest? Guest { get; private set; }

        /// <summary>The window has been destroyed (it can no longer be shown).</summary>
        internal bool Destroyed { get; private set; }

        internal IntPtr Hwnd => WindowNative.GetWindowHandle(this);

        /// <summary>
        /// Starts showing <paramref name="guest"/>: puts its root element in the window, sets the caption button height, the switcher
        /// visibility and the presenter's resizability and minimum size (carried over by <see cref="Attach"/> to the modal presenter).
        /// </summary>
        /// <para>
        /// <paramref name="owner"/> is the window the guest is about to be shown over. It is remembered so that, if something fails before
        /// <see cref="Attach"/>, the window can still go back to the pool tied to that owner (closed together with it) instead of being
        /// destroyed, which would leave its GDI objects behind.
        /// </para>
        internal void Mount(IModalGuest guest, Window owner, UIElement root, double captionButtonHeight, bool shownInSwitchers, bool resizable)
        {
            if (Guest is not null)
                throw new InvalidOperationException("The window is already showing other content");
            Guest = guest;
            _mountOwner = owner;
            _guestRoot = root;
            _root.Children.Insert(0, root);
            SetCaptionButtonHeight(captionButtonHeight);
            CaptionButtonsHidden = false;
            AppWindow.IsShownInSwitchers = shownInSwitchers;
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = resizable;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
                presenter.PreferredMinimumWidth = null;
                presenter.PreferredMinimumHeight = null;
            }
        }

        /// <summary>Makes the window modal over <paramref name="owner"/> (see <see cref="ThemedWindow.AttachModalOwner"/>).</summary>
        internal void Attach(Window owner)
        {
            ReleaseStaleActivation(owner);
            AttachModalOwner(owner);
        }

        /// <summary>
        /// Ends the guest's session: closes the modal windows shown over it (as closing a window would), ends modality and hands
        /// activation back to the owner, hides, removes the guest's content and goes back to the pool.
        /// </summary>
        internal void EndSession()
        {
            if (Destroyed || Guest is null) return;
            CloseModalChildren();
            // Normally the owner of the modal session; if the session never got attached, the owner the guest was about to be shown over
            Window? owner = ModalOwnerWindow ?? _mountOwner;
            _mountOwner = null;
            DetachModalOwner();
            SetTitleBar(null);
            if (_guestRoot is not null)
                _root.Children.Remove(_guestRoot);
            _guestRoot = null;
            Guest = null;
            ModalHostPool.Return(this, owner);
        }

        /// <summary>Hides the window from view without hiding it from the system (DWM cloak), e.g. until the new content is laid out.</summary>
        internal void SetCloaked(bool cloaked)
        {
            int value = cloaked ? 1 : 0;
            DwmSetWindowAttribute(Hwnd, DWMWA_CLOAK, ref value, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        private const int DWMWA_CLOAK = 13;

        internal bool HasModalChildWindow => HasModalChild;

        internal double CaptionWidth => CaptionButtonsWidth;

        internal bool CaptionHidden
        {
            get => CaptionButtonsHidden;
            set => CaptionButtonsHidden = value;
        }

        internal event Action? CaptionChanged
        {
            add => CaptionButtonsChanged += value;
            remove => CaptionButtonsChanged -= value;
        }

        internal bool IsTitleBarDeactivated => TitleBarDeactivated;

        internal static Brush? DeactivatedForeground(Brush? activeForeground) => TitleBarDeactivatedForeground(activeForeground);

        internal const double StandardCaptionHeight = StandardTitleBarHeight, TallCaptionHeight = TallTitleBarHeight;

        /// <summary>Applies the resolved Light/Dark theme to the caption buttons and the window frame (first/last frame color, title bar colors).</summary>
        internal void ApplyTheme(ElementTheme theme)
        {
            _root.RequestedTheme = theme;
            ApplyWindowTheme(theme);
        }

        protected override void OnCaptionCloseClicked()
        {
            if (Guest is { } guest) guest.OnCaptionCloseClicked();
            else base.OnCaptionCloseClicked();
        }

        protected override void OnTitleBarActivationChanged() => Guest?.OnTitleBarActivationChanged();

        protected override void OnModalChildClosed() => Guest?.OnModalChildClosed();
    }

    /// <summary>Resolves what callers pass as an owner to the native window it stands for.</summary>
    internal static class ModalWindows
    {
        /// <summary>A <see cref="Window"/> as is; a shown <see cref="DialogWindow"/> or <see cref="ModalNavWindow"/> → the window it borrowed; anything else → null.</summary>
        internal static Window? Resolve(object? owner) => owner switch
        {
            Window window => window,
            DialogWindow dialog => dialog.Host,
            ModalNavWindow nav => nav.Host,
            _ => null,
        };
    }

    /// <summary>Idle <see cref="ModalHostWindow"/>s, reused by the next dialog or navigation window.</summary>
    internal static class ModalHostPool
    {
        private static readonly List<ModalHostWindow> s_idle = new();
        // While idle, a window is closed for good when the window it was last shown over closes (it is still owned by it)
        private static readonly Dictionary<ModalHostWindow, (Window Owner, TypedEventHandler<object, WindowEventArgs> Handler)> s_ownerHooks = new();

        /// <summary>Idle windows (for self-tests).</summary>
        internal static int IdleCount => s_idle.Count;

        /// <summary>Windows created so far (for self-tests: stays flat while windows are reused).</summary>
        internal static int CreatedCount { get; private set; }

        internal static ModalHostWindow Rent()
        {
            while (s_idle.Count > 0)
            {
                ModalHostWindow host = s_idle[^1];
                s_idle.RemoveAt(s_idle.Count - 1);
                Unhook(host);
                if (!host.Destroyed)
                    return host;
            }
            CreatedCount++;
            return new ModalHostWindow();
        }

        internal static void Return(ModalHostWindow host, Window? lastOwner)
        {
            if (host.Destroyed) return;
            if (lastOwner is null)
            {
                // No owner at all (cannot happen through Mount, kept as a safety net): an unowned hidden window would keep the process
                // alive, so close it even though that leaves its GDI objects behind
                host.Close();
                return;
            }
            // A window whose session never got attached was never shown and is not owned by lastOwner in Win32 terms; it is still closed
            // together with it through the hook below, so it cannot outlive the app
            s_idle.Add(host);
            TypedEventHandler<object, WindowEventArgs> handler = (_, _) =>
            {
                if (s_idle.Contains(host)) host.Close();
            };
            lastOwner.Closed += handler;
            s_ownerHooks[host] = (lastOwner, handler);
        }

        /// <summary>The window is being destroyed: drop it from the pool.</summary>
        internal static void Forget(ModalHostWindow host)
        {
            s_idle.Remove(host);
            Unhook(host);
        }

        private static void Unhook(ModalHostWindow host)
        {
            if (s_ownerHooks.Remove(host, out var hook))
                hook.Owner.Closed -= hook.Handler;
        }
    }
}

