// Browser side of the SCORM editor: the CodeMirror source editor, the preview iframe (which is
// also a visual editor) and a stub LMS runtime so courses can call the SCORM 1.2 and 2004 APIs.
// Package state lives on the server; edits are written back with PUT to /scorm-preview/{id}/{path}.

let dotnet = null;
let frame = null;
let editorHost = null;
let lmsHost = null;
let cm = null;
let base = "";
let currentPath = "";
let live = true;
let silent = false;
const docs = new Map();
const pending = new Map();

const encodePath = (p) => p.split("/").map(encodeURIComponent).join("/");
const dirOf = (p) => (p.includes("/") ? p.slice(0, p.lastIndexOf("/")) : "");

export function init(dotnetRef, frameEl, editorEl, lmsEl) {
    dotnet = dotnetRef;
    frame = frameEl;
    editorHost = editorEl;
    lmsHost = lmsEl;
    cm = window.CodeMirror(editorHost, {
        lineNumbers: true,
        tabSize: 2,
        indentUnit: 2,
        matchBrackets: true,
        autoCloseTags: true,
        theme: "ifs",
    });
    // Reading the whole document on every keystroke is slow for large course files, so the
    // text is only read when the debounced save runs.
    cm.on("change", () => {
        if (silent || !currentPath) return;
        queueSave(currentPath);
    });
    frame.addEventListener("load", onFrameLoad);
    hookPageEvents();
    buildLmsPanel();
    installLmsApi();
}

/* ---------- page conveniences ---------- */

let unsaved = false;

// Small settings remembered in this browser (Advanced on or off, tips hidden).
export function getPrefs() {
    const prefs = {};
    try {
        for (const key of ["advanced", "tips"]) prefs[key] = localStorage.getItem("scorm-editor." + key);
    } catch { }
    return prefs;
}

export function setPref(key, value) {
    try { localStorage.setItem("scorm-editor." + key, value); } catch { }
}

export function setUnsaved(on) {
    unsaved = on;
}

function hookPageEvents() {
    // Closing the tab loses the course, so the browser asks first while there is work to keep.
    window.addEventListener("beforeunload", (e) => {
        if (!unsaved && !hasDrafts()) return;
        e.preventDefault();
        e.returnValue = "";
    });
    // Ctrl/Cmd+S opens the review from anywhere on the editor page as well as inside the course.
    window.addEventListener("keydown", (e) => {
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s" && !e.target.closest?.(".CodeMirror")) {
            e.preventDefault();
            if (hasDrafts()) dotnet.invokeMethodAsync("OnReviewRequested");
        }
    });
    // While a file is dragged over the page, show the drop target (a file input covering the page).
    const root = () => document.querySelector(".scorm-editor");
    let depth = 0;
    window.addEventListener("dragenter", (e) => {
        if (![...(e.dataTransfer?.types ?? [])].includes("Files")) return;
        depth++;
        root()?.classList.add("dropping");
    });
    window.addEventListener("dragleave", () => {
        if (--depth <= 0) { depth = 0; root()?.classList.remove("dropping"); }
    });
    window.addEventListener("drop", () => {
        depth = 0;
        // Let the file input receive the drop before it is hidden again.
        setTimeout(() => root()?.classList.remove("dropping"), 0);
    });
    window.addEventListener("dragover", (e) => {
        // Outside the drop target, stop the browser from opening the file itself.
        if (!e.target.closest?.(".se-drop")) e.preventDefault();
    });
}

export function setPackage(id) {
    base = `/scorm-preview/${id}`;
    docs.clear();
    pending.clear();
    currentPath = "";
    resetEdits();
    resetLms();
}

export function openText(path, text, mode, force) {
    let doc = docs.get(path);
    if (!doc) {
        doc = window.CodeMirror.Doc(text, mode);
        docs.set(path, doc);
    } else if (force && doc.getValue() !== text) {
        silent = true;
        doc.setValue(text);
        silent = false;
    }
    currentPath = path;
    silent = true;
    cm.swapDoc(doc);
    silent = false;
    cm.getWrapperElement().hidden = false;
    cm.refresh();
}

export function hideEditor() {
    currentPath = "";
    cm.getWrapperElement().hidden = true;
}

export function forget(path) {
    docs.delete(path);
    if (currentPath === path) hideEditor();
}

export function refresh() {
    cm?.refresh();
}

export function reindent() {
    if (!currentPath) return;
    cm.operation(() => {
        for (let i = 0; i < cm.lineCount(); i++) cm.indentLine(i, "smart");
    });
}

export async function flush() {
    await Promise.all([...pending.keys()].map((p) => saveNow(p)));
}

function queueSave(path) {
    const entry = pending.get(path) || {};
    clearTimeout(entry.timer);
    entry.timer = setTimeout(() => saveNow(path), 600);
    pending.set(path, entry);
}

async function saveNow(path) {
    const entry = pending.get(path);
    if (!entry) return;
    clearTimeout(entry.timer);
    pending.delete(path);
    const doc = docs.get(path);
    if (!doc) return;
    await putFile(path, doc.getValue());
    if (live) schedulePreviewReload();
}

async function putFile(path, body) {
    const res = await fetch(`${base}/${encodePath(path)}`, { method: "PUT", body });
    if (!res.ok) throw new Error(`Saving ${path} failed (${res.status})`);
}

/* ---------- preview ---------- */

let previewVisible = true;
let previewStale = false;
let reloadTimer = 0;

export function setLive(on) {
    live = on;
}

// The preview only reloads for code edits while it is on screen; otherwise it reloads once
// when the Preview tab is shown again. Reloading a large course on every keystroke is what
// made typing in the Code tab lag.
export function setPreviewVisible(visible) {
    previewVisible = visible;
    if (visible && previewStale && !hasDrafts()) {
        previewStale = false;
        reloadPreview();
    }
}

