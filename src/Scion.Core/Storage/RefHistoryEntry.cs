namespace Scion.Core.Storage;

/// <summary>
/// One recorded move of a ref, including its creation. The history is the ref store's own
/// append-only ledger: every successful compare-and-swap appends exactly one entry, so the
/// sequence of targets a ref pointed at over time is queryable without replaying anything else.
/// </summary>
public sealed record RefHistoryEntry(string Name, string Target, long Revision, long TimestampUtc);
