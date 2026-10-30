namespace Scion.Core;

/// <summary>
/// Path-scope matching for the fetch protocol: the trees/missing endpoint's wanted-paths and
/// sparse-profile filters are glob patterns over a tree's path keys, and this is the one matcher
/// every filter shares.
/// </summary>
/// <remarks>
/// <para>
/// Patterns are rooted at the tree and compared case-sensitively in ordinal order. Path segments
/// are <c>/</c>-separated; <c>**</c> as a whole segment matches zero or more segments (so
/// <c>src/**</c> reaches everything under <c>src/</c>, and <c>a/**/b</c> matches both <c>a/b</c>
/// and <c>a/x/y/b</c>); <c>*</c> and <c>?</c> match within one segment and never cross a
/// <c>/</c>. A pattern containing no slash follows the gitignore convention — it matches the
/// final segment at any depth, so <c>*.cs</c> selects every C# file wherever it sits. Everything
/// else is literal.
/// </para>
/// <para>A null or empty pattern list is no restriction: the walk it scopes is unfiltered.</para>
/// </remarks>
public static class TreePathFilter
{
    /// <summary>
    /// True when <paramref name="path"/> is in scope: the list is unrestricted (null or empty) or
    /// at least one pattern matches.
    /// </summary>
    public static bool AnyMatch(IReadOnlyList<string>? patterns, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (patterns is null || patterns.Count == 0)
        {
            return true;
        }

        foreach (var pattern in patterns)
        {
            if (Matches(pattern, path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when one pattern matches <paramref name="path"/> under the type's rules.</summary>
    /// <exception cref="ArgumentException">The pattern is null, empty, or whitespace.</exception>
    public static bool Matches(string pattern, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(path);

        var patternSegments = pattern.Split('/');

        // Gitignore convention: a slashless pattern is matched against the final segment at any
        // depth. Computed as a plain substring — tree paths are always slash-separated, and
        // Path.GetFileName's platform separator rules have no business here.
        if (patternSegments.Length == 1)
        {
            return MatchSegment(pattern, path[(path.LastIndexOf('/') + 1)..]);
        }

        return MatchSegments(patternSegments, 0, path.Split('/'), 0);
    }

    /// <summary>
    /// Left-to-right segment walk. <c>**</c> branches over zero or more path segments — the only
    /// backtracking in the matcher, and patterns are short enough that plain recursion beats
    /// memoizing it.
    /// </summary>
    private static bool MatchSegments(string[] pattern, int p, string[] path, int q)
    {
        while (true)
        {
            if (p == pattern.Length)
            {
                return q == path.Length;
            }

            if (pattern[p] == "**")
            {
                for (var skip = q; skip <= path.Length; skip++)
                {
                    if (MatchSegments(pattern, p + 1, path, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (q == path.Length || !MatchSegment(pattern[p], path[q]))
            {
                return false;
            }

            p++;
            q++;
        }
    }

    /// <summary>
    /// One-segment glob with star backtracking: <c>*</c> is any run of characters and <c>?</c> is
    /// one character, both confined to the segment they sit in.
    /// </summary>
    private static bool MatchSegment(string pattern, string value)
    {
        int p = 0, v = 0, starP = -1, starV = -1;
        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v]))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starV = v;
            }
            else if (starP >= 0)
            {
                v = ++starV;
                p = starP + 1;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
