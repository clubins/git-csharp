using System.Collections.Immutable;
using System.Text;

namespace Scion.Core;

/// <summary>
/// Three-way merge in the diff3 lineage: each side is diffed against the merge base, the two
/// alignments are swept into maximal unstable chunks — base regions where at least one side
/// changed, containing no line all three texts agree on — and each chunk is resolved
/// independently. A chunk only one side changed is taken from that side; a chunk both sides
/// changed identically is taken once; anything else is a conflict, rendered with the standard
/// markers: <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> ours, <c>|||||||</c> base, <c>=======</c>,
/// <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> theirs.
/// </summary>
/// <remarks>
/// <para>
/// The line-level merge (<see cref="MergeText"/>) is pure text algebra. The tree-level merge
/// (<see cref="Merge(TreeSnapshot, TreeSnapshot, TreeSnapshot, Func{string, string})"/>) composes
/// it with <see cref="TreeDiff"/>: each side's delta from the base is classified per path —
/// changes on disjoint paths compose silently, both-side edits of one path drop to the line-level
/// merge, and the structural cases (delete/modify, and renames under the <see cref="TreeSnapshot"/>
/// rename convention) resolve per <see cref="MergeConflictKind"/>. Neither level touches storage:
/// file contents arrive through the caller-supplied lookup, and merged content is returned as
/// text for the caller to hash and store. Decoding is the caller's too; blob text is UTF-8.
/// </para>
/// <para>
/// A conflicted path keeps ours' state in the merged tree — the working-copy convention git uses,
/// so a resolver always starts from a well-defined side. The merge base itself is the caller's
/// choice; for changeset histories that is <see cref="MergeBasePolicy.Select"/>, which returns
/// null for disjoint histories — <see cref="MergeBasePolicy.SelectRequired"/> turns that null into
/// the exception a merge cannot proceed without. Renames are detected by content only, inheriting
/// <see cref="TreeDiff"/>'s convention: a pure move pairs source and target; a move that also
/// edits content diffs as a delete plus add and merges as such.
/// </para>
/// </remarks>
public static class ThreeWayMerge
{
    // ---- Tree level ---------------------------------------------------------------------

    /// <summary>
    /// Merges theirs into ours against their common base's tree. Changes touching disjoint paths
    /// compose silently; both-side edits of one path are reconciled line by line
    /// (<see cref="MergeText"/>); the structural cases resolve per <see cref="MergeConflictKind"/>.
    /// The result never depends on which side is named ours: swapping ours and theirs swaps the
    /// roles in the output, not the set of clean paths.
    /// </summary>
    /// <param name="baseTree">The tree at the merge base — <see cref="TreeSnapshot.Empty"/> merges
    /// two sides that share nothing.</param>
    /// <param name="oursTree">Our side's tree; the result starts from it.</param>
    /// <param name="theirsTree">Their side's tree.</param>
    /// <param name="contentLookup">Maps a blob hash to its decoded text. Called only for paths
    /// both sides changed — never for paths resolved structurally.</param>
    public static ThreeWayMergeResult Merge(
        TreeSnapshot baseTree,
        TreeSnapshot oursTree,
        TreeSnapshot theirsTree,
        Func<string, string> contentLookup)
    {
        ArgumentNullException.ThrowIfNull(baseTree);
        ArgumentNullException.ThrowIfNull(oursTree);
        ArgumentNullException.ThrowIfNull(theirsTree);
        ArgumentNullException.ThrowIfNull(contentLookup);

        var oursDelta = TreeDiff.Diff(baseTree, oursTree);
        var theirsDelta = TreeDiff.Diff(baseTree, theirsTree);
        var oursMoves = ExtractRenames(oursDelta);
        var theirsMoves = ExtractRenames(theirsDelta);
        var oursMoveBySource = MovesBySource(oursMoves);
        var oursMoveByTarget = MovesByTarget(oursMoves);

        // The merged tree starts from ours (the merge changeset's first parent), so only theirs'
        // changes need applying. Theirs' renames go first: a rename spans two paths, and the
        // per-path pass must see both the applied result and the skip markers.
        var state = new MergeState(oursTree.Entries);
        foreach (var move in theirsMoves)
        {
            MergeTheirsRename(state, move, oursMoveBySource, oursMoveByTarget, contentLookup);
        }

        foreach (var change in theirsDelta)
        {
            if (change.Type == ChangeType.Renamed || state.Handled.Contains(change.Path))
            {
                continue;
            }

            switch (change.Type)
            {
                case ChangeType.Deleted:
                    MergeDeleted(state, change, oursMoveBySource);
                    break;

                case ChangeType.Added:
                    MergeAdded(state, change, oursMoveByTarget, contentLookup);
                    break;

                case ChangeType.Modified:
                    MergeModified(state, baseTree, change, oursMoveBySource, contentLookup);
                    break;
            }
        }

        return new ThreeWayMergeResult(
            new TreeSnapshot(state.Entries),
            [.. state.Conflicts],
            state.MergedContents.ToImmutable());
    }

