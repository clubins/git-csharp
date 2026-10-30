using System.Collections.Immutable;

namespace Scion.Core;

/// <summary>
/// Computes the delta between two <see cref="TreeSnapshot"/>s as a <see cref="ChangeEntry"/> set —
/// the shape <see cref="TreeSnapshot.Apply(ImmutableArray{ChangeEntry})"/> consumes, so
/// <c>before.Apply(TreeDiff.Diff(before, after))</c> equals <c>after</c>. A single merge sweep over
/// the two ordinal-ordered entry maps classifies every nested path, so the cost tracks the union of
/// both trees, not the depth of any directory spine: paths carry their full directory path, and
/// every level is covered by the same sweep.
/// </summary>
/// <remarks>
/// <para>
/// Renames are detected by content: a path present only in <paramref name="before"/> pairs with a
/// path present only in <paramref name="after"/> when both name the same blob (hash compared
/// case-insensitively, like <see cref="TreeEntry"/> equality). Each pair is emitted in the
/// <see cref="TreeSnapshot"/> rename convention — the old path's <see cref="ChangeType.Deleted"/>
/// plus a <see cref="ChangeType.Renamed"/> entry on the new path — so the output composes with
/// <see cref="Changeset.Create"/> unchanged. Pairing is one-to-one and deterministic: per blob
/// hash, the i-th deleted path in ordinal order pairs with the i-th added path. A detected rename
/// never rewrites content — its Renamed entry carries equal blob hashes — so a move that also
/// edits content diffs as a plain delete plus add.
/// </para>
/// <para>
/// Output is in canonical change order (see <see cref="CanonicalChangeOrder"/>): the same pair of
/// snapshots always yields the same sequence, and because every entry names a distinct path the
/// delta applies correctly regardless of entry order.
/// </para>
/// </remarks>
public static class TreeDiff
{
    /// <summary>One side of a rename candidate: a path present in exactly one of the two snapshots.</summary>
    private readonly record struct PathChange(string Path, string BlobHash, FileContentKind Kind);

    /// <summary>
    /// Classifies every path of <paramref name="before"/> against <paramref name="after"/>: paths
    /// only in <paramref name="before"/> are deleted, paths only in <paramref name="after"/> are
    /// added, and paths in both compare by blob hash and kind — an unchanged path produces no
    /// entry, a changed path a <see cref="ChangeType.Modified"/> entry carrying the new kind.
    /// </summary>
    public static ImmutableArray<ChangeEntry> Diff(TreeSnapshot before, TreeSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var deletions = new List<PathChange>();
        var additions = new List<PathChange>();
        var modifications = new List<ChangeEntry>();

        using var beforeEntries = before.Entries.GetEnumerator();
        using var afterEntries = after.Entries.GetEnumerator();
        var hasBefore = beforeEntries.MoveNext();
        var hasAfter = afterEntries.MoveNext();
        while (hasBefore || hasAfter)
        {
            switch (hasBefore, hasAfter)
            {
                case (true, false):
                    deletions.Add(ToPathChange(beforeEntries.Current));
                    hasBefore = beforeEntries.MoveNext();
                    break;

                case (false, true):
                    additions.Add(ToPathChange(afterEntries.Current));
                    hasAfter = afterEntries.MoveNext();
                    break;

                case (true, true):
                    var order = string.CompareOrdinal(beforeEntries.Current.Key, afterEntries.Current.Key);
                    if (order < 0)
                    {
                        deletions.Add(ToPathChange(beforeEntries.Current));
                        hasBefore = beforeEntries.MoveNext();
                    }
                    else if (order > 0)
                    {
                        additions.Add(ToPathChange(afterEntries.Current));
                        hasAfter = afterEntries.MoveNext();
                    }
                    else
                    {
                        if (!beforeEntries.Current.Value.Equals(afterEntries.Current.Value))
                        {
                            modifications.Add(new ChangeEntry(
                                beforeEntries.Current.Key,
                                beforeEntries.Current.Value.BlobHash,
                                afterEntries.Current.Value.BlobHash,
                                afterEntries.Current.Value.Kind,
                                ChangeType.Modified));
                        }

                        hasBefore = beforeEntries.MoveNext();
                        hasAfter = afterEntries.MoveNext();
                    }

                    break;
            }
        }

        return Assemble(deletions, additions, modifications);
    }

    /// <summary>
    /// Pairs rename candidates, then emits the full delta in canonical order: every deletion as a
    /// Deleted entry (renamed or not — the convention removes the old path with its own entry),
    /// each paired addition as a Renamed entry on the new path, each unpaired addition as Added,
    /// and the modifications unchanged.
    /// </summary>
    private static ImmutableArray<ChangeEntry> Assemble(
        List<PathChange> deletions,
        List<PathChange> additions,
        List<ChangeEntry> modifications)
    {
        var additionsByHash = new Dictionary<string, Queue<PathChange>>(StringComparer.OrdinalIgnoreCase);
        foreach (var added in additions)
        {
            if (!additionsByHash.TryGetValue(added.BlobHash, out var queue))
            {
                queue = new Queue<PathChange>();
                additionsByHash.Add(added.BlobHash, queue);
            }

            queue.Enqueue(added);
        }

        var changes = new List<ChangeEntry>(deletions.Count + additions.Count + modifications.Count);
        var renamedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var deleted in deletions)
        {
            changes.Add(new ChangeEntry(deleted.Path, deleted.BlobHash, null, deleted.Kind, ChangeType.Deleted));
            if (additionsByHash.TryGetValue(deleted.BlobHash, out var queue) && queue.Count > 0)
            {
                var renamed = queue.Dequeue();
                renamedPaths.Add(renamed.Path);
                changes.Add(new ChangeEntry(renamed.Path, deleted.BlobHash, renamed.BlobHash, renamed.Kind, ChangeType.Renamed));
            }
        }

        foreach (var added in additions)
        {
            if (!renamedPaths.Contains(added.Path))
            {
                changes.Add(new ChangeEntry(added.Path, null, added.BlobHash, added.Kind, ChangeType.Added));
            }
        }

        changes.AddRange(modifications);
        changes.Sort(CanonicalChangeOrder.Instance);
        return changes.ToImmutableArray();
    }

    private static PathChange ToPathChange(KeyValuePair<string, TreeEntry> entry)
    {
        return new PathChange(entry.Key, entry.Value.BlobHash, entry.Value.Kind);
    }
}
