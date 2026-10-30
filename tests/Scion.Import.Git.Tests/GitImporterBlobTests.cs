using LibGit2Sharp;
using Scion.Core;

namespace Scion.Import.Git.Tests;

public class GitImporterBlobTests
{
    [Fact]
    public void Import_HashesBlobContentWithScionSha256()
    {
        using var fixture = GitImporterLineageTests.SharedRepo();
        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        var binaryHash = Sha256.HashHex(TestGitRepository.BinaryContent);
        var blob = result.Blobs.Single(b => b.Hash == binaryHash);
        Assert.True(blob.Content.Span.SequenceEqual(TestGitRepository.BinaryContent));
    }

    [Fact]
    public void Import_MapsGitBlobShaToScionHash()
    {
        using var fixture = GitImporterLineageTests.SharedRepo();
        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        // The binary file's git SHA-1 and its Scion SHA-256 must both survive the import.
        var root = fixture.Repo.Head.Tip;
        while (root.Parents.Any())
        {
            root = root.Parents.First();
        }

        var gitBlobSha = ResolveBlob(root.Tree, TestGitRepository.BinaryPath).Sha;
        var scionHash = Sha256.HashHex(TestGitRepository.BinaryContent);
        Assert.Contains(new GitBlobMapping(gitBlobSha, scionHash), result.BlobMappings);
    }

    [Fact]
    public void Import_DeduplicatesIdenticalContent()
    {
        using var fixture = GitImporterLineageTests.SharedRepo();
        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        // dup.txt repeats the root commit's a.txt bytes: one blob record serves both paths.
        var alphaHash = Sha256.HashHex("alpha\n"u8);
        Assert.Single(result.Blobs.Where(b => b.Hash == alphaHash));

        var root = result.Changesets[0];
        var drop = result.Changesets[4];
        Assert.Equal(root.Changes.Single(c => c.Path == "a.txt").NewBlob, drop.Changes.Single(c => c.Path == "dup.txt").NewBlob);
    }

    [Fact]
    public void Import_EmitsEachDistinctContentOnce()
    {
        using var fixture = GitImporterLineageTests.SharedRepo();
        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        // alpha (×2 paths), the binary, beta, cee, main, feat2 — the merge's branch commits are
        // outside the first-parent lineage, so their "feat" version is never read.
        Assert.Equal(6, result.Blobs.Count());
    }

    [Fact]
    public void Import_AllChangeBlobReferencesResolve()
    {
        using var fixture = GitImporterLineageTests.SharedRepo();
        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        var hashes = result.Blobs.Select(b => b.Hash).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(hashes);
        foreach (var changeset in result.Changesets)
        {
            foreach (var change in changeset.Changes)
            {
                Assert.True(change.OldBlob is null || hashes.Contains(change.OldBlob), $"Old blob '{change.OldBlob}' of '{change.Path}' is missing from the import.");
                Assert.True(change.NewBlob is null || hashes.Contains(change.NewBlob), $"New blob '{change.NewBlob}' of '{change.Path}' is missing from the import.");
            }
        }
    }

    [Fact]
    public void Import_MapsExecutableAndSymlinkKinds()
    {
        using var fixture = new TestGitRepository();
        fixture.WriteExecutable("run.sh", "#!/bin/sh\necho hi\n");
        fixture.WriteSymlink("latest", "run.sh");
        _ = fixture.Commit("kinds: executable and symlink");

        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();
        var changeset = Assert.Single(result.Changesets);

        Assert.Equal(FileContentKind.Executable, changeset.Changes.Single(c => c.Path == "run.sh").Kind);
        Assert.Equal(FileContentKind.Symlink, changeset.Changes.Single(c => c.Path == "latest").Kind);
    }

    private static LibGit2Sharp.Blob ResolveBlob(Tree tree, string path)
    {
        var current = tree;
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = (Tree)current[segments[i]].Target;
        }

        return (LibGit2Sharp.Blob)current[segments[^1]].Target;
    }
}