    private static void MergeTheirsRename(
        MergeState state,
        Move move,
        ImmutableDictionary<string, Move> oursMoveBySource,
        ImmutableDictionary<string, Move> oursMoveByTarget,
        Func<string, string> contentLookup)
    {
        state.Handled.Add(move.Source);
        state.Handled.Add(move.Target);

        if (oursMoveBySource.TryGetValue(move.Source, out var oursMove))
        {
            if (oursMove.Target != move.Target)
            {
                // Both renamed the same source to different targets — ours' rename stands.
                state.Conflicts.Add(new MergeConflict(
                    move.Source,
                    MergeConflictKind.RenameRename,
                    move.BlobHash,
                    oursMove.BlobHash,
                    move.BlobHash,
                    MergedContent: null,
                    OursTarget: oursMove.Target,
                    TheirsTarget: move.Target));
            }

            return; // identical renames need nothing — ours' is already in the tree
        }

        if (!state.Entries.TryGetValue(move.Source, out var oursSource))
        {
            // Ours deleted what theirs renamed — ours' deletion stands.
            state.Conflicts.Add(new MergeConflict(
                move.Source,
                MergeConflictKind.RenameDelete,
                move.BlobHash,
                OursBlob: null,
                TheirsBlob: move.BlobHash,
                MergedContent: null,
                TheirsTarget: move.Target));
            return;
        }

        if (state.Entries.TryGetValue(move.Target, out var oursTarget))
        {
            // Ours has its own content at the target — a file it added, or a rename of ours
            // landing there. Theirs' rename changes no content, so the target's content
            // reconciles against the source's base content; the source drops only when the
            // target reconciles cleanly.
            var baseText = oursMoveByTarget.TryGetValue(move.Target, out var oursMoveIn)
                ? contentLookup(oursMoveIn.BlobHash)
                : contentLookup(move.BlobHash);
            var merged = MergeText(baseText, contentLookup(oursTarget.BlobHash), contentLookup(move.BlobHash));
            if (!merged.Clean)
            {
                state.Conflicts.Add(new MergeConflict(
                    move.Target,
                    MergeConflictKind.Content,
                    move.BlobHash,
                    oursTarget.BlobHash,
                    move.BlobHash,
                    merged.Text));
                return;
            }

            var blob = MergedBlob(merged.Text);
            state.Remove(move.Source);
            state.Set(move.Target, new TreeEntry(blob.Hash, oursTarget.Kind));
            RecordSynthesizedContent(state, move.Target, merged.Text, blob, oursTarget.BlobHash, move.BlobHash);
            return;
        }

        // A pure rename carries ours' edit when ours modified the source in place.
        var oursEditedSource = !string.Equals(oursSource.BlobHash, move.BlobHash, StringComparison.OrdinalIgnoreCase);
        state.Remove(move.Source);
        state.Set(move.Target, oursEditedSource ? oursSource : new TreeEntry(move.BlobHash, move.Kind));
    }

    private static void MergeDeleted(
        MergeState state,
        ChangeEntry change,
        ImmutableDictionary<string, Move> oursMoveBySource)
    {
        if (oursMoveBySource.TryGetValue(change.Path, out var renamedAway))
        {
            // Theirs deleted what ours renamed — ours' move stands.
            state.Conflicts.Add(new MergeConflict(
                change.Path,
                MergeConflictKind.RenameDelete,
                change.OldBlob,
                renamedAway.BlobHash,
                TheirsBlob: null,
                MergedContent: null,
                OursTarget: renamedAway.Target));
            return;
        }

        if (!state.Entries.TryGetValue(change.Path, out var oursEntry))
        {
            return; // both deleted — nothing to merge
        }

        if (string.Equals(oursEntry.BlobHash, change.OldBlob, StringComparison.OrdinalIgnoreCase))
        {
            state.Remove(change.Path); // ours kept the base version; theirs' deletion applies
            return;
        }

        // Ours modified what theirs deleted — ours' version stands.
        state.Conflicts.Add(new MergeConflict(
            change.Path, MergeConflictKind.DeleteModify, change.OldBlob, oursEntry.BlobHash, TheirsBlob: null, MergedContent: null));
    }

