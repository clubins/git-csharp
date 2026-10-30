using System.Text;
using Scion.Core;

namespace Scion.Cli.Local;

/// <summary>One working-copy file: its canonical path (forward slashes, relative to the repo root), content kind, hash, and bytes.</summary>
/// <param name="Content">The file's content — for a symlink, the UTF-8 bytes of its target path.</param>
internal sealed record WorkingFile(string Path, FileContentKind Kind, string Hash, byte[] Content);

/// <summary>
/// Hashes the working copy. The scanner is the client's only view of the files on disk: it
/// classifies every file (regular, executable, symlink) and hashes its content, so both status
/// and commit are O(change) after the scan. The <c>.scn</c> state directory is never content.
/// </summary>
internal static class WorkingFileScanner
{
    public static IReadOnlyList<WorkingFile> Scan(string repoRoot)
    {
        List<WorkingFile> files = [];
        ScanDirectory(repoRoot, repoRoot, files);
        return files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
    }

    private static void ScanDirectory(string repoRoot, string directory, List<WorkingFile> files)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            // The state directory is local bookkeeping, never content — unless it is itself a
            // symlink, which is then just a symlink entry like any other.
            if (entry.Name == ScionPaths.DirectoryName && entry is DirectoryInfo { LinkTarget: null })
            {
                continue;
            }

            if (entry.LinkTarget is { } target)
            {
                Add(files, repoRoot, entry, FileContentKind.Symlink, Encoding.UTF8.GetBytes(target));
                continue;
            }

            if (entry is DirectoryInfo subdirectory)
            {
                ScanDirectory(repoRoot, subdirectory.FullName, files);
                continue;
            }

            var file = (FileInfo)entry;
            Add(files, repoRoot, entry, ClassifyKind(file), File.ReadAllBytes(file.FullName));
        }
    }

    private static void Add(List<WorkingFile> files, string repoRoot, FileSystemInfo entry, FileContentKind kind, byte[] content)
    {
        var relative = Path.GetRelativePath(repoRoot, entry.FullName).Replace('\\', '/');
        files.Add(new WorkingFile(relative, kind, Sha256.HashHex(content), content));
    }

    private static FileContentKind ClassifyKind(FileInfo file)
    {
        if (OperatingSystem.IsWindows())
        {
            return FileContentKind.Regular;
        }

        return File.GetUnixFileMode(file.FullName).HasFlag(UnixFileMode.UserExecute)
            ? FileContentKind.Executable
            : FileContentKind.Regular;
    }
}
