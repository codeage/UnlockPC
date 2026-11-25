using Microsoft.UI.Windowing;
using System;
using Windows.Graphics;

namespace WinUIKit
{
    /// <summary>Placement of owned windows: shared by all owned windows (dialogs, settings-style windows) so they open by the same position rules.</summary>
    internal static class WindowPlacement
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        /// <summary>
        /// Centers <paramref name="window"/> over <paramref name="owner"/> (both in physical pixels).
        /// When the owner is near a screen edge, the result is clamped to the work area of the owner's monitor so the whole window stays visible.
        /// A minimized owner sits at (-32000, -32000); centering on it and clamping would pin the window to the top-left corner of the work area,
        /// so in that case center on the work area of the owner's monitor (the one it restores to) instead.
        /// The size of <paramref name="window"/> should be set before calling.
        /// </summary>
        internal static void CenterOver(AppWindow window, AppWindow owner)
        {
            // Owner hidden (e.g. an app whose main window stays hidden in the tray): its position is meaningless, so center on the work area of the monitor under the cursor (the screen the user is looking at)
            bool ownerHidden = !owner.IsVisible;
            DisplayArea displayArea = ownerHidden && GetCursorPos(out POINT cursor)
                ? DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary)
                : DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Nearest);
            RectInt32 workArea = displayArea.WorkArea;
            SizeInt32 size = window.Size;
            int x, y;
            if (ownerHidden || owner.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            {
                x = workArea.X + (workArea.Width - size.Width) / 2;
                y = workArea.Y + (workArea.Height - size.Height) / 2;
            }
            else
            {
                PointInt32 ownerPosition = owner.Position;
                SizeInt32 ownerSize = owner.Size;
                x = ownerPosition.X + (ownerSize.Width - size.Width) / 2;
                y = ownerPosition.Y + (ownerSize.Height - size.Height) / 2;
            }

            int maxX = workArea.X + Math.Max(0, workArea.Width - size.Width);
            int maxY = workArea.Y + Math.Max(0, workArea.Height - size.Height);
            x = Math.Clamp(x, workArea.X, maxX);
            y = Math.Clamp(y, workArea.Y, maxY);
            window.Move(new PointInt32(x, y));
        }
    }
}
