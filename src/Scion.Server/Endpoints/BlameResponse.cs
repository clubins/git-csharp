namespace Scion.Server.Endpoints;

/// <summary>
/// A path's blame at one changeset: every line of the file's content there, each with the
/// changeset that introduced it — the path-history chain walked through the line diff. Line
/// content keeps its terminating newline, exactly as the line diff compares it.
/// </summary>
internal sealed record BlameResponse(string Changeset, string Path, BlameLineResponse[] Lines);

/// <summary>One line's attribution: its 1-based number, the changeset that introduced it, its content.</summary>
internal sealed record BlameLineResponse(int Line, string ChangesetId, string Content);
