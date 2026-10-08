/**
 * Characters section (#section-characters) — B2.
 *
 * Search (name / held item / account, exact toggle, deleted filter, online only) over GET /api/admin/characters (server-side
 * paging + sorting), and a detail drawer (AdminInspect) over GET /api/admin/characters/{index} with tabs
 * Info · Items · Magic · Quests · Flags · Pets & Heroes · Mail.
 *
 * Edits: POST /api/admin/characters/{i}/update|delete|restore|flags|quests/reset|magics/remove|magics/set and the item editor
 * POST /api/admin/characters/{i}/items/add | items/{uid}/remove | items/{uid}/edit. GM verbs go through PlayerActions
 * (B4's /api/admin/gm/action), account bans through B3's /api/admin/accounts/{i}/ban. Every mutation asks ONE dialog
 * (with a reason for the audit trail) and sends nothing on Cancel.
 *
 * Deep links: #section=characters&index=<n> (or &character=<n>) opens the drawer; &q=<name>, &account=<id>, &item=<name>.
 */
(function (global) {
  'use strict';

  var O = global.OpsUI;
  var esc = function (s) { return O.esc(s); };
  var fmt = function (n) { return O.formatNumber(n); };

  var TABS = [
    { id: 'info', label: 'Info' },
    { id: 'items', label: 'Items' },
    { id: 'magic', label: 'Magic' },
    { id: 'quests', label: 'Quests' },
    { id: 'flags', label: 'Flags' },
    { id: 'pets', label: 'Pets & Heroes' },
    { id: 'mail', label: 'Mail' }
  ];

  var state = {
    q: '', mode: 'name', exact: false, deleted: 'include', online: false,
    sort: 'name', dir: 'asc', page: 1, pageSize: 50,
    total: 0, pages: 1, rows: [], world: null, loading: false
  };
  var detail = { index: null, data: null, tab: 'info', dirty: false, root: null, panels: {}, loading: 0 };
  var els = {};
  var listSeq = 0;
  var itemOptions = null;      // [{value, label}] from /api/admin/lookups/items (null = not loaded, [] = unavailable)
  var itemOptionsPromise = null;
  var freshlyLoaded = false;   // load() already searched: the first show() does not search again

  try {
    var ps = Number(global.localStorage && global.localStorage.getItem('mircraft_characters_pagesize'));
    if (ps >= 10 && ps <= 500) state.pageSize = ps;
  } catch (e) { /* storage unavailable */ }

  // ---------------------------------------------------------------- helpers

  function dateText(v) {
    var d = O.toDate(v);
    return d ? d.toLocaleString() : '—';
  }

  function agoText(v) {
    var d = O.toDate(v);
    return d ? O.ago(d) : '—';
  }

  function locText(l) {
    if (!l) return '—';
    return (l.mapTitle || l.mapFile || ('Map #' + l.mapIndex)) + ' (' + l.x + ', ' + l.y + ')';
  }

  function fact(label, valueHtml) {
    return '<div><span class="ins-label">' + esc(label) + '</span>' + valueHtml + '</div>';
  }

  function charUrl(index, suffix) {
    return '/api/admin/characters/' + encodeURIComponent(index) + (suffix || '');
  }

  function statsText(stats) {
    if (!stats) return '';
    return Object.keys(stats).map(function (k) { return k + (stats[k] >= 0 ? '+' : '') + stats[k]; }).join(', ');
  }

  function statsEditText(stats) {
    if (!stats) return '';
    return Object.keys(stats).map(function (k) { return k + '=' + stats[k]; }).join('\n');
  }

  /** "MinAC=2, Luck +1\nMaxDC:3" -> { MinAC: 2, Luck: 1, MaxDC: 3 }, or { error }. */
  function parseStatsText(text) {
    var out = {};
    var parts = String(text || '').split(/[\n,;]+/);
    for (var i = 0; i < parts.length; i++) {
      var p = parts[i].trim();
      if (!p) continue;
      var m = /^([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|:|\s)\s*([+-]?\d+)$/.exec(p) || /^([A-Za-z_][A-Za-z0-9_]*?)([+-]\d+)$/.exec(p);
      if (!m) return { error: 'Cannot read "' + p + '". Use StatName=value, one per line.' };
      out[m[1]] = Number(m[2]);
    }
    return { stats: out };
  }

  function sameStats(a, b) {
    var ka = Object.keys(a || {}), kb = Object.keys(b || {});
    if (ka.length !== kb.length) return false;
    return ka.every(function (k) { return Number(a[k]) === Number((b || {})[k]); });
  }

  // ---------------------------------------------------------------- search + table

  function buildSection(el) {
    el.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Characters</h2><span class="mir-muted" data-role="summary"></span></div>' +
      '  <form class="ops-form ch-search" data-role="form" autocomplete="off">' +
      '    <label>Search by<select class="fd-input" data-role="mode">' +
      '      <option value="name">Name</option><option value="item">Held item (name or UID)</option><option value="account">Account (id or index)</option>' +
      '    </select></label>' +
      '    <label class="ch-q">Text<input type="search" class="fd-input" data-role="q" data-section-search placeholder="Name…" /></label>' +
      '    <label class="mir-toggle"><input type="checkbox" data-role="exact" /> Exact</label>' +
      '    <label>Deleted<select class="fd-input" data-role="deleted">' +
      '      <option value="include">Include</option><option value="exclude">Hide</option><option value="only">Only deleted</option>' +
      '    </select></label>' +
      '    <label class="mir-toggle"><input type="checkbox" data-role="online" /> Online only</label>' +
      '    <label>Per page<select class="fd-input" data-role="pagesize"><option>25</option><option>50</option><option>100</option><option>250</option></select></label>' +
      '    <button type="submit" class="mir-btn primary">Search</button>' +
      '    <button type="button" class="mir-btn" data-role="clear">Clear</button>' +
      '  </form>' +
      '  <div class="ops-table-scroll">' +
      '    <table class="user-table users-table ch-table">' +
      '      <thead><tr>' +
      '        <th data-sort="name" scope="col">Name</th><th data-sort="level" scope="col" class="num">Lv</th><th data-sort="class" scope="col">Class</th>' +
      '        <th scope="col">Account</th><th scope="col">Guild</th><th scope="col" class="num">PK</th><th scope="col">Map</th>' +
      '        <th data-sort="lastLogin" scope="col">Last login</th><th scope="col">Last logout</th><th scope="col">Status</th>' +
      '      </tr></thead>' +
      '      <tbody data-role="tbody"></tbody>' +
      '    </table>' +
      '  </div>' +
      '  <p class="mir-muted hidden" data-role="empty"></p>' +
      '  <div class="ch-pager" data-role="pager">' +
      '    <button type="button" class="mir-btn" data-role="prev">‹ Prev</button>' +
      '    <span class="mir-muted" data-role="pageinfo"></span>' +
      '    <button type="button" class="mir-btn" data-role="next">Next ›</button>' +
      '  </div>' +
      '</div>';

    ['summary', 'form', 'mode', 'q', 'exact', 'deleted', 'online', 'pagesize', 'tbody', 'empty', 'prev', 'next', 'pageinfo'].forEach(function (k) {
      els[k] = el.querySelector('[data-role="' + k + '"]');
    });
    els.thead = el.querySelector('thead');
    els.pagesize.value = String(state.pageSize);
    if (els.pagesize.value !== String(state.pageSize)) els.pagesize.value = '50';

    els.form.addEventListener('submit', function (e) {
      e.preventDefault();
      readForm();
      writeHash();
      search(1);
    });
    els.mode.addEventListener('change', function () {
      els.q.placeholder = els.mode.value === 'item' ? 'Item name or UID…' : els.mode.value === 'account' ? 'Account id or index…' : 'Name…';
    });
    els.el = el;
    el.querySelector('[data-role="clear"]').addEventListener('click', function () {
      els.q.value = '';
      els.mode.value = 'name';
      els.exact.checked = false;
      els.deleted.value = 'include';
      els.online.checked = false;
      readForm();
      writeHash();
      search(1);
    });
    els.pagesize.addEventListener('change', function () {
      state.pageSize = Number(els.pagesize.value) || 50;
      try { global.localStorage.setItem('mircraft_characters_pagesize', String(state.pageSize)); } catch (e) { /* ignore */ }
      search(1);
    });
    els.prev.addEventListener('click', function () { if (state.page > 1) search(state.page - 1); });
    els.next.addEventListener('click', function () { if (state.page < state.pages) search(state.page + 1); });
    els.thead.addEventListener('click', function (e) {
      var th = e.target.closest ? e.target.closest('th[data-sort]') : null;
      if (!th) return;
      var key = th.getAttribute('data-sort');
      if (state.sort === key) state.dir = state.dir === 'asc' ? 'desc' : 'asc';
      else { state.sort = key; state.dir = key === 'level' || key === 'lastLogin' ? 'desc' : 'asc'; }
      search(1);
    });
    els.tbody.addEventListener('click', function (e) {
      var t = e.target;
      if (t.closest && t.closest('[data-player-chip]')) return;   // the chip opens its own menu
      var acc = t.closest ? t.closest('[data-account-link]') : null;
      if (acc) {
        e.stopPropagation();
        openAccount(acc.getAttribute('data-account-link'), acc.getAttribute('data-account-index'));
        return;
      }
      var tr = t.closest ? t.closest('tr[data-index]') : null;
      if (tr) openCharacter(Number(tr.getAttribute('data-index')));
    });
    els.tbody.addEventListener('keydown', function (e) {
      if (e.key !== 'Enter') return;
      var tr = e.target.closest ? e.target.closest('tr[data-index]') : null;
      if (tr && e.target === tr) openCharacter(Number(tr.getAttribute('data-index')));
    });
  }

  function readForm() {
    state.q = els.q.value.trim();
    state.mode = els.mode.value;
    state.exact = !!els.exact.checked;
    state.deleted = els.deleted.value;
    state.online = !!els.online.checked;
  }

  function writeForm() {
    els.q.value = state.q;
    els.mode.value = state.mode;
    els.exact.checked = state.exact;
    els.deleted.value = state.deleted;
    els.online.checked = state.online;
  }

  function writeHash() {
    if (!global.portalSetHashParams) return;
    global.portalSetHashParams({
      q: state.mode === 'name' && state.q ? state.q : null,
      item: state.mode === 'item' && state.q ? state.q : null,
      account: state.mode === 'account' && state.q ? state.q : null
    }, true);
  }

  async function openAccount(accountId, accountIndex) {
    // The account drawer replaces this one: never drop unsaved character edits silently.
    if (!(await confirmDiscard())) return;
    var p = { q: accountId || '', mode: 'account' };
    if (accountIndex && accountIndex !== '0') p.account = String(accountIndex);
    if (global.portalOpenDeepLink) global.portalOpenDeepLink('accounts', p);
  }

  async function search(page) {
    var my = ++listSeq;
    state.page = page || 1;
    var qs = ['page=' + state.page, 'pageSize=' + state.pageSize, 'sort=' + encodeURIComponent(state.sort), 'dir=' + state.dir,
      'deleted=' + encodeURIComponent(state.deleted)];
    if (state.q) {
      var key = state.mode === 'item' ? 'item' : state.mode === 'account' ? 'account' : 'q';
      qs.push(key + '=' + encodeURIComponent(state.q));
    }
    if (state.exact) qs.push('exact=1');
    if (state.online) qs.push('online=1');
    els.summary.textContent = 'Searching…';
    var r = await O.fetchResult('/api/admin/characters?' + qs.join('&'));
    if (my !== listSeq) return;
    if (!r.ok) {
      els.summary.textContent = 'Error: ' + r.error;
      portalToast('Character search failed: ' + r.error, 'error');
      return;
    }
    var j = O.unwrap(r.data) || {};
    state.rows = j.rows || [];
    state.total = j.total || 0;
    state.page = j.page || 1;
    state.pages = j.pages || 1;
    state.world = j.world || null;
    renderTable();
  }

  function renderTable() {
    Array.prototype.forEach.call(els.thead.querySelectorAll('th[data-sort]'), function (th) {
      var on = th.getAttribute('data-sort') === state.sort;
      th.setAttribute('aria-sort', on ? (state.dir === 'desc' ? 'descending' : 'ascending') : 'none');
    });
    els.tbody.innerHTML = state.rows.map(function (r) {
      var status = [];
      if (r.online) status.push('<span class="users-online">● online</span>');
      if (r.deleted) status.push('<span class="ops-pill ops-pill-danger">deleted</span>');
      if (r.banned) status.push('<span class="ops-pill ops-pill-danger">banned</span>');
      var selected = detail.index === r.index ? ' class="ch-selected"' : '';
      return '<tr tabindex="0" data-index="' + esc(r.index) + '"' + selected + '>' +
        '<td>' + O.playerLink(r.name, { account: r.accountId, accountIndex: r.accountIndex, online: !!r.online, characterIndex: r.index }) +
        ' <span class="mir-muted ch-idx">#' + esc(r.index) + '</span></td>' +
        '<td class="num">' + esc(r.level) + '</td>' +
        '<td>' + esc(r.className) + ' <span class="mir-muted">' + esc(r.gender === 'Female' ? '♀' : '♂') + '</span></td>' +
        '<td>' + (r.accountId ? '<button type="button" class="users-link" data-account-link="' + esc(r.accountId) + '" data-account-index="' + esc(r.accountIndex) + '">' + esc(r.accountId) + '</button>' : '') + '</td>' +
        '<td>' + esc(r.guild || '') + '</td>' +
        '<td class="num">' + esc(r.pk) + '</td>' +
        '<td>' + esc(r.mapTitle || '') + '</td>' +
        '<td title="' + esc(dateText(r.lastLogin)) + '">' + esc(agoText(r.lastLogin)) + '</td>' +
        '<td title="' + esc(dateText(r.lastLogout)) + '">' + esc(agoText(r.lastLogout)) + '</td>' +
        '<td>' + status.join(' ') + '</td>' +
        '</tr>';
    }).join('');
    els.empty.classList.toggle('hidden', state.rows.length > 0);
    els.empty.textContent = state.world === 'stopped' && !state.total
      ? 'No characters loaded: account data is loaded when the world starts.'
      : 'No character matches.';
    els.pageinfo.textContent = 'Page ' + state.page + ' of ' + state.pages + ' · ' + fmt(state.total) + ' character(s)';
    els.prev.disabled = state.page <= 1;
    els.next.disabled = state.page >= state.pages;
    els.summary.textContent = fmt(state.total) + ' found' + (state.world && state.world !== 'running' ? ' · world ' + state.world : '');
  }

  // ---------------------------------------------------------------- detail drawer

  function drawerEl() { return document.getElementById('inspectDrawer'); }

  function isDrawerOurs() {
    var d = drawerEl();
    return !!(d && !d.classList.contains('hidden') && detail.root && d.contains(detail.root));
  }

  async function confirmDiscard() {
    if (!detail.dirty) return true;
    var ok = await portalConfirm('You have unsaved changes on ' + ((detail.data && detail.data.name) || 'this character') + '. Discard them?',
      { title: 'Unsaved changes', confirmLabel: 'Discard', cancelLabel: 'Keep editing' });
    if (ok) setDirty(false);
    return ok;
  }

  function setDirty(on) {
    detail.dirty = !!on;
    if (detail.root) {
      var save = detail.root.querySelector('[data-role="save"]');
      var reset = detail.root.querySelector('[data-role="reset"]');
      if (save) save.disabled = !on;
      if (reset) reset.disabled = !on;
      var mark = detail.root.querySelector('[data-role="dirtymark"]');
      if (mark) mark.classList.toggle('hidden', !on);
    }
  }

  // Dirty guard for the shared drawer's close button and Escape (capture phase runs before the shell's own handlers).
  document.addEventListener('click', function (e) {
    if (!detail.dirty || !isDrawerOurs()) return;
    var btn = e.target && e.target.closest ? e.target.closest('#inspectDrawerClose') : null;
    if (!btn) return;
    e.preventDefault();
    e.stopPropagation();
    confirmDiscard().then(function (ok) { if (ok && global.AdminInspect) global.AdminInspect.close(); });
  }, true);
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape' || !detail.dirty || !isDrawerOurs()) return;
    if (document.querySelector('.portal-confirm-backdrop')) return;
    e.stopPropagation();
    confirmDiscard().then(function (ok) { if (ok && global.AdminInspect) global.AdminInspect.close(); });
  }, true);
  global.addEventListener('beforeunload', function (e) {
    if (detail.dirty && isDrawerOurs()) { e.preventDefault(); e.returnValue = ''; }
  });

  async function openCharacter(index, opts) {
    opts = opts || {};
    if (!index && index !== 0) return;
    if (detail.index !== index && !(await confirmDiscard())) return;
    if (detail.index === index && detail.dirty && !opts.force && !(await confirmDiscard())) return;
    // Another module's drawer (an account profile, a guild notice, ...) may hold unsaved edits: ask before replacing it.
    if (!isDrawerOurs() && global.AdminInspect.confirmReplace && !(await global.AdminInspect.confirmReplace('characters'))) return;
    var my = ++detail.loading;
    var r = await O.fetchResult(charUrl(index));
    if (my !== detail.loading) return;
    if (!r.ok) {
      portalToast('Character #' + index + ': ' + r.error, 'error');
      return;
    }
    var d = O.unwrap(r.data);
    if (!opts.keepTab && detail.index !== index) detail.tab = 'info';
    detail.index = index;
    detail.data = d;
    setDirty(false);
    renderDrawer();
    if (global.portalSetHashParams && global.AdminSections.current() === 'characters') global.portalSetHashParams({ index: String(index), character: null }, true);
    markSelected();
  }

  function reload() { if (detail.index != null) return openCharacter(detail.index, { keepTab: true }); }

  function markSelected() {
    if (!els.tbody) return;
    Array.prototype.forEach.call(els.tbody.querySelectorAll('tr[data-index]'), function (tr) {
      tr.classList.toggle('ch-selected', Number(tr.getAttribute('data-index')) === detail.index && isDrawerOurs());
    });
  }

  function renderDrawer() {
    var d = detail.data;
    var root = document.createElement('div');
    root.className = 'ch-drawer';
    detail.root = root;
    detail.panels = {};

    var acc = d.account || {};
    var badges = [];
    badges.push(d.online ? '<span class="users-online">● online</span>' : '<span class="mir-muted">offline</span>');
    if (d.info.deleted) badges.push('<span class="ops-pill ops-pill-danger">deleted ' + esc(agoText(d.info.deleteDate)) + '</span>');
    if (d.info.banned) badges.push('<span class="ops-pill ops-pill-danger">character banned</span>');
    if (acc.banned) badges.push('<span class="ops-pill ops-pill-danger" title="' + esc(acc.banReason || '') + '">account banned</span>');
    if (d.info.chatBanned) badges.push('<span class="ops-pill ops-pill-warn" title="until ' + esc(dateText(d.info.chatBanExpiry)) + '">chat banned</span>');
    if (acc.admin) badges.push('<span class="ops-pill ops-pill-warn">admin account</span>');
    if (d.world && d.world !== 'running') badges.push('<span class="ops-pill ops-pill-warn">world ' + esc(d.world) + '</span>');

    root.innerHTML =
      '<div class="ch-head">' +
      '  <div class="ch-head-main">' +
      '    <div class="ch-title">' + esc(d.name) + ' <span class="mir-muted">#' + esc(d.index) + ' · Lv ' + esc(d.info.level) + ' ' + esc(d.info.className) + ' ' + esc(d.info.gender) + '</span></div>' +
      '    <div class="ch-badges">' + badges.join(' ') + '</div>' +
      '    <div class="mir-muted ch-sub">Account ' + (acc.accountId ? '<button type="button" class="users-link" data-role="account">' + esc(acc.accountId) + '</button>' : '—') +
      (d.guild ? ' · Guild ' + esc(d.guild.name) + (d.guild.rank ? ' (' + esc(d.guild.rank) + ')' : '') : '') + '</div>' +
      '  </div>' +
      '  <button type="button" class="mir-btn ops-row-btn" data-role="refresh" title="Reload this character">↻ Refresh</button>' +
      '</div>' +
      '<div class="drawer-gm ch-actions" data-role="actions"></div>' +
      '<div class="admin-logs-tabs ch-tabs" role="tablist" data-role="tabs">' +
      TABS.map(function (t) {
        return '<button type="button" role="tab" class="mir-btn' + (t.id === detail.tab ? ' active' : '') + '" data-tab="' + t.id + '">' + esc(t.label) + '</button>';
      }).join('') +
      '</div>' +
      '<div data-role="panels"></div>';

    var panels = root.querySelector('[data-role="panels"]');
    TABS.forEach(function (t) {
      var p = document.createElement('div');
      p.className = 'ch-panel' + (t.id === detail.tab ? '' : ' hidden');
      p.setAttribute('data-panel', t.id);
      p.setAttribute('role', 'tabpanel');
      panels.appendChild(p);
      detail.panels[t.id] = p;
    });

    renderActions(root.querySelector('[data-role="actions"]'));
    renderInfo(detail.panels.info);
    renderItems(detail.panels.items);
    renderMagic(detail.panels.magic);
    renderQuests(detail.panels.quests);
    renderFlags(detail.panels.flags);
    renderPets(detail.panels.pets);
    renderMail(detail.panels.mail);

    root.querySelector('[data-role="tabs"]').addEventListener('click', function (e) {
      var b = e.target.closest ? e.target.closest('[data-tab]') : null;
      if (!b) return;
      detail.tab = b.getAttribute('data-tab');
      Array.prototype.forEach.call(root.querySelectorAll('[data-tab]'), function (x) { x.classList.toggle('active', x === b); });
      Object.keys(detail.panels).forEach(function (k) { detail.panels[k].classList.toggle('hidden', k !== detail.tab); });
    });
    root.querySelector('[data-role="refresh"]').addEventListener('click', function (e) { O.withBusy(e.currentTarget, reload); });
    var accBtn = root.querySelector('[data-role="account"]');
    if (accBtn) accBtn.addEventListener('click', function () { openAccount(acc.accountId, acc.index); });

    // Opening replaces the drawer content and runs the previous onClose first: it only resets state that is still its own.
    global.AdminInspect.open({
      title: 'Character — ' + d.name,
      node: root,
      owner: 'characters',
      isDirty: function () { return detail.root === root && detail.dirty; },
      onClose: function () {
        if (detail.root !== root) return;
        var dr = drawerEl();
        if (dr) dr.classList.remove('ch-drawer-wide');
        detail.root = null;
        detail.index = null;
        setDirty(false);
        markSelected();
        if (global.portalSetHashParams && global.AdminSections.current() === 'characters') global.portalSetHashParams({ index: null, character: null }, true);
      }
    });
    var drawer = drawerEl();
    if (drawer) drawer.classList.add('ch-drawer-wide');
  }

  function target() {
    var d = detail.data;
    return { name: d.name, characterIndex: d.index, online: !!d.online, accountIndex: d.account && d.account.index, accountId: d.account && d.account.accountId };
  }

  async function afterMutation(res) {
    if (res && res.__ok) {
      await reload();
      if (els.tbody && state.rows.length) search(state.page);
    }
    return res;
  }

  async function renderActions(box) {
    var d = detail.data;
    box.innerHTML = '<span class="mir-muted">Loading actions…</span>';
    var verbs = global.PlayerActions ? await global.PlayerActions.verbs() : [];
    if (detail.data !== d) return;   // a newer character was opened meanwhile
    var groups = {};
    var order = [];
    verbs.forEach(function (v) {
      if (!groups[v.group]) { groups[v.group] = []; order.push(v.group); }
      groups[v.group].push(v);
    });
    var html = order.map(function (g) {
      return '<div class="ch-verb-group"><span class="ins-label">' + esc(g) + '</span>' + groups[g].map(function (v) {
        var disabled = v.needsOnline && !d.online;
        return '<button type="button" class="mir-btn ops-row-btn' + (v.danger === false ? '' : ' ch-verb-danger') + '" data-verb="' + esc(v.action) + '"' +
          (disabled ? ' disabled title="Needs the character online"' : '') + '>' + esc(v.label) + '</button>';
      }).join('') + '</div>';
    }).join('');
    html += '<div class="ch-verb-group"><span class="ins-label">Account &amp; record</span>' +
      '<button type="button" class="mir-btn ops-row-btn danger" data-act="ban"' + (d.account ? '' : ' disabled') + '>Ban account…</button>' +
      (d.info.deleted
        ? '<button type="button" class="mir-btn ops-row-btn primary" data-act="restore">Restore character…</button>'
        : '<button type="button" class="mir-btn ops-row-btn danger" data-act="delete"' + (d.online ? ' disabled title="Kick the player first"' : '') + '>Delete character…</button>') +
      '<button type="button" class="mir-btn ops-row-btn" data-act="chat">Chat log</button>' +
      '<button type="button" class="mir-btn ops-row-btn" data-act="audit">Audit trail</button>' +
      '</div>';
    box.innerHTML = html;
    box.onclick = async function (e) {
      var b = e.target.closest ? e.target.closest('button') : null;
      if (!b || b.disabled) return;
      var verb = b.getAttribute('data-verb');
      if (verb) {
        var v = verbs.filter(function (x) { return x.action === verb; })[0];
        await afterMutation(await global.PlayerActions.runVerb(v, target()));
        return;
      }
      switch (b.getAttribute('data-act')) {
        case 'ban': await afterMutation(await global.PlayerActions.banAccount(target())); break;
        case 'delete': await deleteCharacter(); break;
        case 'restore': await restoreCharacter(); break;
        case 'chat': if (global.portalOpenDeepLink) global.portalOpenDeepLink('chatlog', { player: d.name, name: d.name }); break;
        case 'audit': if (global.portalOpenDeepLink) global.portalOpenDeepLink('auditlog', { q: d.name }); break;
      }
    };
  }

  async function deleteCharacter() {
    var d = detail.data;
    var v = await O.actionDialog({
      title: 'Delete ' + d.name,
      message: 'Soft-deletes the character (Deleted flag + delete date), exactly like deleting it at character select. It can be restored later.',
      confirmText: 'Delete character',
      fields: [{ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true }]
    });
    if (!v) return;
    await afterMutation(await O.post(charUrl(d.index, '/delete'), { reason: v.reason }));
  }

  async function restoreCharacter() {
    var d = detail.data;
    var v = await O.actionDialog({
      title: 'Restore ' + d.name,
      message: 'Clears the Deleted flag. Refused when the account already has the maximum number of characters.',
      confirmText: 'Restore', danger: false,
      fields: [{ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true }]
    });
    if (!v) return;
    await afterMutation(await O.post(charUrl(d.index, '/restore'), { reason: v.reason }));
  }

  // ---------------------------------------------------------------- Info tab

  var EDIT_FIELDS = [
    { key: 'name', label: 'Name', type: 'text', offlineOnly: true },
    { key: 'level', label: 'Level', type: 'number', min: 1, max: 65535 },
    { key: 'pkPoints', label: 'PK points', type: 'number' },
    { key: 'gold', label: 'Gold (account)', type: 'number', min: 0, max: 4294967295 },
    { key: 'credit', label: 'Credit (account)', type: 'number', min: 0, max: 4294967295 },
    { key: 'hair', label: 'Hair', type: 'number', min: 0, max: 255 },
    { key: 'gender', label: 'Gender', type: 'select', options: ['Male', 'Female'], offlineOnly: true }
  ];

  function originalValues() {
    var d = detail.data;
    return {
      name: d.name, level: d.info.level, pkPoints: d.info.pkPoints,
      gold: d.account ? d.account.gold : null, credit: d.account ? d.account.credit : null,
      hair: d.info.hair, gender: d.info.gender
    };
  }

  function renderInfo(p) {
    var d = detail.data, i = d.info, acc = d.account, live = d.live;
    var orig = originalValues();

    var form = '<form class="ch-edit" data-role="editform" autocomplete="off">' +
      '<h4>Edit <span class="ops-pill ops-pill-warn hidden" data-role="dirtymark">unsaved</span></h4>' +
      '<div class="fd-group-grid ch-edit-grid">' +
      EDIT_FIELDS.map(function (f) {
        var v = orig[f.key];
        var disabled = (f.offlineOnly && d.online) || ((f.key === 'gold' || f.key === 'credit') && !acc);
        var input;
        if (f.type === 'select') {
          input = '<select class="fd-input" name="' + f.key + '"' + (disabled ? ' disabled' : '') + '>' + f.options.map(function (o) {
            return '<option' + (o === v ? ' selected' : '') + '>' + esc(o) + '</option>';
          }).join('') + '</select>';
        } else {
          input = '<input class="fd-input" name="' + f.key + '" type="' + (f.type === 'number' ? 'number' : 'text') + '"' +
            (f.min != null ? ' min="' + f.min + '"' : '') + (f.max != null ? ' max="' + f.max + '"' : '') +
            ' value="' + esc(v == null ? '' : v) + '"' + (disabled ? ' disabled' : '') + ' />';
        }
        return '<label class="fd-field"><span class="fd-label">' + esc(f.label) + '</span>' + input +
          (f.offlineOnly && d.online ? '<span class="mir-muted ch-hint">offline only</span>' : '') + '</label>';
      }).join('') +
      '</div>' +
      '<div class="ch-edit-actions">' +
      '<button type="submit" class="mir-btn primary" data-role="save" disabled>Save changes…</button>' +
      '<button type="button" class="mir-btn" data-role="reset" disabled>Reset</button>' +
      (d.online ? '<span class="mir-muted">Online: level, PK and gold/credit changes reach the client immediately.</span>' : '') +
      '</div></form>';

    var facts = '<h4>Character</h4><div class="inspect-grid">' +
      fact('Location', esc(locText(live ? live.location : i.location))) +
      fact('Bind point', esc(locText(i.bind))) +
      fact('HP / MP', esc(live ? (live.hp + ' / ' + live.maxHp + ' · ' + live.mp + ' / ' + live.maxMp) : (i.hp + ' · ' + i.mp))) +
      fact('Experience', esc(fmt(i.experience) + (live && live.maxExperience ? ' / ' + fmt(live.maxExperience) + ' (' + Math.floor(100 * i.experience / live.maxExperience) + '%)' : ''))) +
      fact('Created', esc(dateText(i.creationDate)) + (i.creationIp ? ' <span class="mir-muted">' + esc(i.creationIp) + '</span>' : '')) +
      fact('Last login', esc(dateText(i.lastLogin))) +
      fact('Last logout', esc(dateText(i.lastLogout))) +
      fact('Last IP', esc(i.lastIp || '—')) +
      fact('Chat ban', i.chatBanned ? '<span class="ops-pill ops-pill-warn">until ' + esc(dateText(i.chatBanExpiry)) + '</span>' : 'none') +
      fact('Character ban', i.banned ? esc((i.banReason || 'banned') + ' · until ' + dateText(i.banExpiry)) : 'none') +
      fact('Married to', i.marriedTo ? O.playerLink(i.marriedTo, { characterIndex: i.marriedIndex }) + ' <span class="mir-muted">' + esc(dateText(i.marriedDate)) + '</span>' : '—') +
      fact(i.isMentor ? 'Mentor of' : 'Mentor', i.mentor ? O.playerLink(i.mentor, { characterIndex: i.mentorIndex }) + ' <span class="mir-muted">exp ' + esc(fmt(i.mentorExp)) + '</span>' : '—') +
      fact('Pearls', esc(fmt(i.pearls))) +
      fact('Modes', esc(i.attackMode + ' · pets ' + i.petMode)) +
      fact('Allow', esc([i.allowGroup ? 'group' : null, i.allowTrade ? 'trade' : null, i.allowObserve ? 'observe' : null].filter(Boolean).join(', ') || 'none')) +
      fact('Friends · buffs', esc(i.friendCount + ' · ' + i.buffCount)) +
      '</div>';

    var accHtml = acc ? '<h4>Account</h4><div class="inspect-grid">' +
      fact('Account', '<button type="button" class="users-link" data-role="account2">' + esc(acc.accountId) + '</button> <span class="mir-muted">#' + esc(acc.index) + '</span>') +
      fact('Gold · credit', esc(fmt(acc.gold) + ' · ' + fmt(acc.credit))) +
      fact('Ban', acc.banned ? '<span class="ops-pill ops-pill-danger">' + esc((acc.banReason || 'banned') + ' · until ' + dateText(acc.banExpiry)) + '</span>' : 'none') +
      fact('Characters', esc(acc.characterCount) + (acc.expandedStorage ? ' · expanded storage' : '')) +
      fact('Last IP', esc(acc.lastIp || '—')) +
      fact('Admin', acc.admin ? 'yes' : 'no') +
      '</div>' : '<p class="mir-muted">No account is linked to this character.</p>';

    var liveHtml = live ? '<h4>Live stats</h4><div class="inspect-grid ch-stats">' +
      fact('AC', esc(live.ac)) + fact('MAC', esc(live.mac)) + fact('DC', esc(live.dc)) + fact('MC', esc(live.mc)) + fact('SC', esc(live.sc)) +
      fact('Accuracy', esc(live.accuracy)) + fact('Agility', esc(live.agility)) + fact('Attack speed', esc(live.attackSpeed)) +
      fact('Luck', esc(live.luck)) + fact('IP', esc(live.ip || '—')) + fact('GM', live.isGM ? 'yes' : 'no') + fact('State', live.dead ? 'dead' : 'alive') +
      '</div>' : '';

    p.innerHTML = form + liveHtml + facts + accHtml;

    var f = p.querySelector('[data-role="editform"]');
    function current() {
      var out = {};
      EDIT_FIELDS.forEach(function (fd) {
        var el = f.elements[fd.key];
        if (!el || el.disabled) return;
        out[fd.key] = el.value.trim();
      });
      return out;
    }
    function changes() {
      var cur = current(), list = [];
      Object.keys(cur).forEach(function (k) {
        var o = orig[k] == null ? '' : String(orig[k]);
        if (cur[k] !== o) list.push({ key: k, from: o, to: cur[k] });
      });
      return list;
    }
    f.addEventListener('input', function () { setDirty(changes().length > 0); });
    f.addEventListener('change', function () { setDirty(changes().length > 0); });
    p.querySelector('[data-role="reset"]').addEventListener('click', function () {
      f.reset();
      setDirty(false);
    });
    var acc2 = p.querySelector('[data-role="account2"]');
    if (acc2) acc2.addEventListener('click', function () { openAccount(acc.accountId, acc.index); });
    f.addEventListener('submit', async function (e) {
      e.preventDefault();
      var list = changes();
      if (!list.length) return;
      var body = {};
      for (var k = 0; k < list.length; k++) {
        var c = list[k];
        var fd = EDIT_FIELDS.filter(function (x) { return x.key === c.key; })[0];
        if (fd.type === 'number') {
          if (c.to === '' || !/^-?\d+$/.test(c.to)) { portalToast(fd.label + ' must be a whole number.', 'error'); return; }
          body[c.key] = Number(c.to);
        } else {
          if (!c.to) { portalToast(fd.label + ' cannot be empty.', 'error'); return; }
          body[c.key] = c.to;
        }
      }
      var v = await O.actionDialog({
        title: 'Save changes to ' + d.name,
        message: list.map(function (c) {
          var fd = EDIT_FIELDS.filter(function (x) { return x.key === c.key; })[0];
          return fd.label + ': ' + (c.from === '' ? '—' : c.from) + ' → ' + c.to;
        }).join('\n'),
        confirmText: 'Save', danger: false,
        fields: [{ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true }]
      });
      if (!v) return;
      body.reason = v.reason;
      var res = await O.post(charUrl(d.index, '/update'), body);
      if (res.__ok) {
        setDirty(false);
        await afterMutation(res);
      }
    });
  }

  // ---------------------------------------------------------------- Items tab

  function loadItemOptions() {
    if (itemOptionsPromise) return itemOptionsPromise;
    itemOptionsPromise = O.fetchResult('/api/admin/lookups/items').then(function (r) {
      var j = r.ok ? O.unwrap(r.data) : null;
      var opts = j && Array.isArray(j.options) ? j.options : [];
      itemOptions = opts.map(function (o) {
        var value = o.value != null ? o.value : o.index;
        return { value: value, label: o.label || o.name || String(value) };
      }).filter(function (o) { return o.value != null; });
      return itemOptions;
    }, function () { itemOptions = []; return itemOptions; });
    return itemOptionsPromise;
  }

  function itemTable(list, title, container, size) {
    var head = '<h4>' + esc(title) + ' <span class="mir-muted">' + esc(list.length + (size ? ' / ' + size : '')) + '</span></h4>';
    if (!list.length) return head + '<p class="mir-muted">Empty.</p>';
    return head + '<div class="ops-table-scroll"><table class="user-table ch-items"><thead><tr>' +
      '<th>Slot</th><th>Item</th><th class="num">Count</th><th>Dura</th><th>Added stats</th><th>UID</th><th class="users-actions"></th>' +
      '</tr></thead><tbody>' + list.map(function (it) {
        var flags = [];
        if (it.gmMade) flags.push('<span class="ops-pill ops-pill-warn" title="Created by a GM">GM</span>');
        if (!it.identified) flags.push('<span class="ops-pill" title="Not identified">?</span>');
        if (it.expiresUtc) flags.push('<span class="ops-pill" title="Expires ' + esc(dateText(it.expiresUtc)) + '">exp</span>');
        return '<tr data-uid="' + esc(it.uniqueId) + '" data-container="' + esc(container) + '">' +
          '<td>' + esc(it.slotName) + '</td>' +
          '<td><span class="ch-item-name">' + esc(it.name) + '</span> <span class="mir-muted">#' + esc(it.itemIndex) + '</span> ' + flags.join(' ') + '</td>' +
          '<td class="num">' + esc(it.count) + (it.stackSize > 1 ? '<span class="mir-muted">/' + esc(it.stackSize) + '</span>' : '') + '</td>' +
          '<td>' + esc(it.currentDura + ' / ' + it.maxDura) + '</td>' +
          '<td class="ch-stats-cell">' + esc(statsText(it.addedStats)) + '</td>' +
          '<td class="mir-muted ch-uid">' + esc(it.uniqueId) + '</td>' +
          '<td class="users-actions"><button type="button" class="mir-btn ops-row-btn" data-item-act="edit">Edit…</button>' +
          '<button type="button" class="mir-btn ops-row-btn danger" data-item-act="remove">Remove…</button></td>' +
          '</tr>';
      }).join('') + '</tbody></table></div>';
  }

  function findItem(uid) {
    var it = detail.data.items;
    var all = [].concat(it.equipment || [], it.inventory || [], it.questInventory || [], it.storage || []);
    return all.filter(function (x) { return x.uniqueId === uid; })[0] || null;
  }

  function renderItems(p) {
    var d = detail.data, items = d.items;
    p.innerHTML =
      '<form class="ops-form ch-additem" data-role="addform" autocomplete="off">' +
      '  <label class="ch-item-input">Item (index or name)<input class="fd-input" name="item" list="chItemOptions" required placeholder="e.g. 12 or WoodenSword" /></label>' +
      '  <datalist id="chItemOptions"></datalist>' +
      '  <label>Count<input class="fd-input ch-count" name="count" type="number" min="1" value="1" /></label>' +
      '  <label>Into<select class="fd-input" name="container"><option value="inventory">Bag</option><option value="storage">Storage</option></select></label>' +
      '  <button type="submit" class="mir-btn primary">Add item…</button>' +
      '</form>' +
      (d.online ? '<p class="mir-muted ch-hint">Online: bag changes are sent to the client at once; storage changes show the next time the storage is opened.</p>'
        : '<p class="mir-muted ch-hint">Offline: items go to the first free slot; when the bag is full use GM give item or mail.</p>') +
      itemTable(items.equipment || [], 'Equipment', 'equipment') +
      itemTable(items.inventory || [], 'Bag (belt · bag I · bag II)', 'inventory', items.inventorySize) +
      itemTable(items.questInventory || [], 'Quest bag', 'questInventory', items.questInventorySize) +
      itemTable(items.storage || [], 'Storage (account)', 'storage', items.usableStorageSize);

    var dl = p.querySelector('#chItemOptions');
    loadItemOptions().then(function (opts) {
      if (!dl || !opts.length) return;
      var html = '';
      for (var i = 0; i < opts.length && i < 6000; i++) html += '<option value="' + esc(opts[i].value + ' — ' + opts[i].label) + '"></option>';
      dl.innerHTML = html;
    });

    var form = p.querySelector('[data-role="addform"]');
    form.addEventListener('submit', async function (e) {
      e.preventDefault();
      var raw = form.elements.item.value.trim();
      if (!raw) return;
      var count = Number(form.elements.count.value) || 1;
      var container = form.elements.container.value;
      var body = { count: count, container: container };
      var m = /^(\d+)(\s*[—-].*)?$/.exec(raw);
      var label = raw;
      if (m) {
        body.itemIndex = Number(m[1]);
        label = m[2] ? raw : ('item #' + m[1]);
      } else {
        body.item = raw;
      }
      var v = await O.actionDialog({
        title: 'Add item to ' + d.name,
        message: count + ' × ' + label + ' → ' + (container === 'storage' ? 'storage (account ' + ((d.account && d.account.accountId) || '?') + ')' : 'bag'),
        confirmText: 'Add', danger: false,
        fields: [{ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true }]
      });
      if (!v) return;
      body.reason = v.reason;
      var res = await O.post(charUrl(d.index, '/items/add'), body);
      if (res.__ok) {
        form.elements.item.value = '';
        form.elements.count.value = '1';
        await afterMutation(res);
      }
    });

    p.addEventListener('click', async function (e) {
      var b = e.target.closest ? e.target.closest('[data-item-act]') : null;
      if (!b) return;
      var tr = b.closest('tr[data-uid]');
      var it = tr ? findItem(tr.getAttribute('data-uid')) : null;
      if (!it) return;
      if (b.getAttribute('data-item-act') === 'remove') await removeItem(it);
      else await editItem(it);
    });
  }

  async function removeItem(it) {
    var d = detail.data;
    var v = await O.actionDialog({
      title: 'Remove item from ' + d.name,
      message: it.name + (it.count > 1 ? ' ×' + it.count : '') + ' (' + it.slotName + ', uid ' + it.uniqueId + ') will be destroyed.',
      confirmText: 'Remove item',
      fields: [{ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true }]
    });
    if (!v) return;
    await afterMutation(await O.post(charUrl(d.index, '/items/' + encodeURIComponent(it.uniqueId) + '/remove'), { reason: v.reason }));
  }

  async function editItem(it) {
    var d = detail.data;
    var fields = [];
    if (it.stackSize > 1) fields.push({ key: 'count', label: 'Count (1-' + it.stackSize + ')', type: 'number', min: 1, max: it.stackSize, value: it.count, required: true });
    fields.push({ key: 'currentDura', label: 'Current durability', type: 'number', min: 0, max: 65535, value: it.currentDura, required: true });
    fields.push({ key: 'maxDura', label: 'Max durability', type: 'number', min: 0, max: 65535, value: it.maxDura, required: true });
    fields.push({ key: 'addedStats', label: 'Added stats (StatName=value per line, e.g. MaxDC=2, Luck=1)', type: 'textarea', value: statsEditText(it.addedStats) });
    fields.push({ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true });
    var v = await O.actionDialog({
      title: 'Edit ' + it.name + ' — ' + d.name,
      message: it.slotName + ' · uid ' + it.uniqueId,
      confirmText: 'Save item', danger: false,
      fields: fields
    });
    if (!v) return;
    var body = { reason: v.reason };
    if (it.stackSize > 1 && Number(v.count) !== Number(it.count)) body.count = Number(v.count);
    if (Number(v.currentDura) !== Number(it.currentDura)) body.currentDura = Number(v.currentDura);
    if (Number(v.maxDura) !== Number(it.maxDura)) body.maxDura = Number(v.maxDura);
    var parsed = parseStatsText(v.addedStats);
    if (parsed.error) { portalToast(parsed.error, 'error'); return; }
    if (!sameStats(parsed.stats, it.addedStats || {})) {
      // Send the full set; stats left out are removed (0 removes).
      var stats = Object.assign({}, parsed.stats);
      Object.keys(it.addedStats || {}).forEach(function (k) { if (!(k in stats)) stats[k] = 0; });
      body.addedStats = stats;
    }
    if (Object.keys(body).length === 1) { portalToast('Nothing changed.', 'info'); return; }
    await afterMutation(await O.post(charUrl(d.index, '/items/' + encodeURIComponent(it.uniqueId) + '/edit'), body));
  }

  // ---------------------------------------------------------------- Magic tab

  function renderMagic(p) {
    var d = detail.data;
    var known = {};
    (d.magics || []).forEach(function (m) { known[m.spell] = true; });
    var rows = (d.magics || []).map(function (m) {
      return '<tr data-spell="' + esc(m.spellName) + '">' +
        '<td>' + esc(m.name) + (m.isTempSpell ? ' <span class="ops-pill">temp</span>' : '') + ' <span class="mir-muted">' + esc(m.spellName) + '</span></td>' +
        '<td class="num">' + esc(m.level) + '</td>' +
        '<td>' + esc(m.need != null ? m.experience + ' / ' + m.need : '—') + '</td>' +
        '<td>' + esc(m.keyLabel) + '</td>' +
        '<td class="users-actions"><button type="button" class="mir-btn ops-row-btn" data-magic-act="level">Set level…</button>' +
        '<button type="button" class="mir-btn ops-row-btn danger" data-magic-act="remove">Remove…</button></td></tr>';
    }).join('');
    var options = (d.spellOptions || []).map(function (o) {
      return '<option value="' + esc(o.value) + '">' + esc(o.name) + (known[o.value] ? ' ✓' : '') + '</option>';
    }).join('');
    p.innerHTML =
      '<form class="ops-form" data-role="magicform">' +
      '  <label>Spell<select class="fd-input" name="spell">' + options + '</select></label>' +
      '  <label>Level<select class="fd-input" name="level"><option>0</option><option>1</option><option>2</option><option selected>3</option></select></label>' +
      '  <button type="submit" class="mir-btn primary">Teach / set level…</button>' +
      '</form>' +
      '<h4>Known magic <span class="mir-muted">' + esc((d.magics || []).length) + '</span></h4>' +
      (rows ? '<div class="ops-table-scroll"><table class="user-table"><thead><tr><th>Spell</th><th class="num">Level</th><th>Experience</th><th>Key</th><th></th></tr></thead><tbody>' +
        rows + '</tbody></table></div>' : '<p class="mir-muted">No magic learned.</p>');

    var form = p.querySelector('[data-role="magicform"]');
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var sel = form.elements.spell;
      if (!sel.value) return;
      setMagic(sel.value, sel.options[sel.selectedIndex].text, Number(form.elements.level.value));
    });
    p.addEventListener('click', async function (e) {
      var b = e.target.closest ? e.target.closest('[data-magic-act]') : null;
      if (!b) return;
      var spell = b.closest('tr').getAttribute('data-spell');
      var m = (d.magics || []).filter(function (x) { return x.spellName === spell; })[0];
      if (!m) return;
      if (b.getAttribute('data-magic-act') === 'remove') {
        var v = await O.actionDialog({
          title: 'Remove ' + m.name + ' from ' + d.name, confirmText: 'Remove magic',
          fields: [{ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true }]
        });
        if (!v) return;
        await afterMutation(await O.post(charUrl(d.index, '/magics/remove'), { spell: m.spellName, reason: v.reason }));
      } else {
        await setMagic(m.spellName, m.name, null, m.level);
      }
    });
  }

  async function setMagic(spell, label, level, current) {
    var d = detail.data;
    var fields = [];
    if (level == null) fields.push({ key: 'level', label: 'Level', type: 'select', value: String(current != null ? current : 3), options: [0, 1, 2, 3].map(function (n) { return { value: String(n), label: String(n) }; }) });
    fields.push({ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true });
    var v = await O.actionDialog({
      title: 'Magic for ' + d.name,
      message: level != null ? label + ' → level ' + level : label,
      confirmText: 'Apply', danger: false,
      fields: fields
    });
    if (!v) return;
    var lv = level != null ? level : Number(v.level);
    await afterMutation(await O.post(charUrl(d.index, '/magics/set'), { spell: String(spell), level: lv, reason: v.reason }));
  }

  // ---------------------------------------------------------------- Quests tab

  function renderQuests(p) {
    var d = detail.data;
    function table(list, title) {
      var head = '<h4>' + esc(title) + ' <span class="mir-muted">' + esc(list.length) + '</span></h4>';
      if (!list.length) return head + '<p class="mir-muted">None.</p>';
      return head + '<div class="ops-table-scroll"><table class="user-table"><thead><tr><th class="num">#</th><th>Quest</th><th>Group</th><th>Status</th><th></th></tr></thead><tbody>' +
        list.map(function (q) {
          return '<tr><td class="num">' + esc(q.index) + '</td><td>' + esc(q.name || '(unknown quest)') + '</td><td>' + esc(q.group || '') + '</td>' +
            '<td>' + esc(q.status) + (q.started ? ' <span class="mir-muted">since ' + esc(dateText(q.started)) + '</span>' : '') + '</td>' +
            '<td class="users-actions"><button type="button" class="mir-btn ops-row-btn danger" data-quest="' + esc(q.index) + '" data-quest-name="' + esc(q.name || ('#' + q.index)) + '">Reset…</button></td></tr>';
        }).join('') + '</tbody></table></div>';
    }
    var any = (d.currentQuests || []).length + (d.completedQuests || []).length;
    p.innerHTML =
      '<div class="ch-row"><button type="button" class="mir-btn danger" data-role="resetall"' + (any ? '' : ' disabled') + '>Reset all quests…</button>' +
      '<span class="mir-muted">Reset clears the quest\'s progress and completion so it can be taken again.</span></div>' +
      table(d.currentQuests || [], 'Active') + table(d.completedQuests || [], 'Completed');
    p.addEventListener('click', async function (e) {
      var b = e.target.closest ? e.target.closest('[data-quest],[data-role="resetall"]') : null;
      if (!b || b.disabled) return;
      var all = b.getAttribute('data-role') === 'resetall';
      var v = await O.actionDialog({
        title: (all ? 'Reset ALL quests of ' : 'Reset quest on ') + d.name,
        message: all ? 'Every active and completed quest record is cleared.' : b.getAttribute('data-quest-name'),
        confirmText: all ? 'Reset all' : 'Reset quest',
        fields: [{ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true }]
      });
      if (!v) return;
      var body = { reason: v.reason };
      if (all) body.all = true;
      else body.questIndex = Number(b.getAttribute('data-quest'));
      await afterMutation(await O.post(charUrl(d.index, '/quests/reset'), body));
    });
  }

  // ---------------------------------------------------------------- Flags tab

  function renderFlags(p) {
    var d = detail.data;
    var active = {};
    (d.flags || []).forEach(function (f) { active[f.index] = true; });
    var names = d.flagNames || {};
    var max = Math.max(0, (d.flagCount || 1) - 1);
    p.innerHTML =
      '<form class="ops-form" data-role="flagform">' +
      '  <label>Flag (0-' + esc(max) + ')<input class="fd-input ch-count" name="flag" type="number" min="0" max="' + esc(max) + '" required /></label>' +
      '  <button type="submit" class="mir-btn primary" value="1">Set…</button>' +
      '  <button type="button" class="mir-btn" data-role="clearflag">Clear…</button>' +
      '</form>' +
      '<h4>Active flags <span class="mir-muted">' + esc((d.flags || []).length) + '</span></h4>' +
      ((d.flags || []).length
        ? '<div class="ch-flag-list">' + d.flags.map(function (f) {
          return '<span class="ch-flag on"><b>' + esc(f.index) + '</b> ' + esc(f.name || '') +
            ' <button type="button" class="db-chip-x" data-flag-toggle="' + esc(f.index) + '" data-flag-value="0" title="Clear flag ' + esc(f.index) + '">×</button></span>';
        }).join('') + '</div>'
        : '<p class="mir-muted">No flags set.</p>') +
      '<h4>Named flags <span class="mir-muted">from Envir\\SET [].txt</span></h4>' +
      (Object.keys(names).length
        ? '<input type="search" class="fd-input ch-flag-filter" data-role="flagfilter" placeholder="Filter named flags…" />' +
          '<div class="ops-table-scroll ch-flag-scroll"><table class="user-table"><thead><tr><th class="num">#</th><th>Name</th><th>State</th><th></th></tr></thead><tbody data-role="flagrows">' +
          Object.keys(names).map(Number).sort(function (a, b) { return a - b; }).map(function (n) {
            var on = !!active[n];
            return '<tr data-flag-row="' + esc(n) + '" data-flag-text="' + esc((n + ' ' + names[n]).toLowerCase()) + '"><td class="num">' + esc(n) + '</td><td>' + esc(names[n]) + '</td>' +
              '<td>' + (on ? '<span class="users-online">set</span>' : '<span class="mir-muted">clear</span>') + '</td>' +
              '<td class="users-actions"><button type="button" class="mir-btn ops-row-btn" data-flag-toggle="' + esc(n) + '" data-flag-value="' + (on ? '0' : '1') + '">' + (on ? 'Clear…' : 'Set…') + '</button></td></tr>';
          }).join('') + '</tbody></table></div>'
        : '<p class="mir-muted">No flag names: create Envir\\SET [].txt with lines like "[012] - Killed the boss" to label flags.</p>');

    var form = p.querySelector('[data-role="flagform"]');
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      setFlag(Number(form.elements.flag.value), true);
    });
    p.querySelector('[data-role="clearflag"]').addEventListener('click', function () {
      if (form.elements.flag.value === '') { form.elements.flag.focus(); return; }
      setFlag(Number(form.elements.flag.value), false);
    });
    var filter = p.querySelector('[data-role="flagfilter"]');
    if (filter) filter.addEventListener('input', function () {
      var f = filter.value.trim().toLowerCase();
      Array.prototype.forEach.call(p.querySelectorAll('[data-flag-row]'), function (tr) {
        tr.classList.toggle('hidden', !!f && tr.getAttribute('data-flag-text').indexOf(f) < 0);
      });
    });
    p.addEventListener('click', function (e) {
      var b = e.target.closest ? e.target.closest('[data-flag-toggle]') : null;
      if (!b) return;
      setFlag(Number(b.getAttribute('data-flag-toggle')), b.getAttribute('data-flag-value') === '1');
    });
  }

  async function setFlag(flag, value) {
    var d = detail.data;
    if (!(flag >= 0)) return;
    var name = (d.flagNames || {})[String(flag)];
    var v = await O.actionDialog({
      title: (value ? 'Set' : 'Clear') + ' flag ' + flag + ' on ' + d.name,
      message: name ? name : null,
      confirmText: value ? 'Set flag' : 'Clear flag', danger: false,
      fields: [{ key: 'reason', label: 'Reason (audit trail, optional)', type: 'text' }]
    });
    if (!v) return;
    await afterMutation(await O.post(charUrl(d.index, '/flags'), { flag: flag, value: value, reason: v.reason || '' }));
  }

  // ---------------------------------------------------------------- Pets & heroes / Mail

  function renderPets(p) {
    var d = detail.data;
    var pets = d.pets || [], heroes = d.heroes || [];
    p.innerHTML =
      '<h4>Pets <span class="mir-muted">' + esc(pets.length) + (pets.length && pets[0].live ? ' · live' : ' · saved') + '</span></h4>' +
      (pets.length ? '<div class="ops-table-scroll"><table class="user-table"><thead><tr><th>Monster</th><th class="num">Pet level</th><th>HP</th><th class="num">Exp</th><th>Where</th></tr></thead><tbody>' +
        pets.map(function (pt) {
          return '<tr><td>' + esc(pt.name) + ' <span class="mir-muted">#' + esc(pt.monsterIndex) + '</span></td><td class="num">' + esc(pt.level + ' / ' + pt.maxPetLevel) + '</td>' +
            '<td>' + esc(pt.maxHp ? pt.hp + ' / ' + pt.maxHp : pt.hp) + '</td><td class="num">' + esc(fmt(pt.experience)) + '</td>' +
            '<td>' + esc(pt.live ? (pt.mapTitle || '') + ' (' + pt.x + ', ' + pt.y + ')' : '—') + '</td></tr>';
        }).join('') + '</tbody></table></div>' : '<p class="mir-muted">No pets.</p>') +
      '<h4>Heroes <span class="mir-muted">' + esc(heroes.length + ' / ' + (d.maximumHeroCount || 0)) + '</span></h4>' +
      (heroes.length ? '<div class="ops-table-scroll"><table class="user-table"><thead><tr><th>Name</th><th class="num">Lv</th><th>Class</th><th>Magic</th><th>Items</th><th>State</th></tr></thead><tbody>' +
        heroes.map(function (h) {
          return '<tr><td>' + esc(h.name) + ' <span class="mir-muted">#' + esc(h.index) + '</span></td><td class="num">' + esc(h.level) + '</td>' +
            '<td>' + esc(h.className + ' ' + (h.gender === 'Female' ? '♀' : '♂')) + '</td><td>' + esc(h.magics) + '</td><td>' + esc(h.items) + '</td>' +
            '<td>' + (h.deleted ? '<span class="ops-pill ops-pill-danger">deleted</span>' : h.spawned ? '<span class="users-online">summoned</span>' : '') + '</td></tr>';
        }).join('') + '</tbody></table></div>' : '<p class="mir-muted">No heroes.</p>');
  }

  function renderMail(p) {
    var m = detail.data.mail || { count: 0, unread: 0, recent: [] };
    p.innerHTML =
      '<div class="metrics-grid ch-mail-tiles">' +
      '<div class="metric-card"><div class="metric-label">Mail</div><div class="metric-value">' + esc(m.count) + '</div></div>' +
      '<div class="metric-card' + (m.unread ? ' metric-warn' : '') + '"><div class="metric-label">Unread</div><div class="metric-value">' + esc(m.unread) + '</div></div>' +
      '</div>' +
      '<h4>Latest ' + esc((m.recent || []).length) + '</h4>' +
      ((m.recent || []).length ? '<div class="ops-table-scroll"><table class="user-table"><thead><tr><th>Sent</th><th>From</th><th>Message</th><th class="num">Gold</th><th class="num">Items</th><th>State</th></tr></thead><tbody>' +
        m.recent.map(function (x) {
          var st = [];
          st.push(x.opened ? 'read' : '<b>unread</b>');
          if (x.locked) st.push('locked');
          if (x.collected) st.push('collected');
          return '<tr><td title="' + esc(dateText(x.dateSent)) + '">' + esc(agoText(x.dateSent)) + '</td><td>' + esc(x.sender) + '</td>' +
            '<td class="ch-mail-msg">' + esc(x.preview) + '</td><td class="num">' + esc(fmt(x.gold)) + '</td><td class="num">' + esc(x.itemCount) + '</td>' +
            '<td>' + st.join(', ') + '</td></tr>';
        }).join('') + '</tbody></table></div>' : '<p class="mir-muted">No mail.</p>');
  }

  // ---------------------------------------------------------------- deep links + registration

  function applyParams(params) {
    params = params || {};
    var changed = false;
    if (params.item != null && params.item !== '') {
      if (state.mode !== 'item' || state.q !== params.item) { state.mode = 'item'; state.q = String(params.item); changed = true; }
    } else if (params.account != null && params.account !== '' && params.q == null) {
      if (state.mode !== 'account' || state.q !== params.account) { state.mode = 'account'; state.q = String(params.account); changed = true; }
    } else if (params.q != null) {
      if (state.mode !== 'name' || state.q !== String(params.q)) { state.mode = 'name'; state.q = String(params.q); changed = true; }
    }
    return changed;
  }

  global.portalRegisterDeepLink('characters', function (params) {
    if (!els.form) return;
    var changed = applyParams(params);
    if (changed) {
      writeForm();
      search(1);
    }
    var idx = params && (params.index != null && params.index !== '' ? params.index : params.character);
    if (idx != null && idx !== '' && !isNaN(Number(idx))) {
      var n = Number(idx);
      if (!(isDrawerOurs() && detail.index === n)) openCharacter(n);
    } else if (params && params.q && !params.index && !params.character && state.mode === 'name') {
      // A name link without an index: open the character when the exact name exists.
      O.fetchResult('/api/admin/characters?exact=1&pageSize=2&q=' + encodeURIComponent(params.q)).then(function (r) {
        var j = r.ok ? O.unwrap(r.data) : null;
        if (j && j.rows && j.rows.length === 1 && !(isDrawerOurs() && detail.index === j.rows[0].index)) openCharacter(j.rows[0].index);
      });
    }
  });

  global.AdminSections.register('characters', {
    load: function (el, params) {
      buildSection(el);
      applyParams(params);
      writeForm();
      freshlyLoaded = true;
      return search(1);
    },
    show: function () {
      if (freshlyLoaded) { freshlyLoaded = false; return; }
      return search(state.page);
    },
    hide: function () { /* the shell closes our drawer when leaving the section (AdminInspect owner) */ }
  });
})(window);
