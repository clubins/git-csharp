namespace Scion.Core.Derived;

/// <summary>
/// The "tree-snapshots" derived type: the materialized directory state at each changeset — the
/// spec's derived tree powering diff and checkout. A value's payload is the snapshot's canonical
/// encoding (see <see cref="CanonicalTreeSerializer"/>), so the value's content hash is the
/// snapshot's <see cref="TreeSnapshot.Id"/> — identical trees deduplicate by hash — and
/// <see cref="Decode"/> reads a stored value back into the snapshot it holds.
/// </summary>
/// <remarks>
/// <para>
/// Materializes eagerly (<see cref="IDerivedData.MaterializeEagerly"/>): diff and checkout are the
/// operations every agent runs, and the engine's incremental sweep derives each changeset's
/// snapshot from its first parent's at the cost of the delta — commit cost stays O(change) — so no
/// query ever pays a first-query recursion for the object model's most-queried value. Contrast
/// <see cref="SkipGraphDerivedData"/>, whose towers only merge queries need and which therefore
/// computes on first query.
/// </para>
/// <para>
/// The recursion follows the data model's first-parent convention: a changeset's delta is relative
/// to <see cref="Changeset.Parents"/>[0], so the tree at a merge is its first parent's tree plus
/// its delta — second-parent content reaches a merge through its parents, never its changes. A
/// root builds from <see cref="TreeSnapshot.Empty"/>.
/// </para>
/// </remarks>
public sealed class TreeSnapshotDerivedData : IDerivedData
{
    /// <summary>The registry and storage key — the spec's name for this type.</summary>
    public const string TypeName = "tree-snapshots";

    /// <inheritdoc />
    public string Name => TypeName;

    /// <inheritdoc />
    public bool MaterializeEagerly => true;

    /// <inheritdoc />
    public DerivedDataValue Compute(Changeset changeset, ReadOnlySpan<DerivedDataValue> parentValues)
    {
        ArgumentNullException.ThrowIfNull(changeset);

        var parentTree = parentValues.IsEmpty
            ? TreeSnapshot.Empty
            : CanonicalTreeSerializer.Deserialize(parentValues[0].Payload.Span);
        return DerivedDataValue.FromPayload(CanonicalTreeSerializer.Serialize(parentTree.Apply(changeset.Changes)));
    }

    /// <summary>
    /// Reads a stored value back into the snapshot it holds — the read side of the value whose
    /// content hash is the snapshot's id.
    /// </summary>
    /// <exception cref="InvalidDataException">The payload is not a canonical tree encoding.</exception>
    public static TreeSnapshot Decode(DerivedDataValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return CanonicalTreeSerializer.Deserialize(value.Payload.Span);
    }
}
