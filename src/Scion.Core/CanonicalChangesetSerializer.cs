using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace Scion.Core;

/// <summary>
/// Canonical, deterministic encoding of a changeset payload — the exact bytes whose SHA-256
/// digest is <see cref="Changeset.Id"/>. Identical changesets must produce identical bytes on
/// every run and every platform, so the format fixes field order, endianness, and string
/// encoding, and canonicalizes the change-list order before encoding.
/// </summary>
/// <remarks>
/// <para>v1 layout — integers big-endian, strings UTF-8, lengths unsigned 32-bit:</para>
/// <list type="number">
/// <item>format version byte (0x01)</item>
/// <item>parent count; then per parent, a length-prefixed string</item>
/// <item>change count; then per change (ordered by path ordinal, then type, old blob, new blob,
/// then content kind): length-prefixed path, type byte, kind byte, optional-string old blob,
/// optional-string new blob</item>
/// <item>length-prefixed message</item>
/// <item>length-prefixed author name, then length-prefixed author email</item>
/// <item>64-bit Unix timestamp in seconds, UTC</item>
/// </list>
/// <para>An optional-string is a marker byte (0 = absent, 1 = present) followed, when present, by
/// a length-prefixed string.</para>
/// <para>The changeset <see cref="Changeset.Id"/> is deliberately not encoded — it is the SHA-256
/// of these bytes (see <see cref="ComputeId"/>) — so <see cref="Deserialize"/> always returns a
/// changeset whose Id matches its payload.</para>
/// </remarks>
public static class CanonicalChangesetSerializer
{
    private const byte FormatVersion = 0x01;

    /// <summary>Encodes a changeset payload to its canonical form. <see cref="Changeset.Id"/> is not encoded.</summary>
    public static byte[] Serialize(Changeset changeset)
    {
        ArgumentNullException.ThrowIfNull(changeset);
        return Serialize(changeset.Parents, changeset.Changes, changeset.Message, changeset.Author, changeset.TimestampUtc);
    }

    /// <summary>Encodes a changeset payload to its canonical form.</summary>
    public static byte[] Serialize(
        ImmutableArray<string> parents,
        ImmutableArray<ChangeEntry> changes,
        string message,
        Author author,
        long timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(author);

        using var buffer = new MemoryStream();
        buffer.WriteByte(FormatVersion);

        CanonicalFormat.WriteU32(buffer, (uint)parents.Length);
        foreach (var parent in parents)
        {
            CanonicalFormat.WriteString(buffer, parent);
        }

        var canonicalChanges = CanonicalChangeOrder.Canonicalize(changes);
        CanonicalFormat.WriteU32(buffer, (uint)canonicalChanges.Length);
        foreach (var change in canonicalChanges)
        {
            CanonicalFormat.WriteString(buffer, change.Path);
            buffer.WriteByte((byte)change.Type);
            buffer.WriteByte((byte)change.Kind);
            CanonicalFormat.WriteOptionalString(buffer, change.OldBlob);
            CanonicalFormat.WriteOptionalString(buffer, change.NewBlob);
        }

        CanonicalFormat.WriteString(buffer, message);
        CanonicalFormat.WriteString(buffer, author.Name);
        CanonicalFormat.WriteString(buffer, author.Email);
        CanonicalFormat.WriteI64(buffer, timestampUtc);

        return buffer.ToArray();
    }

    /// <summary>
    /// Decodes a canonical payload back into a changeset, recomputing
    /// <see cref="Changeset.Id"/> from the decoded payload.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are truncated, trailing, or not canonical-format v1.</exception>
    public static Changeset Deserialize(ReadOnlySpan<byte> canonicalBytes)
    {
        var reader = new Reader(canonicalBytes);

        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unknown canonical changeset format version {version}; expected {FormatVersion}.");
        }

        var parentCount = reader.ReadU32();
        var parentsBuilder = ImmutableArray.CreateBuilder<string>();
        for (uint i = 0; i < parentCount; i++)
        {
            parentsBuilder.Add(reader.ReadString());
        }

        var changeCount = reader.ReadU32();
        var changesBuilder = ImmutableArray.CreateBuilder<ChangeEntry>();
        for (uint i = 0; i < changeCount; i++)
        {
            var path = reader.ReadString();
            var type = ReadEnum<ChangeType>(reader.ReadByte(), nameof(ChangeType));
            var kind = ReadEnum<FileContentKind>(reader.ReadByte(), nameof(FileContentKind));
            var oldBlob = reader.ReadOptionalString();
            var newBlob = reader.ReadOptionalString();
            changesBuilder.Add(new ChangeEntry(path, oldBlob, newBlob, kind, type));
        }

        var message = reader.ReadString();
        var authorName = reader.ReadString();
        var authorEmail = reader.ReadString();
        var timestampUtc = reader.ReadI64();
        reader.EnsureEnd();

        // CreateBuilder(...).ToImmutable() yields a *default* (null-backed) array when nothing was
        // added; a decoded root must be observationally identical to a constructed one — [] not
        // default — because foreach over a default ImmutableArray throws.
        var parents = parentsBuilder.Count == 0 ? [] : parentsBuilder.ToImmutable();
        var changes = changesBuilder.Count == 0 ? [] : changesBuilder.ToImmutable();
        var author = new Author(authorName, authorEmail);

        return new Changeset(
            ComputeId(parents, changes, message, author, timestampUtc),
            parents,
            changes,
            message,
            author,
            timestampUtc);
    }

    /// <summary>Computes the changeset Id: the SHA-256 of the canonical payload encoding.</summary>
    public static string ComputeId(Changeset changeset)
    {
        return Sha256.HashHex(Serialize(changeset));
    }

    /// <summary>Computes the changeset Id without materializing a <see cref="Changeset"/> first.</summary>
    public static string ComputeId(
        ImmutableArray<string> parents,
        ImmutableArray<ChangeEntry> changes,
        string message,
        Author author,
        long timestampUtc)
    {
        return Sha256.HashHex(Serialize(parents, changes, message, author, timestampUtc));
    }

    private static TEnum ReadEnum<TEnum>(byte value, string enumName)
        where TEnum : struct, Enum
    {
        var converted = (TEnum)Enum.ToObject(typeof(TEnum), value);
        if (!Enum.IsDefined(converted))
        {
            throw new InvalidDataException($"Unknown {enumName} value {value} in canonical changeset encoding.");
        }

        return converted;
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

        internal long ReadI64()
        {
            Ensure(8);
            var value = BinaryPrimitives.ReadInt64BigEndian(_bytes.Slice(_position, 8));
            _position += 8;
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

        internal string? ReadOptionalString()
        {
            return ReadByte() switch
            {
                0 => null,
                1 => ReadString(),
                var marker => throw new InvalidDataException($"Invalid optional-string marker {marker} at offset {_position - 1} in canonical changeset encoding."),
            };
        }

        internal void EnsureEnd()
        {
            if (_position != _bytes.Length)
            {
                throw new InvalidDataException($"Canonical changeset encoding has {_bytes.Length - _position} trailing byte(s).");
            }
        }

        private void Ensure(uint count)
        {
            if (count > (uint)(_bytes.Length - _position))
            {
                throw new InvalidDataException($"Canonical changeset encoding truncated at offset {_position}: needed {count} more byte(s).");
            }
        }
    }
}
