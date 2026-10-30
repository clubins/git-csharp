using System.Text;
using Scion.Cli.Local;
using Scion.Cli.Server;
using Scion.Core;

namespace Scion.Cli.Working;

/// <summary>What one materialization did: the changeset, its tree, the paths written, and where the content came from.</summary>
internal sealed record MaterializeResult(string ChangesetId, string RootTree, int Paths, int BlobsFetched, int BlobsCached);

/// <summary>
/// The working-copy materializer — the cold-start story that replaces clone. It asks the server
/// to walk a changeset's tree within the wanted paths, refuses when the server's own store cannot
/// serve the tree, streams the referenced blobs back verified, writes every path (regular file,
/// executable bit, symlink), and advances the local manifest to exactly the materialized state.
/// Content the cache already holds never crosses the wire again.
/// </summary>
/// <remarks>
/// Materialization overwrites local content at the requested paths — the tree wins, like
/// <c>git checkout &lt;tree-ish&gt; -- paths</c> — and never deletes: a full checkout leaves extra
/// files on disk, where <c>scn status</c> shows them as additions.
/// </remarks>
internal sealed class WorkingCopyMaterializer(ScionServerClient client, IBlobCache? blobCache = null)
{
    private readonly IBlobCache blobCache = blobCache ?? NullBlobCache.Instance;

    public async Task<MaterializeResult> MaterializeAsync(
        string repoRoot,
        string changesetId,
        IReadOnlyList<string>? wantedPaths,
        IReadOnlyList<string>? sparseProfile = null,
        CancellationToken cancellationToken = default)
    {
        var fetch = await client.TreeEntriesAsync(changesetId, wantedPaths, sparseProfile, cancellationToken);
        if (fetch.MissingBlobs.Count > 0)
        {
            throw new ScionCliException(
                $"The server's blob store lacks {fetch.MissingBlobs.Count} of the blobs the tree at {changesetId} "
                + "references (a crash window or a corrupt store) — nothing can be materialized from it.");
        }

        var pathsByHash = GroupPathsByHash(fetch.Entries);

        // Cached content writes without a round trip; the rest streams back hash-keyed, verified
        // entry by entry and written as it arrives — one blob in memory at a time, never the tree.
        Dictionary<string, byte[]> cached = [];
        List<string> toFetch = [];
        foreach (var hash in pathsByHash.Keys)
        {
            if (blobCache.TryGet(hash, out var content))
            {
                cached[hash] = content;
            }
            else
            {
                toFetch.Add(hash);
            }
        }

        var written = 0;
        foreach (var (hash, content) in cached)
        {
            written += WriteContent(repoRoot, hash, content, pathsByHash[hash]);
        }

        int fetched = 0;
        await foreach (var entry in client.FetchBlobsAsync(toFetch, cancellationToken))
        {
            written += WriteContent(repoRoot, entry.Hash, entry.Content, pathsByHash[entry.Hash]);
            blobCache.Put(entry.Hash, entry.Content);
            fetched++;
        }

        if (fetched != toFetch.Count)
        {
            throw new ScionCliException(
                $"The server's blob stream ended early: {toFetch.Count} blob(s) were requested, {fetched} arrived "
                + "— the fetch is incomplete and the manifest was left untouched.");
        }

        // The manifest IS the working copy: the tip is the materialized changeset and every
        // materialized path's hash and kind is exactly what the tree said.
        new WorkingState
        {
            Tip = changesetId,
            Files = fetch.Entries.ToDictionary(
                entry => entry.Path,
                entry => new WorkingState.Entry(entry.BlobHash, entry.Kind),
                StringComparer.Ordinal),
        }.Save(repoRoot);

        return new MaterializeResult(changesetId, fetch.RootTree, written, fetched, cached.Count);
    }

    /// <summary>In-scope paths grouped by the blob they share, in the walk's ordinal path order.</summary>
    private static Dictionary<string, List<(string Path, FileContentKind Kind)>> GroupPathsByHash(IReadOnlyList<FetchedPath> entries)
    {
        Dictionary<string, List<(string, FileContentKind)>> grouped = new(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!grouped.TryGetValue(entry.BlobHash, out var paths))
            {
                paths = [];
                grouped[entry.BlobHash] = paths;
            }

            paths.Add((entry.Path, entry.Kind));
        }

        return grouped;
    }

    /// <summary>Writes one blob's content to every path that names it; answers the number of paths written.</summary>
    private static int WriteContent(string repoRoot, string hash, byte[] content, List<(string Path, FileContentKind Kind)> paths)
    {
        foreach (var (path, kind) in paths)
        {
            WritePath(repoRoot, path, kind, content);
        }

        return paths.Count;
    }

    private static void WritePath(string repoRoot, string path, FileContentKind kind, byte[] content)
    {
        var target = ResolveWithinRoot(repoRoot, path);

        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // Clear whatever the path held before: the tree is the authority for what it is now. The
        // link probe comes first — File.Exists misses dangling links, and deleting through a
        // directory symlink would reach past the working copy.
        var info = new FileInfo(target);
        if (info.LinkTarget is not null)
        {
            File.Delete(target);
        }
        else if (Directory.Exists(target))
        {
            // A real directory the tree no longer has (the path changed kind); its contents were
            // tracked content of an older tree.
            Directory.Delete(target, recursive: true);
        }
        else if (info.Exists)
        {
            File.Delete(target);
        }

        switch (kind)
        {
            case FileContentKind.Regular:
                File.WriteAllBytes(target, content);
                break;
            case FileContentKind.Executable:
                File.WriteAllBytes(target, content);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(
                        target,
                        File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }

                break;
            case FileContentKind.Symlink:
                // A symlink's content is the UTF-8 path it points at — the scanner's own convention.
                File.CreateSymbolicLink(target, Encoding.UTF8.GetString(content));
                break;
            default:
                throw new ScionCliException($"Tree entry for '{path}' has an unknown content kind '{kind}'.");
        }
    }

    /// <summary>The absolute disk path for a canonical tree path, refusing anything that escapes the root.</summary>
    private static string ResolveWithinRoot(string repoRoot, string path)
    {
        if (path.Length == 0 || path.Split('/').Contains(".."))
        {
            throw new ScionCliException($"Tree path '{path}' is not canonical — refusing to write outside the repository root.");
        }

        var fullRoot = Path.GetFullPath(repoRoot);
        var target = Path.GetFullPath(Path.Combine(fullRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ScionCliException($"Tree path '{path}' resolves outside the repository root — refusing to write it.");
        }

        return target;
    }
}
