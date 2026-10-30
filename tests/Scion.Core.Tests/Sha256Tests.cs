namespace Scion.Core.Tests;

public class Sha256Tests
{
    // FIPS 180 test vectors, re-verified this session against Python's hashlib.
    [Fact]
    public void HashHex_EmptyInput_MatchesKnownVector()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Sha256.HashHex([]));
    }

    [Fact]
    public void HashHex_SingleBlockInput_MatchesKnownVector()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Sha256.HashHex("abc"u8));
    }

    [Fact]
    public void HashHex_MultiBlockInput_MatchesKnownVector()
    {
        Assert.Equal("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1", Sha256.HashHex("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"u8));
    }

    [Fact]
    public void HashHex_ReturnsSixtyFourLowercaseHexCharacters()
    {
        var hash = Sha256.HashHex("abc"u8);

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
    }
}
