using System.Collections.Immutable;

namespace Scion.Core;

/// <summary>
/// One path a replayed changeset's delta could not reconcile onto the new base (see
/// <see cref="Rebase.Replay"/>). The rebased tree keeps ours' state — the new base's — per the
/// merge conventions; <see cref="Conflict"/> carries the path, kind, blobs, and, for content
/// conflicts, the conflict-marked merged text.
/// </summary>
/// <param name="SourceIndex">The source changeset's index in the replayed chain.</param>
/// <param name="SourceId">The source changeset's id.</param>
/// <param name="RebasedId">The id of the rebased changeset the conflict attaches to.</param>
/// <param name="Conflict">The merge conflict as <see cref="ThreeWayMerge.Merge"/> reported it;
/// ours is the new base's side, theirs the replayed changeset's.</param>
public sealed record RebaseConflict(
    int SourceIndex,
    string SourceId,
    string RebasedId,
    MergeConflict Conflict);

/// <summary>
/// The outcome of replaying a chain of changesets onto a new base (see
/// <see cref="Rebase.Replay"/>): one rebased changeset per source changeset, first-parent linked,
/// the first parent being the new base.
/// </summary>
/// <param name="Rebased">The rebased chain, in source order.</param>
/// <param name="Conflicts">One entry per conflicted path per replay step; empty when every step
/// replayed cleanly. The replay never aborts on conflict — <see cref="Rebased"/> is complete
/// either way, and conflicted paths keep the new base's state in the rebased trees.</param>
/// <param name="MergedBlobs">Content the replay synthesized — clean line-level merges of both-side
/// edits — keyed by the blob hash of the merged text, so the caller can store each new blob before
/// serving deltas that reference it. Content selected wholesale from one side is not synthesized;
/// conflict-marker texts are not here either — the rebased tree never references them, so they
/// ride on <see cref="RebaseConflict"/>. Keyed by hash rather than path because later steps may
/// re-synthesize the same path: every blob a rebased delta references keeps its text.</param>
public sealed record RebaseResult(
    ImmutableArray<Changeset> Rebased,
    ImmutableArray<RebaseConflict> Conflicts,
    ImmutableDictionary<string, string> MergedBlobs)
{
    /// <summary>True when every step replayed cleanly and <see cref="Rebased"/> is appendable as-is.</summary>
    public bool IsClean => Conflicts.IsEmpty;
}
