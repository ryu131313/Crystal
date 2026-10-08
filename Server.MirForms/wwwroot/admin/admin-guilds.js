/**
 * Guilds section (#section-guilds) — B5.
 *
 *   GET  /api/admin/guilds?q=&sort=&dir=&page=&pageSize=   paged, server-sorted list (readable while the world is stopped)
 *   GET  /api/admin/guilds/{index}                          detail: ranks/members, notice, storage, buffs, wars, conquests
 *   POST /api/admin/guilds/{index}/notice {lines[]}         live (world running) — every mutation is audited server-side
 *   POST /api/admin/guilds/{index}/members/remove {name, force?, reason?}
 *   POST /api/admin/guilds/{index}/message {text}
 *   POST /api/admin/guilds/{index}/funds {gold?, sparePoints?, mode, reason?}
 *
 * UI: sortable paged table + the shared inspect drawer (widened) with tabs Members / Storage / Notice (editor with a dirty
 * guard) / Buffs / Wars, "Send guild message…" and "Adjust funds…". Deep links: #section=guilds&q=<text>&index=<guildIndex>.
 */
(function (global) {
  'use strict';

  var SECTION = 'guilds';
  var POLL_MS = 20000;
  var PAGE_SIZE = 50;
  var MAX_NOTICE_LINES = 200;
  var MAX_MESSAGE = 200;

  var state = { page: 1, pages: 1, sort: 'index', dir: 'asc', q: '', seq: 0, signature: '' };
  var root = null;
  var stopPoll = null;
  var searchTimer = null;

  // drawer state
  var drawer = { index: null, data: null, tab: 'members', noticeDirty: false, seq: 0 };

  function esc(v) { return OpsUI.esc(v); }
  function num(v) { return (Number(v) || 0).toLocaleString('en-US'); }
  function q(sel) { return root ? root.querySelector(sel) : null; }

  function chip(name, opts) {
    if (!name || name === '—') return '<span class="mir-muted">—</span>';
    return OpsUI.playerLink(name, opts || {});
  }

  function dateText(v) {
    var d = OpsUI.toDate(v);
    if (!d) return '<span class="mir-muted">—</span>';
    return '<span title="' + esc(d.toLocaleString()) + '">' + esc(OpsUI.ago(d)) + '</span>';
  }

  // ------------------------------------------------------------------ list

  function render(el) {
    el.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar">' +
      '    <h2>Guilds</h2>' +
      '    <div class="gld-tools">' +
      '      <input type="search" class="fd-input" data-section-search data-role="search" placeholder="Search name, leader or #index" aria-label="Search guilds" />' +
      '      <button type="button" class="mir-btn" data-role="refresh">Refresh</button>' +
      '      <button type="button" class="mir-btn" data-role="csv" title="Export the current page as CSV">CSV</button>' +
      '    </div>' +
      '  </div>' +
      '  <p class="gld-world-note mir-muted hidden" data-role="world-note">The world is not running: showing the last loaded guild data. Editing needs a running world.</p>' +
      '  <div class="ops-table-scroll">' +
      '    <table class="user-table users-table gld-table" data-role="table">' +
      '      <thead><tr>' +
      '        <th data-sort="index">#</th>' +
      '        <th data-sort="name">Name</th>' +
      '        <th data-sort="leader">Leader</th>' +
      '        <th data-sort="members">Members</th>' +
      '        <th data-sort="online">Online</th>' +
      '        <th data-sort="level">Level</th>' +
      '        <th data-sort="gold">Gold</th>' +
      '        <th data-sort="sparepoints">Points</th>' +
      '        <th data-sort="gt">GT</th>' +
      '        <th data-sort="wars">Wars</th>' +
      '        <th>Conquest</th>' +
      '      </tr></thead>' +
      '      <tbody data-role="tbody"><tr><td colspan="11" class="mir-muted">Loading…</td></tr></tbody>' +
      '    </table>' +
      '  </div>' +
      '  <div class="gld-pager">' +
      '    <button type="button" class="mir-btn" data-role="prev">&lsaquo; Prev</button>' +
      '    <span class="mir-muted" data-role="status"></span>' +
      '    <button type="button" class="mir-btn" data-role="next">Next &rsaquo;</button>' +
      '  </div>' +
      '</div>';

    q('[data-role="refresh"]').addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, function () { return loadList(true); }); });
    q('[data-role="csv"]').addEventListener('click', exportCsv);
    q('[data-role="prev"]').addEventListener('click', function () { if (state.page > 1) { state.page--; loadList(true); } });
    q('[data-role="next"]').addEventListener('click', function () { if (state.page < state.pages) { state.page++; loadList(true); } });

    var search = q('[data-role="search"]');
    search.addEventListener('input', function () {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(function () {
        state.q = search.value.trim();
        state.page = 1;
        global.portalSetHashParams({ q: state.q || null }, true);
        loadList(true);
      }, 300);
    });
    search.addEventListener('keydown', function (e) {
      if (e.key !== 'Enter') return;
      e.preventDefault();
      clearTimeout(searchTimer);
      state.q = search.value.trim();
      state.page = 1;
      loadList(true);
    });

    Array.prototype.forEach.call(root.querySelectorAll('th[data-sort]'), function (th) {
      th.addEventListener('click', function () {
        var key = th.getAttribute('data-sort');
        if (state.sort === key) state.dir = state.dir === 'asc' ? 'desc' : 'asc';
        else { state.sort = key; state.dir = (key === 'name' || key === 'leader' || key === 'index') ? 'asc' : 'desc'; }
        state.page = 1;
        loadList(true);
      });
    });

    q('[data-role="tbody"]').addEventListener('click', function (e) {
      var t = e.target && e.target.closest ? e.target.closest('[data-guild]') : null;
      if (!t) return;
      openGuild(Number(t.getAttribute('data-guild')));
    });
  }

  function syncSort() {
    Array.prototype.forEach.call(root.querySelectorAll('th[data-sort]'), function (th) {
      var on = th.getAttribute('data-sort') === state.sort;
      th.setAttribute('aria-sort', on ? (state.dir === 'desc' ? 'descending' : 'ascending') : 'none');
    });
  }

  var lastRows = [];

  async function loadList(force) {
    if (!root) return;
    var token = ++state.seq;
    var url = '/api/admin/guilds?page=' + state.page + '&pageSize=' + PAGE_SIZE +
      '&sort=' + encodeURIComponent(state.sort) + '&dir=' + state.dir +
      (state.q ? '&q=' + encodeURIComponent(state.q) : '');
    var st = q('[data-role="status"]');
    var j;
    try {
      j = OpsUI.unwrap(await OpsUI.fetchJson(url));
    } catch (e) {
      if (token !== state.seq) return;
      if (st) st.textContent = 'Error: ' + (e && e.message || e);
      if (force) portalToast('Could not load guilds: ' + (e && e.message || e), 'error');
      return;
    }
    if (token !== state.seq || !j) return;

    var rows = j.rows || [];
    state.page = Number(j.page) || 1;
    state.pages = Number(j.pages) || 1;

    var note = q('[data-role="world-note"]');
    if (note) note.classList.toggle('hidden', j.world === 'running');

    // Reconcile: only rebuild the body when something changed (keeps hover / focus stable while polling).
    var sig = JSON.stringify(rows) + '|' + j.total;
    if (force || sig !== state.signature) {
      state.signature = sig;
      lastRows = rows;
      renderRows(rows);
    }

    q('[data-role="prev"]').disabled = state.page <= 1;
    q('[data-role="next"]').disabled = state.page >= state.pages;
    if (st) st.textContent = num(j.total) + ' guild(s) · page ' + state.page + ' / ' + state.pages;
    syncSort();
  }

  function renderRows(rows) {
    var tbody = q('[data-role="tbody"]');
    if (!tbody) return;
    if (!rows.length) {
      tbody.innerHTML = '<tr><td colspan="11" class="mir-muted">' + (state.q ? 'No guild matches “' + esc(state.q) + '”.' : 'No guilds.') + '</td></tr>';
      return;
    }
    tbody.innerHTML = rows.map(function (g) {
      var cap = Number(g.memberCap) || 0;
      var gt = g.gt ? '<span title="Rent until ' + esc(OpsUI.fmtDate(g.gt)) + '">GT #' + esc(g.gtIndex) + '</span>' : '<span class="mir-muted">—</span>';
      return '<tr data-row="' + esc(g.index) + '">' +
        '<td>' + esc(g.index) + '</td>' +
        '<td><button type="button" class="users-link" data-guild="' + esc(g.index) + '">' + esc(g.name) + '</button></td>' +
        '<td>' + chip(g.leader) + '</td>' +
        '<td>' + num(g.members) + (cap ? ' <span class="mir-muted">/ ' + num(cap) + '</span>' : '') + '</td>' +
        '<td>' + (Number(g.onlineMembers) > 0 ? '<span class="users-online">● ' + num(g.onlineMembers) + '</span>' : '<span class="mir-muted">0</span>') + '</td>' +
        '<td>' + esc(g.level) + '</td>' +
        '<td>' + num(g.gold) + '</td>' +
        '<td>' + num(g.sparePoints) + '</td>' +
        '<td>' + gt + '</td>' +
        '<td>' + (Number(g.warCount) > 0 ? '<span class="ops-pill ops-pill-danger">' + num(g.warCount) + '</span>' : '<span class="mir-muted">0</span>') + '</td>' +
        '<td>' + (g.conquest ? esc(g.conquest) : '<span class="mir-muted">—</span>') + '</td>' +
        '</tr>';
    }).join('');
  }

  function exportCsv() {
    if (!lastRows.length) { portalToast('Nothing to export.', 'warn'); return; }
    var header = ['Index', 'Name', 'Leader', 'Members', 'MemberCap', 'Online', 'Level', 'Experience', 'Gold', 'SparePoints', 'GT rent until', 'Wars', 'Conquest'];
    var rows = lastRows.map(function (g) {
      return [g.index, g.name, g.leader, g.members, g.memberCap, g.onlineMembers, g.level, g.experience, g.gold, g.sparePoints, g.gt || '', g.warCount, g.conquest || ''];
    });
    OpsUI.download('guilds-page' + state.page + '.csv', OpsUI.toCsv(header, rows), 'text/csv');
  }

  // ------------------------------------------------------------------ detail drawer

  async function confirmDiscardNotice() {
    if (!drawer.noticeDirty) return true;
    var ok = await portalConfirm('The guild notice has unsaved changes. Discard them?', { title: 'Unsaved notice', confirmLabel: 'Discard', danger: true });
    if (ok) drawer.noticeDirty = false;
    return ok;
  }

  function onDrawerClosed() {
    var d = document.getElementById('inspectDrawer');
    if (d) d.classList.remove('gld-wide');
    if (drawer.noticeDirty) portalToast('Unsaved guild notice changes were discarded.', 'warn');
    drawer.index = null;
    drawer.data = null;
    drawer.noticeDirty = false;
    var hp = global.portalHashParams();
    if (hp.section === SECTION && hp.index != null) global.portalSetHashParams({ index: null }, true);
  }

  async function openGuild(index, tab) {
    if (!(index >= 0) && index !== 0) return;
    if (drawer.index !== null && drawer.index !== index && !(await confirmDiscardNotice())) return;
    if (global.AdminInspect.confirmReplace && !(await global.AdminInspect.confirmReplace('guilds'))) return;

    var sameGuild = drawer.index === index && global.AdminInspect.isOpen();
    var body;
    if (sameGuild) {
      body = global.AdminInspect.body();
      if (tab) drawer.tab = tab;
    } else {
      // open() runs the previous onClose first (which resets the drawer state), so set the state afterwards.
      drawer.noticeDirty = false;
      body = global.AdminInspect.open({ title: 'Guild #' + index, text: 'Loading…', onClose: onDrawerClosed, owner: 'guilds',
        isDirty: function () { return !!drawer.noticeDirty; } });
      drawer.tab = tab || 'members';
    }
    drawer.index = index;
    var d = document.getElementById('inspectDrawer');
    if (d) d.classList.add('gld-wide');
    global.portalSetHashParams({ section: SECTION, index: index }, true);

    var token = ++drawer.seq;
    var g;
    try {
      g = OpsUI.unwrap(await OpsUI.fetchJson('/api/admin/guilds/' + encodeURIComponent(index)));
    } catch (e) {
      if (token !== drawer.seq) return;
      if (body) body.textContent = 'Could not load the guild: ' + (e && e.message || e);
      return;
    }
    if (token !== drawer.seq || drawer.index !== index) return;
    drawer.data = g;
    var title = document.getElementById('inspectDrawerTitle');
    if (title) title.textContent = g.name + ' (#' + g.index + ')';
    renderDrawer();
  }

  function cell(label, valueHtml) {
    return '<div><span class="ins-label">' + esc(label) + '</span>' + valueHtml + '</div>';
  }

  function renderDrawer() {
    var g = drawer.data;
    var body = global.AdminInspect.body();
    if (!g || !body) return;
    var live = !!g.live;
    var dis = live ? '' : ' disabled title="The world is not running"';

    var expPct = g.maxExperience > 0 ? Math.min(100, Math.round(100 * g.experience / g.maxExperience)) : null;
    var html =
      '<div class="inspect-grid gld-summary">' +
      cell('Leader', chip(g.leader)) +
      cell('Level', esc(g.level) + (expPct != null ? ' <span class="mir-muted">(' + num(g.experience) + ' / ' + num(g.maxExperience) + ' xp, ' + expPct + '%)</span>' : ' <span class="mir-muted">(' + num(g.experience) + ' xp)</span>')) +
      cell('Members', num(g.members) + ' / ' + num(g.memberCap) + ' · <span class="users-online">' + num(g.onlineMembers) + ' online</span>') +
      cell('Gold', num(g.gold)) +
      cell('Spare points', num(g.sparePoints)) +
      cell('Guild territory', g.gt ? 'GT #' + esc(g.gtIndex) + ' until ' + esc(OpsUI.fmtDate(g.gt)) : '<span class="mir-muted">none</span>') +
      cell('Flag', '<span class="gld-flag" style="background:' + esc(/^#[0-9A-F]{6}$/i.test(g.flagColour || '') ? g.flagColour : '#ffffff') + '"></span> image ' + esc(g.flagImage)) +
      cell('Conquest', g.conquest ? esc(g.conquest) : '<span class="mir-muted">—</span>') +
      '</div>' +
      (live ? '' : '<p class="mir-muted gld-world-note">World not running — read-only view of the saved guild.</p>') +
      '<div class="users-drawer-actions">' +
      '  <button type="button" class="mir-btn primary" data-act="message"' + dis + '>Send guild message…</button>' +
      '  <button type="button" class="mir-btn" data-act="funds"' + dis + '>Adjust funds…</button>' +
      '  <button type="button" class="mir-btn" data-act="reload">Reload</button>' +
      '</div>' +
      '<div class="gld-tabs" role="tablist">' +
      tabButton('members', 'Members (' + countMembers(g) + ')') +
      tabButton('storage', 'Storage (' + (g.storage || []).length + ')') +
      tabButton('notice', 'Notice (' + (g.notice || []).length + ')') +
      tabButton('buffs', 'Buffs (' + (g.buffs || []).filter(function (b) { return b.owned; }).length + ')') +
      tabButton('wars', 'Wars (' + (g.wars || []).length + ')') +
      '</div>' +
      '<div class="gld-tab-body" data-role="tab-body"></div>';

    body.innerHTML = html;

    body.querySelector('[data-act="message"]').addEventListener('click', sendMessage);
    body.querySelector('[data-act="funds"]').addEventListener('click', adjustFunds);
    body.querySelector('[data-act="reload"]').addEventListener('click', async function () {
      if (!(await confirmDiscardNotice())) return;
      openGuild(drawer.index);
    });
    Array.prototype.forEach.call(body.querySelectorAll('[data-tab]'), function (b) {
      b.addEventListener('click', async function () {
        var t = b.getAttribute('data-tab');
        if (t === drawer.tab) return;
        if (drawer.tab === 'notice' && !(await confirmDiscardNotice())) return;
        drawer.tab = t;
        renderDrawer();
      });
    });

    renderTab(body.querySelector('[data-role="tab-body"]'));
  }

  function tabButton(id, label) {
    var on = drawer.tab === id;
    return '<button type="button" role="tab" aria-selected="' + on + '" class="mir-btn' + (on ? ' primary' : '') + '" data-tab="' + id + '">' + esc(label) + '</button>';
  }

  function countMembers(g) {
    return (g.ranks || []).reduce(function (n, r) { return n + (r.members || []).length; }, 0);
  }

  function renderTab(host) {
    if (!host) return;
    switch (drawer.tab) {
      case 'storage': renderStorage(host); break;
      case 'notice': renderNotice(host); break;
      case 'buffs': renderBuffs(host); break;
      case 'wars': renderWars(host); break;
      default: renderMembers(host);
    }
  }

  function renderMembers(host) {
    var g = drawer.data;
    var live = !!g.live;
    var ranks = g.ranks || [];
    if (!ranks.length) { host.innerHTML = '<p class="mir-muted">No ranks or members.</p>'; return; }

    var leaderRankCount = ranks.length && ranks[0].members ? ranks[0].members.length : 0;
    var html = '<table class="user-table gld-members"><thead><tr><th>Member</th><th>Rank</th><th>Level / class</th><th>Last login</th><th></th></tr></thead><tbody>';
    ranks.forEach(function (r) {
      (r.members || []).forEach(function (m) {
        var opts = { online: !!m.online, characterIndex: m.characterIndex, account: m.accountId, accountIndex: m.accountIndex };
        html += '<tr>' +
          '<td>' + chip(m.name, opts) + (m.deleted ? ' <span class="ops-pill ops-pill-warn">deleted</span>' : '') + '</td>' +
          '<td>' + esc(r.name || ('Rank ' + r.index)) + (r.index === 0 ? ' <span class="ops-pill ops-pill-ok">leader</span>' : '') + '</td>' +
          '<td>' + (m.level != null ? esc(m.level) + ' <span class="mir-muted">' + esc(m.className || '') + '</span>' : '<span class="mir-muted">—</span>') + '</td>' +
          '<td>' + (m.online ? '<span class="users-online">● online</span>' : dateText(m.lastLogin)) + '</td>' +
          '<td class="users-actions"><button type="button" class="mir-btn danger ops-row-btn" data-remove="' + esc(m.name) + '" data-rank="' + esc(r.index) + '"' +
          (live ? '' : ' disabled title="The world is not running"') + '>Remove…</button></td>' +
          '</tr>';
      });
    });
    html += '</tbody></table>';
    host.innerHTML = html;

    Array.prototype.forEach.call(host.querySelectorAll('[data-remove]'), function (b) {
      b.addEventListener('click', function () {
        var name = b.getAttribute('data-remove');
        var rankIndex = Number(b.getAttribute('data-rank'));
        removeMember(name, rankIndex === 0 && leaderRankCount <= 1, countMembers(g) <= 1);
      });
    });
  }

  function renderStorage(host) {
    var items = drawer.data.storage || [];
    if (!items.length) { host.innerHTML = '<p class="mir-muted">Guild storage is empty (' + num(drawer.data.storageSlots) + ' slots).</p>'; return; }
    host.innerHTML =
      '<p class="mir-muted">' + items.length + ' of ' + num(drawer.data.storageSlots) + ' slots used. Read-only.</p>' +
      '<table class="user-table gld-storage"><thead><tr><th>Slot</th><th>Item</th><th>Count</th><th>Dura</th><th>Stored by</th><th>UID</th></tr></thead><tbody>' +
      items.map(function (it) {
        var dura = it.maxDura ? Math.round(it.currentDura / 1000) + ' / ' + Math.round(it.maxDura / 1000) : '<span class="mir-muted">—</span>';
        var by = it.storedBy === 'Server' || it.storedBy === 'Unknown' ? '<span class="mir-muted">' + esc(it.storedBy) + '</span>' : chip(it.storedBy, { characterIndex: it.storedById });
        return '<tr><td>' + esc(it.slot) + '</td><td>' + esc(it.name) + ' <span class="mir-muted">#' + esc(it.itemIndex) + '</span></td>' +
          '<td>' + num(it.count) + '</td><td>' + dura + '</td><td>' + by + '</td><td class="mono">' + esc(it.uniqueId) + '</td></tr>';
      }).join('') + '</tbody></table>';
  }

  function renderNotice(host) {
    var g = drawer.data;
    var live = !!g.live;
    var text = (g.notice || []).join('\n');
    host.innerHTML =
      '<label class="ins-label" for="gldNoticeText">Notice (max ' + MAX_NOTICE_LINES + ' lines)</label>' +
      '<textarea id="gldNoticeText" class="fd-input ops-textarea gld-notice" rows="14" spellcheck="false"' + (live ? '' : ' readonly') + '></textarea>' +
      '<div class="gld-notice-bar">' +
      '  <span class="mir-muted" data-role="notice-meta"></span>' +
      '  <span>' +
      '    <button type="button" class="mir-btn" data-act="notice-reset">Reset</button>' +
      '    <button type="button" class="mir-btn primary" data-act="notice-save"' + (live ? '' : ' disabled title="The world is not running"') + '>Save notice</button>' +
      '  </span>' +
      '</div>';
    var ta = host.querySelector('textarea');
    ta.value = text;
    var meta = host.querySelector('[data-role="notice-meta"]');
    function update() {
      var lines = ta.value === '' ? 0 : ta.value.split('\n').length;
      drawer.noticeDirty = ta.value !== text;
      meta.textContent = lines + ' / ' + MAX_NOTICE_LINES + ' lines' + (drawer.noticeDirty ? ' · unsaved changes' : '');
      meta.classList.toggle('gld-dirty', drawer.noticeDirty);
    }
    ta.addEventListener('input', update);
    update();
    host.querySelector('[data-act="notice-reset"]').addEventListener('click', function () { ta.value = text; update(); });
    host.querySelector('[data-act="notice-save"]').addEventListener('click', function (e) {
      var lines = ta.value.replace(/\r\n/g, '\n').split('\n');
      if (lines.length > MAX_NOTICE_LINES) { portalToast('A guild notice can have at most ' + MAX_NOTICE_LINES + ' lines.', 'error'); return; }
      OpsUI.withBusy(e.currentTarget, async function () {
        var j = await OpsUI.post('/api/admin/guilds/' + drawer.index + '/notice', { lines: lines });
        if (j.__ok) {
          drawer.noticeDirty = false;
          await openGuild(drawer.index, 'notice');
        }
      }, 'Saving…');
    });
  }

  function statsText(stats) {
    if (!stats || !stats.length) return '<span class="mir-muted">—</span>';
    return stats.map(function (s) {
      var pct = /Percent$/.test(s.stat) ? '%' : '';
      return esc(s.stat.replace(/Percent$/, '')) + ' ' + (s.value > 0 ? '+' : '') + esc(s.value) + pct;
    }).join(', ');
  }

  function renderBuffs(host) {
    var buffs = drawer.data.buffs || [];
    if (!buffs.length) { host.innerHTML = '<p class="mir-muted">No guild buffs are configured (Systems ▸ Guilds).</p>'; return; }
    host.innerHTML =
      '<table class="user-table gld-buffs"><thead><tr><th>ID</th><th>Buff</th><th>Status</th><th>Remaining</th><th>Req.</th><th>Cost</th><th>Effect</th></tr></thead><tbody>' +
      buffs.map(function (b) {
        var status = !b.owned ? '<span class="mir-muted">not owned</span>'
          : b.active ? '<span class="ops-pill ops-pill-ok">active</span>' : '<span class="ops-pill ops-pill-warn">inactive</span>';
        var remaining = !b.owned || !b.active ? '<span class="mir-muted">—</span>' : (b.infinite ? 'permanent' : esc(b.remainingMinutes) + ' min');
        return '<tr' + (b.owned ? '' : ' class="gld-muted-row"') + '>' +
          '<td>' + esc(b.id) + '</td>' +
          '<td>' + esc(b.name) + (b.known ? '' : ' <span class="ops-pill ops-pill-danger">not in catalogue</span>') + '</td>' +
          '<td>' + status + '</td>' +
          '<td>' + remaining + '</td>' +
          '<td>Lv ' + esc(b.levelRequirement) + ' · ' + esc(b.pointsRequirement) + ' pt</td>' +
          '<td>' + (b.activationCost ? num(b.activationCost) + ' gold' : '—') + (b.timeLimit ? ' <span class="mir-muted">/ ' + esc(b.timeLimit) + ' min</span>' : '') + '</td>' +
          '<td>' + statsText(b.stats) + '</td>' +
          '</tr>';
      }).join('') + '</tbody></table>';
  }

  function renderWars(host) {
    var g = drawer.data;
    var wars = g.wars || [];
    var conquests = g.conquests || [];
    var html = '<h4>Guild wars</h4>';
    if (!g.live) html += '<p class="mir-muted">Wars are only known while the world is running.</p>';
    else if (!wars.length) html += '<p class="mir-muted">Not at war.</p>';
    else {
      html += '<table class="user-table"><thead><tr><th>Against</th><th>Time left</th></tr></thead><tbody>' +
        wars.map(function (w) {
          return '<tr><td><button type="button" class="users-link" data-guild-link="' + esc(w.guildIndex) + '">' + esc(w.guild) + '</button></td>' +
            '<td>' + esc(w.remainingMinutes) + ' min</td></tr>';
        }).join('') + '</tbody></table>';
    }
    html += '<h4>Conquests</h4>';
    if (!conquests.length) html += '<p class="mir-muted">No conquest owned or attacked.</p>';
    else {
      html += '<table class="user-table"><thead><tr><th>Conquest</th><th>Role</th><th>Gold storage</th><th>NPC rate</th><th>War</th></tr></thead><tbody>' +
        conquests.map(function (c) {
          return '<tr><td>' + esc(c.name) + ' <span class="mir-muted">#' + esc(c.index) + '</span></td><td>' + esc(c.role) + '</td>' +
            '<td>' + num(c.goldStorage) + '</td><td>' + esc(c.npcRate) + '%</td>' +
            '<td>' + (c.warOn ? '<span class="ops-pill ops-pill-danger">at war</span>' : '<span class="mir-muted">—</span>') + '</td></tr>';
        }).join('') + '</tbody></table>';
    }
    host.innerHTML = html;
    Array.prototype.forEach.call(host.querySelectorAll('[data-guild-link]'), function (b) {
      b.addEventListener('click', function () { openGuild(Number(b.getAttribute('data-guild-link'))); });
    });
  }

  // ------------------------------------------------------------------ actions

  async function removeMember(name, soleLeader, lastMember) {
    var g = drawer.data;
    if (!g) return;
    var warning = lastMember
      ? ' They are the LAST member: the guild "' + g.name + '" will be disbanded.'
      : soleLeader ? ' They are the only leader: the guild will be left without a leader.' : '';
    var v = await portalDialog({
      title: 'Remove ' + name + ' from ' + g.name,
      message: 'Removes the member from the guild (they are notified if online).' + warning,
      confirmLabel: lastMember ? 'Remove & disband' : 'Remove member',
      danger: true,
      fields: [{ name: 'reason', label: 'Reason (audit log)', type: 'text', placeholder: 'Inactive / requested by leader' }]
    });
    if (!v) return;
    var j = await OpsUI.post('/api/admin/guilds/' + drawer.index + '/members/remove', { name: name, force: !!(soleLeader || lastMember), reason: v.reason || '' });
    if (!j.__ok) return;
    loadList(true);
    if (j.data && j.data.disbanded) { global.AdminInspect.close(); return; }
    openGuild(drawer.index, 'members');
  }

  async function sendMessage() {
    var g = drawer.data;
    if (!g) return;
    var v = await portalDialog({
      title: 'Message ' + g.name,
      message: 'Sent to every online member in guild chat as "SYSTEM: <text>".',
      confirmLabel: 'Send',
      danger: false,
      fields: [{ name: 'text', label: 'Message', type: 'text', required: true, placeholder: 'Guild war starts in 10 minutes' }],
      validate: function (vals) { return (vals.text || '').length > MAX_MESSAGE ? 'At most ' + MAX_MESSAGE + ' characters.' : null; }
    });
    if (!v) return;
    await OpsUI.post('/api/admin/guilds/' + drawer.index + '/message', { text: v.text });
  }

  async function adjustFunds() {
    var g = drawer.data;
    if (!g) return;
    var v = await portalDialog({
      title: 'Adjust funds — ' + g.name,
      message: 'Current: ' + num(g.gold) + ' gold, ' + num(g.sparePoints) + ' spare points. Leave a field empty to keep it.',
      confirmLabel: 'Apply',
      danger: false,
      fields: [
        { name: 'mode', label: 'Mode', type: 'select', value: 'set', options: [{ value: 'set', label: 'Set to' }, { value: 'add', label: 'Add (negative to remove)' }] },
        { name: 'gold', label: 'Gold', type: 'number', placeholder: String(g.gold) },
        { name: 'sparePoints', label: 'Spare points (0–255)', type: 'number', placeholder: String(g.sparePoints) },
        { name: 'reason', label: 'Reason (audit log)', type: 'text', placeholder: 'Event reward' }
      ],
      validate: function (vals) {
        if (vals.gold == null && vals.sparePoints == null) return 'Enter gold and/or spare points.';
        if (vals.gold != null && !Number.isInteger(vals.gold)) return 'Gold must be a whole number.';
        if (vals.sparePoints != null && !Number.isInteger(vals.sparePoints)) return 'Spare points must be a whole number.';
        if (vals.mode === 'set' && vals.gold != null && (vals.gold < 0 || vals.gold > 4294967295)) return 'Gold must be between 0 and 4,294,967,295.';
        if (vals.mode === 'set' && vals.sparePoints != null && (vals.sparePoints < 0 || vals.sparePoints > 255)) return 'Spare points must be between 0 and 255.';
        return null;
      }
    });
    if (!v) return;
    var body = { mode: v.mode, reason: v.reason || '' };
    if (v.gold != null) body.gold = v.gold;
    if (v.sparePoints != null) body.sparePoints = v.sparePoints;
    var j = await OpsUI.post('/api/admin/guilds/' + drawer.index + '/funds', body);
    if (j.__ok) {
      loadList(true);
      openGuild(drawer.index);
    }
  }

  // ------------------------------------------------------------------ registration

  global.portalRegisterDeepLink(SECTION, function (params) {
    if (!root || !params) return;
    var search = q('[data-role="search"]');
    if (params.q != null && params.q !== state.q) {
      state.q = String(params.q);
      state.page = 1;
      if (search) search.value = state.q;
      loadList(true);
    }
    var idx = params.index != null ? Number(params.index) : NaN;
    if (!isNaN(idx) && idx >= 0 && (drawer.index !== idx || !global.AdminInspect.isOpen())) openGuild(idx);
  });

  global.AdminSections.register(SECTION, {
    load: function (el) {
      root = el;
      render(el);
      var hp = global.portalHashParams();
      if (hp.section === SECTION && hp.q) {
        state.q = String(hp.q);
        q('[data-role="search"]').value = state.q;
      }
    },
    show: function () {
      if (!stopPoll) stopPoll = global.AdminSections.poll(SECTION, function () { return loadList(false); }, POLL_MS);
      return loadList(true);
    },
    hide: function () {
      if (stopPoll) { stopPoll(); stopPoll = null; }
      if (drawer.index !== null && global.AdminInspect.isOpen()) global.AdminInspect.close();
    }
  });

  /** Other sections may open a guild: GuildsSection.open(index). */
  global.GuildsSection = { open: function (index) { global.portalOpenDeepLink(SECTION, { index: index }); } };
})(window);
