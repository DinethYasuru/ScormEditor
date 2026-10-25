using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScormEditor.Services;

/// <summary>
/// Turns edits made in the preview into changes to the package's files.
/// The browser sends one entry per edited element: its HTML and text just before the first edit
/// and now. Only those differences are written, so nothing a course's scripts added to the page
/// (status labels, classes, generated markup) can end up in a saved file.
///
/// Each edit is looked for in three places, in order:
/// 1. the page's own HTML (static pages): the changed characters are patched in place;
/// 2. the text or attribute value anywhere in the package, including JavaScript and JSON data
///    files (Storyline) and base64-encoded course data inside a page (Rise);
/// 3. otherwise it is reported as not found and nothing is written for it.
/// </summary>
public static class EditMapper
{
    public sealed record AttrChange(string Name, string? OldValue, string? NewValue);

    /// <summary>One edited element. Breaks are offsets in its old text where markup sits. Hide is a
    /// CSS selector to hide for learners; Unhide removes every element hidden that way.</summary>
    public sealed record Edit(string Path, string OldHtml, string? NewHtml, string OldText, string? NewText,
        AttrChange[]? Attrs, bool Structural, bool Lost, int[]? Breaks, string? Hide = null, bool Unhide = false);

    /// <summary>A file the edits change. The display texts show encoded course data decoded and indented.</summary>
    public sealed record FileChange(string Path, string NewText, string OldDisplay, string NewDisplay, IReadOnlyList<string> Notes);

    /// <summary>An edit that is left out (Edit is its index). Places is set when the text was found more
    /// than once, so the user can choose to change every copy (by passing Edit in changeAll).</summary>
    public sealed record Problem(string Page, string What, string Why, int Edit = -1, int Places = 0);

    public sealed record Result(IReadOnlyList<FileChange> Files, IReadOnlyList<Problem> Problems);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex Base64Literal = new(@"([""'])([A-Za-z0-9+/]{200,}={0,2})\1", RegexOptions.None, RegexTimeout);
    private static readonly Regex HtmlNearby = new(@"<[a-zA-Z/][^<>]{0,200}>|&[a-zA-Z]+;|&#\d+;", RegexOptions.None, RegexTimeout);
    private static readonly string[] SearchExtensions = [".html", ".htm", ".xhtml", ".js", ".mjs", ".json", ".xml"];

    /// <summary>Maps the edits to file changes. Edits whose index is in skip (unticked in the review) are left out.</summary>
    public static Result Apply(ScormPackage package, IReadOnlyList<Edit> edits, IReadOnlySet<int>? changeAll = null, IReadOnlySet<int>? skip = null)
    {
        var ws = new Workspace(package);
        var problems = new List<Problem>();
        for (var index = 0; index < edits.Count; index++)
        {
            if (skip?.Contains(index) == true) continue;
            var e = edits[index];
            var all = changeAll?.Contains(index) == true;
            if (e.Hide is not null || e.Unhide)
            {
                if (package.Exists(e.Path)) HideRule(ws.File(e.Path), e.Hide);
                continue;
            }
            if (!package.Exists(e.Path)) continue;
            if (e.Lost || e.NewHtml is null)
            {
                problems.Add(new Problem(e.Path, Describe(e.OldText),
                    "The page's own script replaced this part of the page after you edited it, so the edit was lost.", index));
                continue;
            }
            if (e.OldHtml == e.NewHtml) continue;

            // 1. The page's HTML, when the element is written in the file as the browser shows it.
            var page = ws.File(e.Path);
            var outcome = DraftPatcher.ApplyElement(page.Text, e.OldHtml, e.NewHtml);
            if (outcome.Mapped && outcome.Text != page.Text)
            {
                page.Text = outcome.Text;
                continue;
            }
            if (e.Structural)
            {
                problems.Add(new Problem(e.Path, Describe(e.OldText),
                    "This changes the page's structure, and the element is not written in the page's HTML file (a script builds it), so it can't be saved from the preview.", index));
                continue;
            }

            // 2. Text and attribute values anywhere in the package.
            if (e.NewText is not null && e.NewText != e.OldText)
            {
                var (why, places) = ReplaceText(ws, e.Path, e.OldText, e.NewText, e.Breaks ?? [], all);
                if (why is not null) problems.Add(new Problem(e.Path, $"Text change in {Describe(e.OldText)}", why, index, places));
            }
            foreach (var a in e.Attrs ?? [])
            {
                if (a.OldValue is null || a.NewValue is null)
                {
                    problems.Add(new Problem(e.Path, $"{(a.OldValue is null ? "Adding" : "Removing")} the {a.Name} attribute",
                        "The element is not written in the page's HTML file (a script builds it), so only changes to existing text and values can be saved.", index));
                    continue;
                }
                var (why, places) = ReplaceValue(ws, e.Path, a.OldValue, a.NewValue, all);
                if (why is not null) problems.Add(new Problem(e.Path, $"{a.Name}: {Shorten(a.OldValue)} → {Shorten(a.NewValue)}", why, index, places));
            }
        }
        return new Result(ws.Changes(), problems);
    }

