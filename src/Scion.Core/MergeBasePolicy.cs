namespace Scion.Core;

/// <summary>
/// Best-candidate selection among <see cref="SkipGraph.MergeBase"/>'s maximal common ancestors.
/// git ranks common ancestors by ancestry — one is better than another when the other is a strict
/// ancestor of it (git-merge-base) — which leaves a criss-cross DAG with several equally-best
/// bases and, per git's own documentation, an unspecified choice among them. This policy refines
/// that ranking into a deterministic total order: among the maximal common ancestors, prefer the
/// one that is an ancestor of the fewest other candidates, counted over the two tips' joint
/// strict ancestry. The count subsumes git's ranking — a common ancestor strictly above another
/// strictly contains that other's shadow — and among incomparable bases it lands on the freshest
/// shared point, the base dominating the least already-shared history, which is the one a
/// three-way merge wants. Ties break on the content-addressed id, so the answer is symmetric in
/// the arguments and reproducible across builds.
/// </summary>
public static class MergeBasePolicy
{
    /// <summary>
    /// The best merge base of two changesets, or null when the histories share no changeset.
    /// Cost is the same order as <see cref="SkipGraph.MergeBase"/> itself: one tower walk per
    /// node of the joint ancestry, each edge-minimal over the towers.
    /// </summary>
    /// <exception cref="ArgumentNullException">The graph is null.</exception>
    /// <exception cref="KeyNotFoundException">Either id is not part of this graph.</exception>
    public static string? Select(SkipGraph graph, string first, string second)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var bases = graph.MergeBase(first, second);
        if (bases.IsEmpty)
        {
            return null;
        }

        if (bases.Length == 1)
        {
            return bases[0];
        }

        // The joint strict ancestry of both tips is the pool the shadows are counted over. The
        // tips themselves stay out: a base the tips merged directly — whose only descendants are
        // the tips — then scores zero and wins over bases that also dominate the merge layers
        // between.
        var pool = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ancestor in graph.Ancestors(first))
        {
            pool.Add(ancestor);
        }

        foreach (var ancestor in graph.Ancestors(second))
        {
            pool.Add(ancestor);
        }

        // shadow[base] = how many pool nodes sit below the base — how much shared history it
        // dominates. Walking each pool node's ancestry once counts every base found there; every
        // base's own descendants are all ancestors of a tip, so the pool misses none of them.
        var shadows = new Dictionary<string, int>(bases.Length, StringComparer.Ordinal);
        foreach (var candidate in bases)
        {
            shadows[candidate] = 0;
        }

        foreach (var node in pool)
        {
            foreach (var ancestor in graph.Ancestors(node))
            {
                if (shadows.ContainsKey(ancestor))
                {
                    shadows[ancestor]++;
                }
            }
        }

        // Sort before scanning so the strict comparison keeps the smallest id on equal shadows —
        // MergeBase's walk order is an implementation detail the answer must not depend on.
        var ordered = bases.ToArray();
        Array.Sort(ordered, StringComparer.Ordinal);

        var best = ordered[0];
        var bestShadow = shadows[best];
        for (var i = 1; i < ordered.Length; i++)
        {
            var shadow = shadows[ordered[i]];
            if (shadow < bestShadow)
            {
                best = ordered[i];
                bestShadow = shadow;
            }
        }

        return best;
    }
}
