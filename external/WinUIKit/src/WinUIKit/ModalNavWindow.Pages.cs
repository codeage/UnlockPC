using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinUIKit
{
    /// <summary>Optional page interface: notified every time the page is navigated to (including the first time), e.g. to re-validate state that may have changed outside the window.</summary>
    public interface INavShellPage
    {
        void OnShown();
    }

    /// <summary>
    /// Page side of ModalNavWindow: page registration and caching, page switching, page header title, remembering the
    /// last page, page entrance animation, zero-animation first display (SettingsExpander inner Storyboard and chevron
    /// final state), smooth card reflow, instant collapse, remembered expander state, scroll-into-view after expanding,
    /// and uniform combo box width and behavior. All modal navigation windows share this one implementation; pages only
    /// supply content and declarative attached properties (<see cref="NavShell"/>).
    /// </summary>
    public partial class ModalNavWindow
    {
        // The page title is not snapshotted: navigation item text changes with the language, so it is read from the
        // item on each display (see TitleOf).
        private sealed record PageEntry(NavigationViewItem Item, string Tag, Func<FrameworkElement> Factory);

        private readonly Dictionary<string, PageEntry> _pageEntries = new();
        private readonly List<string> _pageOrder = new();
        private readonly Dictionary<string, FrameworkElement> _pages = new();
        private readonly Dictionary<FrameworkElement, List<Panel>> _reflowPanels = new();
        private Panel? _pageHost;
        private TextBlock? _pageTitle;
        private string? _currentPageTag;
        private bool _shown;
        private bool _entranceReady;

        // Two-frame correction on first display (see CompleteInitialStates).
        private readonly HashSet<FrameworkElement> _initializedPages = new();
        private readonly HashSet<FrameworkElement> _pendingPages = new();
        private readonly Dictionary<AnimatedIcon, string> _iconsToNormalize = new();
        private bool _renderingHooked;

        /// <summary>Tag of the page currently shown (the navigation item's Tag).</summary>
        protected string? CurrentPageTag => _currentPageTag;

        /// <summary>
        /// Remember the last visited page only for the current run: reopening within the same process returns to that page,
        /// and after a restart the first page is shown. Defaults to false (remembered across launches in the UI-state store).
        /// Must be set before <see cref="NavigateToLastPage"/>.
        /// </summary>
        protected bool RememberLastPageThisLaunchOnly { get; set; }

        // Windows using RememberLastPageThisLaunchOnly store their page here (keyed by UI-state key prefix); gone when the process exits.
        private static readonly Dictionary<string, string> s_lastPageThisLaunch = new();

        /// <summary>
        /// Registers a page: a navigation item (its Tag is the page id, its Content the page header title, which may be bound)
        /// plus a page factory. The factory runs once, the first time the page is navigated to, and the page is cached after
        /// that (scroll position and expander state persist for the window's lifetime). The factory may return an element
        /// already inside the page host in XAML, or create a new one (which is added to the page host).
        /// Register in navigation order; the first registered page is the default.
        /// </summary>
        protected void RegisterPage(NavigationViewItem item, Func<FrameworkElement> factory)
        {
            string tag = item.Tag as string ?? throw new ArgumentException("Navigation item must have a string Tag", nameof(item));
            _pageEntries[tag] = new PageEntry(item, tag, factory);
            _pageOrder.Add(tag);
        }

        /// <summary>Whether the page can be shown right now (e.g. a page that is still locked can't). If the last page is unavailable, falls back to the first page.</summary>
        protected virtual bool IsPageAvailable(string tag) => true;

        /// <summary>Called after each navigation to a page (including the first); derived windows can update header buttons etc.</summary>
        protected virtual void OnPageShown(string tag, FrameworkElement page) { }

        /// <summary>Navigates to the given page (selects its navigation item).</summary>
        protected void NavigateTo(string tag)
        {
            if (_pageEntries.TryGetValue(tag, out PageEntry? entry) && _nav is not null)
                _nav.SelectedItem = entry.Item;
        }

        /// <summary>Navigates to the last visited page, or to the first available page if there is none or it is unavailable (rules in <see cref="WindowStateRules.ChooseStartPage"/>).</summary>
        protected void NavigateToLastPage()
        {
            string saved = RememberLastPageThisLaunchOnly
                ? s_lastPageThisLaunch.GetValueOrDefault(_sizeKeyPrefix, string.Empty)
                : UiHost.GetSetting(_sizeKeyPrefix + ".LastPage", string.Empty);
            string? tag = WindowStateRules.ChooseStartPage(saved, _pageOrder, t => _pageEntries.ContainsKey(t) && IsPageAvailable(t));
            if (tag is not null)
                NavigateTo(tag);
        }

        /// <summary>Just before the window is shown (see ShowModal): prepare the current page's first frame.</summary>
        private void OnShowing()
        {
            _shown = true;
            if (_currentPageTag is not null && _pages.TryGetValue(_currentPageTag, out FrameworkElement? page))
                PrepareFirstFrame(page);
        }

        /// <summary>The window is closing: stop the first-frame correction.</summary>
        private void OnClosingPages()
        {
            if (_renderingHooked)
            {
                CompositionTarget.Rendering -= CompleteInitialStates;
                _renderingHooked = false;
            }
            _pendingPages.Clear();
            _iconsToNormalize.Clear();
        }

        // A derived window may replace a navigation item's Content with a "text + icon" panel (e.g. a warning badge); take its text.
        private static string TitleOf(PageEntry entry) => entry.Item.Content switch
        {
            string s => s,
            Panel p when p.Children.OfType<TextBlock>().FirstOrDefault() is { } tb => tb.Text,
            _ => entry.Tag,
        };

        /// <summary>Re-reads the current page's header title (after a language change).</summary>
        private void RefreshPageTitle()
        {
            if (_currentPageTag is not null && _pageTitle is not null && _pageEntries.TryGetValue(_currentPageTag, out PageEntry? entry))
                _pageTitle.Text = TitleOf(entry);
        }

        // Combo boxes auto-sized to their longest item on the first frame. A language change changes item text length
        // (translations are often longer), so they must be re-sized or the text gets truncated.
        private readonly HashSet<ComboBox> _autoSizedCombos = new();

        private void ResizeAutoSizedCombos()
        {
            foreach (ComboBox comboBox in _autoSizedCombos)
                ComboBoxSizing.SizeToLongestItem(comboBox);
        }

        private void InitializePages(Panel pageHost, TextBlock pageTitle)
        {
            _pageHost = pageHost;
            _pageTitle = pageTitle;
            _nav!.SelectionChanged += OnNavSelectionChanged;
        }

        private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if ((args.SelectedItemContainer ?? args.SelectedItem as NavigationViewItem)?.Tag is string tag
                && _pageEntries.ContainsKey(tag))
                ShowPage(tag);
        }

        private void ShowPage(string tag)
        {
            if (tag == _currentPageTag)
                return;
            PageEntry entry = _pageEntries[tag];
            if (!_pages.TryGetValue(tag, out FrameworkElement? page))
            {
                page = entry.Factory();
                // Check the host's Children, not page.Parent: before the window is shown, an element already in the host
                // in XAML may still have a null Parent, and adding it again throws COMException 0x800F1000 (measured: crash on open).
                if (!_pageHost!.Children.Contains(page))
                    _pageHost.Children.Add(page);
                OnPageCreated(page);
                _pages[tag] = page;
            }

            // The page host holds only pages: hide all but the current one (including pages placed in the host in XAML
            // that haven't been navigated to yet).
            foreach (UIElement child in _pageHost!.Children)
                child.Visibility = ReferenceEquals(child, page) ? Visibility.Visible : Visibility.Collapsed;
            _currentPageTag = tag;
            _pageTitle!.Text = TitleOf(entry);
            // return to this page on the next open
            if (RememberLastPageThisLaunchOnly) s_lastPageThisLaunch[_sizeKeyPrefix] = tag;
            else UiHost.SetSetting(_sizeKeyPrefix + ".LastPage", tag);

            if (_shown && !_initializedPages.Contains(page))
                PrepareFirstFrame(page);
            // The first frame is static; only later user page switches slide the new page in from below.
            if (_entranceReady)
                PageEntranceAnimation.Play(page);

            OnPageShown(tag, page);
            (page as INavShellPage)?.OnShown();
        }

        /// <summary>
        /// Page just created (not yet in the visual tree, control templates not yet applied): restore remembered expander
        /// state — it must be written before the template is applied so the inner Expander is created directly in its final
        /// state; hook expander events; register panels for smooth reflow; attach the shared combo box behavior.
        /// </summary>
        private void OnPageCreated(FrameworkElement page)
        {
            var reflow = new List<Panel>();
            foreach (DependencyObject node in NavShell.ContentDescendants(page))
            {
                switch (node)
                {
                    case SettingsExpander expander:
                        if (NavShell.GetRememberExpanded(expander) && ExpanderKey(expander) is string key)
                            expander.IsExpanded = UiHost.GetSetting(key, expander.IsExpanded);
                        expander.Expanded += Expander_Expanded;
                        expander.Collapsed += Expander_Collapsed;
                        break;
                    case Panel panel when NavShell.GetReflow(panel):
                        reflow.Add(panel);
                        break;
                    case ComboBox comboBox:
                        ComboBoxSizing.AttachBehaviors(comboBox);
                        break;
                }
            }
            _reflowPanels[page] = reflow;
        }

        private string? ExpanderKey(FrameworkElement expander) =>
            string.IsNullOrEmpty(expander.Name) ? null : $"{_sizeKeyPrefix}.Expander.{expander.Name}";

        // ───────────── Zero-animation first display ─────────────

        /// <summary>
        /// Before a page is first shown: size combo boxes to their longest item (pages have filled their items by now); make
        /// the page transparent and hook Rendering for a two-frame correction before revealing it.
        /// Even with IsExpanded=true initially, the WinUI Expander inside SettingsExpander plays its 333 ms ExpandDown
        /// Storyboard when the template is first applied; GoToState(useTransitions:false) skips only VisualTransitions, not a
        /// VisualState's own Storyboard, so the only option is to skip it to its end.
        /// </summary>
        private void PrepareFirstFrame(FrameworkElement page)
        {
            if (_initializedPages.Contains(page) || !_pendingPages.Add(page))
                return;

            foreach (DependencyObject node in NavShell.ContentDescendants(page))
                if (node is ComboBox comboBox && double.IsNaN(comboBox.Width))
                {
                    ComboBoxSizing.SizeToLongestItem(comboBox);
                    _autoSizedCombos.Add(comboBox);
                }

            page.Opacity = 0;
            HookRendering();
        }

        private void HookRendering()
        {
            if (_renderingHooked)
                return;
            _renderingHooked = true;
            CompositionTarget.Rendering += CompleteInitialStates;
        }

        private void CompleteInitialStates(object? sender, object e)
        {
            CompositionTarget.Rendering -= CompleteInitialStates;
            _renderingHooked = false;

            // Rendering is a UI-thread-wide event: other windows render frames too. If a page isn't really in the visual
            // tree yet (template not applied), wait for the next frame.
            if (_pendingPages.Any(p => !p.IsLoaded))
            {
                HookRendering();
                return;
            }

            // Phase two: the "transition end" marker written last frame has been consumed by AnimatedIcon during layout.
            // Restore the standard state names; there is no transition segment from the end marker to the standard state,
            // so it just jumps there without rotating. The chevron already looks right, so the page can be shown.
            if (_iconsToNormalize.Count > 0)
            {
                foreach ((AnimatedIcon icon, string state) in _iconsToNormalize.ToArray())
                {
                    AnimatedIcon.SetState(icon, state);
                    icon.InvalidateArrange();
                    icon.UpdateLayout();
                }
                _iconsToNormalize.Clear();
                ShowPendingPages();
                return;
            }

            // Phase one: skip the current VisualState Storyboards to their end and point AnimatedIcons at the transition end marker.
            foreach (FrameworkElement page in _pendingPages.ToArray())
                CompleteActiveVisualStateStoryboards(page);

            // AnimatedIcon defers consuming State until a later LayoutUpdated. Restoring the standard state in the same
            // callback would merge the two writes, overwrite the end marker, and the original rotation would play. Wait one more frame.
            if (_iconsToNormalize.Count > 0)
            {
                HookRendering();
                return;
            }

            ShowPendingPages();
        }

        private void ShowPendingPages()
        {
            foreach (FrameworkElement page in _pendingPages)
            {
                page.Opacity = 1;
                _initializedPages.Add(page);
                // The initial layout has reached its static final state. Attaching implicit animations doesn't start any
                // animation, so the first frame stays static and reflow is ready before the first interaction.
                if (_reflowPanels.TryGetValue(page, out List<Panel>? panels))
                    foreach (Panel panel in panels)
                        CardReflow.Arm(panel);
            }
            _pendingPages.Clear();
            _entranceReady = true;
        }

        private void CompleteActiveVisualStateStoryboards(DependencyObject element)
        {
            if (element is AnimatedIcon animatedIcon)
                PrimeInitialAnimatedIconState(animatedIcon);

            if (element is FrameworkElement frameworkElement)
            {
                foreach (VisualStateGroup group in VisualStateManager.GetVisualStateGroups(frameworkElement))
                {
                    try { group.CurrentState?.Storyboard?.SkipToFill(); }
                    catch (InvalidOperationException) { /* no controllable Storyboard running; already in its static final state */ }
                }
            }

            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
                CompleteActiveVisualStateStoryboards(VisualTreeHelper.GetChild(element, i));
        }

        /// <summary>
        /// AnimatedIcon progress is driven by a separate Composition animation that Storyboard.SkipToFill doesn't affect.
        /// Point State at the existing "transition end" marker first, then normalize to the real state on the next frame.
        /// On the first frame AnimatedIcon.State can't be trusted (it may still be the template default NormalOff); the
        /// direction comes from the actual inner Expander.
        /// </summary>
        private void PrimeInitialAnimatedIconState(AnimatedIcon icon)
        {
            Expander? owner = FindAncestor<Expander>(icon);
            if (owner is null)
                return;
            bool expanded = owner.IsExpanded;
            // From now on the chevron never animates (no hover nudge, no rotation on click); see ExpanderChevronStill.
            ExpanderChevronStill.Attach(icon);
            _iconsToNormalize[icon] = expanded ? "NormalOn" : "NormalOff";
            AnimatedIcon.SetState(icon, expanded ? "NormalOffToNormalOn_End" : "NormalOnToNormalOff_End");
            icon.InvalidateArrange();
            icon.UpdateLayout();
        }

        // ───────────── Expander interaction ─────────────

        private bool IsInitializedPageOf(DependencyObject element)
        {
            FrameworkElement? page = _currentPageTag is not null && _pages.TryGetValue(_currentPageTag, out var p) ? p : null;
            return page is not null && _initializedPages.Contains(page) && IsDescendantOf(element, page);
        }

        private void Expander_Expanded(object? sender, EventArgs e)
        {
            if (sender is not SettingsExpander expander || !IsInitializedPageOf(expander))
                return;   // the initial layout (restoring state) is not a user action
            SaveExpanderState(expander);
            // After expanding, scroll the whole expander into view (the content arrives in this layout pass, so queue it after layout).
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                expander.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true }));
        }

        private void Expander_Collapsed(object? sender, EventArgs e)
        {
            if (sender is not SettingsExpander expander || !IsInitializedPageOf(expander))
                return;
            // WinUI's default CollapseUp/CollapseDown translates the content for about 167 ms before it actually collapses,
            // so the cards below move up a beat late. Skip the inner Expander's collapse Storyboard to its end: the content
            // disappears this frame and the cards below slide in sync via the already-attached implicit animation.
            CardReflow.CompleteCollapseImmediately(expander);
            SaveExpanderState(expander);
        }

        private void SaveExpanderState(SettingsExpander expander)
        {
            if (NavShell.GetRememberExpanded(expander) && ExpanderKey(expander) is string key)
            {
                UiHost.SetSetting(key, expander.IsExpanded);
                UiHost.FlushSettings();
            }
        }

        private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
                if (ReferenceEquals(current, ancestor))
                    return true;
            return false;
        }

        private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
        {
            for (DependencyObject? current = VisualTreeHelper.GetParent(element); current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is T match)
                    return match;
            return null;
        }
    }
}
