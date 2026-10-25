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
- **Pick which changes to save**: the review lists every change you made, each with a tick box. Untick one to leave it out; unticked changes are thrown away when you press **Save ticked changes**.
- **Undo**: every save (and every image swap, find and replace, or file operation) is one undo step. **Undo** in the toolbar puts the files back; its tooltip says what it will undo. A step can't be undone once the same file was changed again in the Code tab, which has its own undo. Typing in the preview has the usual Ctrl+Z.
- **Find and replace**: finds text learners see across every page, the manifest and the course data (Rise's encoded data, Storyline's data files, .json files), with match case and whole-word options. Each place found is listed with the text around it and a tick box; only ticked places are replaced. Markup, scripts, styles, data keys and code are left alone: in script data only string values that read as text (words with spaces, markup, or a capitalised label in JSON) count.
- **Word check**: points out words from a word list that should be replaced (the IFS Academy terminology list by default; edit it with Edit list, one `old word<Tab>new word` per line, with match case and whole-word options; the list is kept in the browser). It counts each word across the whole course (pages and Rise/Storyline data, including tabs and slides not on screen) and highlights it in the preview, with the suggested word in a tip or next to each match, previous/next buttons for each word, the places in the course, and a copyable report. Nothing is replaced, because a word swapped without reading the sentence can change its meaning.
- **Change log**: the **changed files** badge opens a log of what was changed and when, and the downloaded course includes it as `scorm-editor-changes.txt` (LMSs ignore files the manifest doesn't list).
- **LMS panel**: the preview answers SCORM 1.2 (`window.API`) and 2004 (`window.API_1484_11`) calls, showing status, score, location and a log of every call.
- **Sync manifest**: rewrites the launch resource's `<file>` list so it names every file in the package.
- **Export .zip**: downloads the edited package with `imsmanifest.xml` as the first entry.

## Server limits

Courses are held in the server's memory while they're open. `appsettings.json` sets the limits under `ScormEditor`:

- `MaxUploadMB` (500): the largest course .zip that can be opened.
- `MaxOpenCourses` (20): the most courses open at once across all users; opening one more asks the user to try again later.
- `IdleHours` (8): a course nobody has used for this long is closed and its memory freed; its tab says so.
- `ReopenMinutes` (30): after the editor page closes (a refresh, going to Home), the course is kept this long so the same tab carries on with it, saved changes and undo included. Edits not yet saved in the preview are lost on a refresh.

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
