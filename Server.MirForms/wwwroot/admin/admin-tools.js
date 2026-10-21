/*
 * Tools (#section-tools, Monitoring group): the StressClient bot fleet and the scratch server it can play on.
 *
 *   GET  /api/admin/tools/status                 {repo, bots{state, config, status, history, log, ...}, build, scratch, self, maxBots}
 *   POST /api/admin/tools/bots/start  {target: 'scratch'|'self', bots, ramp, durationMinutes, prefix, firstIndex, password, class,
 *                                      hunt, loot, town, quests, potions, equip, chat, party, director, run, reconnect,
 *                                      runPercent, seekRange, roamRadius, workers, connectBudget}
 *   POST /api/admin/tools/bots/stop | /bots/build
 *   POST /api/admin/tools/scratch/start {port, maps, reset, rebuild, copyLiveMaps} | /scratch/stop
 *
 * The bots run as a StressClient.exe child of Server.exe and report one "@status" line per second; this page polls the
 * server's copy every 5 s. Targeting THIS server needs the load-test settings (shown as checks) and a confirm.
 * The form (minus the password) is remembered in localStorage (mircraft_tools_bots).
 */
(function (global) {
  'use strict';

  var esc = function (s) { return OpsUI.esc(s); };
  var STORE_KEY = 'mircraft_tools_bots';
  var SCRATCH_KEY = 'mircraft_tools_scratch';

  var BEHAVIOURS = [
    ['hunt', 'Hunt monsters', 'Chase and attack monsters near the bot.'],
    ['loot', 'Pick up loot', 'Walk to items and gold within 8 cells.'],
    ['town', 'Town runs', 'Walk to the merchant to sell junk, buy potions and repair.'],
    ['quests', 'Quests', 'Accept offered quests at the merchant and hand them in.'],
    ['potions', 'Drink potions', 'Use an HP potion below 55 % HP.'],
    ['equip', 'Equip upgrades', 'Wear better weapons and armour from the bag.'],
    ['chat', 'Chat', 'Say something in local chat now and then.'],
    ['party', 'Party', 'Invite nearby players and accept invites.'],
    ['director', 'Director routing', 'Send idle bots toward monster spawns and map exits.'],
    ['run', 'Run', 'Run on straight path segments (else walk only).'],
    ['reconnect', 'Reconnect', 'Log back in after a disconnect.']
  ];

  var PRESETS = {
    full: { label: 'Full players', on: ['hunt', 'loot', 'town', 'quests', 'potions', 'equip', 'chat', 'party', 'director', 'run', 'reconnect'] },
    fighters: { label: 'Fighters', on: ['hunt', 'loot', 'potions', 'equip', 'director', 'run', 'reconnect'] },
    walkers: { label: 'Walkers only', on: ['director', 'run', 'reconnect'] },
    social: { label: 'Social', on: ['chat', 'party', 'director', 'reconnect'] }
  };

  var DEFAULTS = {
    target: 'scratch', bots: 20, ramp: 5, durationMinutes: 0, prefix: 'wb', firstIndex: 0, password: '', 'class': 'warrior',
    runPercent: 40, seekRange: 8, roamRadius: 12, workers: 4, connectBudget: 25
  };
  BEHAVIOURS.forEach(function (b) { DEFAULTS[b[0]] = true; });

  var root = null;
  var state = null;
  var stopPoll = null;
  var chart = null;

  function el(role) { return root ? root.querySelector('[data-role="' + role + '"]') : null; }
  function field(name) { return root ? root.querySelector('[data-field="' + name + '"]') : null; }

  function loadForm() {
    var saved = {};
    try { saved = JSON.parse(localStorage.getItem(STORE_KEY) || '{}') || {}; } catch (e) { saved = {}; }
    var f = Object.assign({}, DEFAULTS, saved);
    Object.keys(DEFAULTS).forEach(function (k) {
      var input = field(k);
      if (!input || k === 'password') return;
      if (input.type === 'checkbox') input.checked = !!f[k]; else input.value = f[k];
    });
    var s = {};
    try { s = JSON.parse(localStorage.getItem(SCRATCH_KEY) || '{}') || {}; } catch (e) { s = {}; }
    if (s.port) el('scratch-port').value = s.port;
    if (s.maps) el('scratch-maps').value = s.maps;
    if (s.world) el('scratch-world').value = s.world;
  }

  /* The form as the start route expects it (pure apart from reading the inputs). */
  function readForm() {
    var body = {};
    Object.keys(DEFAULTS).forEach(function (k) {
      var input = field(k);
      if (!input) return;
      if (input.type === 'checkbox') body[k] = !!input.checked;
      else if (input.type === 'number') body[k] = Number(input.value);
      else body[k] = input.value.trim();
    });
    if (!body.password) body.password = body.target === 'self' ? '' : 'StressPw1';
    return body;
  }

  function saveForm(body) {
    var copy = Object.assign({}, body);
    delete copy.password;
    try { localStorage.setItem(STORE_KEY, JSON.stringify(copy)); } catch (e) { /* storage off */ }
  }

  function applyPreset(key) {
    var p = PRESETS[key];
    if (!p) return;
    BEHAVIOURS.forEach(function (b) { var i = field(b[0]); if (i) i.checked = p.on.indexOf(b[0]) >= 0; });
  }

  /* The start confirmation (pure). */
  function startWarning(body) {
    var lines = ['Start ' + body.bots + ' bots (' + body.ramp + '/s) for ' + (body.durationMinutes > 0 ? body.durationMinutes + ' min' : 'until stopped') + '?', ''];
    if (body.target === 'self') {
      lines.push('Target: THIS server. The bots log into the live world as real players:');
      lines.push('- accounts ' + body.prefix + String(body.firstIndex).padStart(5, '0') + '... are created in this server\'s account database;');
      lines.push('- they show up in Players Online, rankings and the Live Monitor, and take player slots.');
    } else {
      lines.push('Target: the scratch server.');
    }
    return lines.join('\n');
  }

  // ------------------------------------------------------------------ render

  function fmt(n, digits) {
    if (n === null || n === undefined || isNaN(n)) return '–';
    return Number(n).toLocaleString(undefined, { maximumFractionDigits: digits === undefined ? 0 : digits });
  }

  function tile(label, value, sub, tone) {
    return '<div class="tl-tile' + (tone ? ' tl-' + tone : '') + '"><div class="tl-tile-label">' + esc(label) + '</div>' +
      '<div class="tl-tile-value">' + esc(value) + '</div>' + (sub ? '<div class="tl-tile-sub">' + esc(sub) + '</div>' : '') + '</div>';
  }

  function renderBots() {
    var b = state && state.bots;
    if (!b) return;
    var running = b.state === 'running';

    var pill = el('bots-pill');
    if (running) pill.innerHTML = '<span class="ops-pill ops-pill-ok">Running</span> <span class="mir-muted">' + esc((b.config && b.config.target === 'self') ? 'on this server' : 'on the scratch server') +
      ' · since ' + esc(OpsUI.fmtDate(b.startedUtc)) + (b.by ? ' · by ' + esc(b.by) : '') + '</span>';
    else if (b.state === 'exited') pill.innerHTML = '<span class="ops-pill ' + (b.exitCode === 0 ? 'ops-pill-ok' : 'ops-pill-warn') + '">Finished (exit ' + esc(b.exitCode) + ')</span> <span class="mir-muted">' + esc(OpsUI.fmtDate(b.exitedUtc)) + '</span>';
    else pill.innerHTML = '<span class="ops-pill">Idle</span>';

    el('bots-start').disabled = running || !b.exeFound;
    el('bots-stop').disabled = !running;
    el('bots-build').disabled = running || !!(state.build && state.build.running);

    var exe = el('bots-exe');
    if (!state.repo.found) exe.innerHTML = '<span class="tl-warning">No Crystal source folder found. Set <b>[WebAdmin] RepoPath</b> in Server Config (WebAdmin group) and restart Server.exe.</span>';
    else if (!b.exeFound) exe.innerHTML = '<span class="tl-warning">StressClient is not built yet. Click <b>Build bots</b>.</span>';
    else exe.textContent = 'StressClient built ' + OpsUI.fmtDate(b.exeBuiltUtc) + ' · ' + b.exe;

    var s = b.status;
    var tiles = el('bots-tiles');
    if (!s) {
      tiles.innerHTML = running ? '<span class="mir-muted">Waiting for the first report…</span>' : '';
    } else {
      var problems = (s.errors || 0) + (s.decodeErrors || 0) + (s.refused || 0) + (s.loginTimeouts || 0);
      tiles.innerHTML =
        tile('In game', fmt(s.inGame) + ' / ' + fmt(s.bots), 'reached ' + fmt(s.reached) + ' · connecting ' + fmt(s.connecting)) +
        tile('Packets/s', fmt(s.sentPps) + ' out', fmt(s.recvPps) + ' in · ' + fmt(s.recvKBps, 1) + ' KB/s in') +
        tile('Movement/s', fmt(s.walkPs) + ' walk', fmt(s.runPs) + ' run · ' + fmt(s.blockedPs) + ' blocked') +
        tile('Combat', fmt(s.kills) + ' kills', fmt(s.attackPs) + ' atk/s · ' + fmt(s.deaths) + ' deaths') +
        tile('Economy', fmt(s.sold) + ' sold', fmt(s.bought) + ' bought · ' + fmt(s.pickups) + ' pickups · ' + fmt(s.potions) + ' potions') +
        tile('Quests / social', fmt(s.questsDone) + ' quests done', fmt(s.chats) + ' chats · ' + fmt(s.groups) + ' groups') +
        tile('Latency', s.rttMs === undefined ? '–' : fmt(s.rttMs, 1) + ' ms', s.silent ? s.silent + ' bot(s) silent > 5 s' : 'KeepAlive round trip', s.silent ? 'warn' : '') +
        tile('Problems', fmt(problems), 'err ' + fmt(s.errors) + ' · decode ' + fmt(s.decodeErrors) + ' · refused ' + fmt(s.refused) + ' · disc ' + fmt(s.disconnects), problems ? 'warn' : '');
    }

    drawChart(b.history || []);

    var cfg = el('bots-config');
    if (b.config && (running || b.state === 'exited')) {
      var c = b.config;
      var on = BEHAVIOURS.filter(function (x) { return c[x[0]]; }).map(function (x) { return x[1]; });
      cfg.textContent = c.bots + ' bots · ' + c.accounts + ' · ' + c['class'] + ' · ' + (on.length ? on.join(', ') : 'roam only');
    } else cfg.textContent = '';

    setLog('bots-log', b.log);
  }

  function setLog(role, lines) {
    var pre = el(role);
    if (!pre) return;
    var stick = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 8;
    pre.textContent = (lines || []).join('\n');
    if (stick) pre.scrollTop = pre.scrollHeight;
  }

  function drawChart(history) {
    if (typeof global.Chart === 'undefined') return;
    var canvas = el('bots-chart');
    if (!canvas) return;
    if (!chart) {
      var css = getComputedStyle(document.documentElement);
      var accent = (css.getPropertyValue('--accent') || '#ff7a1f').trim();
      var ok = (css.getPropertyValue('--ok') || '#5fd38a').trim();
      var muted = (css.getPropertyValue('--muted') || '#8a7f70').trim();
      chart = new global.Chart(canvas.getContext('2d'), {
        type: 'line',
        data: { labels: [], datasets: [
          { label: 'In game', data: [], borderColor: accent, pointRadius: 0, borderWidth: 2, yAxisID: 'y', stepped: true },
          { label: 'Packets/s in', data: [], borderColor: ok, pointRadius: 0, borderWidth: 1.5, yAxisID: 'y1', tension: 0.25 }
        ] },
        options: {
          animation: false, responsive: true, maintainAspectRatio: false,
          plugins: { legend: { labels: { color: muted } } },
          scales: {
            x: { ticks: { color: muted, maxTicksLimit: 8 }, grid: { display: false } },
            y: { position: 'left', beginAtZero: true, ticks: { color: muted, precision: 0 } },
            y1: { position: 'right', beginAtZero: true, ticks: { color: muted }, grid: { drawOnChartArea: false } }
          }
        }
      });
    }
    chart.data.labels = history.map(function (h) { return Math.round(h.t) + 's'; });
    chart.data.datasets[0].data = history.map(function (h) { return h.inGame; });
    chart.data.datasets[1].data = history.map(function (h) { return h.recvPps; });
    chart.update('none');
  }

  /* With "This server": a line when the chosen class is disabled there (mixed falls back to Warrior/Wizard/Taoist). */
  function classCheck() {
    var cls = field('class').value;
    var off = [];
    if (!state.self.allowAssassin) off.push('Assassin');
    if (!state.self.allowArcher) off.push('Archer');
    if (!off.length || ['assassin', 'archer', 'mixed'].indexOf(cls) < 0) return '';
    var blocked = cls === 'mixed' ? false : off.indexOf(cls.charAt(0).toUpperCase() + cls.slice(1)) >= 0;
    if (cls !== 'mixed' && !blocked) return '';
    var text = blocked
      ? 'This server does not allow new ' + cls + ' characters; pick another class.'
      : 'New ' + off.join(' and ') + ' characters are disabled here: mixed bots use Warrior, Wizard and Taoist instead.';
    return '<li class="' + (blocked ? 'tl-bad' : 'tl-warnc') + '"><span class="tl-mark">' + (blocked ? '✗' : '!') + '</span> <b>Class</b> <span class="mir-muted">' + esc(text) + '</span></li>';
  }

  function renderTarget() {
    if (!state) return;
    var self = field('target').value === 'self';
    var host = el('self-checks');
    host.classList.toggle('hidden', !self);
    el('target-note').textContent = self
      ? 'The bots log into THIS server (port ' + state.self.port + ') as ordinary players. Use your own bot password.'
      : (state.scratch.running ? 'The bots play on the scratch server (port ' + state.scratch.port + ').' : 'Start the scratch server below first.');
    if (self) {
      var bots = Number(field('bots').value) || 0;
      var capOk = bots <= state.self.maxUser;
      var cap = '<li class="' + (capOk ? 'tl-ok' : 'tl-bad') + '"><span class="tl-mark">' + (capOk ? '✓' : '✗') + '</span> <b>' +
        esc(bots + ' bots ≤ [Network] MaxUser = ' + state.self.maxUser) + '</b> <span class="mir-muted">' +
        esc('The server refuses connections above MaxUser (players included); raise it in Server Config for bigger runs.') + '</span></li>';
      host.innerHTML = '<ul class="tl-checks">' + state.self.checks.map(function (c) {
        var cls = c.ok ? 'tl-ok' : (c.required ? 'tl-bad' : 'tl-warnc');
        return '<li class="' + cls + '"><span class="tl-mark">' + (c.ok ? '✓' : (c.required ? '✗' : '!')) + '</span> <b>' + esc(c.label) + '</b> <span class="mir-muted">' + esc(c.detail) + '</span></li>';
      }).join('') + cap + classCheck() + '</ul>' +
        (state.self.ok ? '' : '<p class="tl-warning">Turn the missing settings on in Server Config (Network / General / Permissions) before starting bots here.</p>');
    }
    field('password').placeholder = self ? 'required: your own bot password' : 'StressPw1 (default)';
  }

  function renderScratch() {
    var s = state && state.scratch;
    if (!s) return;
    var op = s.op;
    var busy = !!(op && op.running);
    var pill = el('scratch-pill');
    if (busy) pill.innerHTML = '<span class="ops-pill ops-pill-warn">' + (op.kind === 'scratch-start' ? 'Starting…' : 'Stopping…') + '</span>';
    else if (s.running) pill.innerHTML = '<span class="ops-pill ops-pill-ok">Running</span> <span class="mir-muted">127.0.0.1:' + esc(s.port) + ' · PID ' + esc(s.pid) + ' · ' + esc(s.mapFiles) + ' map file(s)</span>';
    else pill.innerHTML = '<span class="ops-pill">Stopped</span>' + (s.worldExists ? ' <span class="mir-muted">world kept: ' + esc(s.mapFiles) + ' map file(s)</span>' : '');

    if (s.running && s.port) el('scratch-port').value = s.port;
    el('scratch-start').disabled = busy || s.running || !state.repo.found;
    el('scratch-stop').disabled = busy || !s.running;
    el('scratch-info').textContent = 'Folder ' + s.folder + (s.serverBuilt ? '' : ' · first start builds Server.exe (takes a few minutes)');
    setLog('scratch-log', op ? op.log : []);

    var bl = state.build;
    el('build-row').classList.toggle('hidden', !bl);
    if (bl) {
      el('build-pill').innerHTML = bl.running ? '<span class="ops-pill ops-pill-warn">Building…</span>'
        : '<span class="ops-pill ' + (bl.exitCode === 0 ? 'ops-pill-ok' : 'ops-pill-danger') + '">Build ' + (bl.exitCode === 0 ? 'succeeded' : 'failed') + '</span>';
      setLog('build-log', bl.log);
    }
  }

  function apply(s) {
    if (!s) return;
    state = s;
    renderBots();
    renderTarget();
    renderScratch();
  }

  async function refresh() {
    var r = await OpsUI.fetchResult('/api/admin/tools/status');
    if (!r.ok) {
      var p = el('bots-pill');
      if (p) p.innerHTML = '<span class="ops-pill ops-pill-warn">' + esc('Could not load the tools state: ' + r.error) + '</span>';
      return;
    }
    apply(r.data);
  }

  // ------------------------------------------------------------------ actions

  async function startBots(button) {
    var body = readForm();
    if (body.target === 'self' && !body.password) { portalToast('Enter your own bot password for this server.', 'error'); return; }
    saveForm(body);
    if (!await portalConfirm(startWarning(body), { title: 'Start bots', confirmLabel: 'Start', danger: body.target === 'self' })) return;
    var j = await OpsUI.withBusy(button, function () { return OpsUI.post('/api/admin/tools/bots/start', body); }, 'Starting…');
    if (j && j.data) apply(j.data); else refresh();
  }

  async function stopBots(button) {
    var j = await OpsUI.withBusy(button, function () { return OpsUI.post('/api/admin/tools/bots/stop', {}); }, 'Stopping…');
    if (j && j.data) apply(j.data); else refresh();
  }

  async function buildBots(button) {
    var j = await OpsUI.withBusy(button, function () { return OpsUI.post('/api/admin/tools/bots/build', {}); }, 'Starting build…');
    if (j && j.data) apply(j.data); else refresh();
  }

  async function startScratch(button) {
    var body = {
      port: Number(el('scratch-port').value) || 17100,
      maps: Number(el('scratch-maps').value) || 1,
      copyLiveMaps: el('scratch-world').value === 'live',
      reset: !!el('scratch-reset').checked,
      rebuild: !!el('scratch-rebuild').checked
    };
    try { localStorage.setItem(SCRATCH_KEY, JSON.stringify({ port: body.port, maps: body.maps, world: el('scratch-world').value })); } catch (e) { /* storage off */ }
    if (body.reset && !await portalConfirm('Reset the scratch world?\n\nIts maps, accounts and logs are deleted and a new world is generated.', { title: 'Reset scratch world', confirmLabel: 'Reset and start', danger: true })) return;
    var j = await OpsUI.withBusy(button, function () { return OpsUI.post('/api/admin/tools/scratch/start', body); }, 'Starting…');
    if (j && j.data) apply(j.data); else refresh();
  }

  async function stopScratch(button) {
    var j = await OpsUI.withBusy(button, function () { return OpsUI.post('/api/admin/tools/scratch/stop', {}); }, 'Stopping…');
    if (j && j.data) apply(j.data); else refresh();
  }

  // ------------------------------------------------------------------ layout

  function num(name, label, min, max, step, hint) {
    return '<label title="' + esc(hint || '') + '">' + esc(label) + '<input type="number" class="fd-input" data-field="' + name + '" min="' + min + '" max="' + max + '" step="' + (step || 1) + '" /></label>';
  }

  function render(sectionEl) {
    sectionEl.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Stress bots</h2><div class="tl-actions">' +
      '    <button type="button" class="mir-btn" data-role="refresh">Refresh</button>' +
      '    <button type="button" class="mir-btn" data-role="bots-build">Build bots</button>' +
      '    <button type="button" class="mir-btn danger" data-role="bots-stop">Stop bots</button>' +
      '    <button type="button" class="mir-btn primary" data-role="bots-start">Start bots…</button>' +
      '  </div></div>' +
      '  <div data-role="bots-pill"></div>' +
      '  <p class="mir-muted tl-small" data-role="bots-exe"></p>' +
      '  <p class="mir-muted tl-small" data-role="bots-config"></p>' +
      '  <div class="tl-tiles" data-role="bots-tiles"></div>' +
      '  <div class="tl-chart"><canvas data-role="bots-chart"></canvas></div>' +
      '  <details class="tl-logbox"><summary>Bot output</summary><pre class="tl-log" data-role="bots-log"></pre></details>' +
      '</div>' +

      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Bot run settings</h2></div>' +
      '  <div class="ops-form">' +
      '    <label>Target<select class="fd-input" data-field="target"><option value="scratch">Scratch server</option><option value="self">This server</option></select></label>' +
      '    <span class="mir-muted tl-small" data-role="target-note"></span>' +
      '  </div>' +
      '  <div data-role="self-checks" class="hidden"></div>' +
      '  <div class="ops-form">' +
      num('bots', 'Bots', 1, 5000, 1, 'How many bots to run (up to 5000).') +
      num('ramp', 'Ramp (bots/s)', 0.1, 200, 0.1, 'Bots released per second.') +
      num('durationMinutes', 'Duration (min, 0 = until stopped)', 0, 1440, 1, '') +
      '    <label class="tl-narrow">Class<select class="fd-input" data-field="class">' +
      ['warrior', 'wizard', 'taoist', 'assassin', 'archer', 'mixed'].map(function (c) { return '<option value="' + c + '">' + c.charAt(0).toUpperCase() + c.slice(1) + '</option>'; }).join('') +
      '    </select></label>' +
      '    <label>Account prefix<input type="text" class="fd-input" data-field="prefix" maxlength="9" /></label>' +
      num('firstIndex', 'First index', 0, 99999, 1, 'Accounts are prefix + 5 digits, starting here.') +
      '    <label>Bot password<input type="password" class="fd-input" data-field="password" maxlength="15" autocomplete="new-password" /></label>' +
      '  </div>' +
      '  <p class="mir-muted tl-small">New characters get the class above; bot accounts that already exist keep their characters. Bots fight with plain attacks whatever their class.</p>' +
      '  <div class="toolbar tl-subhead"><h3>What the bots do</h3><span class="tl-presets" data-role="presets">' +
      Object.keys(PRESETS).map(function (k) { return '<button type="button" class="mir-btn ops-row-btn" data-preset="' + k + '">' + esc(PRESETS[k].label) + '</button>'; }).join('') +
      '  </span></div>' +
      '  <div class="tl-behaviours">' +
      BEHAVIOURS.map(function (b) {
        return '<label class="mir-toggle" title="' + esc(b[2]) + '"><input type="checkbox" data-field="' + b[0] + '" /> ' + esc(b[1]) + '</label>';
      }).join('') +
      '  </div>' +
      '  <details class="tl-advanced"><summary>Advanced</summary><div class="ops-form">' +
      num('runPercent', 'Run % of straight segments', 0, 100, 1, '') +
      num('seekRange', 'Seek range (cells)', 1, 30, 1, 'Chase monsters within this range.') +
      num('roamRadius', 'Roam radius (cells)', 1, 100, 1, '') +
      num('workers', 'Worker threads', 1, 32, 1, '') +
      num('connectBudget', 'Logins at once', 1, 200, 1, 'Bots allowed between connect and in-game at the same time.') +
      '  </div></details>' +
      '</div>' +

      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Scratch server</h2><div class="tl-actions">' +
      '    <button type="button" class="mir-btn danger" data-role="scratch-stop">Stop</button>' +
      '    <button type="button" class="mir-btn primary" data-role="scratch-start">Start</button>' +
      '  </div></div>' +
      '  <div data-role="scratch-pill"></div>' +
      '  <p class="mir-muted tl-small">A separate Server.exe with a throwaway world and the load-test profile, on 127.0.0.1 only. It never touches this server\'s data. <span data-role="scratch-info"></span></p>' +
      '  <div class="ops-form">' +
      '    <label>Port (17000-17999)<input type="number" class="fd-input" data-role="scratch-port" min="17000" max="17999" value="17100" /></label>' +
      '    <label>World<select class="fd-input" data-role="scratch-world"><option value="synthetic">Synthetic flat maps</option><option value="live">Copies of this server\'s maps</option></select></label>' +
      '    <label>Maps (1-20)<input type="number" class="fd-input" data-role="scratch-maps" min="1" max="20" value="1" /></label>' +
      '    <label class="mir-toggle" title="Delete the scratch world (maps, accounts, logs) and generate a new one. World and map choices only apply to a new world."><input type="checkbox" data-role="scratch-reset" /> New world (reset)</label>' +
      '    <label class="mir-toggle" title="Rebuild Server.exe and the world generator from the source checkout first."><input type="checkbox" data-role="scratch-rebuild" /> Rebuild</label>' +
      '  </div>' +
      '  <details class="tl-logbox"><summary>Script output</summary><pre class="tl-log" data-role="scratch-log"></pre></details>' +
      '  <div data-role="build-row" class="hidden"><div class="tl-build-head">StressClient build <span data-role="build-pill"></span></div><pre class="tl-log tl-log-short" data-role="build-log"></pre></div>' +
      '</div>';
  }

  function wire() {
    el('refresh').addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, refresh); });
    el('bots-start').addEventListener('click', function (e) { startBots(e.currentTarget); });
    el('bots-stop').addEventListener('click', function (e) { stopBots(e.currentTarget); });
    el('bots-build').addEventListener('click', function (e) { buildBots(e.currentTarget); });
    el('scratch-start').addEventListener('click', function (e) { startScratch(e.currentTarget); });
    el('scratch-stop').addEventListener('click', function (e) { stopScratch(e.currentTarget); });
    field('target').addEventListener('change', renderTarget);
    field('bots').addEventListener('input', renderTarget);
    field('class').addEventListener('change', renderTarget);
    el('presets').addEventListener('click', function (e) {
      var b = e.target.closest ? e.target.closest('[data-preset]') : null;
      if (b) applyPreset(b.getAttribute('data-preset'));
    });
  }

  global.AdminSections.register('tools', {
    load: function (sectionEl) {
      root = sectionEl;
      render(sectionEl);
      loadForm();
      wire();
    },
    show: function () {
      if (!stopPoll) stopPoll = global.AdminSections.poll('tools', refresh, 5000);
      return refresh();
    },
    hide: function () {
      if (stopPoll) { stopPoll(); stopPoll = null; }
    }
  });

  global.ToolsPage = { startWarning: startWarning, refresh: refresh };
})(window);
