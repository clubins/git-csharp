namespace Scion.Server.Errors;

/// <summary>
/// Base class for failures the API maps to an HTTP response: the exception handler turns each
/// subtype into a stable ProblemDetails shape (spec C7) instead of an unstructured 500. Core and
/// storage types stay decoupled from HTTP — endpoint code translates store failures into these
/// API-level exceptions at the boundary.
/// </summary>
public abstract class ScionApiException(string errorCode, string message) : Exception(message)
{
    /// <summary>Stable machine-readable discriminator; clients switch on this, not on message text.</summary>
    public string ErrorCode { get; } = errorCode;

    /// <summary>The HTTP status code this failure maps to.</summary>
    public abstract int StatusCode { get; }
}

/// <summary>The request payload violates the API contract.</summary>
public sealed class ScionValidationException(string message)
    : ScionApiException("validation", message)
{
    public override int StatusCode => StatusCodes.Status400BadRequest;
}

/// <summary>A named object (changeset, blob, tree, ref) does not exist.</summary>
public sealed class ScionNotFoundException(string objectType, string objectId)
    : ScionApiException("not_found", $"{objectType} '{objectId}' does not exist.")
{
    public override int StatusCode => StatusCodes.Status404NotFound;

    /// <summary>The kind of object that was requested, e.g. "changeset".</summary>
    public string ObjectType { get; } = objectType;

    /// <summary>The requested object identifier as sent by the client.</summary>
    public string ObjectId { get; } = objectId;
}

/// <summary>
/// A compare-and-swap ref update lost the race: the ref moved between the client's read and its
/// write. The response carries the ref's current value so the client can rebase and retry
/// without a second round trip (spec C7).
/// </summary>
public sealed class RefConflictException(string refName, string currentTarget, long currentRevision)
    : ScionApiException(
        "ref_conflict",
        $"Ref '{refName}' changed on the server: it now targets '{currentTarget}' at revision {currentRevision}. Re-read the ref and retry.")
{
    public override int StatusCode => StatusCodes.Status409Conflict;

    /// <summary>The ref that moved, e.g. "main".</summary>
    public string RefName { get; } = refName;

    /// <summary>The changeset the ref points at now — the current value a retry rebases on.</summary>
    public string CurrentTarget { get; } = currentTarget;

    /// <summary>The ref's current revision; the next CAS attempt's expectedRevision after a re-read.</summary>
    public long CurrentRevision { get; } = currentRevision;
}
