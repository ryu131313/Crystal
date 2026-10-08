/**
 * Name Lists section (#section-namelists) — B5. Text files under Envir\NameLists used by NPC scripts (CHECKNAMELIST,
 * ADDNAMELIST, DELNAMELIST, CLEARNAMELIST and the guild variants). Scripts read the file contents on demand, but a
 * CHECKNAMELIST line is dropped when its file is missing at script load — so after creating a list that an already-loaded
 * script references, NPCs must be reloaded. Scripts use the path verbatim: "NewbieGuild" and "NewbieGuild.txt" are different
 * lists, so paths are shown in full.
 *
 *   GET  /api/admin/namelists                  → {lists:[{path, count, size, mtimeUtc, tooLarge}], totals:{lists, players, unique}}
 *   GET  /api/admin/namelists/file?path=       → {path, names[], untrimmed[], count, size, mtimeUtc}
 *   GET  /api/admin/namelists/find?name=&exact=1 → {lists:[{path, matches[]}]}
 *   POST /api/admin/namelists/create {path, extensionless?} · delete {path} · add {path, name} · remove {path, name}
 *
 * UI: list pane (filter, counts) + names pane (add, filter, remove) + "find a player" across every list.
 * Deep links: #section=namelists&path=<list>&name=<find text>.
 */
