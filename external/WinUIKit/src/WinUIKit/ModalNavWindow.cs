using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace WinUIKit
{
    /// <summary>
    /// Shared shell for "Windows Settings"-style modal navigation windows. Every such window derives from this one class,
    /// so they cannot drift apart over time (separately written copies did drift: 40 vs 48 DIP title bars, Esc closing
    /// only one of them, two different size-memory schemes, only one constrained to the screen).
    /// <para>
    /// What it unifies: extended title bar (48 DIP when it hosts the pane toggle button, leaving 48 DIP on the left for it;
    /// 32 DIP for single-page windows without one, see <see cref="TitleBarHeight"/>; window title
    /// "X - AppName", title bar shows just "X"), pane display mode switched between Left/LeftCompact by width, navigation
    /// pane corner radius and shadow removed, resizable but not minimizable/maximizable, minimum size scaled by DPI,
    /// remembers size only (in effective pixels) and never position, constrained to the owner's screen and centered on
    /// every open, theme follows the setting, Esc closes, modal (<see cref="ThemedWindow.AttachModalOwner"/>), and a single
    /// opening sequence (<see cref="ShowModal"/>). Page management, zero-animation first frame, card reflow, and the shared
    /// expander/combo box behavior live in ModalNavWindow.Pages.cs.
    /// Navigation pane background resources are in NavShellResources.xaml (merged by the derived window into its root Grid).
    /// </para>
    /// <para>
    /// [Not a window itself] This is the window's content: <see cref="ShowModal"/> borrows a native window from a pool
    /// (<see cref="ModalHostWindow"/>) and <see cref="Close"/> gives it back. Creating a native window per open leaks GDI objects in the
    /// Windows App SDK (see ModalHostWindow), so the window is reused. Create a new instance for every open, as before.
    /// <see cref="WindowHandle"/> and <see cref="AppWindow"/> refer to the borrowed window while it is shown.
    /// </para>
    /// Usage: the derived window's XAML root element is <c>utils:ModalNavWindow</c>; in the constructor, after
    /// InitializeComponent, call <see cref="InitializeShell"/> once, then <see cref="RegisterPage"/> in navigation order;
    /// open it only through <see cref="ShowModal"/>.
    /// </summary>
    public partial class ModalNavWindow : UserControl, IModalGuest
    {
        // Same as NavigationView's standard Auto breakpoint, but this class settles the final mode by hand before first
        // show, so the window doesn't play a pane transition from the default state on open (hence no PaneDisplayMode="Auto" in XAML).
        private const double ExpandedPaneThresholdDip = 1008;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private NavigationView? _nav;
        private FrameworkElement? _themeRoot;
        private UIElement? _titleBar;
        private Thickness _titleBarBaseMargin;
        private double _titleBarHeight = ModalHostWindow.TallCaptionHeight;
        private string _sizeKeyPrefix = "";
        private int _minWidthDip;
        private int _minHeightDip;
        private bool _resizable = true;
        private double _scale = 1.0;
        private double _widthDip;
        private double _heightDip;
        private Func<string> _windowName = () => "";
        private TextBlock? _titleBarText;
        private string _title = "";
        private ModalHostWindow? _host;
        private Window? _ownerWindow;   // set in ShowModal; the theme follows it (see ApplyTheme)
        private bool _closed;

        /// <summary>Current window DPI scale (physical pixels / effective pixels).</summary>
        protected double DpiScale => _scale;

        /// <summary>The borrowed window while shown.</summary>
        internal ModalHostWindow? Host => _host;

        /// <summary>Handle of the window this is shown in (IntPtr.Zero when not shown).</summary>
        public IntPtr WindowHandle => _host?.Hwnd ?? IntPtr.Zero;

        /// <summary>AppWindow of the window this is shown in (null when not shown).</summary>
        public AppWindow? AppWindow => _host?.AppWindow;

        /// <summary>Brings the window to the front and activates it (does nothing when not shown).</summary>
        public void Activate() => _host?.Activate();

        /// <summary>Window title ("Name - AppName"); set by <see cref="InitializeShell"/> from the window name.</summary>
        public string Title
        {
            get => _title;
            set
            {
                _title = value ?? "";
                if (_host is not null) _host.Title = _title;
            }
        }

        /// <summary>The window has closed (raised once, after the window was given back; also when it closes along with its owner).</summary>
        public event EventHandler? Closed;

        /// <summary>The window was activated or deactivated (forwarded from the borrowed window while shown).</summary>
        public event TypedEventHandler<object, WindowActivatedEventArgs>? Activated;

        /// <summary>
        /// Assembles the shell. Must be called after InitializeComponent and before <see cref="ShowModal"/>.
        /// </summary>
        /// <param name="nav">Full-height NavigationView.</param>
        /// <param name="titleBar">Title bar drag-region element (passed to SetTitleBar).</param>
        /// <param name="titleBarText">Text shown in the title bar.</param>
        /// <param name="pageHost">Page host (pages live inside it and are shown/hidden by selection).</param>
        /// <param name="pageTitle">Page header title.</param>
        /// <param name="themeRoot">Root element: the theme is set on it, and Esc is handled on it.</param>
        /// <param name="name">Window name, e.g. "Settings". The window title is "Name - AppName"; the title bar shows only the name. Re-read on language change.</param>
        /// <param name="keyPrefix">UI-state key prefix: &lt;prefix&gt;.WidthDip / .HeightDip / .LastPage / .Expander.&lt;name&gt;.</param>
        /// <param name="resizable">false = fixed size (the default size), no border resizing, and the size is not remembered.</param>
        protected void InitializeShell(NavigationView nav, UIElement titleBar, TextBlock titleBarText, Panel pageHost,
            TextBlock pageTitle, FrameworkElement themeRoot, Func<string> name, string keyPrefix,
            int defaultWidthDip, int defaultHeightDip, int minWidthDip, int minHeightDip, bool resizable = true)
        {
            string sizeKeyPrefix = keyPrefix;
            _resizable = resizable;
            _nav = nav;
            _themeRoot = themeRoot;
            _titleBar = titleBar;
            _titleBarBaseMargin = titleBar is FrameworkElement bar ? bar.Margin : default;
            ApplyTitleBarHeight(nav, titleBar);
            _sizeKeyPrefix = sizeKeyPrefix;
            _minWidthDip = minWidthDip;
            _minHeightDip = minHeightDip;

            // By default NavigationView gives the overlay SplitView pane an OverlayCornerRadius on its right side plus a shadow.
            // The pane runs from the window's top edge to its bottom edge, so like Windows Settings it uses square edges and no shadow line.
            nav.Loaded += (_, _) => RemoveNavigationPaneCornerRadius(nav);

            _windowName = name;
            _titleBarText = titleBarText;
            ApplyWindowName();

            // Remember size only (effective pixels), never position: every open centers on the owner, see FitAndCenterOver.
            // A fixed-size window ignores any remembered size (one may have been stored while it was still resizable).
            _widthDip = resizable ? ReadDimension(sizeKeyPrefix + ".WidthDip", defaultWidthDip, minWidthDip) : defaultWidthDip;
            _heightDip = resizable ? ReadDimension(sizeKeyPrefix + ".HeightDip", defaultHeightDip, minHeightDip) : defaultHeightDip;

            // The pane display mode must be settled BEFORE the window is first shown, otherwise opening the pane counts
            // as a state change and animates.
            UpdatePaneDisplayMode();

            ApplyTheme();
            themeRoot.ActualThemeChanged += (_, _) => _host?.ApplyTheme(themeRoot.ActualTheme);
            UiHost.ThemeChanged += OnThemeSettingChanged;
            UiHost.AccentChanged += OnAccentChanged;   // accent override changed: already-rendered controls re-read accent resources

            // Esc closes: handle the bubbling KeyDown on the root element (only keys no child handled). An open combo box
            // swallows Esc in its own popup first, so it never gets here; a dialog (DialogWindow) is a separate window and
            // this one is disabled meanwhile, so it receives no keys. NOT a KeyboardAccelerator: measured, an Esc accelerator
            // attached to the root element never fires. Enter is not handled, to avoid clashing with combo boxes/sliders.
            themeRoot.KeyDown += (_, args) =>
            {
                if (args.Key != VirtualKey.Escape || args.Handled)
                    return;
                args.Handled = true;
                OnEscapePressed();
            };
            UiHost.LanguageChanged += OnLanguageChangedCore;

            InitializePages(pageHost, pageTitle);
        }

        /// <summary>
        /// Shows the window as a modal owned window over <paramref name="owner"/> (a <see cref="Window"/>, or a shown
        /// <see cref="DialogWindow"/> / <see cref="ModalNavWindow"/>). The one opening sequence: borrow a window → make modal → size to
        /// the owner's screen and center → prepare the current page's first frame → show. Showing first and centering afterwards flashes one
        /// frame at the system default position and then jumps to the center (visible movement and flicker).
        /// Can be shown once per instance.
        /// </summary>
        protected void ShowModal(object owner)
        {
            if (_host is not null || _closed)
                throw new InvalidOperationException("A navigation window can only be shown once");
            Window ownerWindow = ModalWindows.Resolve(owner) ?? throw new ArgumentException("Owner must be a Window or a shown WinUIKit window", nameof(owner));

            _ownerWindow = ownerWindow;
            ModalHostWindow host = ModalHostPool.Rent();
            _host = host;
            host.Mount(this, ownerWindow, this, _titleBarHeight, shownInSwitchers: true, resizable: _resizable);
            host.Title = _title;
            if (_titleBar is not null) host.SetTitleBar(_titleBar);
            host.CaptionChanged += ReserveCaptionWidth;
            ReserveCaptionWidth();
            host.AppWindow.Changed += AppWindow_Changed;
            host.Activated += OnHostActivated;
            UpdateScale();
            ApplyTheme();

            host.Attach(ownerWindow);
            FitAndCenterOver(ownerWindow);
            OnShowing();
            // Shown cloaked and revealed once the content has rendered: a reused window still holds the last frame of whatever it showed
            // before, which would flash for a frame otherwise.
            host.SetCloaked(true);
            // Modality depends on AppWindow.Show(): with only Window.Activate() the window is not modal and the owner still
            // responds to mouse and keyboard (measured).
            // WinUI 3 Gallery (SampleSupport/SamplePages/ModalWindow.xaml.cs) calls only Show(); dropping the Activate()
            // below does not change the navigation focus rectangle when opened via a keyboard shortcut (measured).
            host.AppWindow.Show();
            host.Activate();
            RevealWhenRendered(host);
        }

        // Uncloaks after the content has been loaded and two frames have rendered (or after about a second, whatever happens)
        private void RevealWhenRendered(ModalHostWindow host)
        {
            EventHandler<object>? onRendering = null;
            int seen = 0;
            long started = Environment.TickCount64;
            onRendering = (_, _) =>
            {
                if (!ReferenceEquals(_host, host))
                {
                    CompositionTarget.Rendering -= onRendering;
                    return;
                }
                if (IsLoaded) seen++;
                if (seen < 2 && Environment.TickCount64 - started < 1000) return;
                CompositionTarget.Rendering -= onRendering;
                host.SetCloaked(false);
            };
            CompositionTarget.Rendering += onRendering;
        }

        /// <summary>
        /// Closes the window: gives the borrowed window back (closing dialogs shown over it, ending modality, handing activation
        /// back to the owner, hiding), persists the size and raises <see cref="Closed"/>.
        /// </summary>
        public void Close() => Finish(hostClosed: false);

        private void Finish(bool hostClosed)
        {
            if (_closed) return;
            _closed = true;
            // Only persist the cached size (not for fixed-size windows).
            if (_resizable)
            {
                UiHost.SetSetting(_sizeKeyPrefix + ".WidthDip", _widthDip.ToString("0.###", CultureInfo.InvariantCulture));
                UiHost.SetSetting(_sizeKeyPrefix + ".HeightDip", _heightDip.ToString("0.###", CultureInfo.InvariantCulture));
                UiHost.FlushSettings();
            }
            UiHost.ThemeChanged -= OnThemeSettingChanged;
            UiHost.AccentChanged -= OnAccentChanged;
            UiHost.LanguageChanged -= OnLanguageChangedCore;
            OnClosingPages();
            if (_host is { } host)
            {
                _host = null;
                _ownerWindow = null;
                host.CaptionChanged -= ReserveCaptionWidth;
                host.Activated -= OnHostActivated;
                // Do NOT touch AppWindow when the window is being destroyed: the native window is being torn down and reading it may AV.
                if (!hostClosed)
                {
                    host.AppWindow.Changed -= AppWindow_Changed;
                    host.EndSession();
                }
            }
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private void OnHostActivated(object sender, WindowActivatedEventArgs args) => Activated?.Invoke(this, args);

        void IModalGuest.OnSystemCloseRequested() => Close();

        /// <summary>Clicking the title bar close button takes the same path as pressing Esc.</summary>
        void IModalGuest.OnCaptionCloseClicked() => OnEscapePressed();

        void IModalGuest.OnTitleBarActivationChanged() => OnTitleBarActivationChanged();

        void IModalGuest.OnModalChildClosed() { }

        void IModalGuest.OnHostClosed() => Finish(hostClosed: true);

        void IModalGuest.OnOwnerClosing() => Close();

        /// <summary>A modal window (e.g. a dialog) is open over this one (this window is disabled).</summary>
        protected bool HasModalChild => _host?.HasModalChildWindow == true;

        private void ApplyWindowName()
        {
            string name = _windowName();
            Title = $"{name} - {UiHost.AppName}";
            _titleBarText!.Text = name;
        }

        /// <summary>Language change: re-read the window name and page title (navigation item text refreshes via its XAML binding), then hand off to the derived window.</summary>
        private void OnLanguageChangedCore()
        {
            ApplyWindowName();
            OnLanguageChanged();   // before the page title: the derived window may reset navigation item text here (a single-page window's title comes from it)
            RefreshPageTitle();
            ResizeAutoSizedCombos();   // last: the derived window may have just rebuilt combo box items
        }

        /// <summary>Called after a language change; derived windows reset text composed in code here.</summary>
        protected virtual void OnLanguageChanged() { }

        /// <summary>
        /// Title bar height (DIP), decided once by <see cref="InitializeShell"/>. Per the Windows title bar guidance the tall height
        /// is for title bars that host interactive content: a window whose title bar holds the pane toggle button uses 48; a
        /// single-page window (pane or toggle button hidden) has only the title and the close button there and uses the standard 32,
        /// like <see cref="DialogWindow"/>.
        /// </summary>
        protected double TitleBarHeight => _titleBarHeight;

        /// <summary>
        /// Applies <see cref="TitleBarHeight"/> to everything that depends on it, so a derived window doesn't hard-code it in XAML:
        /// the caption buttons (passed to the borrowed window in <see cref="ShowModal"/>), the title bar's row in its parent Grid, and
        /// the top margin of the navigation view's content (the content starts below the title bar because the view spans the window).
        /// The XAML values are only the initial layout; they are overwritten here.
        /// </summary>
        private void ApplyTitleBarHeight(NavigationView nav, UIElement titleBar)
        {
            bool toggleInTitleBar = nav.IsPaneVisible && nav.IsPaneToggleButtonVisible;
            _titleBarHeight = toggleInTitleBar ? ModalHostWindow.TallCaptionHeight : ModalHostWindow.StandardCaptionHeight;
            if (titleBar is FrameworkElement bar && bar.Parent is Grid grid)
            {
                int row = Grid.GetRow(bar);
                if (row < grid.RowDefinitions.Count)
                    grid.RowDefinitions[row].Height = new GridLength(_titleBarHeight);
            }
            if (!toggleInTitleBar && nav.Content is FrameworkElement content)
            {
                Thickness m = content.Margin;
                content.Margin = new Thickness(m.Left, _titleBarHeight, m.Right, m.Bottom);
            }
        }

        /// <summary>Action for Esc; closes the window by default.</summary>
        protected virtual void OnEscapePressed() => Close();

        /// <summary>
        /// Reserves the caption buttons' width on the right of the title bar element (a modal window has only the close button,
        /// drawn by the borrowed window, see ThemedWindow.InstallCaptionButtons).
        /// </summary>
        private void ReserveCaptionWidth()
        {
            if (_titleBar is not FrameworkElement bar) return;
            Thickness m = _titleBarBaseMargin;
            bar.Margin = new Thickness(m.Left, m.Top, m.Right + (_host?.CaptionWidth ?? 0), m.Bottom);
        }

        /// <summary>Grays the title text while the window is inactive (the base class supplies the signal and color); clears it on reactivation to restore the style's color.</summary>
        private void OnTitleBarActivationChanged()
        {
            if (_titleBarText is null || _host is null) return;
            // Clear first: the color to dim is the style's own, not the dimmed brush left by the previous deactivation.
            _titleBarText.ClearValue(TextBlock.ForegroundProperty);
            if (_host.IsTitleBarDeactivated && ModalHostWindow.DeactivatedForeground(_titleBarText.Foreground) is Brush gray)
                _titleBarText.Foreground = gray;
        }

        /// <summary>
        /// Constrains the size to the owner's screen and centers on the owner. Called on every open (old coordinates are not
        /// reused: the owner may have moved elsewhere or to another monitor).
        /// Only <see cref="ShowModal"/> calls it, before Activate: calling it afterwards shows one frame at the system default
        /// position and then jumps to the center.
        /// Before showing, the window's DPI may be that of another screen, so the owner's DPI is used.
        /// When the work area can't fit the design minimum size (low resolution or high scaling), the minimum is lowered
        /// dynamically and the size is clipped to the work area; pages scroll their own content.
        /// </summary>
        private void FitAndCenterOver(Window owner)
        {
            AppWindow window = _host!.AppWindow;
            double ownerScale = GetDpiForWindow(WindowNative.GetWindowHandle(owner)) / 96.0;
            if (ownerScale > 0) _scale = ownerScale;
            RectInt32 workArea = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var (width, height, minWidth, minHeight) = WindowStateRules.FitSize(
                _widthDip, _heightDip, _minWidthDip, _minHeightDip, _scale, workArea.Width, workArea.Height);

            if (window.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = minWidth;
                presenter.PreferredMinimumHeight = minHeight;
            }

            window.Resize(new SizeInt32(width, height));
            WindowPlacement.CenterOver(window, owner.AppWindow);
            UpdatePaneDisplayMode();
        }

        /// <summary>Applies the current theme setting to the root element and the window frame.</summary>
        protected void ApplyTheme()
        {
            if (_themeRoot is null) return;
            // Always set an explicit Light/Dark. ElementTheme.Default can't stand for "auto": it resolves to
            // Application.RequestedTheme, which is fixed once set, so the window would stay on the theme from process start forever.
            // Follow the owner window's theme (falls back to UiHost.ResolveTheme before the window is shown)
            ElementTheme theme = UiHost.ThemeFor(_ownerWindow);
            _themeRoot.RequestedTheme = theme;
            _host?.ApplyTheme(theme);   // first/last-frame erase color, title bar colors, caption buttons
        }

        // UiHost.AccentChanged may fire on a system thread; marshal back to the UI thread.
        private void OnAccentChanged() => DispatcherQueue.TryEnqueue(() =>
        {
            if (_themeRoot is not null) UiHost.RefreshAccentResources?.Invoke(_themeRoot);
        });

        private void OnThemeSettingChanged()
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(OnThemeSettingChanged);
                return;
            }
            ApplyTheme();
        }

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidSizeChange || _scale <= 0 || _closed)
                return;
            // Only cache the effective-pixel size and persist it on close; don't write to disk repeatedly during a drag.
            // On a low-resolution display the effective minimum may be below the design minimum; record the real size here
            // and constrain it to the work area of the time on the next open.
            _widthDip = Math.Max(1, sender.Size.Width / _scale);
            _heightDip = Math.Max(1, sender.Size.Height / _scale);
            UpdatePaneDisplayMode();
        }

        /// <summary>Switches between Left / LeftCompact by window width (not LeftMinimal: even at the narrowest the icon strip and pane toggle stay).</summary>
        private void UpdatePaneDisplayMode()
        {
            // A single-page window (IsPaneVisible=False) has no navigation pane, so there is no mode to switch.
            if (_nav is null || !_nav.IsPaneVisible) return;
            double widthDip = _host is { } host ? host.AppWindow.Size.Width / _scale : _widthDip;
            NavigationViewPaneDisplayMode mode = widthDip >= ExpandedPaneThresholdDip
                ? NavigationViewPaneDisplayMode.Left
                : NavigationViewPaneDisplayMode.LeftCompact;
            if (_nav.PaneDisplayMode == mode)
                return;
            _nav.PaneDisplayMode = mode;
            _nav.IsPaneOpen = mode == NavigationViewPaneDisplayMode.Left;
        }

        private void UpdateScale()
        {
            if (_host is null) return;
            double scale = GetDpiForWindow(_host.Hwnd) / 96.0;
            if (scale > 0) _scale = scale;
        }

        private static double ReadDimension(string key, double fallback, double minimum) =>
            WindowStateRules.ParseDimension(UiHost.GetSetting(key, string.Empty), fallback, minimum);

        private static void RemoveNavigationPaneCornerRadius(NavigationView navigationView)
        {
            navigationView.ApplyTemplate();
            SplitView? paneHost = VisualTreeHelperExtensions.FindChildren<SplitView>(navigationView).FirstOrDefault();
            if (paneHost is not null)
            {
                paneHost.CornerRadius = new CornerRadius(0);
                paneHost.BorderThickness = new Thickness(0);
            }

            // The gray line on the right comes from a separate shadow layer that NavigationView attaches with ThemeShadow in
            // Compact/Minimal overlay modes, not from SplitView.Border.
            Grid? shadowCaster = VisualTreeHelperExtensions.FindChildren<Grid>(navigationView)
                .FirstOrDefault(grid => grid.Name == "ShadowCaster");
            if (shadowCaster is not null)
                shadowCaster.Visibility = Visibility.Collapsed;
        }
    }
}



