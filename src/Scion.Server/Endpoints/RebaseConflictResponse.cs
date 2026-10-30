using Scion.Core;

namespace Scion.Server.Endpoints;

/// <summary>
/// One path a replayed changeset's delta could not reconcile onto the new base (spec C7): the
/// source step it belongs to, the rebased changeset that carries it, and the conflict itself —
/// kind, blobs, and the conflict-marked merged text for content conflicts. Ours is the new base.
/// </summary>
internal sealed record RebaseConflictResponse(int SourceIndex, string SourceId, string RebasedId, MergeConflictResponse Conflict)
{
    public static RebaseConflictResponse From(RebaseConflict conflict)
    {
        return new RebaseConflictResponse(
            conflict.SourceIndex,
            conflict.SourceId,
            conflict.RebasedId,
            MergeConflictResponse.From(conflict.Conflict));
    }
}
