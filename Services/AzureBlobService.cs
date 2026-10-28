using System.Security.Cryptography;
using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace ScormEditor.Services;

/// <summary>
/// Reads and writes courses in Azure Blob Storage through SAS URLs. Nothing here deletes a blob.
/// SAS URLs are secrets: they are never logged, and error messages are built from the status
/// code only (the SDK's own messages can carry request details).
/// </summary>
public sealed class AzureBlobService
{
    public const string OldFolder = ".OLD";

    private static readonly BlobClientOptions ClientOptions = CreateOptions();

    public sealed record Entry(string Name, string FullName, bool IsFolder, long? Size, DateTimeOffset? Modified);

    public sealed record Opened(byte[] Data, ETag ETag);

    public sealed record SaveResult(bool Replaced, string? BackupName, ETag ETag);

    /// <summary>A save that did not go through, with a message for the user. The original file is untouched.</summary>
    public sealed class SaveException(string message, bool changedSinceOpened = false, bool exists = false) : Exception(message)
    {
        /// <summary>A file is at the destination and the user has not agreed to replace it.</summary>
        public bool Exists { get; } = exists;

        /// <summary>The file at the destination is not the one that was opened; the user can choose to replace it anyway.</summary>
        public bool ChangedSinceOpened { get; } = changedSinceOpened;
    }

    public static BlobContainerClient Container(AzureSas sas) => new(sas.ContainerUri, ClientOptions);

    public static BlobClient Blob(AzureSas sas, string? name = null) =>
        name is null && sas.IsBlob ? new BlobClient(sas.Uri, ClientOptions) : Container(sas).GetBlobClient(name ?? sas.Path);

