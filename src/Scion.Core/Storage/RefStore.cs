using Microsoft.Data.Sqlite;

namespace Scion.Core.Storage;

/// <summary>
/// The ref store: named pointers into the changeset DAG that move only by compare-and-swap. A
/// move is the spec's single statement — <c>UPDATE refs SET target = …, revision = revision + 1
/// WHERE name = … AND revision = …</c> — run inside a write transaction; zero rows moved is the
/// conflict signal, and the conflict path reads back the ref's current state so the caller can
/// report it (spec criterion C7: a CAS conflict is answered with the current value, never with a
/// bare error). Creation is the same operation from <see cref="CreationRevision"/>, so there is
/// no separate create path to race a move.
/// </summary>
/// <remarks>
/// <para>
/// Ref moves are decoupled from changeset creation by design: appending a changeset never moves
/// a ref, so commits cannot contend on a ref lock, and two agents whose commits race pay nothing
/// until both try to advance the same ref — at which point exactly one wins and the loser holds
/// the winner's value to retry against.
/// </para>
/// <para>
/// This class claims only the <c>refs</c> and <c>ref_history</c> tables and the <c>refs</c> row
/// in <c>schema_registry</c>. It lives beside the <see cref="ChangesetLog"/> against the same
/// database file, owning its own tables and registry row. Like the log, it is deliberately dumb
/// about graph semantics: a ref may point at a changeset id that is not stored (yet), and target
/// existence and format are the server layer's concern; this layer guarantees only that each
/// move was atomic and is exactly recorded.
/// </para>
/// </remarks>
public sealed class RefStore
{
    /// <summary>The <c>schema_registry</c> row this store owns.</summary>
    private const string SchemaName = "refs";

    private const long SchemaVersion = 1;

    /// <summary>
    /// The revision a ref that does not exist is created from. It is one below every real
    /// revision — a created ref starts at revision 1 — and it is the only legal expectation for
    /// a move whose ref is absent; expecting it on an existing ref is an ordinary conflict.
    /// </summary>
    public const long CreationRevision = 0;

    /// <summary>How long a writer waits for SQLite's write lock before giving up. Same story as the log's.</summary>
    private const int BusyTimeoutMilliseconds = 5_000;

    private readonly string _connectionString;

