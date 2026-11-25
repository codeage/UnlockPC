using System.Security.Cryptography;

namespace UnlockPC;

internal sealed class SavedUnlockStore(string path)
{
    private const byte Version = 1;
    private const int IrkLength = 16;

    public static SavedUnlockStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnlockPC", "automatic-unlock.dpapi"));

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
            if (reader.ReadByte() != Version)
                throw new InvalidDataException("无法识别已保存的自动解锁配置版本。");

            string deviceId = reader.ReadString();
            long secretLength = stream.Length - stream.Position;
            if (string.IsNullOrEmpty(deviceId) || secretLength is not (IrkLength or IrkLength + DirectUnlock.PasswordBytes))
                throw new InvalidDataException("已保存的自动解锁配置不完整。");

            int offset = (int)stream.Position;
            return new SavedUnlock(deviceId, data.AsSpan(offset, IrkLength).ToArray(), data.AsSpan(offset + IrkLength).ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public void Save(string deviceId, byte[] irk, byte[] password)
    {
        if (string.IsNullOrEmpty(deviceId) || irk.Length != IrkLength || password.Length is not (0 or DirectUnlock.PasswordBytes))
            throw new ArgumentException("自动解锁配置不完整。");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Version);
        writer.Write(deviceId);
        // Reserve before appending secrets so the stream never leaves a superseded secret buffer.
        stream.Capacity = (int)stream.Length + irk.Length + password.Length;
        writer.Write(irk);
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

internal sealed record SavedUnlock(string DeviceId, byte[] Irk, byte[] Password) : IDisposable
{
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Irk);
        CryptographicOperations.ZeroMemory(Password);
    }
}
