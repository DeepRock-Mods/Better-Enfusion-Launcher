"use strict";

// ================================================================================================
// Bridge to the C# host
// ================================================================================================
const pending = new Map();
let nextId = 1;
const host = window.chrome && window.chrome.webview;

function call(cmd, args = {}) {
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    host.postMessage({ id, cmd, args });
  });
}

host.addEventListener("message", (e) => {
  const msg = e.data;
  if (msg.event) {
    onEvent(msg.event, msg.data);
    return;
  }
  const p = pending.get(msg.id);
  if (!p) return;
  pending.delete(msg.id);
  msg.ok ? p.resolve(msg.result) : p.reject(new Error(msg.error));
});

// ================================================================================================
// State
// ================================================================================================
const BASE_GUID = "58D0FB3206B6F859";
const STATUSES = [
  { key: "idea", label: "Idea" },
  { key: "progress", label: "In progress" },
  { key: "testing", label: "Testing" },
  { key: "published", label: "Published" },
  { key: "archived", label: "Archived" },
];
const NOISE = [/is obsolete/, /managed texture/, /Unknown keyword\/data/, /Overridden prefab member/, /LogiLED/, /Can't open config file ""/,
  /ResourceViewerPresets/, /script default value overwrites/, /ResourceName picker with string/];

const S = {
  settings: null,
  projects: [],
  byGuid: new Map(),      // GUID -> project (your local copy wins over a downloaded one)
  usedBy: new Map(),      // GUID -> [projects that list it]
  tracking: {},
  sessions: {},           // project ID -> {sessions, lastSession, hours, lastLogDir}
  wb: { running: false, project: null },
  selected: null,         // project dir
  filters: { source: "local", status: "", quick: "", depends: "", usedBy: "", tags: new Set(), search: "", sort: "worked" },
  layout: "grid",
  view: "projects",
  logDir: "",
  remote: new Map(),      // GUID -> what the Workshop has now
  updater: { serverInstalled: false, busy: false, gameVersion: null },
  checking: 0,            // Workshop pages still being read
  run: null,              // the updater's current job: {phase, text, progress, lines[], done, ok}
  updOnly: false,
};

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ================================================================================================
// Data
// ================================================================================================
async function load(cmd = "init") {
  const data = await call(cmd);
  S.settings = data.settings;
  S.tracking = data.tracking || {};
  S.projects = data.projects;
  index();
  renderAll();
}

function index() {
  S.byGuid = new Map();
  S.usedBy = new Map();
  // Downloaded first so a local copy of the same GUID replaces it
  const order = [...S.projects].sort((a, b) => rank(a.source) - rank(b.source));
  for (const p of order) S.byGuid.set(p.guid, p);
  for (const p of S.projects) {
    for (const d of p.dependencies) {
      if (!S.usedBy.has(d)) S.usedBy.set(d, []);
      S.usedBy.get(d).push(p);
    }
  }
}

function rank(source) {
  return source === "local" ? 2 : source === "workshop" ? 1 : 0;
}

function onEvent(name, data) {
  if (name === "stats") {
    const p = S.projects.find((x) => x.dir === data.dir);
    if (p) {
      p.stats = data.stats;
      scheduleRender();
    }
  } else if (name === "sessions") {
    S.sessions = data || {};
    scheduleRender();
  } else if (name === "checking") {
    S.checking = data.count;
    scheduleRender();
  } else if (name === "remote") {
    S.remote.set(data.guid, data);
    S.checking = Math.max(0, S.checking - 1);
    scheduleRender();
  } else if (name === "checked") {
    S.checking = 0;
    scheduleRender();
  } else if (name === "updater") {
    onUpdater(data);
  }
}

let renderQueued = false;
function scheduleRender() {
  if (renderQueued) return;
  renderQueued = true;
  requestAnimationFrame(() => {
    renderQueued = false;
    renderCards(false);
    renderSidebar();
    renderUpdateCount();
    if (S.view === "updates") renderUpdates();
    if (S.selected) renderDrawer(false);
  });
}

const T = (p) => S.tracking[p.guid] || {};
const session = (p) => (p.source === "local" ? S.sessions[p.id] : null);
const missing = (p) => p.dependencies.filter((g) => !S.byGuid.has(g));
const usedByOf = (p) => S.usedBy.get(p.guid) || [];
const isDuplicate = (p) => S.projects.some((x) => x !== p && x.guid === p.guid);

function lastWorked(p) {
  const times = [session(p)?.lastSession, T(p).lastOpened, p.stats?.lastModified].filter(Boolean).map((t) => new Date(t).getTime());
  return times.length ? Math.max(...times) : 0;
}

/** Everything a project needs, all the way down */
function allDeps(p, seen = new Set()) {
  for (const g of p.dependencies) {
    if (seen.has(g)) continue;
    seen.add(g);
    const d = S.byGuid.get(g);
    if (d) allDeps(d, seen);
  }
  return seen;
}

/** Everything that needs a project, all the way up */
function allDependents(guid, seen = new Set()) {
  for (const p of S.usedBy.get(guid) || []) {
    if (seen.has(p.dir)) continue;
    seen.add(p.dir);
    allDependents(p.guid, seen);
  }
  return seen;
}

// ================================================================================================
// Formatting
// ================================================================================================
function ago(t) {
  if (!t) return "never";
  const s = (Date.now() - new Date(t).getTime()) / 1000;
  if (s < 60) return "just now";
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  if (s < 86400 * 30) return `${Math.floor(s / 86400)} d ago`;
  return new Date(t).toLocaleDateString();
}

function size(bytes) {
  if (bytes == null) return "...";
  const u = ["B", "KB", "MB", "GB"];
  let i = 0;
  while (bytes >= 1024 && i < u.length - 1) { bytes /= 1024; i++; }
  return `${bytes.toFixed(i ? 1 : 0)} ${u[i]}`;
}

function hue(guid) {
  let h = 0;
  for (const c of guid) h = (h * 31 + c.charCodeAt(0)) % 360;
  return h;
}

function initials(title) {
  const words = title.replace(/[^A-Za-z0-9 ]/g, " ").split(/\s+/).filter(Boolean);
  return (words.length > 1 ? words[0][0] + words[1][0] : (words[0] || "?").slice(0, 2)).toUpperCase();
}

function cover(p) {
  if (p.thumbnail) return `<img src="${esc(p.thumbnail)}" loading="lazy" onerror="this.remove()">`;
  const h = hue(p.guid);
  const style = `background:linear-gradient(135deg,hsl(${h} 55% 28%),hsl(${(h + 40) % 360} 60% 14%))`;
  return `<div style="position:absolute;inset:0;${style}"></div><div class="initials">${esc(initials(p.title))}</div>`;
}

function statusColor(key) {
  return `var(--st-${key})`;
}

function toast(text, error = false) {
  const t = $("#toast");
  t.textContent = text;
  t.classList.toggle("error", error);
  t.classList.add("show");
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => t.classList.remove("show"), 2600);
}

async function run(promise, okText) {
  try {
    const r = await promise;
    if (okText) toast(okText);
    return r;
  } catch (e) {
    toast(e.message, true);
    return null;
  }
}

