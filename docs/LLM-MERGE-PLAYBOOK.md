# SCORM Editor: merge playbook for AI agents

> **Audience:** an AI coding agent (or a developer) that must move the SCORM editor from this
> repository into another Blazor Server app and wire it to that app's courses.
> **Read this whole file before changing anything.** Then follow the steps in order. Every step
> has a check. Do not skip checks.
>
> Human-oriented reference: [`docs/SCORM-Editor-Guide.md`](SCORM-Editor-Guide.md) (features,
> step-by-step flows in section 6, every file in section 7). This playbook is self-contained for the
> merge, and cites guide sections where more detail exists.

---

## 0. Terms used in this file

| Term | Meaning |
|---|---|
| **Source repo** | This repository, `DinethYasuru/ScormEditor` (project `ScormEditor`, root namespace `ScormEditor`). |
| **Host app** | The other Blazor Server app the editor is moved into (likely the IFS Academy Content Manager, project `IfsContentPortal`). Its root namespace is written `<HostNs>` below. |
| **Host page** | The host app's page that lists courses and has the **Edit** button. |
| **Course** | A SCORM package: a `.zip` with `imsmanifest.xml`. |
| **Launcher** | `ScormEditor.Services.ScormEditorLauncher`, the API the host page calls to open a course in the editor. |
| **Editor page** | `ScormEditorPage.razor`, route `/editor`. |

## 1. Facts (source of truth: the code, as of this file's commit)

- Framework: **.NET 8**, Blazor Web App, **Interactive Server** render mode. Not WebAssembly.
- One NuGet dependency: **`Azure.Storage.Blobs` 12.30.1**.
- All C# services use the explicit namespace **`ScormEditor.Services`**.
- Razor components declare explicit namespaces, so they do not change when copied:
  `ScormEditorPage.razor` → `@namespace ScormEditor.Components.Pages`;
  every file in `Components/Shared/` → `@namespace ScormEditor.Components.Shared`.
- The editor page takes **no `[Parameter]`**. Its only input is the query string
  **`?open=<code>`** (`[SupplyParameterFromQuery(Name = "open")] public string? OpenCode`).
- Open courses are held **in server memory** (`ScormPackageStore`, singleton). Launch requests are
  held in server memory (`ScormEditorLauncher`, singleton) for **5 minutes**, **single use**.
- HTTP endpoints added by `app.MapScormPreview()`:
  - `GET  /scorm-preview/{id:guid}/{**path}`: a file of an open course (the preview iframe loads these).
  - `PUT  /scorm-preview/{id:guid}/{**path}`: write a file (Code tab, image swap). Antiforgery disabled on this endpoint.
  - `GET  /scorm-export/{id:guid}`: the edited course as `.zip` (download / save to computer).
- JavaScript module `wwwroot/js/scorm-editor.js` is imported by the page at
  `"./" + AssetVersion.Url("js/scorm-editor.js")`, i.e. relative to `<base href>`.
- `scorm-editor.js` builds preview URLs as **`/scorm-preview/${id}`** (from the site root,
  line `base = \`/scorm-preview/${id}\``). See pitfall P3.

## 2. Public API the host app uses (exact signatures)

```csharp
namespace ScormEditor.Services;

public sealed class ScormEditorLauncher   // register as singleton
{
    public static readonly TimeSpan Lifetime;            // 5 minutes

    // A. Course in Azure Blob Storage.
    //    sasUrl: SAS with read + write. Either a container/folder SAS plus blobName ("courses/intro.zip"),
    //    or a blob SAS with blobName omitted. Throws ArgumentException if the SAS can't be parsed or no blob is named.
    public string Create(string sasUrl, string? blobName = null, string? sourceName = null,
                         string? returnUrl = null, string? returnLabel = null);          // returns the code
    public string EditorUrl(string sasUrl, string? blobName = null, string? sourceName = null,
                            string? returnUrl = null, string? returnLabel = null);       // "editor?open=" + code

    // B. Course .zip handed over in memory. onSave receives the whole edited .zip on every save.
    //    Throws ArgumentNullException (zip/onSave null) or ArgumentException (zip empty).
    public string Create(byte[] zip, string fileName, Func<byte[], Task> onSave, string? sourceName = null,
                         string? returnUrl = null, string? returnLabel = null);
    public string EditorUrl(byte[] zip, string fileName, Func<byte[], Task> onSave, string? sourceName = null,
                            string? returnUrl = null, string? returnLabel = null);

    public Request? Take(string? code);   // used by the editor page only
}
```

