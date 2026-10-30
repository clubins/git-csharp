using System.Collections.Immutable;

namespace Scion.Core;

/// <summary>
/// Line-level blame over a path's version history: for every line of the newest version, the
/// changeset that introduced it. <see cref="Annotate"/> walks the path's versions oldest first —
/// the chain the path-history index answers — diffing consecutive versions with
/// <see cref="LineDiff"/> and propagating each line's attribution forward: a line the diff finds
/// unchanged keeps the attribution it carried, a line the diff finds added is attributed to the
/// version that added it.
/// </summary>
/// <remarks>
/// <para>
/// A version whose content is empty models the path's absence — a deletion, or the state before a
/// re-add. An empty version between two non-empty ones therefore re-attributes every line to the
/// re-adding version, exactly the semantics a deleted-and-re-added path should answer. The
/// versions list is the path's full touching chain, so consecutive versions are consecutive
/// modifications of the path: an added line belongs to the very next version after the one whose
/// content lacks it.
/// </para>
/// <para>
/// Attribution is line-exact, not move-aware: Myers aligns equal lines wherever they sit, so a
/// pure reorder of unchanged lines resolves to whichever side supplied the alignment, and a
/// rewritten line belongs to the version that last wrote its exact text. This is the same
/// line-based honesty <see cref="LineDiff"/> trades in — copy detection is a later derived type's
/// job, not this function's.
/// </para>
/// </remarks>
public static class Blame
{
    /// <summary>One version of a path: the changeset that produced it and the path's content there.</summary>
    public sealed record BlameVersion(string ChangesetId, string Content);

    /// <summary>
    /// One line of the newest version's blame: its 1-based line number, the changeset that
    /// introduced it, and the line's content including its terminating newline when it has one.
    /// </summary>
    public sealed record LineAttribution(int Line, string ChangesetId, string Content);

    /// <summary>
    /// Attributes every line of the last version to the changeset that introduced it. The versions
    /// run oldest first; the result covers exactly the last version's lines, in line order.
    /// </summary>
    /// <exception cref="ArgumentException">The version list is empty, a version names no changeset,
    /// or a version's content is null.</exception>
    public static ImmutableArray<LineAttribution> Annotate(ImmutableArray<BlameVersion> versions)
    {
        if (versions.IsDefault || versions.IsEmpty)
        {
            throw new ArgumentException(
                "Blame needs at least one version — the path's content at its first touching changeset.", nameof(versions));
        }

        var current = Validate(versions[0]);
        // The first version added every line it has: nothing predates the path's first touch.
        var attribution = new string?[LineDiff.SplitLines(current.Content).Length];
        Array.Fill(attribution, current.ChangesetId);

        for (var i = 1; i < versions.Length; i++)
        {
            var version = Validate(versions[i]);
            var versionLines = LineDiff.SplitLines(version.Content);

            // Every line of the new side is covered exactly once — Equal entries inherit the old
            // side's attribution, Added entries carry this version's. Deleted lines drop out with
            // the content they removed, and their attribution dies with them.
            var propagated = new string?[versionLines.Length];
            foreach (var entry in LineDiff.Compute(current.Content, version.Content))
            {
                switch (entry.Kind)
                {
                    case LineChangeKind.Equal:
                        propagated[entry.NewLine - 1] = attribution[entry.OldLine - 1];
                        break;
                    case LineChangeKind.Added:
                        propagated[entry.NewLine - 1] = version.ChangesetId;
                        break;
                }
            }

            current = version;
            attribution = propagated;
        }

        var lines = LineDiff.SplitLines(current.Content);
        var result = ImmutableArray.CreateBuilder<LineAttribution>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            result.Add(attribution[i] is { } changesetId
                ? new LineAttribution(i + 1, changesetId, lines[i])
                : throw new InvalidOperationException(
                    $"Line {i + 1} of the last version carried no attribution; the version chain is inconsistent."));
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// The name the file answered to before one delta, given its name in the delta's post-state —
    /// the backward half of walking a path's version chain through renames. Modified, deleted,
    /// and re-added entries keep the name — the chain is a path's lineage, so a re-add continues
    /// the same path's history, and its deleted state surfaces as a missing tree entry (an empty
    /// version) rather than a name change. A renamed entry follows the tree-diff convention to
    /// recover the pre-rename name: the paired deleted path carrying the renamed entry's old blob
    /// (ordinal first among matches, mirroring <see cref="TreeDiff"/>'s one-to-one rule). A
    /// rename whose paired deletion is absent — a hand-built delta outside the convention — keeps
    /// the name, the same fallback <see cref="Scion.Core.Derived.PathHistoryValue"/> applies to
    /// unpaired renames.
    /// </summary>
    public static string PreviousName(ImmutableArray<ChangeEntry> delta, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        ChangeEntry? entry = null;
        foreach (var candidate in delta)
        {
            if (string.Equals(candidate.Path, name, StringComparison.Ordinal))
            {
                entry = candidate;
                break;
            }
        }

        return entry switch
        {
            // The delta never touched the file by this name: the name carried over.
            null => name,

            // A rename moved the file: the pre-rename name is the paired deletion's path.
            { Type: ChangeType.Renamed } => PairedDeletion(delta, entry).Path,

            // Added, modified, and deleted keep the name.
            _ => name,
        };
    }

    /// <summary>
    /// The deleted entry a rename is paired with: the ordinal-first deletion naming the renamed
    /// entry's old blob under a different path. The convention guarantees exactly one; a delta
    /// without it is malformed and surfaces as the missing path at the earlier version.
    /// </summary>
    private static ChangeEntry PairedDeletion(ImmutableArray<ChangeEntry> delta, ChangeEntry renamed)
    {
        foreach (var candidate in delta)
        {
            if (candidate.Type == ChangeType.Deleted
                && candidate.Path != renamed.Path
                && string.Equals(candidate.OldBlob, renamed.OldBlob, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return renamed;
    }

    private static BlameVersion Validate(BlameVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version.ChangesetId);
        ArgumentNullException.ThrowIfNull(version.Content);
        return version;
    }
}
