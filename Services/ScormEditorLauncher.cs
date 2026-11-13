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

    /// <summary>
    /// A course waiting to be opened by the editor: a blob (Sas and BlobName), or a zip handed over
    /// in memory (Data, FileName and OnSave).
    /// </summary>
    public sealed record Request(AzureSas? Sas, string? BlobName, string? SourceName, string? ReturnUrl, string? ReturnLabel, DateTimeOffset Expires)
    {
        public byte[]? Data { get; init; }
        public string? FileName { get; init; }
        public Func<byte[], Task>? OnSave { get; init; }
    }

    private readonly ConcurrentDictionary<string, Request> _requests = new();

    /// <summary>
    /// Queues a course for the editor and returns its one-time code.
    /// </summary>
    /// <param name="sasUrl">
    /// A SAS URL with read and write permissions: for the container (or a folder in it), with
    /// <paramref name="blobName"/> the course's full path in the container, or for the course .zip
    /// blob itself, with <paramref name="blobName"/> left out. The editor only ever saves the course
    /// back over that blob: no .OLD copy, and no saving it to another place.
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

        return Add(new Request(sas, name, sourceName, returnUrl, returnLabel, DateTimeOffset.UtcNow + Lifetime));
    }

    /// <summary>
    /// Queues a course .zip handed over in memory and returns its one-time code. When the user saves,
    /// the editor calls <paramref name="onSave"/> with the whole edited .zip; where it goes is up to
    /// the caller. If <paramref name="onSave"/> throws, the user sees the error and can save again.
    /// </summary>
    /// <param name="zip">The course .zip.</param>
    /// <param name="fileName">Its file name, such as "intro.zip".</param>
    /// <param name="onSave">Stores the edited .zip. It runs on the server, in the editor's circuit.</param>
    /// <param name="sourceName">Shown in the editor as where the course came from, such as "Content Manager".</param>
    /// <param name="returnUrl">When set, the editor shows a Back button that goes here.</param>
    /// <param name="returnLabel">The Back button's text; "Back" when left out.</param>
    public string Create(byte[] zip, string fileName, Func<byte[], Task> onSave, string? sourceName = null, string? returnUrl = null, string? returnLabel = null)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(onSave);
        if (zip.Length == 0) throw new ArgumentException("The course .zip is empty.", nameof(zip));
        fileName = Path.GetFileName(fileName ?? "");
        if (fileName.Length == 0) fileName = "course.zip";
        return Add(new Request(null, null, sourceName, returnUrl, returnLabel, DateTimeOffset.UtcNow + Lifetime)
        {
            Data = zip, FileName = fileName, OnSave = onSave,
        });
    }

    /// <summary>Queues a course .zip handed over in memory (see <see cref="Create(byte[], string, Func{byte[], Task}, string?, string?, string?)"/>) and returns the editor URL that opens it.</summary>
    public string EditorUrl(byte[] zip, string fileName, Func<byte[], Task> onSave, string? sourceName = null, string? returnUrl = null, string? returnLabel = null) =>
        "editor?open=" + Create(zip, fileName, onSave, sourceName, returnUrl, returnLabel);

    private string Add(Request request)
    {
        Sweep();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _requests[code] = request;
        return code;
    }

    /// <summary>Queues a course in Azure Blob Storage (see <see cref="Create(string, string?, string?, string?, string?)"/>) and returns the editor URL that opens it.</summary>
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