Parameter meanings: `sourceName` = text shown as where the course came from;
`returnUrl` = if set, the editor shows a Back button linking here; `returnLabel` = that button's
text (default "Back").

## 3. Invariants (MUST / MUST NOT)

These are product decisions made by the project owner. Keep them unless the human says otherwise.

1. **MUST NOT** put a SAS URL in a browser-visible place (URL, HTML, JS, logs). Pass it to the
   launcher on the server; only `?open=<code>` goes in the address.
2. A course opened through the launcher **MUST only overwrite its original** on save: no new file,
   no `.OLD` copy, no saving to another place (Download is allowed). This is implemented by
   `CourseSource.ReplaceOnly = true`; do not remove it.
3. The Azure save **MUST keep the ETag check** ("someone else changed this file"). Do not pass
   `null` as the expected ETag except through the user's explicit **Replace anyway**.
4. The editor **MUST NOT delete blobs**. `AzureBlobService` has no delete; do not add one.
5. Preview edits **MUST be saved as minimal changes** found in source files (`EditMapper`), never
   by saving the rendered page DOM.
6. ARR Word Check **MUST NOT replace words**; it only points them out.
7. The owner wants each change as a **pull request merged to `main` after it is tested**, and wants
   a local working copy kept in sync with `git pull`.

## 4. Questions to ask the human before starting

Ask only what you cannot find in the host repo:

1. Where do courses live: Azure Blob (which container and how the host gets SAS or keys), or
   somewhere the host reads bytes from (database, file share)? → decides **5A** or **5B**.
2. Does the host run under a sub-path (IIS virtual directory, `PathBase`)? → pitfall P3.
3. Is the host load-balanced across several servers? → pitfall P4.
4. Should the editor be in the host's menu, or only reachable from Edit buttons?
5. Should the Azure Blob page (`/azure`) come too? (Default: no.)

## 5. Steps

### Step 1: Copy files

Copy from the source repo into the host project, keeping these relative paths:

```
Services/AssetVersion.cs
Services/AzureBlobService.cs
Services/AzureConnections.cs
Services/AzureSas.cs
Services/CourseSource.cs
Services/DraftPatcher.cs
Services/EditMapper.cs
Services/ScormEditorLauncher.cs
Services/ScormManifest.cs
Services/ScormPackage.cs
Services/ScormPackageStore.cs
Services/ScormPreviewEndpoints.cs
Services/TermList.cs
Services/TextDiff.cs
Services/TextEncoding.cs
Components/Pages/ScormEditorPage.razor
Components/Shared/AzureBrowser.razor
Components/Shared/AzureOpenDialog.razor
Components/Shared/AzurePermissionsNote.razor
Components/Shared/ExpiryBadge.razor
Components/Shared/SasSummary.razor
Components/Shared/SaveCourseDialog.razor
wwwroot/js/scorm-editor.js
wwwroot/css/scorm-editor.css
wwwroot/lib/codemirror/            (whole folder, including LICENSE)
```

Optional (only if the human wants the Azure Blob page): `Components/Pages/AzureBlobPage.razor`.

