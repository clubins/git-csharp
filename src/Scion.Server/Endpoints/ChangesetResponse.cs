using Scion.Core;

namespace Scion.Server.Endpoints;

/// <summary>
/// The wire form of one stored changeset — the canonical record the client hashed, unchanged:
/// parents in order, paths changed relative to the first parent, message, author, timestamp.
/// Built from a log-validated <see cref="Changeset"/>, so serving it never re-exposes unverified bytes.
/// </summary>
internal sealed record ChangesetResponse(string Id, string[] Parents, ChangeEntry[] Changes, string Message, Author Author, long TimestampUtc)
{
    public static ChangesetResponse From(Changeset changeset)
    {
        return new ChangesetResponse(
            changeset.Id,
            [.. changeset.Parents],
            [.. changeset.Changes],
            changeset.Message,
            changeset.Author,
            changeset.TimestampUtc);
    }
}
