namespace Scion.Cli.Working;

/// <summary>
/// The blob-cache seam in the fetch path: the materializer consults the cache before asking the
/// server for a blob and fills it after a verified fetch. This is a seam only — the bounded-LRU
/// cache is separate CLI work and plugs in here; until it lands, the no-op cache keeps
/// correctness independent of caching (only re-checkout bandwidth depends on it).
/// </summary>
internal interface IBlobCache
{
    /// <summary>Whether the cache holds the blob; the content lands in <paramref name="content"/> when it does.</summary>
    bool TryGet(string hash, out byte[] content);

    /// <summary>Stores a verified blob's content under its address; the cache owns eviction.</summary>
    void Put(string hash, byte[] content);
}

/// <summary>The absent cache: every lookup misses, every put is discarded.</summary>
internal sealed class NullBlobCache : IBlobCache
{
    public static readonly NullBlobCache Instance = new();

    public bool TryGet(string hash, out byte[] content)
    {
        content = [];
        return false;
    }

    public void Put(string hash, byte[] content)
    {
    }
}