    private static void MergeAdded(
        MergeState state,
        ChangeEntry change,
        ImmutableDictionary<string, Move> oursMoveByTarget,
        Func<string, string> contentLookup)
    {
        var theirsEntry = new TreeEntry(change.NewBlob!, change.Kind);
        if (!state.Entries.TryGetValue(change.Path, out var oursEntry))
        {
            state.Set(change.Path, theirsEntry); // only theirs added it
            return;
        }

        if (oursEntry.Equals(theirsEntry))
        {
            return; // identical adds
        }

        // Both added the path with different content. When ours' copy arrived by rename, the
        // rename source's base content is the real common ancestor; otherwise none exists.
        var baseText = oursMoveByTarget.TryGetValue(change.Path, out var movedIn)
            ? contentLookup(movedIn.BlobHash)
            : string.Empty;
        var merged = MergeText(baseText, contentLookup(oursEntry.BlobHash), contentLookup(change.NewBlob!));
        if (!merged.Clean)
        {
            state.Conflicts.Add(new MergeConflict(
                change.Path,
                MergeConflictKind.Content,
                BaseBlob: movedIn?.BlobHash,
                oursEntry.BlobHash,
                change.NewBlob,
                merged.Text));
            return;
        }

        var blob = MergedBlob(merged.Text);
        state.Set(change.Path, new TreeEntry(blob.Hash, oursEntry.Kind)); // no base kind to arbitrate — ours' stands
        RecordSynthesizedContent(state, change.Path, merged.Text, blob, oursEntry.BlobHash, change.NewBlob);
    }

    private static void MergeModified(
        MergeState state,
        TreeSnapshot baseTree,
        ChangeEntry change,
        ImmutableDictionary<string, Move> oursMoveBySource,
        Func<string, string> contentLookup)
    {
        var theirsEntry = new TreeEntry(change.NewBlob!, change.Kind);
        if (!state.Entries.TryGetValue(change.Path, out var oursEntry))
        {
            if (oursMoveBySource.TryGetValue(change.Path, out var movedAway))
            {
                // Ours renamed the path; theirs edited its content — the edit lands on ours' target.
                state.Set(movedAway.Target, theirsEntry);
                return;
            }

            // Ours deleted what theirs modified — ours' deletion stands.
            state.Conflicts.Add(new MergeConflict(
                change.Path, MergeConflictKind.DeleteModify, change.OldBlob, OursBlob: null, change.NewBlob, MergedContent: null));
            return;
        }

        if (oursEntry.Equals(theirsEntry))
        {
            return; // identical changes
        }

        TreeEntry? baseEntry = baseTree.TryGet(change.Path, out var found) ? found : null;
        if (string.Equals(oursEntry.BlobHash, change.NewBlob, StringComparison.OrdinalIgnoreCase))
        {
            // Same content both sides — only the kind can disagree. Ours' kind stands when ours
            // changed it; theirs' applies when ours left the base kind alone.
            if (baseEntry is not null && oursEntry.Kind == baseEntry.Kind)
            {
                state.Set(change.Path, theirsEntry);
            }

            return;
        }

        // Both changed the content — the diff3 merge decides.
        var merged = MergeText(
            contentLookup(change.OldBlob!),
            contentLookup(oursEntry.BlobHash),
            contentLookup(change.NewBlob!));
        if (!merged.Clean)
        {
            state.Conflicts.Add(new MergeConflict(
                change.Path,
                MergeConflictKind.Content,
                change.OldBlob,
                oursEntry.BlobHash,
                change.NewBlob,
                merged.Text));
            return;
        }

        var blob = MergedBlob(merged.Text);
        var kind = baseEntry is not null ? ResolveKind(baseEntry.Kind, oursEntry.Kind, change.Kind) : oursEntry.Kind;
        state.Set(change.Path, new TreeEntry(blob.Hash, kind));
        RecordSynthesizedContent(state, change.Path, merged.Text, blob, oursEntry.BlobHash, change.NewBlob);
    }

