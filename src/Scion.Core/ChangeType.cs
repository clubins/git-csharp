namespace Scion.Core;

/// <summary>What happened to a path, relative to the changeset's first parent.</summary>
public enum ChangeType
{
    Added = 0,
    Modified = 1,
    Deleted = 2,
    Renamed = 3,
}
