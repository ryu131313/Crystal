/**
 * Server Control (#section-server): world status card with Start / Stop / Reboot / Save / Shut down, restart countdown,
 * reloads (NPCs, drops, line messages, recipes), the pending-reboot list, server info and the log viewer
 * (Live Server / Live Debug / Live Chat from the in-memory rings, plus the log4net files under Logs\).
 *
 * Endpoints: GET /api/admin/status, GET /api/admin/server/state, GET /api/admin/server/info,
 * POST /api/admin/server/{start|stop|reboot|save|shutdown}, POST /api/admin/server/reload/{kind},
 * POST /api/admin/server/restart-countdown[/cancel], GET /api/admin/logs/{server|debug|chat}?after=&max=,
 * GET /api/admin/logs/files, GET /api/admin/logs/file?kind=&name=&lines=, GET /api/admin/logs/download?kind=&name=.
 * Deep link: #section=server&log=server|debug|chat|files.
 */
(function (global) {
  'use strict';

  var STATUS_MS = 3000;
  var LOG_MS = 2000;
  var LS_CAP = 'mircraft_serverLogCap';
  var LS_TAB = 'mircraft_serverLogTab';
  var LOG_KINDS = ['server', 'debug', 'chat'];
  var RELOADS = [
    { kind: 'npcs', label: 'NPC scripts', hint: 'Reloads every NPC script (Envir\\NPCs) and saves NPC goods first.' },
    { kind: 'drops', label: 'Drops', hint: 'Reloads monster, fishing, awakening, strongbox and blackstone drop files (Envir\\Drops).' },
    { kind: 'linemessages', label: 'Line messages', hint: 'Reloads Envir\\LineMessage.txt.' },
    { kind: 'recipes', label: 'Recipes', hint: 'Reloads the crafting recipes from Envir\\Recipe\\*.txt.' }
  ];

  var root = null;
  var ui = {};
  var stops = [];
  var status = null;      // GET /api/admin/status
  var state = null;       // GET /api/admin/server/state
  var info = null;        // GET /api/admin/server/info
  var infoWorld = null;   // world state when info was last loaded
  var actionBusy = false;

  // Log viewer state
  var logTab = 'server';                     // server | debug | chat | files
  var logCap = 2000;
  var paused = false;
  var live = {};                             // kind -> { lines: [{seq, utc, text}], last }
  var fileView = null;                       // { kind, name, lines: [string], size, truncated }
  var renderedCount = 0;                     // lines currently in the <pre> for the live tab (filtered)

  function esc(s) { return OpsUI.esc(s); }
  function lsGet(k) { try { return global.localStorage.getItem(k); } catch (e) { return null; } }
  function lsSet(k, v) { try { global.localStorage.setItem(k, v); } catch (e) { /* private mode */ } }
  function two(n) { return ('0' + n).slice(-2); }
  function fmtBytes(n) {
    n = Number(n) || 0;
    if (n >= 1048576) return (n / 1048576).toFixed(1) + ' MB';
    if (n >= 1024) return (n / 1024).toFixed(1) + ' KB';
    return n + ' B';
  }

  /* ================================================================= layout */

  function build(el) {
    var reloadButtons = RELOADS.map(function (r) {
      return '<button type="button" class="mir-btn" data-reload="' + esc(r.kind) + '" title="' + esc(r.hint) + '">' + esc(r.label) + '</button>';
    }).join(' ');

    el.innerHTML =
      '<div class="srv-grid">' +
      '  <div class="mir-card srv-status-card">' +
      '    <div class="toolbar"><h2>World</h2><span class="ops-pill" data-role="pill">…</span></div>' +
      '    <dl class="kv-list srv-kv" data-role="facts"></dl>' +
      '    <p class="srv-note hidden" data-role="note"></p>' +
      '    <div class="srv-buttons">' +
      '      <button type="button" class="mir-btn primary" data-act="start">Start</button>' +
      '      <button type="button" class="mir-btn danger" data-act="stop">Stop</button>' +
      '      <button type="button" class="mir-btn danger" data-act="reboot">Reboot</button>' +
      '      <button type="button" class="mir-btn" data-act="save">Save now</button>' +
      '      <button type="button" class="mir-btn danger srv-shutdown" data-act="shutdown" title="Stop the world and exit Server.exe">Shut down Server.exe</button>' +
      '    </div>' +
      '    <details class="srv-lasterror hidden" data-role="lastErrorWrap"><summary data-role="lastErrorHead">Last error</summary><pre class="admin-log-view srv-error-text" data-role="lastError"></pre></details>' +
      '  </div>' +
      '  <div class="mir-card srv-countdown-card">' +
      '    <h3>Restart countdown</h3>' +
      '    <div data-role="cdActive" class="hidden">' +
      '      <p class="srv-countdown-live" data-role="cdText"></p>' +
      '      <button type="button" class="mir-btn" data-act="cd-cancel">Cancel countdown</button>' +
      '    </div>' +
      '    <div data-role="cdForm" class="srv-countdown-form">' +
      '      <p class="mir-muted">Warns players in game (announcement) at 60/45/30/20/15/10/5/4/3/2/1 minutes, 30 and 10 seconds, then reboots the world.</p>' +
      '      <label class="ops-dialog-field"><span>Minutes (1–60)</span><input type="number" class="fd-input" min="1" max="60" value="5" data-role="cdMinutes" /></label>' +
      '      <label class="ops-dialog-field"><span>Extra message (optional)</span><input type="text" class="fd-input" maxlength="200" placeholder="e.g. Applying the new drop tables." data-role="cdMessage" /></label>' +
      '      <button type="button" class="mir-btn primary" data-act="cd-arm">Start countdown</button>' +
      '    </div>' +
      '  </div>' +
      '</div>' +
      '<div class="srv-grid">' +
      '  <div class="mir-card">' +
      '    <h3>Reload live data</h3>' +
      '    <p class="mir-muted">Re-reads files into the running world (no restart). Needs the world running.</p>' +
      '    <div class="srv-buttons">' + reloadButtons + '</div>' +
      '  </div>' +
      '  <div class="mir-card">' +
      '    <div class="toolbar"><h3>Pending reboot</h3><span class="mir-muted" data-role="pendingCount"></span></div>' +
      '    <div data-role="pending"></div>' +
      '  </div>' +
      '</div>' +
      '<div class="mir-card">' +
      '  <div class="toolbar"><h3>Server info</h3><button type="button" class="mir-btn" data-act="info-refresh">Refresh</button></div>' +
      '  <div data-role="info"><p class="mir-muted">Loading…</p></div>' +
      '</div>' +
      '<div class="mir-card">' +
      '  <div class="admin-logs-section">' +
      '    <h3>Logs</h3>' +
      '    <div class="admin-logs-tabs" role="tablist" data-role="logTabs">' +
      '      <button type="button" class="mir-btn" data-log-kind="server" role="tab">Live Server</button>' +
      '      <button type="button" class="mir-btn" data-log-kind="debug" role="tab">Live Debug</button>' +
      '      <button type="button" class="mir-btn" data-log-kind="chat" role="tab">Live Chat</button>' +
      '      <button type="button" class="mir-btn" data-log-kind="files" role="tab">Files</button>' +
      '    </div>' +
      '    <div class="toolbar srv-log-toolbar">' +
      '      <input type="search" class="fd-input srv-log-filter" placeholder="Filter lines" data-role="logFilter" />' +
      '      <label class="mir-muted">Keep <select class="fd-input" style="width:auto" data-role="logCap">' +
      '        <option value="500">500</option><option value="1000">1,000</option><option value="2000">2,000</option><option value="5000">5,000</option><option value="20000">20,000</option>' +
      '      </select> lines</label>' +
      '      <label class="mir-muted srv-pause"><input type="checkbox" data-role="logPause" /> Pause</label>' +
      '      <button type="button" class="mir-btn" data-act="log-clear" title="Clear the view (the server keeps its logs)">Clear view</button>' +
      '      <button type="button" class="mir-btn" data-act="log-download">Download</button>' +
      '    </div>' +
      '    <div class="srv-files hidden" data-role="files"></div>' +
      '    <pre class="admin-log-view srv-log-view" data-role="logView" tabindex="0">Loading…</pre>' +
      '    <p class="mir-muted" data-role="logMeta"></p>' +
      '  </div>' +
      '</div>';

    ui.pill = el.querySelector('[data-role="pill"]');
    ui.facts = el.querySelector('[data-role="facts"]');
    ui.note = el.querySelector('[data-role="note"]');
    ui.lastErrorWrap = el.querySelector('[data-role="lastErrorWrap"]');
    ui.lastErrorHead = el.querySelector('[data-role="lastErrorHead"]');
    ui.lastError = el.querySelector('[data-role="lastError"]');
    ui.cdActive = el.querySelector('[data-role="cdActive"]');
    ui.cdText = el.querySelector('[data-role="cdText"]');
    ui.cdForm = el.querySelector('[data-role="cdForm"]');
    ui.cdMinutes = el.querySelector('[data-role="cdMinutes"]');
    ui.cdMessage = el.querySelector('[data-role="cdMessage"]');
    ui.pending = el.querySelector('[data-role="pending"]');
    ui.pendingCount = el.querySelector('[data-role="pendingCount"]');
    ui.info = el.querySelector('[data-role="info"]');
    ui.logTabs = el.querySelector('[data-role="logTabs"]');
    ui.logFilter = el.querySelector('[data-role="logFilter"]');
    ui.logCap = el.querySelector('[data-role="logCap"]');
    ui.logPause = el.querySelector('[data-role="logPause"]');
    ui.files = el.querySelector('[data-role="files"]');
    ui.logView = el.querySelector('[data-role="logView"]');
    ui.logMeta = el.querySelector('[data-role="logMeta"]');
    ui.btn = {};
    el.querySelectorAll('[data-act]').forEach(function (b) { ui.btn[b.getAttribute('data-act')] = b; });

    ui.btn.start.addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, startWorld); });
    ui.btn.stop.addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, stopWorld); });
    ui.btn.reboot.addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, rebootWorld); });
    ui.btn.save.addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, saveNow); });
    ui.btn.shutdown.addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, shutdown); });
    ui.btn['cd-arm'].addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, armCountdown); });
    ui.btn['cd-cancel'].addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, cancelCountdown); });
    ui.btn['info-refresh'].addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, loadInfo); });
    el.querySelectorAll('[data-reload]').forEach(function (b) {
      b.addEventListener('click', function () { OpsUI.withBusy(b, function () { return reload(b.getAttribute('data-reload')); }); });
    });

    // Log viewer controls
    var cap = Number(lsGet(LS_CAP));
    if ([500, 1000, 2000, 5000, 20000].indexOf(cap) >= 0) logCap = cap;
    ui.logCap.value = String(logCap);
    var tab = lsGet(LS_TAB);
    if (tab === 'files' || LOG_KINDS.indexOf(tab) >= 0) logTab = tab;

    ui.logTabs.addEventListener('click', function (e) {
      var b = e.target && e.target.closest ? e.target.closest('[data-log-kind]') : null;
      if (b) selectLogTab(b.getAttribute('data-log-kind'));
    });
    ui.logFilter.addEventListener('input', function () { renderLog(true); });
    ui.logCap.addEventListener('change', function () {
      logCap = Number(ui.logCap.value) || 2000;
      lsSet(LS_CAP, String(logCap));
      LOG_KINDS.forEach(function (k) { if (live[k]) trimLive(live[k]); });
      if (logTab === 'files' && fileView) openFile(fileView.kind, fileView.name);
      else renderLog(true);
    });
    ui.logPause.addEventListener('change', function () {
      paused = ui.logPause.checked;
      updateLogMeta();
      if (!paused) pollLog();
    });
    ui.btn['log-clear'].addEventListener('click', function () {
      if (logTab === 'files') { fileView = null; renderLog(true); return; }
      var s = live[logTab];
      if (s) s.lines = [];
      renderLog(true);
    });
    ui.btn['log-download'].addEventListener('click', downloadLog);
    ui.files.addEventListener('click', function (e) {
      var b = e.target && e.target.closest ? e.target.closest('[data-file]') : null;
      if (!b) return;
      e.preventDefault();
      openFile(b.getAttribute('data-kind'), b.getAttribute('data-file'));
    });

    markLogTab();
  }

  /* ================================================================= status */

  var WORLD_TEXT = { running: 'Running', stopped: 'Stopped', starting: 'Starting…', stopping: 'Stopping…' };

  function fact(k, v) { return '<dt>' + esc(k) + '</dt><dd>' + esc(v) + '</dd>'; }

  function renderStatus() {
    var world = status ? String(status.world || 'unknown') : 'unreachable';
    var crashed = !!(state && state.crashed);
    var transition = !!(state && state.transition);

    if (ui.pill) {
      ui.pill.className = 'ops-pill ' + (crashed ? 'ops-pill-danger' : world === 'running' ? 'ops-pill-ok' : world === 'stopped' ? 'ops-pill-danger' : 'ops-pill-warn');
      ui.pill.textContent = crashed ? 'Crashed' : (WORLD_TEXT[world] || world);
    }

    var html = '';
    if (status) {
      html += fact('Uptime', world === 'running' && status.uptimeMs > 0 ? OpsUI.duration(status.uptimeMs / 1000) : '—');
      html += fact('Process uptime', status.processUptimeMs ? OpsUI.duration(status.processUptimeMs / 1000) : '—');
      html += fact('Players / connections', (status.players || 0) + ' / ' + (status.connections || 0));
      html += fact('Monsters', String(status.monsters || 0));
      html += fact('Cycle delay', world === 'running' ? (status.cycleDelayMs || 0) + ' ms' : '—');
      html += fact('Admin sessions', String(status.sessions || 0));
    }
    if (info) {
      html += fact('Server version', 'Server DB v' + info.serverVersion + (info.customVersion ? ' (custom ' + info.customVersion + ')' : ''));
      html += fact('Database file', info.dbFileVersion != null ? 'v' + info.dbFileVersion + (info.dbFileVersion !== info.serverVersion ? ' (server writes v' + info.serverVersion + ')' : '') : 'missing');
      html += fact('Accounts file', info.accountsFileVersion != null ? 'v' + info.accountsFileVersion : 'missing');
    }
    if (ui.facts) ui.facts.innerHTML = html;

    var note = '';
    if (crashed) note = 'The game loop crashed (see the last error below). Start recovers it; Stop cleans it up.';
    else if (world === 'stopped') note = 'The world is stopped: players cannot connect. Game data edits are saved to disk and load on Start.';
    else if (world === 'starting') note = 'The world is starting (loading the database, maps and scripts)…';
    else if (world === 'stopping') note = 'The world is stopping: players are disconnected and everything is saved…';
    else if (status && status.systemDataStale) note = 'Game data was edited and saved since the world started; the running world keeps the old data until a reboot.';
    if (ui.note) {
      ui.note.textContent = note;
      ui.note.classList.toggle('hidden', !note);
    }

    var err = state && state.lastError;
    if (ui.lastErrorWrap) {
      ui.lastErrorWrap.classList.toggle('hidden', !err);
      if (err) {
        var firstLine = String(err.text || '').split('\n')[0];
        ui.lastErrorHead.textContent = 'Last error (' + OpsUI.ago(err.utc) + '): ' + (firstLine.length > 140 ? firstLine.substring(0, 140) + '…' : firstLine);
        ui.lastErrorHead.title = OpsUI.fmtDate(err.utc);
        if (ui.lastError.textContent !== err.text) ui.lastError.textContent = err.text || '';
      }
    }

    // Buttons per state
    var running = world === 'running' && !crashed;
    var stopped = world === 'stopped';
    setEnabled('start', !actionBusy && !transition && stopped);
    setEnabled('stop', !actionBusy && !transition && (running || crashed));
    setEnabled('reboot', !actionBusy && !transition && (running || crashed));
    setEnabled('save', !actionBusy && running);
    setEnabled('shutdown', !actionBusy);
    setEnabled('cd-arm', !actionBusy && running);
    RELOADS.forEach(function (r) {
      var b = root && root.querySelector('[data-reload="' + r.kind + '"]');
      if (b && !b.getAttribute('aria-busy')) b.disabled = !running;
    });

    renderCountdown();
    renderPending();
  }

  function setEnabled(act, on) {
    var b = ui.btn && ui.btn[act];
    if (!b || b.getAttribute('aria-busy')) return;
    b.disabled = !on;
  }

  function renderCountdown() {
    var c = state && state.countdown;
    var active = !!(c && c.active);
    ui.cdActive.classList.toggle('hidden', !active);
    ui.cdForm.classList.toggle('hidden', active);
    if (!active) return;
    var rem = Math.max(0, Number(c.remainingSec) || 0);
    ui.cdText.textContent = 'Rebooting in ' + Math.floor(rem / 60) + 'm ' + two(rem % 60) + 's' +
      ' (' + c.minutes + ' min countdown armed by ' + (c.admin || '?') + (c.startedUtc ? ', ' + OpsUI.ago(c.startedUtc) : '') + ')' +
      (c.message ? ' — "' + c.message + '"' : '');
  }

  function renderPending() {
    var list = status && Array.isArray(status.pendingReboot) ? status.pendingReboot : [];
    ui.pendingCount.textContent = list.length ? list.length + ' change' + (list.length === 1 ? '' : 's') : '';
    var sig = JSON.stringify(list);
    if (ui.pending._sig === sig) return;
    ui.pending._sig = sig;
    if (!list.length) {
      ui.pending.innerHTML = '<p class="mir-muted">Nothing waiting for a restart.</p>';
      return;
    }
    var rows = list.slice().reverse().map(function (p) {
      return '<tr><td>' + esc(p.area) + '</td><td>' + esc(p.description) + '</td><td>' + esc(p.admin) + '</td>' +
        '<td title="' + esc(OpsUI.fmtDate(p.utc)) + '">' + esc(OpsUI.ago(p.utc)) + '</td></tr>';
    }).join('');
    ui.pending.innerHTML =
      '<p class="mir-muted">Saved game-data changes that apply when the world restarts (Reboot or the countdown).</p>' +
      '<div class="ops-table-scroll srv-pending-scroll"><table class="user-table"><thead><tr><th>Area</th><th>Change</th><th>Admin</th><th>When</th></tr></thead>' +
      '<tbody>' + rows + '</tbody></table></div>';
  }

  function renderInfo() {
    if (!ui.info) return;
    if (!info) { ui.info.innerHTML = '<p class="mir-muted">Server info unavailable.</p>'; return; }
    var s = info.settings || {};
    var p = info.paths || {};
    var w = info.webAdmin || {};
    var html = '<div class="srv-info-cols"><dl class="kv-list">' +
      fact('Server DB version', String(info.serverVersion)) +
      fact('Min. supported DB', String(info.minDbVersion)) +
      fact('Server.MirDB version', info.dbFileVersion != null ? String(info.dbFileVersion) : 'missing') +
      fact('Server.MirADB version', info.accountsFileVersion != null ? String(info.accountsFileVersion) : 'missing') +
      fact('Client version check', s.checkVersion ? 'on (' + (s.versionHashes || 0) + ' hash' + (s.versionHashes === 1 ? '' : 'es') + ')' : 'off') +
      fact('Version path', s.versionPath || '') +
      fact('Game endpoint', (s.gameIp || '') + ':' + (s.gamePort || '')) +
      fact('Web admin', w.url ? w.url + ' (bind ' + w.bind + ':' + w.port + ')' : '—') +
      fact('Legacy HTTP service', s.httpService ? 'on — ' + (s.httpAddress || '') : 'off') +
      fact('Multithreaded', s.multithreaded ? 'yes (' + s.threadLimit + ' threads)' : 'no') +
      fact('Auto-save', 'every ' + (s.saveDelayMinutes || 0) + ' min') +
      fact('Process started', info.processStartUtc ? OpsUI.fmtDate(info.processStartUtc) : '—') +
      '</dl><dl class="kv-list">' +
      fact('Base directory', p.baseDirectory || '') +
      fact('Working directory', p.workingDirectory || '') +
      fact('Database', p.database || '') +
      fact('Accounts', p.accounts || '') +
      fact('Envir', p.envir || '') +
      fact('Backups', p.backups || '') +
      fact('Logs', p.logs || '') +
      fact('Web root', w.webRoot || '') +
      '</dl></div>';
    ui.info.innerHTML = html;
  }

  async function getJson(url) {
    try { return await OpsUI.fetchJson(url); } catch (e) { return null; }
  }

  async function pollStatus() {
    var res = await Promise.all([
      global.AdminStatus ? global.AdminStatus.refresh() : getJson('/api/admin/status'),
      getJson('/api/admin/server/state')
    ]);
    var st = res[0];
    if (st && st.data && st.ok !== undefined) st = st.data;
    status = st || null;
    if (res[1]) state = res[1];
    var world = status ? status.world : null;
    if (world && world !== infoWorld && (world === 'running' || world === 'stopped')) loadInfo();
    renderStatus();
  }

  async function loadInfo() {
    var j = await getJson('/api/admin/server/info');
    if (j) {
      info = j;
      infoWorld = j.world;
    }
    renderInfo();
    renderStatus();
  }

  // After a lifecycle action, look again sooner than the regular poll.
  function followUp() {
    [800, 2500, 6000, 12000].forEach(function (ms) { setTimeout(function () { if (OpsUI.sectionVisible('server')) pollStatus(); }, ms); });
  }

  /* ================================================================= actions */

  async function runAction(fn) {
    actionBusy = true;
    renderStatus();
    try { return await fn(); }
    finally {
      actionBusy = false;
      renderStatus();
      followUp();
    }
  }

  function reasonField() {
    return { key: 'reason', label: 'Reason (audit log)', type: 'text', placeholder: 'optional' };
  }

  async function startWorld() {
    await runAction(function () { return OpsUI.post('/api/admin/server/start', {}); });
  }

  async function stopWorld() {
    var v = await OpsUI.actionDialog({
      title: 'Stop the world?',
      message: 'Everyone is disconnected without warning and the world saves. The admin site stays up; press Start to bring the world back.',
      confirmText: 'Stop world',
      fields: [reasonField()]
    });
    if (!v) return;
    await runAction(function () { return OpsUI.post('/api/admin/server/stop', { reason: v.reason || '' }); });
  }

  async function rebootWorld() {
    var v = await OpsUI.actionDialog({
      title: 'Reboot the world now?',
      message: 'Everyone is disconnected without warning; the world stops, saves and starts again, loading saved game-data changes. ' +
        'The restart countdown warns players first.',
      confirmText: 'Reboot now',
      fields: [reasonField()]
    });
    if (!v) return;
    await runAction(function () { return OpsUI.post('/api/admin/server/reboot', { reason: v.reason || '' }); });
  }

  async function saveNow() {
    await runAction(function () { return OpsUI.post('/api/admin/server/save', {}); });
  }

  async function shutdown() {
    var first = await global.portalConfirm('Shut down Server.exe?\n\nThe world stops (everyone is disconnected, everything is saved) and the process exits, ' +
      'taking this admin site with it. Nothing restarts it: someone must start Server.exe on the host again.',
      { title: 'Shut down Server.exe', confirmLabel: 'Continue', danger: true });
    if (!first) return;
    var v = await OpsUI.actionDialog({
      title: 'Really shut down Server.exe?',
      message: 'Type SHUTDOWN to confirm. This admin site will not come back by itself.',
      confirmText: 'Shut down',
      fields: [
        { key: 'confirm', label: 'Confirmation', type: 'text', placeholder: 'SHUTDOWN', required: true },
        reasonField()
      ]
    });
    if (!v) return;
    if (String(v.confirm || '').trim().toUpperCase() !== 'SHUTDOWN') {
      global.portalToast('Not shut down: the confirmation text did not match.', 'error');
      return;
    }
    var j = await runAction(function () { return OpsUI.post('/api/admin/server/shutdown', { confirm: 'SHUTDOWN', reason: v.reason || '' }); });
    if (j && j.__ok) {
      ui.note.textContent = 'Server.exe is shutting down. This page stops working when the process exits.';
      ui.note.classList.remove('hidden');
    }
  }

  async function reload(kind) {
    var meta = null;
    RELOADS.forEach(function (r) { if (r.kind === kind) meta = r; });
    if (!meta) return;
    var ok = await global.portalConfirm('Reload ' + meta.label.toLowerCase() + ' into the running world?\n\n' + meta.hint, { title: 'Reload ' + meta.label, confirmLabel: 'Reload' });
    if (!ok) return;
    await OpsUI.post('/api/admin/server/reload/' + encodeURIComponent(kind), {});
  }

  async function armCountdown() {
    var minutes = Number(ui.cdMinutes.value);
    if (!minutes || minutes < 1 || minutes > 60 || Math.floor(minutes) !== minutes) {
      global.portalToast('Enter a whole number of minutes between 1 and 60.', 'error');
      return;
    }
    var message = String(ui.cdMessage.value || '').trim();
    var v = await OpsUI.actionDialog({
      title: 'Restart in ' + minutes + ' minute' + (minutes === 1 ? '' : 's') + '?',
      message: 'Players are warned in game at each mark' + (message ? ' ("' + message + '")' : '') + ', then the world reboots. You can cancel until then.',
      confirmText: 'Start countdown',
      fields: [reasonField()]
    });
    if (!v) return;
    var j = await OpsUI.post('/api/admin/server/restart-countdown', { minutes: minutes, message: message, reason: v.reason || '' });
    if (j && j.__ok) {
      ui.cdMessage.value = '';
      if (j.data) state = Object.assign({}, state || {}, { countdown: j.data });
      renderStatus();
    }
    followUp();
  }

  async function cancelCountdown() {
    var ok = await global.portalConfirm('Cancel the restart countdown? Players are told the restart was cancelled.', { title: 'Cancel restart', confirmLabel: 'Cancel restart' });
    if (!ok) return;
    var j = await OpsUI.post('/api/admin/server/restart-countdown/cancel', {});
    if (j && j.__ok) {
      state = Object.assign({}, state || {}, { countdown: { active: false } });
      renderStatus();
    }
    followUp();
  }

  /* ================================================================= logs */

  function markLogTab() {
    ui.logTabs.querySelectorAll('[data-log-kind]').forEach(function (b) {
      var on = b.getAttribute('data-log-kind') === logTab;
      b.classList.toggle('active', on);
      b.setAttribute('aria-selected', on ? 'true' : 'false');
    });
    ui.files.classList.toggle('hidden', logTab !== 'files');
    ui.logPause.parentNode.style.display = logTab === 'files' ? 'none' : '';
  }

  function selectLogTab(tab) {
    if (tab !== 'files' && LOG_KINDS.indexOf(tab) < 0) return;
    logTab = tab;
    lsSet(LS_TAB, tab);
    markLogTab();
    renderLog(true, true);
    if (tab === 'files') loadFiles();
    else pollLog(true);
  }

  function lineText(l) {
    var d = OpsUI.toDate(l.utc);
    var stamp = d ? two(d.getHours()) + ':' + two(d.getMinutes()) + ':' + two(d.getSeconds()) : '--:--:--';
    return '[' + stamp + '] ' + (l.text == null ? '' : l.text);
  }

  function filterText() { return String(ui.logFilter.value || '').trim().toLowerCase(); }

  function currentLines() {
    if (logTab === 'files') return fileView ? fileView.lines : [];
    var s = live[logTab];
    return s ? s.lines.map(lineText) : [];
  }

  function trimLive(s) {
    if (s.lines.length > logCap) s.lines.splice(0, s.lines.length - logCap);
  }

  function atBottom(pre) {
    return pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 8;
  }

  function matchesFilter(f) {
    return function (t) { return t.toLowerCase().indexOf(f) >= 0; };
  }

  /* One text node per shown line ("\n" + text for every line but the first), so the oldest lines can be dropped cheaply. */
  function appendLineNodes(pre, lines) {
    var frag = document.createDocumentFragment();
    for (var i = 0; i < lines.length; i++) {
      frag.appendChild(document.createTextNode(renderedCount || i ? '\n' + lines[i] : lines[i]));
    }
    pre.appendChild(frag);
  }

  /*
   * full = rebuild the <pre>; otherwise drop the oldest `removed` lines (raw texts trimmed off by the line cap) and append the
   * newest `added` lines (append-only view). The scroll position is kept (compensating for the height of the dropped lines)
   * unless the view was already at the bottom (then it follows the tail).
   */
  function renderLog(full, toBottom, added, removed) {
    var pre = ui.logView;
    if (!pre) return;
    var follow = toBottom || atBottom(pre);
    var keep = pre.scrollTop;
    var f = filterText();

    if (full || added == null) {
      var lines = currentLines();
      var shown = f ? lines.filter(matchesFilter(f)) : lines;
      renderedCount = 0;
      if (logTab === 'files' && !fileView) pre.textContent = 'Pick a file above.';
      else if (!shown.length) pre.textContent = lines.length ? '(no line matches the filter)' : '(empty)';
      else {
        pre.textContent = '';
        appendLineNodes(pre, shown);
        renderedCount = shown.length;
      }
    } else {
      var drop = removed && removed.length ? (f ? removed.filter(matchesFilter(f)).length : removed.length) : 0;
      drop = Math.min(drop, renderedCount, pre.childNodes.length);
      if (drop > 0) {
        var h0 = pre.scrollHeight;
        for (var i = 0; i < drop; i++) pre.removeChild(pre.firstChild);
        renderedCount -= drop;
        var head = pre.firstChild;
        if (head && head.nodeType === 3 && head.nodeValue.charAt(0) === '\n') head.nodeValue = head.nodeValue.slice(1);
        keep = Math.max(0, keep - (h0 - pre.scrollHeight));
      }
      var addShown = f ? added.filter(matchesFilter(f)) : added;
      if (addShown.length) {
        if (!renderedCount) pre.textContent = '';
        appendLineNodes(pre, addShown);
        renderedCount += addShown.length;
      } else if (!renderedCount && drop > 0) {
        pre.textContent = currentLines().length ? '(no line matches the filter)' : '(empty)';
      }
    }

    if (follow) pre.scrollTop = pre.scrollHeight;
    else pre.scrollTop = keep;
    updateLogMeta();
  }

  function updateLogMeta() {
    if (!ui.logMeta) return;
    if (logTab === 'files') {
      ui.logMeta.textContent = fileView
        ? fileView.kind + ' / ' + fileView.name + ' — ' + fileView.lines.length.toLocaleString() + ' line(s) shown, file ' + fmtBytes(fileView.size) +
          (fileView.truncated ? ' (older lines not shown; raise "Keep" or download the file)' : '')
        : '';
      return;
    }
    var s = live[logTab];
    ui.logMeta.textContent = (s ? s.lines.length.toLocaleString() : '0') + ' line(s) in view (the server keeps the last 2,000 per log since Server.exe started)' +
      (paused ? ' · paused' : '');
  }

  /*
   * One request per log kind at a time: the periodic poll, a tab switch, un-pausing and show() can overlap, and two in-flight
   * requests with the same `after` would append the same lines twice. A forced call made while one is in flight runs after it.
   */
  function pollLog(force) {
    if (logTab === 'files') return Promise.resolve();
    if (paused && !force) return Promise.resolve();
    var kind = logTab;
    var s = live[kind] || (live[kind] = { lines: [], last: 0 });
    if (s.inflight) {
      if (!force) return s.inflight;
      return s.inflight.then(function () { return kind === logTab ? pollLog(true) : undefined; });
    }
    var p = pollLogOnce(kind, s);
    s.inflight = p;
    var done = function () { if (s.inflight === p) s.inflight = null; };
    p.then(done, done);
    return p;
  }

  async function pollLogOnce(kind, s) {
    var j = await getJson('/api/admin/logs/' + encodeURIComponent(kind) + '?after=' + s.last + '&max=2000');
    if (!j || kind !== logTab) return;
    var incoming = j.lines || [];
    // The server restarted (sequence went backwards): start this log over.
    if (typeof j.last === 'number' && j.last < s.last) {
      s.lines = [];
      s.last = 0;
      j = await getJson('/api/admin/logs/' + encodeURIComponent(kind) + '?after=0&max=2000');
      if (!j || kind !== logTab) return;
      incoming = j.lines || [];
      s.lines = incoming.slice();
      s.last = j.last || 0;
      trimLive(s);
      renderLog(true, true);
      return;
    }
    // Never append a line twice (belt and braces next to the in-flight guard).
    var after = s.last;
    incoming = incoming.filter(function (l) { return typeof l.seq !== 'number' || l.seq > after; });
    if (!incoming.length) { s.last = Math.max(s.last, j.last || 0); updateLogMeta(); return; }
    var first = s.last === 0 && !s.lines.length;
    var prevCount = s.lines.length;
    Array.prototype.push.apply(s.lines, incoming);
    s.last = Math.max(s.last, j.last || incoming[incoming.length - 1].seq || 0);
    var overflow = Math.max(0, s.lines.length - logCap);
    var dropped = overflow > 0 && overflow < prevCount ? s.lines.slice(0, overflow).map(lineText) : null;
    trimLive(s);
    // Rebuild only when the whole previous view is gone; otherwise drop the oldest lines and append the new ones.
    if (first || (overflow > 0 && !dropped)) renderLog(true, first);
    else renderLog(false, false, incoming.map(lineText), dropped);
  }

  async function loadFiles() {
    ui.files.innerHTML = '<p class="mir-muted">Loading…</p>';
    var j = await getJson('/api/admin/logs/files');
    if (!j) { ui.files.innerHTML = '<p class="mir-muted">Could not list the log files.</p>'; return; }
    var rows = '';
    (j.kinds || []).forEach(function (k) {
      (k.files || []).slice(0, 60).forEach(function (f) {
        var dl = '/api/admin/logs/download?kind=' + encodeURIComponent(k.kind) + '&name=' + encodeURIComponent(f.name);
        rows += '<tr><td>' + esc(k.folder) + '</td>' +
          '<td><a href="#" class="ops-link" data-kind="' + esc(k.kind) + '" data-file="' + esc(f.name) + '">' + esc(f.name) + '</a></td>' +
          '<td class="srv-num">' + esc(fmtBytes(f.size)) + '</td>' +
          '<td title="' + esc(OpsUI.fmtDate(f.modifiedUtc)) + '">' + esc(OpsUI.ago(f.modifiedUtc)) + '</td>' +
          '<td><a class="mir-btn ops-row-btn" href="' + esc(dl) + '" download="' + esc(f.name) + '">Download</a></td></tr>';
      });
    });
    ui.files.innerHTML = rows
      ? '<p class="mir-muted">log4net files under ' + esc(j.root || 'Logs') + ' (newest first). Click a name to view its tail.</p>' +
        '<div class="ops-table-scroll srv-files-scroll"><table class="user-table"><thead><tr><th>Log</th><th>File</th><th>Size</th><th>Modified</th><th></th></tr></thead><tbody>' +
        rows + '</tbody></table></div>'
      : '<p class="mir-muted">No log files yet under ' + esc(j.root || 'Logs') + '.</p>';
  }

  async function openFile(kind, name) {
    if (!kind || !name) return;
    ui.logView.textContent = 'Loading ' + name + '…';
    var url = '/api/admin/logs/file?kind=' + encodeURIComponent(kind) + '&name=' + encodeURIComponent(name) + '&lines=' + logCap;
    try {
      var j = await OpsUI.fetchJson(url);
      fileView = { kind: kind, name: j.name || name, lines: j.lines || [], size: j.size || 0, truncated: !!j.truncated };
    } catch (e) {
      fileView = null;
      global.portalToast('Could not read ' + name + ': ' + ((e && e.message) || e), 'error');
    }
    if (logTab === 'files') renderLog(true, true);
  }

  function downloadLog() {
    var stamp = new Date().toISOString().replace(/[:T]/g, '-').slice(0, 19);
    if (logTab === 'files') {
      if (!fileView) { global.portalToast('Pick a file first.', 'error'); return; }
      var a = document.createElement('a');
      a.href = '/api/admin/logs/download?kind=' + encodeURIComponent(fileView.kind) + '&name=' + encodeURIComponent(fileView.name);
      a.download = fileView.name;
      document.body.appendChild(a);
      a.click();
      setTimeout(function () { if (a.parentNode) a.parentNode.removeChild(a); }, 0);
      return;
    }
    var lines = currentLines();
    var f = filterText();
    if (f) lines = lines.filter(function (t) { return t.toLowerCase().indexOf(f) >= 0; });
    OpsUI.download('mircraft-' + logTab + '-log-' + stamp + '.txt', lines.join('\r\n'));
  }

  /* ================================================================= section */

  function startPolling() {
    if (stops.length) return;
    stops.push(global.AdminSections.poll('server', pollStatus, STATUS_MS));
    stops.push(global.AdminSections.poll('server', function () { return pollLog(false); }, LOG_MS));
    stops.push(global.AdminSections.poll('server', function () {
      var c = state && state.countdown;
      if (c && c.active && c.remainingSec > 0) { c.remainingSec -= 1; renderCountdown(); }
    }, 1000));
  }

  global.AdminSections.register('server', {
    load: function (el) {
      root = el;
      build(el);
      renderStatus();
    },
    show: function () {
      startPolling();
      var work = [pollStatus(), loadInfo()];
      if (logTab === 'files') work.push(loadFiles());
      else work.push(pollLog(true));
      return Promise.all(work);
    },
    hide: function () {
      stops.forEach(function (stop) { stop(); });
      stops = [];
    }
  });

  // #section=server&log=server|debug|chat|files opens that log tab.
  if (typeof global.portalRegisterDeepLink === 'function') {
    global.portalRegisterDeepLink('server', function (params) {
      var tab = params && params.log ? String(params.log).toLowerCase() : '';
      if (!root || !tab || tab === logTab) return;
      if (tab === 'files' || LOG_KINDS.indexOf(tab) >= 0) {
        selectLogTab(tab);
        if (ui.logView && ui.logView.scrollIntoView) ui.logView.scrollIntoView({ block: 'nearest' });
      }
    });
  }
})(window);
