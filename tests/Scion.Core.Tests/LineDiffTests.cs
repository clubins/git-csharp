using System.Diagnostics;

namespace Scion.Core.Tests;

public class LineDiffTests
{
    // --- SplitLines ---

    [Theory]
    [InlineData("", 0)]
    [InlineData("\n", 1)]
    [InlineData("a", 1)]
    [InlineData("a\n", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\n\nb\n", 3)]
    public void SplitLines_CountsLines_KeepingTerminatingNewlines(string text, int expectedCount)
    {
        Assert.Equal(expectedCount, LineDiff.SplitLines(text).Length);
    }

    [Fact]
    public void SplitLines_KeepsNewlineOnEachLineThatHasOne()
    {
        Assert.Equal(new[] { "a\n", "b" }, LineDiff.SplitLines("a\nb").ToArray());
        Assert.Equal(new[] { "a\n", "b\n" }, LineDiff.SplitLines("a\nb\n").ToArray());
        Assert.Equal(new[] { "\n" }, LineDiff.SplitLines("\n").ToArray());
    }

    // --- Compute: known diffs ---

    [Fact]
    public void Compute_IdenticalTexts_ProducesOnlyEqualEntries()
    {
        var entries = LineDiff.Compute("one\ntwo\nthree\n", "one\ntwo\nthree\n");

        Assert.Equal(3, entries.Length);
        Assert.All(entries, e => Assert.Equal(LineChangeKind.Equal, e.Kind));
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i + 1, entries[i].OldLine);
            Assert.Equal(i + 1, entries[i].NewLine);
        }
    }

    [Fact]
    public void Compute_EmptyTexts_ProduceNoEntries()
    {
        Assert.Empty(LineDiff.Compute(string.Empty, string.Empty));
    }