    /// <summary>
    /// Recovers the rename pairs a delta encodes in the <see cref="TreeSnapshot"/> convention:
    /// each <see cref="ChangeType.Renamed"/> entry upserts its target while the source's removal
    /// travels as its own <see cref="ChangeType.Deleted"/> entry. <see cref="TreeDiff"/> pairs the
    /// i-th deleted path with the i-th added path per blob hash, both in ordinal order, and the
    /// canonical delta sort keeps each list ordinal — so matching each hash's deletions (ordinal
    /// source order) with its renames in target order reproduces exactly the pairs TreeDiff
    /// paired. Two passes, because the sort does not guarantee the Deleted entry precedes its
    /// Renamed entry.
    /// </summary>
    private static List<Move> ExtractRenames(ImmutableArray<ChangeEntry> delta)
    {
        var deletions = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in delta)
        {
            if (change.Type == ChangeType.Deleted)
            {
                if (!deletions.TryGetValue(change.OldBlob!, out var queue))
                {
                    queue = new Queue<string>();
                    deletions.Add(change.OldBlob!, queue);
                }

                queue.Enqueue(change.Path);
            }
        }

        var moves = new List<Move>();
        foreach (var change in delta)
        {
            if (change.Type != ChangeType.Renamed)
            {
                continue;
            }

            if (!deletions.TryGetValue(change.OldBlob!, out var sources) || sources.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Malformed delta: rename to '{change.Path}' has no matching deletion for blob '{change.OldBlob}'.");
            }

            moves.Add(new Move(sources.Dequeue(), change.Path, change.OldBlob!, change.Kind));
        }

