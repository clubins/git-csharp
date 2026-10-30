using Scion.Core;

namespace Scion.Server.Endpoints;

/// <summary>
/// The server-side merge request: the two tips to merge (ours is the merge changeset's first
/// parent), the commit metadata for the merge changeset the server creates, and an optional
/// compare-and-swap ref move that publishes it.
/// </summary>
public sealed record MergeRequest
{
    /// <summary>The changeset to merge into — the merge changeset's first parent.</summary>
    public string? Ours { get; init; }

    /// <summary>The changeset to merge — the merge changeset's second parent.</summary>
    public string? Theirs { get; init; }

    /// <summary>The merge changeset's message; may be empty, never null.</summary>
    public string? Message { get; init; }

    /// <summary>Who made the merge changeset.</summary>
    public Author? Author { get; init; }

    /// <summary>Unix timestamp in seconds, UTC — client-supplied so identical payloads hash identically.</summary>
    public long TimestampUtc { get; init; }

    /// <summary>Optional ref move, applied by compare-and-swap after the merge changeset appends.</summary>
    public RefMoveRequest? RefMove { get; init; }

    /// <summary>
    /// A ref move that applies only while the ref is still at <c>ExpectedRevision</c>;
    /// <see cref="Scion.Core.Storage.RefStore.CreationRevision"/> creates a ref that does not exist.
    /// </summary>
    public sealed record RefMoveRequest(string Name, long ExpectedRevision);
}
