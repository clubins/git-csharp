namespace Scion.Core;

/// <summary>What one step of a line-level alignment did.</summary>
public enum LineChangeKind
{
    /// <summary>The line is unchanged and present in both texts.</summary>
    Equal = 0,

    /// <summary>The line exists only in the old text.</summary>
    Deleted = 1,

    /// <summary>The line exists only in the new text.</summary>
    Added = 2,
}
