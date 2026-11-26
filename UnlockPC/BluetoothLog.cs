using System.Diagnostics;
using System.Text.Json;

namespace UnlockPC;

internal static class BluetoothLog
{
    private static readonly object Gate = new();
    private static readonly string PathName = Path.Combine(
        AppDataPaths.Logs,
        $"bluetooth-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
    private static readonly Dictionary<ulong, int> AddressLabels = new();
    private static bool _unavailable;

    internal static string[] ListFiles() => Directory.Exists(AppDataPaths.Logs)
        ? Directory.GetFiles(AppDataPaths.Logs, "bluetooth-*.jsonl") : [];

    internal static void ClearFiles()
    {
        lock (Gate)
        {
            foreach (string file in ListFiles())
                File.Delete(file);
        }
    }

    internal static int AddressLabel(ulong address)
    {
        lock (Gate)
        {
            if (!AddressLabels.TryGetValue(address, out int label))
                AddressLabels[address] = label = AddressLabels.Count + 1;
            return label;
        }
    }

    internal static void Write(string action, object? details = null)
    {
        lock (Gate)
        {
            if (_unavailable)
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                File.AppendAllText(PathName, JsonSerializer.Serialize(new
                {
                    time = DateTimeOffset.Now,
                    action,
                    details
                }) + Environment.NewLine);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _unavailable = true;
                Debug.WriteLine($"Bluetooth diagnostic log unavailable (0x{error.HResult:X8}).");
            }
        }
    }
}