function schedulePreviewReload() {
    // Never reload over unsaved preview edits; the reload waits until they are saved or discarded.
    if (!previewVisible || hasDrafts()) {
        previewStale = true;
        return;
    }
    clearTimeout(reloadTimer);
    reloadTimer = setTimeout(reloadPreview, 300);
}

export function loadPreview(path) {
    teardownEditing();
    previewStale = false;
    frame.src = path ? `${base}/${encodePath(path)}` : "about:blank";
}

export function reloadPreview() {
    clearTimeout(reloadTimer);
    teardownEditing();
    try {
        frame.contentWindow.location.reload();
    } catch {
        frame.src = frame.src;
    }
}

function framePath() {
    return frame.contentWindow ? pathOfWindow(frame.contentWindow) : null;
}

function pathOfWindow(win) {
    try {
        const p = win.location.pathname;
        return p.startsWith(base + "/") ? decodeURIComponent(p.slice(base.length + 1)) : null;
    } catch {
        return null;
    }
}

function onFrameLoad() {
    teardownEditing();
    if (mode === "edit") enableIn(frame.contentWindow);
    const p = framePath();
    if (p) dotnet.invokeMethodAsync("OnPreviewNavigated", p);
}

/* ---------- visual editing ----------
   In Edit mode every page in the preview is editable as soon as it loads: the previewed page and
   every same-origin frame inside it, including frames a course creates later, because most
   authoring tools (Rise, Storyline, LMS wrappers) put the content in a nested iframe.

   Edits are drafts. Each edit is recorded against the smallest element that holds it, with a
   copy of that element taken just before its first change. The review sends only those elements,
   old and new, and the server finds each change in the package's files (the page's HTML, or the
   data a script builds the page from) and shows a diff. Whatever a course's own scripts do to
   the rest of the page never reaches a file. Nothing is written until the user saves the review.

   A right-click menu edits links, embeds (iframe, video, audio), images and raw element HTML.
   Embeds from other sites cannot receive clicks from the editor, so in Edit mode they are
   "shielded": they ignore the mouse, and a right-click over one is matched to it by position.
   Interact mode turns editing off so links, buttons and quizzes work. */

let mode = "edit";
let targets = [];                       // [{ win, doc, path, observer }]
const hookedFrames = new WeakSet();
const records = new Map();              // id -> { id, path, el, structural, oldHtml, oldText }
let nextRecord = 1;
const hides = [];                       // { id, path, selector } or { id, path, unhide: true }
let lastDoc = null;
let imageInput = null;
let menu = null;

const EDIT_STYLE = `
:where(h1,h2,h3,h4,h5,h6,p,li,a,span,td,th,label,button,figcaption,blockquote,dt,dd,strong,em):hover{outline:1px dashed rgba(27,110,194,.7)!important;outline-offset:2px}
a[href]{cursor:context-menu}
img{cursor:pointer!important}
img:hover{outline:3px solid #1b6ec2!important;outline-offset:2px}
[data-se-rec]{background-color:rgba(255,214,0,.3)!important;box-shadow:0 0 0 2px rgba(255,193,7,.6)!important;border-radius:2px}
[data-se-shield]{pointer-events:none!important;outline:2px dashed rgba(255,193,7,.9)!important;outline-offset:-2px}
html{caret-color:#1b6ec2}`;

export function setMode(next) {
    if (next === mode) return;
    mode = next;
    closeMenu();
    if (mode === "edit") enableIn(frame.contentWindow);
    else teardownEditing();
}

export function hasDrafts() {
    return records.size > 0 || hides.length > 0;
}

function enableIn(win) {
    if (!win || mode !== "edit") return;
    let doc;
    try { doc = win.document; } catch { return; }
    const path = pathOfWindow(win);
    if (!doc || !path || targets.some((t) => t.doc === doc)) return;
    const turnOn = () => {
        if (mode !== "edit" || !doc.body || targets.some((t) => t.doc === doc)) return;
        doc.designMode = "on";
        const style = doc.createElement("style");
        style.setAttribute("data-se-injected", "");
        style.textContent = EDIT_STYLE;
        (doc.head || doc.documentElement).append(style);
        doc.addEventListener("beforeinput", onBeforeInput);
        doc.addEventListener("input", onEditInput);
        doc.addEventListener("dblclick", onDoubleClick, true);
        doc.addEventListener("contextmenu", onContextMenu, true);
        doc.addEventListener("focusin", onFocus);
        doc.addEventListener("selectionchange", onFocus);
        doc.addEventListener("keydown", onKeyDown);
        doc.addEventListener("mousedown", closeMenu);
        doc.addEventListener("scroll", closeMenu, true);
        // Buttons and links would leave the page when clicked; in Edit mode a click only places
        // the cursor. Listening on the window, in the capture phase, runs before the course's own
        // handlers (React and similar frameworks listen lower down).
        win.addEventListener("click", blockActions, true);
        win.addEventListener("auxclick", blockActions, true);
        win.addEventListener("submit", blockActions, true);
        // Courses often add their content frames after load, so watch for new ones.
        const observer = new MutationObserver(() => hookFrames(doc));
        observer.observe(doc.documentElement, { childList: true, subtree: true });
        targets.push({ win, doc, path, observer });
        hookFrames(doc);
    };
    if (doc.readyState === "loading") doc.addEventListener("DOMContentLoaded", turnOn, { once: true });
    else turnOn();
}

const EMBEDS = "iframe, frame, embed, object, video, audio";

