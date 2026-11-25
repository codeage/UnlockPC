# WinUIKit

Window infrastructure for WinUI 3 desktop apps: a themed base window with a custom-drawn caption,
modal dialog windows that replace `ContentDialog`, and a settings-style modal navigation window, together with
workarounds for a number of WinUI 3 / Windows App SDK bugs.

- **`ThemedWindow`**: base window. It follows the app theme, draws its own caption buttons (keeping the Windows 11
  snap layout flyout on Maximize), dims the caption when inactive, and handles owned modal windows: it disables the
  owner, hands activation back on close, and forwards activation from a disabled owner to its modal window.
- **`DialogWindow`**: a dialog in its own owned modal window instead of a `ContentDialog` overlay, so dialogs never
  queue (a new one is always on top) and work from any window. `DialogText` lays out dialog body text and
  `SegmentFlowPanel` wraps between segments, never inside one.
- **`ModalNavWindow`**: a "Windows Settings"-style modal window with a `NavigationView` and pages (`NavShell`,
  `NavShellResources.xaml`). It remembers its size, the last page and expander states, and it includes
  `ComboBoxSizing`, `CardReflow`, `ExpanderChevronStill` and `PageEntranceAnimation`.
- **Pooled modal windows**: `DialogWindow` and `ModalNavWindow` are content (`UserControl`), not windows. Each one
  borrows a hidden native window from a pool while it is shown and gives it back when it closes. The Windows App SDK
  leaves about 30 GDI objects behind for every destroyed window whose content is extended into the title bar, so
  creating a window per dialog eventually exhausts the per-process GDI limit; a reused window does not leak.
  Use `WindowHandle`, `AppWindow` and `WindowContent` where a `Window` was used before; an owner may be a `Window`
  or a shown `DialogWindow` / `ModalNavWindow`. Idle windows are closed with the window they were last shown over.
