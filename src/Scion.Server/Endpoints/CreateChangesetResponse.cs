using Scion.Core.Storage;

namespace Scion.Server.Endpoints;

/// <summary>
/// The create-changeset response: the stored changeset's canonical id and, when a ref move was
/// requested and won, the ref's new state. On a lost move there is no response body — the 409
/// problem details carry the ref's current value (spec C7) and the client, which computed the
/// changeset id itself, already knows what was stored.
/// </summary>
public sealed record CreateChangesetResponse(string Id, RefState? Ref);