    [Fact]
    public void Compute_EmptyOldText_AllEntriesAdded()
    {
        var entries = LineDiff.Compute(string.Empty, "x\ny\n");

        Assert.Equal(2, entries.Length);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(LineChangeKind.Added, entries[i].Kind);
            Assert.Equal(0, entries[i].OldLine);
            Assert.Equal(i + 1, entries[i].NewLine);
        }
    }

    [Fact]
    public void Compute_EmptyNewText_AllEntriesDeleted()
    {
        var entries = LineDiff.Compute("x\ny\n", string.Empty);

        Assert.Equal(2, entries.Length);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(LineChangeKind.Deleted, entries[i].Kind);
            Assert.Equal(i + 1, entries[i].OldLine);
            Assert.Equal(0, entries[i].NewLine);
        }
    }

    [Fact]
    public void Compute_SingleLineReplacement_IsMinimalAndAnchored()
    {
        var entries = LineDiff.Compute("a\nb\nc\n", "a\nX\nc\n");

        var expected = new[]
        {
            new LineDiffEntry(LineChangeKind.Equal, 1, 1, "a\n"),
            new LineDiffEntry(LineChangeKind.Deleted, 2, 0, "b\n"),
            new LineDiffEntry(LineChangeKind.Added, 0, 2, "X\n"),
            new LineDiffEntry(LineChangeKind.Equal, 3, 3, "c\n"),
        };
        Assert.Equal(expected, entries.ToArray());
    }

    [Fact]
    public void Compute_TrailingNewlineDifference_IsAChange()
    {
        var entries = LineDiff.Compute("a\nb", "a\nb\n");

        var expected = new[]
        {
            new LineDiffEntry(LineChangeKind.Equal, 1, 1, "a\n"),
            new LineDiffEntry(LineChangeKind.Deleted, 2, 0, "b"),
            new LineDiffEntry(LineChangeKind.Added, 0, 2, "b\n"),
        };
        Assert.Equal(expected, entries.ToArray());
    }

    [Fact]
    public void Compute_CarriageReturnIsPartOfContent()
    {
        var entries = LineDiff.Compute("a\r\n", "a\n");

        var expected = new[]
        {
            new LineDiffEntry(LineChangeKind.Deleted, 1, 0, "a\r\n"),
            new LineDiffEntry(LineChangeKind.Added, 0, 1, "a\n"),
        };
        Assert.Equal(expected, entries.ToArray());
    }

    [Fact]
    public void Compute_BlobOverload_DiffsDecodedContent()
    {
        var entries = LineDiff.Compute(Blob.Create("a\n"u8.ToArray()), Blob.Create("a\nb\n"u8.ToArray()));

        Assert.Equal(2, entries.Length);
        Assert.Equal(LineChangeKind.Equal, entries[0].Kind);
        Assert.Equal(LineChangeKind.Added, entries[1].Kind);
        Assert.Equal("b\n", entries[1].Content);
    }

    // --- Compute: properties (validity, minimality, symmetry, determinism) ---

    [Fact]
    public void Compute_RandomInputs_IsValidMinimalSymmetricAndDeterministic()
    {
        var random = new Random(20261030);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            var a = RandomLines(random, random.Next(0, 11));
            var b = RandomLines(random, random.Next(0, 11));
            var aText = string.Join("\n", a);
            var bText = string.Join("\n", b);

            var entries = LineDiff.Compute(aText, bText);

            // Valid: the alignment reconstructs both texts exactly.
            var oldRebuilt = new System.Text.StringBuilder();
            var newRebuilt = new System.Text.StringBuilder();
            foreach (var entry in entries)
            {
                if (entry.Kind != LineChangeKind.Added)
                {
                    oldRebuilt.Append(entry.Content);
                }

                if (entry.Kind != LineChangeKind.Deleted)
                {
                    newRebuilt.Append(entry.Content);
                }
            }

            Assert.True(oldRebuilt.ToString() == aText, $"iteration {iteration}: old text not reconstructed");
            Assert.True(newRebuilt.ToString() == bText, $"iteration {iteration}: new text not reconstructed");

            // Minimal: the change count equals the reference edit distance.
            var aSplit = LineDiff.SplitLines(aText).ToArray();
            var bSplit = LineDiff.SplitLines(bText).ToArray();
            int nonEqual = entries.Count(e => e.Kind != LineChangeKind.Equal);
            int reference = ReferenceDistance(aSplit, bSplit);
            Assert.True(nonEqual == reference, $"iteration {iteration}: {nonEqual} changes, reference distance {reference}");

            // Symmetric: swapping sides swaps additions and deletions.
            var reverse = LineDiff.Compute(bText, aText);
            Assert.Equal(entries.Count(e => e.Kind == LineChangeKind.Deleted), reverse.Count(e => e.Kind == LineChangeKind.Added));
            Assert.Equal(entries.Count(e => e.Kind == LineChangeKind.Added), reverse.Count(e => e.Kind == LineChangeKind.Deleted));

            // Deterministic: same inputs, same alignment.
            Assert.Equal(entries.ToArray(), LineDiff.Compute(aText, bText).ToArray());
        }
    }

    // --- ComputeHunks ---

    [Fact]
    public void ComputeHunks_NoChanges_NoHunks()
    {
        Assert.Empty(LineDiff.ComputeHunks("same\n", "same\n"));
        Assert.Empty(LineDiff.ComputeHunks(string.Empty, string.Empty));
    }

    [Fact]
    public void ComputeHunks_DistantChanges_SeparateHunks()
    {
        var oldText = string.Join(string.Empty, Enumerable.Range(1, 40).Select(i => $"l{i}\n"));
        var newLines = Enumerable.Range(1, 40).Select(i => i == 1 || i == 40 ? $"L{i}\n" : $"l{i}\n");
        var newText = string.Join(string.Empty, newLines);

        var hunks = LineDiff.ComputeHunks(oldText, newText);

        Assert.Equal(2, hunks.Length);
        Assert.Equal(1, hunks[0].OldStart);
        Assert.Equal(4, hunks[0].OldLines);
        Assert.Equal(1, hunks[0].NewStart);
        Assert.Equal(4, hunks[0].NewLines);
        Assert.Equal(37, hunks[1].OldStart);
        Assert.Equal(4, hunks[1].OldLines);
        Assert.Equal(37, hunks[1].NewStart);
        Assert.Equal(4, hunks[1].NewLines);
    }

    [Fact]
    public void ComputeHunks_NearbyChanges_MergeIntoOneHunk()
    {
        var oldText = "1\n2\n3\n4\n5\n6\n";
        var newText = "1\nX\n3\nY\n5\n6\n";

        var hunks = LineDiff.ComputeHunks(oldText, newText);

        var hunk = Assert.Single(hunks);
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(6, hunk.OldLines);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(6, hunk.NewLines);
    }

    [Fact]
    public void ComputeHunks_ContextClampsAtFileBoundaries()
    {
        var hunks = LineDiff.ComputeHunks("a\nb\nc\n", "a\nB\nc\n");

        var hunk = Assert.Single(hunks);
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(3, hunk.OldLines);
    }

    [Fact]
    public void ComputeHunks_InsertionAtStart_UsesZeroOldAnchor()
    {
        var hunks = LineDiff.ComputeHunks(string.Empty, "x\ny\n");

        var hunk = Assert.Single(hunks);
        Assert.Equal(0, hunk.OldStart);
        Assert.Equal(0, hunk.OldLines);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(2, hunk.NewLines);
    }

    [Fact]
    public void ComputeHunks_InsertionAfterLastLine_AnchorsAtPrecedingContext()
    {
        var oldText = "a\nb\nc\nd\ne\n";
        var newText = "a\nb\nc\nd\ne\nf\n";

        var hunks = LineDiff.ComputeHunks(oldText, newText);

        var hunk = Assert.Single(hunks);
        Assert.Equal(3, hunk.OldStart);
        Assert.Equal(3, hunk.OldLines);
        Assert.Equal(3, hunk.NewStart);
        Assert.Equal(4, hunk.NewLines);
    }

    [Fact]
    public void ComputeHunks_NegativeContext_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LineDiff.ComputeHunks("a\n", "b\n", -1));
    }

    // --- RenderUnified ---

    [Fact]
    public void RenderUnified_SingleReplacement_MatchesUnifiedFormat()
    {
        var rendered = LineDiff.RenderUnified("one\ntwo\nthree\n", "one\nTWO\nthree\n");

        Assert.Equal("@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three\n", rendered);
    }

    [Fact]
    public void RenderUnified_MissingTrailingNewline_EmitsMarker()
    {
        var rendered = LineDiff.RenderUnified("one\ntwo", "one\ntwo\n");

        Assert.Equal("@@ -1,2 +1,2 @@\n one\n-two\n\\ No newline at end of file\n+two\n", rendered);
    }

    [Fact]
    public void RenderUnified_IdenticalTexts_IsEmpty()
    {
        Assert.Equal(string.Empty, LineDiff.RenderUnified("same\n", "same\n"));
    }

    // --- Sanity performance ---

    [Fact]
    public void Compute_LargeFiles_SparseEdits_StaysFast()
    {
        var lines = Enumerable.Range(0, 10_000).Select(i => $"line {i}").ToArray();
        var oldText = string.Join("\n", lines) + "\n";

        var modified = lines.ToArray();
        for (int i = 0; i < modified.Length; i += 500)
        {
            modified[i] = $"line {i} changed";
        }

        var withInsertions = modified.ToList();
        withInsertions.Insert(0, "inserted head");
        withInsertions.Add("inserted tail");
        var newText = string.Join("\n", withInsertions) + "\n";

        var stopwatch = Stopwatch.StartNew();
        var entries = LineDiff.Compute(oldText, newText);
        var hunks = LineDiff.ComputeHunks(oldText, newText);
        stopwatch.Stop();

        // 20 replaced lines (every 500th) plus 2 insertions.
        Assert.Equal(20, entries.Count(e => e.Kind == LineChangeKind.Deleted));
        Assert.Equal(22, entries.Count(e => e.Kind == LineChangeKind.Added));
        Assert.Equal(9_980, entries.Count(e => e.Kind == LineChangeKind.Equal));
        // Head insert + change at line 1 share a hunk; the other 19 edits (500 lines apart)
        // and the tail insert each get their own hunk.
        Assert.Equal(21, hunks.Length);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Diff took {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static List<string> RandomLines(Random random, int count)
    {
        string[] alphabet = ["a", "b", "c", ""];
        return Enumerable.Range(0, count).Select(_ => alphabet[random.Next(alphabet.Length)]).ToList();
    }

    private static int ReferenceDistance(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
        {
            for (int j = b.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        return a.Count + b.Count - 2 * lcs[0, 0];
    }
}