    /// <summary>Whether an edit changes anything; elements typed into and then put back don't.</summary>
    public static bool Changes(Edit e) => e.Hide is not null || e.Unhide || e.Lost || e.NewHtml is null || e.NewHtml != e.OldHtml;

    /// <summary>A one-line description of an edit for the review's checklist and the change log.</summary>
    public static string Summarize(Edit e)
    {
        if (e.Unhide) return "Show hidden elements again";
        if (e.Hide is not null) return $"Hide for learners: {Shorten(e.Hide)}";
        if (e.Lost || e.NewHtml is null) return $"Edit to {Describe(e.OldText)} (lost when the page redrew it)";
        var parts = new List<string>();
        if (e.NewText is not null && e.NewText != e.OldText) parts.Add(TextChange(e.OldText, e.NewText));
        foreach (var a in e.Attrs ?? [])
            parts.Add($"{AttrLabel(a.Name)}: {(a.OldValue is null ? "(none)" : Shorten(a.OldValue))} → {(a.NewValue is null ? "(removed)" : Shorten(a.NewValue))}");
        if (parts.Count == 0) parts.Add($"Formatting of {Describe(e.OldText)}");
        return string.Join("; ", parts);
    }

    // “…the changed words…” → “…the new words…”, with a little of the text around the change.
    private static string TextChange(string oldText, string newText)
    {
        oldText = Regex.Replace(oldText, @"\s+", " ").Trim();
        newText = Regex.Replace(newText, @"\s+", " ").Trim();
        if (oldText.Length == 0) return $"Added text “{Shorten(newText)}”";
        if (newText.Length == 0) return $"Removed text “{Shorten(oldText)}”";
        var p = 0;
        while (p < oldText.Length && p < newText.Length && oldText[p] == newText[p]) p++;
        var s = 0;
        while (s < oldText.Length - p && s < newText.Length - p && oldText[^(s + 1)] == newText[^(s + 1)]) s++;
        const int Around = 20;
        string Window(string t)
        {
            var from = Math.Max(0, p - Around);
            var to = Math.Min(t.Length, t.Length - s + Around);
            var w = t[from..to];
            if (w.Length > 90) w = w[..87] + "…";
            return (from > 0 ? "…" : "") + w + (to < t.Length && !w.EndsWith('…') ? "…" : "");
        }
        return $"“{Window(oldText)}” → “{Window(newText)}”";
    }

    private static string AttrLabel(string name) => name switch
    {
        "href" => "Link",
        "src" or "data-se-orig-src" => "File",
        "alt" => "Image description",
        "title" => "Tooltip",
        "target" => "Opens in",
        _ => name,
    };

    /* ---------- find and replace ---------- */

    /// <summary>One place the searched text was found: the file, and the text around it as a reader sees it.</summary>
    public sealed record Hit(int Index, string Path, string Before, string Text, string After);

