using System.Security.Cryptography;
using System.Text;
using Scion.Cli.Cache;

namespace Scion.Cli.Tests;

/// <summary>
/// The bounded cache: eviction always honors the byte cap, recency (not insertion order) decides
/// what survives, and an entry that is oversize, malformed, corrupted, or vanished is a miss —
/// never bad content served. The eviction decision itself is exercised through the pure policy
/// and through the disk-backed cache end to end.
/// </summary>
public sealed class LruBlobCacheTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "scn-cache-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Eviction_HonorsTheByteCap()
    {
        var cache = new LruBlobCache(directory, maxBytes: 300);
        var a = Put(cache, "a", 100);
        Put(cache, "b", 100);
        Put(cache, "c", 100);
        Assert.Equal(300, cache.CurrentBytes);

        // The fourth blob pushes past the cap; the least-recently-used one pays for it.
        var d = Put(cache, "d", 100);
        Assert.Equal(300, cache.CurrentBytes);
        Assert.False(cache.TryGet(a, out _));
        Assert.True(cache.TryGet(d, out var fetched));
        Assert.Equal(Content("d", 100), fetched);

        // Eviction removes the file, not just the index entry.
        Assert.False(File.Exists(Path.Combine(directory, a[..2], a)));
    }

    [Fact]
    public void Recency_TryGetBumpsAnEntry()
    {
        var cache = new LruBlobCache(directory, maxBytes: 300);
        var a = Put(cache, "a", 100);
        var b = Put(cache, "b", 100);

        Assert.True(cache.TryGet(a, out _));
        Put(cache, "c", 100); // Fits exactly: 300 of 300.

        var d = Put(cache, "d", 100);
        Assert.True(cache.TryGet(a, out _), "the entry read most recently must survive eviction");
        Assert.False(cache.TryGet(b, out _), "the least-recently-used entry must be the one evicted");
        Assert.True(cache.TryGet(d, out _));
    }

    [Fact]
    public void OversizeBlob_IsNeverCached()
    {
        var cache = new LruBlobCache(directory, maxBytes: 100);
        var kept = Put(cache, "kept", 60);

        Assert.False(cache.Put(HashOf("oversize"), new byte[101]));
        Assert.Equal(60, cache.CurrentBytes);
        Assert.False(cache.TryGet(HashOf("oversize"), out _));
        Assert.True(cache.TryGet(kept, out _), "an entry that can never fit must not evict what does");
    }

    [Fact]
    public void CorruptedEntry_IsAMissAndDropped()
    {
        var cache = new LruBlobCache(directory, maxBytes: 1_000);
        var hash = Put(cache, "payload", 8);

        var path = Path.Combine(directory, hash[..2], hash);
        File.WriteAllBytes(path, [1, 2, 3]); // Tampered bytes at the content's address.

        Assert.False(cache.TryGet(hash, out var served));
        Assert.Empty(served);
        Assert.Equal(0, cache.CurrentBytes);
        Assert.False(File.Exists(path), "the corrupt entry must be dropped, not left to miss again");
    }

    [Fact]
    public void MalformedHash_NeverCached_AlwaysMisses()
    {
        var cache = new LruBlobCache(directory, maxBytes: 1_000);

        Assert.False(cache.Put("../escape", [1]));
        Assert.False(cache.Put(new string('g', 64), [1]));
        Assert.Equal(0, cache.CurrentBytes);
        Assert.False(cache.TryGet(new string('g', 64), out _));
        Assert.False(Directory.Exists(directory), "a refused put must not even create the cache directory");
    }

    [Fact]
    public void VanishedFile_IsAMissAndCleansTheIndex()
    {
        var cache = new LruBlobCache(directory, maxBytes: 1_000);
        var hash = Put(cache, "payload", 8);

        File.Delete(Path.Combine(directory, hash[..2], hash));

        Assert.False(cache.TryGet(hash, out _));
        Assert.Equal(0, cache.CurrentBytes);
    }

    [Fact]
    public void EvictToCap_EvictsFromTheOldestEndUntilItFits()
    {
        IReadOnlyList<string> Evict(string a, long aBytes, string b, long bBytes, string c, long cBytes, string d, long dBytes, long cap) =>
            LruEvictionPolicy.EvictToCap([(a, aBytes), (b, bBytes), (c, cBytes), (d, dBytes)], cap);

        Assert.Equal(["a", "b", "c"], Evict("a", 1, "b", 2, "c", 3, "d", 4, cap: 6));
        Assert.Empty(Evict("a", 2, "b", 2, "c", 2, "d", 2, cap: 8));
        // 8 total against a cap of 5: evict a (6 still over), evict b (4 fits), stop.
        Assert.Equal(["a", "b"], Evict("a", 2, "b", 2, "c", 2, "d", 2, cap: 5));
        Assert.Empty(LruEvictionPolicy.EvictToCap<string>([], cap: 0));
    }

    /// <summary>Puts <paramref name="label"/>-numbered content of the given size; returns its content address.</summary>
    private static string Put(LruBlobCache cache, string label, int size)
    {
        var content = Content(label, size);
        var hash = HashOf(content);
        Assert.True(cache.Put(hash, content));
        return hash;
    }

    private static byte[] Content(string label, int size) =>
        Encoding.UTF8.GetBytes($"{label}-").Concat(new byte[size]).ToArray()[..size];

    private static string HashOf(string content) => HashOf(Encoding.UTF8.GetBytes(content));

    private static string HashOf(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
