namespace Scion.Core.Tests;

public class MergeBasePolicyTests
{
    [Fact]
    public void Select_OnLinearHistory_ReturnsTheCommonBase()
    {
        var chain = SkipGraphTestDag.Chain(5);
        var graph = SkipGraph.Build(chain);

        Assert.Equal(chain[2].Id, MergeBasePolicy.Select(graph, chain[4].Id, chain[2].Id));
        Assert.Equal(chain[2].Id, MergeBasePolicy.Select(graph, chain[2].Id, chain[4].Id));
    }

    [Fact]
    public void Select_WhenOneTipDescendsFromTheOther_ReturnsTheAncestor()
    {
        var changesets = SkipGraphTestDag.Diamond();
        var graph = SkipGraph.Build(changesets);
        var chain = SkipGraphTestDag.Chain(3);
        var chainGraph = SkipGraph.Build(chain);

        Assert.Equal(changesets[1].Id, MergeBasePolicy.Select(graph, changesets[3].Id, changesets[1].Id));
        Assert.Equal(chain[2].Id, MergeBasePolicy.Select(chainGraph, chain[2].Id, chain[2].Id));
    }

    [Fact]
    public void Select_OnDiamond_ReturnsTheForkPoint()
    {
        var changesets = SkipGraphTestDag.Diamond();
        var graph = SkipGraph.Build(changesets);

        Assert.Equal(changesets[0].Id, MergeBasePolicy.Select(graph, changesets[1].Id, changesets[2].Id));
    }

    [Fact]
    public void Select_OnDisjointHistories_ReturnsNull()
    {
        var changesets = SkipGraphTestDag.TwoRoots();
        var graph = SkipGraph.Build(changesets);

        Assert.Null(MergeBasePolicy.Select(graph, changesets[1].Id, changesets[3].Id));
    }

    [Fact]
    public void Select_OnSymmetricCrissCross_TiesBreakOnTheSmallerId()
    {
        var changesets = SkipGraphTestDag.CrissCross();
        var graph = SkipGraph.Build(changesets);
        var a = changesets[1].Id;
        var b = changesets[2].Id;
        var expected = string.CompareOrdinal(a, b) < 0 ? a : b;

        Assert.Equal(2, graph.MergeBase(changesets[3].Id, changesets[4].Id).Length);
        Assert.Equal(expected, MergeBasePolicy.Select(graph, changesets[3].Id, changesets[4].Id));
        Assert.Equal(expected, MergeBasePolicy.Select(graph, changesets[4].Id, changesets[3].Id));
    }

    [Fact]
    public void Select_OnThreeBaseCrissCross_PicksTheBaseDominatingTheLeastHistory()
    {
        var changesets = ThreeBaseCrissCross();
        var graph = SkipGraph.Build(changesets);
        SkipGraphTestDag.AssertInvariants(graph, changesets);

        // Precondition: p and q share three maximal ancestors — c, g, and d.
        SkipGraphTestDag.AssertSameElements(
            new[] { changesets[2].Id, changesets[3].Id, changesets[4].Id },
            graph.MergeBase(changesets[7].Id, changesets[8].Id).ToArray());

        // g is a direct parent of both tips, so nothing in the shared ancestry sits below it;
        // c and d each dominate the e/f merge pair. The policy picks g in both argument orders.
        Assert.Equal(changesets[3].Id, MergeBasePolicy.Select(graph, changesets[7].Id, changesets[8].Id));
        Assert.Equal(changesets[3].Id, MergeBasePolicy.Select(graph, changesets[8].Id, changesets[7].Id));
    }

    [Fact]
    public void Select_StaysWithinTheMaximalBases_AndIsSymmetric_OnRandomDags()
    {
        foreach (var seed in new[] { 2_026, 7, 42 })
        {
            var changesets = SkipGraphTestDag.RandomDag(new Random(seed), 40);
            var graph = SkipGraph.Build(changesets);

            for (var pair = 0; pair < 20; pair++)
            {
                var first = changesets[new Random(seed * 100 + pair).Next(changesets.Count)].Id;
                var second = changesets[new Random(seed * 1000 + pair).Next(changesets.Count)].Id;
                var bases = graph.MergeBase(first, second);
                var best = MergeBasePolicy.Select(graph, first, second);

                if (bases.IsEmpty)
                {
                    Assert.Null(best);
                }
                else
                {
                    Assert.Contains(best, bases.ToArray());
                }

                Assert.Equal(best, MergeBasePolicy.Select(graph, second, first));
            }
        }
    }

    [Fact]
    public void Select_UnknownId_Throws()
    {
        var graph = SkipGraph.Build(SkipGraphTestDag.Chain(2));

        Assert.Throws<KeyNotFoundException>(() => MergeBasePolicy.Select(graph, "anything", "anything-else"));
    }

    /// <summary>
    /// An asymmetric criss-cross leaving three maximal common ancestors for tips p and q —
    /// c, g, and d. Indices: [root, b, c, g, d, e, f, p, q]. The c/d criss-cross merges e and f,
    /// while g hangs off b and is a direct second parent of both tips.
    /// </summary>
    private static List<Changeset> ThreeBaseCrissCross()
    {
        var root = SkipGraphTestDag.Cs("root");
        var b = SkipGraphTestDag.Cs("b", root);
        var c = SkipGraphTestDag.Cs("c", b);
        var g = SkipGraphTestDag.Cs("g", b);
        var d = SkipGraphTestDag.Cs("d", root);
        var e = SkipGraphTestDag.Cs("e", c, d);
        var f = SkipGraphTestDag.Cs("f", d, c);
        var p = SkipGraphTestDag.Cs("p", e, g);
        var q = SkipGraphTestDag.Cs("q", f, g);
        return [root, b, c, g, d, e, f, p, q];
    }
}
