namespace ScormEditor.Services;

/// <summary>
/// Adds a version to the URL of the editor's own script and styles, taken from the file's last
/// change, so browsers load the new file after an update instead of a cached old one. An old
/// cached script with a new page is what makes the editor fail with a closed connection.
/// </summary>
public sealed class AssetVersion(IWebHostEnvironment env)
{
    public string Url(string path)
    {
        var file = env.WebRootFileProvider.GetFileInfo(path);
        return file.Exists ? $"{path}?v={file.LastModified.ToUnixTimeSeconds()}" : path;
    }
}
