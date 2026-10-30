using System.Collections.Immutable;

namespace Scion.Core;

/// <summary>
/// Skip-graph derived data over a closed snapshot of the changeset DAG — the spec's derived-data
/// type of "skip lists over the commit graph", the structure behind log-depth DAG queries such as
/// merge-base. Every changeset is labeled with a height and carries right-hand pointers, one slot
/// per level below its height: level 0 mirrors the parent edges (first parent first), and level
/// ℓ &gt; 0 jumps to the nearest ancestors — one per parent chain, deduplicated — whose height
/// exceeds ℓ, skipping over the run of ancestors with height at most ℓ. Heights follow a
/// geometric distribution, so those runs grow geometrically: root walks along the towers take
/// logarithmically many hops on linear spans, while tower reachability stays exactly equal to
/// reachability over the raw parent edges.
/// </summary>
/// <remarks>
/// The pointers are "right-hand" because the log is drawn tip-first: new changesets prepend to
/// the WAL, so following a pointer moves rightward into history. Every query is exact on any
/// DAG — <see cref="MergeBase"/> in particular spends work proportional to the number of common
/// ancestors, with each walk edge-minimal over the towers. Persisting the towers as server-side
/// derived data is a later task; this type is the pure structure.
/// </remarks>
public sealed class SkipGraph
{
    /// <summary>
    /// Upper bound on node heights and therefore on tower levels. Heights beyond ~2·log₂(n) are
    /// exponentially unlikely under the geometric assignment, so 64 leaves ample headroom.
    /// </summary>
    public const int MaxLevel = 64;

    private readonly Dictionary<string, SkipGraphNode> _nodes;

    private SkipGraph(Dictionary<string, SkipGraphNode> nodes)
    {
        _nodes = nodes;
    }

    /// <summary>Number of changesets in the graph.</summary>
    public int Count => _nodes.Count;

    /// <summary>
    /// Builds the skip-graph over a closed snapshot of changesets. Heights come from
    /// <paramref name="heightOf"/>; the default assignment is the deterministic hash-derived one
    /// (see <see cref="SkipGraphHeights.FromId"/>), so independent builds of the same snapshot
    /// produce identical towers.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A changeset is null, an id repeats, a parent reference points outside the snapshot, a
    /// changeset has more than <see cref="Changeset.MaxParents"/> parents, or the parent graph is
    /// cyclic (a self-parent is a one-node cycle).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A height falls outside [1, <see cref="MaxLevel"/>].</exception>
    public static SkipGraph Build(IReadOnlyList<Changeset> changesets, Func<string, int>? heightOf = null)
    {
        ArgumentNullException.ThrowIfNull(changesets);
        var pickHeight = heightOf ?? (id => SkipGraphHeights.FromId(id, MaxLevel));

        var index = new Dictionary<string, int>(changesets.Count, StringComparer.Ordinal);
        for (var i = 0; i < changesets.Count; i++)
        {
            var changeset = changesets[i] ?? throw new ArgumentException($"Changeset at index {i} is null.", nameof(changesets));
            if (!index.TryAdd(changeset.Id, i))
            {
                throw new ArgumentException($"Changeset id {changeset.Id} appears more than once.", nameof(changesets));
            }
        }

        var parentIndices = new int[changesets.Count][];
        for (var i = 0; i < changesets.Count; i++)
        {
            var parents = changesets[i].Parents;
            if (parents.Length > Changeset.MaxParents)
            {
                throw new ArgumentException(
                    $"Changeset {changesets[i].Id} has {parents.Length} parents; at most {Changeset.MaxParents} is supported.",
                    nameof(changesets));
            }

            parentIndices[i] = new int[parents.Length];
            for (var p = 0; p < parents.Length; p++)
            {
                if (!index.TryGetValue(parents[p], out var parentIndex))
                {
                    throw new ArgumentException(
                        $"Changeset {changesets[i].Id} references parent {parents[p]} outside the snapshot.",
                        nameof(changesets));
                }

                parentIndices[i][p] = parentIndex;
            }
        }

        var heights = new int[changesets.Count];
        for (var i = 0; i < changesets.Count; i++)
        {
            var nodeHeight = pickHeight(changesets[i].Id);
            if (nodeHeight is < 1 or > MaxLevel)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(heightOf), nodeHeight, $"Height for {changesets[i].Id} must be within [1, {MaxLevel}].");
            }

