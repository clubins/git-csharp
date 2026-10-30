using System.Collections.Immutable;

namespace Scion.Core;

/// <summary>
/// An immutable path-to-blob mapping — the derived "tree" at a point in history. Trees are never
/// stored per changeset: <see cref="FromChain(System.Collections.Generic.IEnumerable{Changeset})"/>
/// replays a first-parent changeset chain from the empty tree, and <see cref="Apply(Changeset)"/>
/// derives the next snapshot from a delta while sharing structure with the snapshot it starts
/// from, so the cost of accepting a change tracks the delta, not the tree.
/// </summary>
/// <remarks>
/// <para>Entries are keyed and enumerated in ordinal path order: two snapshots describing the same
/// tree enumerate identically and compare equal regardless of the chains that produced them, and
/// <see cref="Id"/> — the SHA-256 of the canonical encoding — makes that identity
/// content-addressed, so identical trees deduplicate by hash.</para>
/// <para>Renames are applied by convention: a <see cref="ChangeType.Renamed"/> entry upserts its
/// post-rename <see cref="ChangeEntry.Path"/>, and the producer emits the old path's removal as a
/// separate <see cref="ChangeType.Deleted"/> entry in the same delta — the rename entry itself
/// does not name the pre-rename path.</para>
/// <para>This type is pure in-memory derived data: no storage, caching, or derived-data engine
/// wiring lives here. Consumers materialize and cache snapshots by <see cref="Id"/>.</para>
/// </remarks>
public sealed record TreeSnapshot(ImmutableSortedDictionary<string, TreeEntry> Entries)
{
    private string? _id;

    /// <summary>The empty tree — the starting point for replaying a root changeset.</summary>
    public static TreeSnapshot Empty { get; } =
        new(ImmutableSortedDictionary<string, TreeEntry>.Empty.WithComparers(StringComparer.Ordinal));

    /// <summary>The number of paths in the snapshot.</summary>
    public int Count => Entries.Count;

    /// <summary>
    /// The lowercase hex SHA-256 of this snapshot's canonical encoding (see
    /// <see cref="CanonicalTreeSerializer"/>). Computed on first access and cached — snapshots are
    /// immutable, so the value is stable for the instance's lifetime; concurrent first accesses
    /// may compute it more than once and always agree.
    /// </summary>
    public string Id => _id ??= CanonicalTreeSerializer.ComputeId(this);

