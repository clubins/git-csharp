using LibGit2Sharp;
using Scion.Core;

namespace Scion.Import.Git.Tests;

public class GitImporterLineageTests
{
    [Fact]
    public void Import_FollowsGitFirstParentOrder()
    {
        using var fixture = SharedRepo();
        var expected = GitFirstParentChain(fixture.Repo);

        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        // One changeset per first-parent commit: A, B, C3, merge, D, E — the branch's own
        // commits are reachable only through the merge's second parent and are not imported.
        Assert.Equal(6, result.Changesets.Count());
        Assert.Equal(6, result.CommitMappings.Count());
        for (var i = 0; i < expected.Count; i++)
        {
            var changeset = result.Changesets[i];
            Assert.Equal(expected[i].Sha, result.CommitMappings[i].GitSha);
            Assert.Equal(changeset.Id, result.CommitMappings[i].ChangesetId);
            Assert.Equal(expected[i].Message, changeset.Message);
            Assert.Equal(new Author(TestGitRepository.IdentityName, TestGitRepository.IdentityEmail), changeset.Author);
            Assert.Equal(expected[i].Committer.When.ToUnixTimeSeconds(), changeset.TimestampUtc);
        }
    }

    [Fact]
    public void Import_ParentChainLinksOldestToNewest()
    {
        using var fixture = SharedRepo();
        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        Assert.Empty(result.Changesets[0].Parents);
        for (var i = 1; i < result.Changesets.Count(); i++)
        {
            var parents = result.Changesets[i].Parents;
            Assert.Single(parents);
            Assert.Equal(result.Changesets[i - 1].Id, parents[0]);
        }
    }

    [Fact]
    public void Import_RootChangesetAddsEveryFile()
    {
        using var fixture = SharedRepo();
        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        var root = result.Changesets[0];
        Assert.Equal(new[] { "a.txt", TestGitRepository.BinaryPath }, root.Changes.Select(c => c.Path).ToArray());

        var text = root.Changes.Single(c => c.Path == "a.txt");
        Assert.Equal(ChangeType.Added, text.Type);
        Assert.Null(text.OldBlob);
        Assert.Equal(Sha256.HashHex("alpha\n"u8), text.NewBlob);

        var binary = root.Changes.Single(c => c.Path == TestGitRepository.BinaryPath);
        Assert.Equal(Sha256.HashHex(TestGitRepository.BinaryContent), binary.NewBlob);
    }

    [Fact]
    public void Import_MergeFlattensOntoFirstParentDelta()
    {
        using var fixture = SharedRepo();

        // The fixture made a real merge: some commit on the chain has two git parents.
        var chain = new List<Commit>();
        for (var commit = fixture.Repo.Head.Tip; commit is not null; commit = commit.Parents.FirstOrDefault())
        {
            chain.Add(commit);
        }

        Assert.Contains(chain, c => c.Parents.Count() == 2);

        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        var merge = result.Changesets[3]; // A, B, C3, merge
        Assert.Single(merge.Parents); // the second parent lies outside the first-parent lineage
        var change = Assert.Single(merge.Changes);
        Assert.Equal("feature/feat.txt", change.Path);
        Assert.Equal(ChangeType.Added, change.Type);
        Assert.Null(change.OldBlob);
        Assert.Equal(Sha256.HashHex("feat2\n"u8), change.NewBlob);
    }

    [Fact]
    public void Import_RenamesMapToRenamedEntries()
    {
        using var fixture = SharedRepo();
        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        // git's default rename detection carries over: one Renamed entry, post-rename path,
        // both blobs pointing at the same bytes.
        var rename = result.Changesets[5];
        var renamed = Assert.Single(rename.Changes);
        Assert.Equal("b.txt", renamed.Path);
        Assert.Equal(ChangeType.Renamed, renamed.Type);
        Assert.Equal(Sha256.HashHex("beta\n"u8), renamed.OldBlob);
        Assert.Equal(renamed.OldBlob, renamed.NewBlob);
        Assert.DoesNotContain(rename.Changes, c => c.Path == "a.txt");
    }

    [Fact]
    public void Import_DeletionCarriesOnlyTheOldBlob()
    {
        using var fixture = SharedRepo();
        using var importer = new GitImporter(fixture.FullPath);
        var result = importer.Import();

        var drop = result.Changesets[4];
        var deleted = drop.Changes.Single(c => c.Path == TestGitRepository.BinaryPath);
        Assert.Equal(ChangeType.Deleted, deleted.Type);
        Assert.Null(deleted.NewBlob);
        Assert.Equal(Sha256.HashHex(TestGitRepository.BinaryContent), deleted.OldBlob);
    }

    [Fact]
    public void Import_EmptyRepository_Throws()
    {
        using var fixture = new TestGitRepository(); // initialized, never committed
        using var importer = new GitImporter(fixture.FullPath);
        Assert.Throws<InvalidOperationException>(importer.Import);
    }

    [Fact]
    public void Import_IsDeterministicAcrossRuns()
    {
        using var fixture = SharedRepo();
        using var firstImporter = new GitImporter(fixture.FullPath);
        using var secondImporter = new GitImporter(fixture.FullPath);
        var first = firstImporter.Import();
        var second = secondImporter.Import();

        Assert.Equal(first.Changesets.ToArray(), second.Changesets.ToArray());
        Assert.Equal(first.Blobs.ToArray(), second.Blobs.ToArray());
        Assert.Equal(first.CommitMappings.ToArray(), second.CommitMappings.ToArray());
        Assert.Equal(first.BlobMappings.ToArray(), second.BlobMappings.ToArray());
    }

    /// <summary>HEAD's first-parent chain, oldest first — git's <c>log --first-parent</c> order.</summary>
    private static List<Commit> GitFirstParentChain(Repository repository)
    {
        var chain = new List<Commit>();
        for (var commit = repository.Head.Tip; commit is not null; commit = commit.Parents.FirstOrDefault())
        {
            chain.Add(commit);
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Main line: root (text + binary), a nested modify/add, a mainline file, a real merge of a
    /// two-commit feature branch, a binary deletion with duplicate content, and a rename.
    /// </summary>
    internal static TestGitRepository SharedRepo()
    {
        var fixture = new TestGitRepository();

        fixture.WriteText("a.txt", "alpha\n");
        fixture.WriteBinary(TestGitRepository.BinaryPath, TestGitRepository.BinaryContent);
        _ = fixture.Commit("initial: text and binary");

        fixture.WriteText("a.txt", "beta\n");
        fixture.WriteText("nested/deep/c.txt", "cee\n");
        _ = fixture.Commit("second: modify text, add nested file");

        fixture.CreateBranchAndCheckout("feature");
        fixture.WriteText("feature/feat.txt", "feat\n");
        _ = fixture.Commit("feature: add file");
        fixture.WriteText("feature/feat.txt", "feat2\n");
        _ = fixture.Commit("feature: update file");

        fixture.Checkout(fixture.InitialBranch);
        fixture.WriteText("main.txt", "main\n");
        _ = fixture.Commit("mainline: add main file");

        fixture.MergeNoFastForward("feature");

        fixture.Delete(TestGitRepository.BinaryPath);
        fixture.WriteText("dup.txt", "alpha\n");
        _ = fixture.Commit("cleanup: drop binary, add duplicate content");

        fixture.Move("a.txt", "b.txt");
        _ = fixture.Commit("rename: a.txt to b.txt");

        return fixture;
    }
}
