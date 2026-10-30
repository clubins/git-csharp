namespace Scion.Cli.Cache;

/// <summary>
/// The eviction decision for a byte-bounded LRU: given the tracked entries ordered
/// least-recently-used first with their byte sizes, the entries to drop so the remainder fits the
/// cap — always from the oldest end, stopping the moment it fits. Pure: no I/O and no clock, so
/// the decision is trivially testable; <see cref="LruBlobCache"/> executes it.
/// </summary>
internal static class LruEvictionPolicy
{
    /// <summary>The entries to evict, oldest first, until the total of the survivors is within <paramref name="cap"/>.</summary>
    public static IReadOnlyList<T> EvictToCap<T>(IReadOnlyList<(T Entry, long Bytes)> entriesLruFirst, long cap)
    {
        long total = 0;
        foreach (var (_, bytes) in entriesLruFirst)
        {
            total += bytes;
        }

        List<T> evictions = [];
        for (var i = 0; i < entriesLruFirst.Count && total > cap; i++)
        {
            total -= entriesLruFirst[i].Bytes;
            evictions.Add(entriesLruFirst[i].Entry);
        }

        return evictions;
    }
}
