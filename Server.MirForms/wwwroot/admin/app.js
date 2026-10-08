/**
 * MirCraft Admin bootstrap (loads LAST): auth gate (GET /api/auth/me), logout, topbar status (world-state pill + pending-reboot
 * badge from GET /api/admin/status every 10 s while the browser tab is visible), then AdminSections.start().
 */
(function (global) {
  'use strict';

  var STATUS_MS = 10000;
  var statusTimer = null;
  var statusInFlight = null;
  var lastStatus = null;
  var statusListeners = [];

  var WORLD_LABELS = { running: 'World running', stopped: 'World stopped', starting: 'World starting', stopping: 'World stopping' };

  // Pending reboot entries: accepts an array of {area, description, admin, utc} or a number.
  function pendingList(st) {
    var p = st && (st.pendingReboot != null ? st.pendingReboot : st.pending);
    if (Array.isArray(p)) return p;
    if (p && Array.isArray(p.items)) return p.items;
    return [];
  }
  function pendingCount(st) {
    var p = st && (st.pendingReboot != null ? st.pendingReboot : st.pending);
    if (typeof p === 'number') return p;
    if (p === true) return 1;
    if (p && typeof p.count === 'number') return p.count;
    return pendingList(st).length;
  }

  function renderStatus(st) {
    var pill = document.getElementById('worldPill');
    var badge = document.getElementById('pendingRebootBadge');
    if (pill) {
      var world = st ? String(st.world || st.worldState || 'unknown') : 'offline';
      pill.className = 'world-pill world-' + world.replace(/[^a-z]/gi, '').toLowerCase();
      pill.textContent = st ? (WORLD_LABELS[world] || ('World ' + world)) : 'Server unreachable';
      var tip = [];
      if (st && st.players != null) tip.push('Players: ' + st.players);
      if (st && st.connections != null) tip.push('Connections: ' + st.connections);
      pill.title = tip.join('\n');
    }
    if (badge) {
      var n = pendingCount(st);
      if (n > 0) {
        badge.classList.remove('hidden');
        badge.textContent = n + ' pending reboot';
        var lines = pendingList(st).slice(0, 12).map(function (e) {
          if (typeof e === 'string') return e;
          return (e.area ? '[' + e.area + '] ' : '') + (e.description || '') + (e.admin ? ' — ' + e.admin : '');
        });
        badge.title = 'Game data changed since the world last started; restart the world to apply.' + (lines.length ? '\n\n' + lines.join('\n') : '');
      } else {
        badge.classList.add('hidden');
        badge.textContent = '';
        badge.title = '';
      }
    }
  }

  // Concurrent callers (topbar timer, dashboard/server polls, post-action refreshes) share the in-flight request, so nobody
  // ever receives the not-yet-loaded status as if the server were unreachable.
  function pollStatus() {
    if (statusInFlight) return statusInFlight;
    statusInFlight = (async function () {
      try {
        var r = await portalApi('/api/admin/status');
        var j = await r.json().catch(function () { return null; });
        var st = null;
        if (r.ok && j) st = (j.data && typeof j.data === 'object' && j.ok !== undefined) ? j.data : j;
        lastStatus = st;
        renderStatus(st);
        statusListeners.slice().forEach(function (fn) { try { fn(st); } catch (e) { /* listener */ } });
        return st;
      } catch (e) {
        lastStatus = null;
        renderStatus(null);
        return null;
      } finally {
        statusInFlight = null;
      }
    })();
    return statusInFlight;
  }

  function startStatusPolling() {
    if (statusTimer) return;
    statusTimer = setInterval(function () {
      if (document.visibilityState && document.visibilityState !== 'visible') return;
      pollStatus();
    }, STATUS_MS);
    document.addEventListener('visibilitychange', function () {
      if (document.visibilityState === 'visible') pollStatus();
    });
    pollStatus();
  }

  /** Topbar status shared with sections: AdminStatus.last(), AdminStatus.refresh() -> Promise<status|null>, onChange(fn). */
  global.AdminStatus = {
    last: function () { return lastStatus; },
    refresh: pollStatus,
    onChange: function (fn) { if (typeof fn === 'function') statusListeners.push(fn); },
    offChange: function (fn) { var i = statusListeners.indexOf(fn); if (i >= 0) statusListeners.splice(i, 1); }
  };

  document.addEventListener('DOMContentLoaded', async function () {
    var j = {};
    try {
      var r = await fetch('/api/auth/me', { credentials: 'include' });
      j = await r.json().catch(function () { return {}; });
      if (j && j.data && j.authenticated === undefined) j = j.data;
    } catch (e) {
      j = {};
    }
    if (!j || !j.authenticated || !j.admin) {
      // Keep the deep link (#section=...&q=...) through the sign-in; login.html comes back to it.
      global.location = global.portalLoginUrl
        ? global.portalLoginUrl(global.location.hash, !!(j && j.authenticated))
        : '/admin/login.html' + (j && j.authenticated ? '?denied=1' : '');
      return;
    }

    var u = document.getElementById('adminUser');
    if (u) u.textContent = j.accountId || '';
    if (j.csrf && global.portalSetCsrfToken) global.portalSetCsrfToken(j.csrf);
    document.title = 'MirCraft Admin';

    var out = document.getElementById('btnAdminLogout');
    if (out) out.onclick = async function () {
      out.disabled = true;
      try { await portalApi('/api/auth/logout', { method: 'POST' }); } catch (e) { /* signed out anyway */ }
      global.location = '/admin/login.html';
    };

    var badge = document.getElementById('pendingRebootBadge');
    if (badge) badge.addEventListener('click', function () {
      if (document.getElementById('section-server')) global.portalShowSection('server');
    });

    if (j.world) renderStatus({ world: j.world });
    startStatusPolling();

    if (global.AdminSections) global.AdminSections.start();
  });
})(window);
