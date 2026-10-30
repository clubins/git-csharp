namespace Scion.Core.Derived;

/// <summary>
/// The spec's "path-history" index as a derived type: for every path, the sequence of changesets
/// that touched it, folded from parent values one changeset at a time
/// (<see cref="PathHistoryValue.Apply"/> is the recursion). Registered against the
/// <see cref="DerivedDataEngine"/>, a point query for one changeset's value answers every path's
/// history as of that changeset; <see cref="PathHistoryValue.HistoryOf"/> reads a path back.
/// </summary>
/// <remarks>
/// <para>
/// A changeset's value folds its <em>first parent's</em> value with the changeset's own delta —
/// the same first-parent lineage the changeset model is built on (deltas are relative to the
/// first parent, and trees replay first-parent changes). A merge's second parent value is not
/// consulted: the merged side's history stays queryable at the second parent's own changeset,
/// while the tip's answers what its trunk lineage did to a path.
/// </para>
/// <para>
/// Eagerly materialized: log-path and blame queries are the API's hot path, so the boot sweep
/// fills every value it can reach and first queries never pay computation latency.
/// </para>
/// </remarks>
public sealed class PathHistoryDerivedData : IDerivedData
{
    /// <summary>The registry and storage key — the spec's name for this index.</summary>
    public const string TypeName = "path-history";

    /// <inheritdoc/>
    public string Name => TypeName;

    /// <inheritdoc/>
    public bool MaterializeEagerly => true;

    /// <inheritdoc/>
    public DerivedDataValue Compute(Changeset changeset, ReadOnlySpan<DerivedDataValue> parentValues)
    {
        ArgumentNullException.ThrowIfNull(changeset);

        var parent = parentValues.IsEmpty
            ? PathHistoryValue.Empty
            : PathHistoryValue.Deserialize(parentValues[0].Payload.Span);
        var updated = parent.Apply(changeset.Id, changeset.Changes);
        return DerivedDataValue.FromPayload(updated.Serialize());
    }
}