function hookFrames(doc) {
    for (const f of doc.querySelectorAll("iframe, frame")) {
        if (!hookedFrames.has(f)) {
            hookedFrames.add(f);
            f.addEventListener("load", () => {
                // The old document in this frame is gone; drop it and edit the new one.
                targets = targets.filter((t) => { if (t.win === f.contentWindow && t.doc !== safeDoc(f)) { t.observer.disconnect(); return false; } return true; });
                enableIn(f.contentWindow);
                shield(doc);
            });
        }
        enableIn(f.contentWindow);
    }
    shield(doc);
}

// Embeds that are not editable pages themselves (other sites, players) get the shield.
function shield(doc) {
    if (mode !== "edit") return;
    for (const el of doc.querySelectorAll(EMBEDS)) {
        const editable = (el.tagName === "IFRAME" || el.tagName === "FRAME") && targets.some((t) => t.doc === safeDoc(el));
        if (editable) el.removeAttribute("data-se-shield");
        else if (!el.hasAttribute("data-se-shield")) el.setAttribute("data-se-shield", "");
    }
}

function safeDoc(f) {
    try { return f.contentDocument; } catch { return null; }
}

function teardownEditing() {
    closeMenu();
    for (const t of targets) {
        try {
            t.observer.disconnect();
            t.doc.designMode = "off";
            for (const [type, fn, capture] of [["beforeinput", onBeforeInput], ["input", onEditInput], ["dblclick", onDoubleClick, true],
                ["contextmenu", onContextMenu, true], ["focusin", onFocus], ["selectionchange", onFocus], ["keydown", onKeyDown],
                ["mousedown", closeMenu], ["scroll", closeMenu, true]]) {
                t.doc.removeEventListener(type, fn, !!capture);
            }
            for (const type of ["click", "auxclick", "submit"]) t.win.removeEventListener(type, blockActions, true);
            t.doc.querySelectorAll("[data-se-hidden-preview]").forEach((n) => n.remove());
            t.doc.querySelectorAll("[data-se-injected]").forEach((n) => n.remove());
            t.doc.querySelectorAll("[data-se-shield]").forEach((n) => n.removeAttribute("data-se-shield"));
        } catch { }
    }
    targets = [];
    lastDoc = null;
}

function targetFor(doc) {
    return targets.find((t) => t.doc === doc);
}

function onFocus(e) {
    lastDoc = e.currentTarget;
}

function onKeyDown(e) {
    if (e.key === "Escape") closeMenu();
    // Ctrl/Cmd+S opens the review, as saving always goes through it.
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
        e.preventDefault();
        dotnet.invokeMethodAsync("OnReviewRequested");
    }
}

const BLOCKS = "p,h1,h2,h3,h4,h5,h6,li,td,th,dt,dd,blockquote,figcaption,pre,caption,summary,label,button";
const STRUCTURAL_INPUT = /^(insertParagraph|insertLineBreak|insertOrderedList|insertUnorderedList|insertHorizontalRule|insertFromPaste|insertFromDrop|formatBlock|formatIndent|formatOutdent)$/;

const elementOf = (n) => (n?.nodeType === 1 ? n : n?.parentElement ?? null);

// The element an edit at the current selection changes: the text's own element, or for edits
// that split, join or add blocks, the element around them.
function editTarget(t, inputType) {
    const sel = t.doc.getSelection();
    if (!sel || !sel.rangeCount) return null;
    const r = sel.getRangeAt(0);
    let el = elementOf(r.startContainer) === elementOf(r.endContainer) ? elementOf(r.startContainer) : elementOf(r.commonAncestorContainer);
    if (!el) return null;
    let structural = elementOf(r.startContainer)?.closest(BLOCKS) !== elementOf(r.endContainer)?.closest(BLOCKS) || STRUCTURAL_INPUT.test(inputType ?? "");
    if (!structural && inputType?.startsWith("delete") && r.collapsed) {
        // Deleting at the very start or end of a block joins it with its neighbour.
        const block = el.closest(BLOCKS) || el;
        const edge = t.doc.createRange();
        edge.selectNodeContents(block);
        if (inputType.includes("Backward")) edge.setEnd(r.startContainer, r.startOffset);
        else edge.setStart(r.endContainer, r.endOffset);
        structural = edge.toString().length === 0;
    }
    if (structural) el = (el.closest(BLOCKS) || el).parentElement || el;
    return { el, structural };
}

// A copy of an element without anything the editor added to it.
function clean(el) {
    const c = el.cloneNode(true);
    c.querySelectorAll("[data-se-injected]").forEach((n) => n.remove());
    for (const n of [c, ...c.querySelectorAll("*")]) {
        for (const a of Array.from(n.attributes)) {
            if (a.name.startsWith("data-se-orig-")) {
                n.setAttribute(a.name.slice(13), a.value);
                n.removeAttribute(a.name);
            } else if (a.name.startsWith("data-se-")) {
                n.removeAttribute(a.name);
            }
        }
    }
    return c;
}

// Records an element just before it changes. An element inside one already recorded is covered by
// that record; records inside a newly recorded element are folded into it, with their original
// HTML put back so the new record starts from the page as it was.
function track(t, target) {
    if (!t || !target?.el) return;
    const { el } = target;
    if (el === t.doc.documentElement || el === t.doc.head) return;
    const outer = el.closest("[data-se-rec]");
    if (outer && records.has(+outer.getAttribute("data-se-rec"))) {
        if (target.structural) records.get(+outer.getAttribute("data-se-rec")).structural = true;
        return;
    }
    let structural = !!target.structural;
    const copy = el.cloneNode(true);
    for (const inner of copy.querySelectorAll("[data-se-rec]")) {
        const rec = records.get(+inner.getAttribute("data-se-rec"));
        if (!rec) continue;
        const tpl = t.doc.createElement("template");
        tpl.innerHTML = rec.oldHtml;
        inner.replaceWith(tpl.content);
        structural ||= rec.structural;
    }
    for (const inner of el.querySelectorAll("[data-se-rec]")) {
        records.delete(+inner.getAttribute("data-se-rec"));
        inner.removeAttribute("data-se-rec");
    }
    const c = clean(copy);
    const id = nextRecord++;
    records.set(id, { id, path: t.path, el, structural, oldHtml: c.outerHTML, oldText: c.textContent });
    el.setAttribute("data-se-rec", id);
}

