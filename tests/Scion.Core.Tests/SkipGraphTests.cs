namespace Scion.Core.Tests;

public class SkipGraphTests
{
    private const string MissingId = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    [Fact]
    public void Build_LinearChain_LevelZeroMirrorsParents()
    {
        var chain = SkipGraphTestDag.Chain(3);
        var graph = SkipGraph.Build(chain);

        var root = graph.Node(chain[0].Id);
        Assert.Empty(root.RightPointers[0]);

        var tip = graph.Node(chain[2].Id);
        Assert.Equal(new[] { chain[1].Id }, tip.RightPointers[0].ToArray());
        Assert.InRange(tip.Height, 1, SkipGraph.MaxLevel);
    }

    [Fact]
    public void Build_MergeNode_LevelZeroListsFirstParentFirst()
    {
        var changesets = SkipGraphTestDag.Diamond();
        var graph = SkipGraph.Build(changesets);

        var merge = graph.Node(changesets[3].Id);

        Assert.Equal(new[] { changesets[1].Id, changesets[2].Id }, merge.RightPointers[0].ToArray());
    }

    [Fact]
    public void Build_InvariantsHold_OnHandBuiltFamilies()
    {
        foreach (var family in new[] { SkipGraphTestDag.Diamond(), SkipGraphTestDag.CrissCross(), SkipGraphTestDag.TwoRoots(), SkipGraphTestDag.Chain(9) })
        {
            SkipGraphTestDag.AssertInvariants(SkipGraph.Build(family), family);
        }
    }

    [Fact]
    public void Build_RejectsUnknownParentReference()
    {
        var orphan = Changeset.Create([MissingId], [], "orphan", SkipGraphTestDag.Author, SkipGraphTestDag.Timestamp);

        Assert.Throws<ArgumentException>(() => SkipGraph.Build([orphan]));
    }

    [Fact]
    public void Build_RejectsDuplicateIds()
    {
        var changeset = SkipGraphTestDag.Cs("dup");

        Assert.Throws<ArgumentException>(() => SkipGraph.Build([changeset, changeset]));
    }

    [Fact]
    public void Build_RejectsCycles()
    {
        var first = SkipGraphTestDag.Cs("first");
        var second = SkipGraphTestDag.Cs("second");
        var firstToSecond = first with { Parents = [second.Id] };
        var secondToFirst = second with { Parents = [firstToSecond.Id] };

        Assert.Throws<ArgumentException>(() => SkipGraph.Build([firstToSecond, secondToFirst]));
    }

    [Fact]
    public void Build_RejectsSelfParent()
    {
        var changeset = SkipGraphTestDag.Cs("self");
        var selfParent = changeset with { Parents = [changeset.Id] };

        Assert.Throws<ArgumentException>(() => SkipGraph.Build([selfParent]));
    }

    [Fact]
    public void Build_RejectsMoreThanTwoParents()
    {
        var first = SkipGraphTestDag.Cs("p1");
        var second = SkipGraphTestDag.Cs("p2");
        var third = SkipGraphTestDag.Cs("p3");
        var child = first with { Parents = [first.Id, second.Id, third.Id] };

        Assert.Throws<ArgumentException>(() => SkipGraph.Build([child]));
    }

