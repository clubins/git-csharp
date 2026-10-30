using Scion.Core;
using Scion.Core.Storage;

namespace Scion.Server.Endpoints;

/// <summary>
/// The create-changeset request: the changeset record as the client computed it (the id is
/// re-derived from the canonical payload server-side and mismatches are rejected — 400), the
/// changed blobs it references, and an optional ref move that publishes the commit by
/// compare-and-swap.
/// </summary>
public sealed record CreateChangesetRequest
{
    /// <summary>The client-computed changeset id: the SHA-256 of the canonical payload encoding.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Parents in order — empty for a root, at most two (a merge).</summary>
    public List<string>? Parents { get; init; }

    /// <summary>Paths changed relative to the first parent.</summary>
    public List<ChangeEntry>? Changes { get; init; }

    /// <summary>The commit message; may be empty, never null.</summary>
    public string? Message { get; init; }

    /// <summary>Who made the changeset.</summary>
    public Author? Author { get; init; }

    /// <summary>Unix timestamp in seconds, UTC — client-supplied so identical payloads hash identically.</summary>
    public long TimestampUtc { get; init; }

    /// <summary>Changed blobs to store before the changeset appends. Blobs already in the store are no-ops.</summary>
    public List<BlobPayload>? Blobs { get; init; }

    /// <summary>Optional ref move, applied by compare-and-swap after the append.</summary>
    public RefMoveRequest? RefMove { get; init; }

    /// <summary>One blob of changed content, addressed by the SHA-256 of <see cref="Content"/>.</summary>
    public sealed record BlobPayload(string Hash, byte[] Content);

    /// <summary>
    /// A ref move that applies only while the ref is still at <paramref name="ExpectedRevision"/>;
    /// <see cref="RefStore.CreationRevision"/> creates a ref that does not exist.
    /// </summary>
    public sealed record RefMoveRequest(string Name, long ExpectedRevision);
}