**Do NOT copy:** `Program.cs`, `Components/App.razor`, `Components/Routes.razor`,
`Components/_Imports.razor`, `Components/Layout/*`, `Components/Pages/Home.razor`,
`Components/Pages/Error.razor`, `wwwroot/lib/bootstrap`, `wwwroot/lib/open-iconic`,
`wwwroot/css/site.css`, `appsettings*.json`, `Properties/*`. The host has its own; merge settings
into them in later steps instead.

If a host file already exists at one of those paths (for example `Services/TextDiff.cs`), stop and
tell the human: do not overwrite host files.

**Check:** every listed file exists in the host project; no host file was overwritten.

### Step 2: NuGet

Add to the host `.csproj` (skip if the host already references it at the same or newer version):

```xml
<PackageReference Include="Azure.Storage.Blobs" Version="12.30.1" />
```

**Check:** `dotnet restore` succeeds.

### Step 3: `_Imports.razor`

Append to the host's `Components/_Imports.razor`:

```razor
@using ScormEditor.Services
@using ScormEditor.Components.Shared
```

The page and dialogs also need these, which a Blazor Web App template already has:
`Microsoft.AspNetCore.Components.Forms`, `Microsoft.AspNetCore.Components.Web`,
`Microsoft.JSInterop`, `static Microsoft.AspNetCore.Components.Web.RenderMode`.

**Check:** later build has no `CS0246` for `ScormPackage`, `SaveCourseDialog`, `AzureOpenDialog`.

### Step 4: `Program.cs`

Add, keeping the host's existing calls (do not register Razor components twice; add
`.AddHubOptions(...)` to the existing `AddInteractiveServerComponents()` chain):

```csharp
using Microsoft.AspNetCore.DataProtection;
using ScormEditor.Services;

// --- SCORM editor services ---
builder.Services.AddSingleton<AssetVersion>();
builder.Services.Configure<ScormEditorOptions>(builder.Configuration.GetSection("ScormEditor"));
builder.Services.AddSingleton<ScormPackageStore>();
builder.Services.AddSingleton<AzureBlobService>();
builder.Services.AddScoped<AzureConnections>();
builder.Services.AddSingleton<ScormEditorLauncher>();

// Data Protection (encrypts saved SAS URLs in the browser). If the host already calls
// AddDataProtection(), keep ONE call and do not change its application name.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("ScormEditor");
if (builder.Configuration["ScormEditor:DataProtectionKeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 64 * 1024 * 1024);   // REQUIRED by the review step
```

After `var app = builder.Build();`, after `UseStaticFiles()`/`UseAntiforgery()` and **before**
`app.MapRazorComponents<App>()`:

```csharp
app.MapScormPreview();
```

If the host uses authentication, put the endpoints behind it, for example:

```csharp
app.MapGroup("").RequireAuthorization().MapScormPreview();
```

**Check:** `dotnet build` succeeds.

### Step 5: `appsettings.json`

Add a top-level section (merge, do not replace the file):

```json
"ScormEditor": {
  "MaxUploadMB": 500,
  "MaxOpenCourses": 20,
  "IdleHours": 8,
  "ReopenMinutes": 30,
  "DataProtectionKeysPath": ""
}
```

On IIS without a loaded user profile, set `DataProtectionKeysPath` to a folder only the app pool
can read.

### Step 6: `App.razor`

1. At the top: `@inject ScormEditor.Services.AssetVersion Assets`
2. In `<head>`, after the host's Bootstrap and open-iconic links:

```razor
<link rel="stylesheet" href="lib/codemirror/codemirror.css" />
<link rel="stylesheet" href="@Assets.Url("css/scorm-editor.css")" />
```

3. In `<body>`, **before** `_framework/blazor.web.js`:

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

The last line is only used by `AzureBlobPage.razor`; harmless otherwise.

4. Bootstrap 5 JS (`bootstrap.bundle.min.js`) must be loaded (the editor uses Bootstrap
   dropdowns). The Content Manager already loads it.

**Check:** host pages still render; no duplicate Bootstrap.

