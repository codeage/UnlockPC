using System.Text.Json;

namespace UnlockPC;

internal sealed record WatchRuleSettings(
    bool AutoUnlock = true, bool AutoLock = true,
    int ReturnRssi = -50, int ReturnWindowSeconds = 2, int ReturnPacketCount = 1,
    bool RequireWatchUnlocked = false, bool RequireEncryptedConnection = false, bool SpeakLockActivity = false,
    int WeakRssi = -75, int VeryWeakRssi = -85, int UnlockDelaySeconds = 0, bool WakeWithWindowsHello = false)
{
    public bool IsValid() =>
        ReturnRssi is >= -126 and <= -1 &&
        ReturnWindowSeconds is >= 1 and <= 30 &&
        ReturnPacketCount is >= 1 and <= 10 &&
        WeakRssi is >= -126 and <= -1 &&
        VeryWeakRssi >= -126 && VeryWeakRssi <= WeakRssi &&
        UnlockDelaySeconds is >= 0 and <= 300;
}

internal sealed class WatchRuleStore(string path)
{
    public static WatchRuleStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnlockPC", "watch-rules.json"));

    public WatchRuleSettings Read()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;
            var settings = root.Deserialize<WatchRuleSettings>();
            if (settings is not null)
            {
                settings = settings with
                {
                    AutoUnlock = settings.AutoUnlock && !settings.WakeWithWindowsHello,
                    ReturnRssi = ReadLegacyValue(nameof(settings.ReturnRssi), "UnlockRssi", settings.ReturnRssi, -126, -1),
                    ReturnWindowSeconds = ReadLegacyValue(nameof(settings.ReturnWindowSeconds), "UnlockSeconds", settings.ReturnWindowSeconds, 1, 30),
                    ReturnPacketCount = ReadLegacyValue(nameof(settings.ReturnPacketCount), "UnlockCount", settings.ReturnPacketCount, 1, 10),
                    VeryWeakRssi = root.TryGetProperty(nameof(settings.VeryWeakRssi), out _)
                        ? settings.VeryWeakRssi : Math.Max(-126, settings.WeakRssi - 10)
                };
            }
            return settings is not null && settings.IsValid()
                ? settings
                : throw new InvalidDataException("高级选项文件无效。");

            int ReadLegacyValue(string currentName, string legacyName, int currentValue, int minimum, int maximum) =>
                !root.TryGetProperty(currentName, out _) &&
                root.TryGetProperty(legacyName, out var legacy) && legacy.ValueKind == JsonValueKind.Number &&
                legacy.TryGetInt32(out int value) && value >= minimum && value <= maximum ? value : currentValue;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }

    public void Save(WatchRuleSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(settings));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
