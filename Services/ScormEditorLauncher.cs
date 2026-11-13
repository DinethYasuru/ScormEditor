using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ScormEditor.Services;

/// <summary>
/// Lets another part of the app open a course from Azure Blob Storage in the editor, for example an
/// "Edit" button next to a course in a file list. The caller hands over the SAS URL on the server and
/// gets back a short one-time code; only that code goes in the editor's URL (editor?open=code), so the
/// SAS never shows in the address bar, browser history or server logs. Codes are single use and
/// expire after a few minutes. Works across tabs, so the editor can also be opened in a new tab.
/// </summary>
/// <example>
/// <code>
/// @inject ScormEditorLauncher Launcher
/// @inject NavigationManager Nav
/// ...
/// Nav.NavigateTo(Launcher.EditorUrl(containerSasUrl, "courses/intro.zip", returnUrl: "content"));
/// </code>
/// </example>
public sealed class ScormEditorLauncher
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>A course waiting to be opened by the editor.</summary>
    public sealed record Request(AzureSas Sas, string BlobName, string? SourceName, string? ReturnUrl, string? ReturnLabel, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Request> _requests = new();

    /// <summary>
    /// Queues a course for the editor and returns its one-time code.
    /// </summary>
    /// <param name="sasUrl">
    /// A SAS URL for the container (or a folder in it) with read, write, create, list and add
    /// permissions, in which case <paramref name="blobName"/> is the course's full path in the
    /// container. A container SAS lets "Keep old file" copy the old version into .OLD before saving.
    /// Or a SAS URL for the course .zip blob itself (read and write), with <paramref name="blobName"/>
    /// left out; the course can then be saved back, but without the .OLD copy.
    /// </param>
    /// <param name="blobName">The course .zip's path in the container, such as "courses/intro.zip".</param>
    /// <param name="sourceName">Shown in the editor as where the course came from, such as "IFS Academy".</param>
    /// <param name="returnUrl">When set, the editor shows a Back button that goes here, such as "content".</param>
    /// <param name="returnLabel">The Back button's text; "Back" when left out.</param>
    /// <exception cref="ArgumentException">The SAS URL can't be read, or no blob is named.</exception>
    public string Create(string sasUrl, string? blobName = null, string? sourceName = null, string? returnUrl = null, string? returnLabel = null)
    {
        var sas = AzureSas.TryParse(sasUrl, out var error) ?? throw new ArgumentException(error, nameof(sasUrl));
        var name = sas.IsBlob ? sas.Path : blobName?.TrimStart('/');
        if (string.IsNullOrEmpty(name) || name.EndsWith('/'))
            throw new ArgumentException("Name the course .zip: pass its path in the container as blobName, or use a SAS URL for the blob itself.", nameof(blobName));
        // A folder SAS points into the container; a name without that folder is taken as relative to it.
        if (!sas.IsBlob && sas.Path.Length > 0 && !name.StartsWith(sas.Path, StringComparison.Ordinal)) name = sas.Path + name;

        Sweep();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _requests[code] = new Request(sas, name, sourceName, returnUrl, returnLabel, DateTimeOffset.UtcNow + Lifetime);
        return code;
    }

    /// <summary>Queues a course (see <see cref="Create"/>) and returns the editor URL that opens it.</summary>
    public string EditorUrl(string sasUrl, string? blobName = null, string? sourceName = null, string? returnUrl = null, string? returnLabel = null) =>
        "editor?open=" + Create(sasUrl, blobName, sourceName, returnUrl, returnLabel);

    /// <summary>Takes the request for a code, once. Null when the code is unknown, used or expired.</summary>
    public Request? Take(string? code)
    {
        if (string.IsNullOrEmpty(code) || !_requests.TryRemove(code, out var request)) return null;
        return request.Expires > DateTimeOffset.UtcNow ? request : null;
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (code, request) in _requests)
            if (request.Expires <= now) _requests.TryRemove(code, out _);
    }
}
