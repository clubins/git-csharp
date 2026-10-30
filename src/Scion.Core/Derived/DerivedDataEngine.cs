using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Scion.Core.Storage;

namespace Scion.Core.Derived;

/// <summary>
/// The materialization and caching layer over the changeset log — the spec's generic recursive
/// mechanism, with no derived type of its own. Registered types (<see cref="IDerivedData"/>) say
/// how one changeset's value follows from its parents'; this class owns everything else: filling
/// values at boot for eager types, computing on first query for deferred ones, persisting every
/// computed value before it is served, and answering repeated queries from a process-lifetime memo.
/// </summary>
/// <remarks>
/// <para>
/// A value is keyed by (type name, changeset id), computed at most once per process, and persisted
/// before it is returned, so the memo is a warm cache over the store — a fresh engine on the same
/// database loses nothing and recomputes nothing that was already stored. Computations are pure
/// (<see cref="IDerivedData.Compute"/>), which is what makes concurrent queries safe without
/// locking: duplicated work only ever agrees with itself, and the idempotent store turns a
/// double-write into a no-op.
/// </para>
/// <para>
/// Point queries are loud about what they cannot compute — an unknown type or changeset throws
/// <see cref="KeyNotFoundException"/>, and ancestry that never reached the log (the log permits
/// dangling parents) throws <see cref="InvalidOperationException"/>. The boot sweep
/// (<see cref="MaterializeEager"/>) has the opposite policy: it materializes everything computable
/// and leaves the rest — dangling children, cyclic fragments — for a later sweep, so a transiently
/// out-of-order append never fails a boot.
/// </para>
/// <para>
/// The memo is deliberately unbounded and never warmed by the sweep: eager materialization of a
/// large log should cost disk, not resident memory, and the memo grows only along query patterns.
/// Eviction belongs to the server tier's tuning, not to this contract.
/// </para>
/// </remarks>
public sealed class DerivedDataEngine
{
    private readonly ChangesetLog _changesetLog;
    private readonly DerivedDataStore _store;
    private readonly Dictionary<string, IDerivedData> _types;
    private readonly ConcurrentDictionary<(string TypeName, string ChangesetId), DerivedDataValue> _memo = new();

    /// <summary>
    /// Registers derived types against a changeset log and the derived-value store beside it —
    /// conventionally the same database file. Two types under one name is a registry bug and is
    /// rejected at construction, before anything is persisted under either.
    /// </summary>
    /// <exception cref="ArgumentException">A type is null, names itself empty, or repeats another type's name.</exception>
    public DerivedDataEngine(ChangesetLog changesetLog, DerivedDataStore store, params IDerivedData[] types)
    {
        ArgumentNullException.ThrowIfNull(changesetLog);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(types);

        _changesetLog = changesetLog;
        _store = store;
        _types = new Dictionary<string, IDerivedData>(types.Length, StringComparer.Ordinal);
        foreach (var type in types)
        {
            ArgumentNullException.ThrowIfNull(type, nameof(types));
            if (string.IsNullOrWhiteSpace(type.Name))
            {
                throw new ArgumentException("A derived type must name itself.", nameof(types));
            }

            if (!_types.TryAdd(type.Name, type))
            {
                throw new ArgumentException($"Two derived types are registered under the name '{type.Name}'.", nameof(types));
            }
        }
    }

    /// <summary>
    /// The value of one derived type at one changeset — answered from the memo, then the store,
    /// then by computing the tip together with whatever ancestry is not materialized yet. Whatever
    /// path answers, the value is persisted first, so the next identical query is a cache hit.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The type is not registered, or the changeset is not in the log.</exception>
    /// <exception cref="InvalidOperationException">The changeset's ancestry is not fully in the log, or its parent graph is cyclic.</exception>
    public DerivedDataValue Get(string typeName, string changesetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(changesetId);

        var type = Resolve(typeName);
        if (TryRead(type, changesetId, out var value))
        {
            return value;
        }

        return ComputeWithAncestry(type, changesetId);
    }

    /// <summary>
    /// The boot sweep: computes and persists every missing value of every eager type across the
    /// whole log, parents before children. Idempotent and incremental — stored values are skipped,
    /// so re-sweeping after appends costs only the new changesets — and best-effort at the edges:
    /// changesets whose ancestry is incomplete or cyclic are left for a later sweep rather than
    /// failing it. Returns the number of values newly computed. Sweep-filled values live in the
    /// store, not the memo — the memo follows queries, not the log's size.
    /// </summary>
    public long MaterializeEager()
    {
        var log = _changesetLog.Replay();
        var materialized = 0L;
        foreach (var type in _types.Values)
        {
            if (type.MaterializeEagerly)
            {
                materialized += MaterializeType(type, log);
            }
        }

        return materialized;
    }

    private long MaterializeType(IDerivedData type, IReadOnlyList<Changeset> log)
    {
        var stored = _store.StoredChangesetIds(type.Name).ToHashSet(StringComparer.Ordinal);
        var computed = 0L;
        foreach (var changeset in ParentsFirstOrder(log))
        {
            if (stored.Contains(changeset.Id) || !TryComputeAndStore(type, changeset, out _))
            {
                // Already stored, or its ancestry is not (yet) materializable — a dangling parent
                // or a cyclic fragment. Both stay for a later sweep.
                continue;
            }

            computed++;
        }

        return computed;
    }

