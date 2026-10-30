using System.Collections.Immutable;
using Scion.Core;

namespace Scion.Import.Git;

/// <summary>Maps one imported git commit to the Scion changeset it became. Order follows the import walk (oldest first).</summary>
public sealed record GitCommitMapping(string GitSha, string ChangesetId);

/// <summary>
/// Maps one git blob to the Scion hash of its content. Scion addresses content with SHA-256 over
/// the raw bytes, not git's SHA-1, so the two hash spaces are disjoint and both names are kept.
/// </summary>
public sealed record GitBlobMapping(string GitSha, string BlobHash);

/// <summary>
/// Everything one git import produced as Scion records. The import library is read-only over the
/// git repository and persists nothing — appending these records to the WAL and blob store is the
/// caller's job.
/// </summary>
/// <param name="Changesets">The first-parent lineage of HEAD, oldest first — the order a WAL expects (parents before children).</param>
/// <param name="Blobs">Every distinct blob content the import touched, deduplicated by hash, in first-seen order.</param>
/// <param name="CommitMappings">Git commit SHA to changeset id, index-aligned with <paramref name="Changesets"/>.</param>
/// <param name="BlobMappings">Git blob SHA to Scion blob hash, in first-seen order.</param>
public sealed record GitImportResult(
    ImmutableArray<Changeset> Changesets,
    ImmutableArray<Blob> Blobs,
    ImmutableArray<GitCommitMapping> CommitMappings,
    ImmutableArray<GitBlobMapping> BlobMappings);
