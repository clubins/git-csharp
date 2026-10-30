namespace Scion.Core.Derived;

/// <summary>
/// One kind of derived data: the recursion from a changeset's parents' values to its own — the
/// spec's generic recursive mechanism ("each type's value for a changeset is computed from its
/// parents' values"). The engine (<see cref="DerivedDataEngine"/>) owns materialization, caching,
/// persistence, and walk order; a type owns only this computation and nothing else, which is why
/// tree snapshots, skip-graph, and path-history can all register later without the engine or the
/// client learning about any of them.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Name"/> is the stable key agents query over HTTP and the key persisted values
/// are stored under, so it must outlive refactors of the implementing class: once a type's values
/// exist under a name, that name means those values forever.
/// </para>
/// <para>
/// Values are opaque bytes (<see cref="DerivedDataValue"/>), so a type serializes its own
/// structure — the engine never inspects a payload, and stays free of every specific type by
/// construction.
/// </para>
/// </remarks>
public interface IDerivedData
{
    /// <summary>
    /// The registry and storage key — the spec's "path-history", "skip-graph", "tree-snapshots".
    /// Unique among registered types; stable for the life of the persisted data.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Whether the engine materializes this type across the whole log on the boot sweep (eager)
    /// or computes values on first query (deferred). Eager suits indices most queries touch;
    /// deferred suits values only some queries ever need.
    /// </summary>
    bool MaterializeEagerly { get; }

    /// <summary>
    /// Computes this changeset's value from its parents' — the recursion the engine drives.
    /// <paramref name="parentValues"/> is empty for a root and otherwise holds one value per
    /// parent in <see cref="Changeset.Parents"/> order, so a merge sees both sides.
    /// </summary>
    /// <remarks>
    /// Must be pure: the same changeset over the same parent values must always yield the same
    /// payload, because results are persisted and reused by content hash, and because concurrent
    /// queries may compute the same value more than once and must agree.
    /// </remarks>
    DerivedDataValue Compute(Changeset changeset, ReadOnlySpan<DerivedDataValue> parentValues);
}
