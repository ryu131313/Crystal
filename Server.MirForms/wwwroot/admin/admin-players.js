/**
 * Players Online section (#section-players-online) — B2.
 *
 * GET /api/admin/players-online (game-thread snapshot) polled every 5 s while the section and the browser tab are visible.
 * The table is reconciled (rows keyed by character index, cells only rewritten when their content changed) so open menus
 * and focus survive a refresh. Row actions call B4's POST /api/admin/gm/action and B3's POST /api/admin/accounts/{i}/ban.
 *
 * Also exposes window.PlayerActions (used by the Characters drawer): the GM verb catalogue (GET /api/admin/gm/caps when
 * available, a built-in fallback otherwise) and one-dialog-per-action runners.
 */
(function (global) {
  'use strict';

  var POLL_MS = 5000;
  var esc = function (s) { return global.OpsUI.esc(s); };

  // ---------------------------------------------------------------- PlayerActions (shared with admin-characters.js)

  var CHATBAN_OPTIONS = [
    { value: '15', label: '15 minutes' }, { value: '60', label: '1 hour' }, { value: '360', label: '6 hours' },
    { value: '1440', label: '1 day' }, { value: '10080', label: '7 days' }, { value: '43200', label: '30 days' },
    { value: '525600', label: '1 year' }
  ];
  var BAN_OPTIONS = [
    { value: '1', label: '1 day' }, { value: '7', label: '7 days' }, { value: '30', label: '30 days' },
    { value: '90', label: '90 days' }, { value: 'permanent', label: 'Permanently' }
  ];

  // Fallback catalogue (same shape as GET /api/admin/gm/caps) used when B4's caps route is not available.
  var FALLBACK_VERBS = [
    { action: 'message', label: 'Message…', group: 'Moderation', needsOnline: true, danger: false, args: [{ name: 'text', type: 'text', required: true, label: 'Message' }] },
    { action: 'kick', label: 'Kick', group: 'Moderation', needsOnline: true, args: [] },
    { action: 'kill', label: 'Kill', group: 'Moderation', needsOnline: true, args: [] },
    { action: 'killpets', label: 'Kill pets', group: 'Moderation', needsOnline: true, args: [] },
    { action: 'chatban', label: 'Chat ban…', group: 'Moderation', needsOnline: false, args: [{ name: 'minutes', type: 'int', required: true, min: 1, label: 'Duration', options: CHATBAN_OPTIONS }] },
    { action: 'unchatban', label: 'Lift chat ban', group: 'Moderation', needsOnline: false, danger: false, args: [] },
    { action: 'clearbuffs', label: 'Clear buffs', group: 'Moderation', needsOnline: true, args: [] },
    { action: 'revive', label: 'Revive', group: 'Moderation', needsOnline: true, danger: false, args: [] },
    { action: 'safezone', label: 'Send to safe zone', group: 'Movement', needsOnline: true, args: [] },
    { action: 'teleport', label: 'Teleport…', group: 'Movement', needsOnline: true, args: [
      { name: 'map', type: 'text', required: true, label: 'Map (index, file name or title)', lookup: 'maps' },
      { name: 'x', type: 'int', required: false, min: 0, label: 'X (blank = random)' },
      { name: 'y', type: 'int', required: false, min: 0, label: 'Y (blank = random)' }] },
    { action: 'givegold', label: 'Give gold…', group: 'Grants', needsOnline: true, danger: false, args: [{ name: 'amount', type: 'int', required: true, min: 1, label: 'Amount' }] },
    { action: 'givecredit', label: 'Give credit…', group: 'Grants', needsOnline: true, danger: false, args: [{ name: 'amount', type: 'int', required: true, min: 1, label: 'Amount' }] },
    { action: 'giveitem', label: 'Give item…', group: 'Grants', needsOnline: true, danger: false, args: [
      { name: 'item', type: 'text', required: true, label: 'Item (index or name)', lookup: 'items' },
      { name: 'count', type: 'int', required: false, min: 1, label: 'Count' }] },
    { action: 'giveskill', label: 'Give skill…', group: 'Grants', needsOnline: true, danger: false, args: [
      { name: 'spell', type: 'text', required: true, label: 'Spell (name or value)', lookup: 'magics' },
      { name: 'level', type: 'int', required: false, min: 0, max: 3, label: 'Level (0-3)' }] },
    { action: 'setlevel', label: 'Set level…', group: 'Character', needsOnline: false, args: [{ name: 'level', type: 'int', required: true, min: 1, max: 65535, label: 'Level' }] },
    { action: 'setpk', label: 'Set PK points…', group: 'Character', needsOnline: true, args: [{ name: 'points', type: 'int', required: true, label: 'PK points' }] }
  ];

  var capsPromise = null;

  /** The GM verb catalogue: B4's /api/admin/gm/caps (player verbs only) or the built-in fallback. Cached. */
  function gmVerbs() {
    if (!capsPromise) {
      capsPromise = global.OpsUI.fetchResult('/api/admin/gm/caps').then(function (r) {
        var j = r.ok ? global.OpsUI.unwrap(r.data) : null;
        var verbs = j && Array.isArray(j.verbs) ? j.verbs : null;
        if (!verbs || !verbs.length) return FALLBACK_VERBS.slice();
        // B4 marks world verbs with target: 'world' (spawnmob, clearmobs, startconquest, stopconquest, ...); the name list is a fallback.
        var worldVerbs = { spawnmob: 1, clearmobs: 1, startconquest: 1, stopconquest: 1 };
        return verbs.filter(function (v) { return v && v.action && !worldVerbs[v.action] && v.world !== true && v.target !== 'world'; }).map(function (v) {
          var fb = FALLBACK_VERBS.filter(function (f) { return f.action === v.action; })[0];
          return {
            action: v.action, label: v.label || (fb && fb.label) || v.action, group: v.group || (fb && fb.group) || 'Other',
            needsOnline: v.needsOnline != null ? !!v.needsOnline : !!(fb && fb.needsOnline),
            danger: fb ? fb.danger : undefined,
            args: Array.isArray(v.args) ? v.args.map(function (a) {
              var fa = fb ? (fb.args || []).filter(function (x) { return x.name === a.name; })[0] : null;
              return Object.assign({}, fa || {}, a, { label: a.label || (fa && fa.label) || a.name });
            }) : ((fb && fb.args) || [])
          };
        });
      }, function () { return FALLBACK_VERBS.slice(); });
    }
    return capsPromise;
  }

  function argField(a) {
    var numeric = /^(int|integer|number|long|uint|ushort|byte|minutes)$/i.test(a.type || '');
    var f = { key: a.name, label: a.label || a.name, required: !!a.required, min: a.min, max: a.max };
    if (a.options && a.options.length) {
      f.type = 'select';
      f.options = a.options;
      f.value = a.options[0].value;
    } else if (/^(bool|boolean)$/i.test(a.type || '')) {
      f.type = 'checkbox';
    } else if (/^(textarea|longtext)$/i.test(a.type || '') || a.name === 'text') {
      f.type = 'textarea';
    } else {
      f.type = numeric ? 'number' : 'text';
    }
    if (a.lookup) f.hint = 'Lookup: ' + a.lookup;
    return f;
  }

  /**
   * Runs a GM verb on a character with ONE dialog (args + reason). target: { name, characterIndex, online }.
   * Resolves the parsed reply (with __ok) or null when cancelled.
   */
  async function runVerb(verb, target) {
    if (!verb || !target) return null;
    var fields = (verb.args || []).map(argField);
    fields.push({ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: verb.action !== 'message', placeholder: 'Why?' });
    var values = await global.OpsUI.actionDialog({
      title: (verb.label || verb.action).replace(/…$/, '') + ' — ' + target.name,
      message: verb.needsOnline && target.online === false ? target.name + ' appears to be offline; this action needs the character online.' : null,
      confirmText: (verb.label || verb.action).replace(/…$/, ''),
      danger: verb.danger === false ? false : undefined,
      fields: fields
    });
    if (!values) return null;
    var body = { action: verb.action, reason: values.reason || '' };
    if (target.characterIndex) body.characterIndex = Number(target.characterIndex);
    body.character = target.name;
    (verb.args || []).forEach(function (a) {
      var v = values[a.name];
      if (v === '' || v == null) return;
      if (typeof v === 'string' && /^(int|integer|number|long|uint|ushort|byte|minutes)$/i.test(a.type || '') && /^-?\d+$/.test(v)) v = Number(v);
      body[a.name] = v;
    });
    return global.OpsUI.post('/api/admin/gm/action', body);
  }

  async function runAction(action, target) {
    var verbs = await gmVerbs();
    var verb = verbs.filter(function (v) { return v.action === action; })[0] ||
      FALLBACK_VERBS.filter(function (v) { return v.action === action; })[0];
    return runVerb(verb, target);
  }

  /** Bans the character's account (B3 contract) with one dialog: duration + reason. */
  async function banAccount(target) {
    if (!target || !target.accountIndex) {
      global.portalToast('The account of this character is unknown.', 'error');
      return null;
    }
    var values = await global.OpsUI.actionDialog({
      title: 'Ban account ' + (target.accountId || ('#' + target.accountIndex)),
      message: 'Bans the whole account' + (target.name ? ' of ' + target.name : '') + ' and disconnects its online characters.',
      confirmText: 'Ban account',
      fields: [
        { key: 'duration', label: 'Duration', type: 'select', options: BAN_OPTIONS, value: '7' },
        { key: 'reason', label: 'Reason (optional, shown to the player)', type: 'text', required: false }
      ]
    });
    if (!values) return null;
    var body = { reason: values.reason };
    if (values.duration === 'permanent') body.permanent = true;
    else body.days = Number(values.duration);
    return global.OpsUI.post('/api/admin/accounts/' + encodeURIComponent(target.accountIndex) + '/ban', body);
  }

  function inspect(target) {
    var p = { index: String(target.characterIndex || '') };
    if (!target.characterIndex) p = { q: target.name };
    if (global.portalOpenDeepLink) global.portalOpenDeepLink('characters', p);
  }

  global.PlayerActions = {
    verbs: gmVerbs,
    runVerb: runVerb,
    run: runAction,
    banAccount: banAccount,
    inspect: inspect,
    FALLBACK_VERBS: FALLBACK_VERBS
  };

  // ---------------------------------------------------------------- section state

  var state = {
    rows: [],
    byIndex: Object.create(null),
    sort: 'name',
    dir: 'asc',
    filter: '',
    auto: true,
    world: null,
    loadedAt: null
  };
  var els = {};
  var stopPoll = null;
  var menu = null;
  var seq = 0;

  try {
    var saved = global.localStorage && global.localStorage.getItem('mircraft_players_sort');
    if (saved) {
      var s = JSON.parse(saved);
      if (s && s.sort) { state.sort = s.sort; state.dir = s.dir === 'desc' ? 'desc' : 'asc'; }
    }
    var auto = global.localStorage && global.localStorage.getItem('mircraft_players_auto');
    if (auto === '0') state.auto = false;
  } catch (e) { /* storage unavailable */ }

  function saveSort() {
    try { global.localStorage.setItem('mircraft_players_sort', JSON.stringify({ sort: state.sort, dir: state.dir })); } catch (e) { /* ignore */ }
  }

  var COLUMNS = [
    { key: 'name', label: 'Name' },
    { key: 'level', label: 'Lv', num: true },
    { key: 'className', label: 'Class' },
    { key: 'map', label: 'Map' },
    { key: 'hp', label: 'HP' },
    { key: 'mp', label: 'MP' },
    { key: 'pk', label: 'PK', num: true },
    { key: 'guild', label: 'Guild' },
    { key: 'accountId', label: 'Account' },
    { key: 'ip', label: 'IP' },
    { key: 'onlineSince', label: 'Online' },
    { key: '', label: '' }
  ];

  function sortValue(r, key) {
    switch (key) {
      case 'level': return Number(r.level) || 0;
      case 'pk': return Number(r.pk) || 0;
      case 'hp': return r.maxHp ? r.hp / r.maxHp : 0;
      case 'mp': return r.maxMp ? r.mp / r.maxMp : 0;
      case 'map': return String(r.mapTitle || r.mapFile || '').toLowerCase();
      case 'onlineSince': { var d = global.OpsUI.toDate(r.onlineSince); return d ? d.getTime() : 0; }
      default: return String(r[key] == null ? '' : r[key]).toLowerCase();
    }
  }

  function matchesFilter(r, f) {
    if (!f) return true;
    var hay = [r.name, r.accountId, r.className, r.mapTitle, r.mapFile, r.guild, r.ip, String(r.level)].join(' ').toLowerCase();
    return f.split(/\s+/).every(function (t) { return !t || hay.indexOf(t) >= 0; });
  }

  function vital(cur, max, cls) {
    cur = Number(cur) || 0; max = Number(max) || 0;
    var pct = max > 0 ? Math.max(0, Math.min(100, Math.round(100 * cur / max))) : 0;
    return '<div class="po-bar ' + cls + '" title="' + esc(cur + ' / ' + max) + '"><div class="po-bar-fill" style="width:' + pct + '%"></div>' +
      '<span class="po-bar-text">' + esc(cur) + '</span></div>';
  }

  function cellsFor(r, perIp) {
    var name = global.OpsUI.playerLink(r.name, { account: r.accountId, accountIndex: r.accountIndex, online: true, characterIndex: r.characterIndex, ip: r.ip });
    var badges = '';
    if (r.isGM) badges += ' <span class="ops-pill ops-pill-warn" title="Game master">GM</span>';
    if (r.dead) badges += ' <span class="ops-pill ops-pill-danger">dead</span>';
    if (r.chatBanned) badges += ' <span class="ops-pill ops-pill-danger" title="Chat banned">muted</span>';
    var map = esc(r.mapTitle || r.mapFile || ('#' + r.mapIndex)) + ' <span class="mir-muted">(' + esc(r.x) + ',' + esc(r.y) + ')</span>';
    var shared = perIp[r.ip] > 1 ? ' <span class="ops-pill ops-pill-warn" title="' + esc(perIp[r.ip] + ' characters online from this IP') + '">×' + perIp[r.ip] + '</span>' : '';
    var since = global.OpsUI.toDate(r.onlineSince);
    return [
      name + badges,
      esc(r.level),
      esc(r.className) + ' <span class="mir-muted">' + esc(r.gender === 'Female' ? '♀' : '♂') + '</span>',
      map,
      vital(r.hp, r.maxHp, 'hp'),
      vital(r.mp, r.maxMp, 'mp'),
      '<span class="' + (Number(r.pk) >= 200 ? 'po-pk-red' : Number(r.pk) >= 100 ? 'po-pk-yellow' : '') + '">' + esc(r.pk) + '</span>',
      esc(r.guild || ''),
      r.accountId ? '<button type="button" class="users-link" data-account-link="' + esc(r.accountId) + '" data-account-index="' + esc(r.accountIndex) + '">' + esc(r.accountId) + '</button>' : '',
      esc(r.ip || '') + shared,
      since ? '<span title="' + esc(since.toLocaleString()) + '">' + esc(global.OpsUI.ago(since).replace(/ ago$/, '')) + '</span>' : '',
      '<button type="button" class="mir-btn ops-row-btn" data-row-menu="' + esc(r.characterIndex) + '" aria-haspopup="menu">Actions ▾</button>'
    ];
  }

  function renderHeader() {
    var tr = els.thead;
    tr.innerHTML = COLUMNS.map(function (c) {
      if (!c.key) return '<th class="users-actions"></th>';
      var aria = c.key === state.sort ? (state.dir === 'desc' ? 'descending' : 'ascending') : 'none';
      return '<th data-sort="' + esc(c.key) + '" aria-sort="' + aria + '" scope="col">' + esc(c.label) + '</th>';
    }).join('');
  }

  /** Reconciles the table body with state.rows (filtered + sorted) without rebuilding unchanged rows. */
  function renderRows() {
    var f = state.filter.trim().toLowerCase();
    var perIp = {};
    state.rows.forEach(function (r) { if (r.ip) perIp[r.ip] = (perIp[r.ip] || 0) + 1; });
    var key = state.sort, mul = state.dir === 'desc' ? -1 : 1;
    var visible = state.rows.filter(function (r) { return matchesFilter(r, f); }).sort(function (a, b) {
      var va = sortValue(a, key), vb = sortValue(b, key);
      if (va < vb) return -1 * mul;
      if (va > vb) return 1 * mul;
      return String(a.name).localeCompare(String(b.name));
    });

    var tbody = els.tbody;
    var existing = Object.create(null);
    Array.prototype.forEach.call(tbody.querySelectorAll('tr[data-ci]'), function (tr) { existing[tr.getAttribute('data-ci')] = tr; });

    var keep = Object.create(null);
    visible.forEach(function (r, i) {
      var id = String(r.characterIndex);
      keep[id] = true;
      var tr = existing[id];
      var cells = cellsFor(r, perIp);
      if (!tr) {
        tr = document.createElement('tr');
        tr.setAttribute('data-ci', id);
        tr.innerHTML = cells.map(function (c, ci) { return '<td' + (COLUMNS[ci].num ? ' class="num"' : '') + '>' + c + '</td>'; }).join('');
        tr._cells = cells;
      } else {
        var tds = tr.children;
        for (var c = 0; c < cells.length; c++) {
          if (!tr._cells || tr._cells[c] !== cells[c]) {
            // Never rewrite the cell holding the open actions menu's anchor.
            if (menu && menu._anchor && tds[c].contains(menu._anchor)) continue;
            tds[c].innerHTML = cells[c];
          }
        }
        tr._cells = cells;
      }
      if (tbody.children[i] !== tr) tbody.insertBefore(tr, tbody.children[i] || null);
    });
    Object.keys(existing).forEach(function (id) {
      if (!keep[id] && existing[id].parentNode) existing[id].parentNode.removeChild(existing[id]);
    });

    els.empty.classList.toggle('hidden', visible.length > 0);
    els.empty.textContent = state.world && state.world !== 'running'
      ? 'The world is ' + state.world + ': nobody can be online.'
      : (state.rows.length ? 'No online player matches the filter.' : 'Nobody is online.');
    renderTiles(perIp);
  }

  function tile(label, value, cls) {
    return '<div class="metric-card' + (cls ? ' ' + cls : '') + '"><div class="metric-label">' + esc(label) + '</div><div class="metric-value">' + esc(value) + '</div></div>';
  }

  function renderTiles(perIp) {
    var rows = state.rows;
    var gms = rows.filter(function (r) { return r.isGM; }).length;
    var dead = rows.filter(function (r) { return r.dead; }).length;
    var ips = Object.keys(perIp);
    var shared = ips.filter(function (ip) { return perIp[ip] > 1; }).length;
    var levels = rows.map(function (r) { return Number(r.level) || 0; });
    var avg = levels.length ? Math.round(levels.reduce(function (a, b) { return a + b; }, 0) / levels.length) : 0;
    els.tiles.innerHTML =
      tile('Online', rows.length) +
      tile('Game masters', gms) +
      tile('Unique IPs', ips.length) +
      tile('Shared IPs', shared, shared ? 'metric-warn' : '') +
      tile('Dead', dead) +
      tile('Avg level', avg || '—');
  }

  function setBadge(n) {
    var b = document.getElementById('navOnlineBadge');
    if (!b) return;
    if (n == null || isNaN(n)) { b.classList.add('hidden'); return; }
    b.textContent = String(n);
    b.classList.toggle('hidden', !(n > 0));
  }

  async function refresh() {
    var my = ++seq;
    var r = await global.OpsUI.fetchResult('/api/admin/players-online');
    if (my !== seq) return;
    if (!r.ok) {
      els.stamp.textContent = 'Error: ' + r.error;
      return;
    }
    var j = global.OpsUI.unwrap(r.data) || {};
    state.rows = Array.isArray(j.rows) ? j.rows : [];
    state.world = j.world || null;
    state.loadedAt = new Date();
    state.byIndex = Object.create(null);
    state.rows.forEach(function (row) { state.byIndex[String(row.characterIndex)] = row; });
    setBadge(state.world === 'running' ? state.rows.length : null);
    els.stamp.textContent = (state.world && state.world !== 'running' ? 'World ' + state.world + ' · ' : '') + 'Updated ' + state.loadedAt.toLocaleTimeString();
    els.count.textContent = String(state.rows.length);
    renderRows();
  }

  // ---------------------------------------------------------------- row actions menu

  function closeMenu() {
    if (menu && menu.parentNode) menu.parentNode.removeChild(menu);
    menu = null;
  }

  function targetOf(row) {
    return { name: row.name, characterIndex: row.characterIndex, online: true, accountIndex: row.accountIndex, accountId: row.accountId };
  }

  var MENU = [
    { id: 'inspect', label: 'Inspect character' },
    { id: 'message', label: 'Message…' },
    { id: 'kick', label: 'Kick…' },
    { id: 'kill', label: 'Kill…' },
    { id: 'safezone', label: 'Send to safe zone…' },
    { id: 'teleport', label: 'Teleport…' },
    { id: 'chatban', label: 'Chat ban…' },
    { id: 'ban', label: 'Ban account…', danger: true }
  ];

  async function runMenu(id, row) {
    var t = targetOf(row);
    var res = null;
    if (id === 'inspect') return inspect(t);
    if (id === 'ban') res = await banAccount(t);
    else res = await runAction(id, t);
    if (res && res.__ok) refresh();
  }

  function openMenu(anchor) {
    var id = anchor.getAttribute('data-row-menu');
    var row = state.byIndex[id];
    var same = menu && menu._anchor === anchor;
    closeMenu();
    if (same || !row) return;
    menu = document.createElement('div');
    menu.className = 'player-chip-menu po-row-menu';
    menu.setAttribute('role', 'menu');
    menu._anchor = anchor;
    var head = document.createElement('div');
    head.className = 'player-chip-menu-head';
    head.textContent = row.name + (row.accountId ? ' · ' + row.accountId : '');
    menu.appendChild(head);
    MENU.forEach(function (it) {
      var b = document.createElement('button');
      b.type = 'button';
      b.setAttribute('role', 'menuitem');
      b.textContent = it.label;
      if (it.danger) b.className = 'po-menu-danger';
      b.addEventListener('click', function (e) {
        e.stopPropagation();
        closeMenu();
        runMenu(it.id, row);
      });
      menu.appendChild(b);
    });
    document.body.appendChild(menu);
    var r = anchor.getBoundingClientRect();
    var h = menu.offsetHeight || 0, w = menu.offsetWidth || 0;
    var top = r.bottom + 4;
    if (top + h > global.innerHeight - 8) top = Math.max(8, r.top - h - 4);
    var left = Math.min(r.left, global.innerWidth - w - 8);
    menu.style.top = top + 'px';
    menu.style.left = Math.max(8, left) + 'px';
    var first = menu.querySelector('button');
    if (first) first.focus();
  }

  document.addEventListener('click', function (e) {
    if (!menu) return;
    if (menu.contains(e.target) || (menu._anchor && menu._anchor.contains(e.target))) return;
    closeMenu();
  }, true);
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && menu) closeMenu(); });
  global.addEventListener('resize', closeMenu);

  // ---------------------------------------------------------------- section

  function build(el) {
    el.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar">' +
      '    <h2>Players Online <span class="po-count" data-role="count">0</span></h2>' +
      '    <div class="po-tools">' +
      '      <input type="search" class="fd-input po-filter" data-role="filter" data-section-search placeholder="Filter name, account, map, IP…" aria-label="Filter players" />' +
      '      <label class="mir-toggle"><input type="checkbox" data-role="auto" /> Auto-refresh</label>' +
      '      <span class="mir-muted" data-role="stamp"></span>' +
      '      <button type="button" class="mir-btn" data-role="csv">Export CSV</button>' +
      '      <button type="button" class="mir-btn primary" data-role="refresh">Refresh</button>' +
      '    </div>' +
      '  </div>' +
      '  <div class="metrics-grid po-tiles" data-role="tiles"></div>' +
      '  <div class="ops-table-scroll">' +
      '    <table class="user-table users-table po-table">' +
      '      <thead><tr data-role="thead"></tr></thead>' +
      '      <tbody data-role="tbody"></tbody>' +
      '    </table>' +
      '  </div>' +
      '  <p class="mir-muted po-empty hidden" data-role="empty"></p>' +
      '  <p class="mir-muted po-hint">Click a name for its player menu, or use Actions for moderation. Every action asks once and is recorded in the audit trail.</p>' +
      '</div>';
    els.thead = el.querySelector('[data-role="thead"]');
    els.tbody = el.querySelector('[data-role="tbody"]');
    els.tiles = el.querySelector('[data-role="tiles"]');
    els.empty = el.querySelector('[data-role="empty"]');
    els.stamp = el.querySelector('[data-role="stamp"]');
    els.count = el.querySelector('[data-role="count"]');
    els.filter = el.querySelector('[data-role="filter"]');
    els.auto = el.querySelector('[data-role="auto"]');

    els.auto.checked = state.auto;
    renderHeader();

    els.filter.addEventListener('input', function () {
      state.filter = els.filter.value;
      if (global.portalSetHashParams) global.portalSetHashParams({ q: state.filter.trim() || null }, true);
      renderRows();
    });
    els.auto.addEventListener('change', function () {
      state.auto = els.auto.checked;
      try { global.localStorage.setItem('mircraft_players_auto', state.auto ? '1' : '0'); } catch (e) { /* ignore */ }
      startPolling();
    });
    el.querySelector('[data-role="refresh"]').addEventListener('click', function (e) {
      global.OpsUI.withBusy(e.currentTarget, refresh);
    });
    el.querySelector('[data-role="csv"]').addEventListener('click', function () {
      var rows = state.rows.map(function (r) {
        return [r.characterIndex, r.name, r.accountId, r.level, r.className, r.gender, r.mapTitle, r.x, r.y, r.hp, r.maxHp, r.mp, r.maxMp, r.pk, r.guild, r.ip, r.onlineSince, r.isGM ? 'GM' : ''];
      });
      global.OpsUI.download('players-online-' + new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-') + '.csv',
        global.OpsUI.toCsv(['Index', 'Name', 'Account', 'Level', 'Class', 'Gender', 'Map', 'X', 'Y', 'HP', 'MaxHP', 'MP', 'MaxMP', 'PK', 'Guild', 'IP', 'OnlineSince', 'GM'], rows),
        'text/csv;charset=utf-8');
    });
    els.thead.addEventListener('click', function (e) {
      var th = e.target.closest ? e.target.closest('th[data-sort]') : null;
      if (!th) return;
      var key = th.getAttribute('data-sort');
      if (state.sort === key) state.dir = state.dir === 'asc' ? 'desc' : 'asc';
      else { state.sort = key; state.dir = key === 'level' || key === 'pk' ? 'desc' : 'asc'; }
      saveSort();
      renderHeader();
      renderRows();
    });
    els.tbody.addEventListener('click', function (e) {
      var t = e.target;
      var btn = t.closest ? t.closest('[data-row-menu]') : null;
      if (btn) { e.stopPropagation(); openMenu(btn); return; }
      var acc = t.closest ? t.closest('[data-account-link]') : null;
      if (acc) {
        var p = { q: acc.getAttribute('data-account-link'), mode: 'account' };
        var ai = acc.getAttribute('data-account-index');
        if (ai && ai !== '0') p.account = ai;
        if (global.portalOpenDeepLink) global.portalOpenDeepLink('accounts', p);
      }
    });
    els.tbody.addEventListener('dblclick', function (e) {
      var tr = e.target.closest ? e.target.closest('tr[data-ci]') : null;
      if (!tr || (e.target.closest && e.target.closest('button'))) return;
      var row = state.byIndex[tr.getAttribute('data-ci')];
      if (row) inspect(targetOf(row));
    });
  }

  function startPolling() {
    if (stopPoll) { stopPoll(); stopPoll = null; }
    // AdminSections.poll only ticks while this section and the browser tab are visible.
    if (state.auto) stopPoll = global.AdminSections.poll('players-online', refresh, POLL_MS);
  }

  // Keep the sidebar badge current from the topbar status poll (every 10 s) even when this section is not shown.
  // app.js (which defines AdminStatus) loads after this file, so subscribe once the document is parsed.
  function hookStatusBadge() {
    if (!(global.AdminStatus && typeof global.AdminStatus.onChange === 'function')) return;
    global.AdminStatus.onChange(function (st) {
      if (!st) return;
      var world = st.world || st.worldState;
      setBadge(world === 'running' && st.players != null ? Number(st.players) : null);
    });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', hookStatusBadge);
  else hookStatusBadge();

  global.portalRegisterDeepLink('players-online', function (params) {
    if (!els.filter) return;
    var q = params && params.q != null ? String(params.q) : '';
    if (q !== els.filter.value) {
      els.filter.value = q;
      state.filter = q;
      renderRows();
    }
  });

  global.AdminSections.register('players-online', {
    load: function (el) { build(el); },
    show: function () {
      startPolling();
      return refresh();
    },
    hide: function () {
      if (stopPoll) { stopPoll(); stopPoll = null; }
      closeMenu();
    }
  });
})(window);
