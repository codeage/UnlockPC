using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WinUIKit
{
    /// <summary>
    /// Uniform combo box sizing and behavior (one implementation for the whole app; ModalNavWindow applies it automatically to all its pages).
    /// </summary>
    public static class ComboBoxSizing
    {
        private const double ChromeWidthDip = 64;   // Left padding 12 + drop-down arrow area 32 + slack
        private const double WidthGridDip = 8;
        private const double MinimumWidthDip = 128;
        private const double MaximumWidthDip = 360;

        /// <summary>
        /// Extra reserved width (DIP). Set 22 on combo boxes whose items can carry a warning icon, so the width doesn't jump and the icon isn't clipped when the marker appears or disappears.
        /// </summary>
        public static readonly DependencyProperty ExtraWidthProperty = DependencyProperty.RegisterAttached(
            "ExtraWidth", typeof(double), typeof(ComboBoxSizing), new PropertyMetadata(0d));

        public static double GetExtraWidth(DependencyObject element) => (double)element.GetValue(ExtraWidthProperty);
        public static void SetExtraWidth(DependencyObject element, double value) => element.SetValue(ExtraWidthProperty, value);

        /// <summary>
        /// Maximum width of this combo box (DIP); 0 = the uniform limit of 360. Widen only combo boxes whose items are inherently long and lose
        /// their meaning when trimmed (e.g. a full hardware device name plus a short annotation does not fit in 360).
        /// </summary>
        public static readonly DependencyProperty MaxWidthProperty = DependencyProperty.RegisterAttached(
            "MaxWidth", typeof(double), typeof(ComboBoxSizing), new PropertyMetadata(0d));

        public static double GetMaxWidth(DependencyObject element) => (double)element.GetValue(MaxWidthProperty);
        public static void SetMaxWidth(DependencyObject element, double value) => element.SetValue(MaxWidthProperty, value);

        /// <summary>
        /// [Size to the widest item from the start] On first measure a ComboBox sizes only to the selected item, and widens to the widest item only after
        /// the first drop-down, once all list items are generated; when right-aligned it can only grow to the left, while the list position was computed
        /// before widening — the open list looks shifted to the right (measured 57 DIP at 150% scaling). Sizing to the widest item up front keeps the width
        /// the same before and after opening.
        /// Rule: measure the longest item in the combo box's own font + arrow area + <see cref="ExtraWidthProperty"/>, round up to an 8 DIP grid, clamp to 128–360.
        /// Call again after the items are rebuilt.
        /// </summary>
        public static void SizeToLongestItem(ComboBox comboBox, double minWidth = MinimumWidthDip)
        {
            double longest = 0;
            foreach (object item in comboBox.Items)
            {
                object? content = item is ComboBoxItem comboBoxItem ? comboBoxItem.Content : item;
                string text = content switch
                {
                    TextBlock textBlock => textBlock.Text,
                    string value => value,
                    null => string.Empty,
                    _ => content.ToString() ?? string.Empty,   // Data items provide their display text via ToString
                };
                if (text.Length == 0)
                    continue;

                var probe = new TextBlock
                {
                    Text = text,
                    FontFamily = comboBox.FontFamily,
                    FontSize = comboBox.FontSize,
                    FontStretch = comboBox.FontStretch,
                    FontStyle = comboBox.FontStyle,
                    FontWeight = comboBox.FontWeight,
                };
                probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                longest = Math.Max(longest, probe.DesiredSize.Width);
            }

            double width = Math.Ceiling((longest + ChromeWidthDip + GetExtraWidth(comboBox)) / WidthGridDip) * WidthGridDip;
            double max = GetMaxWidth(comboBox) > 0 ? GetMaxWidth(comboBox) : MaximumWidthDip;
            comboBox.Width = Math.Clamp(width, Math.Min(minWidth, max), max);
        }

        /// <summary>
        /// Attaches two behaviors (idempotent):
        /// (1) While closed, swallow the mouse wheel so hovering doesn't accidentally change the selection; the scroll is forwarded to the page's ScrollViewer.
        ///    Let it through while the drop-down is open.
        ///    When a ComboBox is focused and closed, the selection changes in its [class handler], which runs before instance handlers, so setting Handled
        ///    afterwards can't stop it. The interception therefore hooks the ComboBox's [template root]: the bubbling event passes the template root first and
        ///    is marked Handled before it reaches the ComboBox. The template is only built after Loaded.
        /// (2) Explicitly go to the Normal visual state on Loaded: when a page is first shown, combo boxes below the initial viewport don't get the
        ///    initial Normal state and render without border or background until a hover or drop-down fixes it.
        /// </summary>
        public static void AttachBehaviors(ComboBox comboBox)
        {
            comboBox.Loaded -= ComboBox_Loaded;
            comboBox.Loaded += ComboBox_Loaded;
            if (comboBox.IsLoaded)
                ComboBox_Loaded(comboBox, null!);
        }

        private static void ComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox combo)
                return;
            VisualStateManager.GoToState(combo, "Normal", useTransitions: false);
            if (VisualTreeHelper.GetChildrenCount(combo) > 0 && VisualTreeHelper.GetChild(combo, 0) is UIElement templateRoot)
            {
                templateRoot.PointerWheelChanged -= TemplateRoot_PointerWheelChanged;
                templateRoot.PointerWheelChanged += TemplateRoot_PointerWheelChanged;
            }
        }

        private static void TemplateRoot_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            ComboBox? combo = FindAncestor<ComboBox>(sender as DependencyObject, includeSelf: true);
            if (combo is null || combo.IsDropDownOpen)
                return;   // Open: let it through, the wheel scrolls the items as usual

            e.Handled = true;
            ScrollViewer? page = FindAncestor<ScrollViewer>(combo, includeSelf: false);
            if (page is null)
                return;
            int delta = e.GetCurrentPoint(combo).Properties.MouseWheelDelta;   // Positive = up
            page.ChangeView(null, page.VerticalOffset - delta, null, disableAnimation: true);
        }

        private static T? FindAncestor<T>(DependencyObject? start, bool includeSelf) where T : DependencyObject
        {
            DependencyObject? current = includeSelf ? start : (start is null ? null : VisualTreeHelper.GetParent(start));
            while (current is not null)
            {
                if (current is T match)
                    return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
