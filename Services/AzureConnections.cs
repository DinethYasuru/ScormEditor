using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace ScormEditor.Services;

/// <summary>A saved Azure Blob location: a name the user chose and a container SAS URL.</summary>
public sealed class AzureConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string SasUrl { get; set; } = "";
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;

    private (string Url, AzureSas? Sas)? _parsed;

    [System.Text.Json.Serialization.JsonIgnore]
    public AzureSas? Sas
    {
        get
        {
            if (_parsed?.Url != SasUrl) _parsed = (SasUrl, AzureSas.TryParse(SasUrl, out _));
            return _parsed.Value.Sas;
        }
    }
}

/// <summary>
/// The user's saved Azure Blob locations, kept in their own browser and encrypted by the server
/// (ASP.NET Core Data Protection), so the SAS URLs are unreadable to anything that reads the
/// browser's storage, and are never sent back to the browser in plain text. Each user sees only
/// their own. One instance per editor tab (circuit).
/// </summary>
public sealed class AzureConnections(ProtectedLocalStorage storage)
{
    private const string Key = "scorm-editor.azure";
    private List<AzureConnection>? _list;

    /// <summary>Set when saved locations could not be read, for example after the server's keys changed.</summary>
    public string? LoadProblem { get; private set; }

    public event Action? Changed;

    /// <summary>A course picked on the Azure Blob page, for the editor to open when it loads.</summary>
    public (string ConnectionId, string BlobName)? OpenRequest { get; set; }

    /// <summary>Loads the list. Only call it after the first render: browser storage is not reachable before.</summary>
    public async Task<IReadOnlyList<AzureConnection>> GetAsync()
    {
        if (_list is not null) return _list;
        try
        {
            var result = await storage.GetAsync<List<AzureConnection>>(Key);
            _list = result.Success ? result.Value ?? [] : [];
            LoadProblem = null;
        }
        catch (CryptographicException)
        {
            _list = [];
            LoadProblem = "Your saved Azure locations could not be unlocked: the server's encryption keys changed (it was moved or reinstalled). Add them again.";
        }
        return _list;
    }

    public async Task SaveAsync(AzureConnection connection)
    {
        var list = (await GetAsync()).ToList();
        var at = list.FindIndex(c => c.Id == connection.Id);
        if (at >= 0) list[at] = connection; else list.Add(connection);
        await StoreAsync(list);
    }

    public async Task DeleteAsync(string id)
    {
        var list = (await GetAsync()).Where(c => c.Id != id).ToList();
        await StoreAsync(list);
    }

    private async Task StoreAsync(List<AzureConnection> list)
    {
        await storage.SetAsync(Key, list);
        _list = list;
        LoadProblem = null;
        Changed?.Invoke();
    }
}
