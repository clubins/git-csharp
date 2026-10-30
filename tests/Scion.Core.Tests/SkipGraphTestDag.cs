using System.Collections.Immutable;

namespace Scion.Core.Tests;

/// <summary>
/// Builders for the DAG families the skip-graph tests walk, plus brute-force reference queries
/// over raw parent edges that the tower-based answers are checked against.
/// </summary>
internal static class SkipGraphTestDag
{
    internal static readonly Author Author = new("Patrick", "patrick@clubins.de");

    internal const long Timestamp = 1_790_000_000;

    /// <summary>Builds a changeset with a distinct message (hence a distinct id) and the given parents.</summary>
    internal static Changeset Cs(string message, params Changeset[] parents)
    {
        return Changeset.Create(parents.Select(parent => parent.Id).ToImmutableArray(), [], message, Author, Timestamp);
    }

    /// <summary>A first-parent chain; index 0 is the root, the last entry the tip.</summary>
    internal static List<Changeset> Chain(int count)
    {
        var changesets = new List<Changeset>(count);
        for (var i = 1; i <= count; i++)
        {
            Changeset[] parents = changesets.Count == 0 ? [] : [changesets[^1]];
            changesets.Add(Cs($"c{i}", parents));
        }

        return changesets;
    }

    /// <summary>Root, two children, and their merge — indices [root, left, right, merge].</summary>
    internal static List<Changeset> Diamond()
    {
        var root = Cs("root");
        var left = Cs("left", root);
        var right = Cs("right", root);
        var merge = Cs("merge", left, right);
        return [root, left, right, merge];
    }

    /// <summary>Two branches merging in both orders — indices [root, a, b, m1(a,b), m2(b,a)].</summary>
    internal static List<Changeset> CrissCross()
    {
        var root = Cs("root");
        var a = Cs("a", root);
        var b = Cs("b", root);
        var m1 = Cs("m1", a, b);
        var m2 = Cs("m2", b, a);
        return [root, a, b, m1, m2];
    }

    /// <summary>Two unrelated root histories — indices [root1, tip1, root2, tip2].</summary>
    internal static List<Changeset> TwoRoots()
    {
        var firstRoot = Cs("root-1");
        var firstTip = Cs("tip-1", firstRoot);
        var secondRoot = Cs("root-2");
        var secondTip = Cs("tip-2", secondRoot);
        return [firstRoot, firstTip, secondRoot, secondTip];
    }

    /// <summary>
    /// Random changeset DAG with trunk-style shapes: first parents biased toward recent
    /// changesets, ~20% merges whose second parent may reach anywhere earlier, ~12% new roots
    /// (multi-root histories).
    /// </summary>
    internal static List<Changeset> RandomDag(Random random, int nodeCount)
    {
        var changesets = new List<Changeset>(nodeCount);
        for (var i = 0; i < nodeCount; i++)
        {
            if (changesets.Count == 0 || random.Next(8) == 0)
            {
                changesets.Add(Cs($"n{i}"));
                continue;
            }

            var window = Math.Min(changesets.Count, 1 + random.Next(8));
            var first = changesets[changesets.Count - random.Next(1, window + 1)];
            Changeset? second = null;
            if (changesets.Count > 1 && random.Next(5) == 0)
            {
                do
                {
                    second = changesets[random.Next(changesets.Count)];
                }
                while (second == first);
            }

            Changeset[] parents = second is null ? [first] : [first, second];
            changesets.Add(Cs($"n{i}", parents));
        }

        return changesets;
    }

    internal static Dictionary<string, string[]> ParentMap(IReadOnlyList<Changeset> changesets)
    {
        return changesets.ToDictionary(changeset => changeset.Id, changeset => changeset.Parents.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Reference strict-ancestor set by breadth-first walk over raw parent edges.</summary>
    internal static HashSet<string> StrictAncestors(Dictionary<string, string[]> parents, string id)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var parent in parents[id])
        {
            queue.Enqueue(parent);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (var parent in parents[current])
            {
                queue.Enqueue(parent);
            }
        }

        return seen;
    }

    /// <summary>
    /// Whether a parent-edge path from the node to the target exists that passes only through
    /// nodes of height at most the level — the skip the pointer claims to make.
    /// </summary>
    internal static bool SkipPathExists(Dictionary<string, string[]> parents, Func<string, int> heightOf, string from, string target, int level)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>();
        foreach (var parent in parents[from])
        {
            queue.Enqueue(parent);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == target)
            {
                return true;
            }

            if (!seen.Add(current))
            {
                continue;
            }

            if (heightOf(current) > level)
            {
                continue;
            }

            foreach (var parent in parents[current])
            {
                queue.Enqueue(parent);
            }
        }

        return false;
    }

    /// <summary>
    /// Reference merge-base by brute force over raw parent edges: the maximal common ancestors,
    /// each side inclusive of itself.
    /// </summary>
    internal static List<string> MergeBaseReference(Dictionary<string, string[]> parents, string first, string second)
    {
        var firstSide = StrictAncestors(parents, first);
        var secondSide = new HashSet<string>(StrictAncestors(parents, second), StringComparer.Ordinal) { second };

        var candidates = firstSide.Where(secondSide.Contains).ToList();
        if (secondSide.Contains(first))
        {
            candidates.Add(first);
        }

        var ancestorSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!ancestorSets.ContainsKey(candidate))
            {
                ancestorSets[candidate] = StrictAncestors(parents, candidate);
            }
        }

        return candidates
            .Where(candidate => !candidates.Any(other => other != candidate && ancestorSets[other].Contains(candidate)))
            .ToList();
    }

    /// <summary>
    /// The three structural invariants, against raw parent edges: tower reachability equals
    /// parent reachability; every pointer targets a true ancestor taller than its level; every
    /// pointer skips only nodes at or below its level.
    /// </summary>
    internal static void AssertInvariants(SkipGraph graph, IReadOnlyList<Changeset> changesets)
    {
        var parents = ParentMap(changesets);
        var heightOf = (string changesetId) => graph.Node(changesetId).Height;

        foreach (var changeset in changesets)
        {
            var id = changeset.Id;
            AssertSameElements(StrictAncestors(parents, id), graph.Ancestors(id));

            var node = graph.Node(id);
            var ancestors = StrictAncestors(parents, id);
            for (var level = 0; level < node.Height; level++)
            {
                foreach (var target in node.RightPointers[level])
                {
                    Assert.True(ancestors.Contains(target), $"Pointer target {Short(target)} of {Short(id)} at level {level} is not an ancestor.");
                    Assert.True(heightOf(target) > level, $"Pointer target {Short(target)} of {Short(id)} at level {level} is not taller than the level.");
                    Assert.True(
                        SkipPathExists(parents, heightOf, id, target, level),
                        $"Pointer {Short(id)} → {Short(target)} at level {level} passes through a node taller than the level.");
                }
            }
        }
    }

    internal static void AssertMergeBase(SkipGraph graph, Dictionary<string, string[]> parents, string first, string second)
    {
        AssertSameElements(MergeBaseReference(parents, first, second), graph.MergeBase(first, second).ToArray());
    }

    internal static void AssertSameElements(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        Assert.Equal(
            expected.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    private static string Short(string id)
    {
        return id.Length <= 12 ? id : id[..12] + "…";
    }
}
