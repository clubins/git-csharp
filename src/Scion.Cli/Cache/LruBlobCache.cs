using System.Security.Cryptography;
using Scion.Cli.Local;
using Scion.Cli.Working;

namespace Scion.Cli.Cache;

/// <summary>
/// The thin client's bounded object cache: blobs stored once under the repository's
/// <c>.scn/cache</c> directory at their content address, tracked in memory by bytes and recency.
/// The cap (default 2 GiB, per the spec) is why a long-running agent never accumulates a
/// de-facto clone: every insertion evicts least-recently-used entries until the total fits.
/// This is the seam fetch and materialization paths share — they call <see cref="Put"/> after a
/// download and <see cref="TryGet"/> before one; the cache owns nothing beyond its directory.
/// </summary>
internal sealed class LruBlobCache(string directory, long maxBytes = LruBlobCache.DefaultMaxBytes) : IBlobCache
{
    /// <summary>The spec's default cap: 2 GiB.</summary>
    public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

    private readonly object gate = new();
    private readonly Dictionary<string, TrackedEntry> index = new(StringComparer.Ordinal);

    // Recency as a monotonic access sequence, not a clock: relative order is all the LRU needs,
    // and the clock's millisecond resolution would tie rapid insertions into unspecified order.
    private long accessSequence;

    /// <summary>The per-repository directory the cache stores blobs under.</summary>
    public static string DefaultDirectory(string repoRoot) =>
        Path.Combine(ScionPaths.StateDirectory(repoRoot), "cache", "blobs");

    /// <summary>The bytes the tracked entries currently occupy — the number eviction keeps under the cap.</summary>
    public long CurrentBytes
    {
        get
        {
            lock (gate)
            {
                return index.Values.Sum(entry => entry.Bytes);
            }
        }
    }

    /// <summary>
    /// Stores content at its address and evicts to the cap. False when nothing was stored: a
    /// malformed hash (never a valid address, so never trusted as a filename), or content larger
    /// than the whole cap — an entry that can never fit must not evict everything else for
    /// nothing. Re-storing an address is a no-op refresh: the bytes are already there.
    /// </summary>
    public bool Put(string hash, byte[] content)
    {
        if (!IsContentAddress(hash))
        {
            return false;
        }

        if (content.Length > maxBytes)
        {
            return false;
        }

        var path = PathFor(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);

        lock (gate)
        {
            index[hash] = new TrackedEntry(content.Length, ++accessSequence);
            EvictToCap();
        }

        return true;
    }

    /// <summary>
    /// The cached content at <paramref name="hash"/>, with the entry bumped to newest. A miss on:
    /// a malformed hash, an untracked address, or a file that vanished or fails its hash check —
    /// corruption surfaces as a miss and the bad entry is dropped, never served.
    /// </summary>
    public bool TryGet(string hash, out byte[] content)
    {
        content = [];

        if (!IsContentAddress(hash))
        {
            return false;
        }

        lock (gate)
        {
            if (!index.TryGetValue(hash, out var tracked))
            {
                return false;
            }

            index[hash] = tracked with { LastAccess = ++accessSequence };
        }

        try
        {
            var bytes = File.ReadAllBytes(PathFor(hash));
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!actualHash.Equals(hash, StringComparison.Ordinal))
            {
                Drop(hash);
                return false;
            }

            content = bytes;
            return true;
        }
        catch (IOException)
        {
            // A vanished entry is a legitimate cache race (evicted between the index check and
            // the read), not a failure of the caller's request: it is a miss, and the stale
            // index entry goes with it.
            Drop(hash);
            return false;
        }
    }

    private void EvictToCap()
    {
        var lruFirst = index
            .OrderBy(pair => pair.Value.LastAccess)
            .Select(pair => (pair.Key, pair.Value.Bytes))
            .ToList();

        foreach (var evicted in LruEvictionPolicy.EvictToCap(lruFirst, maxBytes))
        {
            index.Remove(evicted);
            File.Delete(PathFor(evicted));
        }
    }

    private void Drop(string hash)
    {
        lock (gate)
        {
            index.Remove(hash);
        }

        File.Delete(PathFor(hash));
    }

    private string PathFor(string hash) => Path.Combine(directory, hash[..2], hash);

    /// <summary>A lowercase hex SHA-256 address — the only shape trusted as a filename under the cache directory.</summary>
    private static bool IsContentAddress(string hash)
    {
        return hash.Length == 64
            && hash.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    }

    private sealed record TrackedEntry(long Bytes, long LastAccess);
}
