using System.Collections.Immutable;

namespace Scion.Core.Tests;

public class ChangesetTests
{
    private const long Timestamp = 1_790_000_000;
    private const string BlobA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    private const string BlobB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
    private const string BlobC = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";
    private const string BlobD = "0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d0d";
    private const string BlobE = "0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e";

    private static readonly Author Author = new("Patrick", "patrick@clubins.de");

    [Fact]
    public void Create_NormalizesChangesToCanonicalOrder()
    {
        var changeset = Changeset.Create(
            [],
            [Added("z-last.cs", BlobA), Added("a-first.cs", BlobB)],
            "normalize",
            Author,
            Timestamp);

        Assert.Equal(new[] { "a-first.cs", "z-last.cs" }, changeset.Changes.Select(c => c.Path).ToArray());
    }

    [Fact]
    public void Create_ShuffledChangeInput_YieldsIdenticalChangesets()
    {
        var first = Changeset.Create([], [Added("a.cs", BlobA), Added("z.cs", BlobB)], "same", Author, Timestamp);
        var second = Changeset.Create([], [Added("z.cs", BlobB), Added("a.cs", BlobA)], "same", Author, Timestamp);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Create_PayloadDifference_ProducesDifferentId()
    {
        var first = Changeset.Create([], [Added("a.cs", BlobA)], "same", Author, Timestamp);
        var second = Changeset.Create([], [Added("a.cs", BlobA)], "different", Author, Timestamp);

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Create_MoreThanTwoParents_IsRejected()
    {
        var parents = ImmutableArray.Create("a", "b", "c");

        Assert.Throws<ArgumentOutOfRangeException>(() => Changeset.Create(parents, [], "merge", Author, Timestamp));
    }

    [Fact]
    public void Create_MergeRecordsSecondParentId_ButOnlyFirstParentDeltas()
    {
        var baseChangeset = Changeset.Create(
            [],
            [Added("src/a.cs", BlobA), Added("docs/readme.md", BlobB)],
            "base",
            Author,
            Timestamp);
        var firstParent = Changeset.Create([baseChangeset.Id], [Modified("src/a.cs", BlobA, BlobC)], "first", Author, Timestamp);
        var secondParent = Changeset.Create([baseChangeset.Id], [Modified("docs/readme.md", BlobB, BlobD)], "second", Author, Timestamp);

        var merge = Changeset.Create([firstParent.Id, secondParent.Id], [Modified("src/a.cs", BlobC, BlobE)], "merge", Author, Timestamp);

        Assert.Equal(2, merge.Parents.Length);
        Assert.Equal(firstParent.Id, merge.Parents[0]);
        Assert.Equal(secondParent.Id, merge.Parents[1]);
        var change = Assert.Single(merge.Changes);
        Assert.Equal("src/a.cs", change.Path);
        Assert.DoesNotContain(merge.Changes, c => c.Path == "docs/readme.md");
    }

    private static ChangeEntry Added(string path, string newBlob)
    {
        return new ChangeEntry(path, null, newBlob, FileContentKind.Regular, ChangeType.Added);
    }

    private static ChangeEntry Modified(string path, string oldBlob, string newBlob)
    {
        return new ChangeEntry(path, oldBlob, newBlob, FileContentKind.Regular, ChangeType.Modified);
    }
}
