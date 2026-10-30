namespace Scion.Server.Endpoints;

/// <summary>One replayed changeset: the source it came from and the new changeset the replay produced.</summary>
internal sealed record RebasedChangesetResponse(string SourceId, string Id);
