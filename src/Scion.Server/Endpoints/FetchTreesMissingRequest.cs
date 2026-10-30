namespace Scion.Server.Endpoints;

/// <summary>
/// The trees/missing request: the tree to walk, named either by changeset id (the tree at that
/// changeset, materialized from derived data) or by the tree snapshot's own content hash —
/// exactly one of the two — plus the glob filters that scope the walk. Hashes are bare 64-hex
/// SHA-256 strings, like every other address on this wire.
/// </summary>
public sealed record FetchTreesMissingRequest
{
    /// <summary>The changeset whose tree to walk; mutually exclusive with <see cref="RootTree"/>.</summary>
    public string? ChangesetId { get; init; }

    /// <summary>The tree snapshot's content hash; mutually exclusive with <see cref="ChangesetId"/>.</summary>
    public string? RootTree { get; init; }

    /// <summary>Glob patterns selecting the paths to walk; null or empty walks the whole tree.</summary>
    public List<string>? WantedPaths { get; init; }

    /// <summary>
    /// Additional glob patterns — the client's standing sparse profile — that further restrict
    /// the walk: a path must match the wanted paths <em>and</em> the profile.
    /// </summary>
    public List<string>? SparseProfile { get; init; }
}