    /// <summary>Folders and .zip files directly inside a folder of the container.</summary>
    public async Task<List<Entry>> ListAsync(AzureSas sas, string prefix, bool allFiles = false, CancellationToken ct = default)
    {
        var list = new List<Entry>();
        try
        {
            await foreach (var item in Container(sas).GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", prefix, ct))
            {
                if (item.IsPrefix)
                    list.Add(new(item.Prefix[prefix.Length..].TrimEnd('/'), item.Prefix, true, null, null));
                else if (allFiles || item.Blob.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    list.Add(new(item.Blob.Name[prefix.Length..], item.Blob.Name, false, item.Blob.Properties.ContentLength, item.Blob.Properties.LastModified));
                if (list.Count >= 5000) break;
            }
        }
        catch (RequestFailedException ex) { throw new SaveException(Describe(ex, "list the files")); }
        return list
            .OrderBy(e => e.IsFolder ? 0 : 1)
            .ThenBy(e => e.Name == OldFolder ? 1 : 0)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Checks a SAS by listing one file. Returns null when it works, or what went wrong.</summary>
    public async Task<string?> TestAsync(AzureSas sas, CancellationToken ct = default)
    {
        try
        {
            if (sas.IsBlob) { await Blob(sas).GetPropertiesAsync(cancellationToken: ct); return null; }
            await foreach (var _ in Container(sas).GetBlobsByHierarchyAsync(BlobTraits.None, BlobStates.None, "/", sas.Path, ct).AsPages(pageSizeHint: 1))
                break;
            return null;
        }
        catch (RequestFailedException ex) { return Describe(ex, sas.IsBlob ? "read the file" : "list the files"); }
        catch (Exception ex) when (ex is HttpRequestException or AggregateException or TaskCanceledException)
        {
            return "Could not reach the storage account. Check the URL and your network.";
        }
    }

    public async Task<BlobProperties?> PropertiesAsync(BlobClient blob, CancellationToken ct = default)
    {
        try { return (await blob.GetPropertiesAsync(cancellationToken: ct)).Value; }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
        catch (RequestFailedException ex) { throw new SaveException(Describe(ex, "check the file")); }
    }

    public async Task<Opened> DownloadAsync(BlobClient blob, long maxBytes, CancellationToken ct = default)
    {
        try
        {
            var props = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
            if (props.ContentLength > maxBytes)
                throw new SaveException($"{Path.GetFileName(blob.Name)} is {props.ContentLength / 1048576.0:0.#} MB. The largest course the editor can open is {maxBytes / 1048576} MB.");
            var content = await blob.DownloadContentAsync(new BlobDownloadOptions { Conditions = new() { IfMatch = props.ETag } }, ct);
            var data = content.Value.Content.ToArray();
            if (data.Length != props.ContentLength) throw new SaveException("The file did not download completely. Try again.");
            return new(data, props.ETag);
        }
        catch (RequestFailedException ex) { throw new SaveException(Describe(ex, "open the file")); }
    }

    /// <summary>
    /// Saves a course, keeping the file already there safe:
    /// 1. If a file is there and <paramref name="preserve"/> is set, it is copied into a .OLD folder
    ///    next to it under a new name (never over an older copy), and the copy is read back and
    ///    compared byte for byte before anything else happens.
    /// 2. The new file is uploaded only if the file there is still the one checked in step 1 (or
    ///    still absent), so nobody else's save in between is lost. A blob upload is all or nothing:
    ///    if it fails the old file stays as it was.
    /// 3. The saved file's size and MD5 are checked against what was sent.
    /// </summary>
    /// <param name="expected">The ETag of the file when it was opened; the save stops if it has changed since.</param>
    /// <param name="allowReplace">False when the user was told nothing is there: the save stops if a file has appeared since.</param>
    public async Task<SaveResult> SaveAsync(BlobClient target, BlobContainerClient? backupContainer, byte[] data, bool preserve,
        ETag? expected, bool allowReplace, string backupStamp, CancellationToken ct = default)
    {
        var name = target.Name;
        var file = Path.GetFileName(name);
        var current = await PropertiesAsync(target, ct);
        if (current is not null && !allowReplace)
            throw new SaveException($"{file} is already there.", exists: true);
        if (expected is { } want && current is not null && current.ETag != want)
            throw new SaveException($"{file} was changed in Azure after you opened it, perhaps by someone else. Saving now would replace their version.", changedSinceOpened: true);

        string? backupName = null;
        if (current is not null && preserve)
        {
            if (backupContainer is null)
                throw new SaveException("Keeping the old file needs a SAS for the whole container, not one for a single file. Add the container in Azure Blob on the left, or untick Keep the old file.");
            backupName = await BackupAsync(target, backupContainer, current, backupStamp, ct);
        }

        var md5 = MD5.HashData(data);
        try
        {
            var options = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip", ContentHash = md5 },
                Conditions = current is null ? new BlobRequestConditions { IfNoneMatch = ETag.All } : new BlobRequestConditions { IfMatch = current.ETag },
                TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5 },
            };
            await target.UploadAsync(new BinaryData(data), options, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            throw new SaveException($"{file} changed in Azure while saving, perhaps someone else saved it at the same time. Nothing was replaced{Kept(backupName)}. Try again.");
        }
        catch (RequestFailedException ex)
        {
            throw new SaveException(Describe(ex, current is null ? "create the file" : "replace the file") + $" {file} was not changed{Kept(backupName)}.");
        }

        var saved = await PropertiesAsync(target, ct)
            ?? throw new SaveException($"Azure did not keep {file} after the upload{Kept(backupName)}.");
        if (saved.ContentLength != data.Length || (saved.ContentHash is { Length: > 0 } h && !h.AsSpan().SequenceEqual(md5)))
            throw new SaveException($"{file} in Azure does not match what was sent. Save again{Kept(backupName)}.");
        return new(current is not null, backupName, saved.ETag);
    }

    private static string Kept(string? backupName) => backupName is null ? "" : $"; the old file is kept as {backupName}";

    private async Task<string> BackupAsync(BlobClient target, BlobContainerClient container, BlobProperties current, string stamp, CancellationToken ct)
    {
        var name = target.Name;
        var dir = name.Contains('/') ? name[..(name.LastIndexOf('/') + 1)] : "";
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);

        byte[] old;
        try
        {
            var content = await target.DownloadContentAsync(new BlobDownloadOptions { Conditions = new() { IfMatch = current.ETag } }, ct);
            old = content.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            throw new SaveException($"{Path.GetFileName(name)} changed in Azure while saving. Nothing was replaced. Try again.");
        }
        catch (RequestFailedException ex) { throw new SaveException(Describe(ex, "read the old file to keep a copy") + " Nothing was replaced."); }
        if (old.Length != current.ContentLength || (current.ContentHash is { Length: > 0 } h0 && !MD5.HashData(old).AsSpan().SequenceEqual(h0)))
            throw new SaveException("The old file did not download completely, so no copy could be kept. Nothing was replaced. Try again.");

        var md5 = MD5.HashData(old);
        for (var n = 1; n < 100; n++)
        {
            var backupName = $"{dir}{OldFolder}/{stem}_{stamp}{(n > 1 ? $"_{n}" : "")}{ext}";
            var backup = container.GetBlobClient(backupName);
            try
            {
                await backup.UploadAsync(new BinaryData(old), new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = current.ContentType ?? "application/zip", ContentHash = md5 },
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    Metadata = new Dictionary<string, string> { ["replacedfile"] = Uri.EscapeDataString(name) },
                    TransferValidation = new UploadTransferValidationOptions { ChecksumAlgorithm = StorageChecksumAlgorithm.MD5 },
                }, ct);
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412) { continue; }
            catch (RequestFailedException ex) { throw new SaveException(Describe(ex, $"create the copy in {OldFolder}") + " Nothing was replaced."); }

            // Read the copy back before touching the original.
            try
            {
                var check = (await backup.DownloadContentAsync(ct)).Value.Content.ToArray();
                if (!MD5.HashData(check).AsSpan().SequenceEqual(md5))
                    throw new SaveException($"The copy in {OldFolder} does not match the old file, so nothing was replaced. Try again.");
            }
            catch (RequestFailedException ex) { throw new SaveException(Describe(ex, $"check the copy in {OldFolder}") + " Nothing was replaced."); }
            return backupName;
        }
        throw new SaveException($"Too many copies in {OldFolder} were made this second. Nothing was replaced. Try again.");
    }

    /// <summary>A message for the user from a failed request. Never includes the URL or the SDK's message.</summary>
    public static string Describe(RequestFailedException ex, string doing) => (ex.Status, ex.ErrorCode) switch
    {
        (403, "AuthorizationPermissionMismatch" or "AuthorizationResourceTypeMismatch") =>
            $"The SAS doesn't allow the editor to {doing}. {PermissionHint(doing)}",
        (403, "AuthorizationFailure" or "AuthorizationSourceIPMismatch") =>
            $"Azure refused to {doing}. The SAS may be limited to other IP addresses, or the storage account's network settings block this server.",
        (403, _) => $"Azure refused to {doing}: the SAS has expired, was revoked, or was copied incompletely.",
        (404, "ContainerNotFound") => "The container in this SAS URL doesn't exist.",
        (404, _) => "The file is no longer there.",
        (409, "BlobArchived") or (409, "BlobBeingRehydrated") => "The file is in the Archive tier. Rehydrate it in Azure first.",
        _ => $"Azure could not {doing} (error {ex.Status}{(string.IsNullOrEmpty(ex.ErrorCode) ? "" : ", " + ex.ErrorCode)}).",
    };

    private static string PermissionHint(string doing) =>
        doing.StartsWith("list") ? "Give it the List permission."
        : doing.StartsWith("open") || doing.StartsWith("read") || doing.StartsWith("check") ? "Give it the Read permission."
        : doing.StartsWith("create") ? "Give it the Create and Write permissions."
        : "Give it the Write permission.";

    /// <summary>The name a replaced file gets in .OLD, from the user's local time.</summary>
    public static string Stamp(DateTimeOffset utcNow, TimeSpan offset) => utcNow.ToOffset(offset).ToString("yyyy-MM-dd_HH-mm-ss");

    private static BlobClientOptions CreateOptions()
    {
        var o = new BlobClientOptions();
        // SAS URLs are secrets; keep them out of logs and traces.
        o.Diagnostics.IsLoggingEnabled = false;
        o.Diagnostics.IsTelemetryEnabled = false;
        o.Diagnostics.IsDistributedTracingEnabled = false;
        o.Retry.NetworkTimeout = TimeSpan.FromMinutes(5);
        return o;
    }
}
