namespace Scion.Core;

/// <summary>Why a path failed to merge cleanly (see <see cref="MergeConflict"/>).</summary>
public enum MergeConflictKind
{
    /// <summary>Both sides changed the path's content and the diff3 merge could not reconcile
    /// the edits; <see cref="MergeConflict.MergedContent"/> carries the conflict-marked text.</summary>
    Content = 0,

    /// <summary>One side deleted the path while the other modified it; the merged tree keeps
    /// ours' state.</summary>
    DeleteModify = 1,

    /// <summary>Both sides renamed the same source path to different targets; the merged tree
    /// keeps ours' rename.</summary>
    RenameRename = 2,

    /// <summary>One side renamed the source path while the other deleted it; the merged tree
    /// keeps ours' state.</summary>
    RenameDelete = 3,
}
