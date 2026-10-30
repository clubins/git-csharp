namespace Scion.Server.Endpoints;

/// <summary>The blob-upload response: the normalized (lowercase) address the content is stored under.</summary>
public sealed record BlobUploadResponse(string Hash);
