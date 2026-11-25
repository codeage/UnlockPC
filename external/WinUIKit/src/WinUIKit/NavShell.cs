using System.Collections.Generic;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUIKit
{
    /// <summary>
    /// Declarative attached properties for pages of a modal navigation window (ModalNavWindow). Pages only declare
    /// "what" something is; ModalNavWindow implements the behavior uniformly.
    /// </summary>
    public static class NavShell
    {
        /// <summary>
        /// Set on a vertical card container (StackPanel): its direct children move smoothly when layout shifts them (cards
        /// below slide along when a card appears/disappears or an expander expands/collapses).
        /// Set it on every page's root card stack, and on nested containers whose children are shown/hidden as a group.
        /// </summary>
        public static readonly DependencyProperty ReflowProperty = DependencyProperty.RegisterAttached(
            "Reflow", typeof(bool), typeof(NavShell), new PropertyMetadata(false));

        public static bool GetReflow(DependencyObject element) => (bool)element.GetValue(ReflowProperty);
        public static void SetReflow(DependencyObject element, bool value) => element.SetValue(ReflowProperty, value);

        /// <summary>
        /// Set on a SettingsExpander (it must have an x:Name): remembers the user's last expanded/collapsed state and
        /// restores it the next time the window opens. Don't set it on expanders whose state is bound to a setting or
        /// that must always be open.
        /// </summary>
        public static readonly DependencyProperty RememberExpandedProperty = DependencyProperty.RegisterAttached(
            "RememberExpanded", typeof(bool), typeof(NavShell), new PropertyMetadata(false));

        public static bool GetRememberExpanded(DependencyObject element) => (bool)element.GetValue(RememberExpandedProperty);
        public static void SetRememberExpanded(DependencyObject element, bool value) => element.SetValue(RememberExpandedProperty, value);

        /// <summary>
        /// Walks the "content" relationships (independent of whether control templates have been applied): panel children,
        /// Border.Child, ContentControl.Content (including ScrollViewer, Page/UserControl, SettingsCard), and a
        /// SettingsExpander's Content and Items. Finds controls even before the page has entered the visual tree.
        /// </summary>
        internal static IEnumerable<DependencyObject> ContentDescendants(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DependencyObject node = stack.Pop();
                yield return node;
                switch (node)
                {
                    case Panel panel:
                        foreach (UIElement child in panel.Children) stack.Push(child);
                        break;
                    case Border border when border.Child is not null:
                        stack.Push(border.Child);
                        break;
                    case SettingsExpander expander:
                        if (expander.Content is DependencyObject header) stack.Push(header);
                        if (expander.Items is not null)
                            foreach (object item in expander.Items)
                                if (item is DependencyObject d) stack.Push(d);
                        break;
                    case UserControl userControl when userControl.Content is not null:
                        stack.Push(userControl.Content);
                        break;
                    case ContentControl contentControl when contentControl.Content is DependencyObject content:
                        stack.Push(content);
                        break;
                    case Viewbox viewbox when viewbox.Child is not null:
                        stack.Push(viewbox.Child);
                        break;
                }
            }
        }
    }
}
