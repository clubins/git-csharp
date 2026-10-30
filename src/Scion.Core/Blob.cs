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
    /// <summary>Creates a blob, computing <see cref="Hash"/> from <paramref name="content"/>.</summary>
    public static Blob Create(ReadOnlyMemory<byte> content)
    {
        return new Blob(Sha256.HashHex(content.Span), content);
    }

    /// <summary>True when <see cref="Hash"/> is the SHA-256 of <see cref="Content"/> (case-insensitive hex).</summary>
    public bool HashMatchesContent()
    {
        return Sha256.HashHex(Content.Span).Equals(Hash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Throws when <see cref="Hash"/> is not the SHA-256 of <see cref="Content"/> — the signature of a corrupted blob.</summary>
    public void EnsureHashMatchesContent()
    {
        if (!HashMatchesContent())
        {
            throw new InvalidOperationException($"Blob hash mismatch: stored '{Hash}', content hashes to '{Sha256.HashHex(Content.Span)}'.");
        }
    }

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
