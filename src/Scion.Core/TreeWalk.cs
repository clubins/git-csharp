namespace Scion.Core;

/// <summary>One in-scope path of a walk: its tree path, content address, and content kind.</summary>
public sealed record TreeWalkEntry(string Path, string BlobHash, FileContentKind Kind);

/// <summary>
/// The paths a walk found in scope, with their content: entries enumerate in ordinal path order,
/// and <see cref="BlobHashes"/> is the distinct lowercase hash set the fetch contract answers
/// presence with — deduplicated and ordinal-sorted, so identical walks answer identically.
/// </summary>
public sealed record TreeWalkResult(IReadOnlyList<TreeWalkEntry> Entries)
{
    private readonly string[] blobHashes = [.. Entries
        .Select(entry => entry.BlobHash)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(hash => hash, StringComparer.Ordinal)];

    /// <summary>The distinct lowercase blob hashes the entries reference, ordinal-sorted.</summary>
    public IReadOnlyList<string> BlobHashes => blobHashes;
}

/// <summary>
/// The server-side walk behind the trees/missing fetch endpoint: which paths and blob content a
/// tree snapshot references within a requested path scope. The walk is pure — it reads the
/// snapshot and the filters and touches nothing else — so presence checks against the blob store
/// stay at the endpoint boundary, where the missing set is computed.
/// </summary>
public static class TreeWalk
{
    /// <summary>
    /// Collects the entries of <paramref name="snapshot"/>'s paths that fall in scope. A path is
    /// in scope when it matches <paramref name="wantedPaths"/> (null or empty: everything) and
    /// <paramref name="sparseProfile"/> (null or empty: everything) — a client's standing sparse
    /// profile narrows what its wanted paths select, never widens it.
    /// </summary>
    /// <exception cref="ArgumentException">Any pattern is null, empty, or whitespace.</exception>
    public static TreeWalkResult Collect(
        TreeSnapshot snapshot,
        IReadOnlyList<string>? wantedPaths = null,
        IReadOnlyList<string>? sparseProfile = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidatePatterns(wantedPaths, nameof(wantedPaths));
        ValidatePatterns(sparseProfile, nameof(sparseProfile));

        // Entries enumerate in ordinal path order already; a sorted dictionary pins both the
        // entry order and the response's hash order so identical walks answer identically.
        SortedDictionary<string, TreeEntry> inScope = new(StringComparer.Ordinal);
        foreach (var (path, entry) in snapshot.Entries)
        {
            if (TreePathFilter.AnyMatch(wantedPaths, path) && TreePathFilter.AnyMatch(sparseProfile, path))
            {
                inScope.Add(path, entry);
            }
        }

        return new TreeWalkResult([.. inScope.Select(pair => new TreeWalkEntry(
            pair.Key,
            pair.Value.BlobHash.ToLowerInvariant(),
            pair.Value.Kind))]);
    }

    private static void ValidatePatterns(IReadOnlyList<string>? patterns, string paramName)
    {
        if (patterns is null)
        {
            return;
        }

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                throw new ArgumentException($"Every pattern in '{paramName}' must be a non-empty glob.", paramName);
            }
        }
    }
}
