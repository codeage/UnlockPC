using System.Security.Cryptography;

namespace UnlockPC;

internal sealed class IrkResolver : IDisposable
{
    private Aes _aes;
    private Aes? _reversed;

    public byte[] CopyKey() => _aes.Key;

    public IrkResolver(byte[] key)
    {
        _aes = Aes.Create();
        _aes.Key = key;

        var reversed = key.ToArray();
        Array.Reverse(reversed);
        try
        {
            if (!key.AsSpan().SequenceEqual(reversed))
            {
                _reversed = Aes.Create();
                _reversed.Key = reversed;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(reversed);
        }
    }

    public bool Resolves(ulong address)
    {
        ulong prand = address >> 24;
        if (prand >> 22 != 1 || (prand & 0x3FFFFF) is 0 or 0x3FFFFF)
            return false;

        Span<byte> input = stackalloc byte[16];
        input.Clear();
        input[13] = (byte)(prand >> 16);
        input[14] = (byte)(prand >> 8);
        input[15] = (byte)prand;

        uint expected = (uint)(address & 0xFFFFFF);
        bool forwardMatch = ComputeHash(_aes, input) == expected;
        if (_reversed is not { } reversed)
            return forwardMatch;

        bool reverseMatch = ComputeHash(reversed, input) == expected;
        if (forwardMatch == reverseMatch)
            return false;

        // Keep a single key order for this observation after the first unambiguous hit.
        if (reverseMatch)
        {
            _aes.Dispose();
            _aes = reversed;
        }
        else
        {
            reversed.Dispose();
        }
        _reversed = null;
        return true;
    }

    private static uint ComputeHash(Aes aes, ReadOnlySpan<byte> input)
    {
        Span<byte> encrypted = stackalloc byte[16];
        aes.EncryptEcb(input, encrypted, PaddingMode.None);
        return (uint)(encrypted[13] << 16 | encrypted[14] << 8 | encrypted[15]);
    }

    public void Dispose()
    {
        _aes.Dispose();
        _reversed?.Dispose();
    }
}
