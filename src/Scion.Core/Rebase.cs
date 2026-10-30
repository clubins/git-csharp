using System.Collections.Immutable;
using System.Text;

namespace Scion.Core;

/// <summary>
/// Rebase as replay: walking a chain of changesets in first-parent order and re-applying each
/// one's delta — its changes relative to its old first parent — onto the accumulated tree of the
/// new base, producing new changesets with the same message, author, and timestamp but new
/// first-parent lineage. This is the core primitive behind the server's rebase operation; like
/// <see cref="ThreeWayMerge"/> it is pure in-memory computation: trees arrive as
/// <see cref="TreeSnapshot"/>s, blob text through the caller-supplied lookup, and nothing touches
/// storage.
/// </summary>
/// <remarks>
/// <para>
/// Each step is a three-way merge (see <see cref="ThreeWayMerge.Merge"/>) with the old first
/// parent's tree as base, the new parent's tree as ours, and the source changeset's own tree as
/// theirs: the source's delta applies on top of whatever the new base already changed, the merge
/// conventions decide every path, and the rebased delta is the diff from the new parent's tree to
/// the merged result. Conflicts therefore surface per those conventions — kind reported, ours
/// (the new base) wins the conflicted path — and the replay never aborts: every source changeset
/// yields a rebased changeset, conflicted or not, and later steps replay against the accumulated
/// state, so one conflicted path does not poison the rest of the chain.
/// </para>
/// <para>
/// The replay follows first parents only. A merge changeset in the chain rebases to a non-merge:
/// its <see cref="Changeset.Changes"/> already carry the full delta against its first parent —
/// the merged content, second parent included — so replaying them reproduces the merge's result
/// without naming the second parent. A source changeset whose changes the new base already
/// contains rebases to an empty delta; the replay emits it anyway, keeping the one-to-one
/// source-to-rebased correspondence, because dropping it would re-parent the whole tail (dropping
/// empty rebased changesets is the caller's policy, git-style).
/// </para>
/// </remarks>
public static class Rebase
{
    /// <summary>
    /// Replays <paramref name="chain"/> — first-parent linked, in first-parent order — onto the
    /// changeset named <paramref name="newBaseId"/>, whose tree is <paramref name="newBaseTree"/>.
    /// Message, author, and timestamp carry over; ids differ, because the parents and deltas
    /// changed. <paramref name="oldBaseTree"/> is the tree at <c>chain[0]</c>'s first parent —
    /// the caller asserts this, as with
    /// <see cref="TreeSnapshot.FromChain(TreeSnapshot, System.Collections.Generic.IEnumerable{Changeset})"/>;
    /// when the chain starts at a root it must be <see cref="TreeSnapshot.Empty"/>.
    /// </summary>
    /// <param name="oldBaseTree">The tree at the chain's first parent (empty for a root start).</param>
    /// <param name="chain">The changesets to replay, in first-parent order. Linkage is validated
    /// up front: every changeset after the first must name its predecessor as first parent.</param>
    /// <param name="newBaseId">The id of the changeset the chain rebases onto; it becomes the
    /// first rebased changeset's first parent.</param>
    /// <param name="newBaseTree">The tree at <paramref name="newBaseId"/>.</param>
    /// <param name="contentLookup">Maps a blob hash to its decoded text. Called only for paths
    /// both the new base and the replayed delta changed — never for paths resolved structurally.
    /// Blob text is UTF-8.</param>
    /// <exception cref="ArgumentException">The chain does not extend itself, the chain starts at
    /// a root while <paramref name="oldBaseTree"/> is not empty, or <paramref name="newBaseId"/>
    /// is null or whitespace.</exception>
    public static RebaseResult Replay(
        TreeSnapshot oldBaseTree,
        IReadOnlyList<Changeset> chain,
        string newBaseId,
        TreeSnapshot newBaseTree,
        Func<string, string> contentLookup)
    {
        ArgumentNullException.ThrowIfNull(oldBaseTree);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentException.ThrowIfNullOrWhiteSpace(newBaseId);
        ArgumentNullException.ThrowIfNull(newBaseTree);
        ArgumentNullException.ThrowIfNull(contentLookup);

        if (chain.Count == 0)
        {
            return new RebaseResult([], [], ImmutableDictionary<string, string>.Empty);
        }

        if (chain[0].Parents.Length == 0 && !oldBaseTree.Equals(TreeSnapshot.Empty))
        {
            throw new ArgumentException(
                "The chain starts at a root, whose delta is relative to the empty tree; the old base tree must be empty.",
                nameof(oldBaseTree));
        }

        ValidateChain(chain);

        var rebased = ImmutableArray.CreateBuilder<Changeset>(chain.Count);
        var conflicts = ImmutableArray.CreateBuilder<RebaseConflict>();
        var mergedBlobs = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

        var oldTree = oldBaseTree;
        var newTree = newBaseTree;
        var newParentId = newBaseId;
        for (var sourceIndex = 0; sourceIndex < chain.Count; sourceIndex++)
        {
            var source = chain[sourceIndex];

            // The source's tree: its stored delta applied to its old first parent's tree. Apply
            // validates the delta against the tree, so a malformed chain fails here rather than
            // producing a wrong tree.
            var sourceTree = oldTree.Apply(source.Changes);

            var merged = ThreeWayMerge.Merge(oldTree, newTree, sourceTree, contentLookup);

            // The rebased delta: what the merged result adds to the new parent's tree. When the
            // new base already contained the source's change this is empty — the emitted, empty
            // changeset marks the step's position in the lineage.
            var delta = TreeDiff.Diff(newTree, merged.Tree);
            var rebasedChangeset = Changeset.Create([newParentId], delta, source.Message, source.Author, source.TimestampUtc);
            rebased.Add(rebasedChangeset);

            foreach (var conflict in merged.Conflicts)
            {
                conflicts.Add(new RebaseConflict(sourceIndex, source.Id, rebasedChangeset.Id, conflict));
            }

            // Re-key the merge's synthesized contents by blob hash: a later step may re-synthesize
            // the same path, and every rebased delta references its own blob — keyed by path, an
            // earlier step's blob text would be lost. Blob hashes are deterministic in the
            // content, so the re-key is exact and identical texts collapse onto one entry.
            foreach (var (_, text) in merged.MergedContents)
            {
                mergedBlobs[Blob.Create(Encoding.UTF8.GetBytes(text)).Hash] = text;
            }

            oldTree = sourceTree;
            newTree = merged.Tree;
            newParentId = rebasedChangeset.Id;
        }

        return new RebaseResult(rebased.ToImmutable(), conflicts.ToImmutable(), mergedBlobs.ToImmutable());
    }

    private static void ValidateChain(IReadOnlyList<Changeset> chain)
    {
        for (var i = 1; i < chain.Count; i++)
        {
            var changeset = chain[i];
            if (changeset.Parents.Length == 0)
            {
                throw new ArgumentException(
                    $"Changeset '{changeset.Id}' at index {i} is a root and cannot extend the chain after '{chain[i - 1].Id}'.",
                    nameof(chain));
            }

            if (!string.Equals(changeset.Parents[0], chain[i - 1].Id, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Changeset '{changeset.Id}' at index {i} does not extend the chain: its first parent is '{changeset.Parents[0]}', expected '{chain[i - 1].Id}'.",
                    nameof(chain));
            }
        }
    }
}