    /// <summary>
    /// Finds text the learner sees, in every page, data file and encoded course data of the package,
    /// and replaces it at each hit not in skip. Markup, scripts and data keys are never touched:
    /// in pages only text between tags counts, and in script data only string values.
    /// </summary>
    public static (Result Result, IReadOnlyList<Hit> Hits) FindReplace(ScormPackage package, string find, string replace,
        bool matchCase, bool wholeWord, IReadOnlySet<int>? skip = null, int max = 500)
    {
        var ws = new Workspace(package);
        if (find.Trim().Length == 0) return (new Result([], []), []);
        var pattern = (wholeWord ? @"(?<![\p{L}\p{N}_])" : "") + "(" + Flexible(find, 0, find.Length, []) + ")" + (wholeWord ? @"(?![\p{L}\p{N}_])" : "");
        var re = new Regex(pattern, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase, RegexTimeout);
        var hits = new List<Hit>();
        var chosen = new List<(Container, Match)>();
        foreach (var (where, m) in ws.FindAll(re, ""))
        {
            var g = m.Groups[1];
            if (ReaderText(where, g.Index) is not var (lo, hi)) continue;
            if (hits.Count >= max) break;
            var text = where.Text;
            hits.Add(new Hit(hits.Count, where.File.Path,
                Readable(text, Math.Max(lo, g.Index - 60), g.Index, true),
                Readable(text, g.Index, g.Index + g.Length, false),
                Readable(text, g.Index + g.Length, Math.Min(hi, g.Index + g.Length + 60), false)));
            if (skip?.Contains(hits.Count - 1) != true) chosen.Add((where, m));
        }
        if (chosen.Count > 0) ws.Replace(chosen, _ => replace, isText: true);
        return (new Result(ws.Changes(), []), hits);
    }

    public sealed record TermHit(int Term, string Path, string Before, string Text, string After);

    /// <summary>
    /// Finds every term of a word list in text learners see, the same places find and replace
    /// looks, and changes nothing. Longer terms win where terms overlap, so "Learning Journey" is
    /// not also counted as "Learning". Hits carry the index of the term in the list.
    /// </summary>
    public static IReadOnlyList<TermHit> ScanTerms(ScormPackage package, IReadOnlyList<string> terms, bool matchCase, bool wholeWord, int max = 5000)
    {
        var order = Enumerable.Range(0, terms.Count).Where(i => terms[i].Trim().Length > 0).OrderByDescending(i => terms[i].Length).ToList();
        if (order.Count == 0) return [];
        var alternatives = string.Join("|", order.Select(i => $"(?<t{i}>{Flexible(terms[i], 0, terms[i].Length, [])})"));
        var pattern = (wholeWord ? @"(?<![\p{L}\p{N}_])" : "") + "(?:" + alternatives + ")" + (wholeWord ? @"(?![\p{L}\p{N}_])" : "");
        var re = new Regex(pattern, matchCase ? RegexOptions.ExplicitCapture : RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase, RegexTimeout);
        var ws = new Workspace(package);
        var hits = new List<TermHit>();
        foreach (var (where, m) in ws.FindAll(re, ""))
        {
            if (ReaderText(where, m.Index) is not var (lo, hi)) continue;
            var term = order.First(i => m.Groups["t" + i].Success);
            var text = where.Text;
            hits.Add(new TermHit(term, where.File.Path,
                Readable(text, Math.Max(lo, m.Index - 50), m.Index, true),
                Readable(text, m.Index, m.Index + m.Length, false),
                Readable(text, m.Index + m.Length, Math.Min(hi, m.Index + m.Length + 50), false)));
            if (hits.Count >= max) break;
        }
        return hits;
    }

    // Text a learner reads: in a page, outside tags, scripts and styles; in script data, inside a
    // string that is not an object key, and outside any markup the string holds. Returns the
    // bounds of the text around it (the string, for script data), or null.
    private static (int Lo, int Hi)? ReaderText(Container c, int at)
    {
        var text = c.Text;
        if (c is WholeFile && c.File.IsHtml)
        {
            if (c.IsScript(at) || InsideTag(text, 0, at)) return null;
            var style = text.LastIndexOf("<style", at, StringComparison.OrdinalIgnoreCase);
            if (style >= 0 && text.LastIndexOf("</style", at, StringComparison.OrdinalIgnoreCase) < style) return null;
            return (0, text.Length);
        }
        if (StringAround(text, at) is not var (lo, hi)) return null;
        if (InsideTag(text, lo, at) || InsideEscapedTag(text, lo, at)) return null;
        if (!ReadsAsText(c, text.AsSpan(lo, hi - lo))) return null;
        return (lo, hi);
    }

    // The JSON string value holding position at: its contents' bounds, or null when at is not in a
    // string value (it's in code, a key, or anything else). Script files aren't parsed, so this
    // looks only at the string itself and the punctuation either side. A string written inside
    // another string (Storyline's data) has escaped quotes, \"like this\".
    private static (int Lo, int Hi)? StringAround(string text, int at) => StringAround(text, at, nested: false) ?? StringAround(text, at, nested: true);

