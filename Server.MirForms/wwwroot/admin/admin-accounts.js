/**
 * Accounts section (#section-accounts) — B3.
 *
 * Search (account / character / IP, exact toggle), filter chips, paged sortable table with server totals, and a detail drawer
 * (AdminInspect) with the profile form (dirty guard) and every account action: Ban… (presets + reason), Unban, Admin toggle,
 * Set password…, Require password change, Clear storage password, Currency…, Delete… (typed confirmation).
 *
 * Endpoints: GET /api/admin/accounts, GET /api/admin/accounts/{index}, POST /api/admin/accounts (create) and
 * POST /api/admin/accounts/{index}/{update|ban|unban|admin|require-password-change|password|clear-storage-password|currency|delete}.
 * Deep links: #section=accounts&q=<text>&mode=account|player|ip[&exact=1][&filter=...][&account=<index>].
 */
(function (global) {
  'use strict';

  var API = '/api/admin/accounts';
  var PASSWORD_RE = /^[A-Za-z0-9]{5,15}$/;
  var ACCOUNT_RE = /^[A-Za-z0-9]{3,15}$/;
  var FILTERS = [
    { id: 'all', label: 'All' },
    { id: 'online', label: 'Online' },
    { id: 'banned', label: 'Banned' },
    { id: 'admin', label: 'Admins' },
    { id: 'requirepw', label: 'Password change' }
  ];

  var state = {
    q: '', mode: 'account', exact: false, filter: 'all', sort: 'index', dir: 'asc',
    page: 1, pageSize: 50, rows: [], total: 0, pages: 1, seq: 0
  };
  var root = null;
  var drawer = { index: null, detail: null, dirty: false, token: 0, node: null, original: null };

  function esc(s) { return global.OpsUI.esc(s); }
  function q(sel) { return root ? root.querySelector(sel) : null; }
  function num(n) { return global.OpsUI.formatNumber(n || 0); }
  function when(v) { return global.OpsUI.fmtDate(v) || '—'; }
  function ago(v) { var a = global.OpsUI.ago(v); return a || ''; }

  function banText(a) {
    if (!a.banned) return '';
    return a.banPermanent ? 'permanent' : 'until ' + when(a.banExpiry);
  }

  function statusHtml(a) {
    var bits = [];
    if (a.banned) {
      bits.push('<span class="ops-pill ops-pill-danger" title="' + esc((a.banReason || '') + ' · ' + banText(a)) + '">Banned</span>');
    }
    if (a.admin) bits.push('<span class="ops-pill ops-pill-warn">Admin</span>');
    if (a.requirePasswordChange) bits.push('<span class="ops-pill" title="Must change the password at the next game login">PW change</span>');
    if (a.online) bits.push('<span class="users-online" title="In game">&#9679; ' + esc(a.onlineCharacter || 'online') + '</span>');
    else if (a.connected) bits.push('<span class="users-online" title="Connected (character select)">&#9675; select</span>');
    return bits.join(' ');
  }

  function ipLink(ip) {
    if (!ip) return '<span class="mir-muted">—</span>';
    return '<button type="button" class="users-link" data-act="ip" data-ip="' + esc(ip) + '" title="Accounts using this IP">' + esc(ip) + '</button>';
  }

  // ------------------------------------------------------------------ skeleton

  function build(el) {
    root = el;
    var chips = FILTERS.map(function (f) {
      return '<button type="button" class="mir-btn acc-chip' + (f.id === state.filter ? ' active' : '') + '" data-filter="' + f.id + '">' + esc(f.label) + '</button>';
    }).join('');

    el.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar">' +
      '    <h2>Accounts</h2>' +
      '    <div class="acc-toolbar-actions">' +
      '      <button type="button" class="mir-btn" data-act="create">New account…</button>' +
      '      <button type="button" class="mir-btn" data-act="csv" title="Every match of this search as CSV">CSV</button>' +
      '      <button type="button" class="mir-btn" data-act="refresh">Refresh</button>' +
      '    </div>' +
      '  </div>' +
      '  <div class="admin-status-grid acc-tiles" id="accTiles"></div>' +
      '  <div class="toolbar acc-search">' +
      '    <div class="acc-search-row">' +
      '      <input type="search" id="accSearch" class="fd-input" data-section-search placeholder="Search… (Enter)" />' +
      '      <select id="accMode" class="fd-input" aria-label="Search by">' +
      '        <option value="account">Account ID</option><option value="player">Character</option><option value="ip">IP address</option>' +
      '      </select>' +
      '      <label class="acc-check"><input type="checkbox" id="accExact" /> Exact</label>' +
      '      <button type="button" class="mir-btn primary" data-act="search">Search</button>' +
      '    </div>' +
      '    <div class="acc-chips" role="group" aria-label="Show only">' + chips + '</div>' +
      '  </div>' +
      '  <p id="accStatus" class="mir-muted"></p>' +
      '  <div class="ops-table-scroll">' +
      '    <table class="user-table users-table acc-table" id="accTable">' +
      '      <thead><tr>' +
      '        <th data-sort="index">Idx</th>' +
      '        <th data-sort="accountId">Account</th>' +
      '        <th data-sort="userName">Name / e-mail</th>' +
      '        <th data-sort="characters">Chars</th>' +
      '        <th data-sort="gold" class="num">Gold</th>' +
      '        <th data-sort="credit" class="num">Credit</th>' +
      '        <th data-sort="lastIp">Last IP</th>' +
      '        <th data-sort="lastDate">Last login</th>' +
      '        <th data-sort="created">Created</th>' +
      '        <th data-sort="status">Status</th>' +
      '        <th>Actions</th>' +
      '      </tr></thead>' +
      '      <tbody id="accBody"></tbody>' +
      '    </table>' +
      '  </div>' +
      '  <div class="acc-pager">' +
      '    <button type="button" class="mir-btn" data-act="prev">Prev</button>' +
      '    <span id="accPageInfo" class="mir-muted"></span>' +
      '    <button type="button" class="mir-btn" data-act="next">Next</button>' +
      '    <label class="mir-muted">Rows <select id="accPageSize" class="fd-input">' +
      '      <option>25</option><option selected>50</option><option>100</option><option>250</option></select></label>' +
      '  </div>' +
      '</div>';

    el.addEventListener('click', onClick);
    q('#accSearch').addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); search(); }
    });
    q('#accMode').addEventListener('change', function () { if (q('#accSearch').value.trim()) search(); });
    q('#accExact').addEventListener('change', function () { if (q('#accSearch').value.trim()) search(); });
    q('#accPageSize').addEventListener('change', function () {
      state.pageSize = Number(q('#accPageSize').value) || 50;
      reload(1);
    });
    Array.prototype.forEach.call(el.querySelectorAll('th[data-sort]'), function (th) {
      th.addEventListener('click', function () {
        var key = th.getAttribute('data-sort');
        if (state.sort === key) state.dir = state.dir === 'asc' ? 'desc' : 'asc';
        else { state.sort = key; state.dir = (key === 'index' || key === 'accountId' || key === 'userName') ? 'asc' : 'desc'; }
        reload(1);
      });
    });
    syncInputs();
  }

  function syncInputs() {
    if (!root) return;
    q('#accSearch').value = state.q;
    q('#accMode').value = state.mode;
    q('#accExact').checked = !!state.exact;
    Array.prototype.forEach.call(root.querySelectorAll('.acc-chip'), function (b) {
      b.classList.toggle('active', b.getAttribute('data-filter') === state.filter);
    });
    Array.prototype.forEach.call(root.querySelectorAll('th[data-sort]'), function (th) {
      var on = th.getAttribute('data-sort') === state.sort;
      th.setAttribute('aria-sort', on ? (state.dir === 'desc' ? 'descending' : 'ascending') : 'none');
    });
  }

  function search() {
    state.q = q('#accSearch').value.trim();
    state.mode = q('#accMode').value || 'account';
    state.exact = !!q('#accExact').checked;
    reload(1);
  }

  /** Applies hash params (q, mode, exact, filter); true when anything changed. */
  function applyParams(p) {
    if (!p) return false;
    var changed = false;
    if (p.q != null && p.q !== state.q) { state.q = String(p.q); changed = true; }
    // A link with q but no mode (e.g. the player chip's "Account" entry) searches by account ID.
    var mode = p.mode && /^(account|player|ip)$/.test(p.mode) ? p.mode : (p.q != null ? 'account' : null);
    if (mode && mode !== state.mode) { state.mode = mode; changed = true; }
    if (p.exact != null) {
      var ex = p.exact === '1' || p.exact === 'true';
      if (ex !== state.exact) { state.exact = ex; changed = true; }
    }
    if (p.filter && FILTERS.some(function (f) { return f.id === p.filter; }) && p.filter !== state.filter) { state.filter = p.filter; changed = true; }
    if (changed) syncInputs();
    return changed;
  }

  function listUrl(page, pageSize) {
    return API + '?q=' + encodeURIComponent(state.q) + '&mode=' + encodeURIComponent(state.mode) +
      '&exact=' + (state.exact ? 1 : 0) + '&filter=' + encodeURIComponent(state.filter) +
      '&sort=' + encodeURIComponent(state.sort) + '&dir=' + state.dir +
      '&page=' + page + '&pageSize=' + pageSize;
  }

  async function reload(page) {
    if (!root) return;
    if (page != null) state.page = page;
    var token = ++state.seq;
    var st = q('#accStatus');
    if (st) st.textContent = 'Loading…';
    syncInputs();

    if (global.AdminSections.current() === 'accounts') {
      global.portalSetHashParams({
        q: state.q || null,
        mode: state.mode !== 'account' ? state.mode : null,
        exact: state.exact ? '1' : null,
        filter: state.filter !== 'all' ? state.filter : null
      }, true);
    }

    var j;
    try {
      j = await global.OpsUI.fetchJson(listUrl(state.page, state.pageSize));
    } catch (e) {
      if (token !== state.seq) return;
      if (st) st.textContent = 'Error: ' + e.message;
      renderTiles(null);
      q('#accBody').innerHTML = '';
      return;
    }
    if (token !== state.seq) return;   // a newer search owns the table

    state.rows = j.rows || [];
    state.total = Number(j.total) || 0;
    state.pages = Number(j.pages) || 1;
    state.page = Number(j.page) || 1;
    renderTiles(j);
    renderRows();
    if (st) st.textContent = state.total + ' account(s) match' + (state.q ? ' “' + state.q + '”' : '') + (j.world && j.world !== 'running' ? ' · world ' + j.world + ' (edits are saved to the account file at once)' : '');
    q('#accPageInfo').textContent = 'Page ' + state.page + ' / ' + state.pages;
    q('[data-act="prev"]').disabled = state.page <= 1;
    q('[data-act="next"]').disabled = state.page >= state.pages;
  }

  function tile(k, v, title) {
    return '<div class="admin-status-tile"' + (title ? ' title="' + esc(title) + '"' : '') + '><span class="k">' + esc(k) + '</span><span class="v">' + esc(v) + '</span></div>';
  }

  function renderTiles(j) {
    var box = q('#accTiles');
    if (!box) return;
    if (!j || !j.totals) { box.innerHTML = ''; return; }
    var t = j.totals;
    box.innerHTML =
      tile('Accounts', num(t.accounts)) +
      tile('Online', num(t.online)) +
      tile('Banned', num(t.banned)) +
      tile('Admins', num(t.admins)) +
      tile('Gold', num(t.gold), 'Total gold of non-admin, non-banned accounts') +
      tile('Credit', num(t.credit), 'Total credit of non-admin, non-banned accounts');
  }

  function renderRows() {
    var body = q('#accBody');
    if (!body) return;
    if (!state.rows.length) {
      body.innerHTML = '<tr><td colspan="11" class="mir-muted">No accounts match.</td></tr>';
      return;
    }
    body.innerHTML = state.rows.map(function (a) {
      var sub = a.email ? '<div class="users-sub mir-muted">' + esc(a.email) + '</div>' : '';
      var created = when(a.creationDate) + (a.creationIp && a.creationIp !== a.lastIp ? '<div class="users-sub">' + ipLink(a.creationIp) + '</div>' : '');
      var actions = (a.banned
        ? '<button type="button" class="mir-btn ops-row-btn" data-act="unban" data-index="' + a.index + '">Unban</button>'
        : '<button type="button" class="mir-btn ops-row-btn danger" data-act="ban" data-index="' + a.index + '">Ban…</button>') +
        ' <button type="button" class="mir-btn ops-row-btn" data-act="open" data-index="' + a.index + '">Details</button>';
      return '<tr data-index="' + a.index + '" class="acc-row' + (drawer.index === a.index ? ' selected' : '') + '">' +
        '<td>' + esc(a.index) + '</td>' +
        '<td><button type="button" class="users-link" data-act="open" data-index="' + a.index + '">' + esc(a.accountId) + '</button></td>' +
        '<td>' + esc(a.userName || '') + sub + '</td>' +
        '<td>' + esc(a.characterCount) + (a.deletedCount ? ' <span class="mir-muted" title="Deleted characters">+' + esc(a.deletedCount) + '</span>' : '') + '</td>' +
        '<td class="num">' + num(a.gold) + '</td>' +
        '<td class="num">' + num(a.credit) + '</td>' +
        '<td>' + ipLink(a.lastIp) + '</td>' +
        '<td title="' + esc(when(a.lastDate)) + '">' + esc(a.lastDate ? ago(a.lastDate) : '—') + '</td>' +
        '<td>' + created + '</td>' +
        '<td>' + statusHtml(a) + '</td>' +
        '<td class="users-actions">' + actions + '</td>' +
        '</tr>';
    }).join('');
  }

  function rowByIndex(index) {
    for (var i = 0; i < state.rows.length; i++) if (state.rows[i].index === index) return state.rows[i];
    if (drawer.detail && drawer.detail.account && drawer.detail.account.index === index) return drawer.detail.account;
    return null;
  }

  async function onClick(e) {
    var t = e.target && e.target.closest ? e.target.closest('[data-act],[data-filter]') : null;
    if (!t || !root.contains(t)) {
      var tr = e.target && e.target.closest ? e.target.closest('tr.acc-row') : null;
      if (tr) openDetail(Number(tr.getAttribute('data-index')));
      return;
    }
    if (t.hasAttribute('data-filter')) {
      state.filter = t.getAttribute('data-filter');
      reload(1);
      return;
    }
    var act = t.getAttribute('data-act');
    var index = Number(t.getAttribute('data-index'));
    var row = rowByIndex(index);
    switch (act) {
      case 'search': search(); break;
      case 'refresh': global.OpsUI.withBusy(t, function () { return reload(); }); break;
      case 'prev': if (state.page > 1) reload(state.page - 1); break;
      case 'next': if (state.page < state.pages) reload(state.page + 1); break;
      case 'csv': global.OpsUI.withBusy(t, exportCsv, 'Exporting…'); break;
      case 'create': createAccount(); break;
      case 'open': openDetail(index); break;
      case 'ip':
        state.q = t.getAttribute('data-ip');
        state.mode = 'ip';
        state.exact = true;
        reload(1);
        break;
      case 'ban': if (row) banAccount(row); break;
      case 'unban': if (row) unbanAccount(row); break;
    }
  }

  async function exportCsv() {
    var all = [];
    var page = 1, pages = 1;
    try {
      do {
        var j = await global.OpsUI.fetchJson(listUrl(page, 500));
        all = all.concat(j.rows || []);
        pages = Number(j.pages) || 1;
        page++;
      } while (page <= pages && page <= 200);
    } catch (e) {
      global.portalToast('Export failed: ' + e.message, 'error');
      return;
    }
    var header = ['Index', 'Account', 'User name', 'E-mail', 'Characters', 'Gold', 'Credit', 'Admin', 'Banned', 'Ban reason', 'Ban until',
      'Online', 'Require password change', 'Last IP', 'Last login', 'Creation IP', 'Created'];
    var rows = all.map(function (a) {
      return [a.index, a.accountId, a.userName, a.email, a.characterCount, a.gold, a.credit, a.admin ? 'yes' : '', a.banned ? 'yes' : '',
        a.banReason || '', a.banned ? (a.banPermanent ? 'permanent' : a.banExpiry) : '', a.online ? (a.onlineCharacter || 'yes') : '',
        a.requirePasswordChange ? 'yes' : '', a.lastIp, a.lastDate || '', a.creationIp, a.creationDate || ''];
    });
    global.OpsUI.download('accounts-' + new Date().toISOString().slice(0, 10) + '.csv', global.OpsUI.toCsv(header, rows), 'text/csv;charset=utf-8');
  }

  // ------------------------------------------------------------------ actions (one dialog each; Cancel sends nothing)

  async function post(index, verb, body) {
    var j = await global.OpsUI.post(API + '/' + index + '/' + verb, body || {});
    if (j.__ok) await afterChange(index, verb === 'delete');
    return j;
  }

  async function afterChange(index, deleted) {
    var tasks = [reload()];
    if (drawer.index === index) {
      if (deleted) closeDrawer(true);
      else tasks.push(loadDetail(index, true));
    }
    await Promise.all(tasks);
  }

  function banDialog(label) {
    return global.portalDialog({
      title: 'Ban ' + label,
      message: 'Characters in game are disconnected at once; the reason is shown to the player at login.',
      confirmLabel: 'Ban',
      danger: true,
      fields: [
        {
          name: 'duration', label: 'Duration', type: 'presets', value: '1', options: [
            { value: '1', label: '1 day' }, { value: '7', label: '7 days' }, { value: '30', label: '30 days' },
            { value: 'custom', label: 'Custom…' }, { value: 'permanent', label: 'Permanent' }
          ]
        },
        { name: 'customDays', label: 'Custom duration in days (0.5 = 12 hours)', type: 'number', min: 0 },
        { name: 'reason', label: 'Reason (shown to the player)', type: 'text', required: true, placeholder: 'Botting' }
      ],
      validate: function (v) {
        if (v.duration === 'custom' && !(v.customDays > 0)) return 'Enter a custom number of days.';
        return null;
      },
      preview: function (v) {
        if (v.duration === 'permanent') return 'Permanent ban: only Unban lifts it.';
        var d = v.duration === 'custom' ? v.customDays : Number(v.duration);
        if (!(d > 0)) return null;
        return 'Banned until ' + new Date(Date.now() + d * 86400000).toLocaleString() + '.';
      }
    }).then(function (v) {
      if (!v) return null;
      var body = { reason: v.reason };
      if (v.duration === 'permanent') body.permanent = true;
      else body.days = v.duration === 'custom' ? v.customDays : Number(v.duration);
      return body;
    });
  }

  async function banAccount(a) {
    var body = await banDialog(a.accountId);
    if (!body) return;
    await post(a.index, 'ban', body);
  }

  async function unbanAccount(a) {
    var ok = await global.portalConfirm('Lift the ban on ' + a.accountId + '?' + (a.banReason ? ' (Reason: ' + a.banReason + ')' : ''),
      { title: 'Unban account', confirmLabel: 'Unban', danger: false });
    if (!ok) return;
    await post(a.index, 'unban', {});
  }

  async function toggleAdmin(d) {
    var a = d.account;
    var value = !a.admin;
    var msg = value
      ? 'Grant the Admin flag to ' + a.accountId + '? Admins can sign in to this panel and are GMs in game (from their next login).'
      : 'Remove the Admin flag from ' + a.accountId + '? Their panel sessions end and in-game GM powers are taken away now.' +
        (d.self ? ' This is YOUR account: you will be signed out.' : '');
    var ok = await global.portalConfirm(msg, { title: value ? 'Make admin' : 'Remove admin', confirmLabel: value ? 'Grant' : 'Remove', danger: !value });
    if (!ok) return;
    await post(a.index, 'admin', { value: value, confirmSelf: !!d.self });
  }

  async function setPassword(d) {
    var a = d.account;
    var v = await global.portalDialog({
      title: 'Set password: ' + a.accountId,
      message: '5-15 letters or digits. Other panel sessions of this account are signed out.',
      confirmLabel: 'Set password',
      fields: [
        { name: 'password', label: 'New password', type: 'password', required: true },
        { name: 'confirm', label: 'Repeat the password', type: 'password', required: true },
        { name: 'requireChange', label: 'Require a change at the next game login', type: 'checkbox', value: false }
      ],
      validate: function (x) {
        if (!PASSWORD_RE.test(x.password)) return 'The password must be 5-15 letters or digits.';
        if (x.password !== x.confirm) return 'The passwords do not match.';
        return null;
      },
      preview: function (x) {
        if (x.requireChange && d.self) return 'This is YOUR account: requiring a change signs you out of the panel.';
        if (x.requireChange && a.admin) return 'An admin with a required change cannot sign in to the panel until it is done in game.';
        return null;
      }
    });
    if (!v) return;
    await post(a.index, 'password', { password: v.password, requireChange: !!v.requireChange, confirmSelf: !!d.self });
  }

  async function toggleRequireChange(d) {
    var a = d.account;
    var value = !a.requirePasswordChange;
    var msg = value
      ? a.accountId + ' must change the password at the next game login (the game refuses the login until then).' +
        (a.admin ? ' Admins with a pending change cannot sign in to this panel.' : '') + (d.self ? ' This is YOUR account: you will be signed out.' : '')
      : 'Clear the password change requirement for ' + a.accountId + '?';
    var ok = await global.portalConfirm(msg, { title: 'Require password change', confirmLabel: value ? 'Require' : 'Clear', danger: value && !!d.self });
    if (!ok) return;
    await post(a.index, 'require-password-change', { value: value, confirmSelf: !!d.self });
  }

  async function clearStoragePassword(d) {
    var a = d.account;
    var ok = await global.portalConfirm('Clear the storage password of ' + a.accountId + '? Online characters are told at once and their storage is locked again.',
      { title: 'Clear storage password', confirmLabel: 'Clear' });
    if (!ok) return;
    await post(a.index, 'clear-storage-password', {});
  }

  async function currency(d) {
    var a = d.account;
    var v = await global.portalDialog({
      title: 'Gold & credit: ' + a.accountId,
      message: 'Now: gold ' + num(a.gold) + ' · credit ' + num(a.credit) + (a.online ? ' · online clients update at once' : ''),
      confirmLabel: 'Apply',
      fields: [
        { name: 'mode', label: 'Mode', type: 'select', value: 'add', options: [{ value: 'add', label: 'Add (negative removes)' }, { value: 'set', label: 'Set to' }] },
        { name: 'gold', label: 'Gold', type: 'number' },
        { name: 'credit', label: 'Credit', type: 'number' },
        { name: 'reason', label: 'Reason (audit trail)', type: 'text', placeholder: 'Compensation' }
      ],
      validate: function (x) {
        if (x.gold == null && x.credit == null) return 'Enter gold and/or credit.';
        if ((x.gold != null && Math.floor(x.gold) !== x.gold) || (x.credit != null && Math.floor(x.credit) !== x.credit)) return 'Use whole numbers.';
        if (x.mode === 'set' && ((x.gold != null && x.gold < 0) || (x.credit != null && x.credit < 0))) return 'Set values cannot be negative.';
        return null;
      },
      preview: function (x) {
        function after(cur, val) {
          if (val == null) return cur;
          var n = x.mode === 'set' ? val : cur + val;
          return Math.max(0, Math.min(4294967295, n));
        }
        return 'Result: gold ' + num(after(a.gold, x.gold)) + ' · credit ' + num(after(a.credit, x.credit));
      }
    });
    if (!v) return;
    var body = { mode: v.mode, reason: v.reason || null };
    if (v.gold != null) body.gold = v.gold;
    if (v.credit != null) body.credit = v.credit;
    await post(a.index, 'currency', body);
  }

  async function deleteAccount(d) {
    var a = d.account;
    var chars = (d.characters || []).map(function (c) { return c.name; });
    var v = await global.portalDialog({
      title: 'Delete account ' + a.accountId,
      message: 'This permanently removes the account' + (chars.length ? ', its characters (' + chars.join(', ') + ')' : '') +
        ' and its market listings (open bids from other players on them are refunded by mail). Marriages and mentorships with characters of other accounts are ended. It must be offline. This cannot be undone.',
      confirmLabel: 'Delete permanently',
      danger: true,
      fields: [
        { name: 'confirm', label: 'Type ' + a.accountId + ' to confirm', type: 'text', required: true },
        { name: 'reason', label: 'Reason (audit trail)', type: 'text' }
      ],
      validate: function (x) { return x.confirm === a.accountId ? null : 'Type the account ID exactly.'; }
    });
    if (!v) return;
    await post(a.index, 'delete', { confirm: v.confirm, reason: v.reason || null });
  }

  async function createAccount() {
    var v = await global.portalDialog({
      title: 'New account',
      message: 'The server drops accounts without characters when it loads the account file, so create a character before the next restart.',
      confirmLabel: 'Create',
      fields: [
        { name: 'accountId', label: 'Account ID (3-15 letters or digits)', type: 'text', required: true },
        { name: 'password', label: 'Password (5-15 letters or digits)', type: 'password', required: true },
        { name: 'confirm', label: 'Repeat the password', type: 'password', required: true },
        { name: 'userName', label: 'User name (optional)', type: 'text' },
        { name: 'email', label: 'E-mail (optional)', type: 'text' },
        { name: 'requirePasswordChange', label: 'Require a password change at the first login', type: 'checkbox', value: false }
      ],
      validate: function (x) {
        if (!ACCOUNT_RE.test(x.accountId)) return 'The account ID must be 3-15 letters or digits.';
        if (!PASSWORD_RE.test(x.password)) return 'The password must be 5-15 letters or digits.';
        if (x.password !== x.confirm) return 'The passwords do not match.';
        if (x.userName.length > 20) return 'The user name is at most 20 characters.';
        if (x.email.length > 50) return 'The e-mail is at most 50 characters.';
        return null;
      }
    });
    if (!v) return;
    var j = await global.OpsUI.post(API, {
      accountId: v.accountId, password: v.password, userName: v.userName, email: v.email, requirePasswordChange: !!v.requirePasswordChange
    });
    if (!j.__ok) return;
    state.q = v.accountId;
    state.mode = 'account';
    state.exact = true;
    state.filter = 'all';
    await reload(1);
    if (j.data && j.data.index) openDetail(j.data.index);
  }

  // ------------------------------------------------------------------ detail drawer

  function closeDrawer(force) {
    if (!force && drawer.dirty) return;
    drawer.dirty = false;
    if (global.AdminInspect.isOpen() && drawer.index != null) global.AdminInspect.close();
  }

  async function confirmDiscard() {
    if (!drawer.dirty) return true;
    var ok = await global.portalConfirm('Discard the unsaved profile changes of this account?', { title: 'Unsaved changes', confirmLabel: 'Discard' });
    if (ok) drawer.dirty = false;
    return ok;
  }

  async function openDetail(index) {
    if (!index) return;
    if (drawer.index === index && global.AdminInspect.isOpen()) { loadDetail(index, true); return; }
    if (!(await confirmDiscard())) return;
    if (global.AdminInspect.confirmReplace && !(await global.AdminInspect.confirmReplace('accounts'))) return;
    drawer.index = index;
    drawer.detail = null;
    drawer.dirty = false;
    var token = ++drawer.token;
    var body = document.createElement('div');
    body.className = 'acc-detail';
    body.innerHTML = '<p class="mir-muted">Loading…</p>';
    body.addEventListener('click', onDrawerClick);
    drawer.node = body;
    global.AdminInspect.open({
      title: 'Account #' + index,
      node: body,
      owner: 'accounts',
      isDirty: function () { return drawer.token === token && drawer.dirty; },
      onClose: function () {
        if (drawer.token !== token) return;
        drawer.index = null;
        drawer.detail = null;
        drawer.dirty = false;
        drawer.node = null;
        markSelected();
        if (global.AdminSections.current() === 'accounts') global.portalSetHashParams({ account: null }, true);
      }
    });
    markSelected();
    if (global.AdminSections.current() === 'accounts') global.portalSetHashParams({ account: index }, true);
    await loadDetail(index, false);
  }

  function markSelected() {
    if (!root) return;
    Array.prototype.forEach.call(root.querySelectorAll('tr.acc-row'), function (tr) {
      tr.classList.toggle('selected', Number(tr.getAttribute('data-index')) === drawer.index);
    });
  }

  async function loadDetail(index, keepForm) {
    var token = drawer.token;
    var d;
    try {
      d = await global.OpsUI.fetchJson(API + '/' + index);
    } catch (e) {
      if (token !== drawer.token) return;
      var b = drawer.node;
      if (b) b.innerHTML = '<p class="mir-muted">Error: ' + esc(e.message) + '</p>';
      return;
    }
    if (token !== drawer.token || drawer.index !== index) return;
    if (keepForm && drawer.dirty) {
      // Keep the admin's unsaved typing; refresh everything else.
      drawer.detail = d;
      renderDetail(d, true);
      return;
    }
    drawer.detail = d;
    renderDetail(d, false);
  }

  function kv(label, valueHtml) {
    return '<dt>' + esc(label) + '</dt><dd>' + valueHtml + '</dd>';
  }

  function dateInputValue(iso) {
    var dt = global.OpsUI.toDate(iso);
    if (!dt) return '';
    return dt.toISOString().slice(0, 10);
  }

  function renderDetail(d, preserveForm) {
    var body = drawer.node;
    if (!body) return;
    var a = d.account;
    var title = document.getElementById('inspectDrawerTitle');
    if (title) title.textContent = 'Account · ' + a.accountId;

    var saved = null;
    if (preserveForm) {
      saved = {};
      Array.prototype.forEach.call(body.querySelectorAll('[data-field]'), function (inp) { saved[inp.getAttribute('data-field')] = inp.value; });
    }

    var actions =
      (a.banned
        ? '<button type="button" class="mir-btn" data-dact="unban">Unban</button>'
        : '<button type="button" class="mir-btn danger" data-dact="ban">Ban…</button>') +
      '<button type="button" class="mir-btn" data-dact="admin">' + (a.admin ? 'Remove admin' : 'Make admin') + '</button>' +
      '<button type="button" class="mir-btn" data-dact="password">Set password…</button>' +
      '<button type="button" class="mir-btn" data-dact="requirepw">' + (a.requirePasswordChange ? 'Clear password-change requirement' : 'Require password change') + '</button>' +
      '<button type="button" class="mir-btn" data-dact="storagepw"' + (d.hasStoragePassword ? '' : ' disabled title="No storage password set"') + '>Clear storage password</button>' +
      '<button type="button" class="mir-btn" data-dact="currency">Currency…</button>' +
      '<button type="button" class="mir-btn danger" data-dact="delete"' + (d.self ? ' disabled title="You cannot delete your own account"' : '') + '>Delete…</button>';

    var ban = a.banned
      ? '<div class="acc-ban-box"><span class="ops-pill ops-pill-danger">Banned</span> ' + esc(banText(a)) +
        (a.banReason ? '<div class="users-sub">' + esc(a.banReason) + '</div>' : '') + '</div>'
      : '';

    var info =
      kv('Index', esc(a.index)) +
      kv('Account ID', esc(a.accountId) + (d.self ? ' <span class="ops-pill ops-pill-warn">you</span>' : '')) +
      kv('Status', statusHtml(a) || '<span class="mir-muted">normal</span>') +
      kv('Created', esc(when(a.creationDate)) + (a.creationIp ? ' · ' + ipLink(a.creationIp) : '')) +
      kv('Last login', esc(when(a.lastDate)) + (a.lastIp ? ' · ' + ipLink(a.lastIp) : '')) +
      kv('Gold', esc(num(a.gold))) +
      kv('Credit', esc(num(a.credit))) +
      kv('Storage', esc(d.storageUsed + ' / ' + d.storageSize + ' slots') + (d.expandedStorage ? ' · expanded until ' + esc(when(d.expandedStorageExpiry)) : '')) +
      kv('Storage password', d.hasStoragePassword ? 'set' + (d.storagePasswordLastSet ? ' (' + esc(when(d.storagePasswordLastSet)) + ')' : '') : '<span class="mir-muted">not set</span>') +
      kv('Market listings', esc(d.auctions)) +
      kv('Panel sessions', esc(d.webSessions)) +
      kv('Wrong passwords', esc(d.wrongPasswordCount) + ' <span class="mir-muted">(this run)</span>');

    var chars = (d.characters || []).length
      ? '<table class="user-table acc-mini"><thead><tr><th>Name</th><th>Class</th><th>Lv</th><th>PK</th><th>Guild</th><th>Status</th><th>Last login</th></tr></thead><tbody>' +
        d.characters.map(function (c) {
          var status = c.deleted
            ? '<span class="ops-pill ops-pill-danger" title="Deleted ' + esc(when(c.deleteDate)) + '">Deleted</span>'
            : c.online ? '<span class="users-online">&#9679; online</span>' : '<span class="mir-muted">offline</span>';
          if (c.chatBanned) status += ' <span class="ops-pill ops-pill-warn" title="Chat banned until ' + esc(when(c.chatBanUntil)) + '">Chat ban</span>';
          return '<tr>' +
            '<td><button type="button" class="users-link" data-dact="character" data-char="' + esc(c.index) + '">' + esc(c.name) + '</button></td>' +
            '<td>' + esc(c.className) + '</td><td>' + esc(c.level) + '</td><td>' + esc(c.pk) + '</td>' +
            '<td>' + esc(c.guild || '—') + '</td><td>' + status + '</td>' +
            '<td title="' + esc(when(c.lastLogin)) + '">' + esc(c.lastLogin ? ago(c.lastLogin) : '—') + '</td></tr>';
        }).join('') + '</tbody></table>'
      : '<p class="mir-muted">No characters (the server drops such accounts when it loads the account file).</p>';

    var logins = (d.recentLogins || []).length
      ? '<table class="user-table acc-mini"><thead><tr><th>When</th><th>Result</th><th>IP</th><th>Via</th></tr></thead><tbody>' +
        d.recentLogins.map(function (l) {
          var cls = l.result === 'ok' ? 'ops-pill-ok' : 'ops-pill-danger';
          return '<tr><td title="' + esc(when(l.utc)) + '">' + esc(ago(l.utc)) + '</td><td><span class="ops-pill ' + cls + '">' + esc(l.result) + '</span></td>' +
            '<td>' + ipLink(l.ip) + '</td><td>' + esc(l.source) + '</td></tr>';
        }).join('') + '</tbody></table>'
      : '<p class="mir-muted">No logins recorded in the last 30 days.</p>';

    body.innerHTML =
      ban +
      '<div class="users-drawer-actions">' + actions + '</div>' +
      '<dl class="kv-list">' + info + '</dl>' +
      '<h4 class="acc-h">Profile <span class="acc-dirty hidden" id="accDirtyMark">unsaved</span></h4>' +
      '<form class="acc-form" autocomplete="off">' +
      '  <label><span class="mir-muted">User name</span><input class="fd-input" data-field="userName" maxlength="20" /></label>' +
      '  <label><span class="mir-muted">E-mail</span><input class="fd-input" data-field="email" maxlength="50" /></label>' +
      '  <label><span class="mir-muted">Birth date</span><input class="fd-input" type="date" data-field="birthDate" /></label>' +
      '  <label><span class="mir-muted">Secret question</span><input class="fd-input" data-field="secretQuestion" maxlength="30" /></label>' +
      '  <label><span class="mir-muted">Secret answer</span><input class="fd-input" data-field="secretAnswer" maxlength="30" placeholder="' +
      (d.secretAnswerSet ? 'set — leave blank to keep' : 'not set') + '" /></label>' +
      '  <div class="acc-form-actions">' +
      '    <button type="submit" class="mir-btn primary" data-dact="save" disabled>Save profile</button>' +
      '    <button type="button" class="mir-btn" data-dact="reset" disabled>Reset</button>' +
      '  </div>' +
      '</form>' +
      '<h4 class="acc-h">Characters</h4>' + chars +
      '<h4 class="acc-h">Recent logins</h4>' + logins;

    var original = {
      userName: a.userName || '',
      email: a.email || '',
      birthDate: dateInputValue(d.birthDate),
      secretQuestion: d.secretQuestion || '',
      secretAnswer: ''
    };
    drawer.original = original;
    Array.prototype.forEach.call(body.querySelectorAll('[data-field]'), function (inp) {
      var f = inp.getAttribute('data-field');
      inp.value = saved && saved[f] != null ? saved[f] : original[f];
      inp.addEventListener('input', updateDirty);
      inp.addEventListener('change', updateDirty);
    });
    updateDirty();

    body.querySelector('.acc-form').addEventListener('submit', function (e) { e.preventDefault(); saveProfile(); });
  }

  function formChanges() {
    var body = drawer.node;
    var out = {};
    if (!body || !drawer.original) return out;
    Array.prototype.forEach.call(body.querySelectorAll('[data-field]'), function (inp) {
      var f = inp.getAttribute('data-field');
      var v = String(inp.value || '').trim();
      if (f === 'secretAnswer') { if (v) out[f] = v; return; }
      if (v !== drawer.original[f]) out[f] = v;
    });
    return out;
  }

  function updateDirty() {
    var body = drawer.node;
    if (!body) return;
    var n = Object.keys(formChanges()).length;
    drawer.dirty = n > 0;
    var mark = body.querySelector('#accDirtyMark');
    if (mark) mark.classList.toggle('hidden', !drawer.dirty);
    var save = body.querySelector('[data-dact="save"]');
    var reset = body.querySelector('[data-dact="reset"]');
    if (save) save.disabled = !drawer.dirty;
    if (reset) reset.disabled = !drawer.dirty;
  }

  async function saveProfile() {
    var d = drawer.detail;
    if (!d) return;
    var changes = formChanges();
    if (!Object.keys(changes).length) return;
    var j = await global.OpsUI.post(API + '/' + d.account.index + '/update', changes);
    if (j.__ok) {
      drawer.dirty = false;
      await afterChange(d.account.index, false);
    }
  }

  function onDrawerClick(e) {
    var t = e.target && e.target.closest ? e.target.closest('[data-dact],[data-act]') : null;
    var d = drawer.detail;
    if (!t || !d) return;
    if (t.hasAttribute('data-act') && t.getAttribute('data-act') === 'ip') {
      var ip = t.getAttribute('data-ip');
      // Leaving the section can close or replace the drawer: ask before dropping unsaved profile edits.
      confirmDiscard().then(function (ok) { if (ok) global.portalOpenDeepLink('security', { ip: ip }); });
      return;
    }
    switch (t.getAttribute('data-dact')) {
      case 'ban': banAccount(d.account); break;
      case 'unban': unbanAccount(d.account); break;
      case 'admin': toggleAdmin(d); break;
      case 'password': setPassword(d); break;
      case 'requirepw': toggleRequireChange(d); break;
      case 'storagepw': clearStoragePassword(d); break;
      case 'currency': currency(d); break;
      case 'delete': deleteAccount(d); break;
      case 'reset':
        drawer.dirty = false;
        renderDetail(d, false);
        break;
      case 'character':
        var charIndex = t.getAttribute('data-char');
        // The Characters section reuses the shared drawer (our onClose resets the dirty flag): confirm first.
        confirmDiscard().then(function (ok) { if (ok) global.portalOpenDeepLink('characters', { index: charIndex }); });
        break;
    }
  }

  // Unsaved-profile guard for the shared inspect drawer: intercept its close button and Escape while our form is dirty.
  document.addEventListener('click', function (e) {
    if (!drawer.dirty || drawer.index == null) return;
    var t = e.target && e.target.closest ? e.target.closest('#inspectDrawerClose') : null;
    if (!t) return;
    e.stopImmediatePropagation();
    e.preventDefault();
    confirmDiscard().then(function (ok) { if (ok) global.AdminInspect.close(); });
  }, true);
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape' || !drawer.dirty || drawer.index == null || !global.AdminInspect.isOpen()) return;
    if (document.querySelector('.portal-confirm-backdrop')) return;
    e.stopImmediatePropagation();
    e.preventDefault();
    confirmDiscard().then(function (ok) { if (ok) global.AdminInspect.close(); });
  }, true);

  // ------------------------------------------------------------------ registration

  global.AdminSections.register('accounts', {
    load: function (el, params) {
      applyParams(params);
      build(el);
    },
    show: function (el, params) {
      applyParams(params);
      return reload();
    }
  });

  global.portalRegisterDeepLink('accounts', function (p) {
    if (!root) return;
    if (applyParams(p)) reload(1);
    var idx = Number(p && p.account);
    if (idx > 0 && drawer.index !== idx) openDetail(idx);
  });

  /** Shared account dialogs for other sections (e.g. Players Online "Ban account…"). */
  global.AccountActions = {
    banDialog: banDialog,
    ban: async function (index, label) {
      var body = await banDialog(label || ('account #' + index));
      if (!body) return null;
      return global.OpsUI.post(API + '/' + index + '/ban', body);
    },
    unban: function (index) { return global.OpsUI.post(API + '/' + index + '/unban', {}); },
    open: function (index) { global.portalOpenDeepLink('accounts', { account: index }); }
  };
})(window);
