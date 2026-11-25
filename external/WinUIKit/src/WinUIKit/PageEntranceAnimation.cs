using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace WinUIKit
{
    /// <summary>
    /// "Slide in from below" entrance animation for navigation page switches, shared by all modal navigation windows —
    /// duration, curve, and distance are defined only here, so every window calling this method is consistent by
    /// construction, instead of one using a system transition and another hand-tuning a lookalike that doesn't match.
    ///
    /// Translation only (Translation is independent of Offset / Opacity): it doesn't conflict with the SettingsExpander
    /// first-frame opacity handling or with the card reflow's implicit Offset animation; there's no fade-in precisely
    /// because a fade would fight the Expander first-frame handling on such pages.
    /// Trigger it only for user-initiated page switches; don't call it on the window's first frame (which stays static).
    /// </summary>
    internal static class PageEntranceAnimation
    {
        // Same Fluent deceleration curve as the card reflow; 48 DIP distance, 300 ms duration.
        private const float SlideFromDip = 48f;
        private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);

        internal static void Play(FrameworkElement page)
        {
            if (page is null)
                return;

            ElementCompositionPreview.SetIsTranslationEnabled(page, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(page);
            Compositor compositor = visual.Compositor;

            CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.1f, 0.9f),
                new Vector2(0.2f, 1.0f));

            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0.0f, new Vector3(0f, SlideFromDip, 0f));
            slide.InsertKeyFrame(1.0f, Vector3.Zero, easing);
            slide.Duration = Duration;
            visual.StartAnimation("Translation", slide);
        }
    }
}