function onBeforeInput(e) {
    const t = targetFor(e.currentTarget);
    if (t) track(t, editTarget(t, e.inputType));
}

function onEditInput(e) {
    const t = targetFor(e.currentTarget);
    if (!t) return;
    lastDoc = t.doc;
    reportDrafts();
}

// The editor page only hears when the set of edited pages changes, not on every keystroke.
let reported = "";
function reportDrafts() {
    const paths = [...new Set([...records.values(), ...hides].map((r) => r.path))].sort();
    const key = paths.join("|");
    if (key === reported) return;
    reported = key;
    dotnet.invokeMethodAsync("OnDraftsChanged", paths);
}

// Formatting toolbar commands act on the page that last had the cursor.
export function exec(command) {
    const doc = lastDoc && targetFor(lastDoc) ? lastDoc : targets[0]?.doc;
    if (!doc) return;
    // Clicking the toolbar moves focus to the editor page; commands need it back in the course page.
    try { doc.defaultView.focus(); } catch { }
    const t = targetFor(doc);
    if (command === "createLink") {
        const sel = doc.getSelection();
        if (!sel || sel.isCollapsed) return;
        const url = window.prompt("Link address", "https://");
        if (!url) return;
        track(t, { ...editTarget(t), structural: true });
        doc.execCommand("createLink", false, url);
    } else {
        // Formatting wraps text in new elements, so it is a change of structure.
        const target = editTarget(t, command === "undo" || command === "redo" ? null : "formatBlock");
        track(t, target);
        doc.execCommand(command, false, null);
    }
    reportDrafts();
}

// Offsets in an element's text where markup sits (bold, links, spans), so the server can match
// the text where the course's data writes it with that markup in between.
function textBreaks(root) {
    const breaks = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let offset = 0;
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
        if (offset > 0) breaks.push(offset);
        offset += n.data.length;
    }
    return breaks;
}

// Edited elements for the review: their HTML and text before the first edit and now, and the
// attributes that changed when the element's structure is the same.
export function getDrafts() {
    const out = hides.map((h) => ({ path: h.path, oldHtml: "", newHtml: "", oldText: "", newText: "", attrs: [], structural: false, lost: false, breaks: [], hide: h.selector ?? null, unhide: !!h.unhide }));
    const live = [...records.values()].filter((r) => r.el.isConnected);
    for (const r of records.values()) {
        if (!r.el.isConnected) {
            // Pages built with React and similar redraw elements; when the redrawn element was
            // edited again it has its own record, so the old one is not worth reporting.
            if (!live.some((x) => x.path === r.path && x.oldText === r.oldText))
                out.push({ path: r.path, oldHtml: r.oldHtml, newHtml: null, oldText: r.oldText, newText: null, attrs: [], structural: r.structural, lost: true, breaks: [] });
            continue;
        }
        const c = clean(r.el);
        if (c.outerHTML === r.oldHtml) continue;
        const tpl = document.createElement("template");
        tpl.innerHTML = r.oldHtml;
        const before = [...tpl.content.querySelectorAll("*")];
        const after = [c, ...c.querySelectorAll("*")];
        let structural = r.structural || before.length !== after.length || before.some((b, i) => b.tagName !== after[i].tagName);
        const attrs = [];
        if (!structural) {
            before.forEach((b, i) => {
                const a = after[i];
                for (const name of new Set([...b.getAttributeNames(), ...a.getAttributeNames()])) {
                    const oldValue = b.getAttribute(name), newValue = a.getAttribute(name);
                    if (oldValue !== newValue) attrs.push({ name, oldValue, newValue });
                }
            });
            // Elements are the same, so whatever else changed is text.
        }
        out.push({ path: r.path, oldHtml: r.oldHtml, newHtml: c.outerHTML, oldText: r.oldText, newText: c.textContent, attrs, structural, lost: false, breaks: textBreaks(tpl.content) });
    }
    return out;
}

// Called after the server saved (or the user discarded) the drafts.
export function clearDrafts(reload) {
    for (const r of records.values()) r.el.removeAttribute("data-se-rec");
    records.clear();
    hides.length = 0;
    reportDrafts();
    if (reload) {
        previewStale = false;
        reloadPreview();
    }
}

export function updateDoc(path, text) {
    const doc = docs.get(path);
    if (doc && doc.getValue() !== text) {
        silent = true;
        doc.setValue(text);
        silent = false;
    }
}

export function download(url) {
    const a = document.createElement("a");
    a.href = url;
    a.download = "";
    document.body.append(a);
    a.click();
    a.remove();
}

export function resetEdits() {
    records.clear();
    hides.length = 0;
    reported = "";
}

/* ---------- right-click menu ---------- */

const ACTIONS = "a[href], button, [role=button], [role=link], input[type=button], input[type=submit], input[type=reset], summary, label[for]";

let following = false;

function blockActions(e) {
    if (following) return;
    const action = e.target.closest?.(ACTIONS) || e.target.closest?.("[onclick]");
    if (e.type !== "submit" && !action) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    // Ctrl+click (Cmd+click on a Mac) uses the button or link as a learner would.
    if (e.type === "click" && (e.ctrlKey || e.metaKey) && action) follow(action);
}