// ================================================================================================
// Filtering
// ================================================================================================
function filtered() {
  const f = S.filters;
  const q = f.search.trim().toLowerCase();
  let list = S.projects.filter((p) => p.source !== "game");

  if (f.source) list = list.filter((p) => p.source === f.source);
  if (f.status === "none") list = list.filter((p) => !T(p).status);
  else if (f.status) list = list.filter((p) => T(p).status === f.status);
  if (f.quick === "fav") list = list.filter((p) => T(p).favorite);
  if (f.quick === "missing") list = list.filter((p) => missing(p).length);
  if (f.quick === "week") list = list.filter((p) => Date.now() - lastWorked(p) < 7 * 86400000);
  if (f.quick === "git") list = list.filter((p) => p.stats?.gitBranch);
  if (f.quick === "dupe") list = list.filter(isDuplicate);
  if (f.quick === "unused") list = list.filter((p) => p.source === "workshop" && !usedByOf(p).length);
  if (f.quick === "updates") list = list.filter(hasUpdate);
  if (f.quick === "oldgame") list = list.filter(builtForOlderGame);
  if (f.depends) list = list.filter((p) => allDeps(p).has(f.depends));
  if (f.usedBy) {
    const root = S.byGuid.get(f.usedBy);
    const deps = root ? allDeps(root) : new Set();
    list = list.filter((p) => deps.has(p.guid));
  }
  for (const tag of f.tags) list = list.filter((p) => (T(p).tags || []).includes(tag));
  if (q) {
    list = list.filter((p) => [p.title, p.id, p.guid, ...(T(p).tags || []), T(p).notes].join(" ").toLowerCase().includes(q));
  }

  const by = {
    worked: (a, b) => lastWorked(b) - lastWorked(a),
    modified: (a, b) => new Date(b.stats?.lastModified || 0) - new Date(a.stats?.lastModified || 0),
    name: (a, b) => a.title.localeCompare(b.title),
    size: (a, b) => (b.stats?.sizeBytes || 0) - (a.stats?.sizeBytes || 0),
    used: (a, b) => usedByOf(b).length - usedByOf(a).length,
  }[f.sort];
  // Favorites float to the top
  return list.sort((a, b) => (T(b).favorite ? 1 : 0) - (T(a).favorite ? 1 : 0) || by(a, b));
}

// ================================================================================================
// Rendering
// ================================================================================================
function renderAll() {
  renderSidebar();
  renderCards(true);
  if (S.selected) renderDrawer(true);
  if (S.view === "graph") renderGraph();
}

function filterItem(label, count, active, dot, onClick) {
  const el = document.createElement("div");
  el.className = "filter" + (active ? " active" : "");
  el.innerHTML = `${dot ? `<span class="dot" style="background:${dot}"></span>` : ""}<span>${esc(label)}</span><span class="count">${count}</span>`;
  el.onclick = onClick;
  return el;
}

function renderSidebar() {
  const all = S.projects.filter((p) => p.source !== "game");
  const f = S.filters;

  const src = $("#f-source");
  src.innerHTML = "";
  [["", "All", all.length], ["local", "My projects", all.filter((p) => p.source === "local").length],
   ["workshop", "Downloaded mods", all.filter((p) => p.source === "workshop").length]].forEach(([key, label, n]) =>
    src.append(filterItem(label, n, f.source === key, null, () => { f.source = key; renderAll(); })));

  const st = $("#f-status");
  st.innerHTML = "";
  st.append(filterItem("Any", all.length, !f.status, null, () => { f.status = ""; renderAll(); }));
  for (const s of STATUSES) {
    const n = all.filter((p) => T(p).status === s.key).length;
    st.append(filterItem(s.label, n, f.status === s.key, statusColor(s.key), () => { f.status = f.status === s.key ? "" : s.key; renderAll(); }));
  }
  st.append(filterItem("No status", all.filter((p) => !T(p).status).length, f.status === "none", "#3a4150", () => { f.status = f.status === "none" ? "" : "none"; renderAll(); }));

  const qk = $("#f-quick");
  qk.innerHTML = "";
  [["fav", "★ Favorites", all.filter((p) => T(p).favorite).length],
   ["week", "Worked on this week", all.filter((p) => Date.now() - lastWorked(p) < 7 * 86400000).length],
   ["missing", "Missing dependencies", all.filter((p) => missing(p).length).length],
   ["dupe", "Local copy of a download", all.filter(isDuplicate).length],
   ["unused", "Downloads nothing uses", all.filter((p) => p.source === "workshop" && !usedByOf(p).length).length],
   ["updates", "⬆ Updates available", all.filter(hasUpdate).length],
   ["oldgame", "Built for an older game", all.filter(builtForOlderGame).length],
   ["git", "Under git", all.filter((p) => p.stats?.gitBranch).length]].forEach(([key, label, n]) =>
    qk.append(filterItem(label, n, f.quick === key, null, () => { f.quick = f.quick === key ? "" : key; renderAll(); })));

  const options = all.slice().sort((a, b) => a.title.localeCompare(b.title))
    .map((p) => `<option value="${p.guid}">${esc(p.title)}${p.source === "workshop" ? "  (mod)" : ""}</option>`).join("");
  const baseOption = `<option value="${BASE_GUID}">Arma Reforger (base game)</option>`;
  $("#f-depends").innerHTML = `<option value="">Anything</option>${baseOption}${options}`;
  $("#f-depends").value = f.depends;
  $("#f-usedby").innerHTML = `<option value="">Anything</option>${options}`;
  $("#f-usedby").value = f.usedBy;

  const tags = new Map();
  for (const p of all) for (const t of T(p).tags || []) tags.set(t, (tags.get(t) || 0) + 1);
  const tg = $("#f-tags");
  tg.innerHTML = tags.size ? "" : `<span class="muted" style="font-size:12px">Tag projects in their details.</span>`;
  [...tags.entries()].sort().forEach(([t, n]) => {
    const el = document.createElement("span");
    el.className = "tag" + (f.tags.has(t) ? " active" : "");
    el.textContent = `${t} ${n}`;
    el.onclick = () => { f.tags.has(t) ? f.tags.delete(t) : f.tags.add(t); renderAll(); };
    tg.append(el);
  });
}

function badges(p) {
  const out = [];
  const t = T(p);
  if (t.status) {
    const s = STATUSES.find((x) => x.key === t.status);
    out.push(`<span class="badge status" style="background:${statusColor(t.status)}">${esc(s ? s.label : t.status)}</span>`);
  }
  if (S.wb.running && S.wb.project && S.wb.project === p.id && p.source === "local") out.push(`<span class="badge good">Open in Workbench</span>`);
  if (p.source === "workshop") out.push(`<span class="badge">${p.version ? "v" + esc(p.version) : "Downloaded"}</span>`);
  if (hasUpdate(p)) out.push(`<span class="badge update" title="The Workshop has a newer version">⬆ ${esc(S.remote.get(p.guid).version)}</span>`);
  if (builtForOlderGame(p)) out.push(`<span class="badge bad" title="Built for game ${esc(p.gameVersion)}, you have ${esc(S.updater.gameVersion)}">built for ${esc(majorMinor(p.gameVersion))}</span>`);
  const miss = missing(p).length;
  if (miss) out.push(`<span class="badge bad">${miss} missing</span>`);
  const deps = p.dependencies.filter((g) => g !== BASE_GUID).length;
  if (deps) out.push(`<span class="badge">needs ${deps}</span>`);
  const used = usedByOf(p).length;
  if (used) out.push(`<span class="badge accent">used by ${used}</span>`);
  if (p.stats?.gitBranch) out.push(`<span class="badge">⎇ ${esc(p.stats.gitBranch)}</span>`);
  for (const tag of (t.tags || []).slice(0, 3)) out.push(`<span class="badge">#${esc(tag)}</span>`);
  return out.join("");
}

