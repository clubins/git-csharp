using System.Collections.Immutable;
using LibGit2Sharp;
using Scion.Core;
using GitBlob = LibGit2Sharp.Blob;
using ScionBlob = Scion.Core.Blob;

namespace Scion.Import.Git;

/// <summary>
/// One-way, read-only importer that maps an existing git repository onto Scion model records
/// (spec deliverable D6). Walking the first-parent lineage of HEAD, every git commit becomes a
/// <see cref="Changeset"/> and every file's bytes become a content-addressed <see cref="Blob"/> —
/// hashed with SHA-256 over the raw content, not git's SHA-1. Persistence (the WAL and the blob
/// store) is deliberately left to the caller.
/// </summary>
/// <remarks>
/// <para>
/// The import flattens merges onto the first-parent lineage: a merge changeset's
/// <see cref="Changeset.Parents"/> holds its git first parent only, and the content the merge
/// brought in is carried by <see cref="Changeset.Changes"/> — the delta against that first
/// parent. The imported parent chain therefore equals <c>git log --first-parent</c> (the C2
/// import-equivalence criterion); second-parent commits are outside the first-parent lineage and
/// are not imported.
/// </para>
/// <para>
/// Git's tree shape maps with git's own default rename detection: a rename imports as one
/// <see cref="ChangeType.Renamed"/> entry whose <see cref="ChangeEntry.Path"/> is the
/// post-rename path — the same presentation <c>git diff</c> prints. Submodule (gitlink) entries
/// are rejected: they reference commits, not blobs, and v1 has no representation for them.
/// </para>
/// </remarks>
public sealed class GitImporter : IDisposable
{
    private readonly Repository _repository;

    // Git blobs are content-addressed (same oid ⇒ same bytes), so one hash computation serves
    // every change that references the oid — an unchanged file reappears on every commit of a
    // long first-parent lineage.
    private readonly Dictionary<string, string> _blobHashByGitSha = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScionBlob> _blobsByHash = new(StringComparer.Ordinal);
    private readonly List<ScionBlob> _blobOrder = [];
    private readonly List<GitBlobMapping> _blobMappings = [];

    /// <summary>Opens the git repository at <paramref name="repositoryPath"/> (working directory or .git) for import.</summary>
    /// <exception cref="RepositoryNotFoundException">The path does not contain a git repository.</exception>
    public GitImporter(string repositoryPath)
    {
        _repository = new Repository(repositoryPath);
    }

    /// <summary>
    /// Reads the first-parent lineage of HEAD and maps it to Scion records. The changesets come
    /// back oldest first — the order a WAL expects, since children reference their parents' ids.
    /// </summary>
    /// <exception cref="InvalidOperationException">The repository has no commits.</exception>
    public GitImportResult Import()
    {
        var lineage = FirstParentLineage();
        var changesets = new List<Changeset>(lineage.Count);
        var changesetIdByGitSha = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var commit in lineage)
        {
            var deltas = commit.Parents.Any() ? DeltasAgainstFirstParent(commit) : RootDeltas(commit);
            var changes = deltas.Select(BuildChangeEntry).ToImmutableArray();

            var parents = ImmutableArray<string>.Empty;
            if (commit.Parents.Any())
            {
                var firstParent = commit.Parents.First();
                if (!changesetIdByGitSha.TryGetValue(firstParent.Sha, out var firstParentId))
                {
                    throw new InvalidOperationException($"Commit {commit.Sha} names first parent {firstParent.Sha}, which is not part of the imported first-parent lineage.");
                }

                parents = [firstParentId];
            }

            var author = new Author(commit.Author.Name, commit.Author.Email);
            var changeset = Changeset.Create(parents, changes, commit.Message, author, commit.Committer.When.ToUnixTimeSeconds());

            changesetIdByGitSha.Add(commit.Sha, changeset.Id);
            changesets.Add(changeset);
        }

        var commitMappings = changesets
            .Select((changeset, index) => new GitCommitMapping(lineage[index].Sha, changeset.Id))
            .ToImmutableArray();

