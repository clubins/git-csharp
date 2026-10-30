using Scion.Core.Storage;

namespace Scion.Server.Endpoints;

/// <summary>
/// The merge response. Clean (201): <see cref="Id"/> names the appended merge changeset — first
/// parent ours, second theirs — and <see cref="Ref"/> is the ref state after the optional move.
/// Conflicted (409): <see cref="Id"/> is null, nothing was appended, and <see cref="Conflicts"/>
/// carries the vocabulary an agent resolves from — markers, kinds, both sides' blobs.
/// </summary>
internal sealed record MergeResponse(string MergeBase, string? Id, RefState? Ref, MergeConflictResponse[] Conflicts);
