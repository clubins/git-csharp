using LibGit2Sharp;

namespace Scion.Import.Git;

/// <summary>
/// One path's delta between a commit and its git first parent (or, for a root commit, against the
/// empty tree). Producers translate git's diff shapes into this one record so that building
/// <see cref="ChangeEntry"/>s is a single shared path.
/// </summary>
internal sealed record TreeDelta(
    string Path,
    ChangeKind Status,
    ObjectId? OldOid,
    ObjectId? NewOid,
    Mode OldMode,
    Mode NewMode);
