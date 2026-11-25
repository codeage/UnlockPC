using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace WinUIKit
{
    /// <summary>
    /// Light/dark colors for the system title bar (AppWindow.TitleBar). Shared by all windows so the colors can't diverge between
    /// separate copies. Backgrounds are always fully transparent (A=0) so the XAML content extended into the title bar paints its own background.
    /// The App prefix avoids a clash with Microsoft.UI.Windowing.TitleBarTheme (the same name would be ambiguous).
    /// <para>
    /// [Foreground colors must be opaque] Per the official docs, only Button*BackgroundColor supports alpha; every other color property ignores alpha.
    /// Inactive foregrounds used to be #5C000000 / #66FFFFFF; with alpha dropped they became pure black / pure white,
    /// nearly identical to the active state — the caption buttons didn't gray out when inactive. Now the intended translucency is pre-blended onto the
    /// window background (light #F3F3F3 / dark #202020, matching ThemedWindow's erase background) to get the equivalent opaque color.
    /// </para>
    /// </summary>
    internal static class AppTitleBarTheme
    {
        // Light: #E4000000 over #F3F3F3 ≈ #1A1A1A; #5C000000 over #F3F3F3 ≈ #9B9B9B (Win11 inactive gray).
        private static readonly Windows.UI.Color LightActiveFg = Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);
        private static readonly Windows.UI.Color LightInactiveFg = Windows.UI.Color.FromArgb(0xFF, 0x9B, 0x9B, 0x9B);
        // Dark: #66FFFFFF over #202020 ≈ #797979.
        private static readonly Windows.UI.Color DarkActiveFg = Windows.UI.Color.FromArgb(0xFF, 0xE5, 0xED, 0xF6);
        private static readonly Windows.UI.Color DarkInactiveFg = Windows.UI.Color.FromArgb(0xFF, 0x79, 0x79, 0x79);

        public static void Apply(AppWindow appWindow, ElementTheme theme)
        {
            var titleBar = appWindow?.TitleBar;
            if (titleBar is null) return;

            if (theme == ElementTheme.Light)
            {
                titleBar.ForegroundColor = LightActiveFg;
                titleBar.BackgroundColor = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
                titleBar.InactiveForegroundColor = LightInactiveFg;
                titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
                titleBar.ButtonForegroundColor = LightActiveFg;
                titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
                titleBar.ButtonHoverForegroundColor = LightActiveFg;
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
                titleBar.ButtonPressedForegroundColor = LightActiveFg;
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x18, 0x00, 0x00, 0x00);
                titleBar.ButtonInactiveForegroundColor = LightInactiveFg;
                titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
            }
            else
            {
                titleBar.ForegroundColor = DarkActiveFg;
                titleBar.BackgroundColor = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
                titleBar.InactiveForegroundColor = DarkInactiveFg;
                titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
                titleBar.ButtonForegroundColor = DarkActiveFg;
                titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
                titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
                titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
                titleBar.ButtonInactiveForegroundColor = DarkInactiveFg;
                titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00);
            }
        }
    }
}
