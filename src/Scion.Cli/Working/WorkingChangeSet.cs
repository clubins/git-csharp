using Scion.Cli.Local;
using Scion.Core;

namespace Scion.Cli.Working;

/// <summary>
/// One path's delta between the local manifest and the working copy — the shared comparison both
/// <c>scn status</c> renders and <c>scn commit</c> ships. Blob hashes are lowercase hex SHA-256;
/// <see cref="OldHash"/> is null for additions, <see cref="NewHash"/> null for deletions.
/// </summary>
internal sealed record WorkingChange(string Path, ChangeType Type, FileContentKind Kind, string? OldHash, string? NewHash);

/// <summary>Compares the last-committed manifest against the working files. Pure: no I/O, shared by status and commit.</summary>
internal static class WorkingChangeSet
{
    public static IReadOnlyList<WorkingChange> Diff(
        IReadOnlyDictionary<string, WorkingState.Entry> manifest,
        IReadOnlyList<WorkingFile> workingFiles)
    {
        List<WorkingChange> changes = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var file in workingFiles)
        {
            if (!seen.Add(file.Path))
            {
                continue;
            }

            if (!manifest.TryGetValue(file.Path, out var committed))
            {
                changes.Add(new WorkingChange(file.Path, ChangeType.Added, file.Kind, OldHash: null, NewHash: file.Hash));
            }
            else if (!committed.Hash.Equals(file.Hash, StringComparison.Ordinal))
            {
                changes.Add(new WorkingChange(file.Path, ChangeType.Modified, file.Kind, committed.Hash, file.Hash));
            }
        }

        foreach (var (path, committed) in manifest)
        {
            if (!seen.Contains(path))
            {
                changes.Add(new WorkingChange(path, ChangeType.Deleted, committed.Kind, committed.Hash, NewHash: null));
            }
        }

        return [.. changes.OrderBy(change => change.Path, StringComparer.Ordinal)];
    }
}
