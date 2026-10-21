/**
 * Dashboard (#section-dashboard): live tiles (GET /api/admin/status + /api/admin/host + /api/admin/server/state, every 5 s),
 * Chart.js trend charts (GET /api/admin/status/history?minutes=60|360|1440, every 60 s), the server load panels (game loop phases,
 * iteration histogram, per-thread CPU, mob thread table), the activity feed (GET /api/admin/events?after=<seq>, every 5 s) and quick
 * actions (Save now, Reboot, open Broadcast).
 * Live range: tiles every 1 s and charts from the server's 1 s ring (GET /api/admin/status/history?live=1&after=<unix ms>, every 1 s,
 * which also keeps the server's 1 s sampling lease alive). 1 s polling is a user-approved exception (2026-10-21), only while Live is on.
 * Everything polls only while the section is visible and the browser tab is in the foreground (AdminSections.poll).
 */
(function (global) {
  'use strict';

  var STATUS_MS = 5000;
  var HISTORY_MS = 60000;
  var LIVE_MS = 1000;
  var LIVE_KEEP_MS = 15 * 60 * 1000;
  var EVENTS_MS = 5000;
  var MAX_FEED_ROWS = 300;
  var RANGES = ['live', '60', '360', '1440'];
  var LS_RANGE = 'mircraft_dashRangeMin';
  var LS_FEED = 'mircraft_dashFeedKind';

  var EVENT_META = {
    login: { label: 'Login', cls: 'ev-login' },
    logout: { label: 'Logout', cls: 'ev-logout' },
    'login-failed': { label: 'Login failed', cls: 'ev-login-failed' },
    world: { label: 'World', cls: 'ev-world' },
    admin: { label: 'Admin', cls: 'ev-admin' }
  };

  // Game loop phases (server StatusSample.PhaseNames order) and their chart colours.
  var PHASES = [
    { key: 'Connections', label: 'Connections', color: '#6cb6ff' },
    { key: 'MobBarrier', label: 'Mob barrier', color: '#ff7a1f' },
    { key: 'Objects', label: 'Objects', color: '#e8d3a0' },
    { key: 'Maps', label: 'Maps', color: '#5fd38a' },
    { key: 'EnvirProcess', label: 'Envir', color: '#c38bff' },
    { key: 'AdminDrain', label: 'Admin', color: '#4fd1c5' },
    { key: 'Saves', label: 'Saves', color: '#e0524d' }
  ];
  // Series colours for threads: game thread first, then mob threads.
  var THREAD_COLORS = ['#ff7a1f', '#6cb6ff', '#5fd38a', '#c38bff', '#e8b04a', '#4fd1c5', '#ff8fb1', '#e8d3a0', '#9fd36c', '#b0a3ff'];
  var HIST_LABELS = { '<0.1ms': '< 0.1 ms', '<1ms': '< 1 ms', '<5ms': '< 5 ms', '<10ms': '< 10 ms', '<20ms': '< 20 ms', '<50ms': '< 50 ms', '<100ms': '< 100 ms', '>=100ms': '≥ 100 ms' };

  var root = null;          // the section element
  var ui = {};              // cached element refs
  var stops = [];           // AdminSections.poll stop functions
  var charts = null;        // Chart.js instances by key
  var range = '60';         // 'live' or minutes as a string
  var liveSamples = [];     // live mode: the 1 s ring, oldest first
  var lastSamples = [];     // what the charts show now
  var feedKind = '';
  var lastEventSeq = 0;
  var lastOkMs = 0;
  var pollFailed = false;
  var lastStatus = null;
  var lastHost = null;
  var lastState = null;
  var statePollN = 0;
  var busy = false;         // a quick action is running

  function esc(s) { return OpsUI.esc(s); }
  function isLive() { return range === 'live'; }

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
  function hhmmss(d) { return hhmm(d) + ':' + two(d.getSeconds()); }
  function fmtNum(n) { return n == null || isNaN(n) ? '—' : Number(n).toLocaleString(); }
  function fmtMb(mb) {
    mb = Number(mb) || 0;
    if (mb >= 1048576) return (mb / 1048576).toFixed(2) + ' TB';
    if (mb >= 1024) return (mb / 1024).toFixed(1) + ' GB';
    return mb + ' MB';
  }
  function fmtRate(bytes) {
    bytes = Number(bytes) || 0;
    if (bytes >= 1048576) return (bytes / 1048576).toFixed(1) + ' MB/s';
    if (bytes >= 1024) return (bytes / 1024).toFixed(bytes >= 102400 ? 0 : 1) + ' KB/s';
    return bytes + ' B/s';
  }
  function fmtMs(ms) {
    ms = Number(ms) || 0;
    return (ms >= 100 ? ms.toFixed(0) : ms >= 10 ? ms.toFixed(1) : ms.toFixed(2)) + ' ms';
  }
  function pct(v) { return (Number(v) || 0).toFixed(1) + ' %'; }

  /* ---------------------------------------------------------------- layout */

  function chartCard(key, title, hint, extraCls) {
    return '<div class="mir-card dash-chart-card' + (extraCls ? ' ' + extraCls : '') + '"><h3>' + title + '</h3>' +
      (hint ? '<p class="dash-hint">' + hint + '</p>' : '') +
      '<div class="dash-canvas"><canvas data-chart="' + key + '"></canvas><p class="dash-chart-empty mir-muted" data-empty="' + key + '">Collecting samples…</p></div></div>';
  }

  function build(el) {
    el.innerHTML =
      '<div class="dash-toolbar">' +
      '  <div class="dash-toolbar-group">' +
      '    <div class="dash-live stale" data-role="live"><span class="dash-live-dot"></span><span data-role="liveText">Connecting…</span></div>' +
      '  </div>' +
      '  <div class="dash-toolbar-group dash-range" role="group" aria-label="Chart time range" data-role="range">' +
      '    <span class="mir-muted">Charts</span>' +
      '    <button type="button" class="dash-range-btn dash-range-live" data-range="live" title="Sample every second (last 15 minutes); tiles refresh every second while this is on"><span class="dash-range-dot"></span>Live</button>' +
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
      chartCard('players', 'Players &amp; Connections') +
      chartCard('cycle', 'Tick Lateness', 'Worst delay past due of a processed object, game thread and each mob thread') +
      chartCard('memory', 'Memory &amp; CPU', 'Whole process; CPU is normalised over all cores') +
      '</div>' +
      '<h2 class="dash-band"><span>Server load</span></h2>' +
      '<div class="dash-load">' +
      chartCard('loop', 'Game Loop', 'Share of each second the game thread spent per phase (the rest is idle park)', 'dash-span-2') +
      '  <div class="mir-card dash-now-card"><h3>This Second</h3><div data-role="now"></div></div>' +
      '</div>' +
      '<div class="dash-load">' +
      chartCard('threads', 'Thread CPU', 'Per thread, % of one core. 100 % = that thread is saturated', 'dash-span-2') +
      '  <div class="mir-card dash-cpu-card"><h3>CPU Split</h3><div data-role="cpuSplit"></div></div>' +
      '</div>' +
      '<div class="mir-card dash-mob-card">' +
      '  <h3>Mob Threads</h3>' +
      '  <p class="dash-hint">Last one-second window. Thread 0 runs on the game thread inside the mob barrier.</p>' +
      '  <div class="dash-mob-wrap" data-role="mob"></div>' +
      '</div>' +
      '<div class="dash-charts">' +
      chartCard('monsters', 'Monster Activity', 'Distinct monsters that ran AI each second, and AI runs per second') +
      chartCard('network', 'Network', 'Packets per second in and out; bytes sent per second') +
      chartCard('gc', 'Garbage Collection', 'Allocation per second, collections per second and GC pause') +
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
    ui.now = el.querySelector('[data-role="now"]');
    ui.cpuSplit = el.querySelector('[data-role="cpuSplit"]');
    ui.mob = el.querySelector('[data-role="mob"]');
    ui.feed = el.querySelector('[data-role="feed"]');
    ui.feedKind = el.querySelector('[data-role="feedKind"]');
    ui.countdown = el.querySelector('[data-role="countdown"]');
    ui.countdownText = el.querySelector('[data-role="countdownText"]');
    ui.save = el.querySelector('[data-act="save"]');
    ui.reboot = el.querySelector('[data-act="reboot"]');

    var saved = lsGet(LS_RANGE);
    if (RANGES.indexOf(saved) >= 0) range = saved;
    markRange();
    ui.range.addEventListener('click', function (e) {
      var b = e.target && e.target.closest ? e.target.closest('[data-range]') : null;
      if (!b) return;
      var r = b.getAttribute('data-range');
      if (RANGES.indexOf(r) < 0 || r === range) return;
      range = r;
      lsSet(LS_RANGE, r);
      markRange();
      liveSamples = [];
      startPolling();
      if (isLive()) { drawHistory([]); pollLive(); }
      else pollHistory();
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
      b.classList.toggle('active', b.getAttribute('data-range') === range);
    });
    if (root) root.classList.toggle('dash-is-live', isLive());
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

  /** The current loop profile, or null when the world is not running or it has not published yet. */
  function profile() {
    var st = lastStatus;
    return st && st.world === 'running' && st.loopProfile ? st.loopProfile : null;
  }

  function gameThreadCpu(h) {
    var tc = h && h.threadCpu;
    if (!tc || !tc.threads) return null;
    for (var i = 0; i < tc.threads.length; i++) if (tc.threads[i].mobThreadId === 0) return tc.threads[i].cpuPercent;
    return null;
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
    var lp = profile();
    var gameCpu = gameThreadCpu(h);
    var busyPct = lp ? (Number(lp.busy) || 0) * 100 : null;
    var awake = lp && lp.monstersAwake >= 0 ? lp.monstersAwake : null;

    var specs = [
      { key: 'world', label: 'World', value: crashed ? 'Crashed' : (WORLD_TEXT[world] || world), cls: crashed ? 'metric-bad' : worldCls, href: 'server',
        sub: st.systemDataStale ? 'game data edited' : null, title: 'Open Server Control' },
      { key: 'players', label: 'Players', value: fmtNum(st.players), href: 'players-online', title: 'Open Players Online' },
      { key: 'connections', label: 'Connections', value: fmtNum(st.connections), sub: st.blockedIps ? st.blockedIps + ' IPs blocked' : null },
      { key: 'monsters', label: 'Monsters', value: fmtNum(st.monsters),
        sub: awake != null ? fmtNum(awake) + ' awake' + (st.monsters ? ' · ' + Math.round(awake / st.monsters * 100) + ' %' : '') : null,
        bar: awake != null && st.monsters ? awake / st.monsters : null,
        title: 'Spawned monsters; awake = distinct monsters whose AI ran in the last second' },
      { key: 'gamethread', label: 'Game thread', value: busyPct != null ? busyPct.toFixed(1) + ' %' : '—', bar: busyPct != null ? busyPct / 100 : null,
        cls: busyPct != null ? level(busyPct, 70, 90) : '',
        sub: gameCpu != null ? 'CPU ' + pct(gameCpu) + ' of 1 core' : (lp ? lp.iterations + ' loops/s' : null),
        title: 'Share of the last second the game loop was busy (not idle-parked). The game loop is single-threaded: near 100 % it cannot keep up' },
      { key: 'tick', label: 'Tick time', value: lp ? fmtMs(lp.maxIterationMs) : '—', cls: lp ? level(lp.maxIterationMs, 50, 100) : '',
        sub: running ? 'late ' + fmtNum(st.cycleDelayMs) + ' ms' + (st.mobThreadDelays && st.mobThreadDelays.length ? ' · mobs ' + st.mobThreadDelays.join('/') : '') : null,
        title: 'Slowest game loop iteration in the last second; "late" = worst delay past due of a processed object (game thread, then mob threads)' },
      { key: 'uptime', label: 'Uptime', value: running && st.uptimeMs > 0 ? OpsUI.duration(st.uptimeMs / 1000) : '—', sub: procUp,
        title: 'Game world uptime (Stop / Start / Reboot reset it)' },
      { key: 'memory', label: 'Memory', value: h ? fmtMb(h.workingSetMb) : '—', sub: h ? 'GC heap ' + fmtMb(h.gcHeapMb) : null,
        cls: h ? level(h.workingSetMb, 3072, 4096) : '', title: h ? 'Working set ' + fmtMb(h.workingSetMb) + ', private ' + fmtMb(h.privateMb) + ', peak ' + fmtMb(h.peakWorkingSetMb) : '' },
      { key: 'cpu', label: 'CPU', value: h ? Number(h.cpuPercent || 0).toFixed(1) + ' %' : '—', bar: h ? (Number(h.cpuPercent) || 0) / 100 : null,
        cls: h ? level(h.cpuPercent, 70, 90) : '', sub: h ? (h.processorCount ? h.processorCount + ' cores' : '') + (h.processorCount ? ' · 1 core = ' + (100 / h.processorCount).toFixed(1) + ' %' : '') : null,
        title: 'Server.exe CPU across all cores (all threads). See Thread CPU below for the game and mob threads' },
      { key: 'network', label: 'Network out', value: lp ? fmtRate(lp.bytesOut) : '—',
        sub: lp ? fmtNum(lp.packetsIn) + ' in · ' + fmtNum(lp.packetsOut) + ' out /s' : null,
        title: 'Bytes sent per second; packets received and sent per second (last second)' },
      { key: 'gc', label: 'Allocation', value: lp ? (lp.allocatedBytes / 1048576).toFixed(1) + ' MB/s' : '—',
        cls: lp ? (lp.gen2 > 0 ? 'metric-warn' : level(lp.gcPauseMs, 50, 200)) : '',
        sub: lp ? 'GC ' + lp.gen0 + '/' + lp.gen1 + '/' + lp.gen2 + ' · ' + fmtMs(lp.gcPauseMs) + ' pause' : null,
        title: 'Managed allocation per second; gen0/gen1/gen2 collections and total GC pause in the last second' },
      { key: 'pending', label: 'Pending reboot', value: String(pending), cls: pending > 0 ? 'metric-warn' : 'metric-ok', href: 'server',
        sub: pending > 0 ? 'restart to apply' : 'nothing pending', title: 'Saved game-data changes that apply on the next world start' }
    ];
    if (st.livedb) specs.push(liveDbTile(st.livedb));
    if (h && h.diskFreeMb) {
      specs.push({ key: 'disk', label: 'Disk free', value: fmtMb(h.diskFreeMb), cls: h.diskFreeMb < 2048 ? 'metric-bad' : (h.diskFreeMb < 8192 ? 'metric-warn' : ''),
        sub: h.diskRoot ? 'drive ' + h.diskRoot : null, bar: h.diskTotalMb ? 1 - h.diskFreeMb / h.diskTotalMb : null });
    }
    renderTiles(specs);
    renderNow(lp);
    renderCpuSplit(h, lp);
    renderMob(lp, h);
    renderCountdown();
    updateActions();
  }

  /* ---------------------------------------------------------------- live load panels */

  function emptyNote(text) { return '<p class="dash-panel-empty mir-muted">' + esc(text) + '</p>'; }

  function stat(label, value, cls) {
    return '<div class="dash-stat' + (cls ? ' ' + cls : '') + '"><span class="dash-stat-v">' + esc(value) + '</span><span class="dash-stat-l">' + esc(label) + '</span></div>';
  }

  /** "This second": headline numbers, the phase split as one bar, and the iteration-time histogram. */
  function renderNow(lp) {
    if (!ui.now) return;
    if (!lp) { ui.now.innerHTML = emptyNote('Waiting for the game loop…'); return; }
    var busy = (Number(lp.busy) || 0) * 100;
    var win = Number(lp.windowMs) || 1000;
    var html = '<div class="dash-stats">' +
      stat('busy', busy.toFixed(1) + ' %', busy >= 90 ? 'bad' : busy >= 70 ? 'warn' : '') +
      stat('loops / s', fmtNum(lp.iterations)) +
      stat('slowest', fmtMs(lp.maxIterationMs), lp.maxIterationMs >= 100 ? 'bad' : lp.maxIterationMs >= 50 ? 'warn' : '') +
      '</div>';

    // Phase split of the busy time, one stacked bar.
    var ph = lp.phaseMs || {};
    html += '<div class="dash-split" title="Where the busy time went">';
    PHASES.forEach(function (p) {
      var ms = Number(ph[p.key]) || 0;
      var w = ms / win * 100;
      if (w < 0.05) return;
      html += '<span style="width:' + w.toFixed(2) + '%;background:' + p.color + '" title="' + esc(p.label + ': ' + fmtMs(ms)) + '"></span>';
    });
    html += '</div><div class="dash-split-legend">';
    PHASES.forEach(function (p) {
      var ms = Number(ph[p.key]) || 0;
      html += '<span><i style="background:' + p.color + '"></i>' + esc(p.label) + ' <b>' + esc(fmtMs(ms)) + '</b></span>';
    });
    html += '</div>';

    // Iteration histogram (counts per duration bucket), log-ish bar widths so the rare slow loops stay visible.
    var hist = lp.histogram || {};
    var keys = Object.keys(hist);
    var maxLog = 0;
    keys.forEach(function (k) { maxLog = Math.max(maxLog, Math.log10(1 + (Number(hist[k]) || 0))); });
    html += '<div class="dash-hist-title">Loop iterations by duration</div><div class="dash-hist">';
    keys.forEach(function (k, i) {
      var n = Number(hist[k]) || 0;
      var w = maxLog ? Math.log10(1 + n) / maxLog * 100 : 0;
      var cls = i >= 5 ? 'bad' : i >= 3 ? 'warn' : '';
      html += '<div class="dash-hist-row ' + (n ? '' : 'zero ') + cls + '"><span class="dash-hist-l">' + esc(HIST_LABELS[k] || k) + '</span>' +
        '<span class="dash-hist-bar"><span style="width:' + w.toFixed(1) + '%"></span></span><span class="dash-hist-n">' + esc(fmtNum(n)) + '</span></div>';
    });
    html += '</div>';
    ui.now.innerHTML = html;
  }

  function meter(value, max, cls) {
    var w = max > 0 ? Math.max(0, Math.min(1, value / max)) * 100 : 0;
    return '<span class="dash-meter ' + (cls || '') + '"><span style="width:' + w.toFixed(1) + '%"></span></span>';
  }

  function levelCls(v, warnAt, badAt) { return v >= badAt ? 'bad' : v >= warnAt ? 'warn' : ''; }

  /** CPU split: game thread, each mob thread and the rest of the process, each as % of one core. */
  function renderCpuSplit(h, lp) {
    if (!ui.cpuSplit) return;
    var tc = h && h.threadCpu;
    if (!tc || !tc.threads || !tc.threads.length) {
      ui.cpuSplit.innerHTML = emptyNote(lastStatus && lastStatus.world === 'running' ? 'Measuring (needs two samples)…' : 'The world is not running.');
      return;
    }
    var rows = tc.threads.slice().sort(function (a, b) { return a.mobThreadId - b.mobThreadId; });
    var html = '<div class="dash-cpu-rows">';
    rows.forEach(function (t, i) {
      var name = t.mobThreadId === 0 ? 'Game thread' : 'Mob thread ' + t.mobThreadId;
      var c = Number(t.cpuPercent) || 0;
      html += '<div class="dash-cpu-row"><span class="dash-cpu-name"><i style="background:' + THREAD_COLORS[i % THREAD_COLORS.length] + '"></i>' + esc(name) + '</span>' +
        meter(c, 100, levelCls(c, 70, 90)) + '<span class="dash-cpu-v">' + esc(pct(c)) + '</span></div>';
    });
    var other = Number(tc.otherPercent) || 0;
    html += '<div class="dash-cpu-row dash-cpu-other"><span class="dash-cpu-name"><i></i>Everything else</span>' +
      meter(other, Math.max(100, other), '') + '<span class="dash-cpu-v">' + esc(pct(other)) + '</span></div></div>';
    var cores = h.processorCount || 0;
    html += '<p class="dash-hint">% of one core per thread, averaged over the last 5 s. "Everything else" is network, GC, web admin and timers' +
      (other > 100 ? ' (more than one core)' : '') + '.' + (cores ? ' The machine has ' + cores + ' cores.' : '') + '</p>';
    ui.cpuSplit.innerHTML = html;
  }

  /** Mob thread table from loopProfile.mobThreads (+ per-thread CPU from /host). */
  function renderMob(lp, h) {
    if (!ui.mob) return;
    var list = lp && lp.mobThreads;
    if (!lp) { ui.mob.innerHTML = emptyNote('Waiting for the game loop…'); return; }
    if (!list || !list.length) {
      ui.mob.innerHTML = emptyNote('No mob thread data: Multithreaded or DueScheduler is off ([Optimisation] in Setup.ini).');
      return;
    }
    var cpuById = {};
    var tc = h && h.threadCpu;
    if (tc && tc.threads) tc.threads.forEach(function (t) { cpuById[t.mobThreadId] = t.cpuPercent; });
    var win = Number(lp.windowMs) || 1000;
    var html = '<table class="dash-mob"><thead><tr>' +
      '<th>Thread</th><th class="w">CPU (1 core)</th><th class="w">Busy</th><th>Passes</th><th>Over 10 ms</th><th>Slowest pass</th>' +
      '<th>Queued</th><th>Awake</th><th>AI runs/s</th><th>Late</th></tr></thead><tbody>';
    var tot = { processed: 0, awake: 0, queued: 0, busy: 0 };
    list.forEach(function (t) {
      if (!t) return;
      var busyPct = (Number(t.busyMs) || 0) / win * 100;
      var cpu = t.onGameThread ? null : cpuById[t.id];
      tot.processed += t.processed || 0;
      tot.awake += t.awake || 0;
      tot.queued += t.queued || 0;
      html += '<tr' + (t.dead ? ' class="dead"' : '') + '>' +
        '<td><span class="dash-mob-id">' + esc(String(t.id)) + '</span>' + (t.onGameThread ? '<span class="dash-tag">game</span>' : '') + (t.dead ? '<span class="dash-tag bad">dead</span>' : '') + '</td>' +
        '<td class="w">' + (cpu == null ? '<span class="mir-muted" title="Counted in the game thread">—</span>' : meter(cpu, 100, levelCls(cpu, 70, 90)) + '<span class="dash-num">' + esc(pct(cpu)) + '</span>') + '</td>' +
        '<td class="w">' + meter(busyPct, 100, levelCls(busyPct, 60, 85)) + '<span class="dash-num">' + esc(fmtMs(t.busyMs)) + '</span></td>' +
        '<td class="dash-num">' + esc(fmtNum(t.passes)) + '</td>' +
        '<td class="dash-num' + (t.overruns ? ' warn' : '') + '">' + esc(fmtNum(t.overruns)) + '</td>' +
        '<td class="dash-num' + (t.maxPassMs >= 10 ? ' warn' : '') + '">' + esc(fmtMs(t.maxPassMs)) + '</td>' +
        '<td class="dash-num">' + esc(fmtNum(t.queued)) + '</td>' +
        '<td class="dash-num">' + esc(fmtNum(t.awake)) + '</td>' +
        '<td class="dash-num">' + esc(fmtNum(t.processed)) + '</td>' +
        '<td class="dash-num' + (t.lateMs >= 100 ? ' bad' : t.lateMs >= 30 ? ' warn' : '') + '">' + esc(fmtNum(t.lateMs)) + ' ms</td></tr>';
    });
    html += '</tbody><tfoot><tr><td>All</td><td></td><td></td><td></td><td></td><td></td>' +
      '<td class="dash-num">' + esc(fmtNum(tot.queued)) + '</td><td class="dash-num">' + esc(fmtNum(tot.awake)) + '</td>' +
      '<td class="dash-num">' + esc(fmtNum(tot.processed)) + '</td><td></td></tr></tfoot></table>';
    ui.mob.innerHTML = html;
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
    var stale = pollFailed || age == null || age > (isLive() ? 5 : 15);
    ui.live.classList.toggle('stale', stale);
    if (ui.tiles) ui.tiles.classList.toggle('dash-stale', stale && age != null);
    if (age == null) ui.liveText.textContent = pollFailed ? 'Offline — server not responding' : 'Connecting…';
    else if (stale) ui.liveText.textContent = 'Stale — last update ' + age + 's ago';
    else ui.liveText.textContent = (isLive() ? 'Live · 1 s' : 'Live') + ' · updated ' + age + 's ago';
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
        legend: { labels: { color: text, boxWidth: 10, boxHeight: 10, usePointStyle: true, pointStyle: 'rectRounded' } },
        tooltip: {
          backgroundColor: 'rgba(13,11,16,0.95)',
          borderColor: 'rgba(197,160,89,0.35)',
          borderWidth: 1,
          titleColor: '#e8d3a0',
          bodyColor: '#e9e3d8',
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
        x: { ticks: { color: text, maxTicksLimit: 8, autoSkip: true, maxRotation: 0 }, grid: { color: grid } },
        y: { ticks: { color: text }, grid: { color: grid }, beginAtZero: true }
      }
    };
  }

  function lineDs(label, color, extra) {
    var d = { label: label, data: [], borderColor: color, backgroundColor: withAlpha(color, 0.15), pointRadius: 0, pointHoverRadius: 3, tension: 0.25, borderWidth: 1.75 };
    if (extra) Object.keys(extra).forEach(function (k) { d[k] = extra[k]; });
    return d;
  }

  function makeCharts() {
    if (charts || typeof global.Chart === 'undefined' || !root) return;
    var accent = cssVar('--accent', '#ff7a1f');
    var success = cssVar('--success', '#5fd38a');
    var warn = cssVar('--warn', '#e8b04a');
    var danger = cssVar('--danger', '#e0524d');
    var text = cssVar('--muted', '#8a7f70');

    function make(key, type, datasets, tweak) {
      var canvas = root.querySelector('canvas[data-chart="' + key + '"]');
      if (!canvas) return null;
      var o = baseOptions();
      if (tweak) tweak(o);
      return new global.Chart(canvas.getContext('2d'), { type: type, data: { labels: [], datasets: datasets }, options: o });
    }
    function rightAxis(o, opts) {
      o.scales.y1 = { position: 'right', beginAtZero: true, ticks: { color: text }, grid: { drawOnChartArea: false } };
      if (opts) Object.keys(opts).forEach(function (k) { o.scales.y1[k] = opts[k]; });
    }
    function unit(o, axis, suffix) { o.scales[axis].ticks.callback = function (v) { return v + suffix; }; }

    charts = {
      players: make('players', 'line', [
        lineDs('Players', accent, { fill: true, stepped: true, borderWidth: 2, tension: 0 }),
        lineDs('Connections', success, { stepped: true, tension: 0, backgroundColor: 'transparent' })
      ], function (o) { o.scales.y.ticks.precision = 0; o.scales.y.suggestedMax = 2; }),
      cycle: make('cycle', 'line', [
        lineDs('Game thread', warn, { fill: true, borderWidth: 2 })
      ], function (o) { o.scales.y.suggestedMax = 20; unit(o, 'y', ' ms'); }),
      memory: make('memory', 'line', [
        lineDs('Working set (MB)', accent, { borderWidth: 2, yAxisID: 'y', backgroundColor: 'transparent' }),
        lineDs('CPU (%)', danger, { yAxisID: 'y1', backgroundColor: 'transparent' })
      ], function (o) { rightAxis(o, { max: 100 }); unit(o, 'y1', ' %'); }),
      loop: make('loop', 'line', PHASES.map(function (p, i) {
        return lineDs(p.label, p.color, { fill: i === 0 ? 'origin' : '-1', backgroundColor: withAlpha(p.color, 0.55), borderWidth: 1, tension: 0.2 });
      }).concat([lineDs('Slowest loop (ms)', '#ffffff', { yAxisID: 'y1', borderDash: [4, 3], borderWidth: 1.25, backgroundColor: 'transparent', stack: 'max' })]),
      function (o) {
        o.scales.y.stacked = true;
        o.scales.y.suggestedMax = 25;
        unit(o, 'y', ' %');
        rightAxis(o, { suggestedMax: 20 });
        unit(o, 'y1', ' ms');
      }),
      threads: make('threads', 'line', [], function (o) { o.scales.y.suggestedMax = 25; unit(o, 'y', ' %'); }),
      monsters: make('monsters', 'line', [
        lineDs('Awake monsters', success, { fill: true, borderWidth: 2 }),
        lineDs('AI runs / s', accent, { yAxisID: 'y1', backgroundColor: 'transparent' })
      ], function (o) { o.scales.y.ticks.precision = 0; rightAxis(o); }),
      network: make('network', 'line', [
        lineDs('Packets in /s', '#6cb6ff', { backgroundColor: 'transparent' }),
        lineDs('Packets out /s', accent, { backgroundColor: 'transparent' }),
        lineDs('KB out /s', '#e8d3a0', { yAxisID: 'y1', fill: true, backgroundColor: withAlpha('#e8d3a0', 0.08), borderWidth: 1.25 })
      ], function (o) { rightAxis(o); unit(o, 'y1', ' KB'); }),
      gc: make('gc', 'bar', [
        { type: 'bar', label: 'Allocated MB/s', data: [], backgroundColor: withAlpha(accent, 0.45), borderColor: accent, borderWidth: 0, barPercentage: 1, categoryPercentage: 1, yAxisID: 'y' },
        lineDs('Gen0 /s', '#6cb6ff', { type: 'line', yAxisID: 'y1', backgroundColor: 'transparent', borderWidth: 1.25 }),
        lineDs('Gen1 /s', '#c38bff', { type: 'line', yAxisID: 'y1', backgroundColor: 'transparent', borderWidth: 1.25 }),
        lineDs('Gen2 /s', danger, { type: 'line', yAxisID: 'y1', backgroundColor: 'transparent', borderWidth: 2 }),
        lineDs('Pause (ms)', warn, { type: 'line', yAxisID: 'y1', borderDash: [4, 3], backgroundColor: 'transparent', borderWidth: 1.25 })
      ], function (o) { unit(o, 'y', ' MB'); rightAxis(o, { suggestedMax: 4 }); })
    };
  }

  function setEmpty(key, empty, text) {
    var n = root && root.querySelector('[data-empty="' + key + '"]');
    if (!n) return;
    n.style.display = empty ? '' : 'none';
    if (text) n.textContent = text;
  }

  /** Thread CPU datasets follow the thread count in the data: Game thread, Mob 1..n, Everything else. */
  function syncThreadDatasets(n) {
    var c = charts && charts.threads;
    if (!c) return;
    var want = n + 2;
    if (c.data.datasets.length === want) return;
    var ds = [lineDs('Game thread', THREAD_COLORS[0], { borderWidth: 2.25, fill: true, backgroundColor: withAlpha(THREAD_COLORS[0], 0.1) })];
    for (var i = 1; i <= n; i++) ds.push(lineDs('Mob ' + i, THREAD_COLORS[i % THREAD_COLORS.length], { backgroundColor: 'transparent' }));
    ds.push(lineDs('Everything else', '#8a7f70', { borderDash: [4, 3], backgroundColor: 'transparent', borderWidth: 1.25 }));
    c.data.datasets = ds;
  }

  function syncLateDatasets(n) {
    var c = charts && charts.cycle;
    if (!c) return;
    var want = 1 + n;
    if (c.data.datasets.length === want) return;
    var ds = [c.data.datasets[0]];
    for (var i = 0; i < n; i++) ds.push(lineDs('Mob ' + i, THREAD_COLORS[(i + 1) % THREAD_COLORS.length], { backgroundColor: 'transparent', borderWidth: 1.25 }));
    c.data.datasets = ds;
  }

  function drawHistory(samples) {
    makeCharts();
    samples = samples || [];
    lastSamples = samples;
    var empty = samples.length < 2;
    var emptyText = isLive() ? 'Collecting live samples (1 per second)…' : 'Collecting samples…';
    Object.keys(charts || { players: 1 }).forEach(function (k) { setEmpty(k, empty, emptyText); });
    if (!charts) return;
    var times = samples.map(function (s) { return OpsUI.toDate(s.utc) || new Date(); });
    var multiDay = times.length > 1 && times[0].toDateString() !== times[times.length - 1].toDateString();
    var labels = times.map(function (d) { return isLive() ? hhmmss(d) : (multiDay ? (d.getMonth() + 1) + '/' + d.getDate() + ' ' + hhmm(d) : hhmm(d)); });

    function set(chart, arrays) {
      if (!chart) return;
      chart._times = times;
      chart.data.labels = labels;
      arrays.forEach(function (arr, i) { if (chart.data.datasets[i]) chart.data.datasets[i].data = arr; });
      chart.update('none');
    }
    function col(fn) { return samples.map(fn); }
    function arrLen(key) {
      var n = 0;
      samples.forEach(function (s) { if (s[key] && s[key].length > n) n = s[key].length; });
      return n;
    }
    function at(arr, i) { return arr && arr.length > i ? arr[i] : null; }

    set(charts.players, [col(function (s) { return s.players; }), col(function (s) { return s.connections; })]);

    var lateN = arrLen('mobLateMs');
    syncLateDatasets(lateN);
    var late = [col(function (s) { return s.cycleDelayMs; })];
    for (var li = 0; li < lateN; li++) (function (i) { late.push(col(function (s) { return at(s.mobLateMs, i); })); })(li);
    set(charts.cycle, late);

    set(charts.memory, [col(function (s) { return s.workingSetMb; }), col(function (s) { return s.cpuPercent; })]);

    // Phase ms per one-second window -> % of the second.
    var loop = PHASES.map(function (p, i) { return col(function (s) { var v = at(s.phaseMs, i); return v == null ? null : Math.round(v) / 10; }); });
    loop.push(col(function (s) { return s.maxIterationMs || null; }));
    set(charts.loop, loop);

    var mobN = Math.max(0, arrLen('mobCpu') - 1);
    syncThreadDatasets(mobN);
    var th = [col(function (s) { return s.gameCpuPercent; })];
    for (var mi = 1; mi <= mobN; mi++) (function (i) { th.push(col(function (s) { return at(s.mobCpu, i); })); })(mi);
    th.push(col(function (s) { return s.otherCpuPercent; }));
    set(charts.threads, th);

    set(charts.monsters, [
      col(function (s) { return s.monstersAwake >= 0 ? s.monstersAwake : null; }),
      col(function (s) { return s.aiRuns; })
    ]);
    set(charts.network, [
      col(function (s) { return s.packetsIn; }),
      col(function (s) { return s.packetsOut; }),
      col(function (s) { return Math.round((s.bytesOut || 0) / 102.4) / 10; })
    ]);
    set(charts.gc, [
      col(function (s) { return s.allocMb; }),
      col(function (s) { return s.gen0; }),
      col(function (s) { return s.gen1; }),
      col(function (s) { return s.gen2; }),
      col(function (s) { return s.gcPauseMs; })
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
    // In live mode the server state (countdown, crash flag) is still fetched every 5 s.
    var wantState = !isLive() || statePollN++ % 5 === 0;
    var results = await Promise.all([
      global.AdminStatus ? global.AdminStatus.refresh() : getJson('/api/admin/status'),
      getJson('/api/admin/host'),
      wantState ? getJson('/api/admin/server/state') : Promise.resolve(null)
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
    if (isLive()) return;
    var want = range;
    var j = await getJson('/api/admin/status/history?minutes=' + want);
    if (j && range === want) drawHistory(j.samples || []);
  }

  /** Live mode: fetch the 1 s samples newer than the last one held (this also renews the server's 1 s lease). */
  async function pollLive() {
    if (!isLive()) return;
    var last = liveSamples.length ? liveSamples[liveSamples.length - 1] : null;
    var lastMs = last ? Date.parse(last.utc) : 0;
    var j = await getJson('/api/admin/status/history?live=1&after=' + (lastMs || 0));
    if (!j || !isLive()) return;
    (j.samples || []).forEach(function (s) {
      var ms = Date.parse(s.utc);
      if (!lastMs || ms > lastMs) { liveSamples.push(s); lastMs = ms; }
    });
    var cut = Date.now() - LIVE_KEEP_MS;
    while (liveSamples.length && Date.parse(liveSamples[0].utc) < cut) liveSamples.shift();
    drawHistory(liveSamples);
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

  function stopPolling() {
    stops.forEach(function (stop) { stop(); });
    stops = [];
  }

  /** (Re)starts the pollers for the current range: 1 s while Live is on, the normal 5 s / 60 s otherwise. */
  function startPolling() {
    stopPolling();
    var live = isLive();
    stops.push(global.AdminSections.poll('dashboard', pollStatus, live ? LIVE_MS : STATUS_MS));
    stops.push(global.AdminSections.poll('dashboard', live ? pollLive : pollHistory, live ? LIVE_MS : HISTORY_MS));
    stops.push(global.AdminSections.poll('dashboard', pollEvents, EVENTS_MS));
    stops.push(global.AdminSections.poll('dashboard', function () { updateLive(); renderCountdownTick(); }, 1000));
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
      markRange();
      render();
    },
    show: function () {
      makeCharts();
      if (!stops.length) startPolling();
      return Promise.all([pollStatus(), isLive() ? pollLive() : pollHistory(), pollEvents()]);
    },
    hide: function () {
      stopPolling();
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
