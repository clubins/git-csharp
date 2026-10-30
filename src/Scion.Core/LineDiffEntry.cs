namespace Scion.Core;

/// <summary>
/// One step of a line-level alignment. Line numbers are 1-based; the side a step does not touch
/// is 0 (an added step has <see cref="OldLine"/> 0, a deletion has <see cref="NewLine"/> 0).
/// <see cref="Content"/> includes the line's terminating newline when it had one, so a missing
/// newline at end of file is a real change rather than an invisible one.
/// </summary>
public sealed record LineDiffEntry(LineChangeKind Kind, int OldLine, int NewLine, string Content);