### Step 7: Render mode and route

- If the host's `App.razor` has `<Routes @rendermode="InteractiveServer" />`, nothing to do.
- Otherwise add `@rendermode InteractiveServer` to `ScormEditorPage.razor` right under
  `@namespace ScormEditor.Components.Pages`.
- If the host's router only scans its own assembly, nothing to do (the page is compiled into the
  host assembly). Make sure no other host page uses `/editor`; if one does, change the editor's
  `@page "/editor"` **and** every `"editor?open="` string in `ScormEditorLauncher.cs`, and tell the
  human.
- If the human wants a menu entry, add a `NavLink` to `editor` in the host's nav menu.

**Check:** run the host, open `/editor`, choose a course `.zip` from disk: the course title and
preview appear; browser console has no errors.

### Step 8: Wire the Edit button (choose 5A or 5B from section 4, question 1)

#### 8A. Courses in Azure Blob Storage

On the host page, generate a **read + write** SAS **on the server** and call the launcher:

```razor
@using Azure.Storage.Blobs
@using Azure.Storage.Sas
@inject ScormEditorLauncher Launcher
@inject NavigationManager Nav
@inject IConfiguration Config

<button class="btn btn-sm btn-primary" @onclick="() => Edit(course)">Edit</button>

@code {
    private void Edit(Course course)
    {
        var blob = new BlobClient(Config.GetConnectionString("CourseStorage"), "courses", course.BlobPath);
        var sas = blob.GenerateSasUri(BlobSasPermissions.Read | BlobSasPermissions.Write,
                                      DateTimeOffset.UtcNow.AddHours(8));   // must outlive the editing session
        Nav.NavigateTo(Launcher.EditorUrl(sas.ToString(),
                                          sourceName: "Content Manager",
                                          returnUrl: "content",            // the host page's route
                                          returnLabel: "Back to courses"));
    }
}
```

Adapt `Course`, `BlobPath`, the connection-string name and the container name to the host's real
model and configuration. If the host authenticates to Azure with Entra ID instead of an account
key, create a **user delegation SAS** (`BlobServiceClient.GetUserDelegationKeyAsync` +
`BlobSasBuilder`) instead of `GenerateSasUri`.

#### 8B. The host has the bytes

```razor
@inject ScormEditorLauncher Launcher
@inject NavigationManager Nav
@inject IServiceScopeFactory ScopeFactory

@code {
    private async Task Edit(int courseId)
    {
        byte[] zip; string name;
        using (var scope = ScopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
            var row = await db.Courses.FindAsync(courseId);
            (zip, name) = (row!.Package, row.FileName);
        }

        Nav.NavigateTo(Launcher.EditorUrl(zip, name,
            onSave: async edited =>
            {
                // Runs later, from the editor page. Do NOT use this page's components or
                // injected scoped services here; create a new scope.
                using var scope = ScopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ContentDbContext>();
                var row = await db.Courses.FindAsync(courseId)
                          ?? throw new InvalidOperationException("The course was deleted.");
                row.Package = edited;
                await db.SaveChangesAsync();
            },
            sourceName: "Content Manager", returnUrl: "content", returnLabel: "Back to courses"));
    }
}
```

An exception thrown in `onSave` is shown to the user as **"The course was not saved: <message>"**;
throw messages a user can understand. A "changed since opened" check is the host's job in 8B.

**Check (end-to-end):** click Edit → editor opens the course, address bar shows `/editor` with no
`?open` → click a heading in the preview, type → **Review and save** → **Save changes** →
**Save course** → dialog shows only *Back where it came from* and *Download*, button
**Replace file** → click it → message "Saved to …" → the stored course (blob or DB row) contains
the edit and **no new file/blob was created** → **Back to courses** returns to the host page.
Opening the same `editor?open=<code>` URL again shows "This link to open a course has expired or
was already used".

### Step 9: Deliver

