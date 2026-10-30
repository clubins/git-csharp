using Microsoft.Data.Sqlite;

using Scion.Core.Derived;
using Scion.Core.Storage;

namespace Scion.Core.Tests;

/// <summary>
/// Contract tests for the "skip-graph" derived type through the engine: the type is deferred —
/// the sweep leaves it alone and the first query computes and persists — and every derived node
/// equals the node <see cref="SkipGraph.Build"/> holds for the same DAG.
/// </summary>
public class SkipGraphDerivedDataTests
{
    [Fact]
    public void Name_IsTheSpecKey_AndTypeIsDeferred()
    {
        var type = new SkipGraphDerivedData();

        Assert.Equal("skip-graph", SkipGraphDerivedData.TypeName);
        Assert.Equal(SkipGraphDerivedData.TypeName, type.Name);
        Assert.False(type.MaterializeEagerly);
    }

    [Fact]
    public void DeferredType_ComputesOnFirstQuery()
    {
        using var db = new TempDatabase();
        var dag = SkipGraphTestDag.Diamond();
        new ChangesetLog(db.DatabasePath).AppendRange(dag);
        var engine = Engine(db, new SkipGraphDerivedData());
        var store = new DerivedDataStore(db.DatabasePath);

        // Deferred: the sweep leaves the type alone, and nothing exists before the first query.
        Assert.Equal(0, engine.MaterializeEager());
        Assert.Empty(store.StoredChangesetIds(SkipGraphDerivedData.TypeName));

        // The first query computes the queried tower together with its unmaterialized ancestry —
        // for the diamond tip, the whole DAG — and persists every value before answering.
        var tip = dag[^1];
        var value = engine.Get(SkipGraphDerivedData.TypeName, tip.Id);

        Assert.Equal(
            dag.Select(changeset => changeset.Id).OrderBy(id => id, StringComparer.Ordinal),
            store.StoredChangesetIds(SkipGraphDerivedData.TypeName).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(SkipGraph.Build(dag).Node(tip.Id), SkipGraphDerivedData.DecodeNode(value, tip.Id));

        // Repeats — and a fresh engine over the same database — read the store, never recompute.
        Assert.Equal(value, engine.Get(SkipGraphDerivedData.TypeName, tip.Id));
        Assert.Equal(value, Engine(db, new SkipGraphDerivedData()).Get(SkipGraphDerivedData.TypeName, tip.Id));
    }

    [Fact]
    public void NodeValues_MatchBuild_OverTheStructuralDags()
    {
        AssertNodeValuesMatchBuild(SkipGraphTestDag.Diamond());
        AssertNodeValuesMatchBuild(SkipGraphTestDag.CrissCross());
        AssertNodeValuesMatchBuild(SkipGraphTestDag.TwoRoots());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(64)]
    public void NodeValues_MatchBuild_OverLinearChains(int count)
    {
        AssertNodeValuesMatchBuild(SkipGraphTestDag.Chain(count));
    }

    [Theory]
    [InlineData(1_790_001, 40)]
    [InlineData(42, 80)]
    public void NodeValues_MatchBuild_OverRandomDags(int seed, int nodeCount)
    {
        AssertNodeValuesMatchBuild(SkipGraphTestDag.RandomDag(new Random(seed), nodeCount));
    }

    [Fact]
    public void WithTheTreeSnapshotType_TypesStayIndependent_OverOneLog()
    {
        using var db = new TempDatabase();
        var dag = SkipGraphTestDag.Diamond();
        new ChangesetLog(db.DatabasePath).AppendRange(dag);
        var engine = Engine(db, new TreeSnapshotDerivedData(), new SkipGraphDerivedData());
        var store = new DerivedDataStore(db.DatabasePath);

        // The sweep fills the eager type and leaves the deferred one alone.
        Assert.Equal(dag.Count, engine.MaterializeEager());
        Assert.Equal(dag.Count, store.StoredChangesetIds(TreeSnapshotDerivedData.TypeName).Count);
        Assert.Empty(store.StoredChangesetIds(SkipGraphDerivedData.TypeName));

        // Both types answer over the same log under their own keys; the deferred one computes its
        // first value only when asked.
        var tip = dag[^1];
        Assert.Equal(
            TreeSnapshot.FromChain([dag[0], dag[1], dag[3]]),
            TreeSnapshotDerivedData.Decode(engine.Get(TreeSnapshotDerivedData.TypeName, tip.Id)));
        Assert.Empty(store.StoredChangesetIds(SkipGraphDerivedData.TypeName));

        var value = engine.Get(SkipGraphDerivedData.TypeName, tip.Id);
        Assert.Equal(dag.Count, store.StoredChangesetIds(SkipGraphDerivedData.TypeName).Count);
        Assert.Equal(SkipGraph.Build(dag).Node(tip.Id), SkipGraphDerivedData.DecodeNode(value, tip.Id));
    }

    [Fact]
    public void DecodeNode_MalformedPayloads_ThrowInvalidData()
    {
        // Wrong version byte.
        Assert.Throws<InvalidDataException>(() => Decode([0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01]));
        // Height zero.
        Assert.Throws<InvalidDataException>(() => Decode([0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]));
        // Height beyond MaxLevel (65).
        Assert.Throws<InvalidDataException>(() => Decode([0x01, 0x00, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00, 0x41]));
        // Frontier shorter than the height.
        Assert.Throws<InvalidDataException>(() => Decode([0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]));
        // Truncated: a height with no frontier length behind it.
        Assert.Throws<InvalidDataException>(() => Decode([0x01, 0x00, 0x00, 0x00, 0x01]));
        // Trailing bytes after a complete encoding.
        Assert.Throws<InvalidDataException>(() => Decode([0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0xFF]));
    }

    private static void AssertNodeValuesMatchBuild(IReadOnlyList<Changeset> dag)
    {
        using var db = new TempDatabase();
        new ChangesetLog(db.DatabasePath).AppendRange(dag);
        var engine = Engine(db, new SkipGraphDerivedData());
        var built = SkipGraph.Build(dag);

        foreach (var changeset in dag)
        {
            Assert.Equal(
                built.Node(changeset.Id),
                SkipGraphDerivedData.DecodeNode(engine.Get(SkipGraphDerivedData.TypeName, changeset.Id), changeset.Id));
        }
    }

    private static SkipGraphNode Decode(byte[] payload)
    {
        return SkipGraphDerivedData.DecodeNode(DerivedDataValue.FromPayload(payload), "some-changeset");
    }

    /// <summary>An engine over fresh log and store instances on one temporary database.</summary>
    private static DerivedDataEngine Engine(TempDatabase db, params IDerivedData[] types)
    {
        return new DerivedDataEngine(
            new ChangesetLog(db.DatabasePath),
            new DerivedDataStore(db.DatabasePath),
            types);
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string directory;

        public TempDatabase()
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                "scion-skip-graph-derived-tests-" + Guid.NewGuid().ToString("N"));
            DatabasePath = Path.Combine(directory, "scion.db");
            Directory.CreateDirectory(directory);
        }

        public string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