    /// <summary>Returns the entry mapped to <paramref name="path"/>, or false when the path is absent.</summary>
    public bool TryGet(string path, out TreeEntry? entry)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Entries.TryGetValue(path, out entry);
    }

    /// <summary>
    /// Applies one changeset's changes — the delta relative to its first parent — producing the
    /// next snapshot; equivalent to applying <see cref="Changeset.Changes"/>.
    /// </summary>
    public TreeSnapshot Apply(Changeset changeset)
    {
        ArgumentNullException.ThrowIfNull(changeset);
        return Apply(changeset.Changes);
    }

    /// <summary>
    /// Applies a delta — one changeset's change entries, relative to that changeset's first
    /// parent — and returns the next snapshot, leaving this one untouched. Entries share structure
    /// with the previous snapshot, so cost tracks the delta, not the tree.
    /// </summary>
    /// <remarks>
    /// The delta must be well-formed against this snapshot: an <see cref="ChangeType.Added"/> path
    /// must not exist yet; <see cref="ChangeType.Modified"/> and <see cref="ChangeType.Deleted"/>
    /// paths must exist with the blob their <see cref="ChangeEntry.OldBlob"/> names; and every
    /// entry's blob nullability must follow the <see cref="ChangeEntry"/> contract. A violation
    /// throws — replaying a delta against the wrong parent's tree is a chain bug, not a conflict
    /// to merge. Entries are applied in the order given.
    /// </remarks>
    public TreeSnapshot Apply(ImmutableArray<ChangeEntry> delta)
    {
        if (delta.IsDefault)
        {
            return this;
        }

        var entries = Entries;
        foreach (var change in delta)
        {
            ValidateDeltaEntry(change, entries);
            entries = change.Type switch
            {
                ChangeType.Deleted => entries.Remove(change.Path),
                _ => entries.SetItem(change.Path, new TreeEntry(change.NewBlob!, change.Kind)),
            };
        }

        return ReferenceEquals(entries, Entries) ? this : new TreeSnapshot(entries);
    }

    /// <summary>Replays a first-parent chain — starting at a root changeset — from the empty tree.</summary>
    /// <exception cref="ArgumentException">The chain does not start at a root, a changeset's first
    /// parent is not the preceding changeset, or a delta is malformed.</exception>
    public static TreeSnapshot FromChain(IEnumerable<Changeset> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        return Replay(Empty, chain, requireRootStart: true, paramName: nameof(chain));
    }

    /// <summary>
    /// Replays a first-parent chain onto a base snapshot, skipping the ancestors it already
    /// contains — the efficient path for deriving a child's tree from a cached parent snapshot.
    /// <paramref name="baseTree"/> must be the tree at the first changeset's first parent; the
    /// snapshot carries no changeset id, so the caller asserts this.
    /// </summary>
    /// <exception cref="ArgumentException">The first changeset is a root (a root builds from the
    /// empty tree), a changeset's first parent is not the preceding changeset, or a delta is
    /// malformed.</exception>
    public static TreeSnapshot FromChain(TreeSnapshot baseTree, IEnumerable<Changeset> chain)
    {
        ArgumentNullException.ThrowIfNull(baseTree);
        ArgumentNullException.ThrowIfNull(chain);
        return Replay(baseTree, chain, requireRootStart: false, paramName: nameof(chain));
    }

    private static TreeSnapshot Replay(TreeSnapshot start, IEnumerable<Changeset> chain, bool requireRootStart, string paramName)
    {
        var current = start;
        Changeset? previous = null;
        foreach (var changeset in chain)
        {
            ValidateChainLink(changeset, previous, requireRootStart, paramName);
            current = current.Apply(changeset);
            previous = changeset;
        }

        return current;
    }

    private static void ValidateChainLink(Changeset changeset, Changeset? previous, bool requireRootStart, string paramName)
    {
        if (previous is null)
        {
            if (requireRootStart && changeset.Parents.Length != 0)
            {
                throw new ArgumentException(
                    $"The first changeset in the chain must be a root, but '{changeset.Id}' has {changeset.Parents.Length} parent(s).",
                    paramName);
            }

            if (!requireRootStart && changeset.Parents.Length == 0)
            {
                throw new ArgumentException(
                    $"The first changeset in the chain must have a first parent when replaying onto a base tree; a root builds from {nameof(Empty)}.",
                    paramName);
            }

            return;
        }

        if (changeset.Parents.Length == 0)
        {
            throw new ArgumentException($"Changeset '{changeset.Id}' is a root and cannot extend the chain after '{previous.Id}'.", paramName);
        }

        if (!string.Equals(changeset.Parents[0], previous.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Changeset '{changeset.Id}' does not extend the chain: its first parent is '{changeset.Parents[0]}', expected '{previous.Id}'.",
                paramName);
        }
    }

    private static void ValidateDeltaEntry(ChangeEntry change, ImmutableSortedDictionary<string, TreeEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(change.Path))
        {
            throw new ArgumentException("A change entry must name a non-empty path.");
        }

        if (!Enum.IsDefined(change.Type))
        {
            throw new ArgumentException($"Unknown {nameof(ChangeType)} value {change.Type} for path '{change.Path}'.");
        }

        if (!Enum.IsDefined(change.Kind))
        {
            throw new ArgumentException($"Unknown {nameof(FileContentKind)} value {change.Kind} for path '{change.Path}'.");
        }

        var oldBlobSet = change.OldBlob is not null;
        var newBlobSet = change.NewBlob is not null;
        var blobsFollowContract = change.Type switch
        {
            // OldBlob is null exactly when the delta adds the path; NewBlob exactly when it deletes it.
            ChangeType.Added => !oldBlobSet && newBlobSet,
            ChangeType.Deleted => oldBlobSet && !newBlobSet,
            ChangeType.Modified or ChangeType.Renamed => oldBlobSet && newBlobSet,
            _ => false,
        };

        if (!blobsFollowContract)
        {
            throw new ArgumentException(
                $"The {change.Type} entry for '{change.Path}' violates the change-entry blob contract (OldBlob set: {oldBlobSet}, NewBlob set: {newBlobSet}).");
        }

        switch (change.Type)
        {
            case ChangeType.Added:
                if (entries.ContainsKey(change.Path))
                {
                    throw new ArgumentException($"The {ChangeType.Added} entry for '{change.Path}' conflicts with the snapshot: the path already exists.");
                }

                break;
            case ChangeType.Modified or ChangeType.Deleted:
                RequireExistingBlobMatches(change, entries);
                break;
        }

        // Renamed carries no pre-rename path, so there is nothing to check against the snapshot:
        // the new path is upserted, and the producer removes the old path with its own Deleted entry.
    }

    private static void RequireExistingBlobMatches(ChangeEntry change, ImmutableSortedDictionary<string, TreeEntry> entries)
    {
        if (!entries.TryGetValue(change.Path, out var existing))
        {
            throw new ArgumentException($"The {change.Type} entry for '{change.Path}' assumes the path exists, but the snapshot does not contain it.");
        }

        if (!string.Equals(existing.BlobHash, change.OldBlob, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"The {change.Type} entry for '{change.Path}' names old blob '{change.OldBlob}', but the snapshot maps the path to '{existing.BlobHash}'.");
        }
    }

    /// <summary>Compares entries element-wise; both sides enumerate in ordinal path order.</summary>
    public bool Equals(TreeSnapshot? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return Count == other.Count && Entries.SequenceEqual(other.Entries);
    }

    /// <summary>Hashes the same structure <see cref="Equals"/> compares.</summary>
    public override int GetHashCode()
    {
        var hashCode = new System.HashCode();
        foreach (var (path, entry) in Entries)
        {
            hashCode.Add(path, StringComparer.Ordinal);
            hashCode.Add(entry);
        }

        return hashCode.ToHashCode();
    }
}
