namespace Scion.Core;

/// <summary>
/// Canonical, deterministic encoding of a <see cref="TreeSnapshot"/> — the exact bytes whose
/// SHA-256 digest is <see cref="TreeSnapshot.Id"/>. Entries are encoded in the snapshot's ordinal
/// path order, so two snapshots describing the same tree produce identical bytes, and therefore
/// identical ids, regardless of the chains that produced them.
/// </summary>
/// <remarks>
/// <para>v1 layout — integers big-endian, strings UTF-8, lengths unsigned 32-bit:</para>
/// <list type="number">
/// <item>format version byte (0x01)</item>
/// <item>entry count; then per entry, in ordinal path order: length-prefixed path,
/// length-prefixed blob hash, content-kind byte</item>
/// </list>
/// </remarks>
internal static class CanonicalTreeSerializer
{
    private const byte FormatVersion = 0x01;

    /// <summary>Encodes a tree snapshot to its canonical form.</summary>
    public static byte[] Serialize(TreeSnapshot tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        using var buffer = new MemoryStream();
        buffer.WriteByte(FormatVersion);
        CanonicalFormat.WriteU32(buffer, (uint)tree.Count);
        foreach (var (path, entry) in tree.Entries)
        {
            CanonicalFormat.WriteString(buffer, path);
            CanonicalFormat.WriteString(buffer, entry.BlobHash);
            buffer.WriteByte((byte)entry.Kind);
        }

        return buffer.ToArray();
    }

    /// <summary>Computes the tree id: the SHA-256 of the canonical encoding.</summary>
    public static string ComputeId(TreeSnapshot tree)
    {
        return Sha256.HashHex(Serialize(tree));
    }
}
