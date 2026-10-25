using System.Text.RegularExpressions;

namespace ScormEditor.Services;

/// <summary>
/// The word list of the word check: words that should no longer be used and the words that
/// replace them. The editor only points these out; it never replaces them, because a word swapped
/// without reading the sentence can change what it means.
/// </summary>
public static class TermList
{
    public sealed record Term(string Old, string New);

    /// <summary>The IFS Academy terminology list, used until someone edits the list.</summary>
    public static readonly string Default = string.Join("\n",
        "Hands-on learning experiences\tHands-on experiences",
        "Educational Experience\tAcademy Experience",
        "educational experience\tacademy experience",
        "Learning Achievement\tCapability Status",
        "Competency Tracking\tProgress Tracking",
        "Learning Experience\tAcademy Experience",
        "learning experience\tacademy experience",
        "Certification Path\tCapability Credential Path",
        "In this Capability\tIn this session",
        "in this Capability\tin this session",
        "Learning Outcomes\tCapability Outcomes",
        "Digital Training\tDigital Content",
        "Knowledge Checks\tProgress Checks",
        "Skill Assessment\tSelf-evaluation",
        "Learning Journey\tProduct Capability Journey",
        "In this Course\tIn this session",
        "in this course\tin this session",
        "Learning Area\tTopic Modules",
        "Certification\tCapability Credential",
        "Prerequisites\tRecommended Prerequisites",
        "Curriculum\tContent Collections",
        "Equipping\tProviding with advice and guidance",
        "Learners\tSubscribers",
        "Learning\tExploring",
        "learning\texploring",
        "equipped\tprovided with advice and guidance",
        "Courses\tCapability",
        "courses\tcapability",
        "lessons\tsessions",
        "Course\tCapability",
        "course\tcapability",
        "Lesson\tSession",
        "lesson\tsession",
        "Badge\tAppreciation Status",
        "Learn\tDiscover",
        "learn\tdiscover",
        "Equip\tProvide with advice and guidance");

    private static readonly Regex Separator = new(@"\t+|\s+(?:->|=>|→)\s+");

    /// <summary>
    /// One term per line: the old word, then a Tab (or " -> ") and the new word. Blank lines, a
    /// header line ("Old word …") and repeats are skipped; with matchCase off, words that differ
    /// only in case count as repeats.
    /// </summary>
    public static IReadOnlyList<Term> Parse(string text, bool matchCase)
    {
        var seen = new HashSet<string>(matchCase ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var terms = new List<Term>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var parts = Separator.Split(line.Trim(), 2);
            var old = Tidy(parts[0]);
            var replacement = parts.Length > 1 ? Tidy(parts[1]) : "";
            if (old.Length == 0 || old.Equals("old word", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(old)) terms.Add(new Term(old, replacement));
        }
        return terms;
    }

    private static string Tidy(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
