/**
 * Dashboard (#section-dashboard): live tiles (GET /api/admin/status + /api/admin/host + /api/admin/server/state, every 5 s),
 * Chart.js trend charts (GET /api/admin/status/history?minutes=60|360|1440, every 60 s), the activity feed
 * (GET /api/admin/events?after=<seq>, every 5 s) and quick actions (Save now, Reboot, open Broadcast).
 * Everything polls only while the section is visible and the browser tab is in the foreground (AdminSections.poll).
 */
(function (global) {
  'use strict';

  var STATUS_MS = 5000;
  var HISTORY_MS = 60000;
  var EVENTS_MS = 5000;
  var MAX_FEED_ROWS = 300;
  var RANGES = [60, 360, 1440];
  var LS_RANGE = 'mircraft_dashRangeMin';
  var LS_FEED = 'mircraft_dashFeedKind';

  var EVENT_META = {
    login: { label: 'Login', cls: 'ev-login' },
    logout: { label: 'Logout', cls: 'ev-logout' },
    'login-failed': { label: 'Login failed', cls: 'ev-login-failed' },
    world: { label: 'World', cls: 'ev-world' },
    admin: { label: 'Admin', cls: 'ev-admin' }
  };

  var root = null;          // the section element
  var ui = {};              // cached element refs
  var stops = [];           // AdminSections.poll stop functions
  var charts = null;        // { players, cycle, memory }
  var rangeMin = 60;
  var feedKind = '';
  var lastEventSeq = 0;
  var lastOkMs = 0;
  var pollFailed = false;
  var lastStatus = null;
  var lastHost = null;
  var lastState = null;
  var busy = false;         // a quick action is running

  function esc(s) { return OpsUI.esc(s); }

  function lsGet(key) { try { return global.localStorage.getItem(key); } catch (e) { return null; } }
  function lsSet(key, v) { try { global.localStorage.setItem(key, v); } catch (e) { /* private mode */ } }

  function cssVar(name, fallback) {
    try {
      var v = getComputedStyle(document.documentElement).getPropertyValue(name);
      return (v && v.trim()) || fallback;
    } catch (e) {
      return fallback;
    }
  }

  // '#rrggbb' + alpha -> rgba(); anything else is returned unchanged.
  function withAlpha(color, alpha) {
    var m = /^#([0-9a-f]{6})$/i.exec(String(color || '').trim());
    if (!m) return color;
    var n = parseInt(m[1], 16);
    return 'rgba(' + ((n >> 16) & 255) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + alpha + ')';
  }

  function two(n) { return ('0' + n).slice(-2); }
  function hhmm(d) { return two(d.getHours()) + ':' + two(d.getMinutes()); }
  function fmtNum(n) { return n == null || isNaN(n) ? '—' : Number(n).toLocaleString(); }
  function fmtMb(mb) {
    mb = Number(mb) || 0;
    if (mb >= 1048576) return (mb / 1048576).toFixed(2) + ' TB';
    if (mb >= 1024) return (mb / 1024).toFixed(1) + ' GB';
    return mb + ' MB';
  }

  /* ---------------------------------------------------------------- layout */

  function build(el) {
    el.innerHTML =
      '<div class="dash-toolbar">' +
      '  <div class="dash-toolbar-group">' +
      '    <div class="dash-live stale" data-role="live"><span class="dash-live-dot"></span><span data-role="liveText">Connecting…</span></div>' +
      '  </div>' +
      '  <div class="dash-toolbar-group dash-range" role="group" aria-label="Chart time range" data-role="range">' +
      '    <span class="mir-muted">Charts</span>' +
      '    <button type="button" class="dash-range-btn" data-range="60">1h</button>' +
      '    <button type="button" class="dash-range-btn" data-range="360">6h</button>' +
      '    <button type="button" class="dash-range-btn" data-range="1440">24h</button>' +
      '  </div>' +
      '  <div class="dash-toolbar-group dash-actions">' +
      '    <button type="button" class="mir-btn" data-act="save" title="Run the periodic save (accounts, database, guilds, goods, conquests) on the next game tick">Save now</button>' +
      '    <button type="button" class="mir-btn danger" data-act="reboot" title="Stop and start the game world (players are disconnected)">Reboot</button>' +
      '    <button type="button" class="mir-btn" data-act="broadcast" title="Open Broadcast">Broadcast</button>' +
      '  </div>' +
      '</div>' +
      '<div class="dash-countdown hidden" data-role="countdown">' +
      '  <span class="dash-countdown-text" data-role="countdownText"></span>' +
      '  <button type="button" class="mir-btn" data-act="open-server">Server Control</button>' +
      '</div>' +
      '<div class="metrics-grid dash-cards" data-role="tiles"></div>' +
      '<div class="dash-charts">' +
      '  <div class="mir-card dash-chart-card"><h3>Players &amp; Connections</h3><div class="dash-canvas"><canvas data-chart="players"></canvas><p class="dash-chart-empty mir-muted" data-empty="players">Collecting samples…</p></div></div>' +
      '  <div class="mir-card dash-chart-card"><h3>Cycle Delay</h3><div class="dash-canvas"><canvas data-chart="cycle"></canvas><p class="dash-chart-empty mir-muted" data-empty="cycle">Collecting samples…</p></div></div>' +
      '  <div class="mir-card dash-chart-card"><h3>Memory &amp; CPU</h3><div class="dash-canvas"><canvas data-chart="memory"></canvas><p class="dash-chart-empty mir-muted" data-empty="memory">Collecting samples…</p></div></div>' +
      '</div>' +
      '<div class="mir-card">' +
      '  <div class="dash-feed-head">' +
      '    <h3>Activity</h3>' +
      '    <label class="mir-muted">Show <select class="fd-input" style="width:auto" data-role="feedKind"></select></label>' +
      '  </div>' +
      '  <p class="mir-muted">Logins, logouts, failed logins, world start/stop and admin actions (newest first).</p>' +
      '  <div class="activity-feed" data-role="feed"><p class="feed-empty mir-muted">No activity yet.</p></div>' +
      '</div>';

    ui.live = el.querySelector('[data-role="live"]');
    ui.liveText = el.querySelector('[data-role="liveText"]');
    ui.range = el.querySelector('[data-role="range"]');
    ui.tiles = el.querySelector('[data-role="tiles"]');
    ui.feed = el.querySelector('[data-role="feed"]');
    ui.feedKind = el.querySelector('[data-role="feedKind"]');
    ui.countdown = el.querySelector('[data-role="countdown"]');
    ui.countdownText = el.querySelector('[data-role="countdownText"]');
    ui.save = el.querySelector('[data-act="save"]');
    ui.reboot = el.querySelector('[data-act="reboot"]');

    var saved = Number(lsGet(LS_RANGE));
    if (RANGES.indexOf(saved) >= 0) rangeMin = saved;
    markRange();
    ui.range.addEventListener('click', function (e) {
      var b = e.target && e.target.closest ? e.target.closest('[data-range]') : null;
      if (!b) return;
      var r = Number(b.getAttribute('data-range'));
      if (RANGES.indexOf(r) < 0 || r === rangeMin) return;
      rangeMin = r;
      lsSet(LS_RANGE, String(r));
      markRange();
      pollHistory();
    });

    var opts = '<option value="">All events</option>';
    Object.keys(EVENT_META).forEach(function (k) {
      opts += '<option value="' + esc(k) + '">' + esc(EVENT_META[k].label) + '</option>';
    });
    ui.feedKind.innerHTML = opts;
    feedKind = lsGet(LS_FEED) || '';
    if (!EVENT_META[feedKind]) feedKind = '';
    ui.feedKind.value = feedKind;
    ui.feedKind.addEventListener('change', function () {
      feedKind = ui.feedKind.value;
      lsSet(LS_FEED, feedKind);
      applyFeedFilter();
    });

    el.querySelector('[data-act="save"]').addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, saveNow); });
    el.querySelector('[data-act="reboot"]').addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, reboot); });
    el.querySelector('[data-act="broadcast"]').addEventListener('click', function () { global.portalShowSection('broadcast'); });
    el.querySelector('[data-act="open-server"]').addEventListener('click', function () { global.portalShowSection('server'); });
  }

  function markRange() {
    if (!ui.range) return;
    ui.range.querySelectorAll('[data-range]').forEach(function (b) {
      b.classList.toggle('active', Number(b.getAttribute('data-range')) === rangeMin);
    });
  }

  /* ---------------------------------------------------------------- tiles (reconciled in place, never rebuilt) */

  function makeTile() {
    var el = document.createElement('div');
    el.className = 'metric-card';
    var s = {};
    ['label', 'value', 'sub'].forEach(function (k) {
      var n = document.createElement('div');
      n.className = 'metric-' + k;
      el.appendChild(n);
      s[k] = n;
    });
    var foot = document.createElement('div');
    foot.className = 'metric-foot';
    var bar = document.createElement('div');
    bar.className = 'metric-bar';
    var fill = document.createElement('div');
    fill.className = 'metric-bar-fill';
    bar.appendChild(fill);
    foot.appendChild(bar);
    el.appendChild(foot);
    s.bar = bar;
    s.fill = fill;
    el._s = s;
    el._href = null;
    el.addEventListener('click', function () { if (el._href) global.portalShowSection(el._href); });
    el.addEventListener('keydown', function (e) {
      if (el._href && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); global.portalShowSection(el._href); }
    });
    return el;
  }

  function updateTile(el, t) {
    var s = el._s;
    var cls = 'metric-card' + (t.cls ? ' ' + t.cls : '') + (t.href ? ' metric-link' : '');
    if (el.className !== cls) el.className = cls;
    if (s.label.textContent !== t.label) s.label.textContent = t.label;
    var v = t.value == null ? '—' : String(t.value);
    if (s.value.textContent !== v) s.value.textContent = v;
    s.sub.textContent = t.sub || '';
    s.sub.style.display = t.sub ? '' : 'none';
    var hasBar = t.bar != null && isFinite(t.bar);
    s.bar.style.display = hasBar ? '' : 'none';
    if (hasBar) s.fill.style.width = Math.max(0, Math.min(1, t.bar)) * 100 + '%';
    el.title = t.title || '';
    el._href = t.href || null;
    if (t.href) { el.setAttribute('role', 'button'); el.tabIndex = 0; }
    else { el.removeAttribute('role'); el.removeAttribute('tabindex'); }
  }

  function renderTiles(specs) {
    var grid = ui.tiles;
    if (!grid) return;
    if (!grid._tiles) { grid.innerHTML = ''; grid._tiles = {}; }
    var prev = null;
    var seen = {};
    specs.forEach(function (t) {
      seen[t.key] = true;
      var el = grid._tiles[t.key];
      if (!el) { el = makeTile(); grid._tiles[t.key] = el; }
      updateTile(el, t);
      var want = prev ? prev.nextSibling : grid.firstChild;
      if (el !== want) grid.insertBefore(el, want || null);
      prev = el;
    });
    Object.keys(grid._tiles).forEach(function (k) {
      if (seen[k]) return;
      var el = grid._tiles[k];
      if (el.parentNode) el.parentNode.removeChild(el);
      delete grid._tiles[k];
    });
  }

  function level(value, warnAt, badAt) {
    value = Number(value);
    if (!isFinite(value)) return '';
    if (value >= badAt) return 'metric-bad';
    if (value >= warnAt) return 'metric-warn';
    return '';
  }

  var WORLD_TEXT = { running: 'Running', stopped: 'Stopped', starting: 'Starting', stopping: 'Stopping' };

  function pendingCount(st) {
    var p = st && st.pendingReboot;
    return Array.isArray(p) ? p.length : (typeof p === 'number' ? p : 0);
  }

  var LIVEDB_TEXT = { applied: 'Live', partial: 'Partly live', none: 'Saved', 'world-stopped': 'Saved only', 'rolled-back': 'Rolled back', timeout: 'Timed out' };

  /** LiveDb tile (st.livedb from /api/admin/status): last batch and its live outcome, sweeper queue, diverged parents. */
  function liveDbTile(ldb) {
    var diverged = Number(ldb.divergedCount) || 0;
    var failed = ldb.lastLiveOutcome === 'rolled-back' || ldb.lastLiveOutcome === 'timeout';
    var value = !ldb.enabled ? 'Off' : (ldb.lastBatchId ? (LIVEDB_TEXT[ldb.lastLiveOutcome] || ldb.lastLiveOutcome || '—') : 'Idle');
    var sub = [];
    if (ldb.lastBatchUtc) sub.push('last ' + OpsUI.ago(ldb.lastBatchUtc) + (ldb.lastSaved === false ? ', not saved' : ''));
    if (ldb.sweeperActive || ldb.sweeperPending) sub.push('sweeper ' + (Number(ldb.sweeperPending) || 0) + ' queued');
    if (diverged) sub.push(diverged + ' diverged');
    return {
      key: 'livedb', label: 'LiveDb', value: value, href: 'livedb',
      cls: !ldb.enabled ? '' : (failed ? 'metric-bad' : (diverged || ldb.lastLiveOutcome === 'partial' ? 'metric-warn' : (ldb.lastBatchId ? 'metric-ok' : ''))),
      sub: sub.length ? sub.join(' · ') : (ldb.enabled ? (Number(ldb.batches) || 0) + ' batches' : '[LiveDb] Enabled=False'),
      title: 'Live game-data edits: ' + (Number(ldb.batches) || 0) + ' batches, ' + (Number(ldb.undos) || 0) + ' undos since start' +
        (ldb.lastBatchId ? '; last ' + ldb.lastBatchId : '') + (diverged ? '; diverged: ' + (ldb.diverged || []).join(', ') : '') + '. Open LiveDb History'
    };
  }

  function render() {
    var st = lastStatus;
    var h = lastHost;
    var state = lastState;
    if (!st) {
      renderTiles([{ key: 'world', label: 'World', value: 'Unreachable', cls: 'metric-bad', href: 'server' }]);
      return;
    }
    var world = String(st.world || 'unknown');
    var running = world === 'running';
    var crashed = !!(state && state.crashed);
    var worldCls = running ? 'metric-ok' : (world === 'stopped' ? 'metric-bad' : 'metric-warn');
    var pending = pendingCount(st);
    var procUp = st.processUptimeMs ? 'process up ' + OpsUI.duration(st.processUptimeMs / 1000) : null;

    var specs = [
      { key: 'world', label: 'World', value: crashed ? 'Crashed' : (WORLD_TEXT[world] || world), cls: crashed ? 'metric-bad' : worldCls, href: 'server',
        sub: st.systemDataStale ? 'game data edited' : null, title: 'Open Server Control' },
      { key: 'players', label: 'Players', value: fmtNum(st.players), href: 'players-online', title: 'Open Players Online' },
      { key: 'connections', label: 'Connections', value: fmtNum(st.connections), sub: st.blockedIps ? st.blockedIps + ' IPs blocked' : null },
      { key: 'monsters', label: 'Monsters', value: fmtNum(st.monsters) },
      { key: 'cycle', label: 'Cycle delay', value: running ? fmtNum(st.cycleDelayMs) + ' ms' : '—', cls: running ? level(st.cycleDelayMs, 100, 250) : '',
        sub: running && st.mobThreadDelays && st.mobThreadDelays.length ? 'mob threads ' + st.mobThreadDelays.join(' / ') + ' ms' : null,
        title: 'Game loop time per cycle (Envir.LastRunTime)' },
      { key: 'uptime', label: 'Uptime', value: running && st.uptimeMs > 0 ? OpsUI.duration(st.uptimeMs / 1000) : '—', sub: procUp,
        title: 'Game world uptime (Stop / Start / Reboot reset it)' },
      { key: 'memory', label: 'Memory', value: h ? fmtMb(h.workingSetMb) : '—', sub: h ? 'GC heap ' + fmtMb(h.gcHeapMb) : null,
        cls: h ? level(h.workingSetMb, 3072, 4096) : '', title: h ? 'Working set ' + fmtMb(h.workingSetMb) + ', private ' + fmtMb(h.privateMb) + ', peak ' + fmtMb(h.peakWorkingSetMb) : '' },
      { key: 'cpu', label: 'CPU', value: h ? Number(h.cpuPercent || 0).toFixed(1) + ' %' : '—', bar: h ? (Number(h.cpuPercent) || 0) / 100 : null,
        cls: h ? level(h.cpuPercent, 70, 90) : '', sub: h ? h.processorCount + ' cores' : null, title: 'Server.exe CPU across all cores' },
      { key: 'pending', label: 'Pending reboot', value: String(pending), cls: pending > 0 ? 'metric-warn' : 'metric-ok', href: 'server',
        sub: pending > 0 ? 'restart to apply' : 'nothing pending', title: 'Saved game-data changes that apply on the next world start' }
    ];
    if (st.livedb) specs.push(liveDbTile(st.livedb));
    if (h && h.diskFreeMb) {
      specs.push({ key: 'disk', label: 'Disk free', value: fmtMb(h.diskFreeMb), cls: h.diskFreeMb < 2048 ? 'metric-bad' : (h.diskFreeMb < 8192 ? 'metric-warn' : ''),
        sub: h.diskRoot ? 'drive ' + h.diskRoot : null, bar: h.diskTotalMb ? 1 - h.diskFreeMb / h.diskTotalMb : null });
    }
    renderTiles(specs);
    renderCountdown();
    updateActions();
  }

  function renderCountdown() {
    var c = lastState && lastState.countdown;
    if (!ui.countdown) return;
    if (!c || !c.active) { ui.countdown.classList.add('hidden'); return; }
    ui.countdown.classList.remove('hidden');
    var remaining = Math.max(0, Number(c.remainingSec) || 0);
    var mins = Math.floor(remaining / 60), secs = remaining % 60;
    ui.countdownText.textContent = 'Restart countdown: the world reboots in ' + mins + 'm ' + two(secs) + 's' +
      (c.admin ? ' (armed by ' + c.admin + ')' : '') + (c.message ? ' — "' + c.message + '"' : '');
  }

  function updateActions() {
    var running = !!(lastStatus && lastStatus.world === 'running');
    var crashed = !!(lastState && lastState.crashed);
    if (ui.save && !ui.save.getAttribute('aria-busy')) ui.save.disabled = busy || !running;
    if (ui.reboot && !ui.reboot.getAttribute('aria-busy')) ui.reboot.disabled = busy || !(running || crashed);
  }

  function updateLive() {
    if (!ui.live) return;
    var age = lastOkMs ? Math.floor((Date.now() - lastOkMs) / 1000) : null;
    var stale = pollFailed || age == null || age > 15;
    ui.live.classList.toggle('stale', stale);
    if (ui.tiles) ui.tiles.classList.toggle('dash-stale', stale && age != null);
    if (age == null) ui.liveText.textContent = pollFailed ? 'Offline — server not responding' : 'Connecting…';
    else if (stale) ui.liveText.textContent = 'Stale — last update ' + age + 's ago';
    else ui.liveText.textContent = 'Live · updated ' + age + 's ago';
  }

  /* ---------------------------------------------------------------- charts */

  function baseOptions() {
    var grid = cssVar('--border', 'rgba(197,160,89,0.15)');
    var text = cssVar('--muted', '#8a7f70');
    return {
      responsive: true,
      maintainAspectRatio: false,
      animation: false,
      interaction: { mode: 'index', intersect: false },
      plugins: {
        legend: { labels: { color: text, boxWidth: 12 } },
        tooltip: {
          callbacks: {
            title: function (items) {
              var c = items[0] && items[0].chart;
              var t = c && c._times ? c._times[items[0].dataIndex] : null;
              return t ? t.toLocaleString() : (items[0] ? items[0].label : '');
            }
          }
        }
      },
      scales: {
        x: { ticks: { color: text, maxTicksLimit: 8, autoSkip: true }, grid: { color: grid } },
        y: { ticks: { color: text }, grid: { color: grid }, beginAtZero: true }
      }
    };
  }

  function makeCharts() {
    if (charts || typeof global.Chart === 'undefined' || !root) return;
    var accent = cssVar('--accent', '#ff7a1f');
    var success = cssVar('--success', '#5fd38a');
    var warn = cssVar('--warn', '#e8b04a');
    var danger = cssVar('--danger', '#e0524d');
    var text = cssVar('--muted', '#8a7f70');

    function line(key, datasets, tweak) {
      var canvas = root.querySelector('canvas[data-chart="' + key + '"]');
      if (!canvas) return null;
      var o = baseOptions();
      if (tweak) tweak(o);
      return new global.Chart(canvas.getContext('2d'), { type: 'line', data: { labels: [], datasets: datasets }, options: o });
    }

    charts = {
      players: line('players', [
        { label: 'Players', data: [], borderColor: accent, backgroundColor: withAlpha(accent, 0.18), fill: true, pointRadius: 0, stepped: true, borderWidth: 2 },
        { label: 'Connections', data: [], borderColor: success, pointRadius: 0, stepped: true, borderWidth: 1.5 }
      ], function (o) { o.scales.y.ticks.precision = 0; o.scales.y.suggestedMax = 2; }),
      cycle: line('cycle', [
        { label: 'Cycle delay (ms)', data: [], borderColor: warn, backgroundColor: withAlpha(warn, 0.12), fill: true, pointRadius: 0, tension: 0.25, borderWidth: 2 }
      ], function (o) { o.scales.y.suggestedMax = 20; }),
      memory: line('memory', [
        { label: 'Working set (MB)', data: [], borderColor: accent, pointRadius: 0, tension: 0.25, borderWidth: 2, yAxisID: 'y' },
        { label: 'CPU (%)', data: [], borderColor: danger, pointRadius: 0, tension: 0.25, borderWidth: 1.5, yAxisID: 'y1' }
      ], function (o) {
        o.scales.y1 = { position: 'right', min: 0, max: 100, ticks: { color: text }, grid: { drawOnChartArea: false } };
      })
    };
  }

  function setEmpty(key, empty) {
    var n = root && root.querySelector('[data-empty="' + key + '"]');
    if (n) n.style.display = empty ? '' : 'none';
  }

  function drawHistory(samples) {
    makeCharts();
    var empty = !samples || samples.length < 2;
    ['players', 'cycle', 'memory'].forEach(function (k) { setEmpty(k, empty); });
    if (!charts) return;
    samples = samples || [];
    var times = samples.map(function (s) { return OpsUI.toDate(s.utc) || new Date(); });
    var multiDay = times.length > 1 && times[0].toDateString() !== times[times.length - 1].toDateString();
    var labels = times.map(function (d) { return multiDay ? (d.getMonth() + 1) + '/' + d.getDate() + ' ' + hhmm(d) : hhmm(d); });

    function set(chart, arrays) {
      if (!chart) return;
      chart._times = times;
      chart.data.labels = labels;
      arrays.forEach(function (arr, i) { chart.data.datasets[i].data = arr; });
      chart.update('none');
    }

    set(charts.players, [
      samples.map(function (s) { return s.players; }),
      samples.map(function (s) { return s.connections; })
    ]);
    set(charts.cycle, [samples.map(function (s) { return s.cycleDelayMs; })]);
    set(charts.memory, [
      samples.map(function (s) { return s.workingSetMb; }),
      samples.map(function (s) { return s.cpuPercent; })
    ]);
  }

  /* ---------------------------------------------------------------- activity feed */

  function rowVisible(row) { return !feedKind || row._kind === feedKind; }

  function applyFeedFilter() {
    if (!ui.feed) return;
    var shown = 0;
    for (var i = 0; i < ui.feed.children.length; i++) {
      var row = ui.feed.children[i];
      if (row._kind === undefined) continue;
      var vis = rowVisible(row);
      row.style.display = vis ? '' : 'none';
      if (vis) shown++;
    }
    var empty = ui.feed.querySelector('.feed-empty');
    if (!shown) {
      if (!empty) {
        empty = document.createElement('p');
        empty.className = 'feed-empty mir-muted';
        ui.feed.appendChild(empty);
      }
      empty.textContent = feedKind ? 'No ' + (EVENT_META[feedKind] ? EVENT_META[feedKind].label.toLowerCase() : feedKind) + ' events yet.' : 'No activity yet.';
    } else if (empty) {
      empty.parentNode.removeChild(empty);
    }
  }

  function renderEvents(events) {
    if (!ui.feed || !events || !events.length) return;
    events = events.filter(function (e) { return e && e.seq > lastEventSeq; });
    if (!events.length) return;
    events.sort(function (a, b) { return a.seq - b.seq; });
    events.forEach(function (ev) {
      var meta = EVENT_META[ev.kind] || { label: ev.kind || 'event', cls: '' };
      var row = document.createElement('div');
      row.className = 'feed-row';
      row._kind = ev.kind;

      var badge = document.createElement('span');
      badge.className = 'feed-badge ' + meta.cls;
      badge.textContent = meta.label;

      var text = document.createElement('span');
      text.className = 'feed-text';
      if ((ev.kind === 'login' || ev.kind === 'logout') && ev.character) {
        var rest = String(ev.text || '');
        if (rest.indexOf(ev.character) === 0) rest = rest.substring(ev.character.length);
        text.innerHTML = OpsUI.playerLink(ev.character, { account: ev.account, online: ev.kind === 'login' }) + esc(rest);
      } else {
        text.textContent = ev.text || '';
      }

      var when = document.createElement('span');
      when.className = 'feed-time';
      when.setAttribute('data-utc', ev.utc || '');
      when.textContent = OpsUI.ago(ev.utc);
      when.title = OpsUI.fmtDate(ev.utc);

      row.appendChild(badge);
      row.appendChild(text);
      row.appendChild(when);
      if (!rowVisible(row)) row.style.display = 'none';
      ui.feed.insertBefore(row, ui.feed.firstChild);
      lastEventSeq = Math.max(lastEventSeq, ev.seq);
    });
    while (ui.feed.children.length > MAX_FEED_ROWS) ui.feed.removeChild(ui.feed.lastChild);
    applyFeedFilter();
  }

  function refreshFeedTimes() {
    if (!ui.feed) return;
    ui.feed.querySelectorAll('.feed-time[data-utc]').forEach(function (el) {
      var u = el.getAttribute('data-utc');
      if (u) el.textContent = OpsUI.ago(u);
    });
  }

  /* ---------------------------------------------------------------- polling */

  async function getJson(url) {
    try { return await OpsUI.fetchJson(url); } catch (e) { return null; }
  }

  async function pollStatus() {
    var results = await Promise.all([
      global.AdminStatus ? global.AdminStatus.refresh() : getJson('/api/admin/status'),
      getJson('/api/admin/host'),
      getJson('/api/admin/server/state')
    ]);
    var st = results[0];
    if (st && st.data && st.ok !== undefined) st = st.data;
    if (st) {
      lastStatus = st;
      lastOkMs = Date.now();
      pollFailed = false;
    } else {
      pollFailed = true;
    }
    if (results[1]) lastHost = results[1];
    if (results[2]) lastState = results[2];
    render();
    updateLive();
    refreshFeedTimes();
  }

  async function pollHistory() {
    var j = await getJson('/api/admin/status/history?minutes=' + rangeMin);
    if (j) drawHistory(j.samples || []);
  }

  var resetSeq = false;

  async function pollEvents() {
    var j = await getJson('/api/admin/events?after=' + lastEventSeq + '&max=200');
    if (!j) return;
    // The server restarted (its sequence went backwards): start the feed over.
    if (typeof j.last === 'number' && j.last < lastEventSeq) {
      if (resetSeq) return;
      resetSeq = true;
      lastEventSeq = 0;
      ui.feed.innerHTML = '';
      var again = await getJson('/api/admin/events?after=0&max=200');
      resetSeq = false;
      if (again) renderEvents(again.events || []);
      applyFeedFilter();
      return;
    }
    renderEvents(j.events || []);
  }

  /* ---------------------------------------------------------------- quick actions */

  async function saveNow() {
    busy = true;
    updateActions();
    try {
      await OpsUI.post('/api/admin/server/save', {});
    } finally {
      busy = false;
      updateActions();
    }
  }

  async function reboot() {
    var v = await OpsUI.actionDialog({
      title: 'Reboot the world?',
      message: 'Everyone is disconnected without warning; the world stops, saves and starts again. Use Server Control › Restart countdown to warn players first.',
      confirmText: 'Reboot now',
      fields: [{ key: 'reason', label: 'Reason (audit log)', type: 'text', placeholder: 'optional' }]
    });
    if (!v) return;
    busy = true;
    updateActions();
    try {
      await OpsUI.post('/api/admin/server/reboot', { reason: v.reason || '' });
    } finally {
      busy = false;
      updateActions();
      setTimeout(pollStatus, 1500);
    }
  }

  /* ---------------------------------------------------------------- section */

  global.AdminSections.register('dashboard', {
    load: function (el) {
      root = el;
      build(el);
      render();
    },
    show: function () {
      makeCharts();
      if (!stops.length) {
        stops.push(global.AdminSections.poll('dashboard', pollStatus, STATUS_MS));
        stops.push(global.AdminSections.poll('dashboard', pollHistory, HISTORY_MS));
        stops.push(global.AdminSections.poll('dashboard', pollEvents, EVENTS_MS));
        stops.push(global.AdminSections.poll('dashboard', function () { updateLive(); renderCountdownTick(); }, 1000));
      }
      return Promise.all([pollStatus(), pollHistory(), pollEvents()]);
    },
    hide: function () {
      stops.forEach(function (stop) { stop(); });
      stops = [];
    }
  });

  // Between status polls the countdown keeps ticking locally.
  function renderCountdownTick() {
    var c = lastState && lastState.countdown;
    if (!c || !c.active) return;
    if (c.remainingSec > 0) c.remainingSec = Math.max(0, c.remainingSec - 1);
    renderCountdown();
  }
})(window);