- **`WindowPlacement`**, **`AppTitleBarTheme`** and the caption button / title bar resource dictionaries.
- **Cursor workarounds**: `StaleCursorFix` (microsoft-ui-xaml#8829) and `ContentCursorFallback`.
- **`UiHost`**: the only link to the host app. It supplies the app name, theme, language, logging and the store
  for remembered window state.

UI text of the library itself (caption button names) ships in English and Chinese.

## Requirements

- .NET 10, Windows App SDK 2.5.1, Windows 10 1809 (build 17763) or later.
- Unpackaged apps. Native AOT and trimming compatible (`IsAotCompatible`); bindings use `x:Bind` only.

## Getting started

1. Reference `src\WinUIKit\WinUIKit.csproj` from your app.
2. Declare Common Controls v6 in `app.manifest`. The caption button tooltips use the Win32 tooltip control.
3. At startup, before creating the first window:

   ```csharp
   WinUIKit.UiHost.AppName = "My App";
   WinUIKit.UiHost.ResolveTheme = () => settings.Theme;        // ElementTheme.Default / Light / Dark
   WinUIKit.UiHost.Language = () => settings.Language;         // "en-US", "zh-CN", ...
   WinUIKit.UiHost.Log = message => logger.Error(message);
   WinUIKit.UiHost.Settings = WinUIKit.UiSettingsStores.JsonFile(
       Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyApp", "window-state.json"));
   ```

   When the theme, accent color or language changes, call `UiHost.NotifyThemeChanged()`,
   `NotifyAccentChanged()` or `NotifyLanguageChanged()`.

   Optional hooks: `TraceLog` (diagnostic messages, defaults to `Log`), `RefreshAccentResources` (apps with a custom
   accent color), and `RequestForeground` (called when a dialog could not take the foreground because the app was
   in the background; return `true` if you obtained it some other way).
4. In `App.xaml`, add the cursor fix to the implicit `ComboBox` and `MenuFlyoutPresenter` styles
   (`xmlns:wk="using:WinUIKit"`):

   ```xml
   <Setter Property="wk:StaleCursorFix.IsEnabled" Value="True" />
   ```

   For a main window that does not derive from `ThemedWindow`, call `ContentCursorFallback.Attach(window)` after
   creating it. If the window replaces its own window procedure, attach before that.
5. With `dotnet publish`, copy the XAML resources (`.xbf`) of referenced projects into the publish folder.
   `dotnet publish` does not do this by itself, and `DialogWindowShell` then fails to load with a
   `XamlParseException`.

## Remembered window state

Resizable `ModalNavWindow`s remember their size, and every `ModalNavWindow` remembers its last page and its named
expanders. The library never creates a file on its own. It reads and writes through `UiHost.Settings`, and when that
is `null` (the default), nothing is remembered.

| Store | Use it when |
| --- | --- |
| `UiSettingsStores.JsonFile(path)` | You want it to just work. The state goes into a JSON file at a path you choose. Saves are atomic and keep a `.bak` copy; if the file is damaged, the backup is used, and if both are damaged, the store starts empty. |
| `UiSettingsStores.FromDelegates(read, write, save)` | Your app already has a settings system and you want the state in the same file. `save` is optional. |
| `UiSettingsStores.InMemory()` | Tests. |
| your own `IUiSettingsStore` | Anything else. |

Store contract: every call happens on the UI thread. A store may throw; the library logs the error, uses the default
value or drops the write, and the window keeps working. `Write` only has to record the value: the library calls
`Save` after each batch of writes. A store that persists on every write can leave `Save` empty.

Keys and values (`<prefix>` is the `keyPrefix` your window passes to `InitializeShell`). Choose prefixes that do not
collide with your app's own keys if both share one store.

| Key | Value |
| --- | --- |
| `<prefix>.WidthDip`, `<prefix>.HeightDip` | Window size in DIPs, invariant culture, up to three decimals (`960`, `1200.5`) |
| `<prefix>.LastPage` | `Tag` of the selected navigation item |
| `<prefix>.Expander.<Name>` | `true` / `false` (reading also accepts `True` / `False`) |

When values are read back, invalid ones are replaced: a size that is not a positive finite number falls back to the
default, a size is kept between the window minimum and 32768 DIPs, and the window is fitted to the work area. A page
that no longer exists, or that the window does not allow at the moment, falls back to the first allowed page.
These formats are stable across versions.

## Build switches

| Switch | Effect |
| --- | --- |
| `-p:ShippingBuild=true` (global property) | Release packaging: defines `SHIPPING`, which removes self-test hooks and A/B switches, drops trace logging (`UI_TRACE` is not defined, so `UiHost.WriteTrace` calls and their text are compiled out), and grants no `InternalsVisibleTo`. |
| `WinUIKitFriendAssemblies` | Semicolon-separated assembly names that may see internals (for example an app's own UI self-test) outside `ShippingBuild`. Set it in an untracked `Directory.Build.local.props` at the repository root:<br>`<Project><PropertyGroup><WinUIKitFriendAssemblies>MyApp;MyApp.UiTests</WinUIKitFriendAssemblies></PropertyGroup></Project>` |
| `WINUIKIT_NO_OWNER_REDIRECT=1` (environment variable) | Development builds only: turns off forwarding activation from a disabled owner to its modal window, for A/B comparison. |
| `StaleCursorFix.Disabled`, `ContentCursorFallback.Disabled` | Turn the cursor workarounds off at runtime for A/B comparison. |

## Native AOT notes

- Window and subclass procedures use `[UnmanagedCallersOnly]` function pointers instead of delegates. Native AOT
  keeps the type name of every instantiated delegate and closure class, and function pointers avoid that.
- Type and property names used by XAML stay in the binary, because the XAML runtime looks them up by name.
- Log prefixes (`[Window]`, `[Dialog]`, `[Cursor]`, `[Settings]`, `[ModalNavWindow]`) do not contain class names.

## Tests

```
dotnet test tests\WinUIKit.Tests
```

The tests cover UI text (same keys and placeholders in both languages, every key used in code exists, no unused
keys, the embedded copy is stripped), the settings stores, and the sanitizing rules for remembered state.
They only exercise code that needs no UI runtime.

## Troubleshooting

**WMC9999 (XAML compiler crash) during `dotnet build`.** This is almost always a hidden C# compile error. The second
XAML pass needs the project assembly from the first pass; when C# fails, that assembly is missing, so the compiler
reports WMC1509 ("No LocalAssembly parameter given") and then crashes with WMC9999. Look for the first `CS` error
above it. This includes nullable warnings, because `TreatWarningsAsErrors` is on.

## WinUI issues worked around

| Issue | Symptom | Workaround |
| --- | --- | --- |
| microsoft-ui-xaml#8829 | The cursor stays stuck as a busy or resize cursor over a `ComboBox` dropdown or menu | `StaleCursorFix` sets the arrow cursor again |
| microsoft-ui-xaml#10383 | The system caption buttons keep their hover color after the window deactivates, and their tooltip stays on screen after the window is closed with the keyboard | `ThemedWindow` draws its own caption buttons |
| microsoft-ui-xaml#10379 | Non-client regions registered with `InputNonClientPointerSource` are silently cleared | Re-registered on `RegionsChanged` |
| microsoft-ui-xaml#6327 | A WinUI `ToolTip` catches the mouse and covers the resize border | Caption tooltips use the Win32 tooltip control in tracking mode |

microsoft-ui-xaml#10758 is a WinUI bug: while a modal window is open, pointer input on its owner is replayed into
controls that capture the pointer in the modal window, so sliders jump. It cannot be fully worked around at the
application level and is not fixed in this library.
