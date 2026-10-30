namespace Scion.Core;

/// <summary>Who made a changeset. Core treats both fields as opaque display data.</summary>
public sealed record Author(string Name, string Email);
