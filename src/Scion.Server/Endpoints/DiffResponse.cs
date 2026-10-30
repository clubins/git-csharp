using Scion.Core;

namespace Scion.Server.Endpoints;

/// <summary>
/// The delta between two changesets' tree snapshots: every changed path in canonical change
/// order, the same shape a changeset's own delta carries. Renames follow the tree-diff
/// convention — the old path's deletion plus a renamed entry on the new path — so the output
/// composes with the changeset model unchanged.
/// </summary>
internal sealed record DiffResponse(string From, string To, ChangeEntry[] Changes);