// Clicks a button or link for real, so the course moves to the page it leads to. Leaving for
// another file would drop unsaved edits, so the review opens first instead.
function follow(action) {
    const doc = action.ownerDocument;
    const href = action.closest("a[href]")?.getAttribute("href");
    if (href && hasDrafts()) {
        try {
            const to = new URL(href, doc.defaultView.location.href), here = doc.defaultView.location;
            if (to.pathname !== here.pathname || to.origin !== here.origin) {
                dotnet.invokeMethodAsync("OnReviewRequested");
                return;
            }
        } catch { }
    }
    following = true;
    let handled;
    try {
        handled = !action.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, view: doc.defaultView }));
    } finally {
        following = false;
    }
    // Editable pages don't follow links themselves, so when the course's own script didn't take
    // the click, go to the link's address.
    const link = action.closest("a[href]");
    if (!handled && link) {
        const url = new URL(link.getAttribute("href"), doc.defaultView.location.href).href;
        if (link.target === "_blank") window.open(url, "_blank", "noopener");
        else doc.defaultView.location.href = url;
    }
}

// The element a button's visible label is written in: its longest piece of text, so icons and
// other markup inside the button stay as they are.
function labelNodeOf(el) {
    let best = null;
    const walker = el.ownerDocument.createTreeWalker(el, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
        if (n.data.trim() && (!best || n.data.trim().length > best.data.trim().length)) best = n;
    }
    return best;
}

function editLabelForm(t, action) {
    if (action.tagName === "INPUT") {
        form("Edit button label", [{ name: "label", label: "Label", value: action.value }],
            (v) => change(t, { el: action }, () => action.setAttribute("value", v.label)));
        return;
    }
    const node = labelNodeOf(action);
    if (!node) return;
    // Shown as typed in the course, not as styled on screen (some courses show labels in capitals).
    form("Edit button label", [{ name: "label", label: "Label (as written in the course; the course's style may show it in capitals)", value: node.data.trim() }],
        (v) => change(t, { el: node.parentElement }, () => {
            const lead = node.data.match(/^\s*/)[0], trail = node.data.match(/\s*$/)[0];
            node.data = lead + v.label + trail;
        }));
}

// A CSS selector that finds this element (and as few others as possible) on the page, built from
// its id or its stable class names. Generated names (with long runs of digits or hashes) are
// skipped because they change each time a course is published.
function selectorFor(el) {
    const stable = (c) => /^[a-zA-Z_][\w-]*$/.test(c) && !/\d{3,}|^css-|^sc-|^jsx-|[a-z0-9]{8,}$/i.test(c) && !c.startsWith("se-");
    const part = (n) => {
        if (n.id && stable(n.id)) return "#" + CSS.escape(n.id);
        const classes = [...n.classList].filter(stable).map((c) => "." + CSS.escape(c)).join("");
        return n.tagName.toLowerCase() + classes;
    };
    const doc = el.ownerDocument;
    let selector = part(el);
    let n = el;
    for (let depth = 0; depth < 5 && doc.querySelectorAll(selector).length > 1 && n.parentElement && n.parentElement !== doc.body; depth++) {
        n = n.parentElement;
        selector = part(n) + " " + selector;
    }
    return { selector, count: doc.querySelectorAll(selector).length };
}

// Hiding is saved as a style rule in the page's HTML, so it works for pages a script builds too.
function hideForm(t, el) {
    const { selector, count } = selectorFor(el);
    const what = el.closest(ACTIONS) ? "button" : "element";
    form(`Hide this ${what}`, [{
        name: "info", readonly: true, value: count > 1
            ? `This hides ${count} matching elements on the page for learners (the editor can't tell them apart). You can show them again from this menu.`
            : `Learners won't see this ${what}. You can show it again from this menu.`,
    }], () => {
        hides.push({ id: nextRecord++, path: t.path, selector });
        previewHide(t.doc, selector);
        reportDrafts();
    });
}

function previewHide(doc, selector) {
    const style = doc.createElement("style");
    style.setAttribute("data-se-injected", "");
    style.setAttribute("data-se-hidden-preview", "");
    style.textContent = `${selector}{display:none!important}`;
    (doc.head || doc.documentElement).append(style);
}

function showHiddenAgain(t) {
    hides.push({ id: nextRecord++, path: t.path, unhide: true });
    t.doc.querySelectorAll("#scorm-editor-hidden, [data-se-hidden-preview]").forEach((n) => n.remove());
    reportDrafts();
}

