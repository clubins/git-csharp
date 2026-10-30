using System.Security.Cryptography;

namespace Scion.Core;

/// <summary>SHA-256 content addressing over raw bytes. Hex output is always lowercase.</summary>
public static class Sha256
{
    /// <summary>Hashes <paramref name="data"/> and returns the digest as 64 lowercase hex characters.</summary>
    public static string HashHex(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }
}
