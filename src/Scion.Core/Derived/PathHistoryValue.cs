using System.Collections.Immutable;

namespace Scion.Core.Derived;

/// <summary>
/// The in-memory form of one path-history value: every tracked path's chain — the ordered sequence
/// of changesets that touched it, oldest first. This is the spec's "path-history" index, the data
/// behind log-path queries, file history, and blame; one value answers every path's question at
/// one changeset. <see cref="Apply"/> folds one changeset's delta into the value — the recursion
/// <see cref="PathHistoryDerivedData"/> registers with the derived-data engine, which persists the
/// canonical encoding (<see cref="CanonicalPathHistorySerializer"/>) by content hash.
/// </summary>
/// <remarks>
/// <para>
/// Chains accumulate per path: every changeset that touched a path appends itself, so a path
/// deleted and later re-added keeps one continuing chain. Renames are the one exception — the
/// tree-diff rename convention emits the old path's removal as its own Deleted entry plus a
/// Renamed entry on the new path, and a Renamed entry threads the source path's accumulated chain
/// onto the destination: the file's history follows the file. The old path's own chain also ends
/// with the renaming changeset (its removal touched it), so both sides of the move can answer.
/// A rename whose source deletion is absent — a hand-built delta outside the convention — falls
/// back to the destination path's own chain, and when several deleted paths carry the renamed
/// entry's blob (identical content moved twice), pairing is deterministic: ordinal path order,
/// mirroring <see cref="TreeDiff"/>'s one-to-one rule.
/// </para>
/// <para>
/// Folding one delta is pure and order-independent: chains grow from this value's state, never
/// from a delta's own appends, so the same delta applied to the same value always yields the same
/// result — the property the derived-value store relies on when it refuses disagreeing writes.
/// </para>
/// <para>
/// Entries are keyed and enumerated in ordinal path order (see <see cref="Empty"/>) and chains
/// accumulate in lineage order, so two values describing the same history enumerate identically
/// and serialize — and hash — to the same canonical bytes.
/// </para>
/// </remarks>
public sealed record PathHistoryValue(ImmutableSortedDictionary<string, ImmutableArray<string>> Entries)
{
    /// <summary>No path has a history yet — the starting point for replaying a root changeset.</summary>
    public static PathHistoryValue Empty { get; } = new(
        ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty.WithComparers(StringComparer.Ordinal));

    /// <summary>The number of paths this value tracks a history for.</summary>
    public int Count => Entries.Count;

