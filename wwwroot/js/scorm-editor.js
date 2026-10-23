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
    buildLmsPanel();
    installLmsApi();
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
    if (editedPaths.size) await saveEdited();
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
    if (live && editedPaths.size) queueVisualSave();
}

// The preview only reloads for code edits while it is on screen; otherwise it reloads once
// when the Preview tab is shown again. Reloading a large course on every keystroke is what
// made typing in the Code tab lag.
export function setPreviewVisible(visible) {
    previewVisible = visible;
    if (visible && previewStale) {
        previewStale = false;
        reloadPreview();
    }
}

function schedulePreviewReload() {
    if (!previewVisible) {
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
   authoring tools (Rise, Storyline, LMS wrappers) put the content in a nested iframe. Each edited
   page is saved to its own file: as you type when Live is on, or on Save when it is off.
   Interact mode turns editing off so links, buttons and quizzes work. */

let mode = "edit";
let targets = [];                       // [{ win, doc, path, observer }]
const hookedFrames = new WeakSet();
const editedPaths = new Set();
const originals = new Map();            // path -> file text before the first visual save, for "Undo all changes"
let lastDoc = null;
let saveTimer = 0;
let saving = false;
let imageInput = null;

const EDIT_STYLE = `
:where(h1,h2,h3,h4,h5,h6,p,li,a,span,td,th,label,button,figcaption,blockquote,dt,dd,strong,em):hover{outline:1px dashed rgba(27,110,194,.7)!important;outline-offset:2px}
img{cursor:pointer!important}
img:hover{outline:3px solid #1b6ec2!important;outline-offset:2px}
html{caret-color:#1b6ec2}`;

export function setMode(next) {
    if (next === mode) return;
    mode = next;
    if (mode === "edit") {
        enableIn(frame.contentWindow);
    } else {
        if (editedPaths.size) saveEdited();
        teardownEditing();
    }
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
        doc.addEventListener("input", onEditInput);
        doc.addEventListener("dblclick", onDoubleClick, true);
        doc.addEventListener("focusin", onFocus);
        doc.addEventListener("selectionchange", onFocus);
        doc.addEventListener("keydown", onKeyDown);
        // Courses often add their content frames after load, so watch for new ones.
        const observer = new MutationObserver(() => hookFrames(doc));
        observer.observe(doc.documentElement, { childList: true, subtree: true });
        targets.push({ win, doc, path, observer });
        hookFrames(doc);
    };
    if (doc.readyState === "loading") doc.addEventListener("DOMContentLoaded", turnOn, { once: true });
    else turnOn();
}

function hookFrames(doc) {
    for (const f of doc.querySelectorAll("iframe, frame")) {
        if (!hookedFrames.has(f)) {
            hookedFrames.add(f);
            f.addEventListener("load", () => {
                // The old document in this frame is gone; drop it and edit the new one.
                targets = targets.filter((t) => { if (t.win === f.contentWindow && t.doc !== safeDoc(f)) { t.observer.disconnect(); return false; } return true; });
                enableIn(f.contentWindow);
            });
        }
        enableIn(f.contentWindow);
    }
}

function safeDoc(f) {
    try { return f.contentDocument; } catch { return null; }
}

function teardownEditing() {
    clearTimeout(saveTimer);
    for (const t of targets) {
        try {
            t.observer.disconnect();
            t.doc.designMode = "off";
            t.doc.removeEventListener("input", onEditInput);
            t.doc.removeEventListener("dblclick", onDoubleClick, true);
            t.doc.removeEventListener("focusin", onFocus);
            t.doc.removeEventListener("selectionchange", onFocus);
            t.doc.removeEventListener("keydown", onKeyDown);
            t.doc.querySelectorAll("[data-se-injected]").forEach((n) => n.remove());
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
    // Ctrl/Cmd+S saves straight away, as in any editor.
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
        e.preventDefault();
        saveEdited();
    }
}

function onEditInput(e) {
    const t = targetFor(e.currentTarget);
    if (!t) return;
    lastDoc = t.doc;
    editedPaths.add(t.path);
    markEdited();
}

// The editor page only hears about state changes (not every keystroke), so typing stays fast.
let editState = "";
function reportState(state, paths = []) {
    if (state === editState && !paths.length) return;
    editState = state;
    dotnet.invokeMethodAsync("OnEditState", state, paths);
}

function markEdited() {
    reportState(live ? "saving" : "unsaved");
    if (live) queueVisualSave();
}

function queueVisualSave() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(saveEdited, 700);
}

// Formatting toolbar commands act on the page that last had the cursor.
export function exec(command) {
    const doc = lastDoc && targetFor(lastDoc) ? lastDoc : targets[0]?.doc;
    if (!doc) return;
    // Clicking the toolbar moves focus to the editor page; commands need it back in the course page.
    try { doc.defaultView.focus(); } catch { }
    if (command === "createLink") {
        const url = window.prompt("Link address", "https://");
        if (!url) return;
        doc.execCommand("createLink", false, url);
    } else {
        doc.execCommand(command, false, null);
    }
    const t = targetFor(doc);
    if (t) {
        editedPaths.add(t.path);
        markEdited();
    }
}

function serialize(doc) {
    const root = doc.documentElement.cloneNode(true);
    root.querySelectorAll("[data-se-injected]").forEach((n) => n.remove());
    root.querySelectorAll("*").forEach((n) => {
        for (const a of Array.from(n.attributes)) {
            if (a.name.startsWith("data-se-orig-")) {
                n.setAttribute(a.name.slice(13), a.value);
                n.removeAttribute(a.name);
            }
        }
    });
    const dt = doc.doctype;
    const doctype = dt
        ? `<!DOCTYPE ${dt.name}${dt.publicId ? ` PUBLIC "${dt.publicId}"` : ""}${dt.systemId ? ` "${dt.systemId}"` : ""}>\n`
        : "";
    return doctype + root.outerHTML + "\n";
}

// Saves every edited page to its file and updates the Code tab's copy of it.
export async function saveEdited() {
    clearTimeout(saveTimer);
    if (saving) { queueVisualSave(); return []; }
    saving = true;
    const saved = [];
    try {
        for (const path of [...editedPaths]) {
            const t = targets.find((x) => x.path === path);
            editedPaths.delete(path);
            if (!t) continue;
            const html = serialize(t.doc);
            if (!originals.has(path)) {
                const res = await fetch(`${base}/${encodePath(path)}`, { cache: "no-store" });
                if (res.ok) originals.set(path, await res.text());
            }
            await putFile(path, html);
            const doc = docs.get(path);
            if (doc && doc.getValue() !== html) {
                silent = true;
                doc.setValue(html);
                silent = false;
            }
            saved.push(path);
        }
    } finally {
        saving = false;
    }
    if (saved.length) reportState(editedPaths.size ? "saving" : "saved", saved);
    return saved;
}

// Puts every page edited in the preview back the way it was, then reloads the preview.
export async function undoAllEdits() {
    clearTimeout(saveTimer);
    editedPaths.clear();
    const restored = [];
    for (const [path, text] of originals) {
        await putFile(path, text);
        const doc = docs.get(path);
        if (doc && doc.getValue() !== text) {
            silent = true;
            doc.setValue(text);
            silent = false;
        }
        restored.push(path);
    }
    originals.clear();
    reportState("");
    reloadPreview();
    return restored;
}

// Forgets the undo snapshot, e.g. when another package is opened.
export function resetEdits() {
    clearTimeout(saveTimer);
    editedPaths.clear();
    originals.clear();
    editState = "";
}

function onDoubleClick(e) {
    const img = e.target;
    if (!img || img.tagName !== "IMG") return;
    e.preventDefault();
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
        // A different file type gets its own file next to the old image, and the page points at it.
        const dir = target ? dirOf(target) : dirOf(t.path);
        target = (dir ? dir + "/" : "") + file.name;
        ref = relative(dirOf(t.path), target);
    }
    await putFile(target, await file.arrayBuffer());
    img.setAttribute("data-se-orig-src", ref);
    img.removeAttribute("srcset");
    img.setAttribute("src", `${base}/${encodePath(target)}?v=${Date.now()}`);
    if (ref !== origRef) {
        editedPaths.add(t.path);
        markEdited();
    }
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
        "cmi.core.student_name": "Learner, Sample",
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
        "cmi.learner_name": "Learner, Sample",
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
