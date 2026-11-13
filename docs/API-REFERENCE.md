# SCORM Editor: API reference for connecting another app

This page lists everything another app (the "host app", such as the Content Manager) calls or
configures to use the SCORM editor, with each method, parameter, return value and error, and
examples.

- How to install the editor into the host app: [`LLM-MERGE-PLAYBOOK.md`](LLM-MERGE-PLAYBOOK.md) steps 1-7.
- How it all works inside: [`SCORM-Editor-Guide.md`](SCORM-Editor-Guide.md) sections 6-7.

## Contents

1. [The idea in one picture](#1-the-idea-in-one-picture)
2. [`ScormEditorLauncher`: opening a course in the editor](#2-scormeditorlauncher-opening-a-course-in-the-editor)
   - [2.1 `EditorUrl` for a course in Azure Blob Storage](#21-editorurl-for-a-course-in-azure-blob-storage)
   - [2.2 `EditorUrl` for a course .zip your app already has](#22-editorurl-for-a-course-zip-your-app-already-has)
   - [2.3 `Create` (both versions)](#23-create-both-versions)
   - [2.4 `Lifetime`](#24-lifetime)
   - [2.5 `Take` (editor only)](#25-take-editor-only)
3. [The editor page: route and query string](#3-the-editor-page-route-and-query-string)
4. [Startup: methods to call in Program.cs](#4-startup-methods-to-call-in-programcs)
5. [Configuration: `ScormEditorOptions`](#5-configuration-scormeditoroptions)
6. [HTTP endpoints](#6-http-endpoints)
7. [Messages the user can see](#7-messages-the-user-can-see)
8. [Complete examples](#8-complete-examples)

---

## 1. The idea in one picture

```
Host app page                          ScormEditorLauncher                  Editor page (/editor)
─────────────                          ───────────────────                  ─────────────────────
user clicks Edit
  url = Launcher.EditorUrl(...)  ───►  keeps the course details for 5 min
                                  ◄───  returns "editor?open=9f2c…"
  Nav.NavigateTo(url)  ─────────────────────────────────────────────────►  opens the course
                                                                           user edits, presses
                                                                           Save course → Replace file
                                                                           ─► overwrites the blob
                                                                              or calls your onSave
                                                                           Back to courses ─► returnUrl
```

The host app needs exactly **one call**: `Launcher.EditorUrl(...)`, then navigate to the URL it
returns.

---

## 2. `ScormEditorLauncher`: opening a course in the editor

```csharp
namespace ScormEditor.Services;
public sealed class ScormEditorLauncher
```

- **Registered as:** singleton (`builder.Services.AddSingleton<ScormEditorLauncher>();`).
- **Use it from:** any Blazor component or server code in the host app:

```razor
@inject ScormEditorLauncher Launcher
@inject NavigationManager Nav
```

- **Thread-safe:** yes.
- **Where it runs:** on the server. Never send its inputs (SAS URLs, zips) to the browser.

### 2.1 `EditorUrl` for a course in Azure Blob Storage

```csharp
public string EditorUrl(
    string  sasUrl,
    string? blobName    = null,
    string? sourceName  = null,
    string? returnUrl   = null,
    string? returnLabel = null)
```

Makes a one-time code for the course and returns the editor URL that opens it.

| Parameter | Type | Required | What it is | Example |
|---|---|---|---|---|
| `sasUrl` | `string` | **yes** | A SAS URL with **read and write** permission (`sp` contains `r` and `w`). Either for the **container** (or a folder in it), or for **the course .zip blob** itself. | Container: `https://ifsacademy.blob.core.windows.net/courses?sv=2023-11-03&sr=c&sp=rw&se=2026-11-13T20:00:00Z&sig=…`<br>Blob: `https://ifsacademy.blob.core.windows.net/courses/finance/intro.zip?sv=2023-11-03&sr=b&sp=rw&se=…&sig=…` |
| `blobName` | `string?` | **yes with a container SAS**, ignored with a blob SAS | The course's full path inside the container. A leading `/` is removed. With a **folder** SAS, a name that doesn't start with that folder is taken as inside it. | `"finance/intro.zip"` |
| `sourceName` | `string?` | no | Shown in the editor as where the course came from: `<sourceName> › <blobName>`. When left out, the storage account and container are shown. | `"Content Manager"` → toolbar shows `Content Manager › finance/intro.zip` |
| `returnUrl` | `string?` | no | When set, the editor's toolbar shows a **Back** button linking here. Relative to the app's base URL, or absolute. | `"content"`, `"courses/42"`, `"/content?folder=finance"` |
| `returnLabel` | `string?` | no | Text of the Back button. Default: `"Back"`. | `"Back to courses"` |

**Returns:** `string`, the relative URL `"editor?open=<code>"`, where `<code>` is 32 lowercase hex
characters, for example `"editor?open=9f2c4a7e1b3d5f60718293a4b5c6d7e8"`. Pass it to
`NavigationManager.NavigateTo`, or use it as a link's `href`.

**Throws:** `ArgumentException` when:

| Situation | Message |
|---|---|
| `sasUrl` empty | `Paste a SAS URL.` |
| Not a URL | `That isn't a URL. A SAS URL starts with https:// and ends with ?sv=…&sig=….` |
| Not an Azure Blob URL | `That isn't an Azure Blob Storage URL.` |
| No `sig=` in it | `This URL has no SAS token (no sig=… part). …` |
| No container in it | `This SAS URL has no container in it. …` |
| Container SAS and no `blobName` (or `blobName` ends with `/`) | `Name the course .zip: pass its path in the container as blobName, or use a SAS URL for the blob itself.` |

The editor does **not** call Azure inside `EditorUrl`; a wrong permission or expired SAS shows up
when the editor opens or saves the course (section 7).

**Examples:**

```csharp
// 1. Container SAS + blob name (most common)
string url = Launcher.EditorUrl(
    sasUrl:      containerSas,            // https://…/courses?sv=…&sp=rw&sig=…
    blobName:    "finance/intro.zip",
    sourceName:  "Content Manager",
    returnUrl:   "content",
    returnLabel: "Back to courses");
// url == "editor?open=9f2c4a7e1b3d5f60718293a4b5c6d7e8"
Nav.NavigateTo(url);

// 2. Blob SAS (blobName not needed)
Nav.NavigateTo(Launcher.EditorUrl(blobSas, sourceName: "Content Manager"));

// 3. Folder SAS: "intro.zip" is taken as "finance/intro.zip"
Nav.NavigateTo(Launcher.EditorUrl("https://…/courses/finance?sv=…&sig=…", "intro.zip"));

// 4. Minimal: no Back button, source shown as account/container
Nav.NavigateTo(Launcher.EditorUrl(containerSas, "finance/intro.zip"));
```

**Making the SAS** (server side, with the `Azure.Storage.Blobs` package; the account key stays in
the host's configuration):

```csharp
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

// For one blob
var blob = new BlobClient(connectionString, "courses", "finance/intro.zip");
Uri blobSas = blob.GenerateSasUri(
    BlobSasPermissions.Read | BlobSasPermissions.Write,
    DateTimeOffset.UtcNow.AddHours(8));          // must last as long as the editing session

// For the container
var container = new BlobContainerClient(connectionString, "courses");
Uri containerSas = container.GenerateSasUri(
    BlobContainerSasPermissions.Read | BlobContainerSasPermissions.Write,
    DateTimeOffset.UtcNow.AddHours(8));

Nav.NavigateTo(Launcher.EditorUrl(containerSas.ToString(), "finance/intro.zip"));
```

`GenerateSasUri` needs a client created with the account key (a connection string containing
`AccountKey=`). With Entra ID / managed identity, use a user delegation SAS instead
(`BlobServiceClient.GetUserDelegationKeyAsync` + `BlobSasBuilder`).

**What happens on save:** the editor overwrites **the same blob**, only if nobody changed it since
it was opened (ETag check). No `.OLD` copy, no new blob.

### 2.2 `EditorUrl` for a course .zip your app already has

```csharp
public string EditorUrl(
    byte[]               zip,
    string               fileName,
    Func<byte[], Task>   onSave,
    string?              sourceName  = null,
    string?              returnUrl   = null,
    string?              returnLabel = null)
```

Use this when the host app reads the course itself (database, file share, its own storage code).

| Parameter | Type | Required | What it is | Example |
|---|---|---|---|---|
| `zip` | `byte[]` | **yes** | The course `.zip` file's bytes. Must not be empty. Max size: `MaxUploadMB` (section 5). | `await File.ReadAllBytesAsync(@"\\fs\courses\intro.zip")` |
| `fileName` | `string` | **yes** | The file's name. Folders are removed; empty becomes `"course.zip"`. Shown in the editor and used for Download. | `"intro.zip"` |
| `onSave` | `Func<byte[], Task>` | **yes** | Your code that stores the edited course. The editor calls it **every time the user saves**, with the **whole edited .zip**. If it throws, the user sees the exception's message and can try again. | `edited => File.WriteAllBytesAsync(path, edited)` |
| `sourceName` | `string?` | no | Shown as where the course came from: `<sourceName> › <fileName>`. Default text: `Sent by the app`. | `"Content Manager"` |
| `returnUrl` | `string?` | no | Adds a Back button linking here. | `"content"` |
| `returnLabel` | `string?` | no | Back button text. Default `"Back"`. | `"Back to courses"` |

**Returns:** `string`, `"editor?open=<code>"` (as 2.1).

**Throws:**

| Exception | When |
|---|---|
| `ArgumentNullException` | `zip` or `onSave` is null |
| `ArgumentException` (`The course .zip is empty.`) | `zip.Length == 0` |

**About `onSave`:**

- It runs **on the server**, later, when the user presses **Save course → Replace file**. The page
  that called `EditorUrl` may be gone by then. Don't use that page's components or injected
  scoped services (such as a `DbContext`) inside it; create a scope (`IServiceScopeFactory`) or
  use singletons.
- `edited` is the complete course: the original files with the user's changes, plus
  `scorm-editor-changes.txt` (a log of what was changed).
- Throw an exception with a clear message to report a failure, for example
  `throw new InvalidOperationException("The course is locked by another editor.");`. The user then sees
  **"The course was not saved: The course is locked by another editor."**
- Checking whether someone else changed the course meanwhile is up to your code (the editor can't
  see your storage).

**Examples:**

```csharp
// 1. File share
var path = Path.Combine(@"\\fileserver\courses", "intro.zip");
Nav.NavigateTo(Launcher.EditorUrl(
    zip:         await File.ReadAllBytesAsync(path),
    fileName:    "intro.zip",
    onSave:      edited => File.WriteAllBytesAsync(path, edited),
    sourceName:  "Course share",
    returnUrl:   "content",
    returnLabel: "Back to courses"));

// 2. Database (EF Core), with a "changed meanwhile" check
var openedVersion = row.Version;
Nav.NavigateTo(Launcher.EditorUrl(row.Package, row.FileName, onSave: async edited =>
{
    using var scope = ScopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
    var current = await db.Courses.FindAsync(row.Id)
                  ?? throw new InvalidOperationException("The course was deleted.");
    if (current.Version != openedVersion)
        throw new InvalidOperationException("Someone else saved this course after you opened it.");
    current.Package = edited;
    current.Version++;
    openedVersion = current.Version;      // so the user's next save is allowed
    await db.SaveChangesAsync();
}, sourceName: "Content Manager", returnUrl: "content"));

// 3. Your own Azure code (e.g. managed identity), instead of a SAS
var blob = blobServiceClient.GetBlobContainerClient("courses").GetBlobClient("finance/intro.zip");
var bytes = (await blob.DownloadContentAsync()).Value.Content.ToArray();
Nav.NavigateTo(Launcher.EditorUrl(bytes, "intro.zip",
    onSave: async edited => await blob.UploadAsync(new BinaryData(edited), overwrite: true),
    sourceName: "Content Manager"));
```

### 2.3 `Create` (both versions)

```csharp
public string Create(string sasUrl, string? blobName = null, string? sourceName = null,
                     string? returnUrl = null, string? returnLabel = null)

public string Create(byte[] zip, string fileName, Func<byte[], Task> onSave, string? sourceName = null,
                     string? returnUrl = null, string? returnLabel = null)
```

Same parameters, checks and exceptions as the matching `EditorUrl`, but returns **only the code**
(`"9f2c4a7e1b3d5f60718293a4b5c6d7e8"`). `EditorUrl(...)` is exactly `"editor?open=" + Create(...)`.
Use `Create` when you build the URL yourself, for example to open the editor in a new tab or under
a different route:

```razor
<a href="@($"editor?open={Launcher.Create(sas, blobName)}")" target="_blank">Edit in new tab</a>
```

Create the code when the user asks (on click), not when the page renders: a code expires after 5
minutes and works once.

### 2.4 `Lifetime`

```csharp
public static readonly TimeSpan Lifetime;   // TimeSpan.FromMinutes(5)
```

How long a code from `Create`/`EditorUrl` stays valid. After that, or after it has been used once,
the editor shows "This link to open a course has expired or was already used". Codes are kept in
server memory; expired ones are cleaned up on the next `Create`.

### 2.5 `Take` (editor only)

```csharp
public Request? Take(string? code)
```

Used by the editor page to collect a launch. Returns the request **once**, then forgets it; `null`
for an unknown, used or expired code. The host app does not need to call it.

---

## 3. The editor page: route and query string

| Item | Value |
|---|---|
| Component | `ScormEditor.Components.Pages.ScormEditorPage` (`Components/Pages/ScormEditorPage.razor`) |
| Route | `/editor` |
| `[Parameter]`s | none |
| Query string | `open` (`[SupplyParameterFromQuery(Name = "open")] string? OpenCode`): a code from the launcher |
| Render mode | Interactive Server |

| URL | Result |
|---|---|
| `/editor` | Empty editor (or carries on with the course this tab had open before a refresh), with **Open course** |
| `/editor?open=9f2c…` (valid code) | Opens that course; the `?open=` part is removed from the address bar; Back button if `returnUrl` was given |
| `/editor?open=9f2c…` (used/expired/unknown) | Empty editor with the message "This link to open a course has expired or was already used. Go back and choose Edit again." |

---

## 4. Startup: methods to call in Program.cs

| Call | Required | What it does | Example |
|---|---|---|---|
| `services.AddSingleton<AssetVersion>()` | yes | `?v=` cache-busting for the editor's JS/CSS (used by the page and `App.razor`) | |
| `services.Configure<ScormEditorOptions>(config.GetSection("ScormEditor"))` | yes | Limits, section 5 | |
| `services.AddSingleton<ScormPackageStore>()` | yes | Holds open courses in memory | |
| `services.AddSingleton<AzureBlobService>()` | yes | Azure open/save | |
| `services.AddScoped<AzureConnections>()` | yes | Saved containers (needed by the dialogs even if unused) | |
| `services.AddSingleton<ScormEditorLauncher>()` | yes | Section 2 | |
| `services.AddDataProtection().SetApplicationName("ScormEditor")` | yes | Encrypts saved SAS URLs in the browser | `.PersistKeysToFileSystem(new DirectoryInfo(@"D:\keys\scorm"))` |
| `.AddInteractiveServerComponents().AddHubOptions(o => o.MaximumReceiveMessageSize = 64 * 1024 * 1024)` | yes | Lets big edited pages reach the server for review | |
| `app.MapScormPreview()` | yes | Maps the endpoints in section 6. Returns the `IEndpointRouteBuilder`, so it can be called on a route group | `app.MapGroup("").RequireAuthorization().MapScormPreview();` |

---

## 5. Configuration: `ScormEditorOptions`

Bound from the `ScormEditor` section of `appsettings.json`.

| Key | Type | Default | Meaning | Example |
|---|---|---|---|---|
| `MaxUploadMB` | int | 500 | Largest course .zip that can be opened (also applies to `zip` in 2.2) | `800` |
| `MaxOpenCourses` | int | 20 | Most courses open at once, all users together | `40` |
| `IdleHours` | double | 8 | A course unused for this long is closed | `4` |
| `ReopenMinutes` | double | 30 | How long a course is kept after its tab closes, so a refresh carries on | `60` |
| `DataProtectionKeysPath` | string | `""` | Folder for Data Protection keys (read in Program.cs, not part of the options class) | `"D:\\AppData\\ScormEditorKeys"` |

```json
"ScormEditor": {
  "MaxUploadMB": 500,
  "MaxOpenCourses": 20,
  "IdleHours": 8,
  "ReopenMinutes": 30,
  "DataProtectionKeysPath": ""
}
```

---

## 6. HTTP endpoints

Mapped by `app.MapScormPreview()`. Used by the editor's own browser code; the host app doesn't
call them, but they must be reachable (and should be behind the host's sign-in).

| Method and path | Purpose | Response |
|---|---|---|
| `GET /scorm-preview/{id}/{**path}` | A file of an open course, for the preview iframe. `id` = course GUID, `path` = file path in the zip. Path match falls back to case-insensitive. | `200` with the file and its content type, `Cache-Control: no-store`; `404` if the course or file isn't open |
| `PUT /scorm-preview/{id}/{**path}` | Writes a file (Code tab, image swap). Body = file content; text arrives as UTF-8 and is stored in the file's own encoding. Header `X-Scorm-Editor-Step: <label>` makes it one Undo step. | `204`; `400` empty path; `404` course not open |
| `GET /scorm-export/{id}` | The edited course as a .zip (Download, saving to this computer) | `200` `application/zip`, file name `<name>-edited.zip`; `404` |

Example: `GET /scorm-preview/0b6c…e1/scormcontent/index.html`.

---

## 7. Messages the user can see

| When | Message |
|---|---|
| Course opened | `Opened intro.zip from Content Manager › finance/intro.zip (213 files).` |
| Launch code used/expired | `This link to open a course has expired or was already used. Go back and choose Edit again.` |
| Course too big | `intro.zip is 812.0 MB. The largest course the editor can open is 500 MB.` |
| Server full | `The server already has 20 courses open, the most it allows. …` |
| Azure unreachable while opening | `Could not reach the storage account. Check your network and try again.` |
| Save dialog for a launched course | Options **Back where it came from** and **Download**; warning `Saving replaces intro.zip with your changes. No copy of the old file is kept.`; button **Replace file** |
| Saved | `Saved to Content Manager › finance/intro.zip. It replaced the file that was there.` |
| Blob changed since opened | `Someone else changed this file after you opened it. …` with **Replace anyway** |
| `onSave` threw | `The course was not saved: <exception message>` |

---

## 8. Complete examples

### 8.1 A course list page with Edit buttons (Azure)

```razor
@page "/content"
@using Azure.Storage.Blobs
@using Azure.Storage.Sas
@inject ScormEditorLauncher Launcher
@inject NavigationManager Nav
@inject IConfiguration Config

<h3>Courses</h3>
<table class="table">
    @foreach (var path in coursePaths)
    {
        <tr>
            <td>@path</td>
            <td><button class="btn btn-sm btn-primary" @onclick="() => Edit(path)">Edit</button></td>
        </tr>
    }
</table>

@code {
    private readonly List<string> coursePaths = [];
    private BlobContainerClient container = default!;

    protected override async Task OnInitializedAsync()
    {
        container = new BlobContainerClient(Config.GetConnectionString("CourseStorage"), "courses");
        await foreach (var b in container.GetBlobsAsync())
            if (b.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) coursePaths.Add(b.Name);
    }

    private void Edit(string blobPath)
    {
        var sas = container.GenerateSasUri(
            BlobContainerSasPermissions.Read | BlobContainerSasPermissions.Write,
            DateTimeOffset.UtcNow.AddHours(8));

        Nav.NavigateTo(Launcher.EditorUrl(
            sasUrl:      sas.ToString(),
            blobName:    blobPath,
            sourceName:  "Content Manager",
            returnUrl:   "content",
            returnLabel: "Back to courses"));
    }
}
```

### 8.2 The same, when the app reads and writes the course itself

```razor
@page "/content"
@inject ScormEditorLauncher Launcher
@inject NavigationManager Nav
@inject ICourseRepository Courses      @* your singleton service *@

@code {
    private async Task Edit(int courseId)
    {
        var course = await Courses.GetAsync(courseId);       // has Id, FileName, Zip (byte[])

        Nav.NavigateTo(Launcher.EditorUrl(
            zip:         course.Zip,
            fileName:    course.FileName,
            onSave:      edited => Courses.SaveZipAsync(courseId, edited),
            sourceName:  "Content Manager",
            returnUrl:   "content",
            returnLabel: "Back to courses"));
    }
}
```

### 8.3 Handling bad input

```csharp
try
{
    Nav.NavigateTo(Launcher.EditorUrl(sasUrl, blobName));
}
catch (ArgumentException ex)
{
    error = ex.Message;      // e.g. "This URL has no SAS token (no sig=… part). …"
}
```
