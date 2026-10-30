using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

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

    /// <summary>
    /// Decodes a canonical payload back into a tree snapshot. The id is deliberately not encoded —
    /// it is the SHA-256 of these bytes (see <see cref="ComputeId"/>) — so a canonical payload
    /// decodes to exactly the snapshot whose <see cref="TreeSnapshot.Id"/> is the hash of those
    /// bytes: the read side of the encoding <see cref="Serialize"/> produces.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are truncated, trailing, or not a canonical
    /// tree encoding (unknown version, entries out of ordinal path order, an empty path or blob
    /// hash, or an undefined content kind).</exception>
    public static TreeSnapshot Deserialize(ReadOnlySpan<byte> canonicalBytes)
    {
        var reader = new Reader(canonicalBytes);

        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unknown canonical tree format version {version}; expected {FormatVersion}.");
        }

        var entryCount = reader.ReadU32();
        var entries = ImmutableSortedDictionary<string, TreeEntry>.Empty
            .WithComparers(StringComparer.Ordinal)
            .ToBuilder();
        string? previousPath = null;
        for (var i = 0u; i < entryCount; i++)
        {
            var path = reader.ReadString();
            if (path.Length == 0)
            {
                throw new InvalidDataException($"Canonical tree encoding holds an empty path at entry {i}.");
            }

            // Serialize writes entries in ordinal path order; enforcing that here keeps the
            // guarantee unconditional — a decoded snapshot's canonical bytes are always the input
            // bytes, so its id is always the hash of exactly what was read.
            if (previousPath is not null && string.CompareOrdinal(previousPath, path) >= 0)
            {
                throw new InvalidDataException("Canonical tree encoding holds entries out of ordinal path order.");
            }

            previousPath = path;

            var blobHash = reader.ReadString();
            if (blobHash.Length == 0)
            {
                throw new InvalidDataException($"Canonical tree encoding holds an empty blob hash for path '{path}'.");
            }

            var kind = reader.ReadByte();
            if (!Enum.IsDefined((FileContentKind)kind))
            {
                throw new InvalidDataException($"Unknown {nameof(FileContentKind)} value {kind} in canonical tree encoding.");
            }

            entries.Add(path, new TreeEntry(blobHash, (FileContentKind)kind));
        }

        reader.EnsureEnd();
        return new TreeSnapshot(entries.ToImmutable());
    }

    /// <summary>Computes the tree id: the SHA-256 of the canonical encoding.</summary>
    public static string ComputeId(TreeSnapshot tree)
    {
        return Sha256.HashHex(Serialize(tree));
    }

    /// <summary>Cursor over the canonical bytes; every read validates the remaining length.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _position;

        internal Reader(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _position = 0;
        }

        internal byte ReadByte()
        {
            Ensure(1);
            var value = _bytes[_position];
            _position++;
            return value;
        }

        internal uint ReadU32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_bytes.Slice(_position, 4));
            _position += 4;
            return value;
        }

        internal string ReadString()
        {
            var length = ReadU32();
            Ensure(length);
            var value = Encoding.UTF8.GetString(_bytes.Slice(_position, (int)length));
            _position += (int)length;
            return value;
        }

        internal void EnsureEnd()
        {
            if (_position != _bytes.Length)
            {
                throw new InvalidDataException($"Canonical tree encoding has {_bytes.Length - _position} trailing byte(s).");
            }
        }

        private void Ensure(uint count)
        {
            if (count > (uint)(_bytes.Length - _position))
            {
                throw new InvalidDataException($"Canonical tree encoding truncated at offset {_position}: needed {count} more byte(s).");
            }
        }
    }
}
