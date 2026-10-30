namespace Scion.Core;

/// <summary>How a changed path's content is interpreted by the working-copy materializer.</summary>
public enum FileContentKind
{
    Regular = 0,
    Executable = 1,
    Symlink = 2,
}
