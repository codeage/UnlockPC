namespace UnlockPC;

internal static class AppDataPaths
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnlockPC");

    internal static readonly string WatchRules = Path.Combine(Root, "watch-rules.json");
    internal static readonly string WindowPlacement = Path.Combine(Root, "window-placement.json");
    internal static readonly string DebugPageUnlocked = Path.Combine(Root, "debug-page-unlocked");
    internal static readonly string WatchAuthorization = Path.Combine(Root, "automatic-unlock.dpapi");
    internal static readonly string Logs = Path.Combine(Root, "Logs");
}
