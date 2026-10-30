using Scion.Cli.Local;
using Scion.Core;

namespace Scion.Cli.Tests;

/// <summary>Unit tests for the working-copy scanner: classification, canonical paths, exclusions.</summary>
[Collection("ScionCliSequential")]
public sealed class WorkingFileScannerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "scn-scanner-tests", Guid.NewGuid().ToString("N"));

    public WorkingFileScannerTests() => Directory.CreateDirectory(root);

    [Fact]
    public void ScansNestedFilesWithCanonicalPaths_InOrdinalOrder()
    {
        Directory.CreateDirectory(Path.Combine(root, "src", "deep"));
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        File.WriteAllText(Path.Combine(root, "src", "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "src", "deep", "c.txt"), "c");

        var scanned = WorkingFileScanner.Scan(root);

        Assert.Equal(["b.txt", "src/a.txt", "src/deep/c.txt"], scanned.Select(file => file.Path).ToArray());
        Assert.All(scanned, file => Assert.Equal(FileContentKind.Regular, file.Kind));
    }

    [Fact]
    public void SkipsTheStateDirectory()
    {
        Directory.CreateDirectory(Path.Combine(root, ".scn"));
        File.WriteAllText(Path.Combine(root, ".scn", "state.json"), "{}");
        File.WriteAllText(Path.Combine(root, "real.txt"), "content");

        var scanned = WorkingFileScanner.Scan(root);

        Assert.Equal(["real.txt"], scanned.Select(file => file.Path).ToArray());
    }

    [Fact]
    public void HashesContent()
    {
        File.WriteAllText(Path.Combine(root, "file.txt"), "hello scion");

        var scanned = WorkingFileScanner.Scan(root);

        var file = Assert.Single(scanned);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("hello scion"))).ToLowerInvariant(), file.Hash);
    }

    [Fact]
    public void ClassifiesFilesWithTheExecutableBit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "run.sh");
        File.WriteAllText(path, "#!/bin/sh");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var scanned = WorkingFileScanner.Scan(root);

        Assert.Equal(FileContentKind.Executable, Assert.Single(scanned).Kind);
    }

    [Fact]
    public void TreatsSymlinksAsContent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Directory.CreateDirectory(root);
        File.CreateSymbolicLink(Path.Combine(root, "link"), "/some/target");

        var scanned = WorkingFileScanner.Scan(root);

        var link = Assert.Single(scanned);
        Assert.Equal(FileContentKind.Symlink, link.Kind);
        Assert.Equal("/some/target", System.Text.Encoding.UTF8.GetString(link.Content));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
