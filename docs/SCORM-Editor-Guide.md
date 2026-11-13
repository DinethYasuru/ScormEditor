# IFS Academy SCORM Editor: Full Guide

The complete reference for the SCORM editor: what it does and how to use it, how it is built,
how to run and configure it, how it works with Azure Blob Storage, and how to put it into another
Blazor app and open courses in it from there.

**Repository:** `DinethYasuru/ScormEditor` · **Stack:** .NET 8 Blazor Server (Interactive Server),
Bootstrap 5.3, open-iconic, CodeMirror 5, Azure.Storage.Blobs 12.30.1 · **Theme:** IFS Academy
Content Manager.

> **AI agents:** for the merge into another app, follow [`LLM-MERGE-PLAYBOOK.md`](LLM-MERGE-PLAYBOOK.md)
> (exact steps, invariants and checks). This guide is the reference it cites.
> **Every method, parameter and example** for connecting another app: [`API-REFERENCE.md`](API-REFERENCE.md).

## Contents

1. [What it is](#1-what-it-is)
2. [Running it](#2-running-it)
3. [Using the editor](#3-using-the-editor)
4. [Opening and saving courses (computer and Azure Blob)](#4-opening-and-saving-courses-computer-and-azure-blob)
5. [Configuration and server limits](#5-configuration-and-server-limits)
6. [How it works: the flows step by step](#6-how-it-works-the-flows-step-by-step)
7. [The files, one by one](#7-the-files-one-by-one)
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
| `/scorm-export/{id}` | Not a page: the edited course as a .zip (Download and saving to this computer) |

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

## 6. How it works: the flows step by step

Each flow below is written as **who does what, in which file and method**. "Page" means
`Components/Pages/ScormEditorPage.razor` (its `@code` block runs on the server); "JS" means
`wwwroot/js/scorm-editor.js` (runs in the browser); "store" means the singleton
`ScormPackageStore`.

### 6.1 The big picture

```
 BROWSER                                              SERVER (one ASP.NET Core process)
┌──────────────────────────────────────┐            ┌──────────────────────────────────────────────┐
│ /editor page (Blazor UI, HTML)       │  SignalR   │ ScormEditorPage.razor  (C#, one per tab)      │
│                                      │◄──────────►│   ├─ ScormPackageStore (singleton)            │
│ scorm-editor.js  (JS module)         │  (Blazor   │   │    └─ ScormPackage (one per open course)  │
│   CodeMirror (Code tab)              │  circuit)  │   ├─ EditMapper / DraftPatcher / TextDiff     │
│   Edit mode, toolbar, right-click    │            │   ├─ AzureBlobService ───────► Azure Blob     │
│   drafts, word-check highlights      │            │   ├─ AzureConnections (saved SAS, encrypted)  │
│   LMS stub: window.API / API_1484_11 │            │   └─ ScormEditorLauncher (hand-off codes)     │
│  ┌────────────────────────────────┐  │  HTTP      │                                              │
│  │ preview <iframe>: the course   │──┼──GET──────►│ ScormPreviewEndpoints                        │
│  │ loaded from /scorm-preview/... │  │  PUT ─────►│   GET  /scorm-preview/{id}/{**path}          │
│  └────────────────────────────────┘  │  GET ─────►│   PUT  /scorm-preview/{id}/{**path}          │
│                                      │            │   GET  /scorm-export/{id}   (the .zip)        │
└──────────────────────────────────────┘            └──────────────────────────────────────────────┘
```

Two channels connect browser and server:

- **The Blazor circuit (SignalR):** every button click, every call from C# to JS
  (`module.InvokeVoidAsync("…")`) and from JS back to C# (`dotnet.invokeMethodAsync("On…")`).
- **Plain HTTP:** the preview iframe loads course files with `GET`, the Code tab and image swaps
  write files with `PUT`, and Download fetches the zip. Large data goes this way so it isn't
  limited by SignalR's message size.

Everything about an open course lives in **server memory** in a `ScormPackage`, found by its id
(a random GUID). Nothing is written to the server's disk.

### 6.2 Starting the editor page

1. The browser opens `/editor`. Blazor renders `ScormEditorPage` interactively.
2. **Page `OnAfterRenderAsync(firstRender)`** imports the JS module
   (`import("./js/scorm-editor.js?v=…")`, the `?v=` from `AssetVersion.Url`) and calls
   **JS `init(dotnetRef, frame, editorHost, lmsHost)`**: sets up CodeMirror, the preview iframe's
   load handler, keyboard shortcuts (Ctrl+S), drag-and-drop, the "leave page?" warning and the
   LMS stub.
3. **JS `getPrefs()`** returns the browser's saved preferences (Advanced, tips, word list, time
   zone) and the id of the course this tab had open (`sessionStorage`).
4. The page decides what to open, in this order:
   1. `?open=<code>` in the address → **launcher hand-off** (6.5);
   2. `AzureConnections.OpenRequest` set → a course chosen on the **Azure Blob page** (6.4);
   3. a course id from `sessionStorage` that the store still has → **carry on** after a refresh
      (`OpenPackage(kept, reopened: true)`);
   4. otherwise the empty editor with **Open course**.

### 6.3 Opening a course from this computer

1. The user picks a .zip (**Open course → From this computer**) or drops it on the page.
   - Chrome/Edge: **JS `pickLocalCourse()`** uses the File System Access API and keeps the file
     handle in the browser (so it can be saved back later); **JS `readLocal(key)`** streams the
     bytes.
   - Other browsers / drag and drop: Blazor's `InputFile` → **Page `OpenZip`**.
2. **Page `OpenCourse(name, size, read, source)`** checks `MaxUploadMB`, reads the stream into
   memory and calls **`ScormPackage.FromZip`**, which unzips every entry into a dictionary
   (normalised paths) and parses `imsmanifest.xml` with **`ScormManifest.Parse`** (version,
   organization tree, launch page). `package.Source` is set to a `CourseSource` (`LocalKey` = the
   browser file handle).
3. **Page `OpenPackage(package)`**:
   - **`store.TryAdd`** registers it (refused if `MaxOpenCourses` is reached), **`store.Attach`**
     counts this tab as a viewer;
   - **JS `setPackage(id)`** sets the preview base URL to `/scorm-preview/{id}`;
   - **JS `setSession("open", id)`** remembers it for a refresh;
   - opens the launch page in the Code tab (**`OpenFile`** → **JS `openText`**) and in the
     preview (**`SetPreview`** → **JS `loadPreview(path)`**, see 6.6).

### 6.4 Opening a course from Azure Blob

1. **Open course → From Azure Blob** shows **`AzureOpenDialog`**: pick a saved container
   (`AzureConnections.GetAsync`, decrypted on the server) and browse it with **`AzureBrowser`**
   (`AzureBlobService.ListAsync`), or paste a SAS URL (`AzureSas.TryParse` checks it).
2. **`AzureBlobService.DownloadAsync(blob, maxBytes)`** downloads the .zip and returns the bytes and
   the blob's **ETag**.
3. **Page `OpenedFromAzure`** / **`OpenFromAzure`** → **`OpenCourse`** with a `CourseSource`
   holding `Sas`, `BlobName`, `ETag` and the container name. Then as 6.3 step 3.
4. From the **Azure Blob page** (`AzureBlobPage.razor`), **Open in editor** sets
   `AzureConnections.OpenRequest = (connectionId, blobName)` and navigates to `editor`; the editor
   picks it up in 6.2 step 4.

### 6.5 Opening a course sent by another app (launcher)

```
Other page (Edit button)            ScormEditorLauncher (singleton)         ScormEditorPage
────────────────────────            ───────────────────────────────         ───────────────
EditorUrl(sas, blob, …)    ──────►  parse SAS, make random code,
  or EditorUrl(zip, name, onSave)   keep Request for 5 min
                           ◄──────  "editor?open=<code>"
Nav.NavigateTo(url)        ─────────────────────────────────────────────►  OnAfterRenderAsync:
                                                                            remove ?open from address
                                    Take(code) (once only)   ◄──────────── 
                                    ─────────────────────────────────────►  Azure: OpenFromAzure(...)
                                                                            Bytes: OpenCourse(...) with
                                                                              CourseSource.HostSave = onSave
                                                                            ReplaceOnly = true, Back button
```

1. Your page calls **`ScormEditorLauncher.EditorUrl(...)`** on the server. For Azure it parses the
   SAS (`AzureSas.TryParse`) and works out the blob name; for bytes it keeps the zip, file name and
   `onSave`. It stores a `Request` under a random 32-character code for 5 minutes and returns
   `editor?open=<code>`.
2. Your page navigates there. The editor reads the code (`[SupplyParameterFromQuery] open`),
   removes it from the address (so a refresh doesn't reuse it), and **`Launcher.Take(code)`**
   returns the request once (null if unknown, used or expired → warning message).
3. Azure → **`OpenFromAzure(..., replaceOnly: true)`** (6.4 step 2-3).
   Bytes → **`OpenCourse`** with `CourseSource { HostSave = onSave, HostName, ReplaceOnly = true }`.
4. `returnUrl`/`returnLabel` show the **Back** button in the toolbar.

### 6.6 Showing the preview

1. **JS `loadPreview(path)`** sets the iframe to `/scorm-preview/{id}/{path}`.
2. **`ScormPreviewEndpoints` GET** finds the package in the store (`store.Get(id)`), resolves the
   path (`ScormPackage.Resolve`, case-insensitive fallback) and returns the bytes with the right
   content type and `Cache-Control: no-store`.
3. The course's own HTML, scripts, images and data files load the same way, by relative URL, as
   they would from an LMS. Because the iframe is on the **same origin** as the editor, the course
   finds `window.parent.API` (SCORM 1.2) or `API_1484_11` (2004): the **LMS stub** in JS, which
   answers every call and writes it to the LMS panel.
4. When the frame loads, **JS `onFrameLoad`** turns on Edit mode inside it (and inside its
   same-origin inner frames), tells the page which file is showing
   (**`OnPreviewNavigated`**), and re-applies word-check highlights.

### 6.7 Editing in the preview → Review → Save (the main flow)

```
 user types / formats / right-click edits in the iframe
        │
        ▼
 JS records each edited element: HTML + text BEFORE the first edit, and NOW   (drafts, in browser)
        │  JS → OnDraftsChanged(paths)  (shows "Review and save")
        ▼
 user presses Review and save (or Ctrl+S → OnReviewRequested)
        │
        ▼
 Page ReviewDrafts → JS getDrafts() returns EditMapper.Edit[]  ──SignalR──►  server
        │
        ▼
 EditMapper.Apply(package, edits)  for each edit, find where it lives:
        1. the page's own HTML        → DraftPatcher patches the changed characters in place
        2. elsewhere in the package    → JS/JSON data (Storyline), base64 course data (Rise)
        3. not found                   → listed as a problem, nothing written
        │  returns FileChange[] (old/new text) + Problem[]
        ▼
 TextDiff.Compare(old, new) per file  → the review dialog (diff + tick box per change)
        │
        ▼  user presses Save changes
 Page SaveDrafts → ScormPackage.Step(label, paths, write)   (one Undo step + change-log line)
        │          → WriteText for each file (in its own encoding, TextEncoding)
        ▼
 JS updateDoc (Code tab), JS clearDrafts(reload) → preview reloads from the saved files
```

Why it works this way: saving the **rendered** page would also save everything the course's
scripts added at run time. Sending only *before/after per element* and finding that text in the
source files means only your change is written.

The same machinery serves **Find and replace** (`EditMapper.FindReplace` → review → `Step`) and
the **ARR Word Check** (`EditMapper.ScanTerms` runs on a background thread over every file; JS
`setTerms`/`termsShow` highlight matches in the preview; nothing is written).

### 6.8 Editing in the Code tab

1. **Page `OpenFile(path)`** reads the text (`ScormPackage.ReadText`, decoded from the file's own
   encoding) and calls **JS `openText(path, text, mode)`**, which shows it in CodeMirror.
2. As you type, **JS `queueSave`** waits 600 ms, then **`putFile`** sends `PUT
   /scorm-preview/{id}/{path}` with the text.
3. **`ScormPreviewEndpoints` PUT** re-encodes the text into the file's encoding
   (`package.EncodeText`), writes it (`package.Write`) and logs "Edited in the Code tab".
   An image swap from the right-click menu sends the header `X-Scorm-Editor-Step`, which makes it
   one Undo step instead.
4. `ScormPackage` raises **`Changed`** → **Page `OnPackageChanged`** refreshes the badge and file
   tree; with **Live** on, JS reloads the preview.

### 6.9 Undo

Every save of drafts, find-and-replace, image swap and file operation goes through
**`ScormPackage.Step(label, paths, write, log)`**, which first stores the current bytes of those
paths. **Page `UndoLast`** → **`ScormPackage.Undo()`** puts them back (refused if a file was changed
again in the Code tab since), then **`SyncEditors`** reloads those files in CodeMirror and the
preview. **Start over** → `ScormPackage.Revert` for every changed file.

### 6.10 Saving (Save course)

1. **Page `OpenSave`** (after drafts are saved or discarded) shows **`SaveCourseDialog`**, which
   picks the default destination from `Package.Source`.
2. **JS `flush()`** first pushes any Code-tab text not yet sent.
3. **`ScormPackage.ToZip()`** builds the .zip: `imsmanifest.xml` first, every file, and
   `scorm-editor-changes.txt` (the change log).
4. Then, by destination:

| Destination | What runs |
|---|---|
| Back to Azure / to an Azure container | `SaveAzure` → **`AzureBlobService.SaveAsync`**: optional `.OLD` copy (copy, read back, compare) → upload **only if the ETag still matches** → check size and MD5 → keep the new ETag in `Package.Source` |
| Back to the app that sent it | `SaveHost` → your **`onSave(zip)`**; an exception becomes "The course was not saved: …" |
| This computer (same file or a folder) | `SaveLocal` → **JS `saveLocal(url, target, …)`**, which fetches `GET /scorm-export/{id}` and writes it with the File System Access API (temporary file, then swap; optional `.OLD`) |
| Download | **Page `DownloadFromSave`** → **JS `download("scorm-export/{id}")`** |

5. **Page `Saved(message)`** clears the "unsaved" warning and shows the result.

For launcher courses (`ReplaceOnly`), the dialog only offers **Back where it came from** and
**Download**, with no `.OLD` copy.

### 6.11 Closing, refresh and clean-up

- Leaving the page or refreshing → **Page `DisposeAsync`** → **`store.Release(package)`**. The
  package is kept for `ReopenMinutes`, so the same tab carries on (6.2 step 4.3).
- A background timer in the store (**`Sweep`**, every minute) removes packages that were
  released longer than `ReopenMinutes` ago or unused for `IdleHours`, and raises **`Expired`**, so
  an idle open tab says the course was closed.

### 6.12 What is kept where

| Data | Where | How long |
|---|---|---|
| The course's files, originals, undo steps, change log | Server memory (`ScormPackage` in `ScormPackageStore`) | Until closed + `ReopenMinutes`, or `IdleHours` unused |
| Where it came from (SAS, ETag, file handle key, `onSave`) | Server memory (`CourseSource`) | Same as the course |
| Unsaved preview drafts | Browser (JS) | Until saved, discarded or the page reloads |
| Launch codes | Server memory (`ScormEditorLauncher`) | 5 minutes, one use |
| Saved Azure containers | Browser `localStorage`, encrypted by the server | Until removed |
| Advanced, tips, word list | Browser `localStorage` | Until changed |
| Which course this tab has open | Browser `sessionStorage` | The tab's life |
| Local file handles (Chrome/Edge) | Browser memory | Until the page reloads |

## 7. The files, one by one

### 7.1 App shell

| File | Purpose |
|---|---|
| `Program.cs` | Startup. Registers the services (`AssetVersion`, `ScormEditorOptions`, `ScormPackageStore`, `AzureBlobService`, `AzureConnections`, `ScormEditorLauncher`), Data Protection (keys folder from `ScormEditor:DataProtectionKeysPath`), Interactive Server components with a 64 MB SignalR message limit, and maps `MapScormPreview()` and the Razor components. |
| `appsettings.json` | `ScormEditor` limits (section 5) and logging. |
| `ScormEditor.csproj` | .NET 8 web project; one package: `Azure.Storage.Blobs`. |
| `Properties/launchSettings.json`, `.vscode/launch.json` | Local run profiles (http://localhost:5136). |
| `Components/App.razor` | The HTML document: Bootstrap, open-iconic, CodeMirror and editor CSS in the head; CodeMirror scripts and `blazor.web.js` at the end; `Routes` and `HeadOutlet` set to Interactive Server. |
| `Components/Routes.razor` | Router, with `MainLayout` as the default layout. |
| `Components/_Imports.razor` | `@using` lines shared by all components. |
| `Components/Layout/MainLayout.razor` (+ `.css`) | Sidebar + main area, styled like the Content Manager. |
| `Components/Layout/NavMenu.razor` (+ `.css`) | Left menu: Home, SCORM Editor, Azure Blob. |

### 7.2 Pages

| File | Purpose |
|---|---|
| `Components/Pages/ScormEditorPage.razor` | **The editor (`/editor`).** Markup for the toolbar, tips, side panel (pages / structure / file tree), Preview and Code tabs, formatting toolbar, review dialog, find and replace, word check, LMS panel, and the Save/Open dialogs. Its `@code` block holds all the editor logic: start-up and launcher hand-off (`OnAfterRenderAsync`), opening (`OpenCourse`, `OpenPackage`, `OpenFromAzure`), preview and Code tab (`SetPreview`, `OpenFile`), review and save of drafts (`ReviewDrafts`, `BuildReview`, `SaveDrafts`, `DiscardDrafts`), undo (`UndoLast`), find and replace (`RunFind`, `ApplyReplace`), word check (`ScanCourseTerms`, `ShowTerm`), file operations (add, upload, rename, delete, revert, sync manifest), and the `[JSInvokable]` callbacks the JS module calls (`OnPreviewNavigated`, `OnDraftsChanged`, `OnReviewRequested`, `OnImageReplaced`, `OnTermsOnPage`, `OnTermShown`). |
| `Components/Pages/AzureBlobPage.razor` | **Azure Blob (`/azure`).** Add, test and remove saved containers (`AzureConnections`), browse one (`AzureBrowser`), and **Open in editor** (sets `AzureConnections.OpenRequest`). |
| `Components/Pages/Home.razor` | `/`: introduction cards linking to the editor. |
| `Components/Pages/Error.razor` | Error page for unhandled exceptions. |

### 7.3 Shared components (used by the editor and the Azure page)

| File | Purpose |
|---|---|
| `Components/Shared/SaveCourseDialog.razor` | **Save course.** Chooses the destination (back where it came from, Azure container + folder + name, local folder, download), checks whether the file exists, offers Keep the old file, and runs the save: `SaveAzure`, `SaveHost` (launcher `onSave`), `SaveLocal`. Handles "changed since opened" and errors. In `ReplaceOnly` mode only shows Back where it came from + Download. |
| `Components/Shared/AzureOpenDialog.razor` | **Open from Azure.** Saved container or pasted SAS; browse and pick a .zip; downloads it and hands bytes + `CourseSource` to the editor. |
| `Components/Shared/AzureBrowser.razor` | Folder/file list of a container (folders, .zip files, sizes, dates) for opening or choosing a save folder. |
| `Components/Shared/SasSummary.razor` | Shows what a SAS points at (account/container/folder or blob) and its permissions. |
| `Components/Shared/ExpiryBadge.razor` | "Expires in …" / "Expired" badge for a SAS. |
| `Components/Shared/AzurePermissionsNote.razor` | Which SAS permissions to grant, and why Delete is never needed. |

### 7.4 Services (server-side C#)

| File | Purpose | Main members |
|---|---|---|
| `Services/ScormPackage.cs` | One open course in memory. Paths are normalised (forward slashes, no `..`). Keeps current bytes, original bytes of changed files, the undo stack, the change log, the parsed manifest, `Source`, and the last page shown (for carrying on). Thread-safe (the circuit and the preview endpoints use it at once). | `FromZip`, `ToZip`, `Read`/`ReadText`, `Write`/`WriteText`, `EncodeText`, `Resolve`, `Delete`, `Rename`, `Revert`, `Step`, `Undo`, `LogChange`, `ChangeLog`, `SyncManifestFileList`, events `Changed`, `Expired` |
| `Services/ScormPackageStore.cs` | All open courses, by id. Enforces `MaxOpenCourses` (makes room by dropping the course closed longest ago), counts viewers, keeps closed courses for `ReopenMinutes`, sweeps idle ones. Also defines `ScormEditorOptions` (the `ScormEditor` config section). | `TryAdd`, `Get`, `Attach`, `Release`, `Remove`, `Options` |
| `Services/ScormManifest.cs` | Reads `imsmanifest.xml`: SCORM version, default organization's item tree, resources, launch page. | `Parse`, `Items`, `LaunchPath`, `Version`, `CourseTitle` |
| `Services/ScormPreviewEndpoints.cs` | Minimal-API endpoints for the preview and export. | `MapScormPreview()`: `GET`/`PUT /scorm-preview/{id}/{**path}`, `GET /scorm-export/{id}` |
| `Services/EditMapper.cs` | The core of "only save what you changed". Turns preview drafts into file changes by finding each edit in the page HTML, in script/JSON data, or in base64 Rise data; also Find and replace and the word-check scan, all working on the same view of the course's text. | `Apply`, `Changes`, `Summarize`, `FindReplace`, `ScanTerms`; records `Edit`, `FileChange`, `Problem`, `Hit`, `TermHit` |
| `Services/DraftPatcher.cs` | Patches one edited element into a page's HTML source in place, using the text around it to find the right spot. | `Apply`, `ApplyElement` |
| `Services/TextDiff.cs` | Myers line diff with changed-word highlights, for the review dialog. | `Compare`, `Edits`, `SplitLines` |
| `Services/TextEncoding.cs` | Detects and keeps a text file's encoding (UTF-8, UTF-16, Shift_JIS, EUC-JP, Windows-1252, ISO-8859-1). | `Decode`, `EncodeLike` |
| `Services/TermList.cs` | ARR Word Check list: default IFS Academy pairs and parsing of the user's list. | `Default`, `Parse` |
| `Services/CourseSource.cs` | Where a course came from and how to save it back: Azure (`Sas`, `BlobName`, `ETag`, `ConnectionName`), this computer (`LocalKey`), or another app (`HostSave`, `HostName`); `ReplaceOnly`; `Display` text. | record |
| `Services/AzureSas.cs` | Reads a SAS URL without calling Azure: account, container, folder or blob, permissions (`sp`), expiry (`se`); error messages for bad URLs; `Redact` hides the signature for display. | `TryParse`, `Allows`, `Missing`, `Expired`, `ContainerUri`, `Display` |
| `Services/AzureBlobService.cs` | All Azure Blob calls, through SAS URLs only. Never deletes. Never logs SAS URLs; errors are built from status codes. | `ListAsync`, `TestAsync`, `PropertiesAsync`, `DownloadAsync`, `SaveAsync` (`.OLD` + ETag + MD5), `Blob`, `Container`, `Stamp`; `SaveException` |
| `Services/AzureConnections.cs` | The user's saved containers, stored in the browser with `ProtectedLocalStorage` (encrypted by Data Protection). One instance per tab. Also `OpenRequest` for the Azure page → editor hand-off. | `GetAsync`, `SaveAsync`, `DeleteAsync`, `LoadProblem`, `OpenRequest` |
| `Services/ScormEditorLauncher.cs` | Hand-off from another page: keeps a blob location or zip bytes + `onSave` under a one-time code for 5 minutes. | `EditorUrl` / `Create` (Azure and bytes overloads), `Take`, `Lifetime` |
| `Services/AssetVersion.cs` | Adds `?v=<last modified>` to the editor's JS/CSS URLs, so browsers don't run an old cached script against a new page. | `Url` |

### 7.5 Browser side

| File | Purpose | Main exports |
|---|---|---|
| `wwwroot/js/scorm-editor.js` | The editor's JavaScript module, loaded by the page. **Code tab:** CodeMirror documents per file, debounced `PUT` saves. **Preview:** loads pages, tracks navigation, Live reload, widths. **Edit mode:** makes the page and its same-origin frames editable, formatting commands, right-click menu (links, embeds, images, buttons, HTML), Ctrl+click navigation, hide-for-learners, yellow highlights, and the drafts list (before/after per element). **Word check:** highlights, tips, next/previous, jump to a place. **LMS stub:** `window.API` and `window.API_1484_11` with a call log. **Local files:** File System Access API open/save, `.OLD` in the folder. **Prefs:** localStorage/sessionStorage. | `init`, `getPrefs`, `setPref`, `setSession`, `setPackage`, `openText`, `flush`, `loadPreview`, `reloadPreview`, `setMode`, `exec`, `getDrafts`, `clearDrafts`, `updateDoc`, `resetEdits`, `download`, `setTerms`, `termsShow`, `termsGo`, `resetLms`, `pickLocalCourse`, `readLocal`, `pickFolder`, `localExists`, `saveLocal` |
| `wwwroot/css/scorm-editor.css` | All editor styles: toolbar, panels, tabs, review dialog, diff colours, word-check marks, save/open dialogs, simple vs advanced layout. |
| `wwwroot/css/site.css` | Base app styles. |
| `wwwroot/lib/codemirror/` | CodeMirror 5: core, `xml`, `javascript`, `css`, `htmlmixed` modes; `matchbrackets`, `xml-fold`, `closetag` addons. |
| `wwwroot/lib/bootstrap/`, `wwwroot/lib/open-iconic/` | Bootstrap 5 and the icon font (same as the Content Manager). |
| `wwwroot/favicon.svg` | App icon. |

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

app.MapScormPreview();          // /scorm-preview/{id}/{**path} (GET/PUT) and /scorm-export/{id}; before MapRazorComponents

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
