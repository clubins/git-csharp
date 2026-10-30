namespace Scion.Core.Tests;

public class SkipGraphHeightsTests
{
    [Fact]
    public void FromId_IsDeterministicPerId()
    {
        for (var i = 0; i < 500; i++)
        {
            Assert.Equal(
                SkipGraphHeights.FromId($"changeset-{i}", SkipGraph.MaxLevel),
                SkipGraphHeights.FromId($"changeset-{i}", SkipGraph.MaxLevel));
        }
    }

    [Fact]
    public void FromId_StaysWithinBounds()
    {
        for (var i = 0; i < 500; i++)
        {
            Assert.InRange(SkipGraphHeights.FromId($"changeset-{i}", SkipGraph.MaxLevel), 1, SkipGraph.MaxLevel);
        }
    }

    [Fact]
    public void FromId_SpreadsAcrossLowHeights()
    {
        var observed = new HashSet<int>();
        for (var i = 0; i < 500; i++)
        {
            observed.Add(SkipGraphHeights.FromId($"changeset-{i}", SkipGraph.MaxLevel));
        }

        Assert.True(observed.IsSupersetOf([1, 2, 3, 4]), $"Expected heights 1..4 among 500 ids; saw [{string.Join(", ", observed.Order())}].");
    }

    [Fact]
    public void FromId_CapsAtCustomMaxLevel()
    {
        Assert.Equal(1, SkipGraphHeights.FromId("any", 1));
        Assert.InRange(SkipGraphHeights.FromId("any", 3), 1, 3);
    }

    [Fact]
    public void FromId_RejectsNonPositiveMaxLevel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SkipGraphHeights.FromId("any", 0));
    }
}
