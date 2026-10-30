using Scion.Core;

namespace Scion.Server.Endpoints;

/// <summary>
/// The wire form of one path a merge or rebase could not reconcile (spec C7): the path, why it
/// conflicted, the blob at the base and on each side, and — for content conflicts — the merged
/// text with conflict markers, so an agent can resolve without recomputing the merge. Ours is the
/// merge changeset's first parent (merge) or the new base (rebase); rename conflicts name the
/// targets each side moved to.
/// </summary>
internal sealed record MergeConflictResponse(
    string Path,
    MergeConflictKind Kind,
    string? BaseBlob,
    string? OursBlob,
    string? TheirsBlob,
    string? MergedContent,
    string? OursTarget,
    string? TheirsTarget)
{
    public static MergeConflictResponse From(MergeConflict conflict)
    {
        return new MergeConflictResponse(
            conflict.Path,
            conflict.Kind,
            conflict.BaseBlob,
            conflict.OursBlob,
            conflict.TheirsBlob,
            conflict.MergedContent,
            conflict.OursTarget,
            conflict.TheirsTarget);
    }
}
