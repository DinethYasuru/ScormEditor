using System.IO.Compression;
using System.Text;

namespace ScormEditor.Services;

/// <summary>
/// One SCORM package held in memory while it is being edited. Paths are package-relative,
/// forward-slashed and normalised (no "." or ".." segments). The editor circuit and the preview
/// endpoint both touch the same instance, so every member takes the lock.
/// </summary>
public sealed class ScormPackage
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".css", ".js", ".mjs", ".json", ".xml", ".xsd", ".dtd", ".txt", ".md", ".svg", ".vtt", ".csv", ".srt"
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _original = new(StringComparer.Ordinal);
    // Kept up to date on every write so rendering never compares file contents.
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private IReadOnlyList<string>? _sortedPaths;

    public Guid Id { get; } = Guid.NewGuid();
    public string FileName { get; }
    public string ManifestPath { get; private set; } = "";
    public ScormManifest? Manifest { get; private set; }

    /// <summary>Raised after any file is written, renamed or removed, with the affected path.</summary>
    public event Action<string>? Changed;

    /// <summary>A line in the change log. Step is the undo step it belongs to, or 0 for a change
    /// that can't be undone here (a Code tab edit, which the code editor's own undo covers).</summary>
    public sealed record LogEntry(DateTimeOffset Time, string Page, string What, int Step);

    private sealed record UndoStep(int Id, string Label, Dictionary<string, byte[]?> Before, Dictionary<string, byte[]?> After);

    // Undo keeps the files' earlier bytes (the same arrays, not copies), so a few steps cost little;
    // the oldest steps are dropped beyond these limits.
    private const int MaxUndoSteps = 30;
    private const long MaxUndoBytes = 200L * 1024 * 1024;
    private const string ChangeLogName = "scorm-editor-changes.txt";

    private readonly List<UndoStep> _undo = [];
    private readonly List<LogEntry> _log = [];
    private int _nextStep = 1;

    /// <summary>When the package was last read or written, for closing courses left open and unused.</summary>
    public DateTimeOffset LastUsed { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>The user's time zone offset, so change log times read as their local time.</summary>
    public TimeSpan TimeOffset { get; set; }

    /// <summary>Raised when the server closes the package because it was not used for a long time.</summary>
    public event Action? Expired;

    public void Touch() => LastUsed = DateTimeOffset.UtcNow;

    /// <summary>Editor pages showing the package, and when the last one closed (see ScormPackageStore).</summary>
    internal int Viewers { get; set; }
    internal DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The page in the preview and the file in the Code tab, so a reopened editor shows them again.</summary>
    public string? LastPreviewPath { get; set; }
    public string? LastCodePath { get; set; }

    internal void Expire() => Expired?.Invoke();

    public ScormPackage(string fileName, IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        FileName = fileName;
        foreach (var (path, data) in files)
        {
            _files[path] = data;
            _original[path] = data;
        }
        Reparse();
    }

    public static ScormPackage FromZip(string fileName, Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var files = new List<KeyValuePair<string, byte[]>>();
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.StartsWith("__MACOSX/") || entry.Name == ".DS_Store") continue;
            var path = Normalize(entry.FullName);
            if (path.Length == 0) continue;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            files.Add(new(path, ms.ToArray()));
        }
        return new ScormPackage(fileName, files);
    }

    public byte[] ToZip()
    {
        Touch();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            lock (_gate)
            {
                // What was edited, for whoever reviews the course. LMSs ignore files the manifest
                // doesn't list, so it doesn't change how the course runs.
                if (_log.Count > 0 && !_files.ContainsKey(ChangeLogName))
                {
                    using var log = new StreamWriter(zip.CreateEntry(ChangeLogName, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                    log.Write(ChangeLogTextUnlocked());
                }
                // The manifest goes first: some LMS importers only look at the first entries.
                foreach (var path in _files.Keys.OrderBy(p => p == ManifestPath ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal))
                {
                    var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                    using var s = entry.Open();
                    s.Write(_files[path]);
                }
            }
        }
        return ms.ToArray();
    }

    /// <summary>Sorted file paths. The same list instance is returned until a file is added, removed or renamed.</summary>
    public IReadOnlyList<string> Paths
    {
        get { lock (_gate) return _sortedPaths ??= _files.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList(); }
    }

    public int ChangeCount
    {
        get { lock (_gate) return _dirty.Count; }
    }

    public bool Exists(string path) { lock (_gate) return _files.ContainsKey(path); }

    public bool IsDirty(string path) { lock (_gate) return _dirty.Contains(path); }

    public bool HasOriginal(string path) { lock (_gate) return _original.ContainsKey(path); }

    // A path is dirty when it is new, deleted, or its bytes differ from the opened package.
    private void UpdateDirtyUnlocked(string path)
    {
        var dirty = !_original.TryGetValue(path, out var o) || !_files.TryGetValue(path, out var f)
            || !ReferenceEquals(o, f) && !o.AsSpan().SequenceEqual(f);
        if (!_files.ContainsKey(path) && !_original.ContainsKey(path)) dirty = false;
        if (dirty) _dirty.Add(path); else _dirty.Remove(path);
    }

    /// <summary>Finds a file by exact path, then case-insensitively (LMS servers on Windows ignore case).</summary>
    public string? Resolve(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            if (_files.ContainsKey(path)) return path;
            return _files.Keys.FirstOrDefault(k => string.Equals(k, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public byte[]? Read(string path) { Touch(); lock (_gate) return _files.TryGetValue(path, out var b) ? b : null; }

    public string ReadText(string path) => DecodeText(Read(path) ?? []);

    /// <summary>The file as it was in the opened package, or null for a file added since.</summary>
    public string? ReadOriginalText(string path)
    {
        byte[]? b;
        lock (_gate) b = _original.TryGetValue(path, out var o) ? o : null;
        return b is null ? null : DecodeText(b);
    }

    /// <summary>Paths that differ from the opened package: changed, added or deleted.</summary>
    public IReadOnlyList<string> ChangedPaths
    {
        get { lock (_gate) return _dirty.Order(StringComparer.Ordinal).ToList(); }
    }

    private static string DecodeText(byte[] b) => TextEncoding.Decode(b);

    public void Write(string path, byte[] data)
    {
        lock (_gate)
        {
            // Saving the same bytes again (an echo from the editor) changes nothing and notifies nobody.
            if (_files.TryGetValue(path, out var current) && current.AsSpan().SequenceEqual(data)) return;
            if (!_files.ContainsKey(path)) _sortedPaths = null;
            _files[path] = data;
            UpdateDirtyUnlocked(path);
        }
        Touch();
        if (path == ManifestPath) Reparse();
        Changed?.Invoke(path);
    }

    /// <summary>Writes text in the file's own encoding (and byte order mark), UTF-8 for a new file.</summary>
    public void WriteText(string path, string text) => Write(path, EncodeText(path, text));

    public byte[] EncodeText(string path, string text) => TextEncoding.EncodeLike(Read(path), text);

    public void Delete(string path)
    {
        lock (_gate)
        {
            _files.Remove(path);
            _sortedPaths = null;
            UpdateDirtyUnlocked(path);
        }
        if (path == ManifestPath) Reparse();
        Changed?.Invoke(path);
    }

    public void Rename(string from, string to)
    {
        lock (_gate)
        {
            if (!_files.Remove(from, out var data)) return;
            _files[to] = data;
            _sortedPaths = null;
            UpdateDirtyUnlocked(from);
            UpdateDirtyUnlocked(to);
        }
        if (from == ManifestPath || IsManifestName(to)) Reparse();
        Changed?.Invoke(to);
    }

    public void Revert(string path)
    {
        lock (_gate)
        {
            if (!_original.TryGetValue(path, out var o)) return;
            if (!_files.ContainsKey(path)) _sortedPaths = null;
            _files[path] = o;
            UpdateDirtyUnlocked(path);
        }
        if (path == ManifestPath) Reparse();
        Changed?.Invoke(path);
    }

    /* ---------- undo and change log ---------- */

    /// <summary>
    /// Runs a change as one undo step: the files in paths are remembered before and after write
    /// runs, and the log lines are added under the step. Returns the step's id, or 0 when write
    /// changed nothing (then there is nothing to undo or log).
    /// </summary>
    public int Step(string label, IEnumerable<string> paths, Action write, IEnumerable<(string Page, string What)> log)
    {
        var list = paths.Distinct().ToList();
        var before = Snapshot(list);
        write();
        var after = Snapshot(list);
        if (list.All(p => SameBytes(before[p], after[p]))) return 0;
        lock (_gate)
        {
            var step = new UndoStep(_nextStep++, label, before, after);
            _undo.Add(step);
            var now = DateTimeOffset.UtcNow;
            foreach (var (page, what) in log) _log.Add(new LogEntry(now, page, what, step.Id));
            // Drop the oldest steps beyond the limits; their log lines stay, but can no longer be undone.
            long bytes = _undo.Sum(u => u.Before.Values.Sum(b => (long)(b?.Length ?? 0)));
            while (_undo.Count > 1 && (_undo.Count > MaxUndoSteps || bytes > MaxUndoBytes))
            {
                bytes -= _undo[0].Before.Values.Sum(b => (long)(b?.Length ?? 0));
                _undo.RemoveAt(0);
            }
            return step.Id;
        }
    }

    private Dictionary<string, byte[]?> Snapshot(IEnumerable<string> paths)
    {
        lock (_gate) return paths.ToDictionary(p => p, p => _files.TryGetValue(p, out var b) ? b : null, StringComparer.Ordinal);
    }

    /// <summary>Adds a log line that has no undo step. A repeat of the last line only updates its time,
    /// so typing in the Code tab logs one line per file rather than one per save.</summary>
    public void LogChange(string page, string what)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_log.Count > 0 && _log[^1] is { Step: 0 } last && last.Page == page && last.What == what)
                _log[^1] = last with { Time = now };
            else
                _log.Add(new LogEntry(now, page, what, 0));
        }
    }

    public IReadOnlyList<LogEntry> ChangeLog { get { lock (_gate) return [.. _log]; } }

    /// <summary>What the next Undo puts back, or null when there is nothing to undo.</summary>
    public string? UndoLabel { get { lock (_gate) return _undo.Count == 0 ? null : _undo[^1].Label; } }

    /// <summary>
    /// Puts back the files of the last step. Refused when a file was changed again since (in the
    /// Code tab, say), because undoing would also throw that change away.
    /// </summary>
    public (string? Refused, IReadOnlyList<string> Paths) Undo()
    {
        UndoStep step;
        lock (_gate)
        {
            if (_undo.Count == 0) return ("There is nothing to undo.", []);
            step = _undo[^1];
            var changed = step.After.Where(kv => !SameBytes(kv.Value, _files.TryGetValue(kv.Key, out var b) ? b : null)).Select(kv => kv.Key).ToList();
            if (changed.Count > 0)
                return ($"{string.Join(", ", changed)} {(changed.Count == 1 ? "was" : "were")} changed again after “{step.Label}”, so it can't be undone without losing that later change.", []);
            _undo.RemoveAt(_undo.Count - 1);
            foreach (var (path, data) in step.Before)
            {
                if (data is null) _files.Remove(path); else _files[path] = data;
                UpdateDirtyUnlocked(path);
            }
            _sortedPaths = null;
            _log.RemoveAll(l => l.Step == step.Id);
        }
        if (step.Before.Keys.Any(IsManifestName) || step.Before.ContainsKey(ManifestPath)) Reparse();
        foreach (var path in step.Before.Keys) Changed?.Invoke(path);
        return (null, [.. step.Before.Keys]);
    }

    /// <summary>Forgets the undo steps and the change log (after starting over).</summary>
    public void ClearHistory()
    {
        lock (_gate) { _undo.Clear(); _log.Clear(); }
    }

    public string ChangeLogText() { lock (_gate) return ChangeLogTextUnlocked(); }

    private string ChangeLogTextUnlocked()
    {
        var sb = new StringBuilder();
        var offset = TimeOffset;
        var zone = offset == TimeSpan.Zero ? "UTC" : $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
        sb.AppendLine($"Changes made to {FileName} with the SCORM Editor");
        sb.AppendLine($"Times are {zone}.");
        sb.AppendLine();
        foreach (var e in _log)
            sb.AppendLine($"{e.Time.ToOffset(offset):yyyy-MM-dd HH:mm}  {e.Page}: {e.What}");
        return sb.ToString();
    }

    private static bool SameBytes(byte[]? a, byte[]? b) =>
        a is null || b is null ? a == b : ReferenceEquals(a, b) || a.AsSpan().SequenceEqual(b);

    public string UniquePath(string path)
    {
        if (!Exists(path)) return path;
        var ext = Path.GetExtension(path);
        var stem = path[..^ext.Length];
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}-{i}{ext}";
            if (!Exists(candidate)) return candidate;
        }
    }

    public void Reparse()
    {
        lock (_gate)
        {
            ManifestPath = _files.Keys.Where(IsManifestName).OrderBy(p => p.Count(c => c == '/')).FirstOrDefault() ?? "";
        }
        Manifest = ManifestPath.Length == 0 ? null : ScormManifest.Parse(this, ManifestPath);
    }

    /// <summary>
    /// Rewrites the &lt;file&gt; list of the launch resource so it names every package file.
    /// Returns the number of files listed, or null when there is no resource to update.
    /// </summary>
    public int? SyncManifestFileList()
    {
        if (Manifest is not { Error: null }) return null;
        var xml = ScormManifest.SyncFileList(this, Manifest);
        if (xml is null) return null;
        WriteText(ManifestPath, xml.Value.Xml);
        return xml.Value.Count;
    }

    public static bool IsText(string path) => TextExtensions.Contains(Path.GetExtension(path));

    public static bool IsHtml(string path) => Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".xhtml";

    public static string DirOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Replace('\\', '/').Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    /// <summary>Relative reference from a directory to a package path, e.g. ("a/b", "a/c/x.png") → "../c/x.png".</summary>
    public static string Relative(string fromDir, string to)
    {
        var a = fromDir.Length == 0 ? [] : fromDir.Split('/');
        var b = to.Split('/');
        var i = 0;
        while (i < a.Length && i < b.Length - 1 && a[i] == b[i]) i++;
        return string.Concat(Enumerable.Repeat("../", a.Length - i)) + string.Join('/', b[i..]);
    }

    private static bool IsManifestName(string p) =>
        p.Equals("imsmanifest.xml", StringComparison.OrdinalIgnoreCase) || p.EndsWith("/imsmanifest.xml", StringComparison.OrdinalIgnoreCase);

}