        return moves;
    }

    private static ImmutableDictionary<string, Move> MovesBySource(List<Move> moves)
    {
        return moves.ToImmutableDictionary(move => move.Source, StringComparer.Ordinal);
    }

    private static ImmutableDictionary<string, Move> MovesByTarget(List<Move> moves)
    {
        return moves.ToImmutableDictionary(move => move.Target, StringComparer.Ordinal);
    }

    /// <summary>The side that changed the kind wins it; when both changed, ours' stands (a v1 simplification).</summary>
    private static FileContentKind ResolveKind(FileContentKind baseKind, FileContentKind oursKind, FileContentKind theirsKind)
    {
        return oursKind != baseKind ? oursKind : theirsKind;
    }

    private static Blob MergedBlob(string text)
    {
        return Blob.Create(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Records merged content the tree now references but no side had before, so the caller can store the new blob.</summary>
    private static void RecordSynthesizedContent(
        MergeState state,
        string path,
        string text,
        Blob blob,
        string oursBlob,
        string? theirsBlob)
    {
        if (!string.Equals(blob.Hash, oursBlob, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(blob.Hash, theirsBlob, StringComparison.OrdinalIgnoreCase))
        {
            state.MergedContents[path] = text;
        }
    }

    /// <summary>Accumulating state for one <see cref="Merge"/> call: the merged entries plus what the merge decided per path.</summary>
    private sealed class MergeState(ImmutableSortedDictionary<string, TreeEntry> entries)
    {
        public ImmutableSortedDictionary<string, TreeEntry> Entries { get; private set; } = entries;

        public List<MergeConflict> Conflicts { get; } = [];

        public ImmutableDictionary<string, string>.Builder MergedContents { get; } =
            ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        /// <summary>Paths a theirs' rename already accounted for, so the companion delta entries skip.</summary>
        public HashSet<string> Handled { get; } = new(StringComparer.Ordinal);

        public void Set(string path, TreeEntry entry)
        {
            Entries = Entries.SetItem(path, entry);
        }

        public void Remove(string path)
        {
            Entries = Entries.Remove(path);
        }
    }

    /// <summary>A rename pair as a delta encodes it: the source's Deleted entry and the target's Renamed entry.</summary>
    private sealed record Move(string Source, string Target, string BlobHash, FileContentKind Kind);

    // ---- Line level ---------------------------------------------------------------------

    /// <summary>
    /// Merges two texts against their common base, line by line. Regions exactly one side changed
    /// resolve from that side; regions both changed identically are taken once; regions both
    /// changed differently become conflict blocks in <see cref="LineMergeResult.Text"/>, which is
    /// then not clean. Labels name the sides inside the markers.
    /// </summary>
    public static LineMergeResult MergeText(
        string baseText,
        string oursText,
        string theirsText,
        string oursLabel = "ours",
        string baseLabel = "base",
        string theirsLabel = "theirs")
    {
        ArgumentNullException.ThrowIfNull(baseText);
        ArgumentNullException.ThrowIfNull(oursText);
        ArgumentNullException.ThrowIfNull(theirsText);
        ArgumentNullException.ThrowIfNull(oursLabel);
        ArgumentNullException.ThrowIfNull(baseLabel);
        ArgumentNullException.ThrowIfNull(theirsLabel);

        var baseLines = LineDiff.SplitLines(baseText);
        var ours = Analyze(baseText, baseLines, oursText);
        var theirs = Analyze(baseText, baseLines, theirsText);
        var oursLines = LineDiff.SplitLines(oursText);
        var theirsLines = LineDiff.SplitLines(theirsText);

        var merged = new StringBuilder();
        var conflictCount = 0;
        var copied = 0;
        foreach (var chunk in Chunk(ours, theirs))
        {
            AppendRange(merged, baseLines, copied, chunk.Start);
            var oursStart = SideStart(ours, chunk.Start);
            var oursEnd = SideEnd(ours, chunk.End, baseLines.Length);
            var theirsStart = SideStart(theirs, chunk.Start);
            var theirsEnd = SideEnd(theirs, chunk.End, baseLines.Length);

            if (!chunk.TheirsChanged)
            {
                AppendRange(merged, oursLines, oursStart, oursEnd);
            }
            else if (!chunk.OursChanged)
            {
                AppendRange(merged, theirsLines, theirsStart, theirsEnd);
            }
            else if (SameLines(oursLines, oursStart, oursEnd, theirsLines, theirsStart, theirsEnd))
            {
                AppendRange(merged, oursLines, oursStart, oursEnd); // both changed identically — take it once
            }
            else
            {
                conflictCount++;
                AppendConflict(
                    merged, oursLabel, baseLabel, theirsLabel,
                    oursLines, oursStart, oursEnd,
                    baseLines, chunk.Start, chunk.End,
                    theirsLines, theirsStart, theirsEnd);
            }

            copied = chunk.End;
        }

        AppendRange(merged, baseLines, copied, baseLines.Length);
        return new LineMergeResult(conflictCount == 0, merged.ToString());
    }

    private readonly record struct Run(int BaseStart, int BaseEnd, int SideStart, int SideEnd);

    private readonly record struct UnstableRegion(int Start, int End, bool OursChanged, bool TheirsChanged);

    /// <summary>
    /// How one side's text maps onto the base: where each base line's slot sits on the side, and
    /// which base lines it preserved. <see cref="Pos"/> is monotone — matches and deletion slots
    /// are ordered on both ends — so a base range projects to one contiguous side range.
    /// </summary>
    private sealed record SideView(int[] Pos, bool[] Present, int LineCount, List<Run> Runs);

    /// <summary>
    /// Diff <paramref name="sideText"/> against the base and record the projection: a base line
    /// keeps its match position when the side preserved it, otherwise the position its deletion
    /// occupies. Runs are the maximal groups of non-equal alignment steps — the side's contiguous
    /// edits, base range against side range, both end-exclusive.
    /// </summary>
    private static SideView Analyze(string baseText, ImmutableArray<string> baseLines, string sideText)
    {
        var pos = new int[baseLines.Length];
        var present = new bool[baseLines.Length];
        var runs = new List<Run>();
        var baseIdx = 0;
        var sideIdx = 0;
        var runStart = -1;
        var runBaseEnd = 0;
        var runSideStart = 0;

        foreach (var entry in LineDiff.Compute(baseText, sideText))
        {
            switch (entry.Kind)
            {
                case LineChangeKind.Equal:
                    CloseRun();
                    pos[baseIdx] = sideIdx;
                    present[baseIdx] = true;
                    baseIdx++;
                    sideIdx++;
                    break;

                case LineChangeKind.Deleted:
                    if (runStart < 0)
                    {
                        runStart = baseIdx;
                        runBaseEnd = baseIdx + 1;
                        runSideStart = sideIdx;
                    }
                    else
                    {
                        runBaseEnd = baseIdx + 1;
                    }

                    pos[baseIdx] = sideIdx;
                    present[baseIdx] = false;
                    baseIdx++;
                    break;

                default: // Added
                    if (runStart < 0)
                    {
                        runStart = baseIdx;
                        runBaseEnd = baseIdx; // a pure insertion spans no base lines
                        runSideStart = sideIdx;
                    }

                    sideIdx++;
                    break;
            }
        }

        CloseRun();
        return new SideView(pos, present, sideIdx, runs);

        void CloseRun()
        {
            if (runStart >= 0)
            {
                runs.Add(new Run(runStart, runBaseEnd, runSideStart, sideIdx));
                runStart = -1;
            }
        }
    }

    /// <summary>
    /// Sweeps both sides' runs — sorted by base position — into chunks: runs whose base ranges
    /// overlap or touch belong to one unstable region, because no stable line separates them.
    /// </summary>
    private static List<UnstableRegion> Chunk(SideView ours, SideView theirs)
    {
        var runs = new List<(Run Run, bool IsOurs)>(ours.Runs.Count + theirs.Runs.Count);
        runs.AddRange(ours.Runs.Select(run => (run, true)));
        runs.AddRange(theirs.Runs.Select(run => (run, false)));
        runs.Sort((x, y) =>
        {
            var byStart = x.Run.BaseStart.CompareTo(y.Run.BaseStart);
            return byStart != 0 ? byStart : x.Run.BaseEnd.CompareTo(y.Run.BaseEnd);
        });

        var chunks = new List<UnstableRegion>(runs.Count);
        foreach (var (run, isOurs) in runs)
        {
            if (chunks.Count > 0 && run.BaseStart <= chunks[^1].End)
            {
                var last = chunks[^1];
                chunks[^1] = new UnstableRegion(
                    last.Start,
                    Math.Max(last.End, run.BaseEnd),
                    last.OursChanged || isOurs,
                    last.TheirsChanged || !isOurs);
            }
            else
            {
                chunks.Add(new UnstableRegion(run.BaseStart, run.BaseEnd, isOurs, !isOurs));
            }
        }

        return chunks;
    }

    /// <summary>
    /// The side line where the chunk's content begins: just after base line <c>start-1</c>'s
    /// content (its match position plus one, or its deletion position) — the gap in between holds
    /// exactly the side's insertions at the chunk's opening boundary.
    /// </summary>
    private static int SideStart(SideView side, int baseStart)
    {
        return baseStart == 0 ? 0 : side.Pos[baseStart - 1] + (side.Present[baseStart - 1] ? 1 : 0);
    }

    /// <summary>
    /// The side line where the chunk's content ends: base line <c>end</c>'s slot, or the side's
    /// full length when the chunk runs to the end of the base (taking the side's trailing
    /// insertions with it).
    /// </summary>
    private static int SideEnd(SideView side, int baseEnd, int baseLineCount)
    {
        return baseEnd == baseLineCount ? side.LineCount : side.Pos[baseEnd];
    }

    private static bool SameLines(ImmutableArray<string> left, int leftStart, int leftEnd, ImmutableArray<string> right, int rightStart, int rightEnd)
    {
        if (leftEnd - leftStart != rightEnd - rightStart)
        {
            return false;
        }

        for (var i = 0; i < leftEnd - leftStart; i++)
        {
            if (!string.Equals(left[leftStart + i], right[rightStart + i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendRange(StringBuilder merged, ImmutableArray<string> lines, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            merged.Append(lines[i]);
        }
    }

    private static void AppendConflict(
        StringBuilder merged,
        string oursLabel,
        string baseLabel,
        string theirsLabel,
        ImmutableArray<string> oursLines, int oursStart, int oursEnd,
        ImmutableArray<string> baseLines, int baseStart, int baseEnd,
        ImmutableArray<string> theirsLines, int theirsStart, int theirsEnd)
    {
        merged.Append("<<<<<<< ").Append(oursLabel).Append('\n');
        AppendMarkedLines(merged, oursLines, oursStart, oursEnd);
        merged.Append("||||||| ").Append(baseLabel).Append('\n');
        AppendMarkedLines(merged, baseLines, baseStart, baseEnd);
        merged.Append("=======\n");
        AppendMarkedLines(merged, theirsLines, theirsStart, theirsEnd);
        merged.Append(">>>>>>> ").Append(theirsLabel).Append('\n');
    }

    private static void AppendMarkedLines(StringBuilder merged, ImmutableArray<string> lines, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            merged.Append(lines[i]);
            if (!lines[i].EndsWith('\n'))
            {
                // A marker must start on its own line; an unterminated line only ever ends a
                // section, so the missing newline is supplied rather than glued to a marker.
                merged.Append('\n');
            }
        }
    }
}
