namespace Scion.Core.Tests;

public class BlobTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Create_DerivesHashFromContent()
    {
        Assert.Equal(AbcSha256, Blob.Create("abc"u8.ToArray()).Hash);
    }

    [Fact]
    public void Create_EmptyContent_MatchesKnownVector()
    {
        Assert.Equal(EmptySha256, Blob.Create(ReadOnlyMemory<byte>.Empty).Hash);
    }

    [Fact]
    public void HashMatchesContent_TrueForMatchingBlob_RegardlessOfHexCase()
    {
        var blob = Blob.Create("abc"u8.ToArray());

        Assert.True(blob.HashMatchesContent());
        Assert.True(new Blob(blob.Hash.ToUpperInvariant(), blob.Content).HashMatchesContent());
    }

    [Fact]
    public void HashMatchesContent_FalseWhenHashDoesNotMatchContent()
    {
        var blob = Blob.Create("abc"u8.ToArray()) with { Hash = new string('0', 64) };

        Assert.False(blob.HashMatchesContent());
    }

    [Fact]
    public void EnsureHashMatchesContent_ThrowsOnCorruptedBlob()
    {
        var blob = Blob.Create("abc"u8.ToArray()) with { Hash = "deadbeef" };

        Assert.Throws<InvalidOperationException>(blob.EnsureHashMatchesContent);
    }

    [Fact]
    public void Equality_IsContentAware_AcrossDistinctBackingArrays()
    {
        var first = Blob.Create(new byte[] { 1, 2, 3 });
        var second = Blob.Create(new byte[] { 1, 2, 3 });

        Assert.Equal(first, second);
        Assert.NotEqual(first, Blob.Create(new byte[] { 1, 2, 4 }));
        Assert.NotEqual(first, first with { Content = new byte[] { 1, 2, 3, 4 } });
    }
}
