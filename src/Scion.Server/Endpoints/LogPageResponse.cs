namespace Scion.Server.Endpoints;

/// <summary>
/// One page of the log: the changesets newest-first along first-parent lineage, and the cursor
/// for the next page — the id to send back as <c>after</c> — or null when the walk reached the
/// end of the lineage (the root, or a parent that is not in the log).
/// </summary>
internal sealed record LogPageResponse(ChangesetResponse[] Changesets, string? NextAfter);