    private static (int Lo, int Hi)? StringAround(string text, int at, bool nested)
    {
        // A quote that ends a string at this level: unescaped, or for a nested string escaped once (\").
        bool Ends(int i)
        {
            var n = 0;
            while (i - 1 - n >= 0 && text[i - 1 - n] == '\\') n++;
            return nested ? n == 1 : n % 2 == 0;
        }
        var open = at;
        do open = text.LastIndexOf('"', open - 1); while (open > 0 && !Ends(open));
        if (open <= 0 || !Ends(open)) return null;
        var close = at - 1;
        do close = text.IndexOf('"', close + 1); while (close >= 0 && !Ends(close));
        if (close < 0) return null;
        var lo = open + 1;
        var hi = nested ? close - 1 : close;
        if (hi < at || text.AsSpan(lo, hi - lo).IndexOfAny('\n', '\r') >= 0) return null;
        var before = (nested ? open - 1 : open) - 1;
        while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
        var after = close + 1;
        while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
        if (before < 0 || text[before] is not (':' or '[' or ',' or '(')) return null;
        if (after >= text.Length || text[after] is not (',' or '}' or ']' or ')')) return null;
        return (lo, hi);
    }

    private static readonly Regex Prose = new(@"\p{L}{2,}(?:\s|&nbsp;|\\u00[aA]0)+\p{L}|<\w|\\u003[cC]\w", RegexOptions.None, RegexTimeout);
    private static readonly Regex Label = new(@"^\p{Lu}[\p{L}\p{N}'’.,!?:()\- ]*$", RegexOptions.None, RegexTimeout);

    // Script strings also hold settings, names and code (type: "lesson", "Array"). Only strings
    // that read as text count: words with spaces between them, or markup. In JSON data (a .json
    // file or Rise course data), a capitalised label such as "Summary" counts too.
    private static bool ReadsAsText(Container c, ReadOnlySpan<char> value)
    {
        if (value.Contains("${", StringComparison.Ordinal)) return false;
        var s = value.ToString();
        if (Prose.IsMatch(s)) return true;
        var json = c is Blob || c.File.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        return json && Label.IsMatch(s);
    }

    private static bool InsideTag(string text, int from, int at)
    {
        if (at <= from) return false;
        var open = text.LastIndexOf('<', at - 1, at - from);
        return open >= 0 && text.LastIndexOf('>', at - 1, at - from) < open;
    }

    private static bool InsideEscapedTag(string text, int from, int at)
    {
        var region = text[from..at];
        var open = Math.Max(region.LastIndexOf("\\u003c", StringComparison.OrdinalIgnoreCase), -1);
        return open >= 0 && region.LastIndexOf("\\u003e", StringComparison.OrdinalIgnoreCase) < open;
    }

