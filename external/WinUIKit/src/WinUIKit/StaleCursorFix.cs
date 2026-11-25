using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUIKit;

/// <summary>
/// Works around WinUI bug microsoft/microsoft-ui-xaml#8829 (a regression since WinAppSDK 1.4, still present in 2.5.1,
/// issue still open): when a ComboBox drop-down (or a MenuFlyout / context menu / submenu) opens, the cursor randomly
/// turns into the "busy" spinner or a resize arrow until the mouse moves. The arrow's direction depends on the window
/// edge the mouse last dragged (top/bottom edge → up-down arrow).
/// Root cause (per the analysis in the issue): when hit-testing the cursor for the new popup, the input state manager
/// takes a legacy lookup path, doesn't find WinUI's newer input layer, clears the cursor ownership, and the cursor stays
/// at its previous shape.
/// <para>
/// Approach (the community workaround from the issue): when the drop-down opens, when the pointer enters the combo box,
/// when the menu presenter is loaded into the visual tree, and when the pointer enters the menu, force the arrow with
/// Win32 SetCursor; the popup only really appears a little later, so it is set once more at low priority.
/// </para>
/// <para>
/// Hookup (in the app's App.xaml, app-wide): add <c>&lt;Setter Property="wk:StaleCursorFix.IsEnabled" Value="True" /&gt;</c>
/// (<c>xmlns:wk="using:WinUIKit"</c>) to an implicit ComboBox style (BasedOn DefaultComboBoxStyle) and an implicit
/// MenuFlyoutPresenter style (BasedOn DefaultMenuFlyoutPresenterStyle). Submenus are MenuFlyoutPresenters too, so they are
/// covered as well. Places that set their own MenuFlyoutPresenterStyle (or ComboBox Style) bypass the implicit style and
/// must add the same Setter to their own style.
/// </para>
/// </summary>
public static class StaleCursorFix
{
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] private static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr hCursor);
    private static readonly IntPtr IdcArrow = 32512;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(StaleCursorFix), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>For A/B testing: when true, the fix stays attached but doesn't set the cursor (the app sets it at startup from its own diagnostic switch), to confirm the symptom really is removed by this workaround.</summary>
    public static bool Disabled { get; set; }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || e.OldValue is true) return;
        switch (d)
        {
            case ComboBox combo:
                combo.DropDownOpened += (_, _) => ResetToArrow(combo.DispatcherQueue);
                combo.PointerEntered += (_, _) => { if (!combo.IsDropDownOpen) ResetToArrow(combo.DispatcherQueue); };
                break;
            case MenuFlyoutPresenter menu:
                // Set once when the menu pops up (presenter loaded into the visual tree) and once when the pointer enters it; the presenter is reloaded on every open.
                menu.Loaded += (_, _) => ResetToArrow(menu.DispatcherQueue);
                menu.PointerEntered += (_, _) => ResetToArrow(menu.DispatcherQueue);
                break;
        }
    }

    /// <summary>Resets to the arrow immediately, and once more (at low priority) after the popup appears.</summary>
    public static void ResetToArrow(DispatcherQueue? dispatcher)
    {
        if (Disabled) return;
        Apply();
        dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, Apply);
    }

    private static void Apply() => SetCursor(LoadCursor(IntPtr.Zero, IdcArrow));
}
