using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace WinUIKit
{
    /// <summary>
    /// Smooth reflow and instant collapse for card stacks (used by ModalNavWindow; the single implementation).
    /// <para>
    /// Reflow: attaches a Composition implicit Offset animation to the direct children of a vertical container (250 ms,
    /// Fluent reflow curve cubic-bezier 0.1,0.9 → 0.2,1.0). Attaching doesn't start an animation; it only runs when a
    /// child's position relative to its parent actually changes — page switches and scrolling don't change Offset, so
    /// there are no side effects.
    /// It must be attached after the page's first static frame and before the first interaction: attaching on the first
    /// Expanded/Collapsed makes that first operation jump instead of animating.
    /// </para>
    /// </summary>
    internal static class CardReflow
    {
        private static readonly ConditionalWeakTable<Panel, object> Armed = new();

        /// <summary>Attaches the reflow animation to the container's current direct children (idempotent).</summary>
        internal static void Arm(Panel panel)
        {
            if (Armed.TryGetValue(panel, out _))
                return;
            Armed.Add(panel, new object());

            Compositor compositor = ElementCompositionPreview.GetElementVisual(panel).Compositor;
            CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1.0f));

            Vector3KeyFrameAnimation offset = compositor.CreateVector3KeyFrameAnimation();
            offset.Target = "Offset";
            offset.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
            offset.Duration = TimeSpan.FromMilliseconds(250);

            ImplicitAnimationCollection implicitAnimations = compositor.CreateImplicitAnimationCollection();
            implicitAnimations["Offset"] = offset;
            foreach (UIElement child in panel.Children)
                ElementCompositionPreview.GetElementVisual(child).ImplicitAnimations = implicitAnimations;
        }

        /// <summary>
        /// Makes the content disappear in the same frame when the user collapses: WinUI's default CollapseUp/CollapseDown
        /// translates the content for about 167 ms before setting it to Collapsed.
        /// Only the collapse Storyboard in the inner Expander's "ExpandStates" group is skipped; the header chevron is left
        /// alone. If the internal structure isn't found, it safely falls back to the system default.
        /// </summary>
        internal static void CompleteCollapseImmediately(DependencyObject settingsExpander)
        {
            Expander? inner = VisualTreeHelperExtensions.FindChildren<Expander>(settingsExpander).FirstOrDefault();
            if (inner is not null)
                CompleteCollapseState(inner);
        }

        private static void CompleteCollapseState(DependencyObject element)
        {
            if (element is FrameworkElement frameworkElement)
            {
                foreach (VisualStateGroup group in VisualStateManager.GetVisualStateGroups(frameworkElement))
                {
                    VisualState? state = group.CurrentState;
                    if (group.Name == "ExpandStates" && state?.Name is "CollapseUp" or "CollapseDown")
                    {
                        try { state.Storyboard?.SkipToFill(); }
                        catch (InvalidOperationException) { /* already at the end */ }
                    }
                }
            }

            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
                CompleteCollapseState(VisualTreeHelper.GetChild(element, i));
        }
    }
}