    // A bit of file text as a reader sees it: tags dropped, entities and escapes decoded.
    private static string Readable(string text, int from, int to, bool tail)
    {
        var t = text[from..to];
        t = Regex.Replace(t, @"<(script|style)\b.*?(?:</\1>|$)|^.*?</(?:script|style)>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase, RegexTimeout);
        t = Regex.Replace(t, @"\\u003[cC].*?\\u003[eE]|<[^<>]*>|^[^<>]*>|<[^<>]*$", " ");
        t = Regex.Replace(t, @"\\u([0-9a-fA-F]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        t = t.Replace("\\n", " ").Replace("\\\"", "\"").Replace("\\/", "/");
        t = System.Net.WebUtility.HtmlDecode(t);
        t = Regex.Replace(t, @"\s+", " ");
        return tail ? t.TrimStart() : t.TrimEnd();
    }

    /* ---------- hiding ---------- */

    private static readonly Regex HiddenBlock = new(@"<style id=""scorm-editor-hidden"">(.*?)</style>\s*", RegexOptions.Singleline, RegexTimeout);

    // Elements hidden for learners are listed in one style block in the page's <head>, so the rule
    // works for pages a script builds as well. A null selector removes the block.
    private static void HideRule(FileText file, string? selector)
    {
        var text = file.Text;
        var block = HiddenBlock.Match(text);
        if (selector is null)
        {
            if (block.Success) file.Text = text.Remove(block.Index, block.Length);
            file.Note("Shows every element hidden with the editor again.");
            return;
        }
        // A selector can't close the style element early.
        selector = selector.Replace("<", "\\3c ");
        var rule = $"\n  {selector} {{ display: none !important; }}";
        if (block.Success)
        {
            var at = block.Groups[1].Index + block.Groups[1].Length;
            file.Text = text[..at].TrimEnd() + rule + "\n" + text[at..];
        }
        else
        {
            var tag = $"<style id=\"scorm-editor-hidden\">{rule}\n</style>\n";
            var head = text.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            file.Text = head >= 0 ? text[..head] + tag + text[head..] : tag + text;
        }
        file.Note($"Hides “{Shorten(selector)}” for learners.");
    }

    /* ---------- text ---------- */

    private static (string? Why, int Places) ReplaceText(Workspace ws, string page, string oldText, string newText, int[] breaks, bool all)
    {
        // Narrow the change to the characters that differ; the rest of the element's text is context.
        var p = 0;
        while (p < oldText.Length && p < newText.Length && oldText[p] == newText[p]) p++;
        var s = 0;
        while (s < oldText.Length - p && s < newText.Length - p && oldText[^(s + 1)] == newText[^(s + 1)]) s++;
        var coreEnd = oldText.Length - s;
        var newCore = newText[p..(newText.Length - s)];
        if (breaks.Any(b => b > p && b < coreEnd))
            return ("The change runs across formatting (bold, a link or similar), which can't be matched in the course's data. Edit the parts separately, or use the Code tab.", 0);
        var textStart = oldText.Length - oldText.TrimStart().Length;
        var textEnd = oldText.TrimEnd().Length;
        var places = 0;

        foreach (var size in new[] { int.MaxValue, 40, 16 })
        {
            var from = size == int.MaxValue ? textStart : Math.Max(textStart, p - size);
            var to = size == int.MaxValue ? textEnd : Math.Min(textEnd, coreEnd + size);
            from = Math.Min(from, p);
            to = Math.Max(to, coreEnd);
            if (oldText[from..to].Trim().Length < 3) continue;
            // Where the context is the element's whole text, the match must not run into other words.
            var pattern = (from <= textStart ? @"(?<![\p{L}\p{N}])" : "")
                + Flexible(oldText, from, p, breaks) + "(" + Flexible(oldText, p, coreEnd, breaks) + ")" + Flexible(oldText, coreEnd, to, breaks)
                + (to >= textEnd ? @"(?![\p{L}\p{N}])" : "");
            var matches = ws.FindAll(new Regex(pattern, RegexOptions.None, RegexTimeout), page);
            if (matches.Count == 0) continue;
            if (matches.Count > 1 && !all && !(size == int.MaxValue && oldText[from..to].Trim().Length >= 12 && Mirrored(matches)))
            {
                places = matches.Count;
                break;
            }
            ws.Replace(matches, m => newCore, isText: true);
            if (matches.Count > 1) NoteMany(matches, "The text “" + Shorten(oldText.Trim()) + "”");
            return (null, 0);
        }
        return places > 0
            ? ($"The text appears in {places} places in the package, so it is not clear which one to change.", places)
            : ("This text was not found in the package's files. A script may build it from data the editor can't read; edit it in the Code tab.", 0);
    }

    private static (string? Why, int Places) ReplaceValue(Workspace ws, string page, string oldValue, string newValue, bool all)
    {
        if (oldValue.Trim().Length < 4) return ("The old value is too short to find safely. Edit it in the Code tab.", 0);
        var pattern = @"(?<![\w/.\-])(" + Flexible(oldValue, 0, oldValue.Length, []) + @")(?![\w/.\-])";
        var matches = ws.FindAll(new Regex(pattern, RegexOptions.None, RegexTimeout), page);
        if (matches.Count == 0) return ("This value was not found in the package's files. Edit it in the Code tab.", 0);
        if (matches.Count > 1 && !all && (oldValue.Length < 12 || !Mirrored(matches)))
            return ($"The value appears in {matches.Count} places, so it is not clear which one to change.", matches.Count);
        ws.Replace(matches, m => newValue, isText: false);
        if (matches.Count > 1) NoteMany(matches, "The value “" + Shorten(oldValue) + "”");
        return (null, 0);
    }

    // Several matches are copies of one value (a course title in the page's data, its <title> and
    // the manifest) when no file or data block holds it more than once. The manifest is the
    // exception: it repeats the title for the organization and its item.
    private static bool Mirrored(List<(Container Where, Match Match)> matches) =>
        matches.Where(m => !m.Where.File.Path.Equals("imsmanifest.xml", StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.Where).All(g => g.Count() == 1);

    private static void NoteMany(List<(Container Where, Match Match)> matches, string what)
    {
        foreach (var f in matches.Select(m => m.Where.File).Distinct())
            f.Note($"{what} was found in {matches.Count} places in the package; all of them are changed.");
    }

    // A pattern for text as it may be written in a file: in HTML (entities), in JavaScript or JSON
    // strings (backslash escapes), with any run of whitespace written in any of those ways.
    // Markup that may sit between parts of the text in the source: tags, written plainly or
    // escaped in a JavaScript string.
    private const string TagGap = @"(?:<[^<>]{0,300}>|\\u003[cC](?:(?!\\u003[eE]).){0,300}\\u003[eE])*";

    private static string Flexible(string text, int from, int to, int[] breaks)
    {
        var sb = new StringBuilder();
        for (var i = from; i < to; i++)
        {
            if (i > from && Array.IndexOf(breaks, i) >= 0) sb.Append(TagGap);
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                while (i + 1 < to && char.IsWhiteSpace(text[i + 1]) && Array.IndexOf(breaks, i + 1) < 0) i++;
                sb.Append(@"(?:\s|&nbsp;|&#160;|&#[xX][aA]0;|\\u00[aA]0|\\[ntr]| )+");
                continue;
            }
            sb.Append(c switch
            {
                '&' => @"(?:&amp;|&#38;|\\u0026|&)",
                '<' => @"(?:&lt;|&#60;|\\u003[cC]|<)",
                '>' => @"(?:&gt;|&#62;|\\u003[eE]|>)",
                '"' => @"(?:&quot;|&#34;|\\u0022|\\""|"")",
                '\'' => @"(?:&#39;|&#x27;|&apos;|\\u0027|\\'|')",
                '/' => @"(?:\\/|/)",
                _ when c > 127 => $@"(?:{Regex.Escape(c.ToString())}|\\u{HexPattern((int)c)}|&#{(int)c};|&#[xX]0*{HexPattern((int)c)};)",
                _ => Regex.Escape(c.ToString()),
            });
        }
        return sb.ToString();
    }

    private static string HexPattern(int code) =>
        string.Concat(code.ToString("x4").Select(h => char.IsLetter(h) ? $"[{h}{char.ToUpperInvariant(h)}]" : h.ToString()));

    private static string Describe(string text)
    {
        var t = Regex.Replace(text, @"\s+", " ").Trim();
        return t.Length == 0 ? "an element" : $"“{Shorten(t)}”";
    }

    private static string Shorten(string s) => s.Length > 60 ? s[..57] + "…" : s;

    /* ---------- files and encoded course data ---------- */

    private sealed class Workspace(ScormPackage package)
    {
        private readonly Dictionary<string, FileText> _files = new(StringComparer.Ordinal);
        private List<Container>? _all;

        public FileText File(string path)
        {
            if (!_files.TryGetValue(path, out var f))
                _files[path] = f = new FileText(path, package.ReadText(path));
            return f;
        }

        // The edited page first, then every other file that can hold course text.
        private IEnumerable<Container> Containers(string page)
        {
            _all ??= package.Paths
                .Where(p => SearchExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .SelectMany(p => File(p).Containers())
                .ToList();
            return _all.Where(c => c.File.Path == page).Concat(_all.Where(c => c.File.Path != page));
        }

        public List<(Container Where, Match Match)> FindAll(Regex re, string page)
        {
            var found = new List<(Container, Match)>();
            foreach (var c in Containers(page))
            {
                var text = c.Text;
                foreach (Match m in re.Matches(text))
                    if (!c.InsideBlob(m.Index)) found.Add((c, m));
            }
            return found;
        }

        public void Replace(List<(Container Where, Match Match)> matches, Func<Match, string> value, bool isText)
        {
            foreach (var group in matches.GroupBy(m => m.Where))
            {
                var c = group.Key;
                var text = c.Text;
                foreach (var (_, m) in group.OrderByDescending(x => x.Match.Index))
                {
                    var g = m.Groups[1];
                    text = text[..g.Index] + Encode(value(m), c, text, g.Index, isText) + text[(g.Index + g.Length)..];
                }
                c.Text = text;
            }
        }

        public List<FileChange> Changes() =>
            _files.Values.Where(f => f.Text != f.Original)
                .Select(f => new FileChange(f.Path, f.Text, f.Display(original: true), f.Display(original: false), f.Notes()))
                .ToList();
    }

    // Writes a new value the way the text around it is written: escaped for a JavaScript or JSON
    // string, and with HTML entities where the text is HTML.
    private static string Encode(string value, Container c, string text, int at, bool isText)
    {
        var json = c.IsScript(at);
        var near = text.Substring(Math.Max(0, at - 300), Math.Min(text.Length, at + 300) - Math.Max(0, at - 300));
        var html = !json || HtmlNearby.IsMatch(near);
        if (html)
        {
            value = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace(" ", "&nbsp;");
            if (!isText) value = value.Replace("\"", "&quot;");
        }
        if (json)
        {
            var sb = new StringBuilder();
            foreach (var ch in value)
                sb.Append(ch switch { '\\' => @"\\", '"' => "\\\"", '\n' => @"\n", '\r' => @"\r", '\t' => @"\t", _ when ch < ' ' => $"\\u{(int)ch:x4}", _ => ch.ToString() });
            value = sb.ToString();
        }
        return value;
    }

    private abstract class Container(FileText file)
    {
        public FileText File { get; } = file;
        public abstract string Text { get; set; }
        public abstract bool IsScript(int at);
        public virtual bool InsideBlob(int at) => false;
    }

    private sealed class FileText(string path, string text)
    {
        public string Path { get; } = path;
        public string Original { get; } = text;
        public string Text { get; set; } = text;
        public List<Blob> Blobs { get; } = [];
        private readonly HashSet<string> _notes = [];

        public IEnumerable<Container> Containers()
        {
            var whole = new WholeFile(this);
            yield return whole;
            foreach (Match m in Base64Literal.Matches(Text))
            {
                var decoded = TryDecode(m.Groups[2].Value);
                if (decoded is null) continue;
                var blob = new Blob(this, m.Groups[2].Value, decoded);
                Blobs.Add(blob);
                yield return blob;
            }
        }

        public bool IsHtml => System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".html" or ".htm" or ".xhtml" or ".xml";

        public void Note(string note) => _notes.Add(note);

        public IReadOnlyList<string> Notes() => [.. _notes];

        // The file as the review shows it: course data that is base64-encoded is shown decoded.
        public string Display(bool original)
        {
            var text = original ? Original : Text;
            foreach (var b in Blobs)
            {
                var encoded = original ? b.OriginalEncoded : b.Encoded;
                var decoded = original ? b.OriginalDecoded : b.Decoded;
                text = text.Replace(encoded, "\n/* encoded course data, shown decoded */\n" + Pretty(decoded) + "\n");
            }
            return text;
        }
    }

    private sealed class WholeFile(FileText file) : Container(file)
    {
        public override string Text { get => File.Text; set => File.Text = value; }

        // In a .js or .json file, or inside a <script> element of a page.
        public override bool IsScript(int at)
        {
            if (!File.IsHtml) return true;
            var open = File.Text.LastIndexOf("<script", at, StringComparison.OrdinalIgnoreCase);
            var close = File.Text.LastIndexOf("</script", at, StringComparison.OrdinalIgnoreCase);
            return open > close;
        }

        public override bool InsideBlob(int at) =>
            File.Blobs.Any(b => File.Text.IndexOf(b.Encoded, StringComparison.Ordinal) is var i && i >= 0 && at >= i && at < i + b.Encoded.Length);
    }

    // Course data stored base64-encoded in a string (Rise). Edits are made to the decoded text and
    // the string is re-encoded in the file.
    private sealed class Blob(FileText file, string encoded, string decoded) : Container(file)
    {
        public string OriginalEncoded { get; } = encoded;
        public string OriginalDecoded { get; } = decoded;
        public string Encoded { get; private set; } = encoded;
        public string Decoded { get; private set; } = decoded;

        public override string Text
        {
            get => Decoded;
            set
            {
                var next = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
                File.Text = File.Text.Replace(Encoded, next);
                Encoded = next;
                Decoded = value;
                File.Note("Changes the encoded course data in this file (shown decoded below).");
            }
        }

        public override bool IsScript(int at) => true;
    }

    private static string? TryDecode(string base64)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64));
            var start = text.TrimStart();
            return start.StartsWith('{') || start.StartsWith('[') ? text : null;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static string Pretty(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
