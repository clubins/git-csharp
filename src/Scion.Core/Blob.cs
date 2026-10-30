namespace Scion.Core;

/// <summary>
/// Content-addressed file content. <see cref="Hash"/> is the lowercase hex SHA-256 of
/// <see cref="Content"/>; stores write each blob once and verify the hash on read, so a
/// corrupted blob is detected rather than silently served.
/// </summary>
/// <remarks>
/// Equality is content-aware: two blobs with identical content bytes are equal even when their
/// backing arrays are not — the semantics content addressing implies.
/// </remarks>
public sealed record Blob(string Hash, ReadOnlyMemory<byte> Content)
{
    /// <summary>Compares hashes case-insensitively and content byte-wise.</summary>
    public bool Equals(Blob? other)
    {
        return other is not null
            && string.Equals(Hash, other.Hash, StringComparison.OrdinalIgnoreCase)
            && Content.Span.SequenceEqual(other.Content.Span);
    }

    /// <summary>Hashes by identity only: the hash string already names the content.</summary>
    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Hash);
    }
}