function onContextMenu(e) {
    const doc = e.currentTarget;
    const t = targetFor(doc);
    if (!t) return;
    e.preventDefault();
    lastDoc = doc;
    let el = e.target;
    // Shielded embeds ignore the mouse; find the one under the pointer by position.
    const embed = el.closest?.(EMBEDS) || [...doc.querySelectorAll("[data-se-shield]")].find((x) => {
        const r = x.getBoundingClientRect();
        return e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom;
    });
    const link = el.closest?.("a[href]") || (doc.getSelection()?.anchorNode?.parentElement?.closest?.("a[href]") ?? null);
    const img = el.closest?.("img");
    const sel = doc.getSelection();
    const hasSelection = sel && !sel.isCollapsed && sel.toString().trim().length > 0;
    const block = el.nodeType === 1 && el !== doc.body && el !== doc.documentElement ? el : null;

    const items = [];
    if (link) {
        items.push(["Edit link…", () => editLinkForm(t, link)]);
        items.push(["Open link in new tab", () => openUrl(link.getAttribute("href"), t)]);
        items.push(["Copy link address", () => copy(link.getAttribute("href"))]);
        items.push(["Remove link", () => change(t, { el: link.parentElement, structural: true }, () => link.replaceWith(...link.childNodes))]);
    } else if (hasSelection) {
        items.push(["Add link…", () => editLinkForm(t, null, sel.getRangeAt(0).cloneRange())]);
    }
    if (embed) {
        const attr = embed.tagName === "OBJECT" ? "data" : "src";
        const src = embed.getAttribute(attr) ?? embed.querySelector("source[src]")?.getAttribute("src") ?? "";
        if (items.length) items.push(null);
        items.push([`Edit ${embed.tagName.toLowerCase()} URL…`, () => editEmbedForm(t, embed)]);
        if (src) items.push(["Open embed URL in new tab", () => openUrl(src, t)]);
        if (src) items.push(["Copy embed URL", () => copy(src)]);
    }
    if (img) {
        if (items.length) items.push(null);
        items.push(["Replace image…", () => pickImage(img)]);
        items.push(["Edit alt text…", () => form(`Alt text`, [{ name: "alt", label: "Describes the image for screen readers", value: img.getAttribute("alt") ?? "" }],
            (v) => change(t, { el: img }, () => img.setAttribute("alt", v.alt)))]);
    }
    const action = el.closest?.(ACTIONS);
    if (action) {
        if (items.length) items.push(null);
        items.push([action.closest("a[href]") ? "Go to this link (Ctrl+click)" : "Click this button (Ctrl+click)", () => follow(action)]);
        items.push(["Edit label…", () => editLabelForm(t, action)]);
        items.push(["Hide this for learners…", () => hideForm(t, action)]);
    } else if (block && !embed) {
        if (items.length) items.push(null);
        items.push(["Hide this for learners…", () => hideForm(t, block)]);
    }
    if (doc.getElementById("scorm-editor-hidden") || doc.querySelector("[data-se-hidden-preview]")) {
        items.push(["Show hidden elements again", () => showHiddenAgain(t)]);
    }
    const target = embed || img || link || block;
    if (target) {
        if (items.length) items.push(null);
        items.push([`Edit HTML of <${target.tagName.toLowerCase()}>…`, () => form("Edit HTML", [{ name: "html", label: "", value: clean(target).outerHTML, multiline: true }],
            (v) => change(t, { el: target.parentElement, structural: true }, () => { target.outerHTML = v.html; }))]);
    }
    if (!items.length) return;
    showMenu(items, doc, e.clientX, e.clientY);
}

// Applies one menu edit as a draft, recording the element it changes first.
function change(t, target, fn) {
    track(t, target);
    fn();
    shield(t.doc);
    reportDrafts();
}

function editLinkForm(t, link, range) {
    const fields = [
        { name: "href", label: "Address (URL or page in the package)", value: link?.getAttribute("href") ?? "https://" },
        { name: "text", label: "Text", value: link ? link.textContent : range.toString() },
        { name: "newTab", label: "Open in a new tab", value: link?.getAttribute("target") === "_blank", checkbox: true },
    ];
    const target = link ? { el: link } : { el: elementOf(range.commonAncestorContainer), structural: true };
    form(link ? "Edit link" : "Add link", fields, (v) => change(t, target, () => {
        let a = link;
        if (!a) {
            a = t.doc.createElement("a");
            range.deleteContents();
            range.insertNode(a);
        }
        a.setAttribute("href", v.href);
        if (v.text !== a.textContent) a.textContent = v.text;
        if (v.newTab) { a.setAttribute("target", "_blank"); a.setAttribute("rel", "noopener"); }
        else if (a.getAttribute("target") === "_blank") { a.removeAttribute("target"); if (a.getAttribute("rel") === "noopener") a.removeAttribute("rel"); }
    }));
}

function editEmbedForm(t, embed) {
    const tag = embed.tagName.toLowerCase();
    const attr = tag === "object" ? "data" : "src";
    const source = !embed.hasAttribute(attr) ? embed.querySelector("source[src]") : null;
    const holder = source || embed;
    const fields = [{ name: "src", label: "Address", value: holder.getAttribute(source ? "src" : attr) ?? "" }];
    if (tag === "iframe") fields.push({ name: "title", label: "Title (read by screen readers)", value: embed.getAttribute("title") ?? "" });
    form(`Edit ${tag} URL`, fields, (v) => change(t, { el: embed }, () => {
        holder.setAttribute(source ? "src" : attr, v.src);
        if (tag === "iframe") { if (v.title) embed.setAttribute("title", v.title); else embed.removeAttribute("title"); }
        if (tag === "video" || tag === "audio") embed.load?.();
    }));
}

function openUrl(url, t) {
    if (!url) return;
    try { window.open(new URL(url, t.win.location.href).href, "_blank", "noopener"); } catch { }
}

function copy(text) {
    navigator.clipboard?.writeText(text).catch(() => { });
}

// Where a point inside a (possibly nested) preview frame is on the editor page.
function pagePoint(doc, x, y) {
    let win = doc.defaultView;
    while (win && win !== window) {
        let fe;
        try { fe = win.frameElement; } catch { break; }
        if (!fe) break;
        const r = fe.getBoundingClientRect();
        x += r.left + fe.clientLeft;
        y += r.top + fe.clientTop;
        win = fe.ownerDocument.defaultView;
    }
    return { x, y };
}

function showMenu(items, doc, cx, cy) {
    closeMenu();
    const { x, y } = pagePoint(doc, cx, cy);
    menu = document.createElement("div");
    menu.className = "se-ctx";
    menu.setAttribute("role", "menu");
    for (const item of items) {
        if (!item) { menu.append(Object.assign(document.createElement("hr"), {})); continue; }
        const b = document.createElement("button");
        b.type = "button";
        b.setAttribute("role", "menuitem");
        b.textContent = item[0];
        b.onclick = () => { closeMenu(); item[1](); };
        menu.append(b);
    }
    document.body.append(menu);
    place(menu, x, y);
    menu.querySelector("button")?.focus();
    menu.addEventListener("keydown", (e) => {
        const buttons = [...menu.querySelectorAll("button")];
        const i = buttons.indexOf(document.activeElement);
        if (e.key === "Escape") closeMenu();
        if (e.key === "ArrowDown") { e.preventDefault(); buttons[(i + 1) % buttons.length].focus(); }
        if (e.key === "ArrowUp") { e.preventDefault(); buttons[(i - 1 + buttons.length) % buttons.length].focus(); }
    });
    setTimeout(() => document.addEventListener("mousedown", outsideClose, true), 0);
}

