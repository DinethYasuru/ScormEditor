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

    public sealed record Edit(string Path, string OldHtml, string? NewHtml, string OldText, string? NewText,
        AttrChange[]? Attrs, bool Structural, bool Lost);

    /// <summary>A file the edits change. The display texts show encoded course data decoded and indented.</summary>
    public sealed record FileChange(string Path, string NewText, string OldDisplay, string NewDisplay, IReadOnlyList<string> Notes);

    public sealed record Problem(string Page, string What, string Why);

    public sealed record Result(IReadOnlyList<FileChange> Files, IReadOnlyList<Problem> Problems);

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex Base64Literal = new(@"([""'])([A-Za-z0-9+/]{200,}={0,2})\1", RegexOptions.None, RegexTimeout);
    private static readonly Regex HtmlNearby = new(@"<[a-zA-Z/][^<>]{0,200}>|&[a-zA-Z]+;|&#\d+;", RegexOptions.None, RegexTimeout);
    private static readonly string[] SearchExtensions = [".html", ".htm", ".xhtml", ".js", ".mjs", ".json", ".xml"];

    public static Result Apply(ScormPackage package, IEnumerable<Edit> edits)
    {
        var ws = new Workspace(package);
        var problems = new List<Problem>();
        foreach (var e in edits)
        {
            if (!package.Exists(e.Path)) continue;
            if (e.Lost || e.NewHtml is null)
            {
                problems.Add(new Problem(e.Path, Describe(e.OldText),
                    "The page's own script replaced this part of the page after you edited it, so the edit was lost."));
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
                    "This changes the page's structure, and the element is not written in the page's HTML file (a script builds it), so it can't be saved from the preview."));
                continue;
            }

            // 2. Text and attribute values anywhere in the package.
            if (e.NewText is not null && e.NewText != e.OldText)
            {
                var why = ReplaceText(ws, e.Path, e.OldText, e.NewText);
                if (why is not null) problems.Add(new Problem(e.Path, $"Text change in {Describe(e.OldText)}", why));
            }
            foreach (var a in e.Attrs ?? [])
            {
                if (a.OldValue is null || a.NewValue is null)
                {
                    problems.Add(new Problem(e.Path, $"{(a.OldValue is null ? "Adding" : "Removing")} the {a.Name} attribute",
                        "The element is not written in the page's HTML file (a script builds it), so only changes to existing text and values can be saved."));
                    continue;
                }
                var why = ReplaceValue(ws, e.Path, a.OldValue, a.NewValue);
                if (why is not null) problems.Add(new Problem(e.Path, $"{a.Name}: {Shorten(a.OldValue)} → {Shorten(a.NewValue)}", why));
            }
        }
        return new Result(ws.Changes(), problems);
    }

    /* ---------- text ---------- */

    private static string? ReplaceText(Workspace ws, string page, string oldText, string newText)
    {
        // Narrow the change to the characters that differ; the rest of the element's text is context.
        var p = 0;
        while (p < oldText.Length && p < newText.Length && oldText[p] == newText[p]) p++;
        var s = 0;
        while (s < oldText.Length - p && s < newText.Length - p && oldText[^(s + 1)] == newText[^(s + 1)]) s++;
        var oldCore = oldText[p..^s];
        var newCore = newText[p..^s];
        var left = oldText[..p].TrimStart();
        var right = oldText[^s..].TrimEnd();

        foreach (var size in new[] { int.MaxValue, 40, 16 })
        {
            var l = left.Length > size ? left[^size..] : left;
            var r = right.Length > size ? right[..size] : right;
            if ((l + oldCore + r).Trim().Length < 3) continue;
            // Where the context is the element's whole text, the match must not run into other words.
            var pattern = (l.Length == left.Length ? @"(?<![\p{L}\p{N}])" : "") + Flexible(l) + "(" + Flexible(oldCore) + ")" + Flexible(r)
                + (r.Length == right.Length ? @"(?![\p{L}\p{N}])" : "");
            var matches = ws.FindAll(new Regex(pattern, RegexOptions.None, RegexTimeout), page);
            if (matches.Count == 0) continue;
            if (matches.Count > 1 && !(size == int.MaxValue && (l + oldCore + r).Trim().Length >= 12 && Mirrored(matches)))
                return $"The text appears in {matches.Count} places in the package, so it is not clear which one to change. Edit it in the Code tab.";
            ws.Replace(matches, m => newCore, isText: true);
            if (matches.Count > 1) NoteMany(matches, "The text “" + Shorten(oldText.Trim()) + "”");
            return null;
        }
        return "This text was not found in the package's files. A script may build it from data the editor can't read; edit it in the Code tab.";
    }

    private static string? ReplaceValue(Workspace ws, string page, string oldValue, string newValue)
    {
        if (oldValue.Trim().Length < 4) return "The old value is too short to find safely. Edit it in the Code tab.";
        var pattern = @"(?<![\w/.\-])(" + Flexible(oldValue) + @")(?![\w/.\-])";
        var matches = ws.FindAll(new Regex(pattern, RegexOptions.None, RegexTimeout), page);
        if (matches.Count == 0) return "This value was not found in the package's files. Edit it in the Code tab.";
        if (matches.Count > 1 && (oldValue.Length < 12 || !Mirrored(matches)))
            return $"The value appears in {matches.Count} places, so it is not clear which one to change. Edit it in the Code tab.";
        ws.Replace(matches, m => newValue, isText: false);
        if (matches.Count > 1) NoteMany(matches, "The value “" + Shorten(oldValue) + "”");
        return null;
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
    private static string Flexible(string text)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                while (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])) i++;
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