function renderCards(animate) {
  const list = filtered();
  const f = S.filters;
  $("#list-title").textContent = f.source === "local" ? "My projects" : f.source === "workshop" ? "Downloaded mods" : "All projects";
  $("#list-count").textContent = `${list.length} shown`;
  $("#empty").classList.toggle("hidden", list.length > 0);

  renderRecent();
  const box = $("#cards");
  box.className = `cards ${S.layout}`;
  box.innerHTML = list.map((p, i) => {
    const s = session(p);
    const worked = lastWorked(p);
    const sub = [p.id, worked ? "worked " + ago(worked) : p.source === "workshop" ? "downloaded mod" : "", s ? `${s.sessions} sessions` : ""].filter(Boolean).join("  ·  ");
    return `<div class="card${S.selected === p.dir ? " selected" : ""}" data-dir="${esc(p.dir)}" style="${animate ? `animation-delay:${Math.min(i, 30) * 18}ms` : "animation:none"}">
      <span class="fav${T(p).favorite ? " on" : ""}" data-fav="${p.guid}">★</span>
      <div class="cover">${cover(p)}</div>
      <div class="body"><div class="title">${esc(p.title)}</div><div class="sub">${esc(sub)}</div><div class="badges">${badges(p)}</div></div>
    </div>`;
  }).join("");
}

// ------------------------------------------------------------------------------------------------
// Drawer
// ------------------------------------------------------------------------------------------------
function select(dir) {
  S.selected = dir;
  renderCards(false);
  renderDrawer(true);
  if (S.view === "graph") renderGraph();
}