function place(el, x, y) {
    const w = el.offsetWidth, h = el.offsetHeight;
    el.style.left = Math.max(8, Math.min(x, window.innerWidth - w - 8)) + "px";
    el.style.top = Math.max(8, Math.min(y, window.innerHeight - h - 8)) + "px";
}

function outsideClose(e) {
    if (menu && !menu.contains(e.target)) closeMenu();
}

function closeMenu() {
    menu?.remove();
    menu = null;
    document.removeEventListener("mousedown", outsideClose, true);
}

// A small dialog with labelled fields, used by the menu's "…" items.
function form(title, fields, onApply) {
    closeMenu();
    const backdrop = document.createElement("div");
    backdrop.className = "se-dialog-backdrop";
    const box = document.createElement("form");
    box.className = "se-dialog";
    box.setAttribute("role", "dialog");
    box.setAttribute("aria-label", title);
    const h = document.createElement("h6");
    h.textContent = title;
    box.append(h);
    const inputs = {};
    for (const f of fields) {
        const id = "se-f-" + f.name;
        const wrap = document.createElement("div");
        wrap.className = f.checkbox ? "form-check mb-2" : "mb-2";
        const input = document.createElement(f.multiline ? "textarea" : "input");
        input.id = id;
        if (f.readonly) {
            const p = document.createElement("p");
            p.className = "small mb-0";
            p.textContent = f.value;
            wrap.append(p);
            box.append(wrap);
            continue;
        }
        if (f.checkbox) { input.type = "checkbox"; input.className = "form-check-input"; input.checked = !!f.value; }
        else { input.className = "form-control form-control-sm" + (f.multiline ? " se-dialog-code" : ""); input.value = f.value ?? ""; if (!f.multiline) input.type = "text"; }
        const label = document.createElement("label");
        label.htmlFor = id;
        label.className = f.checkbox ? "form-check-label" : "form-label small text-muted mb-1";
        label.textContent = f.label;
        if (f.checkbox) wrap.append(input, label);
        else { if (f.label) wrap.append(label); wrap.append(input); }
        box.append(wrap);
        inputs[f.name] = input;
    }
    const actions = document.createElement("div");
    actions.className = "d-flex justify-content-end gap-2 mt-3";
    const cancel = Object.assign(document.createElement("button"), { type: "button", className: "btn btn-sm btn-outline-secondary", textContent: "Cancel" });
    const ok = Object.assign(document.createElement("button"), { type: "submit", className: "btn btn-sm btn-primary", textContent: "Apply" });
    actions.append(cancel, ok);
    box.append(actions);
    backdrop.append(box);
    document.body.append(backdrop);
    const close = () => backdrop.remove();
    cancel.onclick = close;
    backdrop.addEventListener("mousedown", (e) => { if (e.target === backdrop) close(); });
    box.addEventListener("keydown", (e) => { if (e.key === "Escape") close(); });
    box.onsubmit = (e) => {
        e.preventDefault();
        const values = {};
        for (const f of fields) if (!f.readonly) values[f.name] = f.checkbox ? inputs[f.name].checked : inputs[f.name].value;
        close();
        onApply(values);
    };
    const first = inputs[fields.find((f) => !f.readonly)?.name];
    (first ?? ok).focus();
    first?.select?.();
}

/* ---------- images ---------- */

function onDoubleClick(e) {
    const img = e.target;
    if (!img || img.tagName !== "IMG") return;
    e.preventDefault();
    pickImage(img);
}

function pickImage(img) {
    imageInput?.remove();
    imageInput = document.createElement("input");
    imageInput.type = "file";
    imageInput.accept = "image/*";
    imageInput.hidden = true;
    document.body.append(imageInput);
    imageInput.onchange = async () => {
        const file = imageInput.files[0];
        if (file) await replaceImage(img, file);
        imageInput.remove();
        imageInput = null;
    };
    imageInput.click();
}

// The new image file is written straight away (it shows in the file list as changed); a change
// to the page itself, when the file type differs, is a draft like any other edit.
async function replaceImage(img, file) {
    const t = targetFor(img.ownerDocument);
    if (!t) return;
    const origRef = img.getAttribute("data-se-orig-src") ?? img.getAttribute("src") ?? "";
    let target = null;
    try {
        const u = new URL(origRef, t.win.location.href);
        if (u.pathname.startsWith(base + "/")) target = decodeURIComponent(u.pathname.slice(base.length + 1));
    } catch { }
    const ext = (n) => (n.match(/\.([^./]+)$/)?.[1] ?? "").toLowerCase();
    let ref = origRef;
    if (!target || ext(target) !== ext(file.name)) {
        const dir = target ? dirOf(target) : dirOf(t.path);
        target = (dir ? dir + "/" : "") + file.name;
        ref = relative(dirOf(t.path), target);
    }
    if (ref !== origRef) track(t, { el: img });
    await putFile(target, await file.arrayBuffer());
    img.setAttribute("data-se-orig-src", ref);
    img.removeAttribute("srcset");
    img.setAttribute("src", `${base}/${encodePath(target)}?v=${Date.now()}`);
    if (ref !== origRef) reportDrafts();
    dotnet.invokeMethodAsync("OnImageReplaced", target);
}

function relative(fromDir, to) {
    const a = fromDir ? fromDir.split("/") : [];
    const b = to.split("/");
    let i = 0;
    while (i < a.length && i < b.length - 1 && a[i] === b[i]) i++;
    return "../".repeat(a.length - i) + b.slice(i).join("/");
}

