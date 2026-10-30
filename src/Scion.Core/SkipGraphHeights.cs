using System.Text;

namespace Scion.Core;

/// <summary>Height assignment for skip-graph nodes.</summary>
public static class SkipGraphHeights
{
    /// <summary>
    /// Deterministic geometric height for a changeset id: one plus the count of leading 1-bits in
    /// the SHA-256 digest of the id, capped at <paramref name="maxLevel"/>. A pure function of the
    /// id, so independent builds of the same DAG assign identical towers — content-addressed
    /// determinism, matching how the rest of the object model derives identity.
    /// </summary>
    public static int FromId(string id, int maxLevel)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLevel, 1);

        var height = 1;
        foreach (var character in Sha256.HashHex(Encoding.UTF8.GetBytes(id)))
        {
            var nibble = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                _ => throw new InvalidOperationException($"Unexpected hex character '{character}' in digest."),
            };

            for (var shift = 3; shift >= 0; shift--)
            {
                if (((nibble >> shift) & 1) == 0)
                {
                    return height;
                }

                if (height == maxLevel)
                {
                    return maxLevel;
                }

                height++;
            }
        }

        return height;
    }
}
