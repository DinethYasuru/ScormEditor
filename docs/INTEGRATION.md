# Putting the SCORM editor into another Blazor app

This guide covers moving **only the SCORM editor page** into another Blazor Server app (.NET 8),
such as the IFS Academy Content Manager, and **opening a course in it from that app**, for example
from an "Edit" button next to a course.

- [1. What to copy](#1-what-to-copy)
- [2. Program.cs](#2-programcs)
- [3. appsettings.json](#3-appsettingsjson)
- [4. App.razor (styles and scripts)](#4-apprazor-styles-and-scripts)
- [5. Opening a course from your app](#5-opening-a-course-from-your-app)
  - [A. The course is in Azure Blob Storage](#a-the-course-is-in-azure-blob-storage)
  - [B. Your app sends the course .zip itself](#b-your-app-sends-the-course-zip-itself)
- [6. How saving works](#6-how-saving-works)
- [7. Things to know](#7-things-to-know)
- [8. Checklist](#8-checklist)

---

## 1. What to copy

From this repository into the other project, keeping the same folders:

| Copy | Why |
|---|---|
| `Services/` (all files) | Course model, store, preview endpoints, edit mapping, Azure access, `ScormEditorLauncher` |
| `Components/Pages/ScormEditorPage.razor` | The editor page, route `/editor` |
| `Components/Shared/` (all files) | The Save and Open dialogs and the parts they use (`SaveCourseDialog`, `AzureOpenDialog`, `AzureBrowser`, `SasSummary`, `ExpiryBadge`, `AzurePermissionsNote`) |
| `wwwroot/js/scorm-editor.js` | Preview editing, Code tab, LMS stub; loaded by the page as a JS module |
| `wwwroot/css/scorm-editor.css` | Editor styles |
| `wwwroot/lib/codemirror/` | Code tab editor (CodeMirror 5, MIT licence) |

Optional: `Components/Pages/AzureBlobPage.razor`, the page where users save container SAS URLs and
browse Azure (route `/azure`). The Open and Save dialogs link to `azure` ("Manage containers"). If
you don't bring that page, users can still open and save courses sent from your app (section 5);
only the dialogs' "Azure Blob" choices will have no saved containers.

Bootstrap 5 and open-iconic are also used. The Content Manager already has both.

**NuGet:** add `Azure.Storage.Blobs` (version 12.30.1 here).

**Namespaces:** everything is in `ScormEditor.Services` and `ScormEditor.Components.Shared`. Add to
the other app's `Components/_Imports.razor`:

```razor
@using ScormEditor.Services
@using ScormEditor.Components.Shared
```

(Or rename the namespaces to your project's.)

## 2. Program.cs

Add these to the other app's `Program.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;
using ScormEditor.Services;

// SCORM editor
builder.Services.AddSingleton<AssetVersion>();
builder.Services.Configure<ScormEditorOptions>(builder.Configuration.GetSection("ScormEditor"));
builder.Services.AddSingleton<ScormPackageStore>();
builder.Services.AddSingleton<AzureBlobService>();
builder.Services.AddScoped<AzureConnections>();
builder.Services.AddSingleton<ScormEditorLauncher>();

// Saved Azure SAS URLs are encrypted with these keys before they are stored in the browser.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("ScormEditor");
if (builder.Configuration["ScormEditor:DataProtectionKeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    // Edited pages are sent to the server for review; a large course page can exceed the 32 KB default.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 64 * 1024 * 1024);

var app = builder.Build();
...
app.UseStaticFiles();
app.UseAntiforgery();

app.MapScormPreview();          // GET/PUT /scorm-preview/{id}/{**path}; before MapRazorComponents

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
```

If your app already calls `AddDataProtection()` or `AddRazorComponents()`, add to those calls rather
than calling them twice. Keep the `MaximumReceiveMessageSize` line: without it, reviewing a large
page fails.

## 3. appsettings.json

```json
"ScormEditor": {
  "MaxUploadMB": 500,
  "MaxOpenCourses": 20,
  "IdleHours": 8,
  "ReopenMinutes": 30,
  "DataProtectionKeysPath": ""
}
```

| Setting | Meaning |
|---|---|
| `MaxUploadMB` | Largest course .zip that can be opened |
| `MaxOpenCourses` | Most courses open at once, across all users (each is held in memory) |
| `IdleHours` | A course nobody has used for this long is closed |
| `ReopenMinutes` | How long a course is kept after its editor tab closes, so a refresh carries on |
| `DataProtectionKeysPath` | On IIS without a loaded user profile, a folder only the app can read, so the keys survive a restart |

## 4. App.razor (styles and scripts)

In `<head>`, after Bootstrap and open-iconic:

```razor
<link rel="stylesheet" href="lib/codemirror/codemirror.css" />
<link rel="stylesheet" href="@Assets.Url("css/scorm-editor.css")" />
```

Before `_framework/blazor.web.js` in `<body>`:

```razor
<script src="lib/codemirror/codemirror.js"></script>
<script src="lib/codemirror/mode/xml.js"></script>
<script src="lib/codemirror/mode/javascript.js"></script>
<script src="lib/codemirror/mode/css.js"></script>
<script src="lib/codemirror/mode/htmlmixed.js"></script>
<script src="lib/codemirror/addon/matchbrackets.js"></script>
<script src="lib/codemirror/addon/xml-fold.js"></script>
<script src="lib/codemirror/addon/closetag.js"></script>
<script>window.scormEditorTimeOffset = () => -new Date().getTimezoneOffset();</script>
```

`@Assets.Url(...)` needs `@inject ScormEditor.Services.AssetVersion Assets` at the top of
`App.razor`. It adds `?v=<last change>` so browsers fetch the new file after an update. The last
script line is used only by the Azure Blob page.

**Render mode:** the editor must run as Interactive Server. This app sets it for every page
(`<Routes @rendermode="InteractiveServer" />`). If your app sets render modes per page, add
`@rendermode InteractiveServer` under `@page "/editor"` in `ScormEditorPage.razor`.

**Menu:** add a link to `editor` if users should be able to open the editor on its own, and leave
it out if it is only reached from your Edit buttons.

## 5. Opening a course from your app

The editor page takes no `[Parameter]`s. Your page hands the course to the singleton
**`ScormEditorLauncher`** on the server and navigates to the URL it returns:

```
editor?open=3f9c0a7e5d1b4c2a9e8f7d6c5b4a3f21
```

Only that **one-time code** is in the address. The SAS URL or the zip stays in server memory and
never reaches the browser. A code works **once** and **expires after 5 minutes**
(`ScormEditorLauncher.Lifetime`). The editor removes it from the address as soon as it opens the
course, so a refresh carries on with the course instead of trying the code again. An expired or
used code shows "This link to open a course has expired or was already used. Go back and choose
Edit again."

Every launch can also set:

| Argument | Example | Effect |
|---|---|---|
| `sourceName` | `"Content Manager"` | Shown in the editor as where the course came from: `Content Manager › courses/intro.zip` |
| `returnUrl` | `"content"` | Adds a Back button to the editor's toolbar that goes here |
| `returnLabel` | `"Back to courses"` | The Back button's text (`"Back"` when left out) |

### A. The course is in Azure Blob Storage

```csharp
public string Create(string sasUrl, string? blobName = null, string? sourceName = null,
                     string? returnUrl = null, string? returnLabel = null);
public string EditorUrl(string sasUrl, string? blobName = null, string? sourceName = null,
                        string? returnUrl = null, string? returnLabel = null);   // "editor?open=" + Create(...)
```

- `sasUrl`: a SAS URL with **read and write** permission, either
  - for the **container** (or a folder in it), with `blobName` being the course's full path in the
    container, such as `"courses/intro.zip"`. A name without the folder of a folder SAS is taken as
    inside that folder.
  - or for **the course .zip blob itself**, with `blobName` left out.
- It throws `ArgumentException` when the SAS URL can't be read or no blob is named.

**Example: an Edit button in a course list.** The SAS is made on the server from the storage
account's connection string, which stays in your configuration:

```razor
@using Azure.Storage.Blobs
@using Azure.Storage.Sas
@inject ScormEditorLauncher Launcher
@inject NavigationManager Nav
@inject IConfiguration Config

@foreach (var course in courses)
{
    <tr>
        <td>@course.Title</td>
        <td><button class="btn btn-sm btn-primary" @onclick="() => Edit(course)">Edit</button></td>
    </tr>
}

@code {
    private void Edit(Course course)
    {
        // A SAS for this one blob: read it, and write it back.
        var blob = new BlobClient(Config.GetConnectionString("CourseStorage"), "courses", course.BlobPath);
        var sas = blob.GenerateSasUri(BlobSasPermissions.Read | BlobSasPermissions.Write,
                                      DateTimeOffset.UtcNow.AddHours(8));

        Nav.NavigateTo(Launcher.EditorUrl(
            sasUrl: sas.ToString(),
            sourceName: "Content Manager",
            returnUrl: "content",
            returnLabel: "Back to courses"));
    }
}
```

The same with a container SAS and a blob name:

```csharp
var container = new BlobContainerClient(Config.GetConnectionString("CourseStorage"), "courses");
var sas = container.GenerateSasUri(BlobContainerSasPermissions.Read | BlobContainerSasPermissions.Write,
                                   DateTimeOffset.UtcNow.AddHours(8));

Nav.NavigateTo(Launcher.EditorUrl(sas.ToString(), blobName: "courses/intro.zip",
                                  sourceName: "Content Manager", returnUrl: "content"));
```

`GenerateSasUri` needs a client made with the account key (a connection string with `AccountKey=`,
or a `StorageSharedKeyCredential`). With Entra ID / managed identity, make a user delegation SAS
instead (`BlobServiceClient.GetUserDelegationKeyAsync` + `BlobSasBuilder`).

**SAS lifetime:** the editor saves with the same SAS, so it must still be valid when the user
presses Save. Make it last as long as an editing session (8 hours above, matching `IdleHours`),
not 5 minutes. The 5 minutes apply only to the one-time code.

### B. Your app sends the course .zip itself

Use this when your app already has the zip (from a database, a file share, an upload, or your own
storage code).

```csharp
public string Create(byte[] zip, string fileName, Func<byte[], Task> onSave, string? sourceName = null,
                     string? returnUrl = null, string? returnLabel = null);
public string EditorUrl(byte[] zip, string fileName, Func<byte[], Task> onSave, string? sourceName = null,
                        string? returnUrl = null, string? returnLabel = null);   // "editor?open=" + Create(...)
```

- `zip`: the course .zip. It throws `ArgumentException` when the zip is empty.
- `fileName`: its name, such as `"intro.zip"` (folders are dropped; `"course.zip"` when empty).
- `onSave`: called with the **whole edited .zip** each time the user saves. Your code stores it.

**Example: a course on a file share.**

```razor
@inject ScormEditorLauncher Launcher
@inject NavigationManager Nav

<button class="btn btn-sm btn-primary" @onclick="() => Edit(course)">Edit</button>

@code {
    private async Task Edit(Course course)
    {
        var path = Path.Combine(@"\\fileserver\courses", course.FileName);
        var zip = await File.ReadAllBytesAsync(path);

        Nav.NavigateTo(Launcher.EditorUrl(
            zip,
            fileName: course.FileName,
            onSave: edited => File.WriteAllBytesAsync(path, edited),
            sourceName: "Content Manager",
            returnUrl: "content",
            returnLabel: "Back to courses"));
    }
}
```

**Example: a course stored in a database.** `onSave` runs later, from the editor page, after the
page that made it may be gone, so don't use that page's components or a `DbContext` it injected.
Create what you need inside `onSave`:

```razor
@inject IServiceScopeFactory ScopeFactory

@code {
    private async Task Edit(int courseId)
    {
        byte[] zip;
        string name;
        using (var scope = ScopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            var row = await db.Courses.FindAsync(courseId);
            (zip, name) = (row!.Package, row.FileName);
        }

        Nav.NavigateTo(Launcher.EditorUrl(zip, name, onSave: async edited =>
        {
            using var scope = ScopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            var row = await db.Courses.FindAsync(courseId)
                      ?? throw new InvalidOperationException("The course was deleted.");
            row.Package = edited;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }, sourceName: "Content Manager", returnUrl: "content"));
    }
}
```

If `onSave` throws, the Save dialog shows **"The course was not saved: <exception message>"**, the
course stays marked as changed, and the user can try again. So throw with a message the user can
understand.

## 6. How saving works

For a course opened through the launcher (A or B), **Save course** only ever **overwrites the
original**. Nothing new is created:

- The Save dialog offers **Back where it came from** (button: **Replace file**) and **Download**.
  The other choices (another Azure container, a folder on this computer) are hidden.
- There is no `.OLD` copy of the old file and no "Keep the old file" option.
- The saved zip includes `scorm-editor-changes.txt`, the change log of what was edited.

**Someone else changed it (Azure only).** The editor remembers the blob's ETag when it opens the
course and saves only if the blob still has it. If someone else saved the blob in the meantime,
the dialog says **"Someone else changed this file after you opened it"** and the user can
**Replace anyway** or Download their copy. After each save the new ETag is kept, so saving again is
fine. For B (zip bytes), the editor can't see your storage, so this check is up to `onSave`, for
example by comparing a version or timestamp you captured before opening and throwing if it changed.

## 7. Things to know

- **One server, in memory.** Open courses and launch codes are held in the app's memory
  (`ScormPackageStore`, `ScormEditorLauncher`). The page that calls `EditorUrl` and the editor must
  run in the **same app** (they do once merged), and on a web farm you need sticky sessions (ARR
  affinity) so a user's requests reach the same server. An app restart closes open courses; saved
  changes are in Azure or wherever `onSave` put them.
- **New tab.** `EditorUrl` also works as a link opened in a new tab:
  `<a href="@url" target="_blank">Edit</a>`, as long as the code is created when the user asks (it
  expires after 5 minutes and works once).
- **Sign-in.** The `/scorm-preview/{id}/...` endpoints have no sign-in of their own (the id is a
  random GUID). Put the editor page and these endpoints behind your app's sign-in, for example
  `app.MapScormPreview()` inside an authorized route group, and `[Authorize]` on the page.
- **Site root.** `scorm-editor.js` writes edits to `/scorm-preview/...` from the site root. If your
  app runs under a sub-path (an IIS virtual directory such as `https://server/academy/`), that path
  needs a small change in `scorm-editor.js` (`base = \`/scorm-preview/${id}\``) first.
- **Window-wide script.** The editor's script listens for keys, drag and drop and `beforeunload` on
  the whole window while the page is open, and sets `window.API` / `window.API_1484_11` (the SCORM
  LMS stub the preview talks to). Check that nothing else in your app uses those names.
- **Packaging later.** Copying files works, but a **Razor Class Library** (one NuGet/project
  reference with `AddScormEditor()` and `MapScormEditor()`, static files under
  `_content/ScormEditor/`) would be the cleaner way to share the editor between apps. It isn't
  built yet.

## 8. Checklist

1. Copy `Services/`, `Components/Pages/ScormEditorPage.razor`, `Components/Shared/`,
   `wwwroot/js/scorm-editor.js`, `wwwroot/css/scorm-editor.css`, `wwwroot/lib/codemirror/`.
2. Add the `Azure.Storage.Blobs` NuGet package.
3. Add the two `@using` lines to `_Imports.razor`.
4. In `Program.cs`, register the 6 services and Data Protection, set `MaximumReceiveMessageSize`,
   and call `app.MapScormPreview()`.
5. Add the `ScormEditor` section to `appsettings.json`.
6. In `App.razor`, add the 2 stylesheets and 8 CodeMirror scripts (plus `@inject AssetVersion`).
7. Make sure `/editor` runs as Interactive Server.
8. In your course list, make the Edit button call `Launcher.EditorUrl(...)` (section 5) and
   `Nav.NavigateTo(...)` the result.
9. Put `/editor` and `/scorm-preview` behind your sign-in.
10. Test: Edit → change some text → Review and save → Save course → Replace file → check the stored
    course has the change → Back to courses.
