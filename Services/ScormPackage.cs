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
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            lock (_gate)
            {
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

    public byte[]? Read(string path) { lock (_gate) return _files.TryGetValue(path, out var b) ? b : null; }

    public string ReadText(string path)
    {
        var b = Read(path) ?? [];
        return Encoding.UTF8.GetString(b.AsSpan(HasBom(b) ? 3 : 0));
    }

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
        if (path == ManifestPath) Reparse();
        Changed?.Invoke(path);
    }

    public void WriteText(string path, string text)
    {
        // Keep a UTF-8 byte order mark if the original file had one.
        var hadBom = HasBom(Read(path) ?? []);
        var bytes = Encoding.UTF8.GetBytes(text);
        Write(path, hadBom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes);
    }

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

    private static bool HasBom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
}
