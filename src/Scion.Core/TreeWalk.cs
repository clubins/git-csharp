namespace Scion.Core;

/// <summary>The blob hashes a walk found in scope: distinct, lowercase, in ordinal order.</summary>
public sealed record TreeWalkResult(IReadOnlyList<string> BlobHashes);

/// <summary>
/// The server-side walk behind the trees/missing fetch endpoint: which blob content a tree
/// snapshot references within a requested path scope. The walk is pure — it reads the snapshot
/// and the filters and touches nothing else — so presence checks against the blob store stay at
/// the endpoint boundary, where the missing set is computed.
/// </summary>
public static class TreeWalk
{
    /// <summary>
    /// Collects the blob hashes referenced by <paramref name="snapshot"/>'s entries that fall in
    /// scope. A path is in scope when it matches <paramref name="wantedPaths"/> (null or empty:
    /// everything) and <paramref name="sparseProfile"/> (null or empty: everything) — a client's
    /// standing sparse profile narrows what its wanted paths select, never widens it.
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

        // Entries enumerate in ordinal path order already; the sorted set dedups blobs shared by
        // several paths and pins the response's hash order so identical walks answer identically.
        SortedSet<string> hashes = new(StringComparer.Ordinal);
        foreach (var (path, entry) in snapshot.Entries)
        {
            if (TreePathFilter.AnyMatch(wantedPaths, path) && TreePathFilter.AnyMatch(sparseProfile, path))
            {
                hashes.Add(entry.BlobHash.ToLowerInvariant());
            }
        }

        return new TreeWalkResult([.. hashes]);
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
