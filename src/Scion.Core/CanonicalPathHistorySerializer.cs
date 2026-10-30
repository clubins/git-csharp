using System.Buffers.Binary;
using System.Text;

namespace Scion.Core;

/// <summary>
/// Canonical, deterministic encoding of a <see cref="Derived.PathHistoryValue"/> — the exact bytes
/// whose SHA-256 digest names the value in the derived-value store. Paths are encoded in the
/// value's ordinal key order and chains in lineage order, so two values describing the same
/// history produce identical bytes — and therefore identical hashes — regardless of how they were
/// built or reached the encoding.
/// </summary>
/// <remarks>
/// <para>v1 layout — integers big-endian, strings UTF-8, lengths unsigned 32-bit:</para>
/// <list type="number">
/// <item>format version byte (0x01)</item>
/// <item>path count; then per path, in ordinal key order: length-prefixed path, chain length,
/// then per chain entry: length-prefixed changeset id</item>
/// </list>
/// </remarks>
internal static class CanonicalPathHistorySerializer
{
    private const byte FormatVersion = 0x01;

    /// <summary>Encodes a path-history value to its canonical form.</summary>
    public static byte[] Serialize(Derived.PathHistoryValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var buffer = new MemoryStream();
        buffer.WriteByte(FormatVersion);
        CanonicalFormat.WriteU32(buffer, (uint)value.Count);
        foreach (var (path, chain) in value.Entries)
        {
            CanonicalFormat.WriteString(buffer, path);
            CanonicalFormat.WriteU32(buffer, (uint)chain.Length);
            foreach (var changesetId in chain)
            {
                CanonicalFormat.WriteString(buffer, changesetId);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Reads a canonical encoding back. The encoding is exactly the writer's output: any deviation
    /// — wrong version, truncation, trailing bytes — is corruption or a bug, and is surfaced
    /// loudly rather than read past.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are truncated, trailing, or not canonical-format v1.</exception>
    public static Derived.PathHistoryValue Deserialize(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);
        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unknown canonical path-history format version {version}; expected {FormatVersion}.");
        }

        var entries = Derived.PathHistoryValue.Empty.Entries;
        var pathCount = reader.ReadU32();
        for (var i = 0; i < pathCount; i++)
        {
            var path = reader.ReadString();
            var chainLength = reader.ReadU32();
            var chain = new List<string>();
            for (var j = 0; j < chainLength; j++)
            {
                chain.Add(reader.ReadString());
            }

            entries = entries.SetItem(path, [.. chain]);
        }

        reader.EnsureAtEnd();
        return new Derived.PathHistoryValue(entries);
    }

    /// <summary>Reads one encoding byte by byte, refusing to walk past the buffer's end.</summary>
    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        public byte ReadByte()
        {
            if (_bytes.IsEmpty)
            {
                throw new InvalidDataException($"Canonical path-history encoding truncated at offset {_position}: needed 1 more byte(s).");
            }

            var value = _bytes[0];
            _bytes = _bytes[1..];
            _position++;
            return value;
        }

        public uint ReadU32()
        {
            if (_bytes.Length < 4)
            {
                throw new InvalidDataException(
                    $"Canonical path-history encoding truncated at offset {_position}: needed {4 - _bytes.Length} more byte(s).");
            }

            var value = BinaryPrimitives.ReadUInt32BigEndian(_bytes);
            _bytes = _bytes[4..];
            _position += 4;
            return value;
        }

        public string ReadString()
        {
            var length = ReadU32();
            if (length > (uint)_bytes.Length)
            {
                throw new InvalidDataException(
                    $"Canonical path-history encoding truncated at offset {_position}: needed {length - (uint)_bytes.Length} more byte(s).");
            }

            var text = Encoding.UTF8.GetString(_bytes[..(int)length]);
            _bytes = _bytes[(int)length..];
            _position += (int)length;
            return text;
        }

        public void EnsureAtEnd()
        {
            if (!_bytes.IsEmpty)
            {
                throw new InvalidDataException($"Canonical path-history encoding has {_bytes.Length} trailing byte(s).");
            }
        }
    }
}
