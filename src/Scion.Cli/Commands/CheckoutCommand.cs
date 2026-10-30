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
/// with one request per object and no negotiation.
/// </summary>
/// <remarks>
/// <para>
/// Scope is the fetch protocol's two filters: <c>--path</c> narrows one checkout to its matches;
/// <c>--sparse</c> sets the persisted sparse profile (the working copy's standing glob set) and
/// materializes exactly its matches. A persisted profile is sticky — every later checkout is
/// scoped to it until widened or cleared, the server intersecting it with the checkout's own
/// wanted paths. <c>--no-sparse</c> clears the profile: the upgrade path back to full
/// materialization.
/// </para>
/// <para>
/// The profile persists only once its materialization succeeded — profile, manifest, and tip
/// then describe the same completed event; a failed checkout leaves all three as they were.
/// Narrowing never deletes: paths that fall out of the profile stay on disk, where
/// <c>scn status</c> shows them as additions.
/// </para>
/// </remarks>
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
        var sparseOption = new Option<string[]?>("--sparse")
        {
            Description = "Set the persisted sparse profile to these glob patterns and materialize exactly their matches; repeat for several",
        };
        var noSparseOption = new Option<bool>("--no-sparse")
        {
            Description = "Clear the persisted sparse profile and materialize the whole tree",
        };

        var command = new Command("checkout", "Materialize a changeset's tree into the working copy — no clone; content arrives by request.");
        command.Arguments.Add(changesetIdArgument);
        command.Options.Add(pathOption);
        command.Options.Add(sparseOption);
        command.Options.Add(noSparseOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // An absent array option reads as an empty array, not null — GetResult is the only
            // way to tell "not given" (null) from "--sparse" alone (given, valueless: an error).
            var sparsePatterns = parseResult.GetResult(sparseOption) is null
                ? null
                : parseResult.GetValue(sparseOption);

            return await CliGuard.RunAsync(() => RunAsync(
                parseResult.GetValue(workingDirectoryOption) ?? Directory.GetCurrentDirectory(),
                parseResult.GetValue(changesetIdArgument),
                parseResult.GetValue(pathOption),
                sparsePatterns,
                parseResult.GetValue(noSparseOption),
                context,
                cancellationToken), context, cancellationToken);
        });

        return command;
    }

    public static async Task<int> RunAsync(
        string workingDirectory,
        string? changesetId,
        string[]? wantedPaths,
        string[]? sparsePatterns,
        bool noSparse,
        CliContext context,
        CancellationToken cancellationToken)
    {
        if (sparsePatterns is not null && noSparse)
        {
            throw new ScionCliException(
                "--sparse and --no-sparse are contradictory: one sets the sparse profile, the other clears it.");
        }

        ValidatePatterns(sparsePatterns);

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

        // The scope this checkout materializes within: the profile being set, nothing when it is
        // being cleared, else the persisted profile when there is one.
        IReadOnlyList<string>? effectiveProfile = sparsePatterns;
        if (sparsePatterns is null && !noSparse)
        {
            effectiveProfile = SparseProfileState.Load(root)?.Patterns is { Count: > 0 } patterns ? patterns : null;
        }

        // Re-checkouts reuse the bounded LRU cache under .scn/cache — only misses cross the wire.
        var materializer = new WorkingCopyMaterializer(client, new LruBlobCache(LruBlobCache.DefaultDirectory(root)));
        var result = await materializer.MaterializeAsync(root, target, wantedPaths, effectiveProfile, cancellationToken);

        // The profile persists only once its materialization succeeded, so the profile on disk
        // always pairs with a manifest that records exactly what that profile materialized.
        if (sparsePatterns is not null)
        {
            new SparseProfileState { Patterns = [.. sparsePatterns.Distinct(StringComparer.Ordinal)] }.Save(root);
        }
        else if (noSparse)
        {
            SparseProfileState.Delete(root);
        }

        var profileNote = sparsePatterns is not null
            ? $"; sparse profile set: {DescribeProfile(sparsePatterns)}"
            : noSparse
                ? "; sparse profile cleared"
                : effectiveProfile is not null
                    ? $"; sparse profile: {DescribeProfile(effectiveProfile)}"
                    : string.Empty;

        context.Out.WriteLine(
            $"checked out {result.ChangesetId} ({result.Paths} path(s) from tree {result.RootTree}; "
            + $"{result.BlobsFetched} blob(s) fetched, {result.BlobsCached} from cache){profileNote}");
        return 0;
    }

    /// <summary>The profile as the user gave it — the patterns, ordinal order, comma-joined.</summary>
    private static string DescribeProfile(IReadOnlyList<string> patterns) => string.Join(", ", patterns);

    /// <summary>
    /// The server rejects blank patterns with a 400; naming that here saves the round trip and
    /// keeps a bad profile from ever reaching disk.
    /// </summary>
    private static void ValidatePatterns(string[]? patterns)
    {
        if (patterns is null)
        {
            return;
        }

        if (patterns.Length == 0)
        {
            throw new ScionCliException("--sparse needs at least one glob pattern (clear the profile with --no-sparse).");
        }

        if (patterns.Any(string.IsNullOrWhiteSpace))
        {
            throw new ScionCliException("Sparse profile patterns must be non-empty globs.");
        }
    }
}
