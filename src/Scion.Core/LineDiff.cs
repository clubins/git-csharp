using System.Collections.Immutable;
using System.Text;

namespace Scion.Core;

/// <summary>
/// Line-level Myers diff over texts, producing a full alignment plus unified-diff hunks.
/// Lines include their terminating newline, so a missing newline at end of file is a real
/// change; comparison is ordinal, so it is encoding- and culture-independent.
/// </summary>
public static class LineDiff
{
    /// <summary>Splits text into lines, each keeping its terminating "\n" when present.</summary>
    public static ImmutableArray<string> SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                builder.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            builder.Add(text[start..]);
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Aligns the two texts line by line. Equal entries carry the 1-based line number on both
    /// sides; additions carry <see cref="LineDiffEntry.NewLine"/>; deletions
    /// <see cref="LineDiffEntry.OldLine"/>. Identical texts yield only Equal entries.
    /// </summary>
    public static ImmutableArray<LineDiffEntry> Compute(string oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var (oldIds, newIds) = Intern(oldLines, newLines);
        var edits = MyersDiff.Compute(oldIds, newIds);

        var builder = ImmutableArray.CreateBuilder<LineDiffEntry>(edits.Count);
        foreach (var edit in edits)
        {
            builder.Add(edit.Kind switch
            {
                LineChangeKind.Equal => new LineDiffEntry(edit.Kind, edit.OldIndex + 1, edit.NewIndex + 1, oldLines[edit.OldIndex]),
                LineChangeKind.Deleted => new LineDiffEntry(edit.Kind, edit.OldIndex + 1, 0, oldLines[edit.OldIndex]),
                _ => new LineDiffEntry(edit.Kind, 0, edit.NewIndex + 1, newLines[edit.NewIndex]),
            });
        }

        return builder.ToImmutable();
    }

    /// <summary>Diffs two text blobs, decoding UTF-8 with replacement for invalid sequences.</summary>
    public static ImmutableArray<LineDiffEntry> Compute(Blob oldBlob, Blob newBlob)
    {
        return Compute(Encoding.UTF8.GetString(oldBlob.Content.Span), Encoding.UTF8.GetString(newBlob.Content.Span));
    }

    /// <summary>
    /// Groups the alignment into unified-diff hunks, keeping <paramref name="contextLines"/>
    /// unchanged lines around each change and merging changes whose separating context overlaps.
    /// </summary>
    public static ImmutableArray<DiffHunk> ComputeHunks(string oldText, string newText, int contextLines = 3)
    {
        if (contextLines < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contextLines), "Context line count cannot be negative.");
        }

        return BuildHunks(Compute(oldText, newText), contextLines);
    }

    /// <summary>
    /// Renders the hunks in unified diff format — hunk headers and bodies, no file headers.
    /// A final line lacking its newline emits the "\ No newline at end of file" marker, as git
    /// does. Returns an empty string when the texts are identical.
    /// </summary>
    public static string RenderUnified(string oldText, string newText, int contextLines = 3)
    {
        var hunks = ComputeHunks(oldText, newText, contextLines);
        if (hunks.Length == 0)
        {
            return string.Empty;
        }

        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var result = new StringBuilder();
        foreach (var hunk in hunks)
        {
            result.Append("@@ -").Append(hunk.OldStart);
            if (hunk.OldLines != 1)
            {
                result.Append(',').Append(hunk.OldLines);
            }

            result.Append(" +").Append(hunk.NewStart);
            if (hunk.NewLines != 1)
            {
                result.Append(',').Append(hunk.NewLines);
            }

            result.Append(" @@\n");

            foreach (var line in hunk.Lines)
            {
                bool touchesOld = line.Kind != LineChangeKind.Added;
                bool touchesNew = line.Kind != LineChangeKind.Deleted;
                result.Append(line.Kind switch
                {
                    LineChangeKind.Equal => ' ',
                    LineChangeKind.Deleted => '-',
                    _ => '+',
                }).Append(line.Content.TrimEnd('\n')).Append('\n');

                bool lastOfOld = touchesOld && line.OldLine == oldLines.Length;
                bool lastOfNew = touchesNew && line.NewLine == newLines.Length;
                if ((lastOfOld && !EndsWithNewline(oldLines)) || (lastOfNew && !EndsWithNewline(newLines)))
                {
                    result.Append("\\ No newline at end of file\n");
                }
            }
        }

        return result.ToString();
    }

    private static bool EndsWithNewline(ImmutableArray<string> lines)
    {
        return lines.Length > 0 && lines[^1].EndsWith('\n');
    }

    private static (int[] Old, int[] New) Intern(ImmutableArray<string> oldLines, ImmutableArray<string> newLines)
    {
        // Dictionary<string, int> compares ordinally — the byte-exact semantics a VCS needs.
        var ids = new Dictionary<string, int>(oldLines.Length + newLines.Length);
        return (Map(oldLines), Map(newLines));

        int[] Map(ImmutableArray<string> lines)
        {
            var result = new int[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                if (!ids.TryGetValue(lines[i], out int id))
                {
                    id = ids.Count;
                    ids[lines[i]] = id;
                }

                result[i] = id;
            }

            return result;
        }
    }

    private static ImmutableArray<DiffHunk> BuildHunks(ImmutableArray<LineDiffEntry> entries, int contextLines)
    {
        var builder = ImmutableArray.CreateBuilder<DiffHunk>();
        int i = 0;
        while (i < entries.Length)
        {
            while (i < entries.Length && entries[i].Kind == LineChangeKind.Equal)
            {
                i++;
            }

            if (i == entries.Length)
            {
                break;
            }

            // Extend the change group while the next change sits within hunk-merge distance.
            int first = i;
            int last = i;
            while (true)
            {
                int next = last + 1;
                while (next < entries.Length && entries[next].Kind == LineChangeKind.Equal)
                {
                    next++;
                }

                if (next == entries.Length || next - last - 1 > 2 * contextLines)
                {
                    break;
                }

                last = next;
            }

            int windowStart = Math.Max(0, first - contextLines);
            int windowEnd = Math.Min(entries.Length - 1, last + contextLines);
            builder.Add(BuildHunk(entries, windowStart, windowEnd));
            i = last + 1;
        }

        return builder.ToImmutable();
    }

    private static DiffHunk BuildHunk(ImmutableArray<LineDiffEntry> entries, int start, int end)
    {
        int oldBefore = 0, newBefore = 0;
        for (int i = 0; i < start; i++)
        {
            if (entries[i].Kind != LineChangeKind.Added)
            {
                oldBefore++;
            }

            if (entries[i].Kind != LineChangeKind.Deleted)
            {
                newBefore++;
            }
        }

        var lines = ImmutableArray.CreateBuilder<LineDiffEntry>(end - start + 1);
        int oldCount = 0, newCount = 0;
        for (int i = start; i <= end; i++)
        {
            var entry = entries[i];
            lines.Add(entry);
            if (entry.Kind != LineChangeKind.Added)
            {
                oldCount++;
            }

            if (entry.Kind != LineChangeKind.Deleted)
            {
                newCount++;
            }
        }

        return new DiffHunk(
            oldCount > 0 ? oldBefore + 1 : oldBefore,
            oldCount,
            newCount > 0 ? newBefore + 1 : newBefore,
            newCount,
            lines.ToImmutable());
    }
}
