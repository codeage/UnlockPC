using System.Text.Json;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace UnlockPC;

internal sealed class MainWindowPlacement
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnlockPC", "window-placement.json");

    private readonly AppWindow _window;
    private Placement _placement;

    public MainWindowPlacement(AppWindow window)
    {
        _window = window;
        _placement = Read() ?? new Placement(window.Position.X, window.Position.Y, window.Size.Width, window.Size.Height, false);
    }

    public bool IsMaximized => _placement.Maximized;

    public void Restore()
    {
        bool maximized = _placement.Maximized;
        var bounds = new RectInt32(_placement.X, _placement.Y, _placement.Width, _placement.Height);
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min(bounds.Width, workArea.Width);
        int height = Math.Min(bounds.Height, workArea.Height);
        int x = Math.Clamp(bounds.X, workArea.X, workArea.X + workArea.Width - width);
        int y = Math.Clamp(bounds.Y, workArea.Y, workArea.Y + workArea.Height - height);
        _window.MoveAndResize(new RectInt32(x, y, width, height));
        _placement = new Placement(x, y, width, height, maximized);

        if (maximized && _window.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    public void Capture()
    {
        if (_window.Presenter is not OverlappedPresenter presenter)
            return;

        // Keep the normal bounds and last non-minimized state when the window is in the tray.
        if (presenter.State == OverlappedPresenterState.Maximized)
            _placement = _placement with { Maximized = true };
        else if (presenter.State == OverlappedPresenterState.Restored)
        {
            var position = _window.Position;
            var size = _window.Size;
            _placement = new Placement(position.X, position.Y, size.Width, size.Height, false);
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            string temporaryPath = StatePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_placement));
            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BluetoothLog.Write("window_placement_save_failed", new { ex.HResult });
        }
    }

    private static Placement? Read()
    {
        try
        {
            if (File.Exists(StatePath) &&
                JsonSerializer.Deserialize<Placement>(File.ReadAllText(StatePath)) is { Width: > 0, Height: > 0 } placement)
                return placement;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            BluetoothLog.Write("window_placement_read_failed", new { ex.HResult });
        }
        return null;
    }

    private sealed record Placement(int X, int Y, int Width, int Height, bool Maximized);
}
