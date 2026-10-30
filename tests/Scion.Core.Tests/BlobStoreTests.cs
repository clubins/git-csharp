using System.IO.Compression;
using Scion.Core.Storage;

namespace Scion.Core.Tests;

public class BlobStoreTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Fact]
    public void Write_Read_RoundTripsContent()
    {
        using var temp = new TempStore();
        var content = new byte[256];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = (byte)i;
        }

        var blob = Blob.Create(content);

        var hash = temp.Store.Write(blob);
        var read = temp.Store.Read(blob.Hash);

        Assert.Equal(blob.Hash, hash);
        Assert.Equal(blob, read);
        Assert.Equal(content, read.Content.ToArray());
    }

    [Fact]
    public void Write_EmptyContent_RoundTrips()
    {
        using var temp = new TempStore();

        var hash = temp.Store.Write(Blob.Create(ReadOnlyMemory<byte>.Empty));

        Assert.Equal(0, temp.Store.Read(hash).Content.Length);
    }

    [Fact]
    public void Write_SecondStoreInstanceOnSameRoot_ReadsSameContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "scion-blobstore-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var content = "persisted across store instances"u8.ToArray();
            string hash;
            var first = new BlobStore(root);
            hash = first.Write(Blob.Create(content));

            var second = new BlobStore(root);
            Assert.Equal(content, second.Read(hash).Content.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Write_IdenticalContentTwice_StoresExactlyOneFile()
    {
        using var temp = new TempStore();
        var first = Blob.Create(new byte[] { 1, 2, 3 });
        var second = Blob.Create(new byte[] { 1, 2, 3 });

        var firstHash = temp.Store.Write(first);
        var secondHash = temp.Store.Write(second);

        Assert.Equal(firstHash, secondHash);
        var files = Directory.EnumerateFiles(temp.Root, "*", SearchOption.AllDirectories).ToList();
        var file = Assert.Single(files);
        Assert.Equal(firstHash, Path.GetFileName(file));
    }

    [Fact]
    public void Write_StoresAtTwoLevelShardedPath()
    {
        using var temp = new TempStore();

        var hash = temp.Store.Write(Blob.Create("abc"u8.ToArray()));

        Assert.Equal(AbcSha256, hash);
        Assert.True(File.Exists(Path.Combine(temp.Root, "ba", "78", AbcSha256)));
    }

    [Fact]
    public void Write_CompressesRepetitiveContent()
    {
        using var temp = new TempStore();
        var blob = Blob.Create(RepeatedBytes((byte)'a', 100_000));

        temp.Store.Write(blob);

        Assert.True(new FileInfo(ShardedPath(temp.Root, blob.Hash)).Length < blob.Content.Length);
    }

    [Fact]
    public void Write_DuplicateAndFresh_LeavesNoTempFilesBehind()
    {
        using var temp = new TempStore();
        var blob = Blob.Create("temp hygiene"u8.ToArray());

        temp.Store.Write(blob);
        temp.Store.Write(blob);

        Assert.DoesNotContain(
            Directory.EnumerateFiles(temp.Root, "*", SearchOption.AllDirectories),
            file => file.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Write_ConcurrentWritersOfSameBlob_LeaveSingleIntactFile()
    {
        using var temp = new TempStore();
        var content = new byte[64 * 1024];
        Random.Shared.NextBytes(content);
        var blob = Blob.Create(content);
        const int Writers = 8;
        const int WritesPerWriter = 6;

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < WritesPerWriter; i++)
            {
                temp.Store.Write(blob);
            }
        })));

        var files = Directory.EnumerateFiles(temp.Root, "*", SearchOption.AllDirectories).ToList();
        var file = Assert.Single(files);
        Assert.Equal(blob.Hash, Path.GetFileName(file));
        Assert.Equal(content, temp.Store.Read(blob.Hash).Content.ToArray());
    }

    [Fact]
    public async Task WriteAsync_ConcurrentWritersOfSameBlob_LeaveSingleIntactFile()
    {
        using var temp = new TempStore();
        var content = new byte[64 * 1024];
        Random.Shared.NextBytes(content);
        var blob = Blob.Create(content);
        const int Writers = 8;

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 6; i++)
            {
                await temp.Store.WriteAsync(blob);
            }
        })));

        var files = Directory.EnumerateFiles(temp.Root, "*", SearchOption.AllDirectories).ToList();
        var file = Assert.Single(files);
        Assert.Equal(blob.Hash, Path.GetFileName(file));
        Assert.Equal(content, temp.Store.Read(blob.Hash).Content.ToArray());
    }

    [Fact]
    public async Task Write_ConcurrentWritersOfDistinctBlobs_PersistEveryBlob()
    {
        using var temp = new TempStore();
        var blobs = Enumerable.Range(0, 16).Select(i =>
        {
            var content = new byte[1024 + i];
            Array.Fill(content, (byte)i);
            return Blob.Create(content);
        }).ToArray();

        await Task.WhenAll(blobs.Select(b => Task.Run(() => temp.Store.Write(b))));

        foreach (var blob in blobs)
        {
            Assert.Equal(blob, temp.Store.Read(blob.Hash));
        }
    }

    [Fact]
    public async Task WriteAsync_ReadAsync_RoundTrips()
    {
        using var temp = new TempStore();
        var content = "async round trip"u8.ToArray();

        var hash = await temp.Store.WriteAsync(Blob.Create(content));
        var read = await temp.Store.ReadAsync(hash);

        Assert.Equal(content, read.Content.ToArray());
    }

    [Fact]
    public void Write_HashDoesNotMatchContent_ThrowsAndWritesNothing()
    {
        using var temp = new TempStore();
        var blob = Blob.Create("verified"u8.ToArray()) with { Hash = new string('0', 64) };

        Assert.Throws<InvalidOperationException>(() => temp.Store.Write(blob));
        Assert.Empty(Directory.EnumerateFiles(temp.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Write_OverSizeCap_ThrowsAndWritesNothing()
    {
        using var temp = new TempStore(maxBlobBytes: 1024);

        Assert.Throws<BlobTooLargeException>(() => temp.Store.Write(Blob.Create(new byte[1025])));
        Assert.Empty(Directory.EnumerateFiles(temp.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Write_AtSizeCap_Succeeds()
    {
        using var temp = new TempStore(maxBlobBytes: 1024);

        var blob = Blob.Create(new byte[1024]);

        Assert.Equal(blob.Hash, temp.Store.Write(blob));
    }

    [Fact]
    public void Read_MissingBlob_ThrowsNotFound()
    {
        using var temp = new TempStore();
        var hash = Blob.Create("never written"u8.ToArray()).Hash;

        var thrown = Assert.Throws<BlobNotFoundException>(() => temp.Store.Read(hash));

        Assert.Equal(hash, thrown.Hash);
    }

    [Fact]
    public void Read_CorruptedOnDisk_ThrowsHashMismatch()
    {
        using var temp = new TempStore();
        var blob = Blob.Create("corrupt me"u8.ToArray());
        temp.Store.Write(blob);
        File.WriteAllBytes(ShardedPath(temp.Root, blob.Hash), "not a zlib stream at all"u8.ToArray());

        var thrown = Assert.Throws<BlobHashMismatchException>(() => temp.Store.Read(blob.Hash));

        Assert.Null(thrown.ActualHash);
    }

    [Fact]
    public void Read_TruncatedOnDisk_ThrowsHashMismatch()
    {
        using var temp = new TempStore();
        var content = new byte[4096];
        Random.Shared.NextBytes(content);
        var blob = Blob.Create(content);
        temp.Store.Write(blob);
        var path = ShardedPath(temp.Root, blob.Hash);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        Assert.Throws<BlobHashMismatchException>(() => temp.Store.Read(blob.Hash));
    }

    [Fact]
    public void Read_TamperedContentHashesDifferently_ThrowsWithActualHash()
    {
        using var temp = new TempStore();
        var blob = Blob.Create("original"u8.ToArray());
        temp.Store.Write(blob);
        var tampered = Blob.Create("tampered"u8.ToArray());
        using (var file = File.Create(ShardedPath(temp.Root, blob.Hash)))
        using (var zlib = new ZLibStream(file, CompressionLevel.Optimal))
        {
            zlib.Write(tampered.Content.Span);
        }

        var thrown = Assert.Throws<BlobHashMismatchException>(() => temp.Store.Read(blob.Hash));

        Assert.Equal(tampered.Hash, thrown.ActualHash);
    }

    [Fact]
    public void Read_CompressedOverSizeCap_ThrowsTooLarge()
    {
        using var temp = new TempStore();
        var content = new byte[100_000];
        Random.Shared.NextBytes(content);
        var hash = temp.Store.Write(Blob.Create(content));
        var restricted = new BlobStore(temp.Root, new BlobStoreOptions { MaxBlobBytes = 1024 });

        Assert.Throws<BlobTooLargeException>(() => restricted.Read(hash));
    }

    [Fact]
    public void Read_DecompressedOverSizeCap_ThrowsTooLarge()
    {
        using var temp = new TempStore();
        var hash = temp.Store.Write(Blob.Create(RepeatedBytes((byte)'a', 100_000)));
        var restricted = new BlobStore(temp.Root, new BlobStoreOptions { MaxBlobBytes = 1024 });

        Assert.Throws<BlobTooLargeException>(() => restricted.Read(hash));
    }

    [Fact]
    public void MalformedHash_IsRejectedByEveryLookup()
    {
        using var temp = new TempStore();
        var store = temp.Store;

        Assert.Throws<ArgumentException>(() => store.Read("../evil"));
        Assert.Throws<ArgumentException>(() => store.Read(new string('g', 64)));
        Assert.Throws<ArgumentException>(() => store.Read(string.Empty));
        Assert.Throws<ArgumentException>(() => store.Exists("../evil"));
        Assert.Throws<ArgumentException>(() => store.Missing(["../evil"]));
    }

    [Fact]
    public void Missing_ReturnsAbsentHashesInFirstSeenOrder_Deduplicated()
    {
        using var temp = new TempStore();
        var stored = Blob.Create("stored"u8.ToArray());
        temp.Store.Write(stored);
        var absent = Blob.Create("absent"u8.ToArray());
        var alsoAbsent = Blob.Create("also absent"u8.ToArray());

        var missing = temp.Store.Missing([stored.Hash, absent.Hash, absent.Hash, stored.Hash.ToUpperInvariant(), alsoAbsent.Hash]);

        Assert.Equal([absent.Hash, alsoAbsent.Hash], missing);
    }

    [Fact]
    public void Exists_ReflectsWrites()
    {
        using var temp = new TempStore();
        var blob = Blob.Create("exists?"u8.ToArray());

        Assert.False(temp.Store.Exists(blob.Hash));
        temp.Store.Write(blob);
        Assert.True(temp.Store.Exists(blob.Hash));
    }

    private static byte[] RepeatedBytes(byte value, int count)
    {
        var bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static string ShardedPath(string root, string hash)
    {
        return Path.Combine(root, hash[..2], hash[2..4], hash);
    }

    /// <summary>A blob store on a fresh temp directory, removed on dispose.</summary>
    private sealed class TempStore : IDisposable
    {
        public TempStore(long? maxBlobBytes = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "scion-blobstore-tests", Guid.NewGuid().ToString("N"));
            Store = maxBlobBytes is null
                ? new BlobStore(Root)
                : new BlobStore(Root, new BlobStoreOptions { MaxBlobBytes = maxBlobBytes.Value });
        }

        public string Root { get; }

        public BlobStore Store { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing to clean up — the directory was already removed.
            }
        }
    }
}
