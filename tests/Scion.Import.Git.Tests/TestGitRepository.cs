using System.Text;
using LibGit2Sharp;

namespace Scion.Import.Git.Tests;

/// <summary>
/// A disposable synthetic git repository with a fixed history, built with LibGit2Sharp itself.
/// Timestamps advance one minute per commit from a fixed base, so expectations are exact.
/// </summary>
internal sealed class TestGitRepository : IDisposable
{
    public const string IdentityName = "Ada Lovelace";
    public const string IdentityEmail = "ada@scion.test";
    public const string BinaryPath = "assets/logo.bin";

    private static readonly DateTimeOffset BaseTime = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    // Deterministic bytes with NULs throughout — unambiguously binary content.
    public static readonly byte[] BinaryContent = MakeBinaryContent();

    private readonly Repository _repository;
    private DateTimeOffset _currentTime = BaseTime;

    public TestGitRepository()
    {
        FullPath = System.IO.Path.Combine(Path.GetTempPath(), "scion-import-tests", Guid.NewGuid().ToString("N"));
        _ = Repository.Init(FullPath);
        _repository = new Repository(FullPath);
        _repository.Config.Set("user.name", IdentityName);
        _repository.Config.Set("user.email", IdentityEmail);
        InitialBranch = _repository.Head.FriendlyName;
    }

    public string FullPath { get; }

    public string InitialBranch { get; }

    public Repository Repo => _repository;

    public void WriteText(string path, string content)
    {
        WriteAndStage(path, Encoding.UTF8.GetBytes(content));
    }

    public void WriteBinary(string path, byte[] content)
    {
        WriteAndStage(path, content);
    }

    public void WriteExecutable(string path, string content)
    {
        WriteAndStage(path, Encoding.UTF8.GetBytes(content));
        // The executable bit only exists on POSIX; on other platforms the file imports as Regular.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                System.IO.Path.Combine(FullPath, path),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            Commands.Stage(_repository, path);
        }
    }

    public void WriteSymlink(string path, string target)
    {
        var fullPath = System.IO.Path.Combine(FullPath, path);
        _ = File.CreateSymbolicLink(fullPath, target);
        Commands.Stage(_repository, path);
    }

    public void Delete(string path)
    {
        File.Delete(System.IO.Path.Combine(FullPath, path));
        Commands.Stage(_repository, path);
    }

    /// <summary>Renames a staged file by moving it on disk and staging both sides — git's rename, verbatim.</summary>
    public void Move(string oldPath, string newPath)
    {
        File.Move(System.IO.Path.Combine(FullPath, oldPath), System.IO.Path.Combine(FullPath, newPath));
        Commands.Stage(_repository, oldPath);
        Commands.Stage(_repository, newPath);
    }

    public Commit Commit(string message)
    {
        var signature = new Signature(IdentityName, IdentityEmail, _currentTime);
        _currentTime += TimeSpan.FromMinutes(1);
        return _repository.Commit(message, signature, signature);
    }

    public void CreateBranchAndCheckout(string branchName)
    {
        _ = _repository.CreateBranch(branchName);
        Checkout(branchName);
    }

    public void Checkout(string branchName)
    {
        Commands.Checkout(_repository, branchName);
    }

    /// <summary>Merges <paramref name="branchName"/> into the current branch, forcing a real merge commit.</summary>
    public void MergeNoFastForward(string branchName)
    {
        var merger = new Signature(IdentityName, IdentityEmail, _currentTime);
        _currentTime += TimeSpan.FromMinutes(1);
        var result = _repository.Merge(_repository.Branches[branchName], merger, new MergeOptions { FastForwardStrategy = FastForwardStrategy.NoFastForward });
        if (result.Commit is null)
        {
            throw new InvalidOperationException($"The synthetic merge of '{branchName}' created no merge commit (status: {result.Status}); the test fixture is invalid.");
        }
    }

    public void Dispose()
    {
        _repository.Dispose();
        Directory.Delete(FullPath, recursive: true);
    }

    private void WriteAndStage(string path, byte[] content)
    {
        var fullPath = System.IO.Path.Combine(FullPath, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        Commands.Stage(_repository, path);
    }

    private static byte[] MakeBinaryContent()
    {
        var content = new byte[512];
        for (var i = 0; i < content.Length; i++)
        {
            content[i] = i % 9 == 0 ? (byte)0 : (byte)(i * 7 % 251 + 1);
        }

        return content;
    }
}
