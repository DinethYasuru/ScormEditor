using System.Collections.Concurrent;

namespace ScormEditor.Services;

/// <summary>
/// Packages open in editor sessions, keyed by id so the preview endpoint can serve their files.
/// Packages live only in memory and are removed when the editor page is closed.
/// </summary>
public sealed class ScormPackageStore
{
    private readonly ConcurrentDictionary<Guid, ScormPackage> _packages = new();

    public void Add(ScormPackage package) => _packages[package.Id] = package;

    public ScormPackage? Get(Guid id) => _packages.TryGetValue(id, out var p) ? p : null;

    public void Remove(Guid id) => _packages.TryRemove(id, out _);
}
