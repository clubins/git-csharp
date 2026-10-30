using System.CommandLine;
using Scion.Cli.Cache;
using Scion.Cli.Local;
using Scion.Cli.Server;
using Scion.Cli.Working;

namespace Scion.Cli.Commands;

/// <summary>
/// <c>scn checkout</c>: materialize a changeset's tree into the working copy — the cold-start
/// story that replaces clone. With no changeset named, the local tip is used when there is one
/// and the server's newest changeset otherwise, so a freshly-initialized repository fills itself
/// with one request per object and no negotiation. With no path patterns, the whole tree is
/// materialized; named patterns materialize exactly their matches (the wanted-path half of the
/// fetch protocol).
/// </summary>
internal static class CheckoutCommand
{
    public static Command Build(CliContext context, Option<string?> workingDirectoryOption)
    {
        var changesetIdArgument = new Argument<string?>("changeset-id")
        {
            Description = "The changeset to materialize (default: the local tip, else the server's newest changeset)",
            Arity = ArgumentArity.ZeroOrOne, // Optional: the local tip, else the server's newest, is the target.
        };
        var pathOption = new Option<string[]?>("--path", "-p")
        {
            Description = "A glob pattern of paths to materialize; repeat for several (default: the whole tree)",
        };

        var command = new Command("checkout", "Materialize a changeset's tree into the working copy — no clone; content arrives by request.");
        command.Arguments.Add(changesetIdArgument);
        command.Options.Add(pathOption);

        command.SetAction(async (parseResult, cancellationToken) => await CliGuard.RunAsync(() => RunAsync(
            parseResult.GetValue(workingDirectoryOption) ?? Directory.GetCurrentDirectory(),
            parseResult.GetValue(changesetIdArgument),
            parseResult.GetValue(pathOption),
            context,
            cancellationToken), context, cancellationToken));

        return command;
    }

    public static async Task<int> RunAsync(
        string workingDirectory,
        string? changesetId,
        string[]? wantedPaths,
        CliContext context,
        CancellationToken cancellationToken)
    {
        var (root, config, state) = Repository.Open(workingDirectory);
        using var client = new ScionServerClient(
            ScionEnvironment.Server(config),
            ScionEnvironment.Token(config),
            context.HttpHandler);

        var target = changesetId
            ?? state.Tip
            ?? await client.NewestChangesetIdAsync(cancellationToken)
            ?? throw new ScionCliException(
                "Nothing to check out: no changeset was named, this repository has no tip yet, and the server's log is empty.");

        // Re-checkouts reuse the bounded LRU cache under .scn/cache — only misses cross the wire.
        var materializer = new WorkingCopyMaterializer(client, new LruBlobCache(LruBlobCache.DefaultDirectory(root)));
        var result = await materializer.MaterializeAsync(root, target, wantedPaths, cancellationToken);

        context.Out.WriteLine(
            $"checked out {result.ChangesetId} ({result.Paths} path(s) from tree {result.RootTree}; "
            + $"{result.BlobsFetched} blob(s) fetched, {result.BlobsCached} from cache)");
        return 0;
    }
}
