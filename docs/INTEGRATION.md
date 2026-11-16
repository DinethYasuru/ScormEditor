# SCORM Editor integration: the full flow and every integration point

This document follows **one course** from the moment a user clicks **Edit** in the host app (such
as the IFS Academy Content Manager) to the moment they are back in the host app with the course
saved. At each stage it says what the user sees, what the **host app** does, what the **editor**
does (file and method), what data crosses between them, and what can go wrong.

The places where the host app and the editor touch are called **integration points** and are
numbered **IP1 to IP9**. The table in section 2 lists them all; section 3 walks through the flow.

**Reading order for someone integrating the editor:** this file (understand the flow) →
[`API-REFERENCE.md`](API-REFERENCE.md) (what to call) → [`LLM-MERGE-PLAYBOOK.md`](LLM-MERGE-PLAYBOOK.md)
(do it, step by step, with checks). Section 7 below lists what these documents **can't** know
about your host app, so decide those first.

Related documents:

| Document | Use it for |
|---|---|
| [`API-REFERENCE.md`](API-REFERENCE.md) | Every method and parameter the host calls, with examples |
| [`LLM-MERGE-PLAYBOOK.md`](LLM-MERGE-PLAYBOOK.md) | Step-by-step installation into the host app (files, Program.cs, App.razor) |
| [`SCORM-Editor-Guide.md`](SCORM-Editor-Guide.md) | Everything else: features, internal flows (section 6), every file (section 7) |

---

## 1. The whole flow on one page

```
 USER              HOST APP (your pages + code)            EDITOR (same app, /editor)                  STORAGE
 ────              ────────────────────────────            ──────────────────────────                  ───────
                   ── one-time setup ── IP1 (Program.cs, App.razor, appsettings, sign-in)

 clicks Edit ───►  [S1] find the course
                   [S2] prepare access ─────────────────────────────────────────────── IP2 ──────────►  make SAS
                        (SAS URL, or read the .zip bytes)                                               or read .zip
                   [S3] url = Launcher.EditorUrl(...) ── IP3 ──► ScormEditorLauncher
                                                                  keeps request, returns
                        ◄──────────────────────────────────────── "editor?open=<code>"
                   [S4] Nav.NavigateTo(url) ─────────── IP4 ──►  /editor?open=<code>
                                                                 (sign-in check: IP5)
                                                                [S5] Take(code) once
                                                                     Azure: download blob ── IP6 ─────►  GET blob (+ETag)
                                                                     Bytes: use the zip
                                                                     course held in memory
                                                                     preview iframe loads
                                                                     /scorm-preview/{id}/… ── IP7 (endpoints)
 edits text   ◄──────────────────────────────────────────────►  [S6] edit → Review and save
                                                                     (all inside the editor)
 Save course
 → Replace file ───────────────────────────────────────────────►[S7] build edited .zip
                                                                     Azure: upload if ETag ── IP8 ────►  PUT blob
                                                                       unchanged
                                                                     Bytes: await onSave(zip) ─ IP8 ──►  your storage
                                                                     "Saved to …"
 Back to courses ──────────────────────────────── IP9 ◄───────  [S8] Back button → returnUrl
                   [S9] show the list again
                        (reload so it shows the new version)
                                                                [S10] course stays in memory for
                                                                      ReopenMinutes, then removed
```

Everything runs in **one ASP.NET Core app**: the host's pages and the editor page share the same
server process and the same singletons. That is what makes the hand-off (IP3/IP4) possible
without passing secrets through the browser.

---

## 2. Integration points at a glance

