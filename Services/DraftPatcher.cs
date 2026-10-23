namespace ScormEditor.Services;

/// <summary>
/// Turns an edit made in the preview into a change to the page's source file.
/// The browser sends two serialisations of the page: one taken just before the first edit
/// (baseline) and one now (edited). Both come from the same serializer, so their difference is
/// exactly what the user changed. Each changed region is then found in the original source and
/// replaced there, which keeps the rest of the file byte-for-byte as it was. When a change cannot
/// be located (text a script generated, or entities written differently) the whole page is saved
/// as the preview shows it, and the review says so.
/// </summary>
public static class DraftPatcher
{
    public sealed record Outcome(string Text, bool Mapped);

    // Medium context first; more context is tried when that is not unique, less when the source
    // is written differently right around the change.
    private static readonly int[] ContextSizes = [80, 24, 240, 8];

    // For one element's HTML, short contexts can match the same words somewhere else in the page
    // (its <title>, another element), so only contexts that reach into the element's markup are used.
    private static readonly int[] ElementContextSizes = [80, 24];

    public static Outcome Apply(string source, string baseline, string edited) => Apply(source, baseline, edited, ContextSizes);

    /// <summary>Patches the change made to one element, given its HTML before and after.</summary>
    public static Outcome ApplyElement(string source, string oldHtml, string newHtml) => Apply(source, oldHtml, newHtml, ElementContextSizes);

    private static Outcome Apply(string source, string baseline, string edited, int[] contextSizes)
    {
        if (baseline == edited) return new Outcome(source, true);

        var crlf = source.Contains("\r\n");
        var work = crlf ? source.Replace("\r\n", "\n") : source;
        var oldFull = baseline.Replace("\r\n", "\n");
        var newFull = edited.Replace("\r\n", "\n");
        var a = oldFull.Split('\n');
        var b = newFull.Split('\n');
        var edits = TextDiff.Edits(a, b);
        if (edits is null) return new Outcome(edited, false);
        var aStarts = LineStarts(a, oldFull.Length);
        var bStarts = LineStarts(b, newFull.Length);

        // Later edits first, so earlier positions in the source stay valid.
        for (var i = edits.Count - 1; i >= 0; i--)
        {
            var e = edits[i];
            // Character ranges of the changed lines, each including its line break.
            var o0 = aStarts[e.OldStart];
            var o1 = aStarts[e.OldStart + e.OldCount];
            var n0 = bStarts[e.NewStart];
            var n1 = bStarts[e.NewStart + e.NewCount];
            var oldBlock = oldFull[o0..o1];
            var newBlock = newFull[n0..n1];
            var before = oldFull[..o0];
            var after = oldFull[o1..];

            // Narrow the change to the characters that differ.
            var p = 0;
            while (p < oldBlock.Length && p < newBlock.Length && oldBlock[p] == newBlock[p]) p++;
            var s = 0;
            while (s < oldBlock.Length - p && s < newBlock.Length - p && oldBlock[^(s + 1)] == newBlock[^(s + 1)]) s++;
            var oldCore = oldBlock[p..^s];
            var newCore = newBlock[p..^s];
            var lead = before + oldBlock[..p];
            var trail = oldBlock[^s..] + after;

            var replaced = false;
            foreach (var size in contextSizes)
            {
                var left = lead.Length > size ? lead[^size..] : lead;
                var right = trail.Length > size ? trail[..size] : trail;
                var key = left + oldCore + right;
                if (key.Length < 4) continue;
                var at = work.IndexOf(key, StringComparison.Ordinal);
                if (at < 0 || work.IndexOf(key, at + 1, StringComparison.Ordinal) >= 0) continue;
                work = work[..(at + left.Length)] + newCore + work[(at + left.Length + oldCore.Length)..];
                replaced = true;
                break;
            }
            if (!replaced) return new Outcome(edited, false);
        }
        return new Outcome(crlf ? work.Replace("\n", "\r\n") : work, true);
    }

    // starts[i] is where line i begins; starts[lines.Length] is the end of the text.
    private static int[] LineStarts(string[] lines, int textLength)
    {
        var starts = new int[lines.Length + 1];
        for (var i = 0; i < lines.Length; i++) starts[i + 1] = Math.Min(textLength, starts[i] + lines[i].Length + 1);
        return starts;
    }
}
