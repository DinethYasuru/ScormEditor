using System.Text.RegularExpressions;
using Azure.Storage.Blobs;

namespace ScormEditor.Services;

/// <summary>
/// What a pasted SAS URL points at and what it allows, read from the URL itself (nothing is sent
/// to Azure). A container SAS (sr=c, or an account SAS with a container in the path) can browse,
/// open and save; a blob SAS (sr=b) reaches one file only.
/// </summary>
public sealed partial class AzureSas
{
    /// <summary>The permissions the editor uses, in the order Azure lists them, with what each is for.</summary>
    public static readonly (char Letter, string Name, string For)[] Needed =
    [
        ('r', "Read", "open courses, and copy the old file into .OLD before it is replaced"),
        ('c', "Create", "save a course as a new file, and create the .OLD copy"),
        ('w', "Write", "replace a course that is already there"),
        ('l', "List", "browse the folders and files in the container"),
    ];

    private static readonly Dictionary<char, string> Names = new()
    {
        ['r'] = "Read", ['a'] = "Add", ['c'] = "Create", ['w'] = "Write", ['d'] = "Delete", ['x'] = "Delete version",
        ['y'] = "Permanent delete", ['l'] = "List", ['t'] = "Tags", ['f'] = "Find", ['m'] = "Move", ['e'] = "Execute",
        ['o'] = "Ownership", ['p'] = "Permissions", ['i'] = "Immutability policy",
    };

    public required Uri Uri { get; init; }
    public required string Account { get; init; }
    public required string Container { get; init; }
    /// <summary>For a blob SAS, the blob; otherwise the folder the URL points into ("" for the container root).</summary>
    public required string Path { get; init; }
    public required bool IsBlob { get; init; }
    /// <summary>The sp letters, or null when a stored access policy sets them (they can't be read from the URL).</summary>
    public string? Permissions { get; init; }
    public DateTimeOffset? Expires { get; init; }
    public bool Https { get; init; }

    public bool Allows(char letter) => Permissions is null || Permissions.Contains(letter);
    public bool Expired => Expires is { } e && e <= DateTimeOffset.UtcNow;

    /// <summary>The container (with the SAS) for browsing and for files next to a blob.</summary>
    public Uri ContainerUri => new BlobUriBuilder(Uri) { BlobName = "" }.ToUri();

    /// <summary>Where it points, without the secret part: account/container/path.</summary>
    public string Display => $"{Account}/{Container}{(Path.Length > 0 ? "/" + Path.TrimEnd('/') : "")}";

    public string PermissionNames => Permissions is null
        ? "set by a stored access policy"
        : string.Join(", ", Permissions.Select(c => Names.GetValueOrDefault(c, c.ToString())));

    /// <summary>The needed permissions the SAS does not give.</summary>
    public IEnumerable<(char Letter, string Name, string For)> Missing =>
        Permissions is null ? [] : Needed.Where(n => !Permissions.Contains(n.Letter));

    public static AzureSas? TryParse(string? text, out string? error)
    {
        error = null;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) { error = "Paste a SAS URL."; return null; }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            error = "That isn't a URL. A SAS URL starts with https:// and ends with ?sv=…&sig=….";
            return null;
        }
        BlobUriBuilder b;
        try { b = new BlobUriBuilder(uri); }
        catch (Exception) { error = "That isn't an Azure Blob Storage URL."; return null; }
        if (!uri.Query.Contains("sig=", StringComparison.Ordinal))
        {
            error = "This URL has no SAS token (no sig=… part). Use Generate SAS in the Azure portal or Storage Explorer and copy the whole \"Blob SAS URL\".";
            return null;
        }
        if (string.IsNullOrEmpty(b.BlobContainerName))
        {
            error = "This SAS URL has no container in it. Add the container name after the account, for example https://account.blob.core.windows.net/courses?sv=….";
            return null;
        }
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        var resource = q.TryGetValue("sr", out var sr) ? sr.ToString() : null;
        var name = Uri.UnescapeDataString(b.BlobName ?? "");
        var isBlob = resource == "b" || resource == "bv" || resource == "bs"
                     || (resource is null && name.Length > 0 && !name.EndsWith('/'));
        if (!isBlob && name.Length > 0 && !name.EndsWith('/')) name += "/";
        DateTimeOffset? expires = q.TryGetValue("se", out var se) && DateTimeOffset.TryParse(se, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var e) ? e : null;
        return new AzureSas
        {
            Uri = uri,
            Account = b.AccountName ?? uri.Host,
            Container = b.BlobContainerName,
            Path = name,
            IsBlob = isBlob,
            Permissions = q.TryGetValue("sp", out var sp) ? sp.ToString() : null,
            Expires = expires,
            Https = uri.Scheme == "https",
        };
    }

    /// <summary>The URL with the signature hidden, for showing on screen.</summary>
    public static string Redact(string text) => SigPattern().Replace(text, "$1sig=•••");

    [GeneratedRegex(@"([?&])sig=[^&#\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex SigPattern();
}
