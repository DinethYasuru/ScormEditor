# IFS Academy SCORM Editor: Full Guide

The complete reference for the SCORM editor: what it does and how to use it, how it is built,
how to run and configure it, how it works with Azure Blob Storage, and how to put it into another
Blazor app and open courses in it from there.

**Repository:** `DinethYasuru/ScormEditor` · **Stack:** .NET 8 Blazor Server (Interactive Server),
Bootstrap 5.3, open-iconic, CodeMirror 5, Azure.Storage.Blobs 12.30.1 · **Theme:** IFS Academy
Content Manager.

## Contents

1. [What it is](#1-what-it-is)
2. [Running it](#2-running-it)
3. [Using the editor](#3-using-the-editor)
4. [Opening and saving courses (computer and Azure Blob)](#4-opening-and-saving-courses-computer-and-azure-blob)
5. [Configuration and server limits](#5-configuration-and-server-limits)
6. [How it works inside](#6-how-it-works-inside)
7. [Code map (every file)](#7-code-map-every-file)
8. [Putting it into another Blazor app, and opening courses from it](#8-putting-it-into-another-blazor-app-and-opening-courses-from-it)
9. [Security](#9-security)
10. [Troubleshooting](#10-troubleshooting)
11. [Not built yet](#11-not-built-yet)

---

## 1. What it is

A web tool for changing SCORM courses (SCORM 1.2 and 2004 packages, including Articulate Rise and
Storyline exports) without unpacking them by hand. You open a course .zip, click into the course
as a learner would see it, type over text, change links, images and embeds, check the wording
against the IFS Academy word list, review exactly what will change, and save the course back where
it came from: a file on your computer, an Azure Blob container, or the app that sent it.

Pages of the app:

| Route | Page |
|---|---|
| `/` | Home: short introduction and links |
| `/editor` | **The SCORM editor** (`ScormEditorPage.razor`) |
| `/azure` | Azure Blob: saved containers (SAS URLs), browse and open courses |
| `/scorm-preview/{id}/{path}` | Not a page: serves an open course's files to the preview (GET) and takes Code-tab writes (PUT) |

## 2. Running it

Needs the .NET 8 SDK.

```
cd ScormEditor
dotnet run
```

Open http://localhost:5136/editor (the `http` profile in `Properties/launchSettings.json`;
`https` uses https://localhost:7220). Choose a course .zip, or drag one onto the page.

In VS Code, `.vscode/launch.json` has a launch configuration. To publish for IIS:
`dotnet publish -c Release`, then host as an ASP.NET Core app with WebSockets enabled (Blazor
Server uses SignalR) and set `ScormEditor:DataProtectionKeysPath` (section 5).

## 3. Using the editor

### 3.1 Layout

- **Toolbar:** course title and where it came from, the **changed files** badge, **Open course**,
  **Find and replace**, **ARR Word Check**, **Undo**, **Start over**, **Save course**, and the
  **Advanced** switch. A Back button appears when another app opened the course (section 8.5).
- **Tips strip:** the four steps (click and type; right-click to change links, buttons, video and
  images; Review and save; Save course). **Hide tips** hides it (remembered in the browser).
- **Left panel:** the course's pages (simple mode) or the course structure and file tree
  (Advanced). The arrow in its header folds it into a thin rail.
- **Work area:** the **Preview** tab, and in Advanced mode the **Code** tab.
- **Preview bar:** page picker, Reload, **Full / Tablet / Phone** widths, **Edit / Try it** modes.

**Simple mode** (default) shows only what is needed to change wording. **Advanced** adds the file
tree, Code tab, manifest tools, Live reload and the LMS panel. The choice is remembered.

### 3.2 Editing in the preview

The Preview opens in **Edit** mode: click any text and type. This works in frames inside the page
too (Rise, Storyline and most LMS wrappers put the content in an inner iframe). Switch to
**Try it** (Interact) to click through as a learner.

**Formatting toolbar:** paragraph style (paragraph, headings 1-4, quote), bold, italic, underline,
strikethrough, superscript, subscript, smaller/larger text, text colour, highlight, align left,
centre, right, justify, bullets, numbering, indent, outdent, link, clear formatting, undo, redo.
On Rise pages, formatting is saved into the paragraph's HTML in the course data. Text that comes
from a plain-text field (such as Rise's course title) can be retyped but not formatted; the review
says so.

**Right-click menu:**

| On | You can |
|---|---|
| A link | Edit its address, text and new-tab setting; open, copy or remove it |
| Selected text | Add a link |
| An embed (iframe, video, audio, object) | Edit its URL (and an iframe's title) |
| An image | Replace it (upload) or edit its alt text |
| A button | **Edit label…** (changed where the course keeps it, e.g. Rise's "Start Course"), **Hide this for learners…**, **Click this button** |
| Any element | Edit its HTML |

Embeds from other sites get a yellow dashed outline in Edit mode, because they can't be clicked
into. **Hide this for learners** adds a rule to `<style id="scorm-editor-hidden">` in the page's
`<head>`; **Show hidden elements again** removes that block.

**Buttons and links in Edit mode** don't leave the page, so their text can be edited.
**Ctrl+click** (Cmd+click on a Mac), or right-click **Go to this link**, follows one as a learner
would. With unsaved edits, going to another file opens the review first.

Edited text is highlighted yellow until it is saved.

### 3.3 Review and save

Edits in the preview are **drafts**. **Review and save** (or **Ctrl+S**) shows, per edited file, a
line-by-line diff with the changed words highlighted, and a list of every change with a tick box.
Untick a change to leave it out. Then **Save changes** (**Save ticked changes**), **Keep editing**,
or **Discard changes**.

Only what you changed is written. The rendered page is never saved, so things a course's scripts
add at run time (Rise's "Unstarted" labels, classes, generated markup) never end up in your
files. Each edit is looked for in this order (`EditMapper`):

1. the page's own HTML (static pages): the changed characters are patched in place;
2. anywhere else in the package where that text or attribute lives: JavaScript and JSON data files
   (Storyline), and base64-encoded course data inside a page (Rise), which the review shows
   decoded;
3. otherwise the edit is listed as **not found** and left out.

Text found in several files (a course title in the data, the page `<title>` and the manifest) is
changed in all of them, and the review says so. Anything that would reload the preview (another
page, Try it, Reload, opening another course) asks you to save or discard drafts first.

### 3.4 Undo, Start over, change log

- **Undo** (toolbar): every save, image swap, find-and-replace and file operation is one step; the
  tooltip says what it will undo. A step can't be undone once the same file was changed again in
  the Code tab (which has its own undo). Typing in the preview has the usual Ctrl+Z.
- **Start over** throws away every change since the course was opened.
- **Change log:** the **changed files** badge opens a log of what changed and when. The saved course
  includes it as `scorm-editor-changes.txt` (LMSs ignore files the manifest doesn't list).
- The browser asks before closing the tab while there are changes not yet saved.

### 3.5 Find and replace

Finds text learners see across every page, the manifest and the course data (Rise's encoded data,
Storyline's data files, .json files), with **match case** and **whole word**. Every place found is
listed with the text around it and a tick box; only ticked places are replaced. Markup, scripts,
styles, data keys and code are left alone: in script data only string values that read as text
count. One replace is one Undo step.

### 3.6 ARR Word Check

Points out words from a word list that should no longer be used, with the word to use instead. The
default list is the IFS Academy terminology list (37 pairs, `TermList.Default`). **Edit list**
changes it, one `old word<Tab>new word` per line, with match case and whole-word options; the list
is kept in the browser.

It counts each word across the whole course (pages and Rise/Storyline data, including tabs and
slides not on screen), highlights matches in the preview with the suggested word in a tip, has
previous/next buttons per word, lists the places in the course, and gives a copyable report.
Clicking a word or one of its places takes the preview there (the page and Rise lesson, scrolled
to the word). **It never replaces anything**, because swapping a word without reading the
sentence can change its meaning. The course-wide count runs in the background and is cached.

### 3.7 Advanced mode

- **Course structure:** reads `imsmanifest.xml` (SCORM 1.2 or 2004): version, the default
  organization's items and the launch page. Click an item to preview it.
- **Files:** every file in the package; changed files get a yellow dot. Add, upload, rename,
  delete and revert files.
- **Code tab:** CodeMirror for HTML, CSS, JavaScript, JSON and XML. Edits save as you type; with
  **Live** on, the preview reloads when you switch back to it.
- **Sync manifest:** rewrites the launch resource's `<file>` list to name every file in the
  package.
- **LMS panel:** the preview answers SCORM 1.2 (`window.API`) and 2004 (`window.API_1484_11`)
  calls and shows status, score, location and a log of every call.

### 3.8 Languages and encodings

Pages saved as UTF-16 or a legacy code page their HTML declares (Shift_JIS or EUC-JP for
Japanese, Windows-1252 or ISO-8859-1 for French and German) are read and written back in that same encoding (`TextEncoding`), so Japanese,
German and French courses keep their characters. Whole-word matching and HTML entities work in
CJK text too.

## 4. Opening and saving courses (computer and Azure Blob)

### 4.1 Opening

**Open course** offers:

- **From this computer:** in Chrome and Edge this uses the File System Access API, so the course can
  later be saved back into the same file. Other browsers upload a copy. You can also **drag a .zip
  onto the page**.
- **From Azure Blob:** pick a saved container and browse to a course, or **Paste a SAS URL** (a
  container SAS or a SAS for one .zip) to open a course once without saving the SAS.
- The **Azure Blob page** (`/azure`) also has **Browse → Open in editor**.
- **From another app** through `ScormEditorLauncher` (section 8.5).

After a refresh, or a visit to another page of the app, the same tab carries on with the course
(saved changes and undo included) for `ReopenMinutes`. Edits not yet saved in the preview are
lost on a refresh.

### 4.2 Saving

**Save course** writes the edited package as a .zip with `imsmanifest.xml` as the first entry.
Destinations:

| Destination | Notes |
|---|---|
| **Back where it came from** | The same blob, the same file on this computer (Chrome/Edge), or the app that sent it |
| **Azure Blob** | Any saved container and folder, with any file name |
| **A folder on this computer** | Chrome/Edge: choose a folder |
| **Download** | The browser's normal download |

Saving where a file already exists says so and needs a second click. **Keep the old file** (ticked
by default) first copies the existing file into a `.OLD` folder next to it as
`.OLD/name_yyyy-MM-dd_HH-mm-ss.zip` (never over an older copy), reads the copy back to check it,
and only then writes the new file.

**Courses opened by another app** (section 8.5) are different: they only ever overwrite the
original. The dialog offers only **Back where it came from** (button **Replace file**) and
**Download**, and no `.OLD` copy is made.

### 4.3 Azure safety

- The new file is uploaded only if the blob is still the one that was opened or checked (ETag), so
  someone else's save is never overwritten silently. If it changed, the dialog says **"Someone else
  changed this file after you opened it"** and offers **Replace anyway**.
- The upload is all or nothing, and the saved size and MD5 are checked afterwards.
- If any step fails, the original stays as it was, and the message says whether a .OLD copy was
  made.
- Nothing in the editor deletes a blob. A SAS never needs Delete.

### 4.4 The Azure Blob page and SAS URLs

Add a container with a name and a **container SAS URL**. The page shows where it points, which
permissions it has or lacks and when it expires; **Test** checks it against Azure; **Browse** lists
folders and opens a course.

Permissions: **Read, Create, Write, List** to open and save (**Read, List** to only open). Saved
SAS URLs are kept in the user's own browser (localStorage) **encrypted by the server** with ASP.NET
Core Data Protection (`ProtectedLocalStorage`), so each user has only their own and the stored
value is unreadable without the server's keys. They are never sent back to the browser in plain
text and never logged. If the server's keys change, saved containers can't be unlocked and the page
asks the user to add them again (see `DataProtectionKeysPath`).

### 4.5 Saving on this computer

In Chrome and Edge, **Back where it came from** writes to the original file and **A folder on this
computer** writes into a chosen folder. Keeping the old file asks for the folder that holds it
(browsers give access to the file only). The browser writes to a temporary file and swaps it in,
so a failed save leaves the old file as it was. A dragged-in course has no file to go back to.

## 5. Configuration and server limits

`appsettings.json`:

```json
"ScormEditor": {
  "MaxUploadMB": 500,
  "MaxOpenCourses": 20,
  "IdleHours": 8,
  "ReopenMinutes": 30,
  "DataProtectionKeysPath": ""
}
```

| Setting | Default | Meaning |
|---|---|---|
| `MaxUploadMB` | 500 | Largest course .zip that can be opened |
| `MaxOpenCourses` | 20 | Most courses open at once across all users (each is in memory); one more asks the user to try again later |
| `IdleHours` | 8 | A course nobody has used for this long is closed and its memory freed; its tab says so |
| `ReopenMinutes` | 30 | How long a course is kept after its editor page closes, so a refresh carries on |
| `DataProtectionKeysPath` | "" | Folder for the Data Protection keys, so saved Azure containers survive restarts and deployments. Set it on IIS without a loaded user profile, to a folder only the app can read |

`Program.cs` also raises the SignalR message limit to 64 MB
(`AddHubOptions(o => o.MaximumReceiveMessageSize = 64 * 1024 * 1024)`), because edited pages go to
the server for review over the Blazor connection.

## 6. How it works inside

### 6.1 The big picture

```
Browser                                         Server (Blazor Server app)
┌──────────────────────────────┐               ┌─────────────────────────────────────────┐
│ ScormEditorPage (Blazor UI)  │◄── SignalR ──►│ ScormEditorPage.razor (C#)              │
│  scorm-editor.js (JS module) │               │   ScormPackageStore ── ScormPackage      │
│   CodeMirror, edit mode,     │               │   EditMapper / DraftPatcher / TextDiff   │
│   LMS stub (window.API)      │               │   AzureBlobService ── Azure Blob Storage │
│  ┌────────────────────────┐  │  GET / PUT    │   ScormEditorLauncher (hand-off codes)   │
│  │ preview <iframe>       │──┼──────────────►│ /scorm-preview/{id}/{path}               │
│  │ the course, same origin│  │               │   (ScormPreviewEndpoints)                │
│  └────────────────────────┘  │               └─────────────────────────────────────────┘
└──────────────────────────────┘
```

### 6.2 Courses live in memory

Opening a course reads the .zip into a `ScormPackage`: a dictionary of package-relative paths to
bytes, plus the original bytes of every changed file, an undo stack, a change log and the parsed
manifest. `ScormPackageStore` (singleton) holds open packages by a random GUID, enforces
`MaxOpenCourses`, closes idle ones, and keeps a closed tab's course for `ReopenMinutes`
(Attach/Release). Nothing is written to the server's disk. The course's `CourseSource` records
where it came from (blob + SAS + ETag, local file handle key, or the launching app's save
function) and stays on the server.

### 6.3 The preview

The preview iframe loads `/scorm-preview/{id}/{launch page}`. `ScormPreviewEndpoints` serves each
file from memory with the right content type and `no-store`. Because the course loads from real
relative URLs on the **same origin** as the editor, it finds the stub LMS API through
`window.parent` and Storyline/Rise packages that fetch their own data files work as they would on
an LMS. Code-tab edits and image swaps are written with `PUT` to the same URL (so large files
don't go over SignalR); text arrives as UTF-8 and is stored in the file's own encoding.

### 6.4 From a preview edit to a saved file

1. In Edit mode, `scorm-editor.js` makes the page editable and records, for each element you edit,
   its HTML and text just before the first edit.
2. **Review and save** sends the drafts (before/after per element) to the server.
3. `EditMapper` finds where each change lives (page HTML, script/JSON data, Rise base64 data) and
   builds the new file contents; `DraftPatcher` patches static HTML in place; `TextDiff` (Myers
   line diff) makes the review.
4. The ticked changes are written in one `ScormPackage.Step` (one Undo step, one change-log entry),
   and the preview reloads.

### 6.5 Saving to Azure

`SaveCourseDialog` builds the zip (`ScormPackage.ToZip`) and calls `AzureBlobService.SaveAsync`:
optional `.OLD` copy (copied, read back, compared), then an upload conditional on the ETag, then a
size/MD5 check. The new ETag is kept so the next save checks against it.

### 6.6 Browser-side state

`localStorage`: Advanced on/off, tips shown, word list, saved Azure containers (encrypted).
`sessionStorage`: the id of the course this tab has open (for carrying on after a refresh).

## 7. Code map (every file)

| File | What it does |
|---|---|
| `Program.cs` | Services, Data Protection, SignalR limit, `MapScormPreview()`, Razor components |
| `appsettings.json` | `ScormEditor` limits (section 5) |
| `Components/App.razor` | HTML shell: Bootstrap, open-iconic, CodeMirror, editor CSS/JS, `blazor.web.js`; Interactive Server for all routes |
| `Components/Routes.razor`, `Layout/MainLayout.razor`, `Layout/NavMenu.razor` | Router and the Content Manager-style layout and left menu |
| `Components/Pages/Home.razor` | `/`, introduction |
| `Components/Pages/ScormEditorPage.razor` | `/editor`: the whole editor UI and its C# logic (open, preview, code, review, find/replace, word check, undo, save, launcher hand-off) |
| `Components/Pages/AzureBlobPage.razor` | `/azure`: saved containers, test, browse, open in editor |
| `Components/Pages/Error.razor` | Error page |
| `Components/Shared/SaveCourseDialog.razor` | Save course dialog: destinations, .OLD, ETag check, host save |
| `Components/Shared/AzureOpenDialog.razor` | Open from Azure: saved container or pasted SAS |
| `Components/Shared/AzureBrowser.razor` | Folder/file browser for a container |
| `Components/Shared/SasSummary.razor`, `ExpiryBadge.razor`, `AzurePermissionsNote.razor` | Show what a SAS points at, its permissions and expiry |
| `Services/ScormPackage.cs` | One open course in memory: files, originals, undo steps, change log, zip in/out, manifest sync |
| `Services/ScormPackageStore.cs` | Open courses by id, limits, idle clean-up; `ScormEditorOptions` |
| `Services/ScormManifest.cs` | Reads `imsmanifest.xml`: version, organization tree, launch page |
| `Services/ScormPreviewEndpoints.cs` | `GET`/`PUT /scorm-preview/{id}/{**path}` |
| `Services/EditMapper.cs` | Preview edits → file changes; find/replace; word-check scanning; Rise/Storyline data |
| `Services/DraftPatcher.cs` | Patches an edit into a page's HTML in place |
| `Services/TextDiff.cs` | Line diff with word highlights for the review |
| `Services/TextEncoding.cs` | Reads/writes text files in their own encoding |
| `Services/TermList.cs` | ARR Word Check list and its default IFS Academy pairs |
| `Services/CourseSource.cs` | Where a course came from and how to save it back |
| `Services/AzureSas.cs` | Parses a SAS URL: account, container, path, blob or container, permissions, expiry |
| `Services/AzureBlobService.cs` | List, test, download and save blobs (ETag, .OLD, MD5 check); never deletes |
| `Services/AzureConnections.cs` | The user's saved containers, encrypted in the browser |
| `Services/ScormEditorLauncher.cs` | Hand-off from another page: one-time codes for a blob or zip bytes |
| `Services/AssetVersion.cs` | `?v=` cache-busting for the editor's JS/CSS |
| `wwwroot/js/scorm-editor.js` | JS module: CodeMirror, edit mode, toolbar, right-click menu, drafts, LMS stub, word-check highlights, local file saving |
| `wwwroot/css/scorm-editor.css` | Editor styles |
| `wwwroot/lib/codemirror/` | CodeMirror 5 core, xml/javascript/css/htmlmixed modes, 3 addons |
| `docs/` | This guide |

## 8. Putting it into another Blazor app, and opening courses from it

This part covers moving **only the editor page** into another Blazor Server app (.NET 8), such as
the Content Manager, and **opening a course in it from that app**, for example from an "Edit"
button next to a course.

### 8.1 What to copy

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
you don't bring that page, users can still open and save courses sent from your app (section 8.5);
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

### 8.2 Program.cs

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

### 8.3 appsettings.json

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

### 8.4 App.razor (styles and scripts)

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

### 8.5 Opening a course from your app

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

#### A. The course is in Azure Blob Storage

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

#### B. Your app sends the course .zip itself

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

### 8.6 How saving works

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

### 8.7 Things to know

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

### 8.8 Checklist

1. Copy `Services/`, `Components/Pages/ScormEditorPage.razor`, `Components/Shared/`,
   `wwwroot/js/scorm-editor.js`, `wwwroot/css/scorm-editor.css`, `wwwroot/lib/codemirror/`.
2. Add the `Azure.Storage.Blobs` NuGet package.
3. Add the two `@using` lines to `_Imports.razor`.
4. In `Program.cs`, register the 6 services and Data Protection, set `MaximumReceiveMessageSize`,
   and call `app.MapScormPreview()`.
5. Add the `ScormEditor` section to `appsettings.json`.
6. In `App.razor`, add the 2 stylesheets and 8 CodeMirror scripts (plus `@inject AssetVersion`).
7. Make sure `/editor` runs as Interactive Server.
8. In your course list, make the Edit button call `Launcher.EditorUrl(...)` (section 8.5) and
   `Nav.NavigateTo(...)` the result.
9. Put `/editor` and `/scorm-preview` behind your sign-in.
10. Test: Edit → change some text → Review and save → Save course → Replace file → check the stored
    course has the change → Back to courses.

## 9. Security

- **Sign-in:** the app has no sign-in of its own yet. The `/scorm-preview` endpoints are reachable
  by anyone who knows a package id (a random GUID). Put `/editor`, `/azure` and `/scorm-preview`
  behind the host app's sign-in before going live.
- **SAS URLs** are secrets: saved ones are encrypted in the browser by the server's Data Protection
  keys, launch hand-offs keep them on the server (only a one-time code goes in the address), and
  they are never logged. Give a SAS only the permissions it needs and a sensible expiry.
- **Course content runs in the browser:** the preview runs the course's own scripts on the editor's
  origin (that is how the LMS stub works). Open courses you trust.
- **Nothing is deleted** in Azure, and overwrites are ETag-checked.

## 10. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| "The server already has 20 courses open" | `MaxOpenCourses` reached. Close finished courses, wait for idle ones to close, or raise the limit (memory permitting) |
| Course too big to open | Raise `MaxUploadMB` |
| Review fails on a big page / connection drops | `MaximumReceiveMessageSize` missing from `AddHubOptions` |
| Editor fails with a closed connection after an update | Old cached JS: make sure `scorm-editor.js` is loaded through `AssetVersion.Url` |
| Saved Azure containers gone after restart / "can't be unlocked" | Data Protection keys changed: set `DataProtectionKeysPath` |
| "This request is not authorized…" on save | SAS lacks Write/Create, or has expired. Make a new SAS |
| "Someone else changed this file after you opened it" | The blob changed since opening. Replace anyway, or download your copy and merge |
| "This link to open a course has expired or was already used" | Launch code older than 5 minutes or reused. Click Edit again |
| "The course was not saved: …" (course from another app) | That app's `onSave` threw; the message is its exception message |
| An edit is listed as "not found" in the review | The text is built in a way the editor can't trace back to a file. Change it in the Code tab (Advanced) |
| Preview edits don't save under a sub-path site | `scorm-editor.js` posts to `/scorm-preview` from the site root (section 8.7) |
| "Save back to the same file" missing for a local course | Only Chrome/Edge, and not for dragged-in courses |

## 11. Not built yet

- Sign-in (to come from the host app).
- Spell check.
- Packaging as a Razor Class Library (section 8.7).
