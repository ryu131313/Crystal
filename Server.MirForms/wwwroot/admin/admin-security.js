/**
 * Security section (#section-security) — B3.
 *
 * Overview cards (GET /api/admin/security/overview?hours=): banned accounts (Lift ban), chat bans (Lift via the GM action
 * `unchatban`), admin accounts, admin panel sessions (Revoke), game IP blocks (Add… / Remove / Clear all), failed logins by IP,
 * accounts sharing an IP; plus the login-log search (GET /api/admin/security/logins). Overview refreshes every 30 s while visible.
 * Deep link: #section=security&ip=<address> (searches the login log for that IP).
 */
(function (global) {
  'use strict';

  var POLL_MS = 30000;
  var root = null;
  var stopPoll = null;
  var seq = 0;
  var loginSeq = 0;
  var lastHtml = Object.create(null);   // box id -> html (reconcile: only touch boxes whose content changed)
  var lastLogins = [];

  function esc(s) { return global.OpsUI.esc(s); }
  function q(sel) { return root ? root.querySelector(sel) : null; }
  function when(v) { return global.OpsUI.fmtDate(v) || '—'; }
  function ago(v) { return global.OpsUI.ago(v) || '—'; }
  function num(n) { return global.OpsUI.formatNumber(n || 0); }

  function setBox(id, html) {
    var el = q('#' + id);
    if (!el || lastHtml[id] === html) return;
    lastHtml[id] = html;
    el.innerHTML = html;
  }

  function table(rows, cols, empty) {
    if (!rows || !rows.length) return '<p class="mir-muted">' + esc(empty || 'None.') + '</p>';
    return '<div class="ops-table-scroll sec-scroll"><table class="user-table sec-table"><thead><tr>' +
      cols.map(function (c) { return '<th>' + esc(c.label) + '</th>'; }).join('') +
      '</tr></thead><tbody>' + rows.map(function (r) {
        return '<tr>' + cols.map(function (c) { return '<td' + (c.cls ? ' class="' + c.cls + '"' : '') + '>' + c.html(r) + '</td>'; }).join('') + '</tr>';
      }).join('') + '</tbody></table></div>';
  }

  function accountLink(accountId, index) {
    if (!accountId) return '<span class="mir-muted">—</span>';
    return '<button type="button" class="users-link" data-sact="account" data-account="' + esc(accountId) + '" data-index="' + (Number(index) || 0) + '">' + esc(accountId) + '</button>';
  }

  function ipLink(ip) {
    if (!ip) return '<span class="mir-muted">—</span>';
    return '<button type="button" class="users-link" data-sact="ip" data-ip="' + esc(ip) + '" title="Search the login log for this IP">' + esc(ip) + '</button>';
  }

  function btn(label, attrs, cls) {
    return '<button type="button" class="mir-btn ops-row-btn' + (cls ? ' ' + cls : '') + '" ' + attrs + '>' + esc(label) + '</button>';
  }

  function resultPill(r) {
    var cls = r === 'ok' ? 'ops-pill-ok' : /^refused|throttled/.test(r || '') ? 'ops-pill-warn' : 'ops-pill-danger';
    return '<span class="ops-pill ' + cls + '">' + esc(r) + '</span>';
  }

  function untilText(x) { return x.permanent ? 'permanent' : when(x.until); }

  // ------------------------------------------------------------------ skeleton

  function build(el) {
    root = el;
    el.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar">' +
      '    <h2>Security</h2>' +
      '    <div class="sec-toolbar">' +
      '      <label class="mir-muted">Failure window <select id="secHours" class="fd-input">' +
      '        <option value="1">1 hour</option><option value="24" selected>24 hours</option><option value="168">7 days</option><option value="720">30 days</option>' +
      '      </select></label>' +
      '      <button type="button" class="mir-btn" data-sact="refresh">Refresh</button>' +
      '    </div>' +
      '  </div>' +
      '  <p id="secStatus" class="mir-muted"></p>' +
      '  <div class="admin-status-grid sec-tiles" id="secTiles"></div>' +
      '  <div class="security-grid">' +
      '    <div><h3>Banned accounts</h3><div id="secBanned"></div></div>' +
      '    <div><h3>Chat bans</h3><div id="secChat"></div></div>' +
      '  </div>' +
      '  <div class="security-grid">' +
      '    <div><h3>Admin accounts</h3><div id="secAdmins"></div></div>' +
      '    <div><h3>Admin panel sessions</h3><p class="mir-muted sec-note">Browsers signed in to this panel now. Revoke signs that browser out at once.</p><div id="secSessions"></div></div>' +
      '  </div>' +
      '  <div class="security-grid">' +
      '    <div>' +
      '      <div class="sec-head"><h3>Game IP blocks</h3><span>' +
      '        <button type="button" class="mir-btn ops-row-btn" data-sact="block">Add block…</button> ' +
      '        <button type="button" class="mir-btn ops-row-btn danger" data-sact="clearblocks">Clear all</button></span></div>' +
      '      <p class="mir-muted sec-note">Blocked addresses cannot open new game connections (the server also adds short blocks itself when an IP exceeds MaxIP).</p>' +
      '      <div id="secBlocks"></div>' +
      '    </div>' +
      '    <div><h3>Failed logins by IP</h3><div id="secFailures"></div></div>' +
      '  </div>' +
      '  <h3>Accounts sharing an IP</h3>' +
      '  <p class="mir-muted sec-note">Last-login or creation IP used by two or more accounts (possible alts or ban evasion; shared networks also show up here).</p>' +
      '  <div id="secShared"></div>' +
      '  <h3 id="secLoginsHead">Login log</h3>' +
      '  <p class="mir-muted sec-note">Game client logins and admin panel sign-ins, good and failed, with their IPs.</p>' +
      '  <div class="toolbar sec-login-bar">' +
      '    <div class="sec-login-row">' +
      '      <input type="search" id="secLoginQ" class="fd-input" data-section-search placeholder="IP or account… (Enter)" />' +
      '      <select id="secLoginResult" class="fd-input" aria-label="Result">' +
      '        <option value="">All results</option><option value="ok">Successful</option><option value="failed">Failed</option>' +
      '        <option value="bad-password">Bad password</option><option value="no-account">No account</option><option value="banned">Banned</option>' +
      '        <option value="refused">Refused</option><option value="throttled">Throttled (panel)</option>' +
      '      </select>' +
      '      <select id="secLoginSource" class="fd-input" aria-label="Source">' +
      '        <option value="all">Game + panel</option><option value="game">Game only</option><option value="web">Panel only</option>' +
      '      </select>' +
      '      <select id="secLoginDays" class="fd-input" aria-label="Window">' +
      '        <option value="1">1 day</option><option value="7" selected>7 days</option><option value="30">30 days</option>' +
      '      </select>' +
      '      <button type="button" class="mir-btn primary" data-sact="logins">Search</button>' +
      '      <button type="button" class="mir-btn" data-sact="logincsv">CSV</button>' +
      '    </div>' +
      '  </div>' +
      '  <p id="secLoginStatus" class="mir-muted"></p>' +
      '  <div id="secLogins"></div>' +
      '</div>';

    el.addEventListener('click', onClick);
    q('#secHours').addEventListener('change', function () { refresh(); });
    q('#secLoginQ').addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); searchLogins(); }
    });
    ['#secLoginResult', '#secLoginSource', '#secLoginDays'].forEach(function (id) {
      q(id).addEventListener('change', function () { searchLogins(); });
    });
  }

  // ------------------------------------------------------------------ overview

  async function refresh() {
    if (!root) return;
    var token = ++seq;
    var hours = Number(q('#secHours').value) || 24;
    var j;
    try {
      j = await global.OpsUI.fetchJson('/api/admin/security/overview?hours=' + hours);
    } catch (e) {
      if (token === seq) q('#secStatus').textContent = 'Error: ' + e.message;
      return;
    }
    if (token !== seq) return;
    render(j);
  }

  function tile(k, v, cls) {
    return '<div class="admin-status-tile' + (cls ? ' ' + cls : '') + '"><span class="k">' + esc(k) + '</span><span class="v">' + esc(v) + '</span></div>';
  }

  function render(j) {
    var c = j.counts || {};
    var status = 'Updated ' + new Date().toLocaleTimeString() + ' · world ' + (j.world || '?');
    if (!j.accountsAvailable && j.accountsError) status += ' · account lists unavailable: ' + j.accountsError;
    q('#secStatus').textContent = status;

    setBox('secTiles',
      tile('Banned', num(c.banned)) +
      tile('Chat bans', num(c.chatBanned)) +
      tile('Admins', num(c.admins)) +
      tile('Panel sessions', num(c.sessions)) +
      tile('IP blocks', num(c.ipBlocks)) +
      tile('Failed logins (' + (j.hours >= 24 ? Math.round(j.hours / 24) + 'd' : j.hours + 'h') + ')', num(c.failures), c.failures ? 'sec-tile-warn' : ''));

    setBox('secBanned', table(j.banned, [
      { label: 'Account', html: function (x) { return accountLink(x.accountId, x.index) + (x.admin ? ' <span class="ops-pill ops-pill-warn">admin</span>' : ''); } },
      { label: 'Reason', html: function (x) { return esc(x.reason || ''); } },
      { label: 'Until', html: function (x) { return esc(untilText(x)); } },
      { label: '', cls: 'users-actions', html: function (x) { return btn('Lift ban', 'data-sact="unban" data-index="' + esc(x.index) + '" data-account="' + esc(x.accountId) + '"'); } }
    ], 'No banned accounts.'));

    setBox('secChat', table(j.chatBanned, [
      { label: 'Character', html: function (x) {
        return '<button type="button" class="users-link" data-sact="character" data-char="' + esc(x.characterIndex) + '">' + esc(x.name) + '</button>' +
          (x.online ? ' <span class="users-online">&#9679;</span>' : '');
      } },
      { label: 'Account', html: function (x) { return accountLink(x.accountId, x.accountIndex); } },
      { label: 'Until', html: function (x) { return esc(untilText(x)); } },
      { label: '', cls: 'users-actions', html: function (x) { return btn('Lift chat ban', 'data-sact="unchatban" data-char="' + esc(x.characterIndex) + '" data-name="' + esc(x.name) + '"'); } }
    ], 'No active chat bans.'));

    setBox('secAdmins', table(j.admins, [
      { label: 'Account', html: function (x) { return accountLink(x.accountId, x.index); } },
      { label: 'State', html: function (x) {
        var bits = [];
        if (x.banned) bits.push('<span class="ops-pill ops-pill-danger">banned</span>');
        if (x.requirePasswordChange) bits.push('<span class="ops-pill">PW change</span>');
        if (x.online) bits.push('<span class="users-online">&#9679; ' + esc(x.onlineCharacter || 'in game') + '</span>');
        return bits.join(' ') || '<span class="mir-muted">ok</span>';
      } },
      { label: 'Last login', html: function (x) { return '<span title="' + esc(when(x.lastDate)) + '">' + esc(x.lastDate ? ago(x.lastDate) : '—') + '</span>'; } },
      { label: 'Last IP', html: function (x) { return ipLink(x.lastIp); } },
      { label: 'Panel', html: function (x) { return esc(x.webSessions || 0); } }
    ], j.accountsAvailable ? 'No admin accounts.' : 'Account data unavailable.'));

    setBox('secSessions', table(j.sessions, [
      { label: 'Account', html: function (x) { return accountLink(x.accountId, x.accountIndex) + (x.current ? ' <span class="ops-pill ops-pill-ok">you</span>' : ''); } },
      { label: 'IP', html: function (x) { return ipLink(x.ip); } },
      { label: 'Signed in', html: function (x) { return '<span title="' + esc(when(x.created)) + '">' + esc(ago(x.created)) + '</span>'; } },
      { label: 'Last seen', html: function (x) { return '<span title="' + esc(x.userAgent || '') + '">' + esc(ago(x.lastSeen)) + '</span>'; } },
      { label: '', cls: 'users-actions', html: function (x) {
        return btn('Revoke', 'data-sact="revoke" data-id="' + esc(x.id) + '" data-account="' + esc(x.accountId) + '" data-current="' + (x.current ? 1 : 0) + '"', 'danger');
      } }
    ], 'No panel sessions.'));

    setBox('secBlocks', table(j.ipBlocks, [
      { label: 'IP', html: function (x) { return ipLink(x.ip); } },
      { label: 'Until', html: function (x) { return esc(when(x.until)); } },
      { label: 'Left', html: function (x) { return esc(global.OpsUI.duration(x.remainingSeconds)); } },
      { label: '', cls: 'users-actions', html: function (x) { return btn('Remove', 'data-sact="unblock" data-ip="' + esc(x.ip) + '"'); } }
    ], 'No blocked IPs.'));

    setBox('secFailures', table(j.failuresByIp, [
      { label: 'IP', html: function (x) { return ipLink(x.ip); } },
      { label: 'Failures', html: function (x) { return esc(x.count); } },
      { label: 'Accounts tried', html: function (x) { return (x.accounts || []).map(function (a) { return esc(a); }).join(', '); } },
      { label: 'Last', html: function (x) { return '<span title="' + esc(when(x.last)) + '">' + esc(ago(x.last)) + '</span>'; } },
      { label: '', cls: 'users-actions', html: function (x) { return x.ip && x.ip !== 'unknown' ? btn('Block…', 'data-sact="block" data-ip="' + esc(x.ip) + '"', 'danger') : ''; } }
    ], 'No failed logins in this window.'));

    setBox('secShared', table(j.sharedIps, [
      { label: 'IP', html: function (x) { return ipLink(x.ip); } },
      { label: 'Accounts', html: function (x) { return esc(x.count); } },
      { label: 'Who', html: function (x) {
        return (x.accounts || []).map(function (a) {
          return accountLink(a.accountId, a.index) +
            (a.banned ? ' <span class="ops-pill ops-pill-danger">banned</span>' : '') +
            (a.admin ? ' <span class="ops-pill ops-pill-warn">admin</span>' : '') +
            ' <span class="mir-muted">' + esc(a.via === 'both' ? 'last+creation' : a.via) + '</span>';
        }).join('<br>');
      } },
      { label: '', cls: 'users-actions', html: function (x) {
        return btn('Accounts', 'data-sact="ipaccounts" data-ip="' + esc(x.ip) + '"') + ' ' + btn('Block…', 'data-sact="block" data-ip="' + esc(x.ip) + '"', 'danger');
      } }
    ], j.accountsAvailable ? 'No IP is shared by two or more accounts.' : 'Account data unavailable.'));
  }

  // ------------------------------------------------------------------ login log

  async function searchLogins() {
    if (!root) return;
    var token = ++loginSeq;
    var term = q('#secLoginQ').value.trim();
    var result = q('#secLoginResult').value;
    var source = q('#secLoginSource').value;
    var days = q('#secLoginDays').value;
    if (global.AdminSections.current() === 'security') global.portalSetHashParams({ ip: term || null }, true);

    var st = q('#secLoginStatus');
    st.textContent = 'Searching…';
    var url = '/api/admin/security/logins?days=' + encodeURIComponent(days) + '&limit=500' +
      (term ? '&q=' + encodeURIComponent(term) : '') + (result ? '&result=' + encodeURIComponent(result) : '') +
      (source ? '&source=' + encodeURIComponent(source) : '');
    var j;
    try {
      j = await global.OpsUI.fetchJson(url);
    } catch (e) {
      if (token === loginSeq) st.textContent = 'Error: ' + e.message;
      return;
    }
    if (token !== loginSeq) return;

    lastLogins = j.entries || [];
    st.textContent = lastLogins.length + ' entr' + (lastLogins.length === 1 ? 'y' : 'ies') + (lastLogins.length >= (j.limit || 500) ? ' (limit reached — narrow the search)' : '');
    q('#secLogins').innerHTML = table(lastLogins, [
      { label: 'When', html: function (e) { return '<span title="' + esc(when(e.utc)) + '">' + esc(when(e.utc)) + '</span>'; } },
      { label: 'Result', html: function (e) { return resultPill(e.result); } },
      { label: 'Account', html: function (e) { return e.accountId ? accountLink(e.accountId, 0) : '<span class="mir-muted">—</span>'; } },
      { label: 'IP', html: function (e) { return ipLink(e.ip); } },
      { label: 'Via', html: function (e) { return e.source === 'web' ? '<span class="ops-pill ops-pill-warn">panel</span>' : 'game'; } }
    ], term ? 'No logins match.' : 'No logins in this window.');
  }

  function exportLogins() {
    if (!lastLogins.length) { global.portalToast('Search first: nothing to export.', 'error'); return; }
    var rows = lastLogins.map(function (e) { return [e.utc, e.result, e.accountId, e.ip, e.source]; });
    global.OpsUI.download('logins-' + new Date().toISOString().slice(0, 10) + '.csv',
      global.OpsUI.toCsv(['UTC', 'Result', 'Account', 'IP', 'Source'], rows), 'text/csv;charset=utf-8');
  }

  function focusIp(ip) {
    q('#secLoginQ').value = ip || '';
    var head = q('#secLoginsHead');
    if (head && head.scrollIntoView) head.scrollIntoView({ behavior: 'smooth', block: 'start' });
    return searchLogins();
  }

  // ------------------------------------------------------------------ actions

  async function addBlock(ip) {
    var v = await global.portalDialog({
      title: 'Block an IP address',
      message: 'New game connections from this address are refused until the block ends. The admin panel is not affected.',
      confirmLabel: 'Block',
      danger: true,
      fields: [
        { name: 'ip', label: 'IPv4 address', type: 'text', value: ip || '', required: true, placeholder: '203.0.113.7' },
        {
          name: 'minutes', label: 'Duration', type: 'presets', value: '60', options: [
            { value: '15', label: '15 min' }, { value: '60', label: '1 hour' }, { value: '1440', label: '1 day' },
            { value: '10080', label: '7 days' }, { value: '43200', label: '30 days' }, { value: '525600', label: '1 year' }
          ]
        },
        { name: 'disconnect', label: 'Also disconnect current connections from this IP', type: 'checkbox', value: true },
        { name: 'reason', label: 'Reason (audit trail)', type: 'text' }
      ],
      validate: function (x) {
        return /^\d{1,3}(\.\d{1,3}){3}$/.test(x.ip) ? null : 'Enter an IPv4 address such as 203.0.113.7.';
      }
    });
    if (!v) return;
    var j = await global.OpsUI.post('/api/admin/security/ipblocks/add', {
      ip: v.ip, minutes: Number(v.minutes), disconnect: !!v.disconnect, reason: v.reason || null
    });
    if (j.__ok) refresh();
  }

  async function onClick(e) {
    var t = e.target && e.target.closest ? e.target.closest('[data-sact]') : null;
    if (!t || !root.contains(t)) return;
    var act = t.getAttribute('data-sact');
    var ok, j;
    switch (act) {
      case 'refresh':
        global.OpsUI.withBusy(t, refresh);
        break;
      case 'logins':
        searchLogins();
        break;
      case 'logincsv':
        exportLogins();
        break;
      case 'ip':
        focusIp(t.getAttribute('data-ip'));
        break;
      case 'ipaccounts':
        global.portalOpenDeepLink('accounts', { q: t.getAttribute('data-ip'), mode: 'ip', exact: '1' });
        break;
      case 'account': {
        var p = { q: t.getAttribute('data-account'), mode: 'account', exact: '1' };
        var idx = Number(t.getAttribute('data-index'));
        if (idx > 0) p.account = String(idx);
        global.portalOpenDeepLink('accounts', p);
        break;
      }
      case 'character':
        global.portalOpenDeepLink('characters', { index: t.getAttribute('data-char') });
        break;
      case 'unban':
        ok = await global.portalConfirm('Lift the ban on ' + t.getAttribute('data-account') + '?', { title: 'Lift ban', confirmLabel: 'Lift ban', danger: false });
        if (!ok) return;
        j = await global.OpsUI.post('/api/admin/accounts/' + encodeURIComponent(t.getAttribute('data-index')) + '/unban', {});
        if (j.__ok) refresh();
        break;
      case 'unchatban':
        ok = await global.portalConfirm('Lift the chat ban of ' + t.getAttribute('data-name') + '?', { title: 'Lift chat ban', confirmLabel: 'Lift chat ban', danger: false });
        if (!ok) return;
        j = await global.OpsUI.post('/api/admin/gm/action', {
          action: 'unchatban', characterIndex: Number(t.getAttribute('data-char')), reason: 'Lifted from the Security page'
        });
        if (j.__ok) refresh();
        break;
      case 'revoke': {
        var self = t.getAttribute('data-current') === '1';
        ok = await global.portalConfirm((self ? 'This is YOUR session: revoking it signs you out. ' : '') +
          'Revoke the panel session of ' + t.getAttribute('data-account') + '?', { title: 'Revoke session', confirmLabel: 'Revoke' });
        if (!ok) return;
        j = await global.OpsUI.post('/api/admin/security/sessions/revoke', { id: t.getAttribute('data-id') });
        if (j.__ok && self) { global.location.href = '/admin/login.html'; return; }
        if (j.__ok) refresh();
        break;
      }
      case 'block':
        addBlock(t.getAttribute('data-ip'));
        break;
      case 'unblock':
        ok = await global.portalConfirm('Remove the block on ' + t.getAttribute('data-ip') + '?', { title: 'Remove IP block', confirmLabel: 'Remove', danger: false });
        if (!ok) return;
        j = await global.OpsUI.post('/api/admin/security/ipblocks/remove', { ip: t.getAttribute('data-ip') });
        if (j.__ok) refresh();
        break;
      case 'clearblocks':
        ok = await global.portalConfirm('Remove every game IP block (including the automatic MaxIP blocks)?', { title: 'Clear IP blocks', confirmLabel: 'Clear all' });
        if (!ok) return;
        j = await global.OpsUI.post('/api/admin/security/ipblocks/clear', {});
        if (j.__ok) refresh();
        break;
    }
  }

  // ------------------------------------------------------------------ registration

  global.AdminSections.register('security', {
    load: function (el, params) {
      build(el);
      if (!stopPoll) stopPoll = global.AdminSections.poll('security', refresh, POLL_MS);
      if (!(params && params.ip)) searchLogins();   // the deep link searches for its IP instead
    },
    show: function () {
      return refresh();
    },
    hide: function () { /* the poller only ticks while the section is visible */ }
  });

  global.portalRegisterDeepLink('security', function (p) {
    if (!root || !p || p.ip == null) return;
    if (q('#secLoginQ').value.trim() === String(p.ip).trim()) return;
    focusIp(String(p.ip));
  });

})(window);