    /// <summary>
    /// The changesets that touched <paramref name="path"/>, oldest first — empty when the path was
    /// never touched as of this value's changeset.
    /// </summary>
    public ImmutableArray<string> HistoryOf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Entries.TryGetValue(path, out var chain) ? chain : [];
    }

    /// <summary>
    /// Folds one changeset's delta — relative to that changeset's first parent — into this value,
    /// returning the value at that changeset and leaving this one untouched. Every entry appends
    /// <paramref name="changesetId"/> to its path's chain; a Renamed entry instead threads the
    /// paired source path's chain onto the destination path (see the type remarks).
    /// </summary>
    /// <remarks>
    /// Entries must follow the change-entry blob contract — the same shape rule
    /// <see cref="TreeSnapshot.Apply(ImmutableArray{ChangeEntry})"/> enforces — and a violation
    /// throws: folding a malformed delta is a producer bug, not a conflict to merge. Unlike tree
    /// snapshots, path history tracks no existence state, so no delta entry is checked against the
    /// map — a path deleted and re-added simply keeps accumulating.
    /// </remarks>
    /// <exception cref="ArgumentException">The changeset id is blank, or an entry violates the change-entry blob contract.</exception>
    public PathHistoryValue Apply(string changesetId, ImmutableArray<ChangeEntry> changes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changesetId);
        if (changes.IsDefault || changes.IsEmpty)
        {
            return this;
        }

        foreach (var change in changes)
        {
            ValidateEntry(change);
        }

        var sources = ResolveRenameSources(changes);
        var entries = Entries;
        foreach (var change in changes)
        {
            var chain = sources.TryGetValue(change.Path, out var sourceChain)
                ? sourceChain
                : ChainOf(Entries, change.Path);
            entries = entries.SetItem(change.Path, chain.Add(changesetId));
        }

        return ReferenceEquals(entries, Entries) ? this : new PathHistoryValue(entries);
    }

    /// <summary>
    /// For each Renamed entry, the paired source deletion's path chain from this (pre-apply)
    /// value — looked up before any appends, so a rename continues from where its file's chain
    /// left off, never from this delta's own writes. Unpaired renames are absent from the result
    /// and fall back to the destination path's own chain.
    /// </summary>
    private Dictionary<string, ImmutableArray<string>> ResolveRenameSources(ImmutableArray<ChangeEntry> changes)
    {
        var sources = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var renamed in OrderedByPath(changes, ChangeType.Renamed))
        {
            foreach (var deleted in OrderedByPath(changes, ChangeType.Deleted))
            {
                if (deleted.Path != renamed.Path
                    && !consumed.Contains(deleted.Path)
                    && string.Equals(deleted.OldBlob, renamed.OldBlob, StringComparison.OrdinalIgnoreCase))
                {
                    consumed.Add(deleted.Path);
                    sources[renamed.Path] = ChainOf(Entries, deleted.Path);
                    break;
                }
            }
        }

        return sources;
    }

    /// <summary>The entries of one type in ordinal path order — the pairing order TreeDiff pairs in.</summary>
    private static IEnumerable<ChangeEntry> OrderedByPath(ImmutableArray<ChangeEntry> changes, ChangeType type)
    {
        return changes
            .Where(change => change.Type == type)
            .OrderBy(change => change.Path, StringComparer.Ordinal);
    }

    /// <summary>
    /// The change-entry blob contract: OldBlob is null exactly when the delta adds the path,
    /// NewBlob exactly when it deletes it. Mirrors <see cref="TreeSnapshot"/>'s delta validation —
    /// the duplication is deliberate: extracting a shared check would reach into a merged type
    /// this delta consumer only reads from.
    /// </summary>
    private static void ValidateEntry(ChangeEntry change)
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
    }

    /// <summary>The accumulated chain for a path, or empty when the value does not track one yet.</summary>
    private static ImmutableArray<string> ChainOf(
        ImmutableSortedDictionary<string, ImmutableArray<string>> entries,
        string path)
    {
        return entries.TryGetValue(path, out var chain) && !chain.IsDefault ? chain : [];
    }

    /// <summary>Encodes this value to its canonical form — the bytes the engine persists by content hash.</summary>
    public byte[] Serialize()
    {
        return CanonicalPathHistorySerializer.Serialize(this);
    }

    /// <summary>Reads a canonical encoding back — the store's payload bytes become a queryable value.</summary>
    /// <exception cref="InvalidDataException">The bytes are truncated, trailing, or not canonical-format v1.</exception>
    public static PathHistoryValue Deserialize(ReadOnlySpan<byte> payload)
    {
        return CanonicalPathHistorySerializer.Deserialize(payload);
    }

    /// <summary>Compares every path's chain element-wise; both sides enumerate in ordinal path order.</summary>
    public bool Equals(PathHistoryValue? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Count != other.Count)
        {
            return false;
        }

        foreach (var (path, chain) in Entries)
        {
            if (!other.Entries.TryGetValue(path, out var otherChain)
                || !chain.SequenceEqual(otherChain, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Hashes the same structure <see cref="Equals"/> compares.</summary>
    public override int GetHashCode()
    {
        var hashCode = new System.HashCode();
        foreach (var (path, chain) in Entries)
        {
            hashCode.Add(path, StringComparer.Ordinal);
            hashCode.Add(chain.Length);
            foreach (var changesetId in chain)
            {
                hashCode.Add(changesetId, StringComparer.Ordinal);
            }
        }

        return hashCode.ToHashCode();
    }
}