(function (global) {
  'use strict';

  var SECTION = 'namelists';

  var root = null;
  var lists = [];
  var current = { path: null, names: [], untrimmed: {}, seq: 0 };
  var findState = { name: '', exact: false, hits: null };

  function esc(v) { return OpsUI.esc(v); }
  function num(v) { return (Number(v) || 0).toLocaleString('en-US'); }
  function q(sel) { return root ? root.querySelector(sel) : null; }
  // Full path: scripts use it verbatim, and "X" and "X.txt" can both exist as different lists.
  function display(path) { return String(path || ''); }
  function scriptRef(path) { return /\s/.test(path) ? '"' + path + '"' : path; }

  function render(el) {
    el.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar">' +
      '    <h2>Name Lists</h2>' +
      '    <div class="nml-tools">' +
      '      <input type="search" class="fd-input" data-role="find" placeholder="Find a player or guild in every list" aria-label="Find a name" />' +
      '      <label class="mir-toggle nml-exact"><input type="checkbox" data-role="exact" /> exact</label>' +
      '      <button type="button" class="mir-btn" data-role="find-btn">Find</button>' +
      '      <button type="button" class="mir-btn" data-role="refresh">Refresh</button>' +
      '      <button type="button" class="mir-btn primary" data-role="create">New list…</button>' +
      '    </div>' +
      '  </div>' +
      '  <div class="admin-status-grid nml-totals" data-role="totals"></div>' +
      '  <div class="nml-find hidden" data-role="find-results"></div>' +
      '  <div class="nml-layout">' +
      '    <div class="nml-pane nml-lists">' +
      '      <input type="search" class="fd-input" data-section-search data-role="list-filter" placeholder="Filter lists" aria-label="Filter lists" />' +
      '      <div class="nml-list-items" data-role="lists" role="listbox" aria-label="Name lists"></div>' +
      '    </div>' +
      '    <div class="nml-pane nml-names" data-role="names-pane">' +
      '      <p class="mir-muted">Select a name list.</p>' +
      '    </div>' +
      '  </div>' +
      '  <p class="mir-muted nml-root" data-role="root"></p>' +
      '</div>';

    q('[data-role="refresh"]').addEventListener('click', function (e) {
      OpsUI.withBusy(e.currentTarget, function () { return loadLists(true).then(function () { return current.path ? loadNames(current.path) : null; }); });
    });
    q('[data-role="create"]').addEventListener('click', createList);
    q('[data-role="list-filter"]').addEventListener('input', renderLists);
    q('[data-role="find-btn"]').addEventListener('click', function () { runFind(); });
    q('[data-role="find"]').addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); runFind(); }
    });
    q('[data-role="exact"]').addEventListener('change', function () { if (findState.name) runFind(); });

    q('[data-role="lists"]').addEventListener('click', function (e) {
      var b = e.target && e.target.closest ? e.target.closest('[data-path]') : null;
      if (b) selectList(b.getAttribute('data-path'));
    });
    q('[data-role="find-results"]').addEventListener('click', function (e) {
      var b = e.target && e.target.closest ? e.target.closest('[data-open]') : null;
      if (b) selectList(b.getAttribute('data-open'));
      var c = e.target && e.target.closest ? e.target.closest('[data-role="find-clear"]') : null;
      if (c) clearFind();
    });
  }

  // ------------------------------------------------------------------ lists

  async function loadLists(force) {
    var j;
    try {
      j = OpsUI.unwrap(await OpsUI.fetchJson('/api/admin/namelists'));
    } catch (e) {
      if (force) portalToast('Could not load name lists: ' + (e && e.message || e), 'error');
      var host = q('[data-role="lists"]');
      if (host) host.innerHTML = '<p class="mir-muted">Error: ' + esc(e && e.message || e) + '</p>';
      return;
    }
    if (!j) return;
    lists = j.lists || [];
    var t = j.totals || {};
    var totals = q('[data-role="totals"]');
    if (totals) {
      totals.innerHTML =
        tile('Name lists', num(t.lists)) +
        tile('Total entries', num(t.players)) +
        tile('Unique names', num(t.unique));
    }
    var rootEl = q('[data-role="root"]');
    if (rootEl) rootEl.textContent = 'Folder: ' + (j.root || '') + (j.exists === false ? ' (does not exist yet — it is created with the first list)' : '');
    renderLists();
  }

  function tile(k, v) {
    return '<div class="admin-status-tile"><span class="k">' + esc(k) + '</span><span class="v">' + esc(v) + '</span></div>';
  }

  function renderLists() {
    var host = q('[data-role="lists"]');
    if (!host) return;
    var f = (q('[data-role="list-filter"]').value || '').trim().toLowerCase();
    var shown = lists.filter(function (l) { return !f || l.path.toLowerCase().indexOf(f) >= 0; });
    if (!shown.length) {
      host.innerHTML = '<p class="mir-muted">' + (lists.length ? 'No list matches the filter.' : 'No name lists yet. Use “New list…”.') + '</p>';
      return;
    }
    host.innerHTML = shown.map(function (l) {
      var on = l.path === current.path;
      return '<button type="button" role="option" aria-selected="' + on + '" class="nml-item' + (on ? ' active' : '') + '" data-path="' + esc(l.path) + '" title="' +
        esc(l.path + ' · ' + num(l.size) + ' bytes · modified ' + OpsUI.fmtDate(l.mtimeUtc)) + '">' +
        '<span class="nml-item-name">' + esc(display(l.path)) + '</span>' +
        (l.tooLarge
          ? '<span class="nml-warn" title="Over 4 MB: not read, and left out of the totals and Find">too large</span>'
          : '<span class="nml-item-count">' + num(l.count) + '</span>') +
        '</button>';
    }).join('');
  }

  async function selectList(path) {
    if (!path) return;
    current.path = path;
    global.portalSetHashParams({ path: path }, true);
    renderLists();
    await loadNames(path);
  }

  async function loadNames(path) {
    var pane = q('[data-role="names-pane"]');
    if (!pane) return;
    var token = ++current.seq;
    var j;
    try {
      j = OpsUI.unwrap(await OpsUI.fetchJson('/api/admin/namelists/file?path=' + encodeURIComponent(path)));
    } catch (e) {
      if (token !== current.seq) return;
      if (e && e.status === 404) {
        current.path = null;
        current.names = [];
        pane.innerHTML = '<p class="mir-muted">' + esc(display(path)) + ' no longer exists.</p>';
        global.portalSetHashParams({ path: null }, true);
        loadLists(false);
        return;
      }
      pane.innerHTML = '<p class="mir-muted">Error: ' + esc(e && e.message || e) + '</p>';
      return;
    }
    if (token !== current.seq || !j) return;
    current.path = j.path;
    current.names = j.names || [];
    current.untrimmed = {};
    (j.untrimmed || []).forEach(function (n) { current.untrimmed[n] = true; });
    renderNames();
  }

  function renderNames() {
    var pane = q('[data-role="names-pane"]');
    if (!pane || !current.path) return;
    var filterValue = (pane.querySelector('[data-role="name-filter"]') || {}).value || '';

    pane.innerHTML =
      '<div class="nml-names-head">' +
      '  <h3>' + esc(display(current.path)) + ' <span class="mir-muted">(' + num(current.names.length) + ')</span></h3>' +
      '  <div class="nml-names-actions">' +
      '    <button type="button" class="mir-btn" data-act="copy" title="Copy the script reference">Copy path</button>' +
      '    <button type="button" class="mir-btn" data-act="export">Export</button>' +
      '    <button type="button" class="mir-btn danger" data-act="delete">Delete list…</button>' +
      '  </div>' +
      '</div>' +
      '<p class="mir-muted nml-ref">Script: <code>CHECKNAMELIST ' + esc(scriptRef(current.path)) + '</code></p>' +
      (Object.keys(current.untrimmed).length
        ? '<p class="nml-note">Some lines have extra spaces, so CHECKNAMELIST does not match them in-game. Add the name again to fix the line.</p>'
        : '') +
      '<form class="nml-add" data-role="add-form" autocomplete="off">' +
      '  <input type="text" class="fd-input" data-role="add-name" maxlength="50" placeholder="Character or guild name" aria-label="Name to add" />' +
      '  <button type="submit" class="mir-btn primary">Add</button>' +
      '</form>' +
      '<input type="search" class="fd-input nml-name-filter" data-role="name-filter" placeholder="Filter names" aria-label="Filter names" />' +
      '<div class="nml-name-items" data-role="names"></div>';

    var filter = pane.querySelector('[data-role="name-filter"]');
    filter.value = filterValue;
    filter.addEventListener('input', renderNameItems);
    pane.querySelector('[data-role="add-form"]').addEventListener('submit', function (e) {
      e.preventDefault();
      addName(pane.querySelector('[data-role="add-name"]'), e.submitter || pane.querySelector('[data-role="add-form"] button'));
    });
    pane.querySelector('[data-act="delete"]').addEventListener('click', deleteList);
    pane.querySelector('[data-act="export"]').addEventListener('click', function () {
      OpsUI.download(current.path.split('/').pop(), current.names.join('\r\n') + (current.names.length ? '\r\n' : ''), 'text/plain');
    });
    pane.querySelector('[data-act="copy"]').addEventListener('click', function () {
      var text = current.path;
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(function () { portalToast('Copied ' + text, 'ok'); }, function () { portalToast(text, 'info'); });
      } else {
        portalToast(text, 'info');
      }
    });
    pane.querySelector('[data-role="names"]').addEventListener('click', function (e) {
      var b = e.target && e.target.closest ? e.target.closest('[data-remove]') : null;
      if (b) removeName(b.getAttribute('data-remove'));
    });
    renderNameItems();
  }

  function renderNameItems() {
    var pane = q('[data-role="names-pane"]');
    var host = pane ? pane.querySelector('[data-role="names"]') : null;
    if (!host) return;
    var f = ((pane.querySelector('[data-role="name-filter"]') || {}).value || '').trim().toLowerCase();
    var shown = current.names.filter(function (n) { return !f || n.toLowerCase().indexOf(f) >= 0; });
    if (!shown.length) {
      host.innerHTML = '<p class="mir-muted">' + (current.names.length ? 'No name matches the filter.' : 'This list is empty.') + '</p>';
      return;
    }
    host.innerHTML = shown.map(function (n) {
      return '<div class="nml-name">' +
        '<span class="nml-name-text">' + OpsUI.playerLink(n, {}) + '</span>' +
        (current.untrimmed[n] ? '<span class="nml-warn" title="This line has extra spaces, so CHECKNAMELIST does not match it. Add the name again to fix it.">spaces</span>' : '') +
        '<button type="button" class="mir-btn danger ops-row-btn" data-remove="' + esc(n) + '" aria-label="Remove ' + esc(n) + '">Remove</button>' +
        '</div>';
    }).join('');
  }

  // ------------------------------------------------------------------ mutations

  async function createList() {
    var v = await portalDialog({
      title: 'New name list',
      message: 'Creates an empty file under Envir\\NameLists. Use "/" for sub-folders (e.g. Events/Winners). NPC scripts use the path ' +
        'exactly as written: "CHECKNAMELIST Events/Winners" reads a file with no extension, "CHECKNAMELIST Events/Winners.txt" reads the .txt file. ' +
        'If a loaded script already references this list, reload NPCs afterwards: its check is skipped while the file was missing at load.',
      confirmLabel: 'Create',
      danger: false,
      fields: [
        { name: 'path', label: 'List name', type: 'text', required: true, placeholder: 'Events/Winners' },
        { name: 'extensionless', label: 'No .txt extension (the script names the list without .txt)', type: 'checkbox', value: false }
      ],
      validate: function (vals) {
        var p = (vals.path || '').trim();
        if (/(^|[\\/])\.\.?([\\/]|$)/.test(p) || /^[\\/]/.test(p) || p.indexOf(':') >= 0) return 'Use a relative name without "..", ":" or a leading slash.';
        if (/\.(?!txt$)[^.\\/]+$/i.test(p)) return 'Name lists are .txt files or have no extension.';
        return null;
      }
    });
    if (!v) return;
    var j = await OpsUI.post('/api/admin/namelists/create', { path: v.path, extensionless: !!v.extensionless });
    if (!j.__ok) return; // the success toast carries the server's "reload NPCs" reminder
    await loadLists(false);
    if (j.data && j.data.path) selectList(j.data.path);
  }

  async function deleteList() {
    if (!current.path) return;
    var path = current.path;
    var ok = await portalConfirm('Delete the name list "' + display(path) + '" (' + current.names.length + ' name(s))? NPC scripts that check it will fail their check. A backup copy is kept under Back Up\\WebAdmin\\NameLists.',
      { title: 'Delete name list', confirmLabel: 'Delete list', danger: true });
    if (!ok) return;
    var j = await OpsUI.post('/api/admin/namelists/delete', { path: path });
    if (!j.__ok) return;
    current.path = null;
    current.names = [];
    global.portalSetHashParams({ path: null }, true);
    var pane = q('[data-role="names-pane"]');
    if (pane) pane.innerHTML = '<p class="mir-muted">Select a name list.</p>';
    await loadLists(false);
    if (findState.name) runFind(true);
  }

  async function addName(input, button) {
    if (!current.path || !input) return;
    var name = (input.value || '').trim();
    if (!name) { input.focus(); return; }
    var path = current.path;
    await OpsUI.withBusy(button, async function () {
      var j = await OpsUI.post('/api/admin/namelists/add', { path: path, name: name });
      if (!j.__ok) return;
      input.value = '';
      await loadNames(path);
      loadLists(false);
      if (findState.name) runFind(true);
      var again = q('[data-role="add-name"]');
      if (again) again.focus();
    });
  }

  async function removeName(name) {
    if (!current.path || !name) return;
    var path = current.path;
    var ok = await portalConfirm('Remove "' + name + '" from ' + display(path) + '?', { title: 'Remove name', confirmLabel: 'Remove', danger: true });
    if (!ok) return;
    var j = await OpsUI.post('/api/admin/namelists/remove', { path: path, name: name });
    if (!j.__ok && j.__status !== 404) return;
    await loadNames(path);
    loadLists(false);
    if (findState.name) runFind(true);
  }

  // ------------------------------------------------------------------ find

  async function runFind(quiet) {
    var input = q('[data-role="find"]');
    var name = input ? input.value.trim() : '';
    var exact = !!(q('[data-role="exact"]') || {}).checked;
    if (!name) { clearFind(); return; }
    findState.name = name;
    findState.exact = exact;
    if (!quiet) global.portalSetHashParams({ name: name }, true);
    var host = q('[data-role="find-results"]');
    var j;
    try {
      j = OpsUI.unwrap(await OpsUI.fetchJson('/api/admin/namelists/find?name=' + encodeURIComponent(name) + (exact ? '&exact=1' : '')));
    } catch (e) {
      portalToast('Find failed: ' + (e && e.message || e), 'error');
      return;
    }
    if (!host || !j) return;
    var hits = j.lists || [];
    findState.hits = hits;
    host.classList.remove('hidden');
    host.innerHTML =
      '<div class="nml-find-head"><strong>' + (hits.length ? 'Found in ' + hits.length + ' list(s)' : 'Not on any list') + '</strong> ' +
      '<span class="mir-muted">for ' + (exact ? 'exactly ' : '') + '“' + esc(name) + '”</span>' +
      ' <button type="button" class="mir-btn ops-row-btn" data-role="find-clear">Clear</button></div>' +
      (hits.length ? '<ul class="nml-find-list">' + hits.map(function (h) {
        return '<li><button type="button" class="users-link" data-open="' + esc(h.path) + '">' + esc(display(h.path)) + '</button> ' +
          '<span class="mir-muted">' + h.matches.map(esc).join(', ') + '</span></li>';
      }).join('') + '</ul>' : '');
  }

  function clearFind() {
    findState = { name: '', exact: false, hits: null };
    var input = q('[data-role="find"]');
    if (input) input.value = '';
    var host = q('[data-role="find-results"]');
    if (host) { host.classList.add('hidden'); host.innerHTML = ''; }
    global.portalSetHashParams({ name: null }, true);
  }

  // ------------------------------------------------------------------ registration

  function applyParams(params) {
    if (!root || !params) return;
    if (params.path && params.path !== current.path) selectList(String(params.path));
    if (params.name && params.name !== findState.name) {
      var input = q('[data-role="find"]');
      if (input) input.value = String(params.name);
      runFind(true);
    }
  }

  global.portalRegisterDeepLink(SECTION, applyParams);

  global.AdminSections.register(SECTION, {
    load: function (el) {
      root = el;
      render(el);
    },
    show: function () {
      return loadLists(true);
    }
  });
})(window);
