using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Scion.Cli.Server;

namespace Scion.Cli.Tests;

/// <summary>
/// The blob-fetch stream reader against hand-built framed streams: multi-chunk entries, a clean
/// empty stream, and the two corruptions the verification contract exists for — a digest that
/// does not match the hash the content arrived under, and a stream that ends early. Both fail
/// rather than yield wrong bytes.
/// </summary>
public sealed class BlobFetchStreamTests
{
    [Fact]
    public async Task ReadEntriesAsync_ParsesMultiChunkEntriesAndVerifiesHashes()
    {
        var stream = new MemoryStream(Framed(
            [
                ("hello", ["he", "llo"]),
                ("world!", ["wor", "ld", "!"]),
            ]));

        var entries = await CollectAsync(stream);

        Assert.Equal(2, entries.Count);
        Assert.Equal(HashOf("hello"), entries[0].Hash);
        Assert.Equal("hello", Encoding.UTF8.GetString(entries[0].Content));
        Assert.Equal(HashOf("world!"), entries[1].Hash);
        Assert.Equal("world!", Encoding.UTF8.GetString(entries[1].Content));
    }

    [Fact]
    public async Task ReadEntriesAsync_EmptyStream_YieldsNothing()
    {
        Assert.Empty(await CollectAsync(new MemoryStream()));
    }

    [Fact]
    public async Task ReadEntriesAsync_ContentThatDoesNotMatchItsAddress_Fails()
    {
        var bytes = Framed([("hello", ["hello"])]);
        bytes[64 + sizeof(ulong)] ^= 0xFF; // Corrupt the first content byte; the hash key still names "hello".

        var failure = await Record.ExceptionAsync(() => CollectAsync(new MemoryStream(bytes)));

        Assert.IsType<ScionCliException>(failure);
        Assert.Contains("hashed to", failure!.Message);
    }

    [Fact]
    public async Task ReadEntriesAsync_TruncatedStream_FailsRatherThanYielding()
    {
        var bytes = Framed([("hello", ["hello"])]);
        var truncated = bytes[..(bytes.Length - 2)]; // Cut into the closing zero-length chunk.

        var failure = await Record.ExceptionAsync(() => CollectAsync(new MemoryStream(truncated)));

        Assert.IsType<ScionCliException>(failure);
        Assert.Contains("ended early", failure!.Message);
    }

    private static async Task<List<BlobFetchEntry>> CollectAsync(Stream stream)
    {
        List<BlobFetchEntry> entries = [];
        await foreach (var entry in BlobFetchStream.ReadEntriesAsync(stream))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static string HashOf(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static byte[] Framed((string Content, string[] Chunks)[] entries)
    {
        using var stream = new MemoryStream();
        foreach (var (content, chunks) in entries)
        {
            stream.Write(Encoding.ASCII.GetBytes(HashOf(content)));
            foreach (var chunk in chunks)
            {
                WriteChunk(stream, Encoding.UTF8.GetBytes(chunk));
            }

            WriteChunk(stream, []); // The zero-length close.
        }

        return stream.ToArray();
    }

    private static void WriteChunk(MemoryStream stream, byte[] payload)
    {
        var prefix = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(prefix, (ulong)payload.Length);
        stream.Write(prefix);
        stream.Write(payload);
    }
}
