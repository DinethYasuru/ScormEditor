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
}

/// <summary>
/// Packages open in editor sessions, keyed by id so the preview endpoint can serve their files.
/// Packages live only in memory. They are removed when the editor page is closed, and also when
/// unused for longer than the idle limit (a tab left open overnight), so memory is given back.
/// </summary>
public sealed class ScormPackageStore : IDisposable
{
    private readonly ConcurrentDictionary<Guid, ScormPackage> _packages = new();
    private readonly Timer _sweep;

    public ScormEditorOptions Options { get; }

    public ScormPackageStore(Microsoft.Extensions.Options.IOptions<ScormEditorOptions> options)
    {
        Options = options.Value;
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public int Count => _packages.Count;

    /// <summary>Adds a package, or returns false when the server already holds as many as allowed.</summary>
    public bool TryAdd(ScormPackage package)
    {
        if (_packages.Count >= Options.MaxOpenCourses)
        {
            Sweep();
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

    public void Remove(Guid id) => _packages.TryRemove(id, out _);

    private void Sweep()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(Options.IdleHours);
        foreach (var (id, p) in _packages)
        {
            if (p.LastUsed >= cutoff || !_packages.TryRemove(id, out _)) continue;
            try { p.Expire(); } catch { /* the editor page may already be gone */ }
        }
    }

    public void Dispose() => _sweep.Dispose();
}
