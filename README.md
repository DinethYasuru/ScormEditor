# IFS Academy SCORM Editor

A Blazor Server app (.NET 8) for editing SCORM packages, styled to match the IFS Academy Content Manager.

## Run it

```
dotnet run
```

Then open http://localhost:5136/editor. The editor opens with a small sample course; use **Open package…** to load your own SCORM .zip.

## What it does

The package panel sits on the left; the work area has two tabs, **Preview** and **Code**. Clicking a file opens it in the Code tab; the eye icon next to a page, or a course item, opens it in the Preview tab.

- **Course structure**: reads `imsmanifest.xml` (SCORM 1.2 or 2004), lists the default organization's items and the launch page. Click an item to preview it.
- **Files**: tree of every file in the package. Changed files get a yellow dot. Add, upload, rename, delete and revert files.
- **Code**: CodeMirror editor for HTML, CSS, JavaScript, JSON and XML. Edits save as you type; the preview reloads when you switch back to it (turn off **Live** to stop that).
- **Edit in the preview**: the Preview tab opens in **Edit** mode, so you can click any text in the page and type straight away. A toolbar gives bold, italic, underline, links, remove formatting, undo and redo, and double-clicking an image replaces it. Frames inside the page (Rise, Storyline and most LMS wrappers put the content in an inner iframe) are editable too, and each edited page is saved to its own file. With **Live** on, edits save as you type; with it off, press **Save** or Ctrl+S. **Undo all changes** puts the pages back. Switch to **Interact** to click through the course as a learner. Pages are saved as they look at that moment, so content a course's own scripts build at runtime is saved too; for script-built pages (Rise, Storyline) edit the source data in the Code tab instead.
- **LMS panel**: the preview answers SCORM 1.2 (`window.API`) and 2004 (`window.API_1484_11`) calls, showing status, score, location and a log of every call.
- **Sync manifest**: rewrites the launch resource's `<file>` list so it names every file in the package.
- **Export .zip**: downloads the edited package with `imsmanifest.xml` as the first entry.

## How the preview works

Open packages are kept in memory (`ScormPackageStore`) and served from `/scorm-preview/{id}/{path}`. Because the course loads from real relative URLs on the same origin as the editor, it finds the stub API through `window.parent`, and Storyline/Rise packages that fetch their own data files work as they would on an LMS. Code edits are written back with `PUT` to the same URL, so large files do not hit the SignalR message limit. Packages are dropped when the editor page closes; nothing is written to disk.

## Moving it into the Content Manager

The editor is self-contained, so it can be copied into `IfsContentPortal`:

- `Services/` (package model, manifest parser, store, preview endpoints, sample)
- `Components/Pages/ScormEditorPage.razor`
- `wwwroot/js/scorm-editor.js`, `wwwroot/css/scorm-editor.css`, `wwwroot/lib/codemirror/`
- In `Program.cs`: `builder.Services.AddSingleton<ScormPackageStore>();` and `app.MapScormPreview();`
- Add the CodeMirror `<script>`/`<link>` tags from `Components/App.razor` to your host page.

The preview endpoints have no authentication of their own (the package id is a random GUID). Put them behind the same sign-in as the rest of the portal before deploying.

## Theme

Bootstrap 5.3.3, open-iconic, `site.css` and the MainLayout/NavMenu styles are copied from the Content Manager so both apps look the same. CodeMirror 5.65.16 and the open-iconic fonts are vendored under `wwwroot/lib`, so the app needs no CDN.
