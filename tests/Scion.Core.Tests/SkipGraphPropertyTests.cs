namespace Scion.Core.Tests;

/// <summary>
/// Randomized property tests over seeded random DAGs: the tower answers must match brute-force
/// walks over raw parent edges. Seeds are fixed (approval date + graph size) so any failure is
/// reproducible.
/// </summary>
public class SkipGraphPropertyTests
{
    private const int Seed = 20261030;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(40)]
    [InlineData(300)]
    public void RandomDag_PointerInvariantsHold(int nodeCount)
    {
        var random = new Random(Seed + nodeCount);
        var changesets = SkipGraphTestDag.RandomDag(random, nodeCount);

        // Both input orders: the build must not depend on parents preceding children in the list.
        SkipGraphTestDag.AssertInvariants(SkipGraph.Build(changesets), changesets);
        var reversed = changesets.AsEnumerable().Reverse().ToList();
        SkipGraphTestDag.AssertInvariants(SkipGraph.Build(reversed), reversed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(40)]
    [InlineData(300)]
    public void RandomDag_MergeBaseMatchesBruteForce(int nodeCount)
    {
        var random = new Random(Seed + nodeCount);
        var changesets = SkipGraphTestDag.RandomDag(random, nodeCount);
        var graph = SkipGraph.Build(changesets.AsEnumerable().Reverse().ToList());
        var parents = SkipGraphTestDag.ParentMap(changesets);

        if (nodeCount <= 6)
        {
            foreach (var first in changesets)
            {
                foreach (var second in changesets)
                {
                    SkipGraphTestDag.AssertMergeBase(graph, parents, first.Id, second.Id);
                }
            }

            return;
        }

        for (var trial = 0; trial < 40; trial++)
        {
            var first = changesets[random.Next(nodeCount)].Id;
            var second = trial % 5 == 0 ? first : changesets[random.Next(nodeCount)].Id;
            SkipGraphTestDag.AssertMergeBase(graph, parents, first, second);
        }
    }

    [Fact]
    public void RandomDag_RebuildIsDeterministic()
    {
        var changesets = SkipGraphTestDag.RandomDag(new Random(Seed), 60);
        var first = SkipGraph.Build(changesets);
        var second = SkipGraph.Build(changesets);

        foreach (var changeset in changesets)
        {
            Assert.Equal(first.Node(changeset.Id), second.Node(changeset.Id));
        }
    }
}
