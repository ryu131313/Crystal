/**
 * Dashboard section (#section-dashboard) — MINIMAL smoke-test version. The dashboard owner replaces this file.
 * Shows one status card from GET /api/admin/status (world, players, connections, monsters, uptime, cycle delay),
 * refreshed every 5 s while the section and the browser tab are visible.
 */
(function (global) {
  'use strict';

  var POLL_MS = 5000;
  var stopPoll = null;
  var grid = null;
  var stamp = null;

  function esc(s) {
    return global.OpsUI ? OpsUI.esc(s) : String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
  }

  function first(obj, names) {
    for (var i = 0; i < names.length; i++) {
      var v = obj ? obj[names[i]] : undefined;
      if (v !== undefined && v !== null) return v;
    }
    return null;
  }

  function count(v) {
    if (v == null) return '—';
    if (Array.isArray(v)) return String(v.length);
    if (typeof v === 'object') return v.count != null ? String(v.count) : (v.total != null ? String(v.total) : '—');
    return String(v);
  }

  function uptimeText(st) {
    // GET /api/admin/status sends uptimeMs (world uptime; 0 while the world is not running).
    if (typeof st.uptimeMs === 'number') {
      if (st.uptimeMs <= 0) return '—';
      return global.OpsUI ? OpsUI.duration(st.uptimeMs / 1000) : Math.round(st.uptimeMs / 1000) + 's';
    }
    var secs = first(st, ['uptimeSeconds', 'worldUptimeSeconds', 'uptimeSec']);
    if (secs == null && typeof st.uptime === 'number') secs = st.uptime;
    if (secs != null) return global.OpsUI ? OpsUI.duration(secs) : secs + 's';
    if (typeof st.uptime === 'string' && st.uptime) return st.uptime;
    var started = first(st, ['startedUtc', 'started', 'startTime', 'worldStarted']);
    if (started && global.OpsUI) {
      var d = OpsUI.toDate(started);
      if (d) return OpsUI.duration((Date.now() - d.getTime()) / 1000);
    }
    return '—';
  }

  function cycleText(st) {
    var v = first(st, ['cycleDelayMs', 'cycleDelay', 'conDelay', 'loopDelay']);
    if (v == null) return '—';
    return typeof v === 'number' ? v + ' ms' : String(v);
  }

  function tile(k, v) {
    return '<div class="admin-status-tile"><span class="k">' + esc(k) + '</span><span class="v">' + esc(v) + '</span></div>';
  }

  function render(st) {
    if (!grid) return;
    if (!st) {
      grid.innerHTML = tile('World', 'unreachable');
      return;
    }
    grid.innerHTML =
      tile('World', st.world || st.worldState || 'unknown') +
      tile('Players', count(first(st, ['players', 'playerCount', 'online']))) +
      tile('Connections', count(first(st, ['connections', 'connectionCount']))) +
      tile('Monsters', count(first(st, ['monsters', 'monsterCount']))) +
      tile('Uptime', uptimeText(st)) +
      tile('Cycle delay', cycleText(st));
    if (stamp) stamp.textContent = 'Updated ' + new Date().toLocaleTimeString();
  }

  async function refresh() {
    try {
      var j = await OpsUI.fetchJson('/api/admin/status');
      render(OpsUI.unwrap(j));
    } catch (e) {
      render(null);
      if (stamp) stamp.textContent = String((e && e.message) || e);
    }
  }

  global.AdminSections.register('dashboard', {
    load: function (el) {
      el.innerHTML =
        '<div class="mir-card">' +
        '  <div class="toolbar"><h2>Server status</h2>' +
        '    <div><span class="mir-muted" data-role="stamp"></span> <button type="button" class="mir-btn" data-role="refresh">Refresh</button></div>' +
        '  </div>' +
        '  <div class="admin-status-grid" data-role="grid"></div>' +
        '</div>';
      grid = el.querySelector('[data-role="grid"]');
      stamp = el.querySelector('[data-role="stamp"]');
      el.querySelector('[data-role="refresh"]').addEventListener('click', function (e) {
        OpsUI.withBusy(e.currentTarget, refresh);
      });
    },
    show: function () {
      if (!stopPoll) stopPoll = global.AdminSections.poll('dashboard', refresh, POLL_MS);
      return refresh();
    },
    hide: function () {
      if (stopPoll) { stopPoll(); stopPoll = null; }
    }
  });
})(window);
