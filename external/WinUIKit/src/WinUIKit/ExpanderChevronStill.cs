using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.CompilerServices;

namespace WinUIKit
{
    /// <summary>
    /// Stops the header chevron (AnimatedIcon) of Expander / SettingsExpander from animating at all: no nudge on hover
    /// or press, and on expand/collapse it switches straight to its final direction without rotating.
    ///
    /// Why: the WinUI chevron is a Lottie animated source (AnimatedChevronUpDownSmall), and the template writes
    /// AnimatedIcon.State = NormalOff / PointerOverOff / PressedOff / …On following the header ToggleButton's visual
    /// state. On every state change it plays the "OldStateToNewState" segment of the source: on hover the chevron dips
    /// down and springs back when the pointer leaves, so it looks like it's "moving"; on click it rotates fully. The
    /// design here calls for a static chevron.
    ///
    /// How: watch the icon's own State. Whenever the template writes a standard state, immediately rewrite it to the
    /// existing standalone "transition end" marker in the source (expanded NormalOffToNormalOn_End, collapsed
    /// NormalOnToNormalOff_End). AnimatedIcon defers consuming State until the next LayoutUpdated, so a later write in
    /// the same frame wins; and for a standalone marker it jumps straight to that position without playing — leaving
    /// just two static images, "up" and "down". The expand animation of the content itself and the reflow animation of
    /// the cards below are unaffected.
    ///
    /// If the template's state names change in the future (no longer ending in On/Off), they pass through unchanged and
    /// it degrades to the system default animation without errors.
    /// </summary>
    internal static class ExpanderChevronStill
    {
        internal const string ExpandedEndpoint = "NormalOffToNormalOn_End";
        internal const string CollapsedEndpoint = "NormalOnToNormalOff_End";

        private static readonly ConditionalWeakTable<AnimatedIcon, object> Attached = new();

        /// <summary>Calling it repeatedly for the same icon has no side effects.</summary>
        internal static void Attach(AnimatedIcon icon)
        {
            if (icon is null || Attached.TryGetValue(icon, out _))
                return;
            Attached.Add(icon, new object());
            icon.RegisterPropertyChangedCallback(AnimatedIcon.StateProperty, OnStateChanged);
        }

        private static void OnStateChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (sender is not AnimatedIcon icon)
                return;

            string state = AnimatedIcon.GetState(icon) ?? string.Empty;
            string? endpoint =
                state.EndsWith("On", StringComparison.Ordinal) ? ExpandedEndpoint :
                state.EndsWith("Off", StringComparison.Ordinal) ? CollapsedEndpoint :
                null;   // already an end marker (written by this method) or an unknown state name: let it through
            if (endpoint is not null)
                AnimatedIcon.SetState(icon, endpoint);
        }
    }
}