    [Fact]
    public void Build_HeightOutsideRange_IsRejected()
    {
        var changeset = SkipGraphTestDag.Cs("solo");

        Assert.Throws<ArgumentOutOfRangeException>(() => SkipGraph.Build([changeset], _ => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SkipGraph.Build([changeset], _ => SkipGraph.MaxLevel + 1));
    }

    [Fact]
    public void Build_EmptySnapshot_YieldsEmptyGraph()
    {
        var graph = SkipGraph.Build([]);

        Assert.Equal(0, graph.Count);
        Assert.Throws<KeyNotFoundException>(() => graph.Node("anything"));
    }

    [Fact]
    public void Build_IsDeterministicForSameInput()
    {
        var changesets = SkipGraphTestDag.CrissCross();
        var first = SkipGraph.Build(changesets);
        var second = SkipGraph.Build(changesets);

        foreach (var changeset in changesets)
        {
            Assert.Equal(first.Node(changeset.Id), second.Node(changeset.Id));
        }
    }

    [Fact]
    public void Node_UnknownId_Throws()
    {
        var graph = SkipGraph.Build(SkipGraphTestDag.Chain(2));

        Assert.Throws<KeyNotFoundException>(() => graph.Node("anything"));
    }

    [Fact]
    public void Ancestors_OnLinearHistory_OrdersNearestFirst()
    {
        var chain = SkipGraphTestDag.Chain(3);
        var graph = SkipGraph.Build(chain);

        Assert.Empty(graph.Ancestors(chain[0].Id));
        Assert.Equal(new[] { chain[1].Id, chain[0].Id }, graph.Ancestors(chain[2].Id).ToArray());
    }

    [Fact]
    public void Ancestors_OnDiamond_ExcludesSelfAndIncludesAllAncestors()
    {
        var changesets = SkipGraphTestDag.Diamond();
        var graph = SkipGraph.Build(changesets);

        SkipGraphTestDag.AssertSameElements(
            new[] { changesets[0].Id, changesets[1].Id, changesets[2].Id },
            graph.Ancestors(changesets[3].Id));
    }

    [Fact]
    public void IsAncestor_IsStrictAndDirectional()
    {
        var changesets = SkipGraphTestDag.Diamond();
        var graph = SkipGraph.Build(changesets);
        var root = changesets[0].Id;
        var left = changesets[1].Id;
        var right = changesets[2].Id;
        var merge = changesets[3].Id;

        Assert.True(graph.IsAncestor(root, merge));
        Assert.True(graph.IsAncestor(root, left));
        Assert.True(graph.IsAncestor(left, merge));
        Assert.False(graph.IsAncestor(merge, left));
        Assert.False(graph.IsAncestor(merge, merge));
        Assert.False(graph.IsAncestor(left, right));
        Assert.Throws<KeyNotFoundException>(() => graph.IsAncestor("anything", merge));
    }

    [Fact]
    public void MergeBase_OnLinearHistory_ReturnsOlderChangeset()
    {
        var chain = SkipGraphTestDag.Chain(5);
        var graph = SkipGraph.Build(chain);

        Assert.Equal(new[] { chain[2].Id }, graph.MergeBase(chain[4].Id, chain[2].Id).ToArray());
        Assert.Equal(new[] { chain[2].Id }, graph.MergeBase(chain[2].Id, chain[4].Id).ToArray());
        Assert.Equal(new[] { chain[4].Id }, graph.MergeBase(chain[4].Id, chain[4].Id).ToArray());
    }

    [Fact]
    public void MergeBase_OnDiamond_ReturnsForkPoint()
    {
        var changesets = SkipGraphTestDag.Diamond();
        var graph = SkipGraph.Build(changesets);

        Assert.Equal(new[] { changesets[0].Id }, graph.MergeBase(changesets[1].Id, changesets[2].Id).ToArray());
        Assert.Equal(new[] { changesets[1].Id }, graph.MergeBase(changesets[3].Id, changesets[1].Id).ToArray());
        Assert.Equal(new[] { changesets[2].Id }, graph.MergeBase(changesets[3].Id, changesets[2].Id).ToArray());
        Assert.Equal(new[] { changesets[0].Id }, graph.MergeBase(changesets[3].Id, changesets[0].Id).ToArray());
    }

    [Fact]
    public void MergeBase_OnCrissCross_ReturnsBothMaximalBases()
    {
        var changesets = SkipGraphTestDag.CrissCross();
        var graph = SkipGraph.Build(changesets);

        SkipGraphTestDag.AssertSameElements(
            new[] { changesets[1].Id, changesets[2].Id },
            graph.MergeBase(changesets[3].Id, changesets[4].Id));
    }

    [Fact]
    public void MergeBase_UnrelatedRoots_ReturnsEmpty()
    {
        var changesets = SkipGraphTestDag.TwoRoots();
        var graph = SkipGraph.Build(changesets);

        Assert.Empty(graph.MergeBase(changesets[1].Id, changesets[3].Id).ToArray());
    }

    [Fact]
    public void TowerWalk_OnLinearHistory_TakesLogarithmicHops()
    {
        const int count = 50_000;
        var chain = SkipGraphTestDag.Chain(count);
        var graph = SkipGraph.Build(chain);
        var current = chain[^1].Id;

        var hops = 0;
        while (true)
        {
            var node = graph.Node(current);
            var jumped = false;
            for (var level = node.Height - 1; level >= 0; level--)
            {
                if (node.RightPointers[level].Length == 0)
                {
                    continue;
                }

                current = node.RightPointers[level][0];
                jumped = true;
                break;
            }

            if (!jumped)
            {
                break;
            }

            hops++;
        }

        Assert.True(hops <= 4 * Math.Log2(count) + 20, $"Root walk took {hops} hops for {count} changesets; expected logarithmic.");
    }
}
