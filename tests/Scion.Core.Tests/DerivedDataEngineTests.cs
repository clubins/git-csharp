using System.Text;

using Microsoft.Data.Sqlite;

using Scion.Core.Derived;
using Scion.Core.Storage;

namespace Scion.Core.Tests;

/// <summary>
/// Engine contract tests through fake derived types — the engine must work for any type, so it is
/// tested only through types invented here: one whose payload traces exactly which parent values
/// it saw, one whose only job is to count computations. The required behaviors — computes from
/// parent values, eager versus deferred, cache hits on repeated queries — are all asserted through
/// those counters and payloads.
/// </summary>
public class DerivedDataEngineTests
{
    private const long Timestamp = 1_790_000_000;
    private const string BlobA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string BlobB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
    private const string BlobC = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";
    private const string Lineage = "test-lineage";
    private const string Mirror = "test-mirror";

    private static readonly Author Author = new("Patrick", "patrick@clubins.de");

    [Fact]
    public void Get_Root_ComputesValueFromTheChangesetAlone()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        new ChangesetLog(db.DatabasePath).Append(root);
        var engine = Engine(db, LineageType(materializeEagerly: false));

        var value = engine.Get(Lineage, root.Id);

        Assert.Equal(Encoding.UTF8.GetBytes(root.Id), value.Payload.ToArray());
        Assert.Equal(Sha256.HashHex(value.Payload.Span), value.Hash);
    }

    [Fact]
    public void Get_Merge_ComputesFromParentValues_InParentOrder()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("a.cs", BlobA, BlobB));
        var merge = Changeset.Create([child.Id, root.Id], [Added("m.cs", BlobC)], "merge", Author, Timestamp);
        new ChangesetLog(db.DatabasePath).AppendRange([root, child, merge]);
        var engine = Engine(db, LineageType(materializeEagerly: false));

        var childValue = engine.Get(Lineage, child.Id);

        // The child's value is its own id followed by its parent's payload — values flow, not ids.
        Assert.Equal(
            Encoding.UTF8.GetBytes(child.Id).Concat(Encoding.UTF8.GetBytes(root.Id)).ToArray(),
            childValue.Payload.ToArray());

        // The merge's value carries both parents' payloads, in Parents order: the first parent's
        // (child's) payload — itself root-flavored — before the second parent's (root's).
        var mergeValue = engine.Get(Lineage, merge.Id);
        var expected = Encoding.UTF8.GetBytes(merge.Id)
            .Concat(childValue.Payload.ToArray())
            .Concat(Encoding.UTF8.GetBytes(root.Id))
            .ToArray();
        Assert.Equal(expected, mergeValue.Payload.ToArray());
    }

    [Fact]
    public void Get_UnknownType_ThrowsKeyNotFound()
    {
        using var db = new TempDatabase();
        var engine = Engine(db);

        Assert.Throws<KeyNotFoundException>(() => engine.Get(Lineage, "any-changeset"));
    }

    [Fact]
    public void Get_UnknownChangeset_ThrowsKeyNotFound()
    {
        using var db = new TempDatabase();
        var engine = Engine(db, LineageType(materializeEagerly: false));

        Assert.Throws<KeyNotFoundException>(() => engine.Get(Lineage, "deadbeef"));
    }

    [Fact]
    public void Get_AncestryMissingFromLog_ThrowsInsteadOfAnsweringPartially()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("a.cs", BlobA, BlobB));
        var log = new ChangesetLog(db.DatabasePath);

        // The log accepts a child whose parent has not arrived yet — and a point query for the
        // child's value cannot be answered, loudly.
        log.Append(child);
        var engine = Engine(db, LineageType(materializeEagerly: false));

        // The engine's loud signal for a dangling parent is KeyNotFound — the queried value
        // cannot exist while its ancestry is incomplete.
        Assert.Throws<KeyNotFoundException>(() => engine.Get(Lineage, child.Id));

        // Once the parent arrives, the same query succeeds.
        log.Append(root);
        engine.Get(Lineage, child.Id);
    }

    [Fact]
    public void Get_DeferredType_ComputesOnce_ThenRepeatedQueriesHitTheCache()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("a.cs", BlobA, BlobB));
        new ChangesetLog(db.DatabasePath).AppendRange([root, child]);
        var counter = new ComputeCounter();
        var engine = Engine(db, LineageType(materializeEagerly: false, counter));

        var first = engine.Get(Lineage, child.Id);
        var second = engine.Get(Lineage, child.Id);
        var third = engine.Get(Lineage, child.Id);

        // Root and child each computed exactly once; the repeats never reached Compute.
        Assert.Equal(2, counter.Calls);
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void Get_OnFreshEngineInstance_UsesPersistedValues_WithoutRecomputing()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("a.cs", BlobA, BlobB));
        new ChangesetLog(db.DatabasePath).AppendRange([root, child]);
        var firstCounter = new ComputeCounter();
        var first = Engine(db, LineageType(materializeEagerly: false, firstCounter));
        var expected = first.Get(Lineage, child.Id);
        Assert.Equal(2, firstCounter.Calls);

        // A new engine over the same database — the restart story: the memo is empty, the store
        // is not, and the query must not recompute anything.
        var secondCounter = new ComputeCounter();
        var second = Engine(db, LineageType(materializeEagerly: false, secondCounter));

        Assert.Equal(expected, second.Get(Lineage, child.Id));
        Assert.Equal(0, secondCounter.Calls);
    }

    [Fact]
    public void MaterializeEager_FillsTheWholeLog_ForEagerTypes_AndQueriesStopComputing()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("a.cs", BlobA, BlobB));
        var merge = Changeset.Create([child.Id, root.Id], [Added("m.cs", BlobC)], "merge", Author, Timestamp);
        new ChangesetLog(db.DatabasePath).AppendRange([root, child, merge]);
        var counter = new ComputeCounter();
        var engine = Engine(db, LineageType(materializeEagerly: true, counter));

        Assert.Equal(3, engine.MaterializeEager());
        Assert.Equal(3, counter.Calls);

        // Every value is in the store by now — a query for any of the three is a pure read.
        Assert.Equal(Encoding.UTF8.GetBytes(root.Id), engine.Get(Lineage, root.Id).Payload.ToArray());
        _ = engine.Get(Lineage, child.Id);
        _ = engine.Get(Lineage, merge.Id);
        Assert.Equal(3, counter.Calls);
    }

    [Fact]
    public void MaterializeEager_LeavesDeferredTypes_Untouched()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("a.cs", BlobA, BlobB));
        new ChangesetLog(db.DatabasePath).AppendRange([root, child]);
        var counter = new ComputeCounter();
        var engine = Engine(db, LineageType(materializeEagerly: false, counter));

        Assert.Equal(0, engine.MaterializeEager());
        Assert.Equal(0, counter.Calls);

        // The first query does the computing, once — then the cache takes over.
        _ = engine.Get(Lineage, child.Id);
        Assert.Equal(2, counter.Calls);
        _ = engine.Get(Lineage, child.Id);
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public void MaterializeEager_AfterAppends_IsIncremental()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("a.cs", BlobA, BlobB));
        var log = new ChangesetLog(db.DatabasePath);
        log.AppendRange([root, child]);
        var engine = Engine(db, LineageType(materializeEagerly: true));
        Assert.Equal(2, engine.MaterializeEager());

        var merge = Changeset.Create([child.Id, root.Id], [Added("m.cs", BlobC)], "merge", Author, Timestamp);
        log.Append(merge);

        Assert.Equal(1, engine.MaterializeEager());
        Assert.Equal(3, new DerivedDataStore(db.DatabasePath).StoredChangesetIds(Lineage).Count);
    }

    [Fact]
    public void MaterializeEager_FillsBackfillableValues_WhenAMissingParentArrives()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("a.cs", BlobA, BlobB));
        var log = new ChangesetLog(db.DatabasePath);
        var counter = new ComputeCounter();
        var engine = Engine(db, LineageType(materializeEagerly: true, counter));

        // The child arrived before its parent: the sweep cannot compute it and must not fail.
        log.Append(child);
        Assert.Equal(0, engine.MaterializeEager());

        // The parent lands; the next sweep fills both, parent first.
        log.Append(root);
        Assert.Equal(2, engine.MaterializeEager());
        Assert.Equal(2, counter.Calls);
        Assert.Equal(
            Encoding.UTF8.GetBytes(child.Id).Concat(Encoding.UTF8.GetBytes(root.Id)).ToArray(),
            engine.Get(Lineage, child.Id).Payload.ToArray());
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public void MaterializeEager_AppendOrderNeedNotBeTopological()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("a.cs", BlobA, BlobB));
        var merge = Changeset.Create([child.Id, root.Id], [Added("m.cs", BlobC)], "merge", Author, Timestamp);

        // Appended tip-first, the reverse of parent order — the sweep must sort, not assume.
        new ChangesetLog(db.DatabasePath).AppendRange([merge, child, root]);
        var engine = Engine(db, LineageType(materializeEagerly: true));

        Assert.Equal(3, engine.MaterializeEager());
        Assert.Equal(
            Encoding.UTF8.GetBytes(root.Id),
            engine.Get(Lineage, root.Id).Payload.ToArray());
    }

    [Fact]
    public void Types_StayIndependent_OneEagerOneDeferredOverTheSameLog()
    {
        using var db = new TempDatabase();
        var root = Root("root", Added("a.cs", BlobA));
        var child = ChildOf(root, "child", Modified("a.cs", BlobA, BlobB));
        new ChangesetLog(db.DatabasePath).AppendRange([root, child]);
        var lineageCounter = new ComputeCounter();
        var mirrorCounter = new ComputeCounter();
        var engine = Engine(
            db,
            LineageType(materializeEagerly: true, lineageCounter),
            new FakeType(Mirror, materializeEagerly: false, mirrorCounter, IdPayload));

        Assert.Equal(2, engine.MaterializeEager());
        Assert.Equal(2, lineageCounter.Calls);
        Assert.Equal(0, mirrorCounter.Calls);

        // Two types over one changeset: two independently keyed values, and the eager type's
        // query reads the store while the deferred one computes its own first value.
        var lineageValue = engine.Get(Lineage, child.Id);
        var mirrorValue = engine.Get(Mirror, child.Id);
        Assert.Equal(2, lineageCounter.Calls);
        Assert.Equal(2, mirrorCounter.Calls);
        Assert.NotEqual(lineageValue.Payload.ToArray(), mirrorValue.Payload.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(child.Id), mirrorValue.Payload.ToArray());
    }

    [Fact]
    public void Constructor_DuplicateTypeName_IsRejected()
    {
        using var db = new TempDatabase();

        Assert.Throws<ArgumentException>(
            () => Engine(db, LineageType(materializeEagerly: true), LineageType(materializeEagerly: false)));
    }

    [Fact]
    public void Constructor_BlankTypeName_IsRejected()
    {
        using var db = new TempDatabase();

        Assert.Throws<ArgumentException>(
            () => Engine(db, new FakeType("   ", materializeEagerly: false, null, IdPayload)));
    }

    /// <summary>An engine over fresh log and store instances on one temporary database.</summary>
    private static DerivedDataEngine Engine(TempDatabase db, params IDerivedData[] types)
    {
        return new DerivedDataEngine(
            new ChangesetLog(db.DatabasePath),
            new DerivedDataStore(db.DatabasePath),
            types);
    }

    /// <summary>The lineage fake: payload = the changeset id's bytes followed by each parent value's payload.</summary>
    private static FakeType LineageType(bool materializeEagerly, ComputeCounter? counter = null)
    {
        return new FakeType(Lineage, materializeEagerly, counter, LineagePayload);
    }

    private static DerivedDataValue LineagePayload(Changeset changeset, ReadOnlySpan<DerivedDataValue> parentValues)
    {
        var idBytes = Encoding.UTF8.GetBytes(changeset.Id);
        var length = idBytes.Length;
        foreach (var parent in parentValues)
        {
            length += parent.Payload.Length;
        }

        var payload = new byte[length];
        idBytes.CopyTo(payload, 0);
        var offset = idBytes.Length;
        foreach (var parent in parentValues)
        {
            parent.Payload.Span.CopyTo(payload.AsSpan(offset));
            offset += parent.Payload.Length;
        }

        return DerivedDataValue.FromPayload(payload);
    }

    private static DerivedDataValue IdPayload(Changeset changeset, ReadOnlySpan<DerivedDataValue> parentValues)
    {
        return DerivedDataValue.FromPayload(Encoding.UTF8.GetBytes(changeset.Id));
    }

    /// <summary>Counts <see cref="IDerivedData.Compute"/> invocations across queries and sweeps.</summary>
    private sealed class ComputeCounter
    {
        public int Calls { get; private set; }

        public void Record() => Calls++;
    }

    private delegate DerivedDataValue ComputeFn(Changeset changeset, ReadOnlySpan<DerivedDataValue> parentValues);

    /// <summary>A fake registered type: the payload function is injected so tests choose what a "value" is.</summary>
    private sealed class FakeType : IDerivedData
    {
        private readonly ComputeCounter? _counter;
        private readonly ComputeFn _compute;

        public FakeType(string name, bool materializeEagerly, ComputeCounter? counter, ComputeFn compute)
        {
            Name = name;
            MaterializeEagerly = materializeEagerly;
            _counter = counter;
            _compute = compute;
        }

        public string Name { get; }

        public bool MaterializeEagerly { get; }

        public DerivedDataValue Compute(Changeset changeset, ReadOnlySpan<DerivedDataValue> parentValues)
        {
            _counter?.Record();
            return _compute(changeset, parentValues);
        }
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
                "scion-derived-engine-tests-" + Guid.NewGuid().ToString("N"));
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
