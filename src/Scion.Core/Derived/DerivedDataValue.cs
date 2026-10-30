namespace Scion.Core.Derived;

/// <summary>
/// The opaque unit of derived data: whatever one <see cref="IDerivedData"/> computes for one
/// changeset, carried as canonical bytes the engine persists, caches, and serves without ever
/// looking inside. <see cref="Hash"/> — the lowercase hex SHA-256 of <see cref="Payload"/> — is
/// the value's identity, so equal hashes mean equal bytes and equality never walks the payload.
/// </summary>
/// <remarks>
/// As with <see cref="Changeset"/>, the record constructor cannot stop a caller from minting a
/// value whose <see cref="Hash"/> is not the hash of its <see cref="Payload"/>; storage is where
/// a lie like that must stop — the derived-value store checks the hash before every write and on
/// every read. Build values through <see cref="FromPayload"/> and the pair always agrees.
/// </remarks>
public sealed record DerivedDataValue(string Hash, ReadOnlyMemory<byte> Payload)
{
    /// <summary>Wraps a payload and computes its content hash.</summary>
    public static DerivedDataValue FromPayload(ReadOnlyMemory<byte> payload)
    {
        return new DerivedDataValue(Sha256.HashHex(payload.Span), payload);
    }

    /// <summary>Compares by content hash — equal hashes are equal payloads by construction.</summary>
    public bool Equals(DerivedDataValue? other)
    {
        return other is not null && string.Equals(Hash, other.Hash, StringComparison.Ordinal);
    }

    /// <summary>Hashes the content address only, never the bytes behind it.</summary>
    public override int GetHashCode()
    {
        return Hash.GetHashCode(StringComparison.Ordinal);
    }
}
