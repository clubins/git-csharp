using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace Scion.Core.Derived;

/// <summary>
/// The "skip-graph" derived type: one changeset's tower of the skip lists over the commit DAG —
/// the structure behind log-depth DAG queries such as merge-base. A value holds the node's height
/// and its per-level frontier of nearest tall ancestors; the levels below the height are the
/// node's right-hand pointers (<see cref="SkipGraphNode.RightPointers"/>), and the levels at or
/// above the height are kept too, because <see cref="IDerivedData.Compute"/> receives only direct
/// parents' values — a taller child's upper levels must be answerable from its parents' stored
/// values alone. A level at or beyond a value's frontier length is empty by construction: the
/// frontier at level ℓ is nonempty exactly when some strict ancestor's height exceeds ℓ, and the
/// stored length is the largest level where that can hold.
/// </summary>
/// <remarks>
/// <para>
/// Deferred (<see cref="IDerivedData.MaterializeEagerly"/> = false): towers serve merge-base and
/// ancestry queries — work only some queries ever need — so appends pay nothing for them, and the
/// first query that needs a tower computes (and persists) it once. Contrast
/// <see cref="TreeSnapshotDerivedData"/>, whose values every diff and checkout touches and which
/// therefore materializes eagerly.
/// </para>
/// <para>
/// Heights come from <see cref="SkipGraphHeights.FromId"/> — the same deterministic assignment
/// <see cref="SkipGraph.Build"/> uses — so a derived tower and a built tower agree node for node,
/// and <see cref="DecodeNode"/> returns exactly what Build's node holds.
/// </para>
/// </remarks>
public sealed class SkipGraphDerivedData : IDerivedData
{
    /// <summary>The registry and storage key — the spec's name for this type.</summary>
    public const string TypeName = "skip-graph";

    private const byte FormatVersion = 0x01;

    /// <inheritdoc />
    public string Name => TypeName;

    /// <inheritdoc />
    public bool MaterializeEagerly => false;

    /// <inheritdoc />
    public DerivedDataValue Compute(Changeset changeset, ReadOnlySpan<DerivedDataValue> parentValues)
    {
        ArgumentNullException.ThrowIfNull(changeset);

        var height = SkipGraphHeights.FromId(changeset.Id, SkipGraph.MaxLevel);
        var parentTowers = new Tower[parentValues.Length];
        var frontierLength = height;
        for (var i = 0; i < parentValues.Length; i++)
        {
            parentTowers[i] = DecodeTower(parentValues[i].Payload.Span);
            frontierLength = Math.Max(frontierLength, parentTowers[i].Frontier.Length);
        }

        var frontier = ImmutableArray.CreateBuilder<ImmutableArray<string>>(frontierLength);
        for (var level = 0; level < frontierLength; level++)
        {
            frontier.Add(NearestTallAncestors(changeset, parentTowers, level));
        }

        return DerivedDataValue.FromPayload(EncodeTower(height, frontier.ToImmutable()));
    }

    /// <summary>
    /// Reads a stored value back into the skip-graph node it holds — id, height, and the
    /// right-hand pointers exactly as <see cref="SkipGraph.Build"/>'s node holds them.
    /// </summary>
    /// <exception cref="InvalidDataException">The payload is not a canonical skip-graph encoding.</exception>
    public static SkipGraphNode DecodeNode(DerivedDataValue value, string changesetId)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(changesetId);

