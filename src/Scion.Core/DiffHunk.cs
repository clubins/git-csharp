using System.Collections.Immutable;

namespace Scion.Core;

/// <summary>
/// A unified-diff hunk: a window of alignment entries plus the 1-based positions it covers.
/// When a hunk contains no old lines (a pure insertion) <see cref="OldStart"/> anchors at the
/// old line the insertion follows — 0 at file start — mirroring the unified-diff convention;
/// <see cref="NewStart"/> mirrors it for deletions.
/// </summary>
public sealed record DiffHunk(
    int OldStart,
    int OldLines,
    int NewStart,
    int NewLines,
    ImmutableArray<LineDiffEntry> Lines);
