using System.Collections.Immutable;

using Microsoft.Data.Sqlite;

using Scion.Core.Derived;
using Scion.Core.Storage;

namespace Scion.Core.Tests;

/// <summary>
/// The path-history derived type through the engine it registers with: values computed over
/// parent values, materialized eagerly by the sweep, persisted by content hash, and read back as
/// chains per path. Scenarios cover the task's required cases — linear chains, branched chains,
/// basic rename tracking, untouched paths — plus the merge lineage rule and purity.
/// </summary>
public class PathHistoryDerivedDataTests
{
    private const long Timestamp = 1_790_000_000;
    private const string BlobA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string BlobB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
    private const string BlobC = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";

    private static readonly Author Author = new("Patrick", "patrick@clubins.de");

    [Fact]
    public void Contract_IsRegisteredAsTheSpecTypeName_AndEager()
    {
        var data = new PathHistoryDerivedData();

        Assert.Equal("path-history", data.Name);
        Assert.True(data.MaterializeEagerly);
    }

    [Fact]
    public void Get_LinearChain_HistoryAccumulatesInLineageOrder()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("f.cs", BlobA));
        var first = ChildOf(root, "first", Modified("f.cs", BlobA, BlobB));
        var second = ChildOf(first, "second", Modified("f.cs", BlobB, BlobC));
        new ChangesetLog(db.DatabasePath).AppendRange([root, first, second]);
        var engine = Engine(db);

        Assert.Equal(new[] { root.Id }, HistoryOf(engine, root.Id, "f.cs").ToArray());
        Assert.Equal(new[] { root.Id, first.Id }, HistoryOf(engine, first.Id, "f.cs").ToArray());
        Assert.Equal(new[] { root.Id, first.Id, second.Id }, HistoryOf(engine, second.Id, "f.cs").ToArray());
    }

    [Fact]
    public void Get_BranchedChains_EachTipSeesItsOwnLineage()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("f.cs", BlobA), Added("g.cs", BlobB));
        var branchA = ChildOf(root, "branch-a", Modified("f.cs", BlobA, BlobC));
        var branchB = ChildOf(root, "branch-b", Modified("g.cs", BlobB, BlobC));
        new ChangesetLog(db.DatabasePath).AppendRange([root, branchA, branchB]);
        var engine = Engine(db);

        Assert.Equal(new[] { root.Id, branchA.Id }, HistoryOf(engine, branchA.Id, "f.cs").ToArray());
        Assert.Equal(new[] { root.Id }, HistoryOf(engine, branchA.Id, "g.cs").ToArray());
        Assert.Equal(new[] { root.Id, branchB.Id }, HistoryOf(engine, branchB.Id, "g.cs").ToArray());
        Assert.Equal(new[] { root.Id }, HistoryOf(engine, branchB.Id, "f.cs").ToArray());
    }

    [Fact]
    public void Get_Merge_FoldsTheFirstParentLineage()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("f.cs", BlobA), Added("g.cs", BlobB));
        var branchA = ChildOf(root, "branch-a", Modified("f.cs", BlobA, BlobC));
        var branchB = ChildOf(root, "branch-b", Modified("g.cs", BlobB, BlobC));
        var merge = Changeset.Create([branchA.Id, branchB.Id], [Added("h.cs", BlobC)], "merge", Author, Timestamp);
        new ChangesetLog(db.DatabasePath).AppendRange([root, branchA, branchB, merge]);
        var engine = Engine(db);

        // The tip's history answers what its trunk lineage did: the first parent's chain plus the
        // merge's own delta. The second parent's touch of g.cs stays queryable at branchB's own
        // changeset — the same first-parent rule the changeset model builds deltas on.
        Assert.Equal(new[] { root.Id, branchA.Id }, HistoryOf(engine, merge.Id, "f.cs").ToArray());
        Assert.Equal(new[] { root.Id }, HistoryOf(engine, merge.Id, "g.cs").ToArray());
        Assert.Equal(new[] { merge.Id }, HistoryOf(engine, merge.Id, "h.cs").ToArray());
        Assert.Equal(new[] { root.Id, branchB.Id }, HistoryOf(engine, branchB.Id, "g.cs").ToArray());
    }

    [Fact]
    public void Get_MergeWithNoOwnDelta_SharesTheParentValueByHash()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("f.cs", BlobA));
        var branchB = ChildOf(root, "branch-b", Modified("g.cs", BlobA, BlobB));
        var merge = Changeset.Create([root.Id, branchB.Id], [], "merge", Author, Timestamp);
        new ChangesetLog(db.DatabasePath).AppendRange([root, branchB, merge]);
        var engine = Engine(db);

        // Nothing to fold — the merge's value is byte-identical to its first parent's, so the
        // content-addressed store deduplicates it for free.
        Assert.Equal(engine.Get(PathHistoryDerivedData.TypeName, root.Id).Hash, engine.Get(PathHistoryDerivedData.TypeName, merge.Id).Hash);
    }

    [Fact]
    public void Get_RenamedFile_HistoryContinuesFromTheOldPath()
    {
        using var db = new TempDatabase();

        // The rename delta comes from the real producer — TreeDiff's convention (old path's
        // Deleted entry plus a Renamed entry on the new path) — not from hand-built entries.
        var root = Root("root", Added("src/a.cs", BlobA), Added("src/b.cs", BlobB));
        var before = TreeSnapshot.Empty.Apply(root.Changes);
        var after = TreeSnapshot.Empty.Apply([Added("src/b.cs", BlobB), Added("src/renamed.cs", BlobA)]);
        var move = ChildOf(root, "move", [.. TreeDiff.Diff(before, after)]);
        var edit = ChildOf(move, "edit", Modified("src/renamed.cs", BlobA, BlobC));
        new ChangesetLog(db.DatabasePath).AppendRange([root, move, edit]);
        var engine = Engine(db);

        Assert.Equal(new[] { root.Id, move.Id, edit.Id }, HistoryOf(engine, edit.Id, "src/renamed.cs").ToArray());
        Assert.Equal(new[] { root.Id, move.Id }, HistoryOf(engine, edit.Id, "src/a.cs").ToArray());
        Assert.Equal(new[] { root.Id }, HistoryOf(engine, edit.Id, "src/b.cs").ToArray());
    }

    [Fact]
    public void Get_UntouchedPath_ReturnsEmpty_AtEveryChangeset()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("f.cs", BlobA));
        var child = ChildOf(root, "child", Modified("f.cs", BlobA, BlobB));
        new ChangesetLog(db.DatabasePath).AppendRange([root, child]);
        var engine = Engine(db);

        Assert.Empty(HistoryOf(engine, root.Id, "untouched.cs"));
        Assert.Empty(HistoryOf(engine, child.Id, "untouched.cs"));
    }

    [Fact]
    public void Compute_IsPure_SameInputsProduceTheSameValue()
    {
        var root = Root("root", Added("f.cs", BlobA));
        var parentValue = DerivedDataValue.FromPayload(
            PathHistoryValue.Empty.Apply(root.Id, root.Changes).Serialize());
        var child = ChildOf(root, "child", Modified("f.cs", BlobA, BlobB));
        var data = new PathHistoryDerivedData();

        var first = data.Compute(child, [parentValue]);
        var second = data.Compute(child, [parentValue]);

        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.Payload.ToArray(), second.Payload.ToArray());
    }

    [Fact]
    public void MaterializeEager_FillsTheWholeLog_AndQueriesReadFromTheStore()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("f.cs", BlobA));
        var child = ChildOf(root, "child", Modified("f.cs", BlobA, BlobB));
        new ChangesetLog(db.DatabasePath).AppendRange([root, child]);
        var engine = Engine(db);

        Assert.Equal(2, engine.MaterializeEager());
        Assert.Equal(2, new DerivedDataStore(db.DatabasePath).StoredChangesetIds(PathHistoryDerivedData.TypeName).Count);

        // The sweep persists before serving — a point query reads back exactly what was stored.
        Assert.Equal(new[] { root.Id, child.Id }, HistoryOf(engine, child.Id, "f.cs").ToArray());
    }

    /// <summary>The engine with the path-history type registered over one temporary database.</summary>
    private static DerivedDataEngine Engine(TempDatabase db)
    {
        return new DerivedDataEngine(
            new ChangesetLog(db.DatabasePath),
            new DerivedDataStore(db.DatabasePath),
            new PathHistoryDerivedData());
    }

    /// <summary>
    /// The task's read-back path, exactly as a server endpoint answers it: the engine hands back
    /// the stored value, and the query deserializes the payload and reads one path's chain.
    /// </summary>
    private static ImmutableArray<string> HistoryOf(DerivedDataEngine engine, string changesetId, string path)
    {
        var value = engine.Get(PathHistoryDerivedData.TypeName, changesetId);
        return PathHistoryValue.Deserialize(value.Payload.Span).HistoryOf(path);
    }

    private static Changeset Root(string message, params ChangeEntry[] changes)
    {
        return Changeset.Create([], [.. changes], message, Author, Timestamp);
    }

    private static Changeset ChildOf(Changeset parent, string message, params ChangeEntry[] changes)
    {
        return Changeset.Create([parent.Id], [.. changes], message, Author, Timestamp);
    }

    private static ChangeEntry Added(string path, string newBlob)
    {
        return new ChangeEntry(path, null, newBlob, FileContentKind.Regular, ChangeType.Added);
    }

    private static ChangeEntry Modified(string path, string oldBlob, string newBlob)
    {
        return new ChangeEntry(path, oldBlob, newBlob, FileContentKind.Regular, ChangeType.Modified);
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string directory;

        public TempDatabase()
        {
            directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "scion-path-history-tests-" + Guid.NewGuid().ToString("N"));
            DatabasePath = System.IO.Path.Combine(directory, "scion.db");
            System.IO.Directory.CreateDirectory(directory);
        }

        public string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
