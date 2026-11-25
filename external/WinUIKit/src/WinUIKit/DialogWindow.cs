using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace WinUIKit
{
    /// <summary>Event handler for dialog window events (same shape as ContentDialog events: sender + args).</summary>
    public delegate void DialogWindowEventHandler<in TArgs>(DialogWindow sender, TArgs args);

    /// <summary>Primary / secondary / close button clicked (counterpart of ContentDialogButtonClickEventArgs). <see cref="Cancel"/> = keep the window open.</summary>
    public sealed class DialogWindowButtonClickEventArgs : EventArgs
    {
        public bool Cancel { get; set; }
    }

    /// <summary>About to close (counterpart of ContentDialogClosingEventArgs). <see cref="Cancel"/> = keep the window open.</summary>
    public sealed class DialogWindowClosingEventArgs : EventArgs
    {
        internal DialogWindowClosingEventArgs(ContentDialogResult result) => Result = result;

        public ContentDialogResult Result { get; }

        public bool Cancel { get; set; }
    }

    /// <summary>
    /// Dialog window: a standalone modal owned window that replaces ContentDialog. ContentDialog lives on one window's XamlRoot and is
    /// queued by DialogManager, so across windows it can pop up on a different window and end up hidden; this class is a real window
    /// that always covers the window it is shown over.
    /// <para>
    /// [API mirrors ContentDialog] Title, Content (a string is laid out by <see cref="DialogText.Build"/>; a UIElement is used as is),
    /// Primary/Secondary/CloseButtonText (empty string = button not shown), DefaultButton, IsPrimary/IsSecondaryButtonEnabled, XamlRoot;
    /// events PrimaryButtonClick / SecondaryButtonClick / CloseButtonClick (Cancel keeps it open), Closing (with Result, cancelable), Opened;
    /// methods ShowAsync() (returns ContentDialogResult) and Hide(). Result and button enums are reused as is: ContentDialogResult / ContentDialogButton.
    /// </para>
    /// <para>
    /// [Differences from ContentDialog]
    /// (1) No queueing; a newly shown dialog is always on top: the actual owner is the topmost modal window above the caller's window
    ///    (<see cref="ThemedWindow.TopmostModalOver"/>), so the dialog covers everything and disables only one window. If a lower dialog is closed by code (Hide) first,
    ///    its ShowAsync returns immediately, and the window stays until the one above it closes (in Win32, closing an owner also destroys its owned windows).
    /// (2) Without a close button (empty CloseButtonText) the user cannot close it: no ✕ in the title bar, Esc and Alt+F4 do nothing
    ///    (progress and wait dialogs rely on this, so they no longer need to cancel Closing).
    /// (3) Hide() also works before ShowAsync: ShowAsync then returns None right away; no need to close it again in Opened.
    /// (4) Width is sized to content between <see cref="MinWidthDip"/> and <see cref="MaxWidthDip"/> (default 320–548, same as ContentDialog) and stays fixed once open;
    ///    height is sized to content (184–756, and never beyond the work area of the owner's monitor) and refits automatically when the content changes;
    ///    when it overflows, the body scrolls and the button bar stays put.
    /// </para>
    /// <para>
    /// [Keys] Esc, the title bar ✕ and Alt+F4 take the same path (<see cref="OnDismissRequested"/>; default = close with None without raising CloseButtonClick,
    /// same as Esc on ContentDialog); clicking the bottom close button raises CloseButtonClick first. Enter = default button (unless the focused control
    /// handled Enter itself). On open, focus goes to the default button without drawing a focus rectangle.
    /// </para>
    /// <para>
    /// [Two ways to use it] new one in code, set properties, then ShowAsync; or write a XAML-derived class (root element <c>utils:DialogWindow</c>, containing only the body),
    /// whose XAML body is shown in the shell on open. Derived classes can override <see cref="OnDismissRequested"/> and <see cref="OnLanguageChanged"/>;
    /// to take over a key, hook KeyDown on the body and set Handled.
    /// </para>
    /// <para>
    /// [Not a window itself] The dialog is content: on ShowAsync it borrows a native window from a pool (<see cref="ModalHostWindow"/>) and gives it
    /// back when done. Creating a native window per dialog leaks GDI objects in the Windows App SDK (see ModalHostWindow), so the window is reused.
    /// <see cref="WindowHandle"/> and <see cref="AppWindow"/> refer to the borrowed window while the dialog is shown.
    /// </para>
    /// </summary>
    public partial class DialogWindow : UserControl, IModalGuest
    {
        // Same as ContentDialog's theme resources (generic.xaml: ContentDialogMinWidth/MaxWidth/MinHeight/MaxHeight)
        public const double DefaultMinWidthDip = 320, DefaultMaxWidthDip = 548, DefaultMinHeightDip = 184, DefaultMaxHeightDip = 756;
        private const double DisplayEdgeMarginDip = 16;
        private const double ButtonSpacingDip = 8;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private static readonly List<DialogWindow> s_open = new();

        /// <summary>
        /// Some dialog is open (including a lower one that has already returned and only disappears once the one above closes).
        /// E.g. a single-instance app receiving a forwarded launch can use this to only bring itself to the foreground instead of opening a new file.
        /// </summary>
        public static bool IsAnyOpen => s_open.Count > 0;

        /// <summary>Open dialogs, in the order they were opened.</summary>
        public static IReadOnlyList<DialogWindow> OpenDialogs => s_open;

#if !SHIPPING
        /// <summary>For AOT self-tests: the shell (title, buttons, body area); null before showing.</summary>
        internal DialogWindowShell? ShellForSelfTest => _shell;

        /// <summary>For AOT self-tests: Opened has fired (focus is placed, so key presses have a target from now on).</summary>
        internal bool OpenedForSelfTest => _opened;

        /// <summary>For AOT self-tests: requested and actual size of the most recent fit.</summary>
        internal string LastFitForSelfTest { get; private set; } = "";

        /// <summary>For AOT self-tests: after the most recent fit, the actual client area matched the request.</summary>
        internal bool LastFitMatchedForSelfTest { get; private set; }

        /// <summary>For AOT self-tests: every fit that changed the window, in order ("initial"/"refit", client size and position in physical pixels).</summary>
        internal List<string> FitHistoryForSelfTest { get; } = new();

        /// <summary>For AOT self-tests: when set and returning non-null, ShowAsync does not show the window and returns this result directly ("auto-answer"). Returning null shows it normally.</summary>
        internal static Func<DialogWindow, ContentDialogResult?>? SelfTestAutoAnswer { get; set; }
#endif

        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
        private readonly TaskCompletionSource<ContentDialogResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DialogWindowShell? _shell;
        private ModalHostWindow? _host;
        private Window? _owner;
        private bool _showCalled, _finished, _closePending, _windowClosed, _refitQueued, _raisingClosing, _cloaked;
#if !SHIPPING
        private bool _opened;   // Self-test only (OpenedForSelfTest): excluded from shipping builds, otherwise it triggers an "assigned but never used" warning (warnings are errors)
#endif
        private ContentDialogResult _result = ContentDialogResult.None;
        private double _widthDip;

        private string _title = "";
        private object? _content;
        private string _primaryText = "", _secondaryText = "", _closeText = "";
        private ContentDialogButton _defaultButton = ContentDialogButton.None;
        private bool _primaryEnabled = true, _secondaryEnabled = true;
        private bool _scrollBody = true;

        public DialogWindow()
        {
            _dispatcher = DispatcherQueue;
        }

        /// <summary>The borrowed window while shown.</summary>
        internal ModalHostWindow? Host => _host;

        /// <summary>Handle of the window the dialog is shown in (IntPtr.Zero when not shown).</summary>
        public IntPtr WindowHandle => _host?.Hwnd ?? IntPtr.Zero;

        /// <summary>AppWindow of the window the dialog is shown in (null when not shown).</summary>
        public AppWindow? AppWindow => _host?.AppWindow;

        /// <summary>Root element of the whole window while shown (title bar, body and buttons; null when not shown), e.g. for UI automation.</summary>
        public UIElement? WindowContent => _host?.Content;

        /// <summary>Brings the dialog's window to the front and activates it (does nothing when not shown).</summary>
        public void Activate() => _host?.Activate();

        /// <summary>The dialog has closed (raised once, for every way of closing, just before the result is handed over).</summary>
        public event EventHandler? Closed;

        // ------------------------------------------------------------------
        // Properties matching ContentDialog
        // ------------------------------------------------------------------

        /// <summary>Title (shown in the title bar). Empty string = no title, only the ✕.</summary>
        public string Title
        {
            get => _title;
            set
            {
                _title = value ?? "";
                ApplyWindowTitle();
                ApplyTitle();
            }
        }

        /// <summary>Body: a string is laid out by <see cref="DialogText.Build"/> ("name : value" two columns, "# " subheadings, "! " red text); a UIElement is used as is.</summary>
        public new object? Content
        {
            get => _content;
            set
            {
                _content = value;
                ApplyBody();
            }
        }

        public string PrimaryButtonText { get => _primaryText; set { _primaryText = value ?? ""; ApplyButtons(); } }

        public string SecondaryButtonText { get => _secondaryText; set { _secondaryText = value ?? ""; ApplyButtons(); } }

        /// <summary>Close button text. Empty string = no close button, and also no ✕, with Esc and Alt+F4 doing nothing (the user cannot close it; only code can Hide it).</summary>
        public string CloseButtonText { get => _closeText; set { _closeText = value ?? ""; ApplyButtons(); } }

        public ContentDialogButton DefaultButton { get => _defaultButton; set { _defaultButton = value; ApplyButtons(); } }

        public bool IsPrimaryButtonEnabled { get => _primaryEnabled; set { _primaryEnabled = value; ApplyButtons(); } }

        public bool IsSecondaryButtonEnabled { get => _secondaryEnabled; set { _secondaryEnabled = value; ApplyButtons(); } }

        /// <summary>XamlRoot of the caller's window (window.Content.XamlRoot or a page's XamlRoot both work), used to find the owner. Set either this or <see cref="Owner"/>.</summary>
        public new XamlRoot? XamlRoot { get; set; }

        /// <summary>
        /// The caller's window: a <see cref="Window"/>, or a <see cref="DialogWindow"/> / <see cref="ModalNavWindow"/> that is currently shown.
        /// When set, <see cref="XamlRoot"/> is ignored.
        /// </summary>
        public object? Owner { get; set; }

        public double MinWidthDip { get; set; } = DefaultMinWidthDip;

        /// <summary>Maximum width. Informational dialogs with wide key/value lines can widen it to <see cref="DialogText.WideMaxWidth"/>.</summary>
        public double MaxWidthDip { get; set; } = DefaultMaxWidthDip;

        public double MinHeightDip { get; set; } = DefaultMinHeightDip;

        public double MaxHeightDip { get; set; } = DefaultMaxHeightDip;

        /// <summary>
        /// true (default) = the whole body goes into a scroll area and scrolls as a whole when too tall. Set false when the body has its own
        /// scroll area (e.g. long release notes): the body then gets the remaining window height and scrolls in its own scroll area.
        /// </summary>
        public bool ScrollBody
        {
            get => _scrollBody;
            set
            {
                _scrollBody = value;
                if (_shell is null) return;
                _shell.BodyScroller.VerticalScrollMode = value ? ScrollMode.Auto : ScrollMode.Disabled;
                _shell.BodyScroller.VerticalScrollBarVisibility = value ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            }
        }

        /// <summary>
        /// Whether the user can close it with the title bar ✕, Esc or Alt+F4 (default true). When false, no ✕ is shown, all three do nothing,
        /// and <see cref="OnDismissRequested"/> is not called; this only governs those three user gestures — code <see cref="Hide"/> still works and
        /// the bottom buttons stay clickable. Independent of whether there is a close button
        /// (a window that can be canceled should have a close button; a progress dialog overrides OnDismissRequested so ✕ equals "Cancel").
        /// Set false for wait dialogs that close themselves when done and must not be closed midway.
        /// </summary>
        public bool CanDismiss
        {
            get => _canDismiss;
            set { _canDismiss = value; ApplyButtons(); }
        }

        private bool _canDismiss = true;

        /// <summary>
        /// Whether the user may close it right now: <see cref="CanDismiss"/> by default. Derived classes can tighten it based on their own state
        /// (a progress dialog: not closable while there is no clickable "Cancel"); if it depends on button text or enabled state, no extra notification
        /// is needed — the base class re-evaluates whenever a button property changes.
        /// </summary>
        protected virtual bool IsDismissAllowed => _canDismiss;

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------

        public event DialogWindowEventHandler<DialogWindowButtonClickEventArgs>? PrimaryButtonClick;
        public event DialogWindowEventHandler<DialogWindowButtonClickEventArgs>? SecondaryButtonClick;

        /// <summary>The bottom close button was clicked (not raised by Esc, ✕ or Alt+F4, same as ContentDialog).</summary>
        public event DialogWindowEventHandler<DialogWindowButtonClickEventArgs>? CloseButtonClick;

        /// <summary>About to close; every way of closing (buttons, Esc, Hide) goes through here, except the cascade close when the owner closes.</summary>
        public event DialogWindowEventHandler<DialogWindowClosingEventArgs>? Closing;

        /// <summary>The window is shown and focus is placed.</summary>
        public event DialogWindowEventHandler<EventArgs>? Opened;

        // ------------------------------------------------------------------
        // Showing and closing
        // ------------------------------------------------------------------

        /// <summary>
        /// Shows as a modal owned window and returns the result once closed (Primary / Secondary / None). When the owner closes, this closes too and returns None.
        /// Each instance can be shown only once.
        /// </summary>
        public Task<ContentDialogResult> ShowAsync()
        {
            if (_showCalled)
                throw new InvalidOperationException("A dialog can only be shown once");
            _showCalled = true;

#if !SHIPPING
            if (SelfTestAutoAnswer?.Invoke(this) is ContentDialogResult answer)
            {
                _result = answer;
                _finished = true;
            }
#endif
            if (_finished)
            {
                // Result already known before showing (Hide was called first, or a self-test auto-answer): don't show
                Finish(hostClosed: false);
                return _done.Task;
            }

            if ((ModalWindows.Resolve(Owner) ?? ThemedWindow.FromXamlRoot(XamlRoot)) is not { } root)
            {
                _finished = true;
                Finish(hostClosed: false);
                throw new InvalidOperationException("Specify the dialog's window (Owner or XamlRoot) before showing it");
            }
            _owner = ThemedWindow.TopmostModalOver(root);

            _host = ModalHostPool.Rent();
            EnsureShell();
            _host.Mount(this, _owner, _shell!, ModalHostWindow.StandardCaptionHeight, shownInSwitchers: false, resizable: false);
            _host.SetTitleBar(_shell!.TitleBar);
            _host.CaptionChanged += ApplyTitle;
            ApplyWindowTitle();
            ApplyTitle();
            ApplyButtons();
            ApplyTheme();
            s_open.Add(this);
            _host.Attach(_owner);
            FitToContent(initial: true);

            TypedEventHandler<object, WindowActivatedEventArgs>? opened = null;
            opened = (_, e) =>
            {
                if (e.WindowActivationState == WindowActivationState.Deactivated) return;
                if (_host is { } h) h.Activated -= opened;
                _openedHandler = null;
                // Queued after the base class's "turn first-activation focus into pointer focus": put focus on the default button as pointer focus, no focus rectangle
                _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    if (_windowClosed) return;
                    InitialFocus()?.Focus(FocusState.Pointer);
                    ScheduleRefit();   // Once on screen, refit the height from the actual layout (the pre-show measurement can be off by 1 pixel)
#if !SHIPPING
                    _opened = true;
#endif
                    Opened?.Invoke(this, EventArgs.Empty);
                });
            };
            _host.Activated += opened;
            _openedHandler = opened;

            // A reused window would otherwise also show the previous content for a frame.
            // Shown cloaked first and revealed once the size has settled (see RevealWhenSettled): the size measured before showing does not always
            // match the first real layout, and correcting it while visible made the window visibly jump (measured on a media-info style body:
            // 326 DIP before showing → 539 on the first layout pass → 342 settled, the window moving up and down with each change).
            SetCloaked(true);
            // Modality needs AppWindow.Show(); with only Activate() the owner stays usable (see ModalNavWindow.ShowModal)
            _host.AppWindow.Show();
            _host.Activate();
            EnsureForeground();
            RevealWhenSettled(0, 0);
            return _done.Task;
        }

        private const int MaxRevealPasses = 20;
        private TypedEventHandler<object, WindowActivatedEventArgs>? _openedHandler;

        private void SetCloaked(bool cloaked)
        {
            _cloaked = cloaked;
            _host?.SetCloaked(cloaked);
        }

        /// <summary>
        /// While cloaked, refits once per layout turn (low priority, after the layout pass) until two turns in a row change nothing, the shell is laid
        /// out at the window's width and the body no longer scrolls; then uncloaks. The first layout pass can report a much larger scrollable height
        /// than the content really needs, so one refit is not enough. Gives up waiting after <see cref="MaxRevealPasses"/> turns and reveals anyway.
        /// </summary>
        private void RevealWhenSettled(int pass, int quietPasses)
        {
            _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_windowClosed || !_cloaked || _shell is null || _host is null) return;
                bool changed = FitToContent(initial: false);
                double scale = _shell.XamlRoot?.RasterizationScale ?? 1;
                bool laidOut = Math.Abs(_shell.ActualWidth * scale - _host.AppWindow.ClientSize.Width) <= 1
                               && (!_scrollBody || _shell.BodyScroller.ScrollableHeight <= 0.01);
                int quiet = !changed && laidOut ? quietPasses + 1 : 0;
                if (quiet >= 2 || pass >= MaxRevealPasses)
                {
                    if (pass >= MaxRevealPasses) UiHost.WriteTrace($"[Dialog] Size did not settle after {pass} layout turns; revealing anyway");
                    SetCloaked(false);
                    return;
                }
                RevealWhenSettled(pass + 1, quiet);
            });
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

        /// <summary>
        /// When the app is in the background (e.g. an app whose main window stays hidden in the tray pops up a prompt), the system may refuse to give
        /// this window the foreground: ask once more ourselves, and if that still fails, let the host take over (<see cref="UiHost.RequestForeground"/>,
        /// e.g. via a helper process).
        /// </summary>
        private void EnsureForeground()
        {
            IntPtr hwnd = WindowHandle;
            if (GetForegroundWindow() == hwnd || SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd)
                return;
            bool ok = UiHost.RequestForeground?.Invoke(hwnd) ?? false;
            UiHost.WriteTrace($"[Dialog] This process could not take the foreground; host takeover={ok}");
        }
        /// <summary>
        /// Initial focus: default button → first focusable control in the body → first visible command button → the shell itself. Some element must hold
        /// focus, otherwise key events have no origin and neither Esc nor Enter is received (measured: with no default button and a text-only body,
        /// Esc did not close the dialog). If the derived class has already focused a control in the body, leave it alone.
        /// </summary>
        private Control? InitialFocus()
        {
            if (_shell is null) return null;
            if (DefaultButtonElement is { IsEnabled: true, Visibility: Visibility.Visible } button)
                return button;
            if (FocusManager.GetFocusedElement(_shell.XamlRoot) is Control { FocusState: not FocusState.Unfocused } focused
                && !ReferenceEquals(focused, _shell.PrimaryButton) && !ReferenceEquals(focused, _shell.SecondaryButton) && !ReferenceEquals(focused, _shell.CloseButton))
                return null;
            if (FocusManager.FindFirstFocusableElement(_shell.BodyHost) is Control inBody)
                return inBody;
            foreach (Button command in new[] { _shell.PrimaryButton, _shell.SecondaryButton, _shell.CloseButton })
            {
                if (command is { IsEnabled: true, Visibility: Visibility.Visible })
                    return command;
            }
            // No buttons and no controls in the body: the shell takes focus itself (pointer focus, no rectangle), otherwise Esc does nothing on such a closable dialog (measured)
            _shell.IsTabStop = true;
            return _shell;
        }

        /// <summary>Closes from code (result None). Goes through <see cref="Closing"/> and can be canceled. Also works before ShowAsync.</summary>
        public void Hide() => TryFinish(ContentDialogResult.None);

        /// <summary>
        /// The user pressed the title bar ✕, Esc or Alt+F4 (called only when <see cref="CanDismiss"/> is true). Default: close with None
        /// (through Closing, without raising CloseButtonClick, same as Esc on ContentDialog). Derived classes can override it,
        /// e.g. a progress dialog treats it as clicking "Cancel", or a download dialog cancels the running download or asks for confirmation first.
        /// </summary>
        protected virtual void OnDismissRequested() => TryFinish(ContentDialogResult.None);

        /// <summary>Single entry point for ✕, Esc and Alt+F4: does nothing when the user may not close it (<see cref="CanDismiss"/> is false).</summary>
        private void RequestDismiss()
        {
            if (IsDismissAllowed)
                OnDismissRequested();
        }

        /// <summary>Clicks one of the bottom buttons from code: exactly like the user clicking it (raises its click event and closes unless canceled). For derived classes that map ✕ to a button.</summary>
        protected void PerformButtonClick(ContentDialogButton button) => OnButtonClicked(button);

        /// <summary>Called after the UI language changes: derived classes reset text set in code here (the base class refits the height to the new text afterwards).</summary>
        protected virtual void OnLanguageChanged() { }

        /// <summary>
        /// Every key press in the window arrives here first (including ones already handled by a control), before the base class handles Esc / Enter.
        /// To take over a key, a derived class sets <c>e.Handled = true</c> and the base class no longer treats it as close or default button
        /// (a shortcut-recording dialog relies on this to record Enter); keys left unhandled go through the base class as usual.
        /// </summary>
        protected new virtual void OnPreviewKeyDown(KeyRoutedEventArgs e) { }

        /// <summary>
        /// Closes the dialog right away (result None, or the result already decided), without raising <see cref="Closing"/> — what closing the
        /// window used to do. <see cref="Hide"/> is the normal path through Closing. Also works before ShowAsync.
        /// </summary>
        public void Close()
        {
            _finished = true;
            if (_showCalled)
                Finish(hostClosed: false);
        }

        void IModalGuest.OnCaptionCloseClicked() => RequestDismiss();

        // A system close by the user (Alt+F4) takes the same path as Esc / ✕
        void IModalGuest.OnSystemCloseRequested() => RequestDismiss();

        void IModalGuest.OnHostClosed() => Finish(hostClosed: true);

        void IModalGuest.OnOwnerClosing() => Close();

        void IModalGuest.OnTitleBarActivationChanged() => OnTitleBarActivationChanged();

        /// <summary>Closes after going through Closing; returns false if Closing canceled it.</summary>
        private bool TryFinish(ContentDialogResult result)
        {
            if (_finished)
                return true;
            if (_raisingClosing)
                return false;   // A close requested from within a Closing handler (e.g. Hide inside it): the close already in progress wins
            var args = new DialogWindowClosingEventArgs(result);
            _raisingClosing = true;
            try { Closing?.Invoke(this, args); }
            finally { _raisingClosing = false; }
            if (args.Cancel)
                return false;
            _finished = true;
            _result = result;
            if (!_showCalled)
                return true;   // Not shown yet: ShowAsync returns right away
            if (_host?.HasModalChildWindow == true)
            {
                // Another dialog still covers this one: hand over the result now, close once the one above closes (see OnModalChildClosed)
                _closePending = true;
                _done.TrySetResult(result);
                return true;
            }
            Finish(hostClosed: false);
            return true;
        }

        void IModalGuest.OnModalChildClosed()
        {
            if (_closePending && _host?.HasModalChildWindow != true)
                _dispatcher.TryEnqueue(() =>
                {
                    if (!_windowClosed) Finish(hostClosed: false);
                });
        }

        /// <summary>
        /// Ends the dialog exactly once, whichever way it closes: gives the window back (ends modality, hands activation back, hides; nothing
        /// to give back when the window itself is being destroyed), then hands over the result.
        /// </summary>
        private void Finish(bool hostClosed)
        {
            if (_windowClosed) return;
            _windowClosed = true;
            s_open.Remove(this);
            UiHost.LanguageChanged -= OnLanguageChangedCore;
            UiHost.ThemeChanged -= OnThemeSettingChanged;
            UiHost.AccentChanged -= OnAccentChanged;
            if (_host is { } host)
            {
                _host = null;
                host.CaptionChanged -= ApplyTitle;
                if (_openedHandler is { } opened) host.Activated -= opened;
                _openedHandler = null;
                if (!hostClosed) host.EndSession();
            }
            ContentDialogResult result = _finished ? _result : ContentDialogResult.None;   // No result = closed along with the owner
            _finished = true;
            Closed?.Invoke(this, EventArgs.Empty);
            // Deliver the result on a later tick: after modality is released the SDK restores the owner over the next few ticks, and a caller that
            // immediately shows another window on the same owner must not get tangled up with that
            if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => _done.TrySetResult(result)))
                _done.TrySetResult(result);
        }

        // ------------------------------------------------------------------
        // Shell
        // ------------------------------------------------------------------

        private void EnsureShell()
        {
            if (_shell is not null)
                return;
            // A XAML-derived dialog set its body as its content in InitializeComponent: keep it as the body. This control sits in the
            // shell's body area and presents the body as its own content. The title bar only shows the title and ✕, so the borrowed
            // window uses the standard caption height.
            if (_content is null && base.Content is { } xamlBody)
                _content = xamlBody;
            var shell = new DialogWindowShell();
            _shell = shell;
            shell.BodyHost.Child = this;

            shell.PrimaryButton.Click += (_, _) => OnButtonClicked(ContentDialogButton.Primary);
            shell.SecondaryButton.Click += (_, _) => OnButtonClicked(ContentDialogButton.Secondary);
            shell.CloseButton.Click += (_, _) => OnButtonClicked(ContentDialogButton.Close);
            // Key takeover by derived classes (e.g. recording a shortcut): handledEventsToo, before the base class's Esc / Enter, received even with focus on a bottom button.
            // Attached to the shell itself: with no buttons and no body controls focus sits on the shell, and a handler on the inner RootGrid would never fire
            shell.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => OnPreviewKeyDown(e)), handledEventsToo: true);
            shell.KeyDown += OnRootKeyDown;
            shell.BodyHost.SizeChanged += (_, _) => ScheduleRefit();
            shell.TitleBar.SizeChanged += (_, _) => ScheduleRefit();
            shell.ActualThemeChanged += (_, _) => _host?.ApplyTheme(shell.ActualTheme);

            ScrollBody = _scrollBody;
            ApplyBody();
            UiHost.LanguageChanged += OnLanguageChangedCore;
            UiHost.ThemeChanged += OnThemeSettingChanged;
            UiHost.AccentChanged += OnAccentChanged;
        }

        /// <summary>Window title (taskbar, accessibility): "Title - AppName".</summary>
        private void ApplyWindowTitle()
        {
            if (_host is null || _windowClosed) return;
            _host.Title = _title.Length == 0 ? UiHost.AppName : $"{_title} - {UiHost.AppName}";
        }

        private void ApplyTitle()
        {
            if (_shell is null || _windowClosed) return;   // Property changed after closing: just store the value, don't touch the closed window
            TextBlock title = _shell.TitleText;
            title.Text = _title;
            title.Visibility = _title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            double captionWidth = _host?.CaptionWidth ?? 0;
            // The drag region (the whole title bar) leaves room for ✕ on the right: otherwise clicks on ✕ land in the drag region and the button never gets them
            _shell.TitleBar.Margin = new Thickness(0, 0, captionWidth, 0);
            // No title and no ✕ (wait / progress dialogs): keep only a 22-high draggable title bar; plus the body's top 2 that equals the bottom 24,
            // so top and bottom whitespace are symmetric. With a title or ✕, use the standard title bar height.
            _shell.TitleBar.MinHeight = _title.Length == 0 && !IsDismissAllowed ? 22 : ModalHostWindow.StandardCaptionHeight;
            title.Margin = new Thickness(16, 0, captionWidth > 0 ? 0 : 16, 0);
        }

        private void ApplyBody()
        {
            if (_shell is null || _windowClosed) return;   // Property changed after closing: just store the value, don't touch the closed window
            base.Content = _content switch
            {
                null => null,
                UIElement element => element,
                string text => DialogText.Build(text),
                object other => DialogText.Build(other.ToString() ?? ""),
            };
        }

        private Button? DefaultButtonElement => _shell is null ? null : _defaultButton switch
        {
            ContentDialogButton.Primary => _shell.PrimaryButton,
            ContentDialogButton.Secondary => _shell.SecondaryButton,
            ContentDialogButton.Close => _shell.CloseButton,
            _ => null,
        };

        /// <summary>
        /// Buttons are laid out like ContentDialog's command area: visible buttons have equal width — three take a third each, two take half each,
        /// a single one takes the right half (the PrimaryVisible etc. states in generic.xaml); the default button uses AccentButtonStyle.
        /// With no buttons at all the command area collapses entirely.
        /// </summary>
        private void ApplyButtons()
        {
            if (_shell is null || _windowClosed) return;   // Property changed after closing: just store the value, don't touch the closed window
            DialogWindowShell s = _shell;
            Configure(s.PrimaryButton, _primaryText, _primaryEnabled, ContentDialogButton.Primary);
            Configure(s.SecondaryButton, _secondaryText, _secondaryEnabled, ContentDialogButton.Secondary);
            Configure(s.CloseButton, _closeText, true, ContentDialogButton.Close);

            var visible = new List<Button>(3);
            foreach (Button button in new[] { s.PrimaryButton, s.SecondaryButton, s.CloseButton })
            {
                if (button.Visibility == Visibility.Visible)
                    visible.Add(button);
            }
            s.CommandBar.Visibility = visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            int slots = Math.Max(2, visible.Count);
            if (s.CommandBar.ColumnDefinitions.Count != slots * 2 - 1)
            {
                s.CommandBar.ColumnDefinitions.Clear();
                for (int i = 0; i < slots; i++)
                {
                    if (i > 0)
                        s.CommandBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ButtonSpacingDip) });
                    s.CommandBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                }
            }
            int first = slots - visible.Count;
            for (int i = 0; i < visible.Count; i++)
                Grid.SetColumn(visible[i], (first + i) * 2);

            if (_host is not null) _host.CaptionHidden = !IsDismissAllowed;
            ApplyTitle();      // Whether there is a ✕ determines the title bar height
            ScheduleRefit();   // Showing / collapsing the command area changes the height
        }

        private void Configure(Button button, string text, bool enabled, ContentDialogButton kind)
        {
            button.Content = text;
            button.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            button.IsEnabled = enabled;
            if (_defaultButton == kind)
            {
                var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
                if (!ReferenceEquals(button.Style, accent)) button.Style = accent;
            }
            else
            {
                button.ClearValue(FrameworkElement.StyleProperty);
            }
        }

        private void OnButtonClicked(ContentDialogButton kind)
        {
            if (_finished)
                return;
            var args = new DialogWindowButtonClickEventArgs();
            (kind switch
            {
                ContentDialogButton.Primary => PrimaryButtonClick,
                ContentDialogButton.Secondary => SecondaryButtonClick,
                _ => CloseButtonClick,
            })?.Invoke(this, args);
            if (args.Cancel)
                return;
            TryFinish(kind switch
            {
                ContentDialogButton.Primary => ContentDialogResult.Primary,
                ContentDialogButton.Secondary => ContentDialogResult.Secondary,
                _ => ContentDialogResult.None,
            });
        }

        /// <summary>
        /// Esc / Enter: handled from the bubbling KeyDown at the shell root; keys already handled by body controls (opening/closing a combo box,
        /// a newline in a multi-line text box, Enter on the focused button) are left alone.
        /// No KeyboardAccelerator: an Esc accelerator on the root element was measured not to fire (see ModalNavWindow).
        /// </summary>
        private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Handled)
                return;
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                RequestDismiss();
            }
            else if (e.Key == VirtualKey.Enter && DefaultButtonElement is { IsEnabled: true, Visibility: Visibility.Visible })
            {
                e.Handled = true;
                OnButtonClicked(_defaultButton);
            }
        }

        // ------------------------------------------------------------------
        // Size
        // ------------------------------------------------------------------

        /// <summary>
        /// Refits the height to the content (width unchanged). With ScrollBody = true this happens automatically whenever the body size changes;
        /// with ScrollBody = false the body area is sized by the window and content changes are not visible to it, so derived classes call this
        /// after showing or hiding content.
        /// </summary>
        public void UpdateSizeToContent() => ScheduleRefit();

        private void ScheduleRefit()
        {
            if (!_showCalled || _windowClosed || _refitQueued)
                return;
            _refitQueued = _dispatcher.TryEnqueue(() =>
            {
                _refitQueued = false;
                FitToContent(initial: false);
            });
        }

        /// <summary>
        /// Sizes the window's client area to the content and centers it over the owner. The first time (before showing) width and height come from the
        /// DPI and work area of the owner's monitor; afterwards the width stays fixed and only the height is refit.
        /// First measure the width the content needs at the maximum width, then measure the needed height at that width with unlimited height;
        /// beyond the limit the window takes the limit and the body scrolls in its scroll area.
        /// </summary>
        /// <returns>Whether the window's size or position was changed.</returns>
        private bool FitToContent(bool initial)
        {
            if (_shell is null || _owner is null || _host is null || _windowClosed)
                return false;
            AppWindow window = _host.AppWindow;
            AppWindow reference = initial ? _owner.AppWindow : window;
            double scale = GetDpiForWindow(initial ? WindowNative.GetWindowHandle(_owner) : _host.Hwnd) / 96.0;
            if (scale <= 0) scale = 1;
            RectInt32 work = DisplayArea.GetFromWindowId(reference.Id, DisplayAreaFallback.Nearest).WorkArea;
            double maxWidth = Math.Max(1, Math.Min(MaxWidthDip, work.Width / scale - DisplayEdgeMarginDip * 2));
            double maxHeight = Math.Max(1, Math.Min(MaxHeightDip, work.Height / scale - DisplayEdgeMarginDip * 2));

            if (initial)
            {
                _shell.Measure(new Size(maxWidth, double.PositiveInfinity));
                _widthDip = Math.Clamp(_shell.DesiredSize.Width, Math.Min(MinWidthDip, maxWidth), maxWidth);
            }
            // Once shown, the actual layout wins: if the body can still scroll (content taller than the viewport), grow by the remainder. Read first,
            // then Measure (Measure changes layout state). The height measured before showing with unlimited height can be 1 pixel less than the
            // actual on-screen layout (0.667 DIP at 150%), and relying on the measurement alone leaves a scroll bar
            // (measured: a 4–5 line body measured 236 but laid out at 236.667).
            double laidOutNeed = 0;
            if (!initial && _scrollBody && _shell.BodyScroller.ScrollableHeight > 0.01)
                laidOutNeed = window.ClientSize.Height / scale + _shell.BodyScroller.ScrollableHeight;
            _shell.Measure(new Size(_widthDip, double.PositiveInfinity));
            double heightDip = Math.Clamp(Math.Max(_shell.DesiredSize.Height, laidOutNeed), Math.Min(MinHeightDip, maxHeight), maxHeight);
            _shell.InvalidateMeasure();   // Both measurements above used unlimited height; the next layout pass remeasures at the window's actual size

            // Always round physical pixels up: rounding to nearest can make the window a fraction of a DIP shorter than the content, which adds a
            // scroll bar to the body (symptom: a dialog whose content fits still showed a scroll bar). Subtract a tiny amount so floating-point error
            // doesn't bump an exact integer up by one.
            var size = new SizeInt32((int)Math.Ceiling(_widthDip * scale - 0.001), (int)Math.Ceiling(heightDip * scale - 0.001));
            SizeInt32 current = window.ClientSize;
            // Already large enough and at most 1 pixel larger: leave it (avoids oscillation). Shorter than the content, even by less than 1 pixel: resize —
            // an earlier "ignore any difference within 1 pixel" rule left that shortfall in place
            if (!initial && current.Width == size.Width && current.Height >= size.Height && current.Height - size.Height <= 1)
                return false;
            SetClientSize(window, size);
            WindowPlacement.CenterOver(window, _owner.AppWindow);
#if !SHIPPING
            FitHistoryForSelfTest.Add($"{(initial ? "initial" : _cloaked ? "hidden-refit" : "refit")} client {window.ClientSize.Width}×{window.ClientSize.Height} at {window.Position.X},{window.Position.Y} (measured {_widthDip:0.##}×{heightDip:0.##} DIP, scale {scale:0.##}, visible {window.IsVisible})");
            LastFitMatchedForSelfTest = window.ClientSize.Width == size.Width && window.ClientSize.Height == size.Height;
            LastFitForSelfTest = $"requested client {size.Width}×{size.Height} ({_widthDip:0}×{heightDip:0} DIP, scale {scale:0.##}) → actual client {window.ClientSize.Width}×{window.ClientSize.Height}, window {window.Size.Width}×{window.Size.Height}";
#endif
            return true;
        }

        /// <summary>
        /// Sets the client area to <paramref name="size"/> (physical pixels). Not AppWindow.ResizeClient: this window uses a dialog presenter with a border
        /// but no system title bar, yet ResizeClient still converts as if there were a title bar, so the client area ends up one title bar taller than
        /// requested (measured at 150%: requested 780, got 825). Instead convert using the current actual "outer frame − client area" difference,
        /// and verify once more after setting.
        /// </summary>
        private static void SetClientSize(AppWindow window, SizeInt32 size)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                SizeInt32 outer = window.Size, client = window.ClientSize;
                if (client.Width == size.Width && client.Height == size.Height)
                    return;
                window.Resize(new SizeInt32(size.Width + (outer.Width - client.Width), size.Height + (outer.Height - client.Height)));
            }
        }

        // ------------------------------------------------------------------
        // Theme, deactivation, language
        // ------------------------------------------------------------------

        private void ApplyTheme()
        {
            if (_shell is null) return;
            // Always apply an explicit Light/Dark: the owner window's theme (falls back to UiHost.ResolveTheme)
            ElementTheme theme = UiHost.ThemeFor(_owner);
            _shell.RequestedTheme = theme;
            _host?.ApplyTheme(theme);
        }

        private void OnThemeSettingChanged()
        {
            if (!_dispatcher.HasThreadAccess)
            {
                _dispatcher.TryEnqueue(OnThemeSettingChanged);
                return;
            }
            if (!_windowClosed) ApplyTheme();
        }

        // UiHost.AccentChanged may fire on a system thread; switch back to the UI thread
        private void OnAccentChanged() => _dispatcher.TryEnqueue(() =>
        {
            if (!_windowClosed && _shell is not null) UiHost.RefreshAccentResources?.Invoke(_shell);
        });

        private void OnLanguageChangedCore()
        {
            if (_windowClosed) return;
            OnLanguageChanged();
            ScheduleRefit();
        }

        /// <summary>Grays out the title text while the window is inactive (the base class provides the signal and color); cleared on reactivation.</summary>
        private void OnTitleBarActivationChanged()
        {
            if (_shell is null || _host is null) return;
            TextBlock title = _shell.TitleText;
            // Clear first: the color to dim is the style's own, not the dimmed brush left by the previous deactivation.
            title.ClearValue(TextBlock.ForegroundProperty);
            if (_host.IsTitleBarDeactivated && ModalHostWindow.DeactivatedForeground(title.Foreground) is Brush gray)
                title.Foreground = gray;
        }
    }
}


