namespace Scion.Core;

/// <summary>
/// Myers' O(ND) difference algorithm with the linear-space divide-and-conquer refinement — the
/// variant git itself uses. Each region's middle snake is found by running the greedy scan
/// forward and backward until the paths overlap, then recursing on either side of it. Operates
/// on interned line ids so every comparison is integer equality.
/// </summary>
internal static class MyersDiff
{
    /// <summary>One alignment step. Indexes are 0-based; the side a step does not touch is -1.</summary>
    internal readonly record struct LineEdit(LineChangeKind Kind, int OldIndex, int NewIndex);

    /// <summary>
    /// Computes the complete alignment. Equal steps carry both indexes; deletions carry
    /// <paramref name="a"/>'s index; additions <paramref name="b"/>'s.
    /// </summary>
    public static List<LineEdit> Compute(int[] a, int[] b)
    {
        var path = FindPath(a, b, 0, 0, a.Length, b.Length);
        var edits = new List<LineEdit>();
        if (path is null)
        {
            return edits;
        }

        for (int i = 0; i + 1 < path.Count; i++)
        {
            Walk(a, b, path[i], path[i + 1], edits);
        }

        // A skeleton that failed to cover the graph would silently truncate the diff.
        int oldLines = 0, newLines = 0;
        foreach (var edit in edits)
        {
            if (edit.Kind != LineChangeKind.Added)
            {
                oldLines++;
            }

            if (edit.Kind != LineChangeKind.Deleted)
            {
                newLines++;
            }
        }

        if (oldLines != a.Length || newLines != b.Length)
        {
            throw new InvalidOperationException(
                $"Myers diff produced an incomplete alignment: {oldLines}/{a.Length} old and {newLines}/{b.Length} new lines accounted for.");
        }

        return edits;
    }

    /// <summary>
    /// Recursively splits the edit-graph box on its middle snake, collecting snake endpoints in
    /// path order. Recursion depth is O(log D): each half of the split carries at most half the
    /// region's edit distance.
    /// </summary>
    private static List<(int X, int Y)>? FindPath(int[] a, int[] b, int left, int top, int right, int bottom)
    {
        var snake = Midpoint(a, b, left, top, right, bottom);
        if (snake is null)
        {
            return null;
        }

        var (start, finish) = snake.Value;
        var head = FindPath(a, b, left, top, start.X, start.Y);
        var tail = FindPath(a, b, finish.X, finish.Y, right, bottom);
        // A found sub-path already ends at (and, for the tail, begins at) the snake endpoints.
        List<(int X, int Y)> result;
        if (head is null)
        {
            if (tail is null)
            {
                result = [start, finish];
            }
            else
            {
                tail.Insert(0, start);
                result = tail;
            }
        }
        else if (tail is null)
        {
            head.Add(finish);
            result = head;
        }
        else
        {
            head.AddRange(tail);
            result = head;
        }

        return result;
    }

    private static ((int X, int Y) Start, (int X, int Y) Finish)? Midpoint(
        int[] a, int[] b, int left, int top, int right, int bottom)
    {
        int width = right - left, height = bottom - top;
        if (width + height == 0)
        {
            return null;
        }

        int max = (width + height + 1) / 2;
        int delta = width - height;
        bool deltaOdd = (delta & 1) != 0;

        // Indexed by diagonal (forward: k, backward: relative diagonal c), offset by max+1.
        // Slot max+2 seeds each scan's first read — the classic v[1] seeding trick.
        var vf = new int[2 * max + 2];
        var vb = new int[2 * max + 2];
        vf[max + 2] = left;
        vb[max + 2] = bottom;

        for (int d = 0; d <= max; d++)
        {
            if (TryForward(a, b, left, top, right, bottom, vf, vb, delta, deltaOdd, d, max, out var snake)
                || TryBackward(a, b, left, top, right, bottom, vf, vb, delta, deltaOdd, d, max, out snake))
            {
                return snake;
            }
        }

        // The region's edit distance is at most width+height, so the scans must overlap by d=max.
        throw new InvalidOperationException(
            $"No middle snake found in a {width}x{height} region; the diff algorithm is inconsistent.");
    }

    private static bool TryForward(
        int[] a, int[] b, int left, int top, int right, int bottom,
        int[] vf, int[] vb, int delta, bool deltaOdd, int d, int max,
        out ((int X, int Y) Start, (int X, int Y) Finish) snake)
    {
        // Descending k, matching git: on overlap, prefer the uppermost diagonal.
        for (int k = d; k >= -d; k -= 2)
        {
            int c = k - delta;
            int x, px;
            if (k == -d || (k != d && vf[k - 1 + max + 1] < vf[k + 1 + max + 1]))
            {
                px = x = vf[k + 1 + max + 1];
            }
            else
            {
                px = vf[k - 1 + max + 1];
                x = px + 1;
            }

            int y = top + (x - left) - k;
            int py = (d == 0 || x != px) ? y : y - 1;
            while (x < right && y < bottom && a[x] == b[y])
            {
                x++;
                y++;
            }

            vf[k + max + 1] = x;

            // Backward has completed d-1 rounds: diagonals |c| <= d-1 hold valid values.
            if (deltaOdd && c >= 1 - d && c <= d - 1 && y >= vb[c + max + 1])
            {
                snake = ((px, py), (x, y));
                return true;
            }
        }

        snake = default;
        return false;
    }

    private static bool TryBackward(
        int[] a, int[] b, int left, int top, int right, int bottom,
        int[] vf, int[] vb, int delta, bool deltaOdd, int d, int max,
        out ((int X, int Y) Start, (int X, int Y) Finish) snake)
    {
        for (int c = d; c >= -d; c -= 2)
        {
            int k = c + delta;
            int y, py;
            if (c == -d || (c != d && vb[c - 1 + max + 1] > vb[c + 1 + max + 1]))
            {
                py = y = vb[c + 1 + max + 1];
            }
            else
            {
                py = vb[c - 1 + max + 1];
                y = py - 1;
            }

            int x = left + (y - top) + k;
            int px = (d == 0 || y != py) ? x : x + 1;
            while (x > left && y > top && a[x - 1] == b[y - 1])
            {
                x--;
                y--;
            }

            vb[c + max + 1] = y;

            // Forward has completed d rounds (it runs first): diagonals |k| <= d hold valid values.
            if (!deltaOdd && k >= -d && k <= d && x <= vf[k + max + 1])
            {
                snake = ((x, y), (px, py));
                return true;
            }
        }

        snake = default;
        return false;
    }

    /// <summary>
    /// Emits the steps between consecutive snake endpoints: the diagonal runs on either side of
    /// the single non-diagonal step. Whether the step comes first or last is decided by whether
    /// the lines at the boundary match.
    /// </summary>
    private static void Walk(int[] a, int[] b, (int X, int Y) from, (int X, int Y) to, List<LineEdit> edits)
    {
        int x = from.X, y = from.Y;
        while (x < to.X && y < to.Y && a[x] == b[y])
        {
            edits.Add(new LineEdit(LineChangeKind.Equal, x, y));
            x++;
            y++;
        }

        if (to.X - x > to.Y - y)
        {
            edits.Add(new LineEdit(LineChangeKind.Deleted, x, -1));
            x++;
        }
        else if (to.Y - y > to.X - x)
        {
            edits.Add(new LineEdit(LineChangeKind.Added, -1, y));
            y++;
        }

        while (x < to.X && y < to.Y && a[x] == b[y])
        {
            edits.Add(new LineEdit(LineChangeKind.Equal, x, y));
            x++;
            y++;
        }
    }
}
