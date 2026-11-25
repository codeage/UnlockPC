using Microsoft.UI.Xaml;

namespace WinUIKit;

/// <summary>
/// The library's link to the host application. Fill it in at startup, before the first window is created; every
/// window and dialog in the library takes the app name, theme, language and logging from here. The library has no
/// settings, localization or logging system of its own.
/// <para>
/// Required: <see cref="AppName"/>, <see cref="ResolveTheme"/>, <see cref="Language"/>; recommended: <see cref="Log"/>.
/// When the theme, accent color or UI language changes, call <see cref="NotifyThemeChanged"/>,
/// <see cref="NotifyAccentChanged"/> or <see cref="NotifyLanguageChanged"/>.
/// To have windows remember their size, last page and expander states, set <see cref="Settings"/>.
/// Everything here is used on the UI thread.
/// </para>
/// </summary>
public static class UiHost
{
    /// <summary>Application name (window titles read "Title - AppName").</summary>
    public static string AppName { get; set; } = "";

    /// <summary>The theme windows should use right now (the app's "System / Light / Dark" setting).</summary>
    public static Func<ElementTheme> ResolveTheme { get; set; } = static () => ElementTheme.Default;

    /// <summary>
    /// Theme for a dialog or modal window shown over <paramref name="owner"/>: the owner's actual theme, so the dialog
    /// matches the window it belongs to (an app can give different windows different themes, e.g. one follows the
    /// Windows app theme and another the taskbar theme). Falls back to <see cref="ResolveTheme"/> when the owner has no resolved theme.
    /// </summary>
    internal static ElementTheme ThemeFor(Window? owner)
    {
        if (owner?.Content is FrameworkElement { ActualTheme: ElementTheme.Light or ElementTheme.Dark } root)
            return root.ActualTheme;
        return ResolveTheme();
    }

    /// <summary>Current UI language, e.g. "zh-CN" or "en-US". Anything starting with "zh" shows Chinese, everything else English.</summary>
    public static Func<string> Language { get; set; } = static () => "en-US";

    /// <summary>Error log sink (<see cref="WriteLog"/>): real errors only; also written in release builds.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Trace log sink (<see cref="WriteTrace"/>): status and diagnostic messages that are not errors. Falls back to
    /// <see cref="Log"/> when not set. In builds without the <c>UI_TRACE</c> symbol the trace calls are compiled out.
    /// </summary>
    public static Action<string>? TraceLog { get; set; }

    /// <summary>
    /// Re-applies accent-dependent theme resources to an element tree after the accent color changed.
    /// Only needed by apps with a custom accent color.
    /// </summary>
    public static Action<FrameworkElement>? RefreshAccentResources { get; set; }

    /// <summary>
    /// Called with the window handle when a dialog was shown but this process could not take the foreground
    /// (Windows' foreground lock refuses it while the app is in the background, e.g. a notification raised by an
    /// app that lives in the tray). Return whether the foreground was obtained. If not set, the dialog still shows,
    /// possibly behind other windows with a flashing taskbar button.
    /// </summary>
    public static Func<IntPtr, bool>? RequestForeground { get; set; }

    // ------------------------------------------------------------------
    // Settings: window state the library remembers. The library never creates a settings file by itself;
    // the application chooses the store. Without one nothing is remembered and windows open with their defaults.
    // ------------------------------------------------------------------

    /// <summary>
    /// Where windows keep their remembered state (size, last page, expander states). <c>null</c> (the default)
    /// means nothing is remembered. See <see cref="IUiSettingsStore"/> and <see cref="UiSettingsStores"/>.
    /// </summary>
    public static IUiSettingsStore? Settings { get; set; }

    // Failures of the application's store (unreadable file, disk full, file locked) are runtime errors: log them,
    // read back the default, drop the write. A window must never fail to open or close because it cannot
    // remember its size.

    internal static string GetSetting(string key, string defaultValue = "") => ReadHostSetting(key) ?? defaultValue;

    /// <summary>Accepts "true"/"false" in any letter case (including the "True"/"False" of <see cref="bool.ToString()"/>).</summary>
    internal static bool GetSetting(string key, bool defaultValue) =>
        bool.TryParse(ReadHostSetting(key), out bool value) ? value : defaultValue;

    internal static void SetSetting(string key, string value)
    {
        try { Settings?.Write(key, value); }
        catch (Exception ex) { WriteLog($"[Settings] Failed to write window state: {ex.GetType().Name}: {ex.Message}"); }
    }

    internal static void SetSetting(string key, bool value) => SetSetting(key, value ? "true" : "false");

    internal static void FlushSettings()
    {
        try { Settings?.Save(); }
        catch (Exception ex) { WriteLog($"[Settings] Failed to save window state: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static string? ReadHostSetting(string key)
    {
        try { return Settings?.Read(key); }
        catch (Exception ex)
        {
            WriteLog($"[Settings] Failed to read window state, using the default: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The theme setting changed (windows re-apply their theme).</summary>
    public static event Action? ThemeChanged;

    /// <summary>The accent color changed.</summary>
    public static event Action? AccentChanged;

    /// <summary>The UI language changed (all library text is refreshed).</summary>
    public static event Action? LanguageChanged;

    public static void NotifyThemeChanged() => ThemeChanged?.Invoke();

    public static void NotifyAccentChanged() => AccentChanged?.Invoke();

    public static void NotifyLanguageChanged()
    {
        RefreshTracked();
        LanguageChanged?.Invoke();
    }

    /// <summary>Whether the current UI language is Chinese.</summary>
    public static bool IsChinese => Language().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes to the error log (dropped when <see cref="Log"/> is not set; never throws). Real errors only;
    /// use <see cref="WriteTrace"/> for status and diagnostics.
    /// </summary>
    public static void WriteLog(string message)
    {
        try { Log?.Invoke(message); }
        catch { }
    }

    /// <summary>
    /// Writes to the trace log (status, diagnostics, A/B switches). Calls are compiled out, message text included,
    /// in projects that do not define <c>UI_TRACE</c>; the library defines it unless built with
    /// <c>-p:ShippingBuild=true</c>.
    /// </summary>
    [System.Diagnostics.Conditional("UI_TRACE")]
    public static void WriteTrace(string message)
    {
        try { (TraceLog ?? Log)?.Invoke(message); }
        catch { }
    }

    // ------------------------------------------------------------------
    // Elements created in code: set a text and reset it whenever the language changes
    // (weak references; entries of collected elements are dropped).
    // ------------------------------------------------------------------

    private static readonly List<(WeakReference<DependencyObject> Target, DependencyProperty Property, Func<string> Text)> Tracked = new();

    internal static T Track<T>(T target, DependencyProperty property, Func<string> text) where T : DependencyObject
    {
        target.SetValue(property, text());
        Tracked.Add((new WeakReference<DependencyObject>(target), property, text));
        return target;
    }

    private static void RefreshTracked()
    {
        Tracked.RemoveAll(t => !t.Target.TryGetTarget(out _));
        foreach ((WeakReference<DependencyObject> target, DependencyProperty property, Func<string> text) in Tracked.ToList())
        {
            if (target.TryGetTarget(out DependencyObject? obj))
                obj.SetValue(property, text());
        }
    }
}
