# IFS Academy SCORM Editor

A Blazor Server app (.NET 8) for editing SCORM packages, styled to match the IFS Academy Content Manager.

## Run it

```
dotnet run
```

Then open http://localhost:5136/editor. Choose your course .zip (or drag it onto the page) to start editing.

## What it does

The editor opens in **simple mode**: the course's pages on the left and the course itself, ready to edit, on the right. A short how-to strip explains the four steps (type, right-click, Review and save, Download course) and can be hidden. Drop a course .zip anywhere on the page to open it. Edited text is highlighted in yellow until it is saved, **Start over** undoes every change, and the browser asks before closing the tab while there are changes not yet downloaded. Switch on **Advanced** for the file tree, Code tab, manifest tools, Live reload and the LMS panel described below; the choice is remembered in the browser.

The package panel sits on the left and folds into a thin rail (the arrow in its header) to give the editor the full width; the work area has two tabs, **Preview** and **Code**. Clicking a file opens it in the Code tab; the eye icon next to a page, or a course item, opens it in the Preview tab.

- **Course structure**: reads `imsmanifest.xml` (SCORM 1.2 or 2004), lists the default organization's items and the launch page. Click an item to preview it.
- **Files**: tree of every file in the package. Changed files get a yellow dot. Add, upload, rename, delete and revert files.
- **Code**: CodeMirror editor for HTML, CSS, JavaScript, JSON and XML. Edits save as you type; the preview reloads when you switch back to it (turn off **Live** to stop that).
- **Edit in the preview**: the Preview tab opens in **Edit** mode, so you can click any text in the page and type straight away. A toolbar gives bold, italic, underline, links, remove formatting, undo and redo. Frames inside the page (Rise, Storyline and most LMS wrappers put the content in an inner iframe) are editable too. Switch to **Interact** to click through the course as a learner.
- **Right-click menu**: on a link, edit its address, text and new-tab setting, open it, copy it or remove it; on selected text, add a link; on an embed (iframe, video, audio, object), edit its URL (and an iframe's title); on an image, replace it or edit its alt text; on any element, edit its HTML. Embeds from other sites get a yellow dashed outline in Edit mode, as they cannot be clicked into.
- **Buttons**: in Edit mode, clicking a button or link no longer leaves the page, so its text can be clicked into. Ctrl+click (Cmd+click on a Mac), or right-click → **Go to this link** / **Click this button**, uses it as a learner would to reach inner pages; with unsaved edits, a link to another file opens the review first. Right-click a button for **Edit label…** (the label is changed where the course keeps it, for example Rise's "Start Course" in its course data) and **Hide this for learners…**, which adds a rule to a `<style id="scorm-editor-hidden">` block in the page's `<head>`, so it works on script-built pages too. **Show hidden elements again** removes that block.
- **Review before saving**: preview edits are drafts until you save them. **Review and save** (or Ctrl+S) shows a line-by-line diff of each edited page's source, with the changed words highlighted, then **Save changes**, **Keep editing** or **Discard changes**. Only what you changed is written. The editor records each element you edit and looks for the change in the page's HTML, or, for courses whose pages are built by a script, in the data the script reads: JavaScript and JSON files (Storyline) and base64-encoded course data inside a page (Rise), which the review shows decoded. Text found in several files (a course title in the data, the page `<title>` and the manifest) is changed in all of them, and the review says so. An edit that can't be found is listed and left out, and the rendered page itself is never saved, so labels and markup a course's scripts add (such as Rise's "Unstarted") stay out of your files. Anything that would reload the preview (another page, Interact, Reload, Export, opening another course) asks you to save or discard first. The **changed files** badge next to the course title shows every change since the package was opened.
- **LMS panel**: the preview answers SCORM 1.2 (`window.API`) and 2004 (`window.API_1484_11`) calls, showing status, score, location and a log of every call.
- **Sync manifest**: rewrites the launch resource's `<file>` list so it names every file in the package.
- **Export .zip**: downloads the edited package with `imsmanifest.xml` as the first entry.

## How the preview works

Open packages are kept in memory (`ScormPackageStore`) and served from `/scorm-preview/{id}/{path}`. Because the course loads from real relative URLs on the same origin as the editor, it finds the stub API through `window.parent`, and Storyline/Rise packages that fetch their own data files work as they would on an LMS. Code edits are written back with `PUT` to the same URL. Preview drafts go to the server for review over SignalR, so `Program.cs` raises the hub's message limit to 64 MB. Packages are dropped when the editor page closes; nothing is written to disk.

## Moving it into the Content Manager

The editor is self-contained, so it can be copied into `IfsContentPortal`:

- `Services/` (package model, manifest parser, store, preview endpoints, edit mapping and diff)
- `Components/Pages/ScormEditorPage.razor`
- `wwwroot/js/scorm-editor.js`, `wwwroot/css/scorm-editor.css`, `wwwroot/lib/codemirror/`
- In `Program.cs`: `builder.Services.AddSingleton<ScormPackageStore>();` and `app.MapScormPreview();`
- Add the CodeMirror `<script>`/`<link>` tags from `Components/App.razor` to your host page.

The preview endpoints have no authentication of their own (the package id is a random GUID). Put them behind the same sign-in as the rest of the portal before deploying.

## Theme

Bootstrap 5.3.3, open-iconic, `site.css` and the MainLayout/NavMenu styles are copied from the Content Manager so both apps look the same. CodeMirror 5.65.16 and the open-iconic fonts are vendored under `wwwroot/lib`, so the app needs no CDN.
