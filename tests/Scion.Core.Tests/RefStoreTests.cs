using Microsoft.Data.Sqlite;
using Scion.Core.Storage;

namespace Scion.Core.Tests;

/// <summary>
/// Contract tests for the CAS ref store: creation and movement by compare-and-swap, the conflict
/// contract (spec C7 — losers learn the ref's current value, never a bare error), the concurrent
/// race (exactly one winner per revision), the per-ref move history, and coexistence with the
/// changeset log inside one database file.
/// </summary>
public class RefStoreTests
{
    private const string Main = "main";
    private const string BlobA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";

    private static readonly Author Author = new("Patrick", "patrick@clubins.de");
    private const long Timestamp = 1_790_000_000;

    [Fact]
    public void TryCompareAndSwap_FromCreationRevision_CreatesRefAtRevisionOne()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);

        var swapped = store.TryCompareAndSwap(Main, RefStore.CreationRevision, Target(1), out var state);

        Assert.True(swapped);
        Assert.Equal(new RefState(Main, Target(1), 1), state);
        Assert.Equal(new RefState(Main, Target(1), 1), store.Get(Main));
    }

    [Fact]
    public void TryCompareAndSwap_WithCurrentRevision_MovesRef()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);
        store.TryCompareAndSwap(Main, RefStore.CreationRevision, Target(1), out _);

        var swapped = store.TryCompareAndSwap(Main, 1, Target(2), out var state);

        Assert.True(swapped);
        Assert.Equal(new RefState(Main, Target(2), 2), state);
        Assert.Equal(new RefState(Main, Target(2), 2), store.Get(Main));
    }

    [Fact]
    public void TryCompareAndSwap_WithStaleRevision_FailsAndReturnsCurrentState()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);
        store.TryCompareAndSwap(Main, RefStore.CreationRevision, Target(1), out _);
        store.TryCompareAndSwap(Main, 1, Target(2), out _);

        // A move against revision 1 lost earlier: the ref is at revision 2. The CAS fails and
        // hands back the current value — what a 409 response would carry — and moves nothing.
        var swapped = store.TryCompareAndSwap(Main, 1, Target(3), out var state);

        Assert.False(swapped);
        Assert.Equal(new RefState(Main, Target(2), 2), state);
        Assert.Equal(new RefState(Main, Target(2), 2), store.Get(Main));
    }

    [Fact]
    public void TryCompareAndSwap_OnMissingRef_WithNonZeroExpectation_FailsWithNullState()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);

        var swapped = store.TryCompareAndSwap(Main, 1, Target(1), out var state);

        Assert.False(swapped);
        Assert.Null(state);
        Assert.Null(store.Get(Main));
    }

    [Fact]
    public void TryCompareAndSwap_CreateAgainstExistingRef_FailsAndReturnsExistingRef()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);
        store.TryCompareAndSwap(Main, RefStore.CreationRevision, Target(1), out _);

        // CreationRevision against an existing ref is an ordinary conflict, not a re-create.
        var swapped = store.TryCompareAndSwap(Main, RefStore.CreationRevision, Target(2), out var state);

        Assert.False(swapped);
        Assert.Equal(new RefState(Main, Target(1), 1), state);
        Assert.Equal(new RefState(Main, Target(1), 1), store.Get(Main));
    }

    [Fact]
    public async Task TryCompareAndSwap_ConcurrentRacers_ExactlyOneWinsAndLosersSeeItsState()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);

        const int racers = 8;
        const int rounds = 5;

        // Each round, all racers CAS from the ref's current revision with distinct targets.
        // Exactly one may win; every loser must report the state the winner left behind — the
        // value it needs to reconcile and retry against.
        for (var round = 0; round < rounds; round++)
        {
            var current = store.Get(Main);
            if (current is null)
            {
                Assert.Equal(0, round);
            }
            else
            {
                Assert.Equal(round, current.Revision);
            }

            var expectedRevision = current?.Revision ?? RefStore.CreationRevision;
            var targets = Enumerable.Range(round * racers, racers).Select(Target).ToArray();

            var outcomes = await Task.WhenAll(
                Enumerable.Range(0, racers).Select(index => Task.Run(() =>
                {
                    var swapped = store.TryCompareAndSwap(Main, expectedRevision, targets[index], out var state);
                    return (Target: targets[index], Swapped: swapped, State: state);
                })));

            var winner = Assert.Single(outcomes, outcome => outcome.Swapped);
            Assert.Equal(expectedRevision + 1, winner.State!.Revision);
            Assert.Equal(winner.Target, winner.State.Target);

            var stateAfterRound = new RefState(Main, winner.Target, expectedRevision + 1);
            foreach (var loser in outcomes.Where(outcome => !outcome.Swapped))
            {
                Assert.NotNull(loser.State);
                Assert.Equal(stateAfterRound, loser.State);
            }
        }

        // One successful move per round, no more: the final revision and history size agree.
        Assert.Equal(new RefState(Main, store.Get(Main)!.Target, rounds), store.Get(Main));
        Assert.Equal(rounds, store.GetHistory(Main).Count);
    }

    [Fact]
    public void GetHistory_RecordsEveryMoveInOrder()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);
        store.TryCompareAndSwap(Main, RefStore.CreationRevision, Target(1), out _);
        store.TryCompareAndSwap(Main, 1, Target(2), out _);
        store.TryCompareAndSwap(Main, 2, Target(3), out _);
        store.TryCompareAndSwap(Main, 3, Target(4), out _);

        var history = store.GetHistory(Main);

        Assert.Equal(4, history.Count);
        Assert.All(history, entry => Assert.Equal(Main, entry.Name));
        Assert.Equal(new[] { Target(1), Target(2), Target(3), Target(4) }, history.Select(entry => entry.Target));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, history.Select(entry => entry.Revision));
        Assert.All(history, entry => Assert.True(entry.TimestampUtc > 0));
    }

    [Fact]
    public void GetHistory_MissingRef_IsEmpty()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);

        Assert.Empty(store.GetHistory(Main));
    }

    [Fact]
    public void List_ReturnsEveryRefOrderedByName()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);
        store.TryCompareAndSwap("release/1.0", RefStore.CreationRevision, Target(1), out _);
        store.TryCompareAndSwap(Main, RefStore.CreationRevision, Target(2), out _);

        Assert.Equal(
            new[] { new RefState(Main, Target(2), 1), new RefState("release/1.0", Target(1), 1) },
            store.List());
    }

    [Fact]
    public void Open_SequentialInstances_StateAndHistoryPersistAcrossBoots()
    {
        using var temp = new TempDatabase();
        new RefStore(temp.DatabasePath).TryCompareAndSwap(Main, RefStore.CreationRevision, Target(1), out _);
        new RefStore(temp.DatabasePath).TryCompareAndSwap(Main, 1, Target(2), out _);

        var reader = new RefStore(temp.DatabasePath);

        Assert.Equal(new RefState(Main, Target(2), 2), reader.Get(Main));
        Assert.Equal(new[] { Target(1), Target(2) }, reader.GetHistory(Main).Select(entry => entry.Target));
    }

    [Fact]
    public void Open_AlongsideChangesetLog_BothStoresShareOneDatabaseFile()
    {
        using var temp = new TempDatabase();

        // The intended server-tier flow: append a changeset, then point a ref at it. The stores
        // are separate classes against one file, each with its own schema_registry row.
        var log = new ChangesetLog(temp.DatabasePath);
        var refs = new RefStore(temp.DatabasePath);
        var changeset = Root();
        log.Append(changeset);
        Assert.True(refs.TryCompareAndSwap(Main, RefStore.CreationRevision, changeset.Id, out var state));
        Assert.Equal(new RefState(Main, changeset.Id, 1), state);

        var reopenedLog = new ChangesetLog(temp.DatabasePath);
        var reopenedRefs = new RefStore(temp.DatabasePath);
        Assert.Equal([changeset], reopenedLog.Replay());
        Assert.Equal(new RefState(Main, changeset.Id, 1), reopenedRefs.Get(Main));

        Assert.Equal(["changesets", "refs"], RegistryRows(temp.DatabasePath));
    }

    [Fact]
    public void TryCompareAndSwap_BlankName_ThrowsWithoutMovingAnything()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);

        Assert.Throws<ArgumentException>(() => store.TryCompareAndSwap(" ", RefStore.CreationRevision, Target(1), out _));
        Assert.Empty(store.List());
    }

    [Fact]
    public void TryCompareAndSwap_BlankTarget_ThrowsWithoutMovingAnything()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);
        store.TryCompareAndSwap(Main, RefStore.CreationRevision, Target(1), out _);

        Assert.Throws<ArgumentException>(() => store.TryCompareAndSwap(Main, 1, " ", out _));
        Assert.Equal(new RefState(Main, Target(1), 1), store.Get(Main));
    }

    [Fact]
    public void TryCompareAndSwap_NegativeExpectedRevision_Throws()
    {
        using var temp = new TempDatabase();
        var store = new RefStore(temp.DatabasePath);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.TryCompareAndSwap(Main, -1, Target(1), out _));
    }

    private static string Target(int index)
    {
        // A 64-character opaque string in the shape of a changeset id; the store never inspects it.
        return $"{index:D64}";
    }

    private static Changeset Root()
    {
        return Changeset.Create([], [Added("a.cs", BlobA)], "root", Author, Timestamp);
    }

    private static ChangeEntry Added(string path, string newBlob)
    {
        return new ChangeEntry(path, null, newBlob, FileContentKind.Regular, ChangeType.Added);
    }

    private static IReadOnlyList<string> RegistryRows(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM schema_registry ORDER BY name";
        using var reader = command.ExecuteReader();

        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        SqliteConnection.ClearAllPools();
        return rows;
    }

    /// <summary>A temp directory plus the database path inside it; disposal drops pooled handles first so the tree can be deleted.</summary>
    private sealed class TempDatabase : IDisposable
    {
        private readonly string directory;

        public TempDatabase()
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                "scion-ref-tests-" + Guid.NewGuid().ToString("N"));
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
