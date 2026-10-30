namespace Scion.Server.Endpoints;

/// <summary>
/// The server-side rebase request: the first-parent chain to replay — from <see cref="Start"/>
/// (inclusive, oldest) to <see cref="Tip"/> (inclusive, newest) — and the changeset to replay it
/// onto. Message, author, and timestamp carry over from the source changesets; ids differ,
/// because the parents and deltas changed.
/// </summary>
public sealed record RebaseRequest
{
    /// <summary>The oldest changeset of the chain to replay; its first parent is the old base.</summary>
    public string? Start { get; init; }

    /// <summary>The newest changeset of the chain to replay; it must extend start along first parents.</summary>
    public string? Tip { get; init; }

    /// <summary>The changeset the chain rebases onto — the first rebased changeset's first parent.</summary>
    public string? NewBase { get; init; }

    /// <summary>Optional ref move, applied by compare-and-swap after the chain appends; the ref targets the rebased tip.</summary>
    public RefMoveRequest? RefMove { get; init; }

    /// <summary>A ref move that applies only while the ref is still at <c>ExpectedRevision</c>.</summary>
    public sealed record RefMoveRequest(string Name, long ExpectedRevision);
}
