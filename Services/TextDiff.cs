namespace ScormEditor.Services;

/// <summary>Line-based diff (Myers) used to review changes before they are saved.</summary>
public static class TextDiff
{
    public enum Kind { Same, Removed, Added }

    /// <summary>One line of a diff. Segments split the text so the changed part of a line can be highlighted.</summary>
    public sealed record Line(Kind Kind, int? OldNumber, int? NewNumber, IReadOnlyList<Segment> Segments);

    public sealed record Segment(string Text, bool Changed);

    public sealed record Hunk(IReadOnlyList<Line> Lines);

    public sealed record Result(IReadOnlyList<Hunk> Hunks, int Added, int Removed, bool TooLarge);

    /// <summary>A raw edit: lines [OldStart, OldStart + OldCount) of a become lines [NewStart, NewStart + NewCount) of b.</summary>
    public readonly record struct Edit(int OldStart, int OldCount, int NewStart, int NewCount);

    private const int MaxEditDistance = 4000;
    private const int LongLine = 300;

    public static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Returns the edits that turn a into b, or null when they differ too much to diff quickly.</summary>
    public static List<Edit>? Edits(string[] a, string[] b)
    {
        // Common head and tail are trimmed first; most edits touch a small part of a file.
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        var endA = a.Length;
        var endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }

        var n = endA - start;
        var m = endB - start;
        var edits = new List<Edit>();
        if (n == 0 && m == 0) return edits;
        if (n == 0 || m == 0) { edits.Add(new Edit(start, n, start, m)); return edits; }

        var max = n + m;
        if (max > 200_000) return null;
        var limit = Math.Min(max, MaxEditDistance);
        var v = new int[2 * limit + 2];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= limit && !found; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var idx = k + limit;
                var x = k == -d || (k != d && v[idx - 1] < v[idx + 1]) ? v[idx + 1] : v[idx - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[start + x] == b[start + y]) { x++; y++; }
                v[idx] = x;
                if (x >= n && y >= m) { found = true; break; }
            }
        }
        if (!found) return null;

        // Walk the trace back to recover the path, then group consecutive changes into edits.
        var ops = new List<(Kind kind, int ai, int bi)>();
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d > 0; d--)
        {
            var pv = trace[d];
            var k = cx - cy;
            var idx = k + limit;
            var prevK = k == -d || (k != d && pv[idx - 1] < pv[idx + 1]) ? k + 1 : k - 1;
            var prevX = pv[prevK + limit];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY) { cx--; cy--; ops.Add((Kind.Same, cx, cy)); }
            if (cx == prevX) { cy--; ops.Add((Kind.Added, cx, cy)); }
            else { cx--; ops.Add((Kind.Removed, cx, cy)); }
        }
        while (cx > 0 && cy > 0) { cx--; cy--; ops.Add((Kind.Same, cx, cy)); }
        ops.Reverse();

        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].kind == Kind.Same) { i++; continue; }
            var os = ops[i].ai;
            var ns = ops[i].bi;
            int oc = 0, nc = 0;
            while (i < ops.Count && ops[i].kind != Kind.Same)
            {
                if (ops[i].kind == Kind.Removed) oc++; else nc++;
                i++;
            }
            edits.Add(new Edit(start + os, oc, start + ns, nc));
        }
        return edits;
    }

    /// <summary>Builds a reviewable diff with a few lines of context around each change.</summary>
    public static Result Compare(string oldText, string newText, int context = 3)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        var edits = Edits(a, b);
        if (edits is null) return new Result([], b.Length, a.Length, true);

        var hunks = new List<Hunk>();
        int added = 0, removed = 0;
        var e = 0;
        while (e < edits.Count)
        {
            // Merge edits whose context would overlap into one hunk.
            var first = e;
            while (e + 1 < edits.Count && edits[e + 1].OldStart - (edits[e].OldStart + edits[e].OldCount) <= context * 2) e++;
            var last = e;
            e++;

            var lines = new List<Line>();
            var ctxStart = Math.Max(0, edits[first].OldStart - context);
            var offset = edits[first].NewStart - edits[first].OldStart;
            for (var ai = ctxStart; ai < edits[first].OldStart; ai++)
                lines.Add(Plain(Kind.Same, ai + 1, ai + offset + 1, a[ai]));

            for (var j = first; j <= last; j++)
            {
                var ed = edits[j];
                var pairs = Math.Min(ed.OldCount, ed.NewCount);
                for (var r = 0; r < ed.OldCount; r++)
                {
                    var oldLine = a[ed.OldStart + r];
                    lines.Add(r < pairs
                        ? Highlighted(Kind.Removed, ed.OldStart + r + 1, null, oldLine, b[ed.NewStart + r], isOld: true)
                        : Plain(Kind.Removed, ed.OldStart + r + 1, null, oldLine));
                }
                for (var r = 0; r < ed.NewCount; r++)
                {
                    var newLine = b[ed.NewStart + r];
                    lines.Add(r < pairs
                        ? Highlighted(Kind.Added, null, ed.NewStart + r + 1, newLine, a[ed.OldStart + r], isOld: false)
                        : Plain(Kind.Added, null, ed.NewStart + r + 1, newLine));
                }
                removed += ed.OldCount;
                added += ed.NewCount;

                var gapEnd = j < last ? edits[j + 1].OldStart : Math.Min(a.Length, ed.OldStart + ed.OldCount + context);
                var off = ed.NewStart + ed.NewCount - (ed.OldStart + ed.OldCount);
                for (var ai = ed.OldStart + ed.OldCount; ai < gapEnd; ai++)
                    lines.Add(Plain(Kind.Same, ai + 1, ai + off + 1, a[ai]));
            }
            hunks.Add(new Hunk(lines));
        }
        return new Result(hunks, added, removed, false);
    }

    private static Line Plain(Kind kind, int? oldNo, int? newNo, string text) =>
        new(kind, oldNo, newNo, [new Segment(Shorten(text, 0, 0), false)]);

    // Marks the part of a changed line that differs from its counterpart, and trims very long
    // (minified) lines down to the region around the change.
    private static Line Highlighted(Kind kind, int? oldNo, int? newNo, string text, string other, bool isOld)
    {
        var p = 0;
        while (p < text.Length && p < other.Length && text[p] == other[p]) p++;
        var s = 0;
        while (s < text.Length - p && s < other.Length - p && text[^(s + 1)] == other[^(s + 1)]) s++;
        var head = text[..p];
        var mid = text[p..^s];
        var tail = text[^s..];
        if (head.Length > 80) head = "…" + head[^80..];
        if (tail.Length > 80) tail = tail[..80] + "…";
        if (mid.Length > 600) mid = mid[..300] + " … " + mid[^300..];
        var segs = new List<Segment>();
        if (head.Length > 0) segs.Add(new Segment(head, false));
        if (mid.Length > 0) segs.Add(new Segment(mid, true));
        if (tail.Length > 0) segs.Add(new Segment(tail, false));
        if (segs.Count == 0) segs.Add(new Segment("", false));
        return new Line(kind, oldNo, newNo, segs);
    }

    private static string Shorten(string text, int _, int __) =>
        text.Length > LongLine ? text[..LongLine] + $" … ({text.Length - LongLine:N0} more characters)" : text;
}