/* ---------- LMS runtime stub ---------- */

const lms = { data: {}, log: [], lastError: "0" };
const errors = { "0": "No error", "101": "General exception", "301": "Not initialized", "401": "Not implemented" };

function defaults() {
    return {
        "cmi.core.student_id": "learner-001",
        "cmi.core.student_name": "Learner, Preview",
        "cmi.core.lesson_status": "not attempted",
        "cmi.core.lesson_location": "",
        "cmi.core.entry": "ab-initio",
        "cmi.core.credit": "credit",
        "cmi.core.lesson_mode": "normal",
        "cmi.core.score.raw": "",
        "cmi.core.score.min": "",
        "cmi.core.score.max": "",
        "cmi.core.total_time": "0000:00:00",
        "cmi.core._children": "student_id,student_name,lesson_location,credit,lesson_status,entry,score,total_time,exit,session_time",
        "cmi.suspend_data": "",
        "cmi.launch_data": "",
        "cmi._version": "1.0",
        "cmi.learner_id": "learner-001",
        "cmi.learner_name": "Learner, Preview",
        "cmi.completion_status": "unknown",
        "cmi.success_status": "unknown",
        "cmi.location": "",
        "cmi.entry": "ab-initio",
        "cmi.mode": "normal",
        "cmi.credit": "credit",
        "cmi.score.raw": "",
        "cmi.score.scaled": "",
        "cmi.total_time": "PT0S",
    };
}

function record(fn, args, result) {
    lms.log.push({ t: new Date().toTimeString().slice(0, 8), fn, args, result });
    if (lms.log.length > 400) lms.log.shift();
    renderLms();
    return result;
}

function installLmsApi() {
    const get = (fn) => (k) => {
        const v = lms.data[k];
        lms.lastError = v === undefined ? "401" : "0";
        return record(fn, [k], v === undefined ? "" : String(v));
    };
    const set = (fn) => (k, v) => {
        lms.data[k] = String(v);
        lms.lastError = "0";
        return record(fn, [k, v], "true");
    };
    const ok = (fn) => (...a) => {
        lms.lastError = "0";
        return record(fn, a, "true");
    };
    window.API = {
        LMSInitialize: ok("LMSInitialize"), LMSFinish: ok("LMSFinish"), LMSGetValue: get("LMSGetValue"),
        LMSSetValue: set("LMSSetValue"), LMSCommit: ok("LMSCommit"), LMSGetLastError: () => lms.lastError,
        LMSGetErrorString: (c) => errors[c] ?? "Unknown error", LMSGetDiagnostic: (c) => errors[c] ?? "",
    };
    window.API_1484_11 = {
        Initialize: ok("Initialize"), Terminate: ok("Terminate"), GetValue: get("GetValue"),
        SetValue: set("SetValue"), Commit: ok("Commit"), GetLastError: () => lms.lastError,
        GetErrorString: (c) => errors[c] ?? "Unknown error", GetDiagnostic: (c) => errors[c] ?? "",
    };
}

export function resetLms() {
    lms.data = defaults();
    lms.log = [];
    renderLms();
}

let lmsEls = null;

function buildLmsPanel() {
    lmsHost.innerHTML = `
        <div class="lms-head">
            <span class="lms-label">LMS</span>
            <span class="lms-stat">Status <b data-k="status">—</b></span>
            <span class="lms-stat">Score <b data-k="score">—</b></span>
            <span class="lms-stat">Location <b data-k="loc">—</b></span>
            <span class="flex-grow-1"></span>
            <button type="button" class="btn btn-sm btn-link" data-a="log">Show calls</button>
            <button type="button" class="btn btn-sm btn-link" data-a="reset">Reset learner</button>
        </div>
        <div class="lms-log" hidden></div>`;
    lmsEls = {
        status: lmsHost.querySelector('[data-k="status"]'),
        score: lmsHost.querySelector('[data-k="score"]'),
        loc: lmsHost.querySelector('[data-k="loc"]'),
        log: lmsHost.querySelector(".lms-log"),
        toggle: lmsHost.querySelector('[data-a="log"]'),
    };
    lmsEls.toggle.onclick = () => {
        lmsEls.log.hidden = !lmsEls.log.hidden;
        lmsEls.toggle.textContent = lmsEls.log.hidden ? "Show calls" : "Hide calls";
        renderLms();
    };
    lmsHost.querySelector('[data-a="reset"]').onclick = () => {
        resetLms();
        reloadPreview();
    };
}

function renderLms() {
    if (!lmsEls) return;
    const d = lms.data;
    const used2004 = lms.log.some((e) => !e.fn.startsWith("LMS"));
    lmsEls.status.textContent = (used2004 ? `${d["cmi.completion_status"]} / ${d["cmi.success_status"]}` : d["cmi.core.lesson_status"]) || "—";
    lmsEls.score.textContent = d["cmi.core.score.raw"] || d["cmi.score.raw"] || d["cmi.score.scaled"] || "—";
    lmsEls.loc.textContent = d["cmi.core.lesson_location"] || d["cmi.location"] || "—";
    if (lmsEls.log.hidden) return;
    lmsEls.log.replaceChildren(...lms.log.slice(-150).map((e) => {
        const row = document.createElement("div");
        const t = document.createElement("span");
        t.className = "lms-t";
        t.textContent = e.t;
        const fn = document.createElement("span");
        fn.className = "lms-fn";
        fn.textContent = e.fn;
        const res = document.createElement("span");
        res.className = "lms-res";
        res.textContent = JSON.stringify(e.result);
        row.append(t, fn, `(${e.args.map((a) => JSON.stringify(a)).join(", ")}) → `, res);
        return row;
    }));
    lmsEls.log.scrollTop = lmsEls.log.scrollHeight;
}
