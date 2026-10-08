/**
 * Scripts & Files section (#section-scripts) — B10.
 * Text editor for Crystal's script folders (Envir\NPCs, Drops, Quests, Recipe, Routes, Values, Envir\*.txt, SystemScripts):
 * root tabs, filterable file tree, editor (line-number gutter, Tab inserts a tab, Ctrl+S save, Ctrl+F find, Ctrl+G go to line),
 * diagnostics from the server-side validators, dirty guard, Save / Save & reload, search across files, backups + restore,
 * create / rename / delete and the drop class tool (ConfigForm's Remove / Re-add Assassin & Archer drops).
 *
 * API: /api/admin/scripts/{roots,tree,file,create,delete,rename,search,backups,backup,restore,validate,reload-quests,
 * drops/class-toggle}; reloads use B1's POST /api/admin/server/reload/{npcs|drops|linemessages|recipes}.
 * Deep link: #section=scripts&root=<id>&path=<relative path>&line=<n>
 */
(function (global) {
  'use strict';

  var API = '/api/admin/scripts';
  var DIRTY_KEY = 'scripts';
  var LS_ROOT = 'mircraft_scripts_root';
  var LINE_HEIGHT = 18;          // keep in sync with admin-scripts.css (.scripts-text / .scripts-gutter)
  var COLLAPSE_OVER = 150;       // folders start collapsed when a root has more files than this

  var RELOAD = {
    npcs: { label: 'Reload NPCs', url: '/api/admin/server/reload/npcs' },
    drops: { label: 'Reload Drops', url: '/api/admin/server/reload/drops' },
    linemessages: { label: 'Reload Line messages', url: '/api/admin/server/reload/linemessages' },
    recipes: { label: 'Reload Recipes', url: '/api/admin/server/reload/recipes' },
    quests: { label: 'Reload quest', url: API + '/reload-quests' }
  };

  var el = null;          // the section element
  var ui = {};            // data-role → element
  var roots = [];
  var rootId = null;
  var files = [];
  var collapsed = Object.create(null);
  var expanded = Object.create(null);
  var current = null;     // { root, path, etag, encoding, lineEnding, mixed, reload, effect, validator, saved }
  var dirty = false;
  var diagnostics = [];
  var gutterLines = -1;
  var gutterKey = '';
  var sidePane = 'files';
  var openSeq = 0;
  var skipNextShowRefresh = false;

  // ------------------------------------------------------------------ helpers

  function esc(s) {
    return global.OpsUI ? OpsUI.esc(s) : String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function toast(msg, kind) { if (global.portalToast) portalToast(msg, kind || 'ok'); }

  function errMsg(e) { return (e && e.message) || String(e); }

  function fmtSize(n) {
    if (n == null) return '';
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1024 / 1024).toFixed(2) + ' MB';
  }

  function fmtDate(v) {
    if (global.OpsUI && OpsUI.fmtDate) return OpsUI.fmtDate(v);
    var d = new Date(v);
    return isNaN(d.getTime()) ? '' : d.toLocaleString();
  }

  function qs(obj) {
    return Object.keys(obj).filter(function (k) { return obj[k] != null && obj[k] !== ''; })
      .map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(obj[k]); }).join('&');
  }

  function rootById(id) {
    for (var i = 0; i < roots.length; i++) if (roots[i].id === id) return roots[i];
    return null;
  }

  function lsGet(k) { try { return global.localStorage.getItem(k); } catch (e) { return null; } }
  function lsSet(k, v) { try { global.localStorage.setItem(k, v); } catch (e) { /* private mode */ } }

  /** Sends JSON with portalApi and returns { ok, status, body } (never throws on HTTP errors). */
  async function send(method, url, body) {
    var r = await portalApi(url, { method: method, body: body || {} });
    var j = await r.json().catch(function () { return {}; });
    return { ok: r.ok, status: r.status, body: j || {} };
  }

  async function getData(url) {
    var j = await OpsUI.fetchJson(url);
    return OpsUI.unwrap ? OpsUI.unwrap(j) : (j && j.data !== undefined ? j.data : j);
  }

  function setDirty(on) {
    on = !!on;
    if (on === dirty) return;
    dirty = on;
    if (global.DbSave) DbSave.setDirty(DIRTY_KEY, on);
    renderFileBar();
  }

  async function confirmDiscard(what) {
    if (!dirty || !current) return true;
    return portalConfirm('You have unsaved changes to ' + current.path + '. Discard them and ' + what + '?', {
      title: 'Unsaved changes', confirmLabel: 'Discard changes', danger: true
    });
  }

  // ------------------------------------------------------------------ layout

  function layout() {
    el.innerHTML =
      '<div class="mir-card scripts-card">' +
      '  <div class="toolbar">' +
      '    <h2>Scripts &amp; Files</h2>' +
      '    <div class="scripts-head-actions">' +
      '      <button type="button" class="mir-btn hidden" data-role="classTool" title="Comment out or restore Assassin / Archer drops in every drop file">Class drops…</button>' +
      '      <button type="button" class="mir-btn" data-role="refresh">Refresh</button>' +
      '    </div>' +
      '  </div>' +
      '  <div class="sheet-tabs scripts-roots" data-role="roots" role="tablist"></div>' +
      '  <p class="mir-muted scripts-root-desc" data-role="rootDesc"></p>' +
      '  <div class="scripts-layout">' +
      '    <aside class="scripts-side">' +
      '      <div class="sheet-tabs scripts-side-tabs">' +
      '        <button type="button" class="mir-btn active" data-side="files">Files</button>' +
      '        <button type="button" class="mir-btn" data-side="search">Search</button>' +
      '      </div>' +
      '      <div data-role="filesPane" class="scripts-pane">' +
      '        <input type="search" class="fd-input scripts-filter" data-role="filter" placeholder="Filter files…" aria-label="Filter files" />' +
      '        <div class="scripts-side-actions">' +
      '          <button type="button" class="mir-btn" data-role="newFile">New file…</button>' +
      '          <button type="button" class="mir-btn" data-role="findBackups" title="Backups of any file, including deleted ones">Backups…</button>' +
      '        </div>' +
      '        <div class="scripts-tree" data-role="tree" role="tree"></div>' +
      '        <div class="mir-muted scripts-count" data-role="count"></div>' +
      '      </div>' +
      '      <div data-role="searchPane" class="scripts-pane hidden">' +
      '        <form class="scripts-search-form" data-role="searchForm">' +
      '          <input type="search" class="fd-input" data-role="searchQ" placeholder="Text to find in this root…" aria-label="Search text" />' +
      '          <label class="scripts-check"><input type="checkbox" data-role="searchRegex" /> Regex</label>' +
      '          <button type="submit" class="mir-btn primary">Search</button>' +
      '        </form>' +
      '        <div class="mir-muted scripts-count" data-role="searchInfo"></div>' +
      '        <div class="scripts-results" data-role="results"></div>' +
      '      </div>' +
      '    </aside>' +
      '    <section class="scripts-main">' +
      '      <div class="scripts-filebar">' +
      '        <div class="scripts-filetitle">' +
      '          <span class="scripts-path" data-role="path">No file open</span>' +
      '          <span class="ops-pill ops-pill-warn hidden" data-role="dirtyPill">unsaved</span>' +
      '          <span class="scripts-meta mir-muted" data-role="meta"></span>' +
      '        </div>' +
      '        <div class="scripts-filebtns">' +
      '          <button type="button" class="mir-btn primary" data-role="save" title="Save (Ctrl+S)">Save</button>' +
      '          <button type="button" class="mir-btn hidden" data-role="saveReload">Save &amp; reload</button>' +
      '          <button type="button" class="mir-btn hidden" data-role="reload"></button>' +
      '          <button type="button" class="mir-btn hidden" data-role="validate">Validate</button>' +
      '          <button type="button" class="mir-btn" data-role="find" title="Find (Ctrl+F)">Find</button>' +
      '          <button type="button" class="mir-btn" data-role="goto" title="Go to line (Ctrl+G)">Go to line</button>' +
      '          <button type="button" class="mir-btn" data-role="backups">Backups</button>' +
      '          <button type="button" class="mir-btn" data-role="rename">Rename</button>' +
      '          <button type="button" class="mir-btn danger" data-role="delete">Delete</button>' +
      '        </div>' +
      '      </div>' +
      '      <div class="scripts-effect mir-muted" data-role="effect"></div>' +
      '      <div class="scripts-findbar hidden" data-role="findBar">' +
      '        <input type="search" class="fd-input" data-role="findQ" placeholder="Find in file" aria-label="Find in file" />' +
      '        <label class="scripts-check"><input type="checkbox" data-role="findCase" /> Match case</label>' +
      '        <button type="button" class="mir-btn" data-role="findPrev" title="Previous (Shift+Enter)">&#8593;</button>' +
      '        <button type="button" class="mir-btn" data-role="findNext" title="Next (Enter)">&#8595;</button>' +
      '        <span class="mir-muted" data-role="findInfo"></span>' +
      '        <button type="button" class="mir-btn" data-role="findClose" title="Close (Esc)">&#215;</button>' +
      '      </div>' +
      '      <div class="scripts-editor" data-role="editorWrap">' +
      '        <pre class="scripts-gutter" data-role="gutter" aria-hidden="true"></pre>' +
      '        <textarea class="scripts-text" data-role="text" spellcheck="false" wrap="off" autocomplete="off" autocapitalize="off" ' +
      '          aria-label="File content" disabled placeholder="Choose a file on the left."></textarea>' +
      '      </div>' +
      '      <div class="scripts-status mir-muted" data-role="status"></div>' +
      '      <div class="scripts-diag" data-role="diagWrap">' +
      '        <div class="scripts-diag-head"><strong>Diagnostics</strong> <span class="mir-muted" data-role="diagSummary"></span></div>' +
      '        <div class="scripts-diag-list" data-role="diagList"></div>' +
      '      </div>' +
      '    </section>' +
      '  </div>' +
      '</div>';

    Array.prototype.forEach.call(el.querySelectorAll('[data-role]'), function (n) { ui[n.getAttribute('data-role')] = n; });
  }

  // ------------------------------------------------------------------ roots + tree

  async function loadRoots() {
    var data = await getData(API + '/roots');
    roots = (data && data.roots) || [];
    var saved = lsGet(LS_ROOT);
    if (!rootId || !rootById(rootId)) rootId = rootById(saved) ? saved : (roots[0] && roots[0].id);
    renderRoots();
  }

  function renderRoots() {
    ui.roots.innerHTML = roots.map(function (r) {
      var cls = 'mir-btn' + (r.id === rootId ? ' active' : '') + (r.exists ? '' : ' scripts-root-missing');
      return '<button type="button" role="tab" aria-selected="' + (r.id === rootId) + '" class="' + cls + '" data-root="' + esc(r.id) + '" title="' +
        esc(r.directory || '') + '">' + esc(r.label) + '</button>';
    }).join('');
    var r = rootById(rootId);
    ui.rootDesc.textContent = r ? (r.description || '') + (r.exists ? '' : ' (folder not found: ' + r.directory + ')') : '';
    ui.classTool.classList.toggle('hidden', rootId !== 'drops');
  }

  async function selectRoot(id) {
    if (!rootById(id)) return;
    if (id !== rootId) {
      rootId = id;
      lsSet(LS_ROOT, id);
      collapsed = Object.create(null);
      expanded = Object.create(null);
      ui.results.innerHTML = '';
      ui.searchInfo.textContent = '';
    }
    renderRoots();
    await loadTree();
  }

  async function loadTree() {
    if (!rootId) return;
    ui.tree.innerHTML = '<div class="mir-muted scripts-empty">Loading…</div>';
    try {
      var data = await getData(API + '/tree?' + qs({ root: rootId }));
      files = (data && data.files) || [];
      if (data && data.truncated) toast('This folder has too many files; only the first ' + files.length + ' are listed.', 'warn');
      renderTree();
    } catch (e) {
      files = [];
      ui.tree.innerHTML = '<div class="scripts-empty">' + esc(errMsg(e)) + '</div>';
      ui.count.textContent = '';
    }
  }

  function folderOf(path) {
    var i = path.lastIndexOf('/');
    return i < 0 ? '' : path.substring(0, i);
  }

  function nameOf(path) {
    var i = path.lastIndexOf('/');
    return i < 0 ? path : path.substring(i + 1);
  }

  function isCollapsed(folder, filtering) {
    if (filtering) return false;
    if (collapsed[folder]) return true;
    if (expanded[folder]) return false;
    if (current && current.root === rootId && folderOf(current.path) === folder) return false;
    return folder !== '' && files.length > COLLAPSE_OVER;
  }

  function renderTree() {
    var f = (ui.filter.value || '').trim().toLowerCase();
    var shown = f ? files.filter(function (x) { return x.path.toLowerCase().indexOf(f) >= 0; }) : files;
    var groups = Object.create(null);
    var order = [];
    shown.forEach(function (x) {
      var folder = folderOf(x.path);
      if (!groups[folder]) { groups[folder] = []; order.push(folder); }
      groups[folder].push(x);
    });
    order.sort(function (a, b) {
      if (a === '') return -1;
      if (b === '') return 1;
      return a.toLowerCase() < b.toLowerCase() ? -1 : a.toLowerCase() > b.toLowerCase() ? 1 : 0;
    });

    var html = [];
    order.forEach(function (folder) {
      var list = groups[folder];
      var closed = isCollapsed(folder, !!f);
      if (folder !== '') {
        html.push('<button type="button" class="scripts-folder" data-folder="' + esc(folder) + '" aria-expanded="' + (!closed) + '">' +
          '<span class="scripts-caret">' + (closed ? '&#9656;' : '&#9662;') + '</span> ' + esc(folder) +
          ' <span class="mir-muted">(' + list.length + ')</span></button>');
      }
      if (closed) return;
      list.forEach(function (x) {
        var active = current && current.root === rootId && current.path === x.path;
        html.push('<button type="button" role="treeitem" class="scripts-file' + (folder ? ' nested' : '') + (active ? ' active' : '') +
          '" data-path="' + esc(x.path) + '" title="' + esc(x.path + ' · ' + fmtSize(x.size) + ' · ' + fmtDate(x.mtimeUtc)) + '">' +
          esc(nameOf(x.path)) + '</button>');
      });
    });

    var r = rootById(rootId);
    if (!html.length) {
      html.push('<div class="mir-muted scripts-empty">' + (r && !r.exists ? 'This folder does not exist.' : (f ? 'No file matches the filter.' : 'No .txt files here yet.')) + '</div>');
    }
    ui.tree.innerHTML = html.join('');
    ui.count.textContent = (f ? shown.length + ' of ' : '') + files.length + ' file' + (files.length === 1 ? '' : 's');
  }

  // ------------------------------------------------------------------ open / editor

  async function openFile(root, path, line) {
    if (current && current.root === root && current.path === path) {
      if (line) goToLine(line);
      return true;
    }
    if (!(await confirmDiscard('open ' + path))) return false;

    var seq = ++openSeq;
    var data;
    try {
      data = await getData(API + '/file?' + qs({ root: root, path: path }));
    } catch (e) {
      toast(errMsg(e), 'error');
      return false;
    }
    if (seq !== openSeq) return false;

    current = {
      root: root,
      path: data.path,
      etag: data.etag,
      encoding: data.encoding,
      lineEnding: data.lineEnding,
      mixed: !!data.mixedLineEndings,
      size: data.size,
      mtimeUtc: data.mtimeUtc,
      reload: data.reload || null,
      effect: data.effect || '',
      validator: data.validator || null,
      readOnly: !!data.readOnly,
      readOnlyReason: data.readOnlyReason || '',
      saved: data.content || ''
    };
    ui.reload.classList.remove('primary');
    ui.text.disabled = false;
    ui.text.readOnly = current.readOnly;
    ui.text.value = current.saved;
    ui.text.scrollTop = 0;
    ui.text.scrollLeft = 0;
    setDirty(false);
    diagnostics = [];
    gutterLines = -1;
    renderGutter(true);
    renderDiagnostics();
    renderFileBar();
    updateStatus();
    if (global.portalSetHashParams) portalSetHashParams({ root: root, path: current.path, line: null }, true);
    if (root === rootId) renderTree();
    if (line) goToLine(line); else ui.text.setSelectionRange(0, 0);
    if (current.validator && !current.readOnly) validate(true);
    return true;
  }

  function closeFile() {
    current = null;
    ui.reload.classList.remove('primary');
    ui.text.value = '';
    ui.text.disabled = true;
    ui.text.readOnly = false;
    setDirty(false);
    diagnostics = [];
    gutterLines = -1;
    renderGutter(true);
    renderDiagnostics();
    renderFileBar();
    updateStatus();
    if (global.portalSetHashParams) portalSetHashParams({ root: null, path: null, line: null }, true);
    renderTree();
  }

  function renderFileBar() {
    if (!ui.path) return;
    var has = !!current;
    ui.path.textContent = has ? (rootLabel(current.root) + ' / ' + current.path) : 'No file open';
    ui.dirtyPill.classList.toggle('hidden', !(has && dirty));
    ui.meta.textContent = has
      ? [current.encoding, (current.lineEnding || '').toUpperCase() + (current.mixed ? ' (mixed)' : ''), fmtSize(current.size)].filter(Boolean).join(' · ')
      : '';
    ['save', 'find', 'goto', 'backups', 'rename', 'delete'].forEach(function (k) { ui[k].disabled = !has; });
    ui.save.disabled = !has || !dirty || current.readOnly;
    var reload = has && current.reload ? RELOAD[current.reload] : null;
    ui.saveReload.classList.toggle('hidden', !reload);
    ui.saveReload.disabled = !has || !dirty || current.readOnly;
    ui.reload.classList.toggle('hidden', !reload);
    if (reload) ui.reload.textContent = reload.label;
    ui.validate.classList.toggle('hidden', !(has && current.validator && !current.readOnly));
    var note = has ? (current.effect || '') : '';
    if (has && current.encoding === 'latin1') note = (note ? note + ' ' : '') + 'This file is not UTF-8: it is edited byte-for-byte (Latin-1).';
    if (has && current.readOnly) note = current.readOnlyReason + (note ? ' ' + note : '');
    if (has && current.mixed) note = (note ? note + ' ' : '') + 'Mixed line endings are normalised to ' + (current.lineEnding || '').toUpperCase() + ' on save.';
    ui.effect.textContent = note;
  }

  function rootLabel(id) {
    var r = rootById(id);
    return r ? r.label : id;
  }

  function lineCount(text) {
    var n = 1;
    for (var i = text.indexOf('\n'); i >= 0; i = text.indexOf('\n', i + 1)) n++;
    return n;
  }

  function renderGutter(force) {
    var text = ui.text.value;
    var lines = current ? lineCount(text) : 0;
    var key = diagnostics.map(function (d) { return d.line + d.severity; }).join(',');
    if (!force && lines === gutterLines && key === gutterKey) return;
    gutterLines = lines;
    gutterKey = key;

    var marks = Object.create(null);
    diagnostics.forEach(function (d) {
      if (!d.line) return;
      var prev = marks[d.line];
      if (!prev || d.severity === 'error' || (d.severity === 'warning' && prev === 'info')) marks[d.line] = d.severity;
    });

    var parts = new Array(lines);
    for (var i = 1; i <= lines; i++) {
      parts[i - 1] = marks[i] ? '<span class="sev-' + marks[i] + '">' + i + '</span>' : String(i);
    }
    ui.gutter.innerHTML = parts.join('\n') + '\n\n';
    syncScroll();
  }

  function syncScroll() {
    ui.gutter.scrollTop = ui.text.scrollTop;
  }

  function caretLineCol() {
    var pos = ui.text.selectionStart || 0;
    var before = ui.text.value.substring(0, pos);
    var line = lineCount(before);
    var col = pos - (before.lastIndexOf('\n') + 1) + 1;
    return { line: line, col: col };
  }

  function updateStatus() {
    if (!current) {
      ui.status.textContent = '';
      return;
    }
    var lc = caretLineCol();
    ui.status.textContent = 'Ln ' + lc.line + ', Col ' + lc.col + ' · ' + gutterLines + ' lines';
  }

  function lineStart(text, line) {
    var pos = 0;
    for (var n = 1; n < line; n++) {
      var i = text.indexOf('\n', pos);
      if (i < 0) return text.length;
      pos = i + 1;
    }
    return pos;
  }

  function goToLine(line) {
    if (!current) return;
    var text = ui.text.value;
    var total = lineCount(text);
    line = Math.max(1, Math.min(total, parseInt(line, 10) || 1));
    var start = lineStart(text, line);
    var end = text.indexOf('\n', start);
    if (end < 0) end = text.length;
    ui.text.focus();
    ui.text.setSelectionRange(start, end);
    scrollToLine(line);
    updateStatus();
  }

  function scrollToLine(line) {
    var target = (line - 1) * LINE_HEIGHT - Math.round(ui.text.clientHeight / 3);
    ui.text.scrollTop = Math.max(0, target);
    syncScroll();
  }

  function insertTab() {
    var t = ui.text;
    t.focus();
    var ok = false;
    try { ok = document.execCommand && document.execCommand('insertText', false, '\t'); } catch (e) { ok = false; }
    if (!ok) {
      var s = t.selectionStart, e2 = t.selectionEnd;
      t.setRangeText('\t', s, e2, 'end');
      onInput();
    }
  }

  function onInput() {
    if (!current) return;
    setDirty(ui.text.value !== current.saved);
    renderGutter(false);
    updateStatus();
  }

  // ------------------------------------------------------------------ save / reload / validate

  async function save(andReload) {
    if (!current) return false;
    if (!dirty && !andReload) { toast('No changes to save.'); return true; }
    var file = current;
    var content = ui.text.value;
    if (dirty) {
      var res = await send('PUT', API + '/file', { root: file.root, path: file.path, content: content, etag: file.etag });
      if (res.status === 409 && res.body && res.body.data && res.body.data.etag) {
        var overwrite = await portalConfirm(
          file.path + ' was changed on disk (by another admin or a tool) since you opened it. Overwrite it with your version? ' +
          'The version on disk is backed up first. Cancel keeps your edits here so you can copy them and reload the file.',
          { title: 'File changed on disk', confirmLabel: 'Overwrite', danger: true });
        if (!overwrite) return false;
        res = await send('PUT', API + '/file', { root: file.root, path: file.path, content: content, etag: file.etag, force: true });
      }
      if (!res.ok) {
        toast((res.body && res.body.message) || ('Save failed (HTTP ' + res.status + ').'), 'error');
        return false;
      }
      if (current !== file) return true;   // another file was opened meanwhile
      var d = res.body.data || {};
      file.etag = d.etag;
      file.size = d.size;
      file.mtimeUtc = d.mtimeUtc;
      file.saved = content;
      setDirty(ui.text.value !== file.saved);
      renderFileBar();
      toast(res.body.message || 'Saved.', 'ok');
      refreshTreeEntry(file.path, d.size, d.mtimeUtc);
      if (file.validator) validate(true);
      if (!andReload && file.reload && RELOAD[file.reload]) {
        ui.effect.textContent = 'Saved. ' + (file.effect || '') + ' Use "' + RELOAD[file.reload].label + '" to apply it now.';
        ui.reload.classList.add('primary');
      }
    }
    if (andReload) return reloadCurrent();
    return true;
  }

  function refreshTreeEntry(path, size, mtimeUtc) {
    for (var i = 0; i < files.length; i++) {
      if (files[i].path === path) { files[i].size = size; files[i].mtimeUtc = mtimeUtc; return; }
    }
  }

  async function reloadCurrent() {
    if (!current || !current.reload || !RELOAD[current.reload]) return false;
    var spec = RELOAD[current.reload];
    var body = current.reload === 'quests' ? { path: current.path } : {};
    var res = await send('POST', spec.url, body);
    if (!res.ok) {
      var msg = (res.body && res.body.message) || ('HTTP ' + res.status);
      if (res.status === 404 && current.reload !== 'quests') msg = 'The reload endpoint is not available on this server (' + msg + ').';
      toast(spec.label + ' failed: ' + msg, 'error');
      return false;
    }
    ui.reload.classList.remove('primary');
    toast(res.body.message || (spec.label + ' done.'), 'ok');
    return true;
  }

  async function validate(quiet) {
    if (!current || !current.validator || current.readOnly) return;
    var file = current;
    var res = await send('POST', API + '/validate', { root: file.root, path: file.path, content: ui.text.value });
    if (current !== file) return;
    if (!res.ok) {
      if (!quiet) toast((res.body && res.body.message) || ('Validation failed (HTTP ' + res.status + ').'), 'error');
      return;
    }
    var d = res.body.data || {};
    diagnostics = d.diagnostics || [];
    renderDiagnostics();
    renderGutter(true);
    if (!quiet) {
      var e = diagnostics.filter(function (x) { return x.severity === 'error'; }).length;
      var w = diagnostics.filter(function (x) { return x.severity === 'warning'; }).length;
      toast(e || w ? (e + ' error(s), ' + w + ' warning(s).') : 'No problems found.', e ? 'error' : (w ? 'warn' : 'ok'));
    }
  }

  function renderDiagnostics() {
    var show = !!(current && current.validator);
    ui.diagWrap.classList.toggle('hidden', !show);
    if (!show) return;
    var e = 0, w = 0, n = 0;
    diagnostics.forEach(function (d) { if (d.severity === 'error') e++; else if (d.severity === 'warning') w++; else n++; });
    ui.diagSummary.textContent = diagnostics.length ? (e + ' error(s) · ' + w + ' warning(s)' + (n ? ' · ' + n + ' note(s)' : '')) : 'No problems found.';
    ui.diagList.innerHTML = diagnostics.map(function (d) {
      return '<button type="button" class="scripts-diag-row sev-' + esc(d.severity) + '" data-line="' + (d.line || 0) + '">' +
        '<span class="scripts-diag-line">' + (d.line ? 'Ln ' + d.line : '—') + '</span>' +
        '<span class="scripts-diag-sev">' + esc(d.severity) + '</span>' +
        '<span class="scripts-diag-msg">' + esc(d.message) + '</span></button>';
    }).join('');
  }

  // ------------------------------------------------------------------ find

  function openFind() {
    if (!current) return;
    ui.findBar.classList.remove('hidden');
    var sel = ui.text.value.substring(ui.text.selectionStart, ui.text.selectionEnd);
    if (sel && sel.indexOf('\n') < 0 && sel.length < 200) ui.findQ.value = sel;
    ui.findQ.focus();
    ui.findQ.select();
  }

  function closeFind() {
    ui.findBar.classList.add('hidden');
    ui.findInfo.textContent = '';
    if (current) ui.text.focus();
  }

  function findNext(backwards) {
    var q = ui.findQ.value;
    if (!q || !current) return;
    var matchCase = ui.findCase.checked;
    var text = ui.text.value;
    var hay = matchCase ? text : text.toLowerCase();
    var needle = matchCase ? q : q.toLowerCase();
    var idx;
    if (backwards) {
      var from = ui.text.selectionStart - 1;
      idx = from >= 0 ? hay.lastIndexOf(needle, from) : -1;
      if (idx < 0) idx = hay.lastIndexOf(needle);
    } else {
      idx = hay.indexOf(needle, ui.text.selectionEnd);
      if (idx < 0) idx = hay.indexOf(needle);
    }
    if (idx < 0) {
      ui.findInfo.textContent = 'Not found';
      return;
    }
    var total = 0;
    for (var i = hay.indexOf(needle); i >= 0 && total < 10000; i = hay.indexOf(needle, i + needle.length)) total++;
    ui.findInfo.textContent = total + ' match' + (total === 1 ? '' : 'es');
    ui.text.focus();
    ui.text.setSelectionRange(idx, idx + q.length);
    scrollToLine(lineCount(text.substring(0, idx)));
    updateStatus();
    ui.findQ.focus();
  }

  async function promptGoToLine() {
    if (!current) return;
    var v = await portalDialog({
      title: 'Go to line',
      fields: [{ name: 'line', label: 'Line (1–' + gutterLines + ')', type: 'number', min: 1, max: gutterLines, value: caretLineCol().line, required: true }],
      confirmLabel: 'Go',
      submitOnEnter: true
    });
    if (v && v.line) goToLine(v.line);
  }

  // ------------------------------------------------------------------ search across files

  async function runSearch() {
    var q = ui.searchQ.value;
    if (!q) return;
    ui.searchInfo.textContent = 'Searching…';
    ui.results.innerHTML = '';
    try {
      var d = await getData(API + '/search?' + qs({ root: rootId, q: q, regex: ui.searchRegex.checked ? '1' : '' }));
      var hits = d.hits || [];
      ui.searchInfo.textContent = hits.length + ' hit' + (hits.length === 1 ? '' : 's') + ' in ' + d.filesScanned + ' file(s)' +
        (d.truncated ? ' — first ' + hits.length + ' shown, refine the search' : '') + (d.filesSkipped ? ' · ' + d.filesSkipped + ' skipped' : '');
      var html = [];
      var last = null;
      hits.forEach(function (h) {
        if (h.path !== last) {
          html.push('<div class="scripts-result-file">' + esc(h.path) + '</div>');
          last = h.path;
        }
        html.push('<button type="button" class="scripts-result" data-path="' + esc(h.path) + '" data-line="' + h.line + '">' +
          '<span class="scripts-diag-line">' + h.line + '</span><span class="scripts-result-text">' + esc(h.text) + '</span></button>');
      });
      ui.results.innerHTML = html.join('') || '<div class="mir-muted scripts-empty">No matches.</div>';
    } catch (e) {
      ui.searchInfo.textContent = errMsg(e);
    }
  }

  function setSidePane(which) {
    sidePane = which;
    ui.filesPane.classList.toggle('hidden', which !== 'files');
    ui.searchPane.classList.toggle('hidden', which !== 'search');
    Array.prototype.forEach.call(el.querySelectorAll('[data-side]'), function (b) {
      b.classList.toggle('active', b.getAttribute('data-side') === which);
    });
    if (which === 'search') ui.searchQ.focus();
  }

  // ------------------------------------------------------------------ create / rename / delete

  function txtPathProblem(p) {
    if (!p) return 'Enter a path.';
    if (!/\.txt$/i.test(p)) return 'The file name must end in .txt.';
    if (/(^|[\\/])\.\.?([\\/]|$)/.test(p)) return '"." and ".." are not allowed.';
    if (/^[\\/]|:/.test(p)) return 'Use a path relative to the root folder.';
    var r = rootById(rootId);
    if (r && r.flat && /[\\/]/.test(p)) return 'This root holds files directly in its folder (no subfolders).';
    return null;
  }

  async function createFile() {
    var r = rootById(rootId);
    if (!r) return;
    var folder = current && current.root === rootId ? folderOf(current.path) : '';
    var v = await portalDialog({
      title: 'New file in ' + r.label,
      message: r.flat ? 'Files go directly into ' + (r.directory || r.label) + '.' : 'Use / to create it in a subfolder (created if missing).',
      fields: [{ name: 'path', label: 'File path', type: 'text', value: folder ? folder + '/' : '', placeholder: r.flat ? 'Name.txt' : 'Folder/Name.txt', required: true }],
      validate: function (vals) { return txtPathProblem(vals.path); },
      confirmLabel: 'Create',
      submitOnEnter: true
    });
    if (!v) return;
    var res = await send('POST', API + '/create', { root: rootId, path: v.path });
    if (!res.ok) { toast((res.body && res.body.message) || ('Create failed (HTTP ' + res.status + ').'), 'error'); return; }
    toast(res.body.message || 'Created.', 'ok');
    await loadTree();
    await openFile(rootId, (res.body.data && res.body.data.path) || v.path);
  }

  async function renameFile() {
    if (!current) return;
    if (dirty) { toast('Save or discard your changes before renaming.', 'warn'); return; }
    var file = current;
    var flat = (rootById(file.root) || {}).flat;
    var v = await portalDialog({
      title: 'Rename ' + file.path,
      message: 'References to this file in other scripts (#INSERT, #INCLUDE, CALL, NPC / quest FileName) are NOT updated.',
      fields: [{ name: 'newPath', label: 'New path', type: 'text', value: file.path, required: true }],
      validate: function (vals) {
        if (vals.newPath === file.path) return 'Enter a different name.';
        if (flat && /[\\/]/.test(vals.newPath)) return 'This root holds files directly in its folder (no subfolders).';
        return txtPathProblem(vals.newPath);
      },
      confirmLabel: 'Rename',
      submitOnEnter: true
    });
    if (!v) return;
    var res = await send('POST', API + '/rename', { root: file.root, path: file.path, newPath: v.newPath });
    if (!res.ok) { toast((res.body && res.body.message) || ('Rename failed (HTTP ' + res.status + ').'), 'error'); return; }
    toast(res.body.message || 'Renamed.', 'ok');
    var newPath = (res.body.data && res.body.data.path) || v.newPath;
    if (current === file) {
      file.path = newPath;
      renderFileBar();
      if (global.portalSetHashParams) portalSetHashParams({ root: file.root, path: newPath }, true);
    }
    if (file.root === rootId) await loadTree();
  }

  async function deleteFile() {
    if (!current) return;
    var file = current;
    var ok = await portalConfirm('Delete ' + rootLabel(file.root) + ' / ' + file.path + '? It is moved to the backups folder' +
      (dirty ? ' (your unsaved changes are lost)' : '') + ' and can be restored from "Backups…".',
      { title: 'Delete file', confirmLabel: 'Delete', danger: true });
    if (!ok) return;
    var res = await send('POST', API + '/delete', { root: file.root, path: file.path });
    if (!res.ok) { toast((res.body && res.body.message) || ('Delete failed (HTTP ' + res.status + ').'), 'error'); return; }
    toast(res.body.message || 'Deleted.', 'ok');
    if (current === file) {
      setDirty(false);
      closeFile();
    }
    if (file.root === rootId) await loadTree();
  }

  // ------------------------------------------------------------------ backups

  async function askBackupsPath() {
    var r = rootById(rootId);
    if (!r) return;
    var v = await portalDialog({
      title: 'Backups in ' + r.label,
      message: 'Backups are kept for every save, delete and restore. Enter the file path (it may no longer exist).',
      fields: [{
        name: 'path', label: 'File path', type: 'text', required: true,
        value: current && current.root === rootId ? current.path : '',
        suggestions: files.map(function (x) { return x.path; })
      }],
      validate: function (vals) { return txtPathProblem(vals.path); },
      confirmLabel: 'Show backups',
      submitOnEnter: true
    });
    if (v) showBackups(rootId, v.path);
  }

  async function showBackups(root, path) {
    if (global.AdminInspect && AdminInspect.confirmReplace && !(await AdminInspect.confirmReplace('scripts'))) return;
    var data;
    try {
      data = await getData(API + '/backups?' + qs({ root: root, path: path }));
    } catch (e) {
      toast(errMsg(e), 'error');
      return;
    }
    var node = document.createElement('div');
    node.className = 'scripts-backups';
    var list = data.backups || [];
    var head = '<p class="mir-muted">' + esc(rootLabel(root) + ' / ' + data.path) + (data.exists ? '' : ' — the file does not exist now (restoring re-creates it).') + '</p>';
    if (!list.length) {
      node.innerHTML = head + '<p class="mir-muted">No backups yet.</p>';
    } else {
      node.innerHTML = head +
        '<table class="user-table scripts-backup-table"><thead><tr><th>Backup</th><th>Size</th><th></th></tr></thead><tbody>' +
        list.map(function (b) {
          return '<tr><td title="' + esc(b.name) + '">' + esc(fmtDate(b.mtimeUtc)) + '<div class="mir-muted scripts-backup-name">' + esc(b.name) + '</div></td>' +
            '<td>' + esc(fmtSize(b.size)) + '</td>' +
            '<td class="scripts-backup-actions"><button type="button" class="mir-btn" data-preview="' + esc(b.name) + '">Preview</button> ' +
            '<button type="button" class="mir-btn danger" data-restore="' + esc(b.name) + '">Restore</button></td></tr>';
        }).join('') + '</tbody></table>' +
        '<pre class="scripts-preview hidden" data-role="preview"></pre>';
    }

    node.addEventListener('click', async function (e) {
      var pv = e.target.closest('[data-preview]');
      var rs = e.target.closest('[data-restore]');
      if (pv) {
        var pre = node.querySelector('[data-role="preview"]');
        try {
          var b = await getData(API + '/backup?' + qs({ root: root, path: data.path, name: pv.getAttribute('data-preview') }));
          pre.textContent = b.content || '';
          pre.classList.remove('hidden');
          pre.scrollIntoView({ block: 'nearest' });
        } catch (err) {
          toast(errMsg(err), 'error');
        }
      } else if (rs) {
        var name = rs.getAttribute('data-restore');
        var isOpen = current && current.root === root && current.path === data.path;
        var ok = await portalConfirm('Restore ' + data.path + ' from backup ' + name + '? The current version is backed up first' +
          (isOpen && dirty ? '; your unsaved changes in the editor are discarded' : '') + '.',
          { title: 'Restore backup', confirmLabel: 'Restore', danger: true });
        if (!ok) return;
        var res = await send('POST', API + '/restore', { root: root, path: data.path, backup: name });
        if (!res.ok) { toast((res.body && res.body.message) || ('Restore failed (HTTP ' + res.status + ').'), 'error'); return; }
        toast(res.body.message || 'Restored.', 'ok');
        if (global.AdminInspect) AdminInspect.close();
        if (isOpen) {
          setDirty(false);
          current = null;
        }
        if (root === rootId) await loadTree();
        await openFile(root, data.path);
        if (current && current.reload && RELOAD[current.reload]) {
          ui.effect.textContent = 'Restored. Use "' + RELOAD[current.reload].label + '" to apply it now.';
          ui.reload.classList.add('primary');
        }
      }
    });

    if (global.AdminInspect) AdminInspect.open({ title: 'Backups', node: node });
  }

  // ------------------------------------------------------------------ drop class tool

  async function classTool() {
    var v = await portalDialog({
      title: 'Class drops',
      message: 'Comments out (prefix ;) or restores every drop line whose item requires the class, in all drop files — like the ' +
        'Remove / Re-add Assassin and Archer drops buttons of the server Config window. Each changed file is backed up first.',
      fields: [
        { name: 'cls', label: 'Class', type: 'select', value: 'Assassin', options: [{ value: 'Assassin', label: 'Assassin' }, { value: 'Archer', label: 'Archer' }] },
        { name: 'mode', label: 'Action', type: 'select', value: 'remove', options: [{ value: 'remove', label: 'Remove (comment out) their drops' }, { value: 'add', label: 'Re-add (uncomment) their drops' }] }
      ],
      confirmLabel: 'Preview'
    });
    if (!v) return;
    var enable = v.mode === 'add';
    var dry = await send('POST', API + '/drops/class-toggle', { 'class': v.cls, enable: enable, dryRun: true });
    if (!dry.ok) { toast((dry.body && dry.body.message) || ('Preview failed (HTTP ' + dry.status + ').'), 'error'); return; }
    var d = dry.body.data || {};
    if (!d.linesChanged) { toast('Nothing to change: no ' + (enable ? 'commented ' : '') + v.cls + ' drops found.', 'ok'); return; }
    var sample = (d.files || []).slice(0, 8).map(function (f) { return f.path + ' (' + f.lines.length + ')'; }).join(', ');
    var ok = await portalConfirm(dry.body.message + ' For example: ' + sample + ((d.files || []).length > 8 ? ', …' : '') +
      '. Apply now? Then use Reload Drops to make it live.',
      { title: (enable ? 'Re-add ' : 'Remove ') + v.cls + ' drops', confirmLabel: 'Apply', danger: !enable });
    if (!ok) return;
    var res = await send('POST', API + '/drops/class-toggle', { 'class': v.cls, enable: enable, dryRun: false });
    if (!res.ok) { toast((res.body && res.body.message) || ('Failed (HTTP ' + res.status + ').'), 'error'); return; }
    var rd = res.body.data || {};
    toast(res.body.message + (rd.failed && rd.failed.length ? ' ' + rd.failed.length + ' file(s) could not be written.' : ''), rd.failed && rd.failed.length ? 'warn' : 'ok');
    await loadTree();

    if (current && current.root === 'drops') {
      var touched = (rd.files || []).some(function (f) { return f.path === current.path; });
      if (touched) {
        if (dirty) {
          toast(current.path + ' was changed on disk by the tool; saving will ask before overwriting it.', 'warn');
        } else {
          var path = current.path;
          current = null;
          await openFile('drops', path);
        }
      }
    }

    var reloadNow = await portalConfirm('Reload drops now so the change takes effect in the running world?',
      { title: 'Reload Drops', confirmLabel: 'Reload Drops', cancelLabel: 'Later', danger: false });
    if (reloadNow) {
      var rr = await send('POST', RELOAD.drops.url, {});
      toast(rr.ok ? (rr.body.message || 'Drops reloaded.') : ('Reload Drops failed: ' + ((rr.body && rr.body.message) || ('HTTP ' + rr.status))), rr.ok ? 'ok' : 'error');
    }
  }

  // ------------------------------------------------------------------ events

  function wire() {
    ui.refresh.addEventListener('click', function (e) {
      OpsUI.withBusy(e.currentTarget, async function () {
        await loadRoots();
        await loadTree();
      });
    });

    ui.roots.addEventListener('click', function (e) {
      var b = e.target.closest('[data-root]');
      if (b) selectRoot(b.getAttribute('data-root'));
    });

    el.querySelector('.scripts-side-tabs').addEventListener('click', function (e) {
      var b = e.target.closest('[data-side]');
      if (b) setSidePane(b.getAttribute('data-side'));
    });

    var filterTimer = null;
    ui.filter.addEventListener('input', function () {
      clearTimeout(filterTimer);
      filterTimer = setTimeout(renderTree, 120);
    });

    ui.tree.addEventListener('click', function (e) {
      var folder = e.target.closest('[data-folder]');
      if (folder) {
        var key = folder.getAttribute('data-folder');
        var closedNow = isCollapsed(key, false);
        if (closedNow) { delete collapsed[key]; expanded[key] = true; } else { collapsed[key] = true; delete expanded[key]; }
        renderTree();
        return;
      }
      var f = e.target.closest('[data-path]');
      if (f) openFile(rootId, f.getAttribute('data-path'));
    });

    ui.newFile.addEventListener('click', createFile);
    ui.findBackups.addEventListener('click', askBackupsPath);

    ui.searchForm.addEventListener('submit', function (e) {
      e.preventDefault();
      runSearch();
    });
    ui.results.addEventListener('click', async function (e) {
      var r = e.target.closest('[data-path]');
      if (!r) return;
      var line = parseInt(r.getAttribute('data-line'), 10);
      var opened = await openFile(rootId, r.getAttribute('data-path'), line);
      if (opened && ui.searchQ.value && !ui.searchRegex.checked) {
        ui.findQ.value = ui.searchQ.value;
        ui.findBar.classList.remove('hidden');
        goToLine(line);
      }
    });

    ui.text.addEventListener('input', onInput);
    ui.text.addEventListener('scroll', syncScroll);
    ui.text.addEventListener('keyup', updateStatus);
    ui.text.addEventListener('click', updateStatus);
    ui.text.addEventListener('keydown', function (e) {
      if (e.key === 'Tab' && !ui.text.readOnly && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey) {
        e.preventDefault();
        insertTab();
      }
    });

    el.addEventListener('keydown', function (e) {
      if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
      var k = (e.key || '').toLowerCase();
      if (k === 's') {
        e.preventDefault();
        if (current) save(false);
      } else if (k === 'f' && current) {
        e.preventDefault();
        openFind();
      } else if (k === 'g' && current) {
        e.preventDefault();
        promptGoToLine();
      }
    });

    ui.save.addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, function () { return save(false); }); });
    ui.saveReload.addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, function () { return save(true); }); });
    ui.reload.addEventListener('click', async function (e) {
      var btn = e.currentTarget;
      if (dirty && !(await portalConfirm('The editor has unsaved changes; the reload uses the file on disk. Reload anyway?',
        { title: 'Unsaved changes', confirmLabel: 'Reload', danger: false }))) return;
      OpsUI.withBusy(btn, reloadCurrent);
    });
    ui.validate.addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, function () { return validate(false); }); });
    ui.find.addEventListener('click', openFind);
    ui.goto.addEventListener('click', promptGoToLine);
    ui.backups.addEventListener('click', function () { if (current) showBackups(current.root, current.path); });
    ui.rename.addEventListener('click', renameFile);
    ui.delete.addEventListener('click', deleteFile);
    ui.classTool.addEventListener('click', classTool);

    ui.findQ.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') {
        e.preventDefault();
        findNext(e.shiftKey);
      } else if (e.key === 'Escape') {
        e.preventDefault();
        e.stopPropagation();
        closeFind();
      }
    });
    ui.findQ.addEventListener('input', function () { ui.findInfo.textContent = ''; });
    ui.findNext.addEventListener('click', function () { findNext(false); });
    ui.findPrev.addEventListener('click', function () { findNext(true); });
    ui.findClose.addEventListener('click', closeFind);

    ui.diagList.addEventListener('click', function (e) {
      var row = e.target.closest('[data-line]');
      var line = row ? parseInt(row.getAttribute('data-line'), 10) : 0;
      if (line > 0) goToLine(line);
    });

    if (global.portalRegisterDeepLink) {
      portalRegisterDeepLink('scripts', async function (params) {
        if (!params || !params.root || !params.path) return;
        if (!rootById(params.root)) return;
        if (params.root !== rootId) await selectRoot(params.root);
        await openFile(params.root, params.path, params.line ? parseInt(params.line, 10) : 0);
      });
    }
  }

  // ------------------------------------------------------------------ section

  global.AdminSections.register('scripts', {
    load: async function (sectionEl) {
      el = sectionEl;
      layout();
      wire();
      renderFileBar();
      renderDiagnostics();
      await loadRoots();
      await loadTree();
      skipNextShowRefresh = true;
    },
    show: function () {
      if (skipNextShowRefresh) { skipNextShowRefresh = false; return; }
      // Files may have changed on disk (another admin, the WinForms tools): refresh the list, keep the editor as is.
      if (rootId && files.length) return loadTree();
    }
  });
})(window);