        return new GitImportResult(
            changesets.ToImmutableArray(),
            _blobOrder.ToImmutableArray(),
            commitMappings,
            _blobMappings.ToImmutableArray());
    }

    public void Dispose()
    {
        _repository.Dispose();
    }

    private List<Commit> FirstParentLineage()
    {
        var tip = _repository.Head.Tip;
        if (tip is null)
        {
            throw new InvalidOperationException("The repository has no commits; there is no first-parent lineage to import.");
        }

        // HEAD → root by first parent, then reversed: the import emits parents before children so
        // every changeset's parent id already exists.
        var lineage = new List<Commit>();
        for (var commit = tip; commit is not null; commit = commit.Parents.FirstOrDefault())
        {
            lineage.Add(commit);
        }

        lineage.Reverse();
        return lineage;
    }

    private IEnumerable<TreeDelta> DeltasAgainstFirstParent(Commit commit)
    {
        var changes = _repository.Diff.Compare<TreeChanges>(commit.Parents.First().Tree, commit.Tree);
        foreach (var change in changes)
        {
            yield return new TreeDelta(
                change.Path,
                change.Status,
                OldOid: change.Status == ChangeKind.Added ? null : change.OldOid,
                NewOid: change.Status == ChangeKind.Deleted ? null : change.Oid,
                OldMode: change.OldMode,
                NewMode: change.Mode);
        }
    }

    private static List<TreeDelta> RootDeltas(Commit root)
    {
        var deltas = new List<TreeDelta>();
        CollectTreeDeltas(root.Tree, string.Empty, deltas);
        return deltas;
    }

    private static void CollectTreeDeltas(Tree tree, string prefix, List<TreeDelta> deltas)
    {
        foreach (var entry in tree)
        {
            var path = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            switch (entry.Target)
            {
                case Tree subtree:
                    CollectTreeDeltas(subtree, path, deltas);
                    break;
                case GitBlob blob:
                    deltas.Add(new TreeDelta(path, ChangeKind.Added, OldOid: null, NewOid: blob.Id, OldMode: entry.Mode, NewMode: entry.Mode));
                    break;
                default:
                    throw new NotSupportedException($"Path '{path}' is a git {entry.Mode} entry; only blobs and directories are importable in v1 (submodules are rejected).");
            }
        }
    }

    private ChangeEntry BuildChangeEntry(TreeDelta delta)
    {
        return delta.Status switch
        {
            ChangeKind.Added => new ChangeEntry(delta.Path, null, ScionHash(delta.NewOid, delta.Path), KindFor(delta), ChangeType.Added),
            ChangeKind.Deleted => new ChangeEntry(delta.Path, ScionHash(delta.OldOid, delta.Path), null, KindFor(delta), ChangeType.Deleted),
            // A rename keeps both blob references; Path is the post-rename path, per ChangeEntry.
            ChangeKind.Renamed => new ChangeEntry(delta.Path, ScionHash(delta.OldOid, delta.Path), ScionHash(delta.NewOid, delta.Path), KindFor(delta), ChangeType.Renamed),
            // A typechange (file ↔ symlink) keeps its path; the content hash pair tells the story.
            ChangeKind.Modified or ChangeKind.TypeChanged => new ChangeEntry(delta.Path, ScionHash(delta.OldOid, delta.Path), ScionHash(delta.NewOid, delta.Path), KindFor(delta), ChangeType.Modified),
            var status => throw new NotSupportedException($"Change kind {status} on '{delta.Path}' is not importable in v1."),
        };
    }

    private static FileContentKind KindFor(TreeDelta delta)
    {
        return MapKind(delta.Status == ChangeKind.Deleted ? delta.OldMode : delta.NewMode);
    }

    private static FileContentKind MapKind(Mode mode)
    {
        return mode switch
        {
            Mode.NonExecutableFile or Mode.NonExecutableGroupWritableFile => FileContentKind.Regular,
            Mode.ExecutableFile => FileContentKind.Executable,
            Mode.SymbolicLink => FileContentKind.Symlink,
            var unexpected => throw new NotSupportedException($"Git entry mode '{unexpected}' has no Scion content kind; only regular files, executables, and symlinks are importable in v1."),
        };
    }

    private string? ScionHash(ObjectId? gitOid, string path)
    {
        if (gitOid is null)
        {
            return null;
        }

        if (_blobHashByGitSha.TryGetValue(gitOid.Sha, out var cachedHash))
        {
            return cachedHash;
        }

        var content = ReadBlobContent(gitOid, path);
        var blob = ScionBlob.Create(content);
        _blobHashByGitSha.Add(gitOid.Sha, blob.Hash);
        if (_blobsByHash.TryAdd(blob.Hash, blob))
        {
            _blobOrder.Add(blob);
        }

        _blobMappings.Add(new GitBlobMapping(gitOid.Sha, blob.Hash));
        return blob.Hash;
    }

    private byte[] ReadBlobContent(ObjectId gitOid, string path)
    {
        var blob = _repository.Lookup<GitBlob>(gitOid);
        if (blob is null)
        {
            throw new NotSupportedException($"Path '{path}' references git object {gitOid.Sha}, which is not a blob; git submodule (gitlink) changes are not importable in v1.");
        }

        using var stream = blob.GetContentStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
