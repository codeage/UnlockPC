using System.Security.Cryptography;

namespace UnlockPC;

internal sealed class SavedUnlockStore(string path)
{
    private const byte Version = 2;
    private const int LegacyIrkLength = 16;

    public static SavedUnlockStore ForCurrentUser() => new(AppDataPaths.WatchAuthorization);

    public bool Exists => File.Exists(path);

    public SavedUnlock? Read()
    {
        byte[] encrypted;
        try { encrypted = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }

        byte[] data = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = new BinaryReader(stream);
            byte version = reader.ReadByte();
            if (version is not (1 or Version))
                throw new InvalidDataException("无法识别已保存的自动解锁配置版本。");

            string deviceId = reader.ReadString();
            ulong bondAddress = version == Version ? reader.ReadUInt64() : 0;
            long passwordOffset = stream.Position + (version == 1 ? LegacyIrkLength : 0);
            long passwordLength = stream.Length - passwordOffset;
            if (string.IsNullOrEmpty(deviceId) || bondAddress > 0xffffffffffff || passwordLength is not (0 or DirectUnlock.PasswordBytes))
                throw new InvalidDataException("已保存的自动解锁配置不完整。");

            byte[] password = data.AsSpan((int)passwordOffset).ToArray();
            try
            {
                if (version == 1)
                    Save(deviceId, bondAddress, password);
                return new SavedUnlock(deviceId, bondAddress, password);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(password);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public void Save(string deviceId, ulong bondAddress, byte[] password)
    {
        if (string.IsNullOrEmpty(deviceId) || bondAddress > 0xffffffffffff || password.Length is not (0 or DirectUnlock.PasswordBytes))
            throw new ArgumentException("自动解锁配置不完整。");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Version);
        writer.Write(deviceId);
        writer.Write(bondAddress);
        // Reserve before appending secrets so the stream never leaves a superseded secret buffer.
        stream.Capacity = (int)stream.Length + password.Length;
        writer.Write(password);
        byte[] data = stream.GetBuffer();
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(data); }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(encrypted);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Delete()
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }
    }
}

internal sealed record SavedUnlock(string DeviceId, ulong BondAddress, byte[] Password) : IDisposable
{
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Password);
    }
}
