using Azure;

namespace ScormEditor.Services;

/// <summary>
/// Where a course lives: a blob in Azure, or a file on the user's computer. It is kept with the
/// package in server memory only (the SAS URL in it is never sent to the browser).
/// </summary>
public sealed record CourseSource
{
    /// <summary>The file name, for a blob without its folders.</summary>
    public required string FileName { get; init; }

    /// <summary>For a blob: the SAS it is reached with (a container SAS, or a SAS for this one blob).</summary>
    public AzureSas? Sas { get; init; }
    /// <summary>For a blob: its full name in the container.</summary>
    public string? BlobName { get; init; }
    /// <summary>For a blob: its ETag when opened or last saved, so a save notices someone else's change.</summary>
    public ETag? ETag { get; init; }
    /// <summary>For a blob: the saved location's name, when it came through one.</summary>
    public string? ConnectionName { get; init; }

    /// <summary>For a file on the user's computer: the key of its handle in the browser (File System Access API).</summary>
    public string? LocalKey { get; init; }

    public bool IsAzure => Sas is not null;

    /// <summary>Where it is, for showing on screen.</summary>
    public string Display => Sas is { } s
        ? $"{ConnectionName ?? s.Account + "/" + s.Container} › {BlobName}"
        : $"This computer › {FileName}";
}
