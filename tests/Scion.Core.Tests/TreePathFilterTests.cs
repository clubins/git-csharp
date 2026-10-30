namespace Scion.Core.Tests;

/// <summary>
/// The glob rules the fetch protocol's path filters share — anchored segment matching, zero-or-
/// more-segment <c>**</c>, segment-confined <c>*</c> and <c>?</c>, gitignore-style slashless
/// patterns, ordinal case sensitivity, and literal treatment of everything else.
/// </summary>
public sealed class TreePathFilterTests
{
    [Theory]
    [InlineData(true, "src/**", "src/a.cs")]
    [InlineData(true, "src/**", "src/deep/a.cs")]
    [InlineData(true, "src/**", "src")] // ** matches zero segments; a flat tree never lists directories anyway
    [InlineData(false, "src/**", "srcx/a.cs")]
    [InlineData(false, "src/**", "docs/src/a.cs")] // patterns are rooted, not suffix-matched
    [InlineData(true, "a/**/b.cs", "a/b.cs")]
    [InlineData(true, "a/**/b.cs", "a/x/b.cs")]
    [InlineData(true, "a/**/b.cs", "a/x/y/b.cs")]
    [InlineData(false, "a/**/b.cs", "b.cs")]
    [InlineData(false, "a/**/b.cs", "a/b")]
    [InlineData(true, "**/*.cs", "a.cs")]
    [InlineData(true, "**/*.cs", "x/y/a.cs")]
    [InlineData(false, "**/*.cs", "a.txt")]
    [InlineData(true, "*.cs", "a.cs")] // slashless pattern: basename at any depth
    [InlineData(true, "*.cs", "src/a.cs")]
    [InlineData(true, "*.cs", "src/deep/a.cs")]
    [InlineData(false, "*.cs", "a.txt")]
    [InlineData(true, "README.md", "README.md")]
    [InlineData(true, "README.md", "docs/README.md")]
    [InlineData(false, "docs/README.md", "README.md")] // a slashed pattern is anchored
    [InlineData(true, "src/*.cs", "src/a.cs")]
    [InlineData(false, "src/*.cs", "src/deep/a.cs")] // * never crosses a slash
    [InlineData(true, "src/?.cs", "src/a.cs")]
    [InlineData(false, "src/?.cs", "src/ab.cs")]
    [InlineData(true, "a+b (1).cs", "a+b (1).cs")] // everything but *, ?, / is literal
    [InlineData(false, "a+b.cs", "axb.cs")]
    [InlineData(false, "SRC/**", "src/a.cs")] // ordinal, case-sensitive
    [InlineData(false, "Src/A.cs", "src/a.cs")]
    public void Matches_FollowsTheGlobRules(bool expected, string pattern, string path)
    {
        Assert.Equal(expected, TreePathFilter.Matches(pattern, path));
    }

    [Fact]
    public void AnyMatch_NullOrEmptyList_IsUnrestricted()
    {
        Assert.True(TreePathFilter.AnyMatch(null, "any/path"));
        Assert.True(TreePathFilter.AnyMatch([], "any/path"));
    }

    [Fact]
    public void AnyMatch_AnyMatchingPattern_PutsThePathInScope()
    {
        Assert.True(TreePathFilter.AnyMatch(["docs/**", "src/**"], "src/a.cs"));
        Assert.False(TreePathFilter.AnyMatch(["docs/**", "lib/**"], "src/a.cs"));
    }

    [Fact]
    public void Matches_NullEmptyOrWhitespacePattern_Throws()
    {
        Assert.Throws<ArgumentException>(() => TreePathFilter.Matches("", "a"));
        Assert.Throws<ArgumentException>(() => TreePathFilter.Matches("  ", "a"));
    }
}
