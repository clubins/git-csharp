using System.CommandLine;
using Scion.Cli.Local;
using Scion.Cli.Server;
using Scion.Cli.Working;
using Scion.Core;

namespace Scion.Cli.Commands;

/// <summary>
/// <c>scn commit</c>: ship the working-copy changes as one changeset. Hashes the working files,
/// asks the server which blob hashes the tip's tree already holds (trees/missing — the "diff
/// against the remote tree" step), uploads only content the store demonstrably lacks (uploads are
/// idempotent regardless), and appends the changeset with first parent = the last-known tip. No
/// ref move, no packfile, no lock: the append is the whole write.
/// </summary>
internal static class CommitCommand
{
    private const string NoAuthorMessage =
        "No author configured. Set one with 'scn init --name <name> --email <email>', the "
        + $"{ScionEnvironment.AuthorNameVariable}/{ScionEnvironment.AuthorEmailVariable} environment variables, "
        + "or 'scn commit --name <name> --email <email>'.";

    public static Command Build(CliContext context, Option<string?> workingDirectoryOption)
    {
        var messageOption = new Option<string>("--message", "-m")
        {
            Description = "The commit message",
            Required = true,
        };
        var nameOption = new Option<string?>("--name")
        {
            Description = "The author name for this commit (wins over the environment and the config)",
        };
        var emailOption = new Option<string?>("--email")
        {
            Description = "The author email for this commit (wins over the environment and the config)",
        };

        var command = new Command("commit", "Commit the working-copy changes: upload changed blobs and append one changeset.")
        {
            messageOption,
            nameOption,
            emailOption,
        };

        command.SetAction(async (parseResult, cancellationToken) => await CliGuard.RunAsync(() => RunAsync(
            parseResult.GetValue(workingDirectoryOption) ?? Directory.GetCurrentDirectory(),
            parseResult.GetValue(messageOption)!, // Required = true: the parser refuses to invoke without it.
            parseResult.GetValue(nameOption),
            parseResult.GetValue(emailOption),
            context,
            cancellationToken), context, cancellationToken));

        return command;
    }

    public static async Task<int> RunAsync(
        string workingDirectory,
        string message,
        string? name,
        string? email,
        CliContext context,
        CancellationToken cancellationToken)
    {
        var (root, config, state) = Repository.Open(workingDirectory);
        var author = new Author(
            Resolve(name, ScionEnvironment.AuthorName(config), NoAuthorMessage),
            Resolve(email, ScionEnvironment.AuthorEmail(config), NoAuthorMessage));

        var workingFiles = WorkingFileScanner.Scan(root);
        var changes = WorkingChangeSet.Diff(state.Files, workingFiles);
        if (changes.Count == 0)
        {
            context.Out.WriteLine("no changes");
            return 0;
        }

        var contentByPath = workingFiles.ToDictionary(file => file.Path, StringComparer.Ordinal);
        using var client = new ScionServerClient(
            ScionEnvironment.Server(config),
            ScionEnvironment.Token(config),
            context.HttpHandler);

        // The trees/missing walk answers which blob hashes the tip's tree already holds; content
        // whose hash is present there never re-uploads. When there is no tip yet, every blob is new.
        HashSet<string> presentBlobs = state.Tip is { } tip
            ? (await client.TreesMissingAsync(tip, cancellationToken)).PresentBlobs.ToHashSet(StringComparer.Ordinal)
            : [];

        foreach (var (hash, content) in BlobsToUpload(changes, contentByPath, presentBlobs))
        {
            await client.UploadBlobAsync(hash, content, cancellationToken);
        }

        var changeset = Changeset.Create(
            state.Tip is { } parent ? [parent] : [],
            [.. changes.Select(ToChangeEntry)],
            message,
            author,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var id = await client.CreateChangesetAsync(ToPayload(changeset), cancellationToken);

        // After a successful append the manifest IS the working copy: the tip advances and every
        // scanned path's hash becomes the committed one.
        new WorkingState
        {
            Tip = id,
            Files = workingFiles.ToDictionary(file => file.Path, file => new WorkingState.Entry(file.Hash, file.Kind)),
        }.Save(root);

        context.Out.WriteLine($"committed {id} ({StatusCommand.Counts(changes)})");
        return 0;
    }

    /// <summary>Content to upload: each added/modified path's new blob, deduplicated by hash, skipping hashes the tip's tree already holds.</summary>
    private static IEnumerable<(string Hash, byte[] Content)> BlobsToUpload(
        IReadOnlyList<WorkingChange> changes,
        IReadOnlyDictionary<string, WorkingFile> contentByPath,
        IReadOnlySet<string> presentBlobs)
    {
        HashSet<string> uploaded = new(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (change.Type is ChangeType.Deleted or ChangeType.Renamed
                || change.NewHash is not { } hash
                || presentBlobs.Contains(hash)
                || !uploaded.Add(hash))
            {
                continue;
            }

            yield return (hash, contentByPath[change.Path].Content);
        }
    }

    private static ChangeEntry ToChangeEntry(WorkingChange change) =>
        new(change.Path, change.OldHash, change.NewHash, change.Kind, change.Type);

    private static CreateChangesetPayload ToPayload(Changeset changeset) => new(
        changeset.Id,
        [.. changeset.Parents],
        [.. changeset.Changes],
        changeset.Message,
        changeset.Author,
        changeset.TimestampUtc);

    private static string Resolve(string? explicitValue, string? fallback, string failure)
    {
        var resolved = ScionEnvironment.First(explicitValue, fallback);
        return resolved ?? throw new ScionCliException(failure);
    }
}