    /// <summary>
    /// Opens (creating on first use) the ref store inside the database at
    /// <paramref name="databasePath"/> — the same file the <see cref="ChangesetLog"/> owns. The
    /// constructor is the boot path: it establishes the schema atomically and validates the
    /// registry version.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database was written by an incompatible schema version.</exception>
    public RefStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        Initialize();
    }

    /// <summary>
    /// Moves (or creates) a ref by compare-and-swap.
    /// </summary>
    /// <param name="name">The ref name, e.g. <c>main</c>.</param>
    /// <param name="expectedRevision">
    /// The revision the caller last observed; the move applies only while the ref is still at
    /// this revision. <see cref="CreationRevision"/> creates a ref that does not exist.
    /// </param>
    /// <param name="newTarget">The changeset id the ref should point at. Opaque to this store.</param>
    /// <param name="state">
    /// On success, the ref's new state. On conflict, the ref's current state — the value a 409
    /// response carries (spec C7) — or null when a move expected a ref that does not exist.
    /// </param>
    /// <returns>True when the ref now points at <paramref name="newTarget"/>; false on conflict.</returns>
    /// <remarks>
    /// A lost race is an outcome, not an exception: the caller reads <paramref name="state"/>,
    /// reconciles, and re-reads-retries per the spec. The write lock is held from
    /// <c>BEGIN IMMEDIATE</c> through the decision, so racing moves serialize and every loser
    /// reports the state the winner left behind.
    /// </remarks>
    public bool TryCompareAndSwap(string name, long expectedRevision, string newTarget, out RefState? state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(newTarget);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            // The spec's CAS statement, verbatim: the row moves only while its revision still
            // equals the caller's expectation, and the revision advances by exactly one. SQLite
            // evaluates the SET against the pre-update row, so a matching row ends at
            // expectedRevision + 1 — the value recorded below without re-reading.
            using var move = connection.CreateCommand();
            move.CommandText = """
                UPDATE refs SET target = @target, revision = revision + 1
                WHERE name = @name AND revision = @expectedRevision
                """;
            move.Parameters.AddWithValue("@target", newTarget);
            move.Parameters.AddWithValue("@name", name);
            move.Parameters.AddWithValue("@expectedRevision", expectedRevision);

            long newRevision;
            if (move.ExecuteNonQuery() == 1)
            {
                newRevision = expectedRevision + 1;
            }
            else
            {
                // Zero rows moved: a lost race against a concurrent move, a create racing an
                // existing ref, or a move expecting a ref that is not there. Read the current
                // state under the write lock to tell those apart and to report it.
                var current = ReadState(connection, name);
                if (current is not null || expectedRevision != CreationRevision)
                {
                    Execute(connection, "ROLLBACK");
                    state = current;
                    return false;
                }

                // Creation: the ref does not exist, the expectation is CreationRevision, and the
                // write lock guarantees no one can materialize it between the read and here.
                using var create = connection.CreateCommand();
                create.CommandText = "INSERT INTO refs (name, target, revision) VALUES (@name, @target, 1)";
                create.Parameters.AddWithValue("@name", name);
                create.Parameters.AddWithValue("@target", newTarget);
                create.ExecuteNonQuery();

                newRevision = 1;
            }

            AppendHistory(connection, name, newTarget, newRevision);
            Execute(connection, "COMMIT");
            state = new RefState(name, newTarget, newRevision);
            return true;
        }
        catch
        {
            RollbackOnUnwind(connection);
            throw;
        }
    }

    /// <summary>The ref's current state, or null when the ref does not exist.</summary>
    public RefState? Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var connection = Open();
        return ReadState(connection, name);
    }

    /// <summary>Every ref, ordered by name.</summary>
    public IReadOnlyList<RefState> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, target, revision FROM refs ORDER BY name";
        using var reader = command.ExecuteReader();

        List<RefState> refs = [];
        while (reader.Read())
        {
            refs.Add(new RefState(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }

        return refs;
    }

    /// <summary>
    /// The ref's recorded moves in append order — creation first. A ref that does not exist has
    /// no history; a ref that exists has at least its creation entry.
    /// </summary>
    public IReadOnlyList<RefHistoryEntry> GetHistory(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT target, revision, timestamp_utc FROM ref_history WHERE name = @name ORDER BY seq";
        command.Parameters.AddWithValue("@name", name);
        using var reader = command.ExecuteReader();

        List<RefHistoryEntry> history = [];
        while (reader.Read())
        {
            history.Add(new RefHistoryEntry(name, reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        return history;
    }

    private void Initialize()
    {
        using var connection = Open();
        BeginImmediate(connection);
        try
        {
            Execute(connection, "CREATE TABLE IF NOT EXISTS schema_registry (name TEXT PRIMARY KEY, version INTEGER NOT NULL)");
            Execute(
                connection,
                """
                CREATE TABLE IF NOT EXISTS refs (
                    name TEXT PRIMARY KEY,
                    target TEXT NOT NULL,
                    revision INTEGER NOT NULL)
                """);
            Execute(
                connection,
                """
                CREATE TABLE IF NOT EXISTS ref_history (
                    seq INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    target TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    timestamp_utc INTEGER NOT NULL)
                """);
            Execute(connection, "CREATE INDEX IF NOT EXISTS ref_history_by_name ON ref_history(name, seq)");

            Execute(
                connection,
                "INSERT INTO schema_registry (name, version) VALUES (@name, @version) ON CONFLICT (name) DO NOTHING",
                new SqliteParameter("@name", SchemaName),
                new SqliteParameter("@version", SchemaVersion));

            var recorded = (long)Scalar(connection, $"SELECT version FROM schema_registry WHERE name = '{SchemaName}'")!;
            if (recorded != SchemaVersion)
            {
                throw new InvalidOperationException($"Ref store schema is v{recorded}; this build speaks v{SchemaVersion}.");
            }

            Execute(connection, "COMMIT");
        }
        catch
        {
            RollbackOnUnwind(connection);
            throw;
        }
    }

    private static RefState? ReadState(SqliteConnection connection, string name)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT target, revision FROM refs WHERE name = @name";
        query.Parameters.AddWithValue("@name", name);
        using var reader = query.ExecuteReader();

        return reader.Read()
            ? new RefState(name, reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    private static void AppendHistory(SqliteConnection connection, string name, string target, long revision)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO ref_history (name, target, revision, timestamp_utc) VALUES (@name, @target, @revision, @timestampUtc)";
        insert.Parameters.AddWithValue("@name", name);
        insert.Parameters.AddWithValue("@target", target);
        insert.Parameters.AddWithValue("@revision", revision);
        insert.Parameters.AddWithValue("@timestampUtc", DateTime.UtcNow.Ticks);
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// Starts a write transaction with <c>BEGIN IMMEDIATE</c> — the write lock is taken up front,
    /// so racing CAS moves wait out the busy timeout instead of failing a deferred upgrade with
    /// SQLITE_BUSY_SNAPSHOT. Microsoft.Data.Sqlite's own <see cref="SqliteConnection.BeginTransaction"/>
    /// only issues a deferred BEGIN, hence the raw statement.
    /// </summary>
    private static void BeginImmediate(SqliteConnection connection)
    {
        Execute(connection, "BEGIN IMMEDIATE");
    }

    /// <summary>
    /// Rolls back on the exception path. A ROLLBACK failure here is covered by SQLite itself: the
    /// transaction is left open only because the connection is unusable, and closing it rolls
    /// back any open transaction — so no half-moved ref can survive either way.
    /// </summary>
    private static void RollbackOnUnwind(SqliteConnection connection)
    {
        try
        {
            Execute(connection, "ROLLBACK");
        }
        catch (SqliteException)
        {
        }
    }

    /// <summary>
    /// Opens a connection with the pragmas every operation relies on. WAL mode is persistent in
    /// the database file; the remaining pragmas are per connection.
    /// </summary>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Busy timeout first, so the WAL-mode assertion below waits out (rather than trips over)
        // a concurrent writer during mode transitions.
        Execute(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}");
        var journalMode = (string)Scalar(connection, "PRAGMA journal_mode = WAL")!;
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Could not enable WAL mode on the ref store (journal mode is '{journalMode}').");
        }

        // NORMAL is the WAL-recommended setting: durable against process death (kill -9), without
        // an fsync per commit. FULL would only add power-loss safety, which a server process does
        // not observe as an error path.
        Execute(connection, "PRAGMA synchronous = NORMAL");
        Execute(connection, "PRAGMA foreign_keys = ON");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string text, params SqliteParameter[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.AddRange(parameters);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        return command.ExecuteScalar();
    }
}