            heights[i] = nodeHeight;
        }

        var order = TopologicalOrder(parentIndices, changesets);

        // The frontier of nearest ancestors taller than each level — per node, per level. Levels
        // at or above the snapshot's tallest height have an empty frontier for every node.
        var topLevel = 0;
        foreach (var nodeHeight in heights)
        {
            topLevel = Math.Max(topLevel, nodeHeight);
        }

        var nodes = new Dictionary<string, SkipGraphNode>(changesets.Count, StringComparer.Ordinal);
        var frontiers = new ImmutableArray<string>[changesets.Count][];
        foreach (var i in order)
        {
            var frontier = new ImmutableArray<string>[topLevel];
            for (var level = 0; level < topLevel; level++)
            {
                frontier[level] = NearestTallAncestors(parentIndices[i], frontiers, heights, changesets, level);
            }

            frontiers[i] = frontier;
            nodes[changesets[i].Id] = new SkipGraphNode(
                changesets[i].Id, heights[i], frontier.Take(heights[i]).ToImmutableArray());
        }

        return new SkipGraph(nodes);
    }

    /// <summary>The skip-graph node for a changeset id.</summary>
    /// <exception cref="KeyNotFoundException">The id is not part of this graph.</exception>
    public SkipGraphNode Node(string id) => Resolve(id);

    /// <summary>
    /// All strict ancestors of the changeset — identical as a set to a walk over the raw parent
    /// edges, but walked over the towers — in deterministic breadth-first order, nearest
    /// generations first.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The id is not part of this graph.</exception>
    public IReadOnlyList<string> Ancestors(string id) => TowerBfs(id);

    /// <summary>
    /// Whether <paramref name="candidate"/> is a strict ancestor of <paramref name="descendant"/> —
    /// a changeset is not its own ancestor. The walk leaves early once the candidate is seen.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Either id is not part of this graph.</exception>
    public bool IsAncestor(string candidate, string descendant)
    {
        Resolve(candidate);
        Resolve(descendant);
        return candidate != descendant && Reachable(descendant, candidate);
    }

    /// <summary>
    /// All maximal common ancestors of two changesets: the fork point for trunk-style history,
    /// several for a criss-cross DAG, none for unrelated roots. Each changeset counts as its own
    /// base against a descendant (a is the base of a and any descendant of a). Exact on any DAG:
    /// the candidate set is the intersection of two tower walks, and maximality filters
    /// candidates that are strict ancestors of another candidate, so the cost is linear in the
    /// number of common ancestors with tower-minimal walks.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Either id is not part of this graph.</exception>
    public ImmutableArray<string> MergeBase(string first, string second)
    {
        Resolve(first);
        Resolve(second);

        var secondSide = TowerBfs(second);
        secondSide.Add(second);
        var secondSet = secondSide.ToHashSet(StringComparer.Ordinal);

        var candidates = new List<string>();
        if (secondSet.Contains(first))
        {
            candidates.Add(first);
        }

        foreach (var ancestor in TowerBfs(first))
        {
            if (secondSet.Contains(ancestor))
            {
                candidates.Add(ancestor);
            }
        }

        var ancestorsOf = new Dictionary<string, HashSet<string>>(candidates.Count, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            ancestorsOf[candidate] = TowerBfs(candidate).ToHashSet(StringComparer.Ordinal);
        }

        return candidates
            .Where(candidate => !candidates.Any(other => other != candidate && ancestorsOf[other].Contains(candidate)))
            .ToImmutableArray();
    }

    /// <summary>
    /// Level-ℓ right-hand pointers for one changeset: for each parent chain, the nearest ancestor
    /// with height above the level — the parent itself when it is tall enough, otherwise the
    /// parent's own level-ℓ frontier. Chains are walked first-parent first and duplicates
    /// collapse on first sight.
    /// </summary>
    private static ImmutableArray<string> NearestTallAncestors(
        int[] parentIndices,
        ImmutableArray<string>[][] frontiers,
        int[] heights,
        IReadOnlyList<Changeset> changesets,
        int level)
    {
        var targets = new List<string>(parentIndices.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parent in parentIndices)
        {
            if (heights[parent] > level)
            {
                if (seen.Add(changesets[parent].Id))
                {
                    targets.Add(changesets[parent].Id);
                }

                continue;
            }

            foreach (var inherited in frontiers[parent][level])
            {
                if (seen.Add(inherited))
                {
                    targets.Add(inherited);
                }
            }
        }

        return [.. targets];
    }

    /// <summary>Parents-before-children order, stable on input order; rejects cycles including self-parents.</summary>
    private static List<int> TopologicalOrder(int[][] parentIndices, IReadOnlyList<Changeset> changesets)
    {
        var children = new List<int>[parentIndices.Length];
        var inDegree = new int[parentIndices.Length];
        for (var i = 0; i < parentIndices.Length; i++)
        {
            children[i] = [];
            inDegree[i] = parentIndices[i].Length;
        }

        // Second pass: a parent may be listed after its child in the input, so every bucket must
        // exist before edges are wired.
        for (var i = 0; i < parentIndices.Length; i++)
        {
            foreach (var parent in parentIndices[i])
            {
                children[parent].Add(i);
            }
        }

        var ready = new Queue<int>();
        for (var i = 0; i < parentIndices.Length; i++)
        {
            if (inDegree[i] == 0)
            {
                ready.Enqueue(i);
            }
        }

        var order = new List<int>(parentIndices.Length);
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            order.Add(current);
            foreach (var child in children[current])
            {
                inDegree[child]--;
                if (inDegree[child] == 0)
                {
                    ready.Enqueue(child);
                }
            }
        }

        if (order.Count != parentIndices.Length)
        {
            throw new ArgumentException("The changeset parent graph contains a cycle.", nameof(changesets));
        }

        return order;
    }

    private SkipGraphNode Resolve(string id)
    {
        if (!_nodes.TryGetValue(id, out var node))
        {
            throw new KeyNotFoundException($"Changeset {id} is not part of this skip graph.");
        }

        return node;
    }

    private List<string> TowerBfs(string root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { root };
        var order = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = Resolve(queue.Dequeue());
            for (var level = 0; level < node.Height; level++)
            {
                foreach (var pointer in node.RightPointers[level])
                {
                    if (!seen.Add(pointer))
                    {
                        continue;
                    }

                    order.Add(pointer);
                    queue.Enqueue(pointer);
                }
            }
        }

        return order;
    }

    private bool Reachable(string from, string sought)
    {
        if (from == sought)
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var node = Resolve(queue.Dequeue());
            for (var level = 0; level < node.Height; level++)
            {
                foreach (var pointer in node.RightPointers[level])
                {
                    if (pointer == sought)
                    {
                        return true;
                    }

                    if (seen.Add(pointer))
                    {
                        queue.Enqueue(pointer);
                    }
                }
            }
        }

        return false;
    }
}
