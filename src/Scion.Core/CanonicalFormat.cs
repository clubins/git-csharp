using System.Buffers.Binary;
using System.Text;

namespace Scion.Core;

/// <summary>
/// Byte-level primitives shared by canonical serializers: unsigned 32-bit integers big-endian,
/// strings UTF-8 with an unsigned 32-bit length prefix, and the optional-string marker encoding.
/// Every canonical format composes these, so identical payloads produce identical bytes on every
/// platform.
/// </summary>
internal static class CanonicalFormat
{
    public static void WriteU32(MemoryStream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public static void WriteI64(MemoryStream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public static void WriteString(MemoryStream stream, string value)
    {
        var encoded = Encoding.UTF8.GetBytes(value);
        WriteU32(stream, (uint)encoded.Length);
        stream.Write(encoded);
    }

    public static void WriteOptionalString(MemoryStream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }

        stream.WriteByte(1);
        WriteString(stream, value);
    }
}