        var tower = DecodeTower(value.Payload.Span);
        return new SkipGraphNode(changesetId, tower.Height, tower.Frontier[..tower.Height]);
    }

    /// <summary>
    /// Level-ℓ pointers over the parents: for each parent chain, the parent itself when it is
    /// taller than the level, otherwise the parent's own level-ℓ frontier. Chains are walked
    /// first-parent first and duplicates collapse on first sight — <see cref="SkipGraph.Build"/>'s
    /// per-level rule, one level at a time.
    /// </summary>
    private static ImmutableArray<string> NearestTallAncestors(Changeset changeset, Tower[] parentTowers, int level)
    {
        var targets = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var p = 0; p < parentTowers.Length; p++)
        {
            if (parentTowers[p].Height > level)
            {
                if (seen.Add(changeset.Parents[p]))
                {
                    targets.Add(changeset.Parents[p]);
                }

                continue;
            }

            if (level >= parentTowers[p].Frontier.Length)
            {
                continue; // Beyond the parent's stored frontier: provably empty there.
            }

            foreach (var inherited in parentTowers[p].Frontier[level])
            {
                if (seen.Add(inherited))
                {
                    targets.Add(inherited);
                }
            }
        }

        return targets.Count == 0 ? [] : targets.ToImmutable();
    }

    /// <summary>A decoded tower: the node's height and its frontier, one entry per stored level.</summary>
    private readonly record struct Tower(int Height, ImmutableArray<ImmutableArray<string>> Frontier);

    private static byte[] EncodeTower(int height, ImmutableArray<ImmutableArray<string>> frontier)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(FormatVersion);
        CanonicalFormat.WriteU32(buffer, (uint)height);
        CanonicalFormat.WriteU32(buffer, (uint)frontier.Length);
        foreach (var level in frontier)
        {
            CanonicalFormat.WriteU32(buffer, (uint)level.Length);
            foreach (var pointer in level)
            {
                CanonicalFormat.WriteString(buffer, pointer);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// v1 layout — integers big-endian, strings UTF-8 with unsigned 32-bit length prefixes: format
    /// version byte, height, frontier level count; then per level, its pointer count and pointers.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are truncated, trailing, or not a canonical
    /// skip-graph encoding (unknown version, or height/frontier lengths outside the shape a tower
    /// can hold).</exception>
    private static Tower DecodeTower(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);

        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unknown canonical skip-graph format version {version}; expected {FormatVersion}.");
        }

        var height = reader.ReadU32();
        if (height is < 1 or > SkipGraph.MaxLevel)
        {
            throw new InvalidDataException(
                $"Canonical skip-graph encoding holds height {height}; expected within [1, {SkipGraph.MaxLevel}].");
        }

        var frontierLength = reader.ReadU32();
        if (frontierLength > SkipGraph.MaxLevel || frontierLength < height)
        {
            throw new InvalidDataException(
                $"Canonical skip-graph encoding holds {frontierLength} frontier level(s) for height {height}; the frontier never truncates below the height and never exceeds {SkipGraph.MaxLevel} levels.");
        }

        var frontier = ImmutableArray.CreateBuilder<ImmutableArray<string>>((int)frontierLength);
        for (var level = 0u; level < frontierLength; level++)
        {
            var pointerCount = reader.ReadU32();
            var pointers = ImmutableArray.CreateBuilder<string>();
            for (var i = 0u; i < pointerCount; i++)
            {
                pointers.Add(reader.ReadString());
            }

            frontier.Add(pointers.Count == 0 ? [] : pointers.ToImmutable());
        }

        reader.EnsureEnd();
        return new Tower((int)height, frontier.ToImmutable());
    }

    /// <summary>Cursor over the canonical bytes; every read validates the remaining length.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _position;

        internal Reader(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _position = 0;
        }

        internal byte ReadByte()
        {
            Ensure(1);
            var value = _bytes[_position];
            _position++;
            return value;
        }

        internal uint ReadU32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadUInt32BigEndian(_bytes.Slice(_position, 4));
            _position += 4;
            return value;
        }

        internal string ReadString()
        {
            var length = ReadU32();
            Ensure(length);
            var value = Encoding.UTF8.GetString(_bytes.Slice(_position, (int)length));
            _position += (int)length;
            return value;
        }

        internal void EnsureEnd()
        {
            if (_position != _bytes.Length)
            {
                throw new InvalidDataException($"Canonical skip-graph encoding has {_bytes.Length - _position} trailing byte(s).");
            }
        }

        private void Ensure(uint count)
        {
            if (count > (uint)(_bytes.Length - _position))
            {
                throw new InvalidDataException($"Canonical skip-graph encoding truncated at offset {_position}: needed {count} more byte(s).");
            }
        }
    }
}
