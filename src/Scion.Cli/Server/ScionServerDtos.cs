using Scion.Core;

namespace Scion.Cli.Server;

// Wire shapes for the Scion server API — deliberately CLI-local records so the thin client
// depends on the wire contract (camelCase JSON, string enums, lowercase hex hashes), never on
// the server assembly. Each mirrors a server endpoint record exactly.

/// <summary>The create-changeset request the CLI ships: the canonical record plus its referenced blobs are uploaded separately, just before.</summary>
internal sealed record CreateChangesetPayload(
    string Id,
    IReadOnlyList<string>? Parents,
    IReadOnlyList<ChangeEntry>? Changes,
    string? Message,
    Author? Author,
    long TimestampUtc);

/// <summary>The create-changeset response: the stored changeset's canonical id.</summary>
internal sealed record CreatedChangeset(string Id);

/// <summary>The trees/missing response: the walked tree's hash, and which of its blob hashes the server store holds or lacks.</summary>
internal sealed record TreesMissing(string RootTree, IReadOnlyList<string> PresentBlobs, IReadOnlyList<string> MissingBlobs);