| IP | Name | When | Owner | What crosses | Contract |
|---|---|---|---|---|---|
| **IP1** | Setup | Once, at startup | Host developer | Service registrations, endpoints, config, CSS/JS tags | `Program.cs`, `App.razor`, `appsettings.json` ([playbook steps 2-7](LLM-MERGE-PLAYBOOK.md#5-steps)) |
| **IP2** | Course access | Each Edit click | Host code | Host → storage: create a SAS, or read the zip bytes | SAS with **read + write**, valid for the editing session; or a `byte[]` |
| **IP3** | Hand-off | Each Edit click | Host code → editor | SAS + blob name, or zip + file name + `onSave`; `sourceName`, `returnUrl`, `returnLabel` | `ScormEditorLauncher.EditorUrl(...)` returns `"editor?open=<code>"` |
| **IP4** | Navigation | Right after IP3 | Host code | Only the one-time **code**, in the URL | `NavigationManager.NavigateTo(url)` or a link; code valid **5 min, once** |
| **IP5** | Sign-in | Every request to the editor | Host app | The host's auth cookie / identity | `/editor`, `/scorm-preview`, `/scorm-export` behind the host's authorization |
| **IP6** | Read the course | Editor opens the course | Editor → storage | Azure: blob download + ETag. Bytes: nothing (already given) | `AzureBlobService.DownloadAsync` with the SAS from IP3 |
| **IP7** | Preview endpoints | While editing | Editor (browser ↔ server) | Course files over HTTP, same origin | `GET/PUT /scorm-preview/{id}/{**path}`, `GET /scorm-export/{id}` (mapped by `MapScormPreview()` in IP1) |
| **IP8** | Save back | User presses **Replace file** | Editor → storage or host code | The whole edited `.zip` | Azure: conditional upload over the same blob. Bytes: `await onSave(zip)` |
| **IP9** | Return | User presses **Back** | Editor → host page | Nothing but navigation | Link to `returnUrl` from IP3 |

---

## 3. The flow, stage by stage

### Stage 0: one-time setup (IP1)

Done once by a developer when the editor is added to the host app. Full steps:
[`LLM-MERGE-PLAYBOOK.md` steps 1-7](LLM-MERGE-PLAYBOOK.md#5-steps). In short:

```csharp
// Program.cs
builder.Services.AddSingleton<AssetVersion>();
builder.Services.Configure<ScormEditorOptions>(builder.Configuration.GetSection("ScormEditor"));
builder.Services.AddSingleton<ScormPackageStore>();
builder.Services.AddSingleton<AzureBlobService>();
builder.Services.AddScoped<AzureConnections>();
builder.Services.AddSingleton<ScormEditorLauncher>();      // ← the hand-off service (IP3)
builder.Services.AddDataProtection().SetApplicationName("ScormEditor");
builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 64 * 1024 * 1024);
...
app.MapScormPreview();                                     // ← the endpoints (IP7)
```

Plus the `ScormEditor` section in `appsettings.json`, and the CodeMirror and editor CSS/JS tags in
`App.razor`.

**Check:** `/editor` opens and can open a `.zip` from disk.

---

### Stage 1: the user picks a course in the host app

**User sees:** the host's course list, with an **Edit** button per course.
**Host does:** renders its own list (from Azure, a database, a file share). Nothing editor-specific
yet, except the button:

```razor
@inject ScormEditorLauncher Launcher
@inject NavigationManager Nav

<button class="btn btn-sm btn-primary" @onclick="() => Edit(course)">Edit</button>
```

**Integration points:** none yet.

---

### Stage 2: the host prepares access to the course (IP2)

The host decides **how the editor will read and write the course**. Two options:

#### Option A: the course is a blob in Azure; give the editor a SAS

```csharp
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

var blob = new BlobClient(Config.GetConnectionString("CourseStorage"), "courses", course.BlobPath);
Uri sas = blob.GenerateSasUri(
    BlobSasPermissions.Read | BlobSasPermissions.Write,     // read to open, write to save
    DateTimeOffset.UtcNow.AddHours(8));                      // must outlive the editing session
```

| Rule | Why |
|---|---|
| Read **and** Write | Read opens the course (IP6), Write saves it (IP8). Delete is never needed. |
| Lifetime ≥ an editing session (e.g. 8 h, matching `IdleHours`) | The editor saves with this same SAS, possibly hours later. The 5-minute limit in IP4 applies only to the code, not the SAS. |
| Created on the **server** | The SAS is a password for that blob. It must never be sent to the browser. |

A container SAS plus the blob's path works too (pass `blobName` in IP3).

#### Option B: the host reads the course itself and will store it itself

```csharp
byte[] zip  = await File.ReadAllBytesAsync(path);        // or from a database, your own Azure code…
string name = Path.GetFileName(path);
Func<byte[], Task> onSave = edited => File.WriteAllBytesAsync(path, edited);   // used in IP8
```

**What can go wrong:** the host can't reach its storage, so it shows its own error and doesn't
navigate.

---

### Stage 3: the host hands the course to the editor (IP3)

```csharp
// Option A
string url = Launcher.EditorUrl(
    sasUrl:      sas.ToString(),
    sourceName:  "Content Manager",       // shown in the editor: "Content Manager › finance/intro.zip"
    returnUrl:   "content",               // Back button target (IP9)
    returnLabel: "Back to courses");

// Option B
string url = Launcher.EditorUrl(zip, name, onSave,
    sourceName: "Content Manager", returnUrl: "content", returnLabel: "Back to courses");

// url == "editor?open=9f2c4a7e1b3d5f60718293a4b5c6d7e8"
```

**Editor does** (`Services/ScormEditorLauncher.cs`):

1. Option A: parses the SAS (`AzureSas.TryParse`); works out the blob name (from a blob SAS, or
   `blobName` with a container/folder SAS). Throws `ArgumentException` if it can't.
   Option B: checks `zip` isn't empty and `onSave` isn't null.
2. Makes a random 32-character code, stores a `Request` under it **in server memory**, valid for
   `ScormEditorLauncher.Lifetime` (**5 minutes**).
3. Returns `"editor?open=" + code`.

**What crosses:** host → launcher: the SAS (or zip + `onSave`) and the display options. Nothing is
sent to the browser yet. Nothing has been read from Azure yet.

**What can go wrong:** a malformed SAS or a missing blob name throws `ArgumentException`
([messages](API-REFERENCE.md#21-editorurl-for-a-course-in-azure-blob-storage)); catch it and show
the message.

---

### Stage 4: navigation to the editor (IP4, IP5)

```csharp
Nav.NavigateTo(url);                   // same tab, same Blazor circuit
// or, for a new tab:  <a href="@url" target="_blank">Edit</a>  (create the code on click)
```

**What crosses:** only `editor?open=<code>` in the address bar. The code alone is useless after 5
minutes or after one use.

**IP5, sign-in:** the request for `/editor` (and later `/scorm-preview/...`, `/scorm-export/...`)
goes through the host's authentication like any other page. The editor has no sign-in of its own;
protect it with the host's (`[Authorize]` on the page, `RequireAuthorization()` on
`MapScormPreview()`).

**Sub-path hosting:** if the host runs under a sub-path (`https://server/academy/`), the relative
URL `editor?open=…` resolves under it, and the editor's script builds its `/scorm-preview` URLs
from `<base href>`. Only `<base href="/academy/" />` must be right (tested).

---

### Stage 5: the editor opens the course (IP6, IP7)

**User sees:** the editor with the course title, the source (`Content Manager › finance/intro.zip`),
the course in the preview, and a **Back to courses** button.

**Editor does** (`Components/Pages/ScormEditorPage.razor`, `OnAfterRenderAsync`):

1. Loads its JavaScript module (`wwwroot/js/scorm-editor.js`, `init`).
2. Reads `open` from the query string (`[SupplyParameterFromQuery] OpenCode`) and removes it from
   the address bar (so a refresh doesn't try the used code again).
3. `Launcher.Take(code)` returns the request **once**. If it's unknown, used or expired, the
   editor shows *"This link to open a course has expired or was already used. Go back and choose
   Edit again."* and stops.
4. Remembers `returnUrl` / `returnLabel` for the Back button (IP9).
5. **IP6, reading the course:**
   - Option A: `OpenFromAzure(sas, blobName, sourceName, replaceOnly: true)` →
     `AzureBlobService.DownloadAsync` downloads the blob and its **ETag** (kept for IP8).
   - Option B: the zip from the request is used as is.
6. `OpenCourse` → `ScormPackage.FromZip` unzips it into memory and reads `imsmanifest.xml`;
   `CourseSource` records where it came from (`Sas`, `BlobName`, `ETag` or `HostSave = onSave`),
   with **`ReplaceOnly = true`**.
7. `OpenPackage` registers it in `ScormPackageStore` under a new GUID and points the preview at it.
8. **IP7, preview endpoints:** the preview iframe loads `/scorm-preview/{id}/{launch page}`, and
   the course's own files load from the same place. These endpoints were mapped in IP1.

**What crosses:** Azure → editor: the course bytes and ETag (Option A). Browser ↔ editor: course
files over `/scorm-preview`.

**What can go wrong:**

| Problem | User sees | Cause |
|---|---|---|
| SAS lacks Read, expired, or blob missing | *"Could not reach the storage account…"* or an Azure message | IP2 |
| Course bigger than `MaxUploadMB` | *"intro.zip is 812.0 MB. The largest course the editor can open is 500 MB."* | Config |
| `MaxOpenCourses` reached | *"The server already has 20 courses open…"* | Config / load |
| Not a zip | *"intro.zip is not a readable .zip file."* | The stored file |
| Preview blank | `/scorm-preview` not mapped, blocked by auth, or `<base href>` wrong under a sub-path | IP1 / IP5 |

---

### Stage 6: the user edits (inside the editor)

**User does:** clicks text in the preview and types, formats, changes links or images, uses Find
and replace, checks words with ARR Word Check, presses **Review and save** and **Save changes**.

**Editor does:** keeps the edits as drafts in the browser, maps them to the course's source files
(`EditMapper.Apply`), shows a diff, and writes the ticked changes into the in-memory course
(`ScormPackage.Step`, one Undo step each). Details:
[guide section 6.7](SCORM-Editor-Guide.md#67-editing-in-the-preview--review--save-the-main-flow).

**Integration points:** none. Nothing is written to Azure or to the host during editing. Until
the user saves the course (stage 7), every change lives only in the server's memory.

**Refresh:** the same tab carries on with the course for `ReopenMinutes` (saved changes kept;
unsaved preview drafts lost).

---

### Stage 7: saving back (IP8)

**User does:** **Save course**. Because the course came through the launcher (`ReplaceOnly`), the
dialog offers only:

- **Back where it came from**, with the warning *"Saving replaces intro.zip with your changes. No
  copy of the old file is kept."* and the button **Replace file**;
- **Download** (a copy to the user's computer; nothing is written to storage).

**Editor does** (`Components/Shared/SaveCourseDialog.razor`, `Save`):

1. `flush()` (JS) sends any Code-tab text not yet sent.
2. `ScormPackage.ToZip()` builds the edited `.zip`: `imsmanifest.xml` first, all files, plus
   `scorm-editor-changes.txt` (what was changed and when).
3. **IP8, writing back:**
   - **Option A** → `SaveAzure` → `AzureBlobService.SaveAsync`:
     uploads over **the same blob**, **only if its ETag is still the one read in IP6**, then
     checks size and MD5. No `.OLD` copy. Keeps the new ETag, so saving again works.
   - **Option B** → `SaveHost` → **`await onSave(zip)`**: your code from IP2 stores it.
4. Shows *"Saved to Content Manager › finance/intro.zip. It replaced the file that was there."*

**What crosses:** editor → storage (A) or editor → host code (B): the whole edited `.zip`.

**What can go wrong:**

| Problem | User sees | What happens |
|---|---|---|
| Someone else saved the blob since IP6 (A) | *"Someone else changed this file after you opened it…"* + **Replace anyway** | Nothing written until the user chooses |
| SAS expired or lacks Write (A) | Azure error message | Nothing written; the course stays open, so the user can Download |
| `onSave` throws (B) | *"The course was not saved: \<exception message\>"* | Course stays marked as changed; user can try again |
| Network drops mid-upload (A) | Error | Upload is all-or-nothing; the blob stays as it was |

For Option B, a "changed by someone else" check is the host's job inside `onSave`
([example](API-REFERENCE.md#22-editorurl-for-a-course-zip-your-app-already-has)).

---

### Stage 8: returning to the host app (IP9)

**User does:** clicks **Back to courses** (the `returnLabel` from IP3).

**Editor does:** it's a plain link to `returnUrl`. If there are unsaved changes, the editor doesn't
block the link, but the course stays in memory for `ReopenMinutes`, so opening `/editor` again in
the same tab carries on with it.

**Host does (stage 9):** shows its list again. **Reload the course data** (for example the blob's
last-modified time or your DB row) so the list shows the new version. A Blazor page reached by
navigation runs `OnInitializedAsync` again, so a list loaded there is fresh.

---

### Stage 10: clean-up (no integration point)

- When the editor page closes, `ScormPackageStore.Release` starts the `ReopenMinutes` timer.
  After it, or after `IdleHours` without use, the course is removed from memory.
- Launch codes are single use and expire after 5 minutes; expired ones are removed on the next
  `EditorUrl`/`Create`.
- The SAS from IP2 simply expires at its `se=` time; the editor keeps no copy after the course is
  removed.

---

## 4. Data and lifetimes

| Item | Created in | Lives in | Lifetime | Seen by the browser? |
|---|---|---|---|---|
| SAS URL (A) | IP2, host | Launcher request, then `CourseSource` | Its own expiry; dropped with the course | **No** |
| Zip bytes + `onSave` (B) | IP2, host | Launcher request, then `CourseSource` | Dropped with the course | **No** |
| One-time code | IP3 | Launcher (server memory) | 5 minutes, single use | Yes (address bar), useless after use |
| Course files | IP6 | `ScormPackage` in `ScormPackageStore` | Until closed + `ReopenMinutes`, or `IdleHours` unused | Yes, via `/scorm-preview` (needed for the preview) |
| ETag (A) | IP6, updated at each save | `CourseSource` | With the course | No |
| Edited `.zip` | IP8 | Storage (A) or your `onSave` (B) | Permanent | Only if the user chooses Download |

## 5. Testing each integration point

| IP | Test | Expected |
|---|---|---|
| IP1 | Open `/editor`, open a `.zip` from disk | Course appears, no console errors |
| IP2 | Generate a SAS for a test blob and paste it into a browser | The .zip downloads (proves Read and that the SAS is valid) |
| IP3 | Call `EditorUrl` with a broken SAS | `ArgumentException` with a clear message |
| IP4 | Click Edit; then reload the editor URL with the same `?open=` | First: course opens, `?open` disappears. Second: "expired or already used" |
| IP5 | Open `/editor` and `/scorm-preview/<guid>/x` signed out | Host's sign-in / 401 |
| IP6 | Edit a course | Title, source and preview appear |
| IP7 | Look at the network tab | `/scorm-preview/{id}/…` requests return 200 |
| IP8 | Change a heading, Review and save, Save course, Replace file | Blob/DB has the change; **no new blob/file created**; saving again also works |
| IP8 | Change the blob from elsewhere, then save | "Someone else changed this file…" |
| IP8 | Make `onSave` throw | "The course was not saved: …" |
| IP9 | Click Back to courses | Host list, showing the new version |

For Azure tests without a real account, use Azurite
([playbook section 8](LLM-MERGE-PLAYBOOK.md#8-testing-without-real-azure)).

## 6. Complete host page (Option A)

```razor
@page "/content"
@using Azure.Storage.Blobs
@using Azure.Storage.Sas
@inject ScormEditorLauncher Launcher
@inject NavigationManager Nav
@inject IConfiguration Config

<h3>Courses</h3>
@if (error is not null) { <div class="alert alert-danger">@error</div> }
<table class="table">
    @foreach (var c in courses)
    {
        <tr>
            <td>@c.Name</td>
            <td>@c.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")</td>
            <td><button class="btn btn-sm btn-primary" @onclick="() => Edit(c.Name)">Edit</button></td>
        </tr>
    }
</table>

@code {
    private record Course(string Name, DateTimeOffset? Modified);
    private readonly List<Course> courses = [];
    private BlobContainerClient container = default!;
    private string? error;

    // Stage 1 and stage 9: (re)load the list, so it shows the saved version after Back.
    protected override async Task OnInitializedAsync()
    {
        container = new BlobContainerClient(Config.GetConnectionString("CourseStorage"), "courses");
        await foreach (var b in container.GetBlobsAsync())
            if (b.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                courses.Add(new Course(b.Name, b.Properties.LastModified));
    }

    private void Edit(string blobName)
    {
        try
        {
            // IP2: read + write SAS for this blob, valid for the editing session.
            var sas = container.GetBlobClient(blobName).GenerateSasUri(
                BlobSasPermissions.Read | BlobSasPermissions.Write, DateTimeOffset.UtcNow.AddHours(8));

            // IP3: hand-off. IP4: navigate with the one-time code.
            Nav.NavigateTo(Launcher.EditorUrl(
                sasUrl:      sas.ToString(),
                sourceName:  "Content Manager",
                returnUrl:   "content",           // IP9
                returnLabel: "Back to courses"));
        }
        catch (ArgumentException ex) { error = ex.Message; }
    }
}
```

## 7. Requirements, and what these documents can't know about your host app

### 7.1 Requirements the host app must meet

| Requirement | Why | If not met |
|---|---|---|
| ASP.NET Core **.NET 8** (or later) **Blazor Web App / Blazor Server** | The editor is an Interactive Server component and uses server memory | Blazor WebAssembly-only or MVC hosts need a Blazor Server area added first; not covered here |
| Editor runs **in the same process** as the page with the Edit button | Launch codes and open courses are in that process's memory | Two separately deployed apps can't hand off this way. Options: host the editor inside the host app (recommended), or link to a separately deployed editor and add a server-to-server hand-off (not built) |
| **One instance**, or sticky sessions | Same reason | Users get "expired or already used" or "course is no longer open" when a request reaches another server |
| WebSockets allowed (IIS: WebSocket Protocol feature on) | Blazor Server's connection | Falls back to long polling (slower) or fails behind some proxies |
| Bootstrap 5 CSS + `bootstrap.bundle.min.js` | Dropdowns and layout | Open course menu doesn't open |
| Memory: at least each open course's unzipped size, plus copies of changed files (for Undo) and the zip built when saving | Courses are held in memory | Lower `MaxOpenCourses` / `MaxUploadMB` |

### 7.2 Decisions the host team must make (the documents can't make them for you)

| Decision | Options | Where it goes |
|---|---|---|
| Where courses live and how the editor reaches them | A: Azure + SAS (editor reads/writes the blob). B: host reads/writes (zip + `onSave`) | IP2, IP3, IP8 |
| How the host gets Azure credentials | Account key (connection string) → `GenerateSasUri`; Entra ID / managed identity → user delegation SAS | IP2 |
| Who may edit which course | The editor has no roles. Check permission **before** calling `EditorUrl` (and protect `/editor` and `/scorm-preview` with the host's sign-in) | Stage 1, IP5 |
| Two people editing the same course | Option A: the ETag check stops the second save and offers Replace anyway. Option B: your `onSave` must check a version | IP8 |
| Whether to keep old versions | The launcher path overwrites with no `.OLD` copy (owner's decision). For history, use Azure blob versioning / soft delete, or keep a copy in `onSave` | IP8 |
| Where the editor appears | Full page at `/editor` (default) with Back button; menu entry optional | IP4, IP9 |
| Layout height | `.scorm-editor` fills `100vh - 1.75rem - 1rem` (this app's header strip). Adjust in `scorm-editor.css` to the host's header | Playbook pitfall P7 |
| Whether to bring the Azure Blob page (`/azure`) | Only needed for users opening courses by hand from containers | Playbook step 1 |

### 7.3 Things that are not covered (yet)

- **The host app's own code:** these documents were written without access to the Content
  Manager's source. Model names (`Course`, `BlobPath`, `ContentDbContext`), connection-string
  names and routes in the examples are placeholders; replace them with the host's real ones.
- **A Razor Class Library / NuGet package:** integration is by copying files (playbook step 1).
- **A separately deployed editor** (different site or server from the host): not supported; see
  7.1.
- **Automated tests:** the repository has no test project; the checks in section 5 and the
  playbook are manual or Playwright-based.
- **Sign-in inside the editor:** it relies entirely on the host's (IP5).

