namespace Scion.Core.Storage;

/// <summary>
/// The current state of one ref: the changeset id it points at, and the revision that CAS moves
/// compare against. A ref is created by a compare-and-swap from revision
/// <see cref="RefStore.CreationRevision"/> ("does not exist") and every successful move advances
/// the revision by exactly one, so a caller that observed revision N can phrase its own move as
/// "apply only if still N" and lose cleanly — with the current value in hand — when another
/// writer got there first.
/// </summary>
public sealed record RefState(string Name, string Target, long Revision);
