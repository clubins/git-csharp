using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;

namespace Scion.Cli.Server;

/// <summary>One verified blob read from the fetch stream: its content address and raw content.</summary>
internal sealed record BlobFetchEntry(string Hash, byte[] Content);

/// <summary>
/// The client's half of the blobs/missing wire format (the format is specified on the server's
/// <c>BlobFetchFraming</c>): entries of a 64-ASCII-byte hash key followed by length-prefixed
/// chunks — an 8-byte unsigned little-endian length, then that many bytes — closed by a
/// zero-length chunk. Entries are parsed one at a time off the stream, and each entry's content
/// is hashed as it drains: a digest that does not match the hash it arrived under, or a stream
/// that ends early, is a failure — never wrong bytes presented as right.
/// </summary>
internal static class BlobFetchStream
{
    private const int HashKeyBytes = 64;
    private const int LengthPrefixBytes = sizeof(ulong);

    /// <summary>Rejects absurd chunk lengths so a corrupted stream cannot demand unbounded memory.</summary>
    private const long MaxChunkBytes = 64 * 1024 * 1024;

    /// <summary>Reads the framed entries in stream order, verifying each against its own hash.</summary>
    public static async IAsyncEnumerable<BlobFetchEntry> ReadEntriesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prefix = new byte[LengthPrefixBytes];
        while (true)
        {
            var key = new byte[HashKeyBytes];
            var read = await ReadSomeAsync(stream, key, cancellationToken);
            if (read == 0)
            {
                // Clean end: the stream closed exactly on an entry boundary.
                yield break;
            }

            if (read < HashKeyBytes || !IsHex(key))
            {
                throw new ScionCliException("The server's blob stream is malformed: a blob's hash key is not 64 hex characters.");
            }

            var hash = Encoding.ASCII.GetString(key);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var content = new MemoryStream();
            while (true)
            {
                await ReadExactlyAsync(stream, prefix, "a chunk's length prefix", cancellationToken);
                var length = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
                if (length == 0)
                {
                    break;
                }

                if (length > MaxChunkBytes)
                {
                    throw new ScionCliException($"The server's blob stream is malformed: a chunk claims {length} bytes.");
                }

                var chunk = new byte[length];
                await ReadExactlyAsync(stream, chunk, $"a {length}-byte chunk", cancellationToken);
                hasher.AppendData(chunk);
                content.Write(chunk);
            }

            var computed = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!computed.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ScionCliException(
                    $"The blob stream's content addressed '{hash}' hashed to '{computed}' — corrupt transfer, refusing to use it.");
            }

            yield return new BlobFetchEntry(hash, content.ToArray());
        }
    }

    /// <summary>Fills the buffer, answering how much was read — zero only at a clean end of stream.</summary>
    private static async Task<int> ReadSomeAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, string stage, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken);
        }
        catch (EndOfStreamException)
        {
            throw new ScionCliException($"The server's blob stream ended early — {stage} is incomplete; the fetch is truncated.");
        }
    }

    private static bool IsHex(byte[] key) => key.All(byteValue => Uri.IsHexDigit((char)byteValue));
}