Build, run the end-to-end check, commit on a branch, open a PR in the **host** repo describing
what was copied and changed, and tell the human. Do not merge without the human's go-ahead unless
they have asked for it.

## 6. Pitfalls (read before debugging)

| # | Symptom | Cause | Fix |
|---|---|---|---|
| P1 | `CS0246` for `SaveCourseDialog` / `AzureOpenDialog` | Missing `@using ScormEditor.Components.Shared` | Step 3 |
| P2 | Review dialog never opens on big pages; circuit disconnects | `MaximumReceiveMessageSize` not set | Step 4 |
| P3 | Preview is blank or edits fail with 404 when the host runs under a sub-path | `scorm-editor.js` uses `/scorm-preview/${id}` from the site root | In `scorm-editor.js` `setPackage`, build `base` from `document.baseURI` (e.g. `new URL("scorm-preview/" + id, document.baseURI).pathname`), then test |
| P4 | Random "course is no longer open" / launch code not found | Several servers behind a load balancer; state is in one server's memory | Sticky sessions (ARR affinity), or one instance |
| P5 | `onSave` throws `ObjectDisposedException` | `onSave` captured the launching page's scoped `DbContext` | Create a scope inside `onSave` (8B) |
| P6 | Azure save: "This request is not authorized…" | SAS lacks Write, or expired before the user saved | Read + Write, lifetime ≥ editing session |
| P7 | Editor layout cut off or double scrollbars | `.scorm-editor` height is `calc(100vh - 1.75rem - 1rem)`, sized for this app's 1.75rem header strip | Adjust that rule in `scorm-editor.css` to the host layout's header height |
| P8 | Old behaviour after updating JS | Browser cached old `scorm-editor.js` | Keep `AssetVersion.Url(...)` for the import and CSS link |
| P9 | Bootstrap dropdowns (Open course) don't open | `bootstrap.bundle.min.js` not loaded | Step 6.4 |
| P10 | Global key/drag handlers clash with the host | `scorm-editor.js` adds window-level `keydown`, drag/drop and `beforeunload` listeners while the editor is open, and sets `window.API` / `window.API_1484_11` | Check the host doesn't use those names |

## 7. Where things are (for code changes after the merge)

| You need to change… | Look in |
|---|---|
| How a course opens from the launcher | `ScormEditorPage.razor` `OnAfterRenderAsync` (block starting `if (OpenCode is { Length: > 0 } code)`) |
| What the Save dialog offers / how it saves | `Components/Shared/SaveCourseDialog.razor` (`ReplaceOnly`, `Save`, `SaveAzure`, `SaveHost`, `SaveLocal`) |
| Azure upload, `.OLD`, ETag, MD5 | `Services/AzureBlobService.cs` `SaveAsync` |
| How preview edits become file changes | `Services/EditMapper.cs` `Apply`; `Services/DraftPatcher.cs` |
| Limits | `appsettings.json` `ScormEditor`; `Services/ScormPackageStore.cs` (`ScormEditorOptions`) |
| Launch code lifetime | `Services/ScormEditorLauncher.cs` `Lifetime` |
| Preview/edit behaviour in the browser | `wwwroot/js/scorm-editor.js` |

Step-by-step flows with method names: guide section 6. One-line description of every file: guide
section 7.

## 8. Testing without real Azure

Azurite (the Azure Storage emulator) works for the Azure path:

```bash
npm install -g azurite
azurite-blob --location ./azurite --blobPort 10000 --loose --skipApiVersionCheck
```

Account `devstoreaccount1`, well-known key from the Azurite docs; connection string
`UseDevelopmentStorage=true`. Create a container, upload a course `.zip`, generate a SAS with
the account key, and run the Step 8 end-to-end check.

A minimal test course: a `.zip` with `imsmanifest.xml` (one organization, one item pointing at
`index.html`) and an `index.html` with a heading and a paragraph.
