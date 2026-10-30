using Scion.Core.Storage;

namespace Scion.Server.Endpoints;

/// <summary>
/// The rebase response (201 in both cases): the replayed chain — one rebased changeset per source
/// changeset, first-parent linked onto the new base — plus any conflicts the replay hit (the
/// replay never aborts; conflicted paths keep the new base's state) and the ref state after the
/// optional move, which targets the rebased tip.
/// </summary>
internal sealed record RebaseResponse(string NewBase, RebasedChangesetResponse[] Rebased, RebaseConflictResponse[] Conflicts, RefState? Ref);