    private DerivedDataValue ComputeWithAncestry(IDerivedData type, string changesetId)
    {
        // Collect the unmaterialized ancestors reachable over parent edges. Every id in the
        // ancestry must resolve in the log — a dangling parent means the queried value cannot
        // exist, and a point query says so instead of answering a partial truth.
        var pending = new Dictionary<string, Changeset>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(changesetId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (pending.ContainsKey(id) || TryRead(type, id, out _))
            {
                continue; // already collected, or already materialized — the recursion's boundary
            }

            var changeset = ResolveChangeset(id);
            pending[id] = changeset;
            foreach (var parent in changeset.Parents)
            {
                queue.Enqueue(parent);
            }
        }

        // Parents before children over exactly the unmaterialized subgraph. Kahn's order comes up
        // short against a cycle, and a cyclic ancestry has no value to answer with. (Content
        // addressing makes cycles unconstructible through the log — this is defense in depth.)
        var ordered = ParentsFirstOrder(new List<Changeset>(pending.Values));
        if (ordered.Count != pending.Count)
        {
            throw new InvalidOperationException(
                $"The ancestry of {changesetId} contains a parent cycle; the {type.Name} value cannot be computed.");
        }

        foreach (var changeset in ordered)
        {
            if (!TryComputeAndStore(type, changeset, out _))
            {
                throw new InvalidOperationException(
                    $"A parent of {changeset.Id} lost its {type.Name} value between the boundary check and the compute; the derived-value store is inconsistent.");
            }
        }

        // Parents-first ordering puts the queried changeset last, so its value is stored by now —
        // and reading it back warms the memo exactly the way any store hit does.
        return TryRead(type, changesetId, out var result)
            ? result
            : throw new InvalidOperationException(
                $"The {type.Name} value of {changesetId} was computed but could not be read back; the derived-value store is inconsistent.");
    }

    /// <summary>Memo first, then store; a store hit warms the memo, because queries repeat and disks are slower than dictionaries.</summary>
    private bool TryRead(IDerivedData type, string changesetId, [NotNullWhen(true)] out DerivedDataValue? value)
    {
        if (_memo.TryGetValue((type.Name, changesetId), out var memoed))
        {
            value = memoed;
            return true;
        }

        if (_store.TryGet(type.Name, changesetId, out var stored))
        {
            value = _memo[(type.Name, changesetId)] = stored;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Computes one changeset's value over its parents' — read from memo or store, never
    /// recomputed — then persists it. False means a parent's value is not materialized: the
    /// caller decides whether that is an error (point query) or a reason to wait (sweep).
    /// </summary>
    private bool TryComputeAndStore(IDerivedData type, Changeset changeset, [NotNullWhen(true)] out DerivedDataValue? value)
    {
        var parentValues = new DerivedDataValue[changeset.Parents.Length];
        for (var i = 0; i < changeset.Parents.Length; i++)
        {
            if (!TryRead(type, changeset.Parents[i], out var parentValue))
            {
                value = null;
                return false;
            }

            parentValues[i] = parentValue;
        }

        value = type.Compute(changeset, parentValues);
        _store.Put(type.Name, changeset.Id, value);
        return true;
    }

    private Changeset ResolveChangeset(string changesetId)
    {
        return _changesetLog.TryGetById(changesetId, out var changeset)
            ? changeset!
            : throw new KeyNotFoundException($"Changeset {changesetId} is not in the log; its derived value cannot be computed.");
    }

    private IDerivedData Resolve(string typeName)
    {
        return _types.TryGetValue(typeName, out var type)
            ? type
            : throw new KeyNotFoundException($"No derived type is registered under the name '{typeName}'.");
    }

    /// <summary>
    /// Kahn's parents-before-children order over parent edges that stay inside the snapshot. The
    /// result is shorter than the input exactly when the snapshot contains a cycle; ties resolve
    /// in input order, so two sweeps of the same log walk it identically.
    /// </summary>
    private static List<Changeset> ParentsFirstOrder(IReadOnlyList<Changeset> changesets)
    {
        var index = new Dictionary<string, int>(changesets.Count, StringComparer.Ordinal);
        for (var i = 0; i < changesets.Count; i++)
        {
            index.TryAdd(changesets[i].Id, i);
        }

        var children = new List<int>[changesets.Count];
        var inDegree = new int[changesets.Count];
        for (var i = 0; i < changesets.Count; i++)
        {
            children[i] = [];
        }

        // Second pass: a child's parent may sit at a later index than the child itself, so every
        // children list must exist before any of them is appended to.
        for (var i = 0; i < changesets.Count; i++)
        {
            foreach (var parent in changesets[i].Parents)
            {
                if (index.TryGetValue(parent, out var parentIndex))
                {
                    inDegree[i]++;
                    children[parentIndex].Add(i);
                }
            }
        }

        var ready = new Queue<int>();
        for (var i = 0; i < changesets.Count; i++)
        {
            if (inDegree[i] == 0)
            {
                ready.Enqueue(i);
            }
        }

        var order = new List<Changeset>(changesets.Count);
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            order.Add(changesets[current]);
            foreach (var child in children[current])
            {
                if (--inDegree[child] == 0)
                {
                    ready.Enqueue(child);
                }
            }
        }

        return order;
    }
}