function renderDrawer(full) {
  const p = S.projects.find((x) => x.dir === S.selected);
  const d = $("#drawer");
  if (!p) {
    d.classList.add("hidden");
    return;
  }
  d.classList.remove("hidden");

  // Keep the notes box (and its cursor) when only stats changed
  if (!full && d.dataset.dir === p.dir) {
    const stats = $("#d-stats", d);
    if (stats) stats.innerHTML = statsHtml(p);
    return;
  }
  d.dataset.dir = p.dir;

  const t = T(p);
  const local = p.source === "local";
  const s = session(p);
  d.innerHTML = `
    <div class="hero">${cover(p)}<button class="btn icon close" id="d-close">✕</button></div>
    <div class="head">
      <h1>${esc(p.title)}</h1>
      <div class="ids">
        <span class="copy" data-copy="${p.guid}" title="Copy GUID">${p.guid}</span>
        <span>${esc(p.id)}</span>
        <span>${local ? "My project" : p.source === "workshop" ? "Downloaded mod" : "Base game"}</span>
      </div>
    </div>
    <div class="actions">
      ${local ? `<button class="btn primary big" id="d-open">Open in Workbench</button>` : ""}
      <button class="btn" id="d-folder">Open folder</button>
      ${local ? `<button class="btn" id="d-code">VS Code</button>` : ""}
      ${s?.lastLogDir ? `<button class="btn" id="d-log">Last session log</button>` : ""}
      <button class="btn" id="d-path">Copy path</button>
      ${local ? `<button class="btn" id="d-shortcut" title="A desktop icon that opens this project straight in Workbench">Desktop shortcut</button>` : ""}
    </div>
    <section>
      <h3>Tracking</h3>
      <div class="statuses" id="d-status">
        ${STATUSES.map((st) => `<span class="status-btn${t.status === st.key ? " active" : ""}" data-status="${st.key}" style="${t.status === st.key ? `background:${statusColor(st.key)}` : ""}">${st.label}</span>`).join("")}
        <span class="status-btn${t.favorite ? " active" : ""}" id="d-fav" style="${t.favorite ? "background:var(--warn)" : ""}">★ Favorite</span>
      </div>
      <div class="tag-editor" style="margin-top:12px">
        ${(t.tags || []).map((tag) => `<span class="tag">#${esc(tag)}<b data-untag="${esc(tag)}">✕</b></span>`).join("")}
        <input class="tag-input" id="d-tag" placeholder="+ add tag">
      </div>
      <textarea id="d-notes" placeholder="Notes, to-dos, ideas..." style="margin-top:12px">${esc(t.notes || "")}</textarea>
    </section>
    <section><h3>Stats</h3><div class="stats" id="d-stats">${statsHtml(p)}</div></section>
    <section>
      <h3>Dependencies</h3>
      <svg class="mini-graph" id="d-mini"></svg>
      <div style="margin-top:10px">${depTree(p, new Set([p.guid]), 0)}</div>
      ${p.dependencies.length ? "" : `<div class="muted">No dependencies.</div>`}
    </section>
    <section>
      <h3>Used by (${usedByOf(p).length})</h3>
      ${usedByOf(p).map((u) => depRow(u, u.guid, false, false)).join("") || `<div class="muted">Nothing depends on this.</div>`}
    </section>
    ${p.source === "workshop" ? workshopSection(p) : ""}
  `;
  bindDrawer(p);
  drawMiniGraph(p);
}

function statsHtml(p) {
  const st = p.stats;
  const s = session(p);
  const t = T(p);
  const cell = (k, v) => `<div class="stat"><div class="k">${k}</div><div class="v">${v}</div></div>`;
  const cells = [
    cell("Size", st ? size(st.sizeBytes) : "..."),
    cell("Files", st ? st.files.toLocaleString() : "..."),
  ];
  if (p.source === "local") {
    cells.push(cell("Scripts", st ? st.scripts : "..."), cell("Prefabs", st ? st.prefabs : "..."),
      cell("Layouts", st ? st.layouts : "..."), cell("Worlds", st ? st.worlds : "..."),
      cell("Last change", st?.lastModified ? ago(st.lastModified) : "..."),
      cell("Workbench sessions", s ? s.sessions : 0),
      cell("Time in Workbench", s ? `${s.hours.toFixed(1)} h` : "0 h"),
      cell("Last session", s ? ago(s.lastSession) : "never"),
      cell("Opened from Hub", t.openCount ? `${t.openCount}×` : "never"),
      cell("Git", st?.gitBranch ? esc(st.gitBranch) : "no"));
  }
  return cells.join("");
}

function depRow(dep, guid, missingDep, twisty, open) {
  const name = dep ? dep.title : guid === BASE_GUID ? "Arma Reforger" : guid;
  const src = missingDep ? "MISSING" : dep ? (dep.source === "local" ? "project" : dep.source === "workshop" ? "mod" : "game") : "";
  return `<div class="dep${missingDep ? " missing" : ""}" ${dep ? `data-goto="${esc(dep.dir)}"` : ""} ${twisty ? `data-toggle="1"` : ""}>
    <span class="twisty">${twisty ? (open ? "▾" : "▸") : ""}</span>
    <span class="name">${esc(name)}</span><span class="src">${src}</span></div>`;
}

function depTree(p, seen, depth) {
  return p.dependencies.map((g) => {
    const dep = S.byGuid.get(g);
    const children = dep && dep.dependencies.some((c) => !seen.has(c)) && depth < 6;
    const row = depRow(dep, g, !dep, children, depth < 1);
    if (!children) return row;
    const next = new Set([...seen, g]);
    return row + `<div class="dep-children${depth < 1 ? "" : " hidden"}">${depTree(dep, next, depth + 1)}</div>`;
  }).join("");
}

let notesTimer = null;
function bindDrawer(p) {
  const d = $("#drawer");
  $("#d-close", d).onclick = () => { S.selected = null; d.classList.add("hidden"); renderCards(false); };
  const open = $("#d-open", d);
  if (open) open.onclick = () => openWorkbench(p);
  $("#d-folder", d).onclick = () => run(call("openPath", { path: p.dir }));
  const code = $("#d-code", d);
  if (code) code.onclick = () => run(call("openInCode", { path: p.dir }));
  const log = $("#d-log", d);
  if (log) log.onclick = () => { S.logDir = session(p).lastLogDir; setView("logs"); };
  $("#d-path", d).onclick = () => run(call("copy", { text: p.dir }), "Path copied");
  const upd = $("#d-update", d);
  if (upd) upd.onclick = () => updateMods([p.guid]);
  const page = $("#d-page", d);
  if (page) page.onclick = () => run(call("openUrl", { url: workshopUrl(p.guid) }));
  const recheck = $("#d-recheck", d);
  if (recheck) recheck.onclick = () => run(call("checkUpdates", { guids: [p.guid] }), "Checking the Workshop...");
  const shortcut = $("#d-shortcut", d);
  if (shortcut) shortcut.onclick = () => run(call("projectShortcut", { guid: p.guid }), "Shortcut added to your desktop");

  $$("[data-status]", d).forEach((el) => el.onclick = () => {
    const status = T(p).status === el.dataset.status ? "" : el.dataset.status;
    track(p, { status }, true);
  });
  $("#d-fav", d).onclick = () => track(p, { favorite: !T(p).favorite }, true);
  $$("[data-untag]", d).forEach((el) => el.onclick = () => track(p, { tags: (T(p).tags || []).filter((t) => t !== el.dataset.untag) }, true));
  const tagInput = $("#d-tag", d);
  tagInput.onkeydown = (e) => {
    if (e.key !== "Enter") return;
    const tag = tagInput.value.trim().replace(/^#/, "").toLowerCase();
    if (tag) track(p, { tags: [...(T(p).tags || []), tag] }, true).then(() => $("#d-tag")?.focus());
  };
  const notes = $("#d-notes", d);
  notes.oninput = () => {
    clearTimeout(notesTimer);
    notesTimer = setTimeout(() => track(p, { notes: notes.value }, false), 500);
  };

  $$("[data-toggle]", d).forEach((el) => el.querySelector(".twisty").onclick = (e) => {
    e.stopPropagation();
    const kids = el.nextElementSibling;
    kids.classList.toggle("hidden");
    el.querySelector(".twisty").textContent = kids.classList.contains("hidden") ? "▸" : "▾";
  });
  $$("[data-goto]", d).forEach((el) => el.onclick = () => select(el.dataset.goto));
}

async function track(p, patch, rerender) {
  const t = await run(call("setTracking", { guid: p.guid, patch }));
  if (!t) return;
  S.tracking[p.guid] = t;
  if (rerender) {
    renderSidebar();
    renderCards(false);
    renderDrawer(true);
  }
}

async function openWorkbench(p) {
  if (!p) return;
  if (S.wb.running && !confirm(`Workbench is already running${S.wb.project ? ` (${S.wb.project})` : ""}. Open another one for ${p.title}?`)) return;
  const t = await run(call("openWorkbench", { guid: p.guid }), `Opening ${p.title} in Workbench...`);
  if (t) {
    S.tracking[p.guid] = t;
    renderCards(false);
    if (S.selected) renderDrawer(false);
  }
}

// ------------------------------------------------------------------------------------------------
// Mini graph: what the project needs on the left, what needs it on the right
// ------------------------------------------------------------------------------------------------
function drawMiniGraph(p) {
  const svg = $("#d-mini");
  const w = svg.clientWidth || 400, h = 190;
  svg.setAttribute("viewBox", `0 0 ${w} ${h}`);
  const left = p.dependencies.slice(0, 7);
  const right = usedByOf(p).slice(0, 7);
  const node = (x, y, label, cls, dir) =>
    `<g class="g-node ${cls}" ${dir ? `data-goto="${esc(dir)}"` : ""} transform="translate(${x},${y})">
      <rect x="-62" y="-12" width="124" height="24"></rect>
      <text text-anchor="middle" y="4" font-size="11">${esc(label.length > 18 ? label.slice(0, 17) + "…" : label)}</text></g>`;
  const col = (items, x) => items.map((_, i) => ({ x, y: (h / (items.length + 1)) * (i + 1) }));
  const lp = col(left, 72), rp = col(right, w - 72), c = { x: w / 2, y: h / 2 };
  let edges = "", nodes = "";
  left.forEach((g, i) => {
    const d = S.byGuid.get(g);
    edges += `<path class="g-edge hot" d="M${c.x - 62},${c.y} C${c.x - 110},${c.y} ${lp[i].x + 110},${lp[i].y} ${lp[i].x + 62},${lp[i].y}"></path>`;
    nodes += node(lp[i].x, lp[i].y, d ? d.title : g === BASE_GUID ? "Arma Reforger" : g, d ? "" : "missing", d?.dir);
  });
  right.forEach((u, i) => {
    edges += `<path class="g-edge" d="M${rp[i].x - 62},${rp[i].y} C${rp[i].x - 110},${rp[i].y} ${c.x + 110},${c.y} ${c.x + 62},${c.y}"></path>`;
    nodes += node(rp[i].x, rp[i].y, u.title, "", u.dir);
  });
  nodes += node(c.x, c.y, p.title, "sel", null);
  svg.innerHTML = edges + nodes;
  $$("[data-goto]", svg).forEach((el) => el.onclick = () => select(el.dataset.goto));
}

// ================================================================================================
// Full graph
// ================================================================================================
const G = { scale: 1, x: 0, y: 0, width: 0, height: 0 };

function renderGraph() {
  const svg = $("#graph");
  const all = $("#g-workshop").checked;

  // Nodes: your projects (or everything) plus whatever they need
  const guids = new Set();
  for (const p of S.projects) {
    if (p.source === "local" || (all && p.source === "workshop")) {
      guids.add(p.guid);
      for (const g of allDeps(p)) guids.add(g);
    }
  }

  const nodes = [...guids].map((g) => ({ guid: g, p: S.byGuid.get(g) }));
  const level = new Map();
  const depth = (g, stack = new Set()) => {
    if (level.has(g)) return level.get(g);
    if (stack.has(g)) return 0;
    stack.add(g);
    const p = S.byGuid.get(g);
    const deps = p ? p.dependencies.filter((d) => guids.has(d)) : [];
    const l = deps.length ? 1 + Math.max(...deps.map((d) => depth(d, stack))) : 0;
    level.set(g, l);
    return l;
  };
  nodes.forEach((n) => depth(n.guid));

  const columns = new Map();
  for (const n of nodes) {
    const l = level.get(n.guid);
    if (!columns.has(l)) columns.set(l, []);
    columns.get(l).push(n);
  }

  const W = 210, H = 44, GX = 90, GY = 14;
  const pos = new Map();
  let maxY = 0;
  for (const [l, col] of columns) {
    col.sort((a, b) => (a.p?.title || a.guid).localeCompare(b.p?.title || b.guid));
    col.forEach((n, i) => {
      pos.set(n.guid, { x: 40 + l * (W + GX), y: 40 + i * (H + GY) });
      maxY = Math.max(maxY, 40 + i * (H + GY) + H);
    });
  }
  G.width = 40 + columns.size * (W + GX);
  G.height = maxY + 40;

  // Highlight the selected project's whole chain
  const sel = S.projects.find((p) => p.dir === S.selected);
  const chain = new Set();
  if (sel) {
    chain.add(sel.guid);
    for (const g of allDeps(sel)) chain.add(g);
    for (const dir of allDependents(sel.guid)) {
      const q = S.projects.find((x) => x.dir === dir);
      if (q) chain.add(q.guid);
    }
  }

  let edges = "";
  for (const n of nodes) {
    if (!n.p) continue;
    const a = pos.get(n.guid);
    for (const d of n.p.dependencies) {
      const b = pos.get(d);
      if (!b) continue;
      const hot = sel && chain.has(n.guid) && chain.has(d);
      const cls = sel ? (hot ? "hot" : "dim") : "";
      const x1 = a.x, y1 = a.y + H / 2, x2 = b.x + W, y2 = b.y + H / 2;
      edges += `<path class="g-edge ${cls}" d="M${x1},${y1} C${x1 - 60},${y1} ${x2 + 60},${y2} ${x2},${y2}"></path>`;
    }
  }

  let boxes = "";
  for (const n of nodes) {
    const a = pos.get(n.guid);
    const p = n.p;
    const title = p ? p.title : n.guid === BASE_GUID ? "Arma Reforger" : "Missing " + n.guid;
    const src = p ? (p.source === "local" ? "PROJECT" : p.source === "workshop" ? "MOD" : "GAME") : "NOT FOUND";
    const color = p ? (p.source === "local" ? "var(--accent)" : p.source === "workshop" ? "var(--info)" : "var(--muted)") : "var(--bad)";
    const cls = [p ? "" : "missing", sel && p && p.dir === sel.dir ? "sel" : "", sel && !chain.has(n.guid) ? "dim" : ""].join(" ");
    const used = (S.usedBy.get(n.guid) || []).length;
    boxes += `<g class="g-node ${cls}" ${p ? `data-goto="${esc(p.dir)}"` : ""} transform="translate(${a.x},${a.y})">
      <rect width="${W}" height="${H}"></rect>
      <rect class="bar" width="4" height="${H - 16}" x="8" y="8" fill="${color}"></rect>
      <text x="20" y="19">${esc(title.length > 26 ? title.slice(0, 25) + "…" : title)}</text>
      <text class="src" x="20" y="34">${src}${used ? `  ·  used by ${used}` : ""}</text></g>`;
  }

  svg.innerHTML = `<g id="g-root">${edges}${boxes}</g>`;
  $$("[data-goto]", svg).forEach((el) => el.addEventListener("click", () => {
    if (G.moved) return;
    select(el.dataset.goto);
  }));
  applyGraphTransform();
}

function applyGraphTransform() {
  const root = $("#g-root");
  if (root) root.setAttribute("transform", `translate(${G.x},${G.y}) scale(${G.scale})`);
}

function fitGraph() {
  const svg = $("#graph");
  const w = svg.clientWidth, h = svg.clientHeight;
  G.scale = Math.min(1.2, Math.min(w / G.width, h / G.height) * 0.95);
  G.x = (w - G.width * G.scale) / 2;
  G.y = Math.max(10, (h - G.height * G.scale) / 2);
  applyGraphTransform();
}

function bindGraph() {
  const svg = $("#graph");
  let drag = null;
  svg.addEventListener("mousedown", (e) => { drag = { x: e.clientX, y: e.clientY, gx: G.x, gy: G.y }; G.moved = false; svg.classList.add("dragging"); });
  window.addEventListener("mousemove", (e) => {
    if (!drag) return;
    const dx = e.clientX - drag.x, dy = e.clientY - drag.y;
    if (Math.abs(dx) + Math.abs(dy) > 4) G.moved = true;
    G.x = drag.gx + dx;
    G.y = drag.gy + dy;
    applyGraphTransform();
  });
  window.addEventListener("mouseup", () => { drag = null; svg.classList.remove("dragging"); setTimeout(() => (G.moved = false), 0); });
  svg.addEventListener("wheel", (e) => {
    e.preventDefault();
    const r = svg.getBoundingClientRect();
    const mx = e.clientX - r.left, my = e.clientY - r.top;
    const k = e.deltaY < 0 ? 1.12 : 1 / 1.12;
    const s = Math.min(3, Math.max(0.15, G.scale * k));
    G.x = mx - ((mx - G.x) * s) / G.scale;
    G.y = my - ((my - G.y) * s) / G.scale;
    G.scale = s;
    applyGraphTransform();
  }, { passive: false });
  $("#g-fit").onclick = fitGraph;
  $("#g-workshop").onchange = () => { renderGraph(); fitGraph(); };
}

// ================================================================================================
// Logs
// ================================================================================================
async function renderLogs() {
  const data = await run(call("logErrors", { dir: S.logDir }));
  if (!data) return;
  S.logDir = data.dir || "";
  const hide = $("#log-noise").checked;
  const errors = data.errors.filter((e) => !hide || !NOISE.some((rx) => rx.test(e.text)));
  $("#log-info").textContent = data.dir ? `${data.dir.split(/[\\/]/).pop()}${data.project ? "  ·  project " + data.project : ""}  ·  ${errors.length} distinct errors` : "No logs found";
  const banner = $("#log-banner");
  banner.classList.toggle("hidden", !data.compileFailed);
  banner.textContent = "Scripts failed to compile in this session. Workbench runs without its game API until this is fixed and Workbench restarts.";
  $("#log-list").innerHTML = errors.map((e) => `<div class="log-row${e.module === "SCRIPT" ? " script" : ""}">
      <span class="mod">${esc(e.module)}</span><span class="txt">${esc(e.text)}</span><span class="n">${e.count > 1 ? "×" + e.count : ""}</span></div>`).join("")
    || `<div class="empty">No errors in this session.</div>`;
}

// ================================================================================================
// Modals: new project, settings
// ================================================================================================
function modal(html) {
  $("#modal-card").innerHTML = html;
  $("#modal").classList.remove("hidden");
}

function closeModal() {
  $("#modal").classList.add("hidden");
}

function showNewProject() {
  const choices = S.projects.filter((p) => p.source !== "game").sort((a, b) => a.title.localeCompare(b.title));
  modal(`<h2>New project</h2>
    <div class="row"><label>Name</label><input type="text" id="n-title" placeholder="My Awesome Mod"></div>
    <div class="row"><label>Depends on (the base game is always included)</label>
      <input type="text" id="n-filter" placeholder="Filter..." style="margin-bottom:6px">
      <div class="pick-list" id="n-deps">${choices.map((p) => `<label data-name="${esc(p.title.toLowerCase())}"><input type="checkbox" value="${p.guid}"> ${esc(p.title)} <span class="muted">${p.source === "workshop" ? "mod" : "project"}</span></label>`).join("")}</div>
    </div>
    <div class="muted">Creates the folder with an addon.gproj (new GUID) and Scripts/Game in your Workbench addons folder.</div>
    <div class="foot"><button class="btn" id="n-cancel">Cancel</button><button class="btn primary" id="n-create">Create</button></div>`);
  $("#n-title").focus();
  $("#n-filter").oninput = (e) => $$("#n-deps label").forEach((l) => l.classList.toggle("hidden", !l.dataset.name.includes(e.target.value.toLowerCase())));
  $("#n-cancel").onclick = closeModal;
  $("#n-create").onclick = async () => {
    const dependencies = $$("#n-deps input:checked").map((i) => i.value);
    const r = await run(call("newProject", { title: $("#n-title").value, dependencies }));
    if (!r) return;
    closeModal();
    await load("rescan");
    const p = S.projects.find((x) => x.guid === r.guid);
    if (p) select(p.dir);
    toast("Project created");
  };
}

function showSettings() {
  const s = S.settings;
  const row = (id, label, value, kind) => `<div class="row"><label>${label}</label><div class="inline">
    <input type="text" id="${id}" value="${esc(value)}"><button class="btn" data-browse="${id}" data-kind="${kind}">Browse</button></div></div>`;
  modal(`<h2>Settings</h2>
    ${row("s-wb", "Workbench executable", s.workbenchExe, "file")}
    ${row("s-game", "Game folder", s.gameDir, "folder")}
    ${row("s-local", "Your Workbench addons folder", s.localAddonsDir, "folder")}
    ${row("s-workshop", "Downloaded mods folder", s.workshopAddonsDir, "folder")}
    ${row("s-logs", "Workbench logs folder", s.logsDir, "folder")}
    ${row("s-server", "Updater server folder (empty: the Hub's own copy)", s.serverDir || "", "folder")}
    <div class="row"><label class="check"><input type="checkbox" id="s-check" ${s.checkUpdatesOnStart !== false ? "checked" : ""}> Check the Workshop for mod updates when the Hub starts</label></div>
    <div class="row"><label>Shortcuts</label><button class="btn" id="s-shortcuts">Add Reforger Hub to the desktop and Start menu</button></div>
    <div class="row"><label>Extra project folders (one per line)</label><textarea id="s-extra" style="min-height:70px">${esc((s.extraDirs || []).join("\n"))}</textarea></div>
    <div class="foot"><button class="btn" id="s-cancel">Cancel</button><button class="btn primary" id="s-save">Save</button></div>`);
  $$("[data-browse]").forEach((b) => b.onclick = async () => {
    const input = $("#" + b.dataset.browse);
    const path = await call(b.dataset.kind === "file" ? "browseFile" : "browseFolder", { start: input.value });
    if (path) input.value = path;
  });
  $("#s-shortcuts").onclick = () => run(call("appShortcuts"), "Shortcuts added (desktop + Start menu)");
  $("#s-cancel").onclick = closeModal;
  $("#s-save").onclick = async () => {
    const settings = {
      workbenchExe: $("#s-wb").value, gameDir: $("#s-game").value, localAddonsDir: $("#s-local").value,
      workshopAddonsDir: $("#s-workshop").value, logsDir: $("#s-logs").value,
      serverDir: $("#s-server").value, checkUpdatesOnStart: $("#s-check").checked,
      extraDirs: $("#s-extra").value.split("\n").map((x) => x.trim()).filter(Boolean),
    };
    const data = await run(call("saveSettings", { settings }), "Settings saved");
    if (!data) return;
    closeModal();
    S.settings = data.settings;
    S.projects = data.projects;
    S.tracking = data.tracking;
    index();
    renderAll();
  };
}

// ================================================================================================
// Quick open: recent strip and the Ctrl+K palette
// ================================================================================================
function recentProjects() {
  return S.projects.filter((p) => p.source === "local" && lastWorked(p)).sort((a, b) => lastWorked(b) - lastWorked(a));
}

function renderRecent() {
  const box = $("#recent");
  if (S.filters.source === "workshop") { box.innerHTML = ""; return; }
  const list = recentProjects().slice(0, 6);
  box.innerHTML = list.length ? `<span class="r-label">Jump back in</span>` + list.map((p, i) => `
    <div class="r-item" style="animation-delay:${i * 40}ms">
      <span class="r-title" data-show="${esc(p.dir)}">${esc(p.title)}</span>
      <span class="r-when">${ago(lastWorked(p))}</span>
      <button class="btn primary" data-launch="${esc(p.dir)}">Open</button>
    </div>`).join("") : "";
  $$("[data-show]", box).forEach((el) => el.onclick = () => select(el.dataset.show));
  $$("[data-launch]", box).forEach((el) => el.onclick = () => openWorkbench(S.projects.find((x) => x.dir === el.dataset.launch)));
}

const P = { items: [], index: 0 };

function openPalette() {
  closeModal();
  $("#palette").classList.remove("hidden");
  const input = $("#palette-input");
  input.value = "";
  P.index = 0;
  renderPalette();
  input.focus();
}

function closePalette() {
  $("#palette").classList.add("hidden");
}

/** Letters in order, earlier and tighter matches score higher */
function fuzzy(text, query) {
  text = text.toLowerCase();
  if (!query) return 1;
  const direct = text.indexOf(query);
  if (direct >= 0) return 1000 - direct;
  let score = 0, at = 0;
  for (const c of query) {
    const i = text.indexOf(c, at);
    if (i < 0) return 0;
    score += i === at ? 5 : 1;
    at = i + 1;
  }
  return score;
}

function renderPalette() {
  const q = $("#palette-input").value.trim().toLowerCase();
  P.items = S.projects.filter((p) => p.source === "local")
    .map((p) => ({ p, score: Math.max(fuzzy(p.title, q), fuzzy(p.id, q) * 0.9, fuzzy(p.guid, q) * 0.5) }))
    .filter((x) => x.score > 0)
    .sort((a, b) => (q ? b.score - a.score : 0) || (lastWorked(b.p) - lastWorked(a.p)) || a.p.title.localeCompare(b.p.title))
    .slice(0, 12)
    .map((x) => x.p);
  if (q) P.index = 0;
  P.index = Math.min(P.index, Math.max(0, P.items.length - 1));
  $("#palette-list").innerHTML = P.items.map((p, i) => {
    const h = hue(p.guid);
    const when = lastWorked(p) ? "worked " + ago(lastWorked(p)) : "not opened yet";
    const status = T(p).status ? "  ·  " + esc(T(p).status) : "";
    return `<div class="p-row${i === P.index ? " active" : ""}" data-i="${i}">
      <div class="p-icon" style="background:linear-gradient(135deg,hsl(${h} 55% 30%),hsl(${(h + 40) % 360} 60% 16%))">${esc(initials(p.title))}</div>
      <div class="p-name"><div>${esc(p.title)}</div><div>${esc(p.id)}  ·  ${when}${status}</div></div>
      <span class="p-go">Open in Workbench ↵</span></div>`;
  }).join("") || `<div class="empty" style="padding:30px">No project matches.</div>`;
  $$(".p-row").forEach((el) => {
    el.onmouseenter = () => { P.index = +el.dataset.i; highlightPalette(); };
    el.onclick = () => choosePalette(false);
  });
}

function highlightPalette() {
  $$(".p-row").forEach((el, i) => el.classList.toggle("active", i === P.index));
  const active = $(".p-row.active");
  if (active) active.scrollIntoView({ block: "nearest" });
}

function choosePalette(showOnly) {
  const p = P.items[P.index];
  if (!p) return;
  closePalette();
  if (showOnly) {
    setView("projects");
    select(p.dir);
  } else {
    openWorkbench(p);
  }
}

function bindPalette() {
  const input = $("#palette-input");
  input.oninput = renderPalette;
  input.onkeydown = (e) => {
    if (e.key === "ArrowDown") { e.preventDefault(); P.index = Math.min(P.index + 1, P.items.length - 1); highlightPalette(); }
    else if (e.key === "ArrowUp") { e.preventDefault(); P.index = Math.max(P.index - 1, 0); highlightPalette(); }
    else if (e.key === "Enter") { e.preventDefault(); choosePalette(e.shiftKey); }
    else if (e.key === "Escape") { e.preventDefault(); closePalette(); }
  };
  $("#palette").onclick = (e) => { if (e.target.id === "palette") closePalette(); };
}

// ================================================================================================
// Views & wiring
// ================================================================================================
// ================================================================================================
// Mod updates: the Workshop's versions, and the updater (a hidden dedicated server downloads mods)
// ================================================================================================
const workshopUrl = (guid) => "https://reforger.armaplatform.com/workshop/" + guid;

function verParts(v) {
  return String(v || "").split(".").map((x) => parseInt(x, 10) || 0);
}

function verCmp(a, b) {
  const x = verParts(a), y = verParts(b);
  for (let i = 0; i < Math.max(x.length, y.length); i++) {
    const d = (x[i] || 0) - (y[i] || 0);
    if (d) return d;
  }
  return 0;
}

const majorMinor = (v) => verParts(v).slice(0, 2).join(".");

function hasUpdate(p) {
  if (p.source !== "workshop") return false;
  const r = S.remote.get(p.guid);
  return !!(r && r.version && p.version && verCmp(r.version, p.version) > 0);
}

function builtForOlderGame(p) {
  if (p.source !== "workshop" || !p.gameVersion || !S.updater.gameVersion) return false;
  return verCmp(majorMinor(p.gameVersion), majorMinor(S.updater.gameVersion)) < 0;
}

function workshopSection(p) {
  const r = S.remote.get(p.guid);
  const upd = hasUpdate(p);
  const busy = S.updater.busy;
  const stat = (k, v, cls = "") => `<div class="stat"><div class="k">${k}</div><div class="v ${cls}">${v}</div></div>`;
  return `<section><h3>Workshop</h3>
    ${upd ? `<div class="upd-card warn" style="margin-bottom:10px"><div class="grow"><h3>Update available</h3>
      <p>${esc(p.version)} → <b style="color:var(--accent-2)">${esc(r.version)}</b>${r.size ? ` · ${size(r.size)}` : ""}${r.gameVersion ? ` · for game ${esc(r.gameVersion)}` : ""}</p></div></div>` : ""}
    <div class="stats" style="margin-bottom:10px">
      ${stat("Installed", esc(p.version || "?"))}
      ${stat("Latest", r ? (r.error ? `<span class="muted">${esc(r.error)}</span>` : esc(r.version || "?")) : S.checking ? "checking..." : "?", upd ? "new" : "")}
      ${stat("Built for game", esc(p.gameVersion || "?"), builtForOlderGame(p) ? "bad" : "")}
      ${stat("Your game", esc(S.updater.gameVersion || "?"))}
      ${r && !r.error ? stat("Subscribers", (r.subscribers || 0).toLocaleString()) + stat("Rating", r.rating ? Math.round(r.rating * 100) + "%" : "?")
        + stat("Author", esc(r.author || "?")) + stat("Updated", r.updatedAt ? ago(new Date(r.updatedAt).getTime()) : "?") : ""}
    </div>
    <div class="upd-actions">
      ${upd ? `<button class="btn primary" id="d-update" ${busy ? "disabled" : ""}>Update to ${esc(r.version)}</button>` : ""}
      <button class="btn" id="d-page">Workshop page</button>
      <button class="btn" id="d-recheck">Check again</button>
    </div>
    ${r && r.dependencies && r.dependencies.length ? `<div class="muted" style="margin-top:10px;font-size:12px">Needs: ${r.dependencies.map((d) => esc(d.name) + (d.version ? " " + esc(d.version) : "")).join(", ")}</div>` : ""}
    ${(upd && r.changelog) || p.changelog ? `<h3 style="margin-top:14px">${upd && r.changelog ? "What's new in " + esc(r.version) : "Changelog"}</h3>
      <div class="changelog">${esc(upd && r.changelog ? r.changelog : p.changelog)}</div>` : ""}
  </section>`;
}

function renderUpdateCount() {
  const n = S.projects.filter(hasUpdate).length;
  const el = $("#upd-count");
  el.textContent = n;
  el.classList.toggle("hidden", !n);
}

async function refreshUpdater() {
  try {
    S.updater = await call("updaterState");
  } catch { /* host busy */ }
}

function updaterPanel() {
  const u = S.updater;
  const job = S.run;
  if (job && !job.done) {
    const pct = job.progress != null ? Math.round(job.progress * 100) : null;
    return `<div class="upd-card warn"><div class="grow">
      <h3>${job.phase === "install" ? "Installing the updater" : "Updating mods"}${pct != null ? ` · ${pct}%` : ""}</h3>
      <p>${esc(job.text || "Working...")}</p>
      <div class="upd-progress ${pct == null ? "indeterminate" : ""}"><div style="width:${pct ?? 0}%"></div></div>
      <div class="upd-log" id="upd-log">${esc(job.lines.slice(-200).join("\n"))}</div>
    </div><button class="btn" id="upd-cancel">Cancel</button></div>`;
  }
  if (job && job.done) {
    const list = (job.results || []).map((r) => `${esc(r.name)}: ${esc(r.installed || "missing")}${r.expected && r.installed !== r.expected ? ` (wanted ${esc(r.expected)})` : ""}`).join(" · ");
    return `<div class="upd-card ${job.ok ? "" : "bad"}"><div class="grow"><h3>${job.ok ? "✔ " : "✖ "}${esc(job.text)}</h3>
      ${list ? `<p>${list}</p>` : ""}
      ${job.lines.length ? `<details style="margin-top:8px"><summary class="muted" style="cursor:pointer;font-size:12px">Show log</summary><div class="upd-log">${esc(job.lines.slice(-300).join("\n"))}</div></details>` : ""}
    </div><button class="btn" id="upd-dismiss">OK</button></div>`;
  }
  if (!u.serverInstalled) {
    return `<div class="upd-card warn"><div class="grow"><h3>One-time setup: install the updater</h3>
      <p>Mods download from Bohemia's servers, which only the game or the free Arma Reforger dedicated server can do. The Hub installs that server once
      (with Valve's SteamCMD, no Steam account needed, a few GB) and runs it hidden to download updates straight into your mods folder - no need to open the game.</p>
    </div><button class="btn primary" id="upd-install">Install updater</button></div>`;
  }
  if (u.blocking) {
    return `<div class="upd-card bad"><div class="grow"><h3>Close ${esc(u.blocking)} to update</h3>
      <p>It has the mod files open. Updates can be checked, but not downloaded, while it runs.</p></div>
      <button class="btn" id="upd-state">I closed it</button></div>`;
  }
  return "";
}

function renderUpdates() {
  const mods = S.projects.filter((p) => p.source === "workshop");
  const withUpdate = mods.filter(hasUpdate);
  const u = S.updater;
  $("#upd-info").textContent = [u.gameVersion ? `Your game ${u.gameVersion}` : "",
    S.checking ? `checking ${S.checking} on the Workshop...` : `${withUpdate.length} of ${mods.length} mods have updates`].filter(Boolean).join("  ·  ");
  const all = $("#upd-all");
  all.textContent = withUpdate.length ? `Update all (${withUpdate.length})` : "Update all";
  all.disabled = !withUpdate.length || u.busy || !u.serverInstalled;
  $("#upd-check").disabled = !!S.checking;

  const panel = $("#upd-panel");
  const log = $("#upd-log");
  const stick = !log || log.scrollTop + log.clientHeight >= log.scrollHeight - 8;
  panel.innerHTML = updaterPanel();
  const newLog = $("#upd-log");
  if (newLog && stick) newLog.scrollTop = newLog.scrollHeight;
  const bind = (id, fn) => { const el = $(id, panel); if (el) el.onclick = fn; };
  bind("#upd-install", async () => {
    S.run = { phase: "install", text: "Starting...", lines: [], done: false };
    renderUpdates();
    if (!await run(call("installUpdater"))) { S.run = null; renderUpdates(); }
  });
  bind("#upd-cancel", () => run(call("cancelUpdate"), "Stopping..."));
  bind("#upd-dismiss", () => { S.run = null; renderUpdates(); });
  bind("#upd-state", async () => { await refreshUpdater(); renderUpdates(); });

  let rows = mods.slice();
  if (S.updOnly) rows = rows.filter(hasUpdate);
  // Updates first, then mods built for an older game, then by name
  rows.sort((a, b) => (hasUpdate(b) - hasUpdate(a)) || (builtForOlderGame(b) - builtForOlderGame(a)) || a.title.localeCompare(b.title));

  $("#upd-table").innerHTML = `<div class="upd-row head"><span></span><span>Mod</span><span>Installed</span><span>Latest</span><span>Built for</span><span>Size</span><span>Status</span><span></span></div>`
    + rows.map((p) => {
      const r = S.remote.get(p.guid);
      const upd = hasUpdate(p);
      const old = builtForOlderGame(p);
      let status = `<span class="chip">checking...</span>`;
      if (r && r.error) status = `<span class="chip" title="${esc(r.error)}">not on Workshop</span>`;
      else if (upd) status = `<span class="chip warn">update</span>`;
      else if (r) status = old ? `<span class="chip bad" title="Up to date, but made for an older game">outdated mod</span>` : `<span class="chip good">up to date</span>`;
      else if (!S.checking) status = `<span class="chip">not checked</span>`;
      return `<div class="upd-row" data-dir="${esc(p.dir)}">
        <span class="thumb">${cover(p)}</span>
        <span class="name">${esc(p.title)}<small>${esc(p.guid)}${usedByOf(p).length ? ` · used by ${usedByOf(p).length}` : ""}</small></span>
        <span class="v">${esc(p.version || "?")}</span>
        <span class="v ${upd ? "new" : "muted"}">${esc(r?.version || "")}</span>
        <span class="v ${old ? "bad" : "muted"}">${esc(p.gameVersion ? majorMinor(p.gameVersion) : "?")}</span>
        <span class="v muted">${r?.size ? size(r.size) : p.stats ? size(p.stats.sizeBytes) : ""}</span>
        <span>${status}</span>
        ${upd ? `<button class="btn primary" data-upd="${p.guid}" ${u.busy || !u.serverInstalled ? "disabled" : ""}>Update</button>`
          : `<button class="btn" data-page="${p.guid}">Workshop</button>`}
      </div>`;
    }).join("");
  $$("[data-upd]").forEach((b) => b.onclick = (e) => { e.stopPropagation(); updateMods([b.dataset.upd]); });
  $$("[data-page]").forEach((b) => b.onclick = (e) => { e.stopPropagation(); run(call("openUrl", { url: workshopUrl(b.dataset.page) })); });
  $$(".upd-row[data-dir]").forEach((row) => row.onclick = () => select(row.dataset.dir));
}

async function updateMods(guids) {
  await refreshUpdater();
  if (!S.updater.serverInstalled) {
    setView("updates");
    toast("Install the updater first (one-time) - see the Updates tab", true);
    return;
  }
  S.run = { phase: "update", text: `Updating ${guids.length} mod${guids.length === 1 ? "" : "s"}...`, lines: [], done: false, guids };
  S.updater.busy = true;
  setView("updates");
  if (!await run(call("updateMods", { guids }))) {
    S.run = null;
    await refreshUpdater();
    renderUpdates();
  }
}

function onUpdater(data) {
  if (!S.run) S.run = { phase: data.phase, lines: [], done: false };
  const job = S.run;
  if (data.phase === "done") {
    job.done = true;
    job.ok = data.ok;
    job.text = data.text;
    job.results = data.results;
    S.updater.busy = false;
    toast(data.text, !data.ok);
    // New versions on disk: read the mods again, and the Workshop for anything still behind
    load("rescan").then(async () => {
      await refreshUpdater();
      renderAll();
      renderUpdates();
    });
    return;
  }
  job.phase = data.phase;
  if (data.text) job.text = data.text;
  if (data.progress != null) job.progress = data.progress;
  if (data.line) {
    job.lines.push(data.line);
    if (job.lines.length > 2000) job.lines.splice(0, job.lines.length - 2000);
  }
  scheduleRender();
}

function setView(view) {
  S.view = view;
  $$(".views button").forEach((b) => b.classList.toggle("active", b.dataset.view === view));
  $$(".view").forEach((v) => v.classList.toggle("active", v.id === "view-" + view));
  $("#sidebar").classList.toggle("hidden", view !== "projects");
  if (view === "graph") { renderGraph(); fitGraph(); }
  if (view === "logs") renderLogs();
  if (view === "updates") refreshUpdater().then(renderUpdates);
}

async function pollWorkbench() {
  try {
    const wb = await call("workbenchState");
    const changed = wb.running !== S.wb.running || wb.project !== S.wb.project;
    S.wb = wb;
    const pill = $("#wb-state");
    pill.classList.toggle("on", wb.running);
    pill.textContent = wb.running ? `Workbench${wb.project ? ": " + wb.project : " running"}` : "Workbench closed";
    if (changed) renderCards(false);
  } catch { /* host busy */ }
}

function wire() {
  $$(".views button").forEach((b) => b.onclick = () => setView(b.dataset.view));
  $$("#layout-toggle button").forEach((b) => b.onclick = () => {
    S.layout = b.dataset.layout;
    $$("#layout-toggle button").forEach((x) => x.classList.toggle("active", x === b));
    renderCards(true);
  });
  $("#search").oninput = (e) => { S.filters.search = e.target.value; renderCards(false); };
  $("#f-depends").onchange = (e) => { S.filters.depends = e.target.value; renderAll(); };
  $("#f-usedby").onchange = (e) => { S.filters.usedBy = e.target.value; renderAll(); };
  $("#f-sort").onchange = (e) => { S.filters.sort = e.target.value; renderCards(true); };
  $("#btn-rescan").onclick = () => run(load("rescan"), "Rescanned");
  $("#btn-new").onclick = showNewProject;
  $("#btn-quick").onclick = openPalette;
  $("#btn-settings").onclick = showSettings;
  $("#log-refresh").onclick = () => { S.logDir = ""; renderLogs(); };
  $("#upd-check").onclick = () => run(call("checkUpdates", {}), "Checking the Workshop...");
  $("#upd-all").onclick = () => updateMods(S.projects.filter(hasUpdate).map((p) => p.guid));
  $("#upd-only").onchange = (e) => { S.updOnly = e.target.checked; renderUpdates(); };
  $("#log-noise").onchange = renderLogs;
  $("#log-open").onclick = () => S.logDir && run(call("openPath", { path: S.logDir }));
  $("#modal").onclick = (e) => { if (e.target.id === "modal") closeModal(); };
  bindPalette();

  $("#cards").addEventListener("click", (e) => {
    const fav = e.target.closest("[data-fav]");
    if (fav) {
      e.stopPropagation();
      const p = S.projects.find((x) => x.guid === fav.dataset.fav);
      track(p, { favorite: !T(p).favorite }, true);
      return;
    }
    const card = e.target.closest(".card");
    if (card) select(card.dataset.dir);
  });
  $("#cards").addEventListener("dblclick", (e) => {
    const card = e.target.closest(".card");
    const p = card && S.projects.find((x) => x.dir === card.dataset.dir);
    if (p && p.source === "local") openWorkbench(p);
  });
  document.addEventListener("click", (e) => {
    const c = e.target.closest("[data-copy]");
    if (c) run(call("copy", { text: c.dataset.copy }), "Copied " + c.dataset.copy);
  });
  document.addEventListener("keydown", (e) => {
    if (e.ctrlKey && (e.key === "k" || e.key === "K" || e.key === "p")) { e.preventDefault(); openPalette(); return; }
    if (!$("#palette").classList.contains("hidden")) return;
    if (e.key === "Escape") closeModal();
    if (e.ctrlKey && e.key === "f") { e.preventDefault(); $("#search").focus(); }
  });
  bindGraph();
}

wire();
load().then(async () => {
  pollWorkbench();
  await refreshUpdater();
  renderAll();
  renderUpdateCount();
  if (S.settings.checkUpdatesOnStart !== false) call("checkUpdates", {}).catch(() => {});
  // Workbench is another program: its state is checked every few seconds
  setInterval(pollWorkbench, 4000);
}).catch((e) => toast(e.message, true));
