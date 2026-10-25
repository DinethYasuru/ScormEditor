using System.Collections.Concurrent;

namespace ScormEditor.Services;

/// <summary>Limits on the courses the server holds, from the "ScormEditor" configuration section.</summary>
public sealed class ScormEditorOptions
{
    /// <summary>Largest course .zip that can be opened, in MB.</summary>
    public int MaxUploadMB { get; set; } = 500;

    /// <summary>Most courses open at once across all users. Each one is held in memory.</summary>
    public int MaxOpenCourses { get; set; } = 20;

    /// <summary>A course nobody has used for this long is closed to free its memory.</summary>
    public double IdleHours { get; set; } = 8;

    /// <summary>
    /// How long a course is kept after its editor page closes (a refresh, a visit to another page
    /// of the app), so coming back to the editor in the same tab carries on with it.
    /// </summary>
    public double ReopenMinutes { get; set; } = 30;
}

/// <summary>
/// Packages open in editor sessions, keyed by id so the preview endpoint can serve their files.
/// Packages live only in memory. When the editor page closes, a package is kept for a short time
/// so a refresh or a trip to another page of the app can pick it up again; after that, and when
/// unused for longer than the idle limit (a tab left open overnight), it is removed so memory is
/// given back.
/// </summary>
public sealed class ScormPackageStore : IDisposable
{
    private readonly ConcurrentDictionary<Guid, ScormPackage> _packages = new();
    private readonly Timer _sweep;

    public ScormEditorOptions Options { get; }

    public ScormPackageStore(Microsoft.Extensions.Options.IOptions<ScormEditorOptions> options)
    {
        Options = options.Value;
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public int Count => _packages.Count;

    /// <summary>Adds a package, or returns false when the server already holds as many as allowed.</summary>
    public bool TryAdd(ScormPackage package)
    {
        if (_packages.Count >= Options.MaxOpenCourses)
        {
            Sweep();
            // Make room by dropping the course whose page closed longest ago.
            if (_packages.Count >= Options.MaxOpenCourses
                && _packages.Values.Where(p => p.Viewers == 0).OrderBy(p => p.ClosedAt).FirstOrDefault() is { } oldest)
                _packages.TryRemove(oldest.Id, out _);
            if (_packages.Count >= Options.MaxOpenCourses) return false;
        }
        _packages[package.Id] = package;
        return true;
    }

    public ScormPackage? Get(Guid id)
    {
        if (!_packages.TryGetValue(id, out var p)) return null;
        p.Touch();
        return p;
    }

    /// <summary>An editor page shows the package.</summary>
    public void Attach(ScormPackage package)
    {
        lock (package) { package.Viewers++; package.ClosedAt = null; }
    }

    /// <summary>An editor page no longer shows the package; it is kept for <see cref="ScormEditorOptions.ReopenMinutes"/>.</summary>
    public void Release(ScormPackage package)
    {
        lock (package)
        {
            package.Viewers = Math.Max(0, package.Viewers - 1);
            if (package.Viewers == 0) package.ClosedAt = DateTimeOffset.UtcNow;
        }
    }

    public void Remove(Guid id) => _packages.TryRemove(id, out _);

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        var idle = now - TimeSpan.FromHours(Options.IdleHours);
        var closed = now - TimeSpan.FromMinutes(Options.ReopenMinutes);
        foreach (var (id, p) in _packages)
        {
            var gone = p.Viewers == 0 && p.ClosedAt < closed;
            if ((!gone && p.LastUsed >= idle) || !_packages.TryRemove(id, out _)) continue;
            try { p.Expire(); } catch { /* the editor page may already be gone */ }
        }
    }

    public void Dispose() => _sweep.Dispose();
}
