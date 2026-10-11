/**
 * Live Monitor section (#section-monitor).
 *
 * GET /api/admin/monitor/snapshot (one game-thread snapshot: every occupied map with its players, the monsters of the focus map when
 * that layer is on, and the events since the last poll) polled every 1 s while the section and the browser tab are visible.
 * The map is drawn on one canvas. Base: with map art ([WebAdmin] ClientDataPath) the map's authored minimap
 * (GET .../maps/{index}/minimap) and, once zoomed in, real 768 x 512 tiles (GET .../maps/{index}/tiles/{z}/{tx}/{ty}, z = 0..3 =
 * 1/2^z scale) streamed in for the visible area; otherwise the walkability image (GET .../maps/{index}/image, 3:2 cells like the game),
 * then heat (GET /api/admin/monitor/heat, accumulated by the server so it is there the moment the page opens), trails (built here
 * from successive snapshots), monsters, players and death flashes.
 *
 * Scroll zooms, drag pans, double-click resets; right-click a player (pin or row) or the map for GM actions, which all go through
 * POST /api/admin/gm/action (one dialog, reason, audit trail). Deep link: #section=monitor&map=<index>:<instance>&player=<name>.
 */
(function (global) {
  'use strict';

  var POLL_MS = 1000;               // user-approved override of the panel's ~5 s polling floor (2026-10-11)
  var HEAT_REFRESH_MS = 15000;
  var TRAIL_KEEP_MS = 65000;
  var JUMP_CELLS = 12;              // a step longer than this between two 1 s snapshots is drawn dashed (teleport / town scroll)
  var TILE_CONCURRENCY = 3;         // map-art tile requests in flight (the server renders one at a time)
  var TILE_CACHE_MAX = 400;
  var FLASH_MS = 2600;
  var LS_KEY = 'mircraft_monitor_prefs';
  var CLASS_COLOURS = { Warrior: '#e0644f', Wizard: '#5b9cf0', Taoist: '#5fc47a', Assassin: '#b47ff0', Archer: '#e6c14f' };
  var esc = function (s) { return global.OpsUI.esc(s); };

  var state = {
    maps: [],
    mapByKey: Object.create(null),
    focusKey: null,
    selected: null,                 // lower-case character name
    filter: '',
    hideGms: false,
    paused: false,
    sort: { key: 'name', dir: 1 },
    layers: { art: 'game', trail: 30, heat: 0, mobs: 'off', flashes: true },
    art: null,                      // the server's map-art flags ({ minimap, tiles, tileCells, maxZoom, tilePx })
    evSince: null,                  // null until the first snapshot (which never flashes)
    world: null,
    totalPlayers: 0,
    deaths10m: 0,
    lastOkAt: 0,
    mobs: null,
    trails: Object.create(null),    // lower-case name -> [{ t, key, x, y }]
    feed: [],                       // newest first
    flashes: [],                    // { key, x, y, t0 }
    heat: null,                     // { index, minutes, at, cells, max, grid }
    heatLoading: false,
    img: null,                      // { url, kind, index, el, ok, failed }
    minimapFailed: Object.create(null),
    tiles: new Map(),               // 'index/z/tx/ty' -> { img, ok, failed, at }
    tileQueue: [],
    tilesInFlight: 0,
    view: null,                     // { key, k, fitK, vx, vy }
    hover: null,
    allMaps: null
  };

  var els = {};
  var stopPoll = null;
  var drawQueued = false;
  var flashTimer = null;

  // ---- preferences (per viewer; never required) ------------------------------------------------------------------------

  function loadPrefs() {
    try {
      var p = JSON.parse(global.localStorage.getItem(LS_KEY) || 'null');
      if (!p || typeof p !== 'object') return;
      if ([0, 10, 30, 60].indexOf(p.trail) >= 0) state.layers.trail = p.trail;
      if ([0, 1, 5, 15].indexOf(p.heat) >= 0) state.layers.heat = p.heat;
      if (['off', 'all', 'bosses'].indexOf(p.mobs) >= 0) state.layers.mobs = p.mobs;
      if (typeof p.flashes === 'boolean') state.layers.flashes = p.flashes;
      if (p.art === 'game' || p.art === 'walk') state.layers.art = p.art;
      if (typeof p.hideGms === 'boolean') state.hideGms = p.hideGms;
    } catch (e) { /* storage blocked or malformed: defaults */ }
  }

  function savePrefs() {
    try {
      global.localStorage.setItem(LS_KEY, JSON.stringify({
        art: state.layers.art, trail: state.layers.trail, heat: state.layers.heat, mobs: state.layers.mobs, flashes: state.layers.flashes, hideGms: state.hideGms
      }));
    } catch (e) { /* ignore */ }
  }

  // ---- DOM ------------------------------------------------------------------------------------------------------------

  function build(el) {
    el.innerHTML =
      '<div class="mir-card mon-card">' +
      '  <div class="toolbar">' +
      '    <h2>Live Monitor</h2>' +
      '    <div class="mon-tools">' +
      '      <input type="search" class="fd-input mon-filter" data-role="filter" data-section-search placeholder="Filter name, class, guild, account  ( / )" aria-label="Filter players" />' +
      '      <label class="mir-toggle"><input type="checkbox" data-role="hidegms" /> Hide GMs</label>' +
      '      <span class="mir-muted mon-stamp" data-role="stamp"></span>' +
      '      <button type="button" class="mir-btn" data-role="pause">Pause</button>' +
      '      <button type="button" class="mir-btn primary" data-role="refresh">Refresh</button>' +
      '    </div>' +
      '  </div>' +
      '  <div class="metrics-grid mon-tiles" data-role="tiles"></div>' +
      '  <div class="mon-layers" role="group" aria-label="Map layers">' +
      '    <span class="mir-muted">Layers</span>' +
      '    <label>Map <select class="fd-input" data-role="art" title="Game art: the map&#39;s minimap from the client files and, when zoomed in, the real map tiles. Needs [WebAdmin] ClientDataPath on the server; otherwise walkability is shown">' +
      '      <option value="game">Game art</option><option value="walk">Walkability</option></select></label>' +
      '    <label>Trails <select class="fd-input" data-role="trail" title="Recent movement, from the last snapshots; dashed lines are jumps (teleports, town scrolls)">' +
      '      <option value="0">Off</option><option value="10">10 s</option><option value="30">30 s</option><option value="60">60 s</option></select></label>' +
      '    <label>Heat <select class="fd-input" data-role="heat" title="Where players have been standing (every map instance of this map). Sampled by the server every 5 s, so it is available as soon as the page opens">' +
      '      <option value="0">Off</option><option value="1">1 min</option><option value="5">5 min</option><option value="15">15 min</option></select></label>' +
      '    <label>Monsters <select class="fd-input" data-role="mobs" title="Live monster positions on the selected map (corpses are not shown). Hover a dot for its name, level and HP">' +
      '      <option value="off">Off</option><option value="all">All</option><option value="bosses">Bosses</option></select></label>' +
      '    <label class="mir-toggle"><input type="checkbox" data-role="flashes" /> Death flashes</label>' +
      '  </div>' +
      '  <div class="mon-banner hidden" data-role="banner" role="alert"></div>' +
      '  <div class="mon-layout">' +
      '    <aside class="mon-side">' +
      '      <form class="mon-goto" data-role="gotoForm" autocomplete="off">' +
      '        <input type="text" class="fd-input" data-role="goto" list="monMapList" placeholder="Go to any map…" aria-label="Go to map" />' +
      '        <datalist id="monMapList" data-role="mapOptions"></datalist>' +
      '      </form>' +
      '      <ul class="mon-maps" data-role="maps" aria-label="Occupied maps"></ul>' +
      '      <h3 class="mon-side-h">Recent events</h3>' +
      '      <ul class="mon-feed" data-role="feed"></ul>' +
      '    </aside>' +
      '    <div class="mon-main">' +
      '      <div class="mon-map-head">' +
      '        <span class="mon-map-title" data-role="mapTitle"></span>' +
      '        <span class="mir-muted mon-map-msg" data-role="mapMsg"></span>' +
      '        <span class="mon-zoom">' +
      '          <button type="button" class="mir-btn" data-role="zoomIn" title="Zoom in" aria-label="Zoom in">+</button>' +
      '          <button type="button" class="mir-btn" data-role="zoomOut" title="Zoom out" aria-label="Zoom out">&minus;</button>' +
      '          <button type="button" class="mir-btn" data-role="zoomReset" title="Reset zoom (double-click the map)" aria-label="Reset zoom">&#8634;</button>' +
      '        </span>' +
      '      </div>' +
      '      <div class="mon-map-wrap" data-role="wrap">' +
      '        <canvas class="mon-canvas" data-role="canvas" role="img" aria-label="Live map: player and monster positions"></canvas>' +
      '        <div class="mon-tip hidden" data-role="tip" role="tooltip"></div>' +
      '      </div>' +
      '      <div class="mon-legend mir-muted" data-role="legend"></div>' +
      '      <p class="mir-muted mon-hint">Scroll to zoom &middot; drag to pan &middot; double-click to reset &middot; click a player to select &middot; right-click a player (pin or row) or the map for GM actions &middot; &uarr;/&darr; step through players</p>' +
      '      <div class="ops-table-scroll">' +
      '        <table class="user-table mon-table">' +
      '          <thead><tr data-role="thead"></tr></thead>' +
      '          <tbody data-role="tbody"></tbody>' +
      '        </table>' +
      '      </div>' +
      '      <p class="mir-muted mon-empty hidden" data-role="empty"></p>' +
      '    </div>' +
      '  </div>' +
      '</div>';

    ['filter', 'hidegms', 'stamp', 'pause', 'refresh', 'tiles', 'art', 'trail', 'heat', 'mobs', 'flashes', 'banner', 'gotoForm', 'goto',
      'mapOptions', 'maps', 'feed', 'mapTitle', 'mapMsg', 'zoomIn', 'zoomOut', 'zoomReset', 'wrap', 'canvas', 'tip', 'legend',
      'thead', 'tbody', 'empty'].forEach(function (r) { els[r] = el.querySelector('[data-role="' + r + '"]'); });

    els.hidegms.checked = state.hideGms;
    els.art.value = state.layers.art;
    els.trail.value = String(state.layers.trail);
    els.heat.value = String(state.layers.heat);
    els.mobs.value = state.layers.mobs;
    els.flashes.checked = state.layers.flashes;
    renderHeader();
    renderLegend();

    els.filter.addEventListener('input', function () {
      state.filter = els.filter.value;
      renderTable();
      queueDraw();
    });
    els.hidegms.addEventListener('change', function () { state.hideGms = els.hidegms.checked; savePrefs(); renderAll(); });
    els.pause.addEventListener('click', function () {
      state.paused = !state.paused;
      els.pause.textContent = state.paused ? 'Resume' : 'Pause';
      els.pause.classList.toggle('primary', state.paused);
      renderStamp();
      if (!state.paused) refresh();
    });
    els.refresh.addEventListener('click', function () { refresh(true); });
    els.art.addEventListener('change', function () { state.layers.art = els.art.value; state.img = null; savePrefs(); renderMapHead(); queueDraw(); });
    els.trail.addEventListener('change', function () { state.layers.trail = Number(els.trail.value) || 0; savePrefs(); queueDraw(); });
    els.heat.addEventListener('change', function () {
      state.layers.heat = Number(els.heat.value) || 0;
      savePrefs();
      if (state.layers.heat) loadHeat(true); else queueDraw();
    });
    els.mobs.addEventListener('change', function () {
      state.layers.mobs = els.mobs.value;
      if (state.layers.mobs === 'off') state.mobs = null;
      savePrefs();
      renderLegend();
      if (state.layers.mobs !== 'off') refresh(true); else { renderTiles(); queueDraw(); }
    });
    els.flashes.addEventListener('change', function () { state.layers.flashes = els.flashes.checked; savePrefs(); });
    els.gotoForm.addEventListener('submit', function (e) { e.preventDefault(); gotoTyped(); });
    els.goto.addEventListener('change', gotoTyped);
    els.goto.addEventListener('focus', loadAllMaps);

    els.maps.addEventListener('click', function (e) {
      var b = e.target.closest('[data-key]');
      if (b) setFocus(b.getAttribute('data-key'), true);
    });
    els.feed.addEventListener('click', function (e) {
      var b = e.target.closest('[data-ev]');
      if (!b) return;
      var key = b.getAttribute('data-key');
      var name = b.getAttribute('data-name');
      if (key) setFocus(key, true);
      if (name) select(name, true);
    });

    els.thead.addEventListener('click', function (e) {
      var th = e.target.closest('th[data-sort]');
      if (!th) return;
      var k = th.getAttribute('data-sort');
      if (state.sort.key === k) state.sort.dir = -state.sort.dir;
      else { state.sort.key = k; state.sort.dir = k === 'name' || k === 'class' || k === 'guild' ? 1 : -1; }
      renderHeader();
      renderTable();
    });
    els.tbody.addEventListener('click', function (e) {
      if (e.target.closest('[data-player-chip]')) return;   // the chip opens its own menu
      var tr = e.target.closest('tr[data-name]');
      if (tr) select(tr.getAttribute('data-name'), true);
    });
    els.tbody.addEventListener('contextmenu', function (e) {
      var tr = e.target.closest('tr[data-name]');
      if (!tr) return;
      var p = playerByName(tr.getAttribute('data-name'));
      if (!p) return;
      e.preventDefault();
      select(p.name, false);
      openPlayerMenu(p, e.clientX, e.clientY);
    });

    els.zoomIn.addEventListener('click', function () { zoomBy(1.6); });
    els.zoomOut.addEventListener('click', function () { zoomBy(1 / 1.6); });
    els.zoomReset.addEventListener('click', resetView);
    wireCanvas();

    global.addEventListener('resize', function () { if (sectionVisible()) { state.view = null; queueDraw(); } });
    global.document.addEventListener('keydown', onKey);
  }

  function sectionVisible() {
    return global.AdminSections && global.AdminSections.isVisible ? global.AdminSections.isVisible('monitor') : true;
  }

  // ---- data -----------------------------------------------------------------------------------------------------------

  async function refresh(force) {
    if (state.paused && !force) return;
    var q = [];
    if (state.focusKey) q.push('map=' + encodeURIComponent(state.focusKey));
    if (state.layers.mobs !== 'off' && state.focusKey) q.push('mobs=' + state.layers.mobs);
    if (state.evSince != null) q.push('evSince=' + state.evSince);
    var r = await global.OpsUI.fetchResult('/api/admin/monitor/snapshot' + (q.length ? '?' + q.join('&') : ''));
    if (!r.ok) {
      showBanner(r.status === 401 || r.status === 403 ? 'Not signed in as an admin.' : 'Snapshot failed: ' + (r.error || ('HTTP ' + r.status)) + '. Retrying every ' + (POLL_MS / 1000) + ' s.');
      renderStamp();
      return;
    }
    apply(r.data || {});
  }

  function apply(d) {
    var now = Date.now();
    state.world = d.world || null;
    var artWas = JSON.stringify(state.art);
    state.art = d.art || null;
    if (JSON.stringify(state.art) !== artWas) state.img = null;
    state.totalPlayers = d.totalPlayers || 0;
    state.deaths10m = d.deaths10m || 0;
    state.maps = Array.isArray(d.maps) ? d.maps : [];
    state.mapByKey = Object.create(null);
    state.maps.forEach(function (m) { state.mapByKey[m.key] = m; });
    state.lastOkAt = now;

    if (state.world && state.world !== 'running') showBanner('The world is ' + state.world + '. The monitor shows live data while the world runs.');
    else showBanner(null);

    if (!state.focusKey) {
      var first = state.maps.filter(function (m) { return m.players && m.players.length; })[0];
      if (first) setFocus(first.key, state.layers.mobs !== 'off');
    }

    // trails: one point per player per snapshot
    state.maps.forEach(function (m) {
      (m.players || []).forEach(function (p) {
        var k = String(p.name).toLowerCase();
        var list = state.trails[k] || (state.trails[k] = []);
        var last = list[list.length - 1];
        if (!last || last.key !== m.key || last.x !== p.x || last.y !== p.y || now - last.t > 4000) list.push({ t: now, key: m.key, x: p.x, y: p.y });
      });
    });
    Object.keys(state.trails).forEach(function (k) {
      var list = state.trails[k];
      while (list.length && now - list[0].t > TRAIL_KEEP_MS) list.shift();
      if (!list.length) delete state.trails[k];
    });

    // events: the first poll lists the recent ones without flashing them
    var evs = Array.isArray(d.events) ? d.events : [];
    var first = state.evSince == null;
    for (var i = 0; i < evs.length; i++) {
      var e = evs[i];
      state.feed.unshift(e);
      if (!first && state.layers.flashes && e.type === 'death' && e.key && e.key === state.focusKey)
        state.flashes.push({ key: e.key, x: e.x, y: e.y, t0: now });
    }
    if (state.feed.length > 60) state.feed.length = 60;
    state.evSince = d.latestSeq || 0;

    state.mobs = d.mobs && d.mobs.key === state.focusKey ? d.mobs : null;

    if (state.layers.heat && state.focusKey) {
      var idx = mapIndexOf(state.focusKey);
      if (!state.heat || state.heat.index !== idx || state.heat.minutes !== state.layers.heat || now - state.heat.at > HEAT_REFRESH_MS) loadHeat(false);
    }

    renderAll();
    if (state.flashes.length) runFlashes();
  }

  async function loadHeat(force) {
    if (!state.focusKey || !state.layers.heat || (state.heatLoading && !force)) return;
    var idx = mapIndexOf(state.focusKey);
    var minutes = state.layers.heat;
    state.heatLoading = true;
    try {
      var r = await global.OpsUI.fetchResult('/api/admin/monitor/heat?map=' + idx + '&minutes=' + minutes);
      if (r.ok && r.data && idx === mapIndexOf(state.focusKey)) {
        state.heat = { index: idx, minutes: minutes, at: Date.now(), cells: r.data.cells || [], max: r.data.max || 0, grid: r.data.grid || 2 };
        queueDraw();
      }
    } finally {
      state.heatLoading = false;
    }
  }

  async function loadAllMaps() {
    if (state.allMaps) return;
    state.allMaps = [];
    var r = await global.OpsUI.fetchResult('/api/admin/gm/maps');
    var maps = r.ok && r.data && Array.isArray(r.data.maps) ? r.data.maps : [];
    state.allMaps = maps;
    els.mapOptions.innerHTML = maps.map(function (m) {
      return '<option value="' + esc(mapOptionLabel(m)) + '"></option>';
    }).join('');
  }

  function mapOptionLabel(m) {
    return (m.title || m.fileName || ('Map')) + ' (#' + m.index + (m.fileName ? ', ' + m.fileName : '') + ')';
  }

  function gotoTyped() {
    var v = String(els.goto.value || '').trim();
    if (!v) return;
    var key = null;
    var mHash = /#(\d+)/.exec(v);
    if (mHash) key = mHash[1] + ':1';
    else if (/^\d+(:\d+)?$/.test(v)) key = v.indexOf(':') >= 0 ? v : v + ':1';
    else if (state.allMaps) {
      var lv = v.toLowerCase();
      var hit = state.allMaps.filter(function (m) { return String(m.title || '').toLowerCase() === lv || String(m.fileName || '').toLowerCase() === lv; })[0];
      if (hit) key = hit.index + ':1';
    }
    if (!key) { global.portalToast('Pick a map from the list (or type #index).', 'error'); return; }
    els.goto.value = '';
    setFocus(key, true);
  }

  // ---- focus / selection ----------------------------------------------------------------------------------------------

  function mapIndexOf(key) { return Number(String(key || '').split(':')[0]) || 0; }

  function focusMap() { return state.focusKey ? state.mapByKey[state.focusKey] || null : null; }

  function setFocus(key, fetchNow) {
    if (!key || key === state.focusKey) return;
    state.focusKey = key;
    state.view = null;
    state.mobs = null;
    state.heat = null;
    state.flashes = [];
    state.hover = null;
    if (global.portalSetHashParams) global.portalSetHashParams({ map: key }, true);
    renderAll();
    if (fetchNow) refresh(true);
    if (state.layers.heat) loadHeat(true);
  }

  function playerByName(name) {
    var m = focusMap();
    if (!m || !name) return null;
    var k = String(name).toLowerCase();
    return (m.players || []).filter(function (p) { return String(p.name).toLowerCase() === k; })[0] || null;
  }

  function findPlayerAnywhere(name) {
    var k = String(name || '').toLowerCase();
    for (var i = 0; i < state.maps.length; i++) {
      var ps = state.maps[i].players || [];
      for (var j = 0; j < ps.length; j++) if (String(ps[j].name).toLowerCase() === k) return { map: state.maps[i], player: ps[j] };
    }
    return null;
  }

  function select(name, centre) {
    var k = name ? String(name).toLowerCase() : null;
    state.selected = k;
    if (global.portalSetHashParams) global.portalSetHashParams({ player: k ? name : null }, true);
    var p = playerByName(name);
    if (p && centre && state.view && state.view.k > state.view.fitK * 1.01) centreOn(p.x, p.y);
    renderTable();
    queueDraw();
    var row = els.tbody.querySelector('tr.mon-selected');
    if (row && row.scrollIntoView) row.scrollIntoView({ block: 'nearest' });
  }

  function onKey(e) {
    if (!sectionVisible()) return;
    var t = e.target;
    var typing = t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable);
    if (e.key === 'Escape') { closeMenu(); return; }
    if (typing || e.ctrlKey || e.metaKey || e.altKey) return;
    if (global.document.querySelector('.portal-confirm-backdrop')) return;
    if (e.key === '/') { e.preventDefault(); els.filter.focus(); return; }
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return;
    var rows = visibleRows();
    if (!rows.length) return;
    e.preventDefault();
    var i = -1;
    for (var j = 0; j < rows.length; j++) if (String(rows[j].name).toLowerCase() === state.selected) i = j;
    i = e.key === 'ArrowDown' ? Math.min(rows.length - 1, i + 1) : Math.max(0, i < 0 ? 0 : i - 1);
    select(rows[i].name, true);
  }

  // ---- rendering: panels ------------------------------------------------------------------------------------------------

  function renderAll() {
    renderStamp();
    renderTiles();
    renderMaps();
    renderFeed();
    renderTable();
    renderMapHead();
    queueDraw();
  }

  function showBanner(text) {
    if (!els.banner) return;
    els.banner.textContent = text || '';
    els.banner.classList.toggle('hidden', !text);
  }

  function renderStamp() {
    if (!els.stamp) return;
    if (state.paused) { els.stamp.textContent = 'Paused'; return; }
    els.stamp.textContent = state.lastOkAt ? 'Updated ' + new Date(state.lastOkAt).toLocaleTimeString() + ' · every ' + (POLL_MS / 1000) + ' s' : 'Loading…';
  }

  function tile(label, value, title, warn) {
    return '<div class="metric-card' + (warn ? ' metric-warn' : '') + '"' + (title ? ' title="' + esc(title) + '"' : '') + '>' +
      '<div class="metric-label">' + esc(label) + '</div><div class="metric-value">' + esc(value) + '</div></div>';
  }

  function renderTiles() {
    var m = focusMap();
    var gms = 0;
    state.maps.forEach(function (x) { (x.players || []).forEach(function (p) { if (p.gm) gms++; }); });
    var occupied = state.maps.filter(function (x) { return x.players && x.players.length; }).length;
    var html = tile('Players online', global.OpsUI.formatNumber(state.totalPlayers)) +
      tile('Occupied maps', occupied) +
      tile('On this map', m ? (m.players || []).length : '–') +
      tile('Deaths (10 min)', state.deaths10m, 'Player deaths in the last 10 minutes, all maps', state.deaths10m > 0) +
      tile('GMs online', gms);
    if (state.layers.mobs !== 'off') {
      var mobs = state.mobs ? state.mobs.total : null;
      html += tile(state.layers.mobs === 'bosses' ? 'Bosses on map' : 'Monsters on map', mobs == null ? '…' : global.OpsUI.formatNumber(mobs),
        state.mobs && state.mobs.truncated ? 'Only the first ' + state.mobs.rows.length / 6 + ' are drawn' : null);
    }
    els.tiles.innerHTML = html;
  }

  function renderMaps() {
    var list = state.maps.filter(function (m) { return (m.players && m.players.length) || m.key === state.focusKey; });
    if (!list.length) {
      els.maps.innerHTML = '<li class="mir-muted mon-maps-empty">' + (state.world && state.world !== 'running' ? 'World not running.' : 'Nobody online.') + '</li>';
      return;
    }
    els.maps.innerHTML = list.map(function (m) {
      var sel = m.key === state.focusKey;
      return '<li><button type="button" class="mon-map-btn' + (sel ? ' active' : '') + '" data-key="' + esc(m.key) + '"' + (sel ? ' aria-current="true"' : '') +
        ' title="' + esc(m.title + ' — #' + m.index + (m.fileName ? ', file ' + m.fileName : '') + (m.instance > 1 ? ', instance ' + m.instance : '')) + '">' +
        '<span class="mon-map-name">' + esc(m.title) + (m.instance > 1 ? ' <span class="mir-muted">#' + esc(m.instance) + '</span>' : '') + '</span>' +
        '<span class="mon-map-count">' + esc((m.players || []).length) + '</span></button></li>';
    }).join('');
  }

  function evTime(e) {
    var d = global.OpsUI.toDate ? global.OpsUI.toDate(e.utc) : new Date(e.utc);
    return d && !isNaN(d) ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '';
  }

  function renderFeed() {
    if (!state.feed.length) { els.feed.innerHTML = '<li class="mir-muted">No events yet.</li>'; return; }
    els.feed.innerHTML = state.feed.slice(0, 40).map(function (e) {
      var m = e.key ? state.mapByKey[e.key] : null;
      var where = m ? m.title : (e.key ? 'map #' + mapIndexOf(e.key) : '');
      var text;
      if (e.type === 'death') text = '<b>' + esc(e.name) + '</b> died' + (e.by ? ' to ' + (e.byKind === 'player' ? '<b>' + esc(e.by) + '</b>' : esc(e.by)) : '');
      else if (e.type === 'login') text = '<b>' + esc(e.name) + '</b> logged in';
      else text = '<b>' + esc(e.name) + '</b> logged out';
      return '<li><button type="button" class="mon-ev mon-ev-' + esc(e.type) + '" data-ev="1" data-key="' + esc(e.key || '') + '" data-name="' + esc(e.type === 'logout' ? '' : e.name) + '">' +
        '<span class="mon-ev-time">' + esc(evTime(e)) + '</span> ' + text + (where ? ' <span class="mir-muted">· ' + esc(where) + '</span>' : '') + '</button></li>';
    }).join('');
  }

  function renderMapHead() {
    var m = focusMap();
    if (!state.focusKey) { els.mapTitle.textContent = 'No map selected'; els.mapMsg.textContent = ''; return; }
    if (!m) {
      els.mapTitle.textContent = 'Map #' + mapIndexOf(state.focusKey);
      els.mapMsg.textContent = state.world === 'running' ? 'Not loaded (or that instance does not exist).' : '';
      return;
    }
    els.mapTitle.textContent = m.title + (m.instance > 1 ? ' (instance ' + m.instance + ')' : '');
    els.mapMsg.textContent = '#' + m.index + (m.fileName ? ' · ' + m.fileName : '') + ' · ' + m.width + ' × ' + m.height +
      (state.img && state.img.index === m.index && state.img.failed ? ' · map image unavailable' : '') + artNote();
  }

  function artNote() {
    if (state.layers.art !== 'game' || !state.art) return '';
    if (!state.art.minimap && !state.art.tiles) return ' · no map art (set [WebAdmin] ClientDataPath)';
    if (state.img && state.img.kind === 'minimap') return ' · minimap' + (state.art.tiles ? ', real tiles when zoomed in' : '');
    return state.art.tiles ? ' · no minimap; real tiles when zoomed in' : '';
  }

  function renderLegend() {
    var parts = Object.keys(CLASS_COLOURS).map(function (c) {
      return '<span class="mon-key"><i style="background:' + CLASS_COLOURS[c] + '"></i>' + esc(c) + '</span>';
    });
    parts.push('<span class="mon-key"><i class="mon-key-gm"></i>GM</span>');
    parts.push('<span class="mon-key"><i class="mon-key-dead"></i>Dead</span>');
    parts.push('<span class="mon-key"><i class="mon-key-safe"></i>Safe zone</span>');
    if (state.layers.mobs !== 'off') {
      parts.push('<span class="mon-key"><i class="mon-key-mob"></i>Monster</span>');
      parts.push('<span class="mon-key"><i class="mon-key-boss"></i>Boss</span>');
      parts.push('<span class="mon-key"><i class="mon-key-pet"></i>Pet</span>');
    }
    els.legend.innerHTML = parts.join('');
  }

  // ---- table ----------------------------------------------------------------------------------------------------------

  var COLUMNS = [
    { key: 'num', label: '#', num: true },
    { key: 'name', label: 'Name', sort: true },
    { key: 'level', label: 'Lv', sort: true, num: true },
    { key: 'class', label: 'Class', sort: true },
    { key: 'guild', label: 'Guild', sort: true },
    { key: 'group', label: 'Grp', sort: true, num: true, title: 'Group size' },
    { key: 'hp', label: 'HP', sort: true, num: true },
    { key: 'pos', label: 'Position' },
    { key: 'status', label: 'Status' }
  ];

  function renderHeader() {
    els.thead.innerHTML = COLUMNS.map(function (c) {
      var arrow = state.sort.key === c.key ? (state.sort.dir > 0 ? ' ▲' : ' ▼') : '';
      return '<th' + (c.num ? ' class="num' + (c.sort ? ' sortable' : '') + '"' : (c.sort ? ' class="sortable"' : '')) +
        (c.sort ? ' data-sort="' + c.key + '" aria-sort="' + (arrow ? (state.sort.dir > 0 ? 'ascending' : 'descending') : 'none') + '"' : '') +
        (c.title ? ' title="' + esc(c.title) + '"' : '') + '>' + esc(c.label) + arrow + '</th>';
    }).join('');
  }

  function matches(p) {
    if (state.hideGms && p.gm) return false;
    var f = state.filter.trim().toLowerCase();
    if (!f) return true;
    return [p.name, p.className, p.guild, p.accountId].some(function (v) { return v && String(v).toLowerCase().indexOf(f) >= 0; });
  }

  function sortValue(p, k) {
    switch (k) {
      case 'level': return p.level;
      case 'class': return p.className || '';
      case 'guild': return p.guild || '';
      case 'group': return p.group;
      case 'hp': return p.dead ? -1 : p.hpPercent;
      default: return String(p.name || '').toLowerCase();
    }
  }

  function visibleRows() {
    var m = focusMap();
    var rows = m ? (m.players || []).filter(matches) : [];
    var k = state.sort.key;
    var dir = state.sort.dir;
    return rows.slice().sort(function (a, b) {
      var x = sortValue(a, k), y = sortValue(b, k);
      if (x < y) return -dir;
      if (x > y) return dir;
      return String(a.name).localeCompare(String(b.name));
    });
  }

  function renderTable() {
    var m = focusMap();
    var rows = visibleRows();
    els.tbody.innerHTML = rows.map(function (p, i) {
      var sel = String(p.name).toLowerCase() === state.selected;
      var colour = CLASS_COLOURS[p.className] || '#9aa4b2';
      var status = [];
      if (p.dead) status.push('<span class="mon-tag mon-tag-dead">Dead</span>');
      if (p.gm) status.push('<span class="mon-tag mon-tag-gm">GM</span>');
      if (p.safeZone) status.push('<span class="mon-tag mon-tag-safe">Safe</span>');
      return '<tr data-name="' + esc(p.name) + '"' + (sel ? ' class="mon-selected" aria-selected="true"' : '') + '>' +
        '<td class="num mir-muted">' + (i + 1) + '</td>' +
        '<td><span class="mon-dot" style="background:' + colour + '"></span>' +
        global.OpsUI.playerLink(p.name, { online: true, characterIndex: p.characterIndex, account: p.accountId, accountIndex: p.accountIndex }) + '</td>' +
        '<td class="num">' + esc(p.level) + '</td>' +
        '<td>' + esc(p.className || '') + '</td>' +
        '<td>' + esc(p.guild || '') + '</td>' +
        '<td class="num">' + (p.group ? esc(p.group) : '<span class="mir-muted">–</span>') + '</td>' +
        '<td class="num">' + hpBar(p) + '</td>' +
        '<td class="mon-pos">' + esc(p.x) + ', ' + esc(p.y) + '</td>' +
        '<td>' + status.join(' ') + '</td>' +
        '</tr>';
    }).join('');
    var empty = !rows.length;
    els.empty.classList.toggle('hidden', !empty);
    if (empty) {
      els.empty.textContent = !m ? (state.focusKey ? 'This map is not loaded.' : 'Pick a map.') :
        (m.players && m.players.length ? 'No player matches the filter.' : 'Nobody is on this map right now.');
    }
  }

  function hpBar(p) {
    if (p.dead) return '<span class="mir-muted">0%</span>';
    var v = Math.max(0, Math.min(100, p.hpPercent || 0));
    var cls = v < 25 ? ' low' : v < 60 ? ' mid' : '';
    return '<span class="mon-hp' + cls + '" title="' + v + '% HP"><span style="width:' + v + '%"></span></span>';
  }

  // ---- map canvas -----------------------------------------------------------------------------------------------------

  function artOn() { return state.layers.art === 'game' && !!state.art; }

  function baseUrl(m) {
    if (artOn() && state.art.minimap && m.miniMap > 0 && !state.minimapFailed[m.index])
      return { url: '/api/admin/monitor/maps/' + encodeURIComponent(m.index) + '/minimap', kind: 'minimap' };
    return { url: '/api/admin/monitor/maps/' + encodeURIComponent(m.index) + '/image', kind: 'walk' };
  }

  function ensureImage(m) {
    if (!m) return;
    var want = baseUrl(m);
    if (state.img && state.img.url === want.url) return;
    var img = new global.Image();
    var rec = { url: want.url, kind: want.kind, index: m.index, el: img, ok: false, failed: false };
    state.img = rec;
    img.onload = function () { if (state.img === rec) { rec.ok = true; renderMapHead(); queueDraw(); } };
    img.onerror = function () {
      if (state.img !== rec) return;
      rec.failed = true;
      if (rec.kind === 'minimap') { state.minimapFailed[rec.index] = true; state.img = null; }
      renderMapHead();
      queueDraw();
    };
    img.src = want.url;
  }

  // ---- map-art tiles ---------------------------------------------------------------------------------------------------

  /** Tile zoom for the current scale (0 = the game's own scale ... 3 = 1/8), or -1 while the base image is enough. */
  function tileZoom(v) {
    if (!artOn() || !state.art.tiles) return -1;
    var dpr = global.devicePixelRatio || 1;
    var cellPx = 3 * v.k * dpr;                      // screen px per cell width
    if (cellPx < 4) return -1;
    var z = Math.floor(Math.log2(48 / cellPx));
    return Math.max(0, Math.min(state.art.maxZoom || 3, z));
  }

  function drawTiles(ctx, m, v) {
    var z = tileZoom(v);
    state.tileQueue = [];
    if (z < 0) return;
    var cells = (state.art.tileCells || 16) << z;
    var visW = v.cw / (3 * v.k), visH = v.ch / (2 * v.k);
    var tx0 = Math.max(0, Math.floor(v.vx / cells)), ty0 = Math.max(0, Math.floor(v.vy / cells));
    var tx1 = Math.min(Math.ceil(m.width / cells) - 1, Math.floor((v.vx + visW) / cells));
    var ty1 = Math.min(Math.ceil(m.height / cells) - 1, Math.floor((v.vy + visH) / cells));
    var dpr = global.devicePixelRatio || 1;
    var now = Date.now();
    var want = [];

    ctx.save();
    ctx.beginPath();
    ctx.rect(-v.vx * 3 * v.k, -v.vy * 2 * v.k, m.width * 3 * v.k, m.height * 2 * v.k);
    ctx.clip();
    ctx.imageSmoothingEnabled = 3 * v.k * dpr < (48 >> z);
    for (var ty = ty0; ty <= ty1; ty++) {
      for (var tx = tx0; tx <= tx1; tx++) {
        var key = m.index + '/' + z + '/' + tx + '/' + ty;
        var t = state.tiles.get(key);
        if (t && t.ok) {
          ctx.drawImage(t.img, (tx * cells - v.vx) * 3 * v.k, (ty * cells - v.vy) * 2 * v.k, cells * 3 * v.k, cells * 2 * v.k);
        } else if (!t || (t.failed && now - t.at > 30000)) {
          var dx = (tx + 0.5) * cells - (v.vx + visW / 2), dy = (ty + 0.5) * cells - (v.vy + visH / 2);
          want.push({ key: key, d: dx * dx + dy * dy });
        }
      }
    }
    ctx.restore();

    want.sort(function (a, b) { return a.d - b.d; });
    state.tileQueue = want.map(function (w) { return w.key; });
    pumpTiles();
  }

  function pumpTiles() {
    while (state.tilesInFlight < TILE_CONCURRENCY && state.tileQueue.length) {
      var key = state.tileQueue.shift();
      var t = state.tiles.get(key);
      if (t && (t.ok || !t.failed)) continue;          // loaded, or already loading
      loadTile(key);
    }
  }

  function loadTile(key) {
    var parts = key.split('/');
    var img = new global.Image();
    var rec = { img: img, ok: false, failed: false, at: Date.now() };
    state.tiles.delete(key);
    state.tiles.set(key, rec);
    while (state.tiles.size > TILE_CACHE_MAX) state.tiles.delete(state.tiles.keys().next().value);
    state.tilesInFlight++;
    var done = function (ok) {
      state.tilesInFlight = Math.max(0, state.tilesInFlight - 1);
      rec.ok = ok;
      rec.failed = !ok;
      rec.at = Date.now();
      queueDraw();
      pumpTiles();
    };
    img.onload = function () { done(true); };
    img.onerror = function () { done(false); };
    img.src = '/api/admin/monitor/maps/' + parts[0] + '/tiles/' + parts[1] + '/' + parts[2] + '/' + parts[3];
  }

  /** Canvas size and fit scale for the focus map; k = CSS px per third of a cell width (a cell is 3k x 2k, like 48 x 32). */
  function layout(m) {
    var wrap = els.wrap;
    var cw = Math.max(200, wrap.clientWidth || 600);
    var maxH = Math.max(260, Math.round((global.innerHeight || 800) * 0.62));
    var fitK = Math.min(cw / (3 * m.width), maxH / (2 * m.height));
    var ch = Math.max(180, Math.round(2 * m.height * fitK));
    return { cw: cw, ch: ch, fitK: fitK };
  }

  function ensureView(m, lay) {
    var v = state.view;
    if (!v || v.key !== m.key || Math.abs(v.fitK - lay.fitK) > 1e-9 || v.cw !== lay.cw) {
      var keepZoom = v && v.key === m.key && v.fitK > 0 ? v.k / v.fitK : 1;
      var cx = v && v.key === m.key ? v.vx + lay.cw / (6 * v.k) : m.width / 2;
      var cy = v && v.key === m.key ? v.vy + lay.ch / (4 * v.k) : m.height / 2;
      state.view = v = { key: m.key, fitK: lay.fitK, k: lay.fitK * keepZoom, vx: 0, vy: 0, cw: lay.cw, ch: lay.ch };
      v.vx = cx - lay.cw / (6 * v.k);
      v.vy = cy - lay.ch / (4 * v.k);
    }
    v.ch = lay.ch;
    clampView(m, v);
    return v;
  }

  function clampView(m, v) {
    v.k = Math.max(v.fitK, Math.min(Math.max(v.fitK * 48, 24), v.k));   // up to 1.5x the game's own scale
    var visW = v.cw / (3 * v.k), visH = v.ch / (2 * v.k);
    v.vx = visW >= m.width ? (m.width - visW) / 2 : Math.max(0, Math.min(m.width - visW, v.vx));
    v.vy = visH >= m.height ? (m.height - visH) / 2 : Math.max(0, Math.min(m.height - visH, v.vy));
  }

  function toScreen(v, x, y) { return { x: (x + 0.5 - v.vx) * 3 * v.k, y: (y + 0.5 - v.vy) * 2 * v.k }; }
  function toCell(v, sx, sy) { return { x: Math.floor(sx / (3 * v.k) + v.vx), y: Math.floor(sy / (2 * v.k) + v.vy) }; }

  function queueDraw() {
    if (drawQueued) return;
    drawQueued = true;
    (global.requestAnimationFrame || function (f) { return setTimeout(f, 16); })(function () { drawQueued = false; draw(); });
  }

  function draw() {
    var canvas = els.canvas;
    if (!canvas || !sectionVisible()) return;
    var m = focusMap();
    var ctx = canvas.getContext('2d');
    var dpr = global.devicePixelRatio || 1;

    if (!m || !m.width || !m.height) {
      var w0 = Math.max(200, els.wrap.clientWidth || 600), h0 = 220;
      sizeCanvas(canvas, w0, h0, dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.fillStyle = '#0b0d11';
      ctx.fillRect(0, 0, w0, h0);
      ctx.fillStyle = '#7d8696';
      ctx.font = '13px Inter, sans-serif';
      ctx.textAlign = 'center';
      ctx.fillText(state.focusKey ? 'This map is not loaded.' : 'Nobody is online — pick a map with "Go to any map…".', w0 / 2, h0 / 2);
      return;
    }

    ensureImage(m);
    var lay = layout(m);
    var v = ensureView(m, lay);
    sizeCanvas(canvas, lay.cw, lay.ch, dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.fillStyle = '#0b0d11';
    ctx.fillRect(0, 0, lay.cw, lay.ch);

    // map image
    var ox = -v.vx * 3 * v.k, oy = -v.vy * 2 * v.k, mw = m.width * 3 * v.k, mh = m.height * 2 * v.k;
    if (state.img && state.img.index === m.index && state.img.ok) {
      ctx.imageSmoothingEnabled = state.img.kind === 'minimap';
      ctx.drawImage(state.img.el, ox, oy, mw, mh);
    } else {
      ctx.fillStyle = '#161a22';
      ctx.fillRect(ox, oy, mw, mh);
    }
    drawTiles(ctx, m, v);

    drawHeat(ctx, m, v);
    drawTrails(ctx, m, v);
    drawMobs(ctx, v);
    drawPlayers(ctx, m, v);
    drawFlashes(ctx, m, v);

    if (v.k > v.fitK * 1.01) {
      ctx.fillStyle = 'rgba(11,13,17,0.75)';
      ctx.fillRect(6, lay.ch - 22, 64, 16);
      ctx.fillStyle = '#c9d1dc';
      ctx.font = '11px Inter, sans-serif';
      ctx.textAlign = 'left';
      ctx.fillText('× ' + (v.k / v.fitK).toFixed(1), 12, lay.ch - 10);
    }
  }

  function sizeCanvas(canvas, w, h, dpr) {
    var pw = Math.round(w * dpr), ph = Math.round(h * dpr);
    if (canvas.width !== pw) canvas.width = pw;
    if (canvas.height !== ph) canvas.height = ph;
    canvas.style.width = w + 'px';
    canvas.style.height = h + 'px';
  }

  function drawHeat(ctx, m, v) {
    var h = state.heat;
    if (!state.layers.heat || !h || h.index !== m.index || !h.max) return;
    var c = h.cells, g = h.grid;
    var cw = g * 3 * v.k, chh = g * 2 * v.k;
    for (var i = 0; i + 2 < c.length; i += 3) {
      var t = Math.sqrt(c[i + 2] / h.max);
      var sx = (c[i] * g - v.vx) * 3 * v.k, sy = (c[i + 1] * g - v.vy) * 2 * v.k;
      if (sx + cw < 0 || sy + chh < 0 || sx > v.cw || sy > v.ch) continue;
      ctx.fillStyle = 'hsla(' + Math.round(55 - 55 * t) + ',100%,55%,' + (0.18 + 0.62 * t).toFixed(3) + ')';
      ctx.fillRect(sx, sy, Math.max(cw, 2), Math.max(chh, 2));
    }
  }

  function drawTrails(ctx, m, v) {
    var win = state.layers.trail * 1000;
    if (!win) return;
    var now = Date.now();
    ctx.lineWidth = 1.5;
    (m.players || []).forEach(function (p) {
      var list = state.trails[String(p.name).toLowerCase()];
      if (!list || list.length < 2) return;
      var colour = CLASS_COLOURS[p.className] || '#9aa4b2';
      var prev = null;
      for (var i = 0; i < list.length; i++) {
        var pt = list[i];
        if (pt.key !== m.key || now - pt.t > win) { prev = null; continue; }
        if (prev) {
          var a = toScreen(v, prev.x, prev.y), b = toScreen(v, pt.x, pt.y);
          var jump = Math.max(Math.abs(pt.x - prev.x), Math.abs(pt.y - prev.y)) > JUMP_CELLS;
          ctx.globalAlpha = 0.25 + 0.5 * (1 - (now - pt.t) / win);
          ctx.strokeStyle = colour;
          ctx.setLineDash(jump ? [4, 4] : []);
          ctx.beginPath();
          ctx.moveTo(a.x, a.y);
          ctx.lineTo(b.x, b.y);
          ctx.stroke();
        }
        prev = pt;
      }
    });
    ctx.setLineDash([]);
    ctx.globalAlpha = 1;
  }

  function drawMobs(ctx, v) {
    var mobs = state.mobs;
    if (state.layers.mobs === 'off' || !mobs || !mobs.rows) return;
    var r = mobs.rows;
    var base = Math.max(1.5, Math.min(4, v.k * 0.9));
    for (var i = 0; i + 5 < r.length; i += 6) {
      var s = toScreen(v, r[i + 1], r[i + 2]);
      if (s.x < -6 || s.y < -6 || s.x > v.cw + 6 || s.y > v.ch + 6) continue;
      var flags = r[i + 5];
      var boss = flags & 1, pet = flags & 2;
      var size = boss ? base * 2.2 : base;
      ctx.fillStyle = boss ? '#ff8a3d' : pet ? '#62c6e8' : '#d9534f';
      ctx.fillRect(s.x - size / 2, s.y - size / 2, size, size);
      if (boss) {
        ctx.strokeStyle = '#1a0d05';
        ctx.lineWidth = 1;
        ctx.strokeRect(s.x - size / 2, s.y - size / 2, size, size);
      }
    }
  }

  function pinRadius(v) { return Math.max(4, Math.min(9, 3 + v.k * 0.9)); }

  function drawPlayers(ctx, m, v) {
    var r = pinRadius(v);
    var sel = null;
    var players = (m.players || []).slice().sort(function (a, b) { return (matches(a) ? 1 : 0) - (matches(b) ? 1 : 0); });
    players.forEach(function (p) {
      if (state.hideGms && p.gm) return;
      var s = toScreen(v, p.x, p.y);
      if (s.x < -20 || s.y < -20 || s.x > v.cw + 20 || s.y > v.ch + 20) return;
      var on = matches(p);
      ctx.globalAlpha = on ? 1 : 0.28;
      ctx.beginPath();
      ctx.arc(s.x, s.y, r, 0, Math.PI * 2);
      ctx.fillStyle = p.dead ? '#59606b' : (CLASS_COLOURS[p.className] || '#9aa4b2');
      ctx.fill();
      ctx.lineWidth = p.gm ? 2 : 1.25;
      ctx.strokeStyle = p.gm ? '#ffffff' : p.safeZone ? '#7fe0a8' : '#0b0d11';
      ctx.stroke();
      if (p.dead) {
        ctx.strokeStyle = '#0b0d11';
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.moveTo(s.x - r * 0.55, s.y - r * 0.55); ctx.lineTo(s.x + r * 0.55, s.y + r * 0.55);
        ctx.moveTo(s.x + r * 0.55, s.y - r * 0.55); ctx.lineTo(s.x - r * 0.55, s.y + r * 0.55);
        ctx.stroke();
      }
      if (String(p.name).toLowerCase() === state.selected) sel = { p: p, s: s };
    });
    ctx.globalAlpha = 1;
    if (sel) {
      ctx.beginPath();
      ctx.arc(sel.s.x, sel.s.y, r + 5, 0, Math.PI * 2);
      ctx.strokeStyle = '#ffd666';
      ctx.lineWidth = 2;
      ctx.stroke();
      label(ctx, sel.p.name + ' (' + sel.p.x + ', ' + sel.p.y + ')', sel.s.x, sel.s.y - r - 8, v);
    }
  }

  function label(ctx, text, x, y, v) {
    ctx.font = '600 12px Inter, sans-serif';
    var w = ctx.measureText(text).width + 10;
    var lx = Math.max(2, Math.min(v.cw - w - 2, x - w / 2));
    var ly = Math.max(2, y - 16);
    ctx.fillStyle = 'rgba(11,13,17,0.85)';
    ctx.fillRect(lx, ly, w, 17);
    ctx.fillStyle = '#ffd666';
    ctx.textAlign = 'left';
    ctx.fillText(text, lx + 5, ly + 12.5);
  }

  function drawFlashes(ctx, m, v) {
    if (!state.flashes.length) return;
    var now = Date.now();
    state.flashes = state.flashes.filter(function (f) { return now - f.t0 < FLASH_MS && f.key === m.key; });
    state.flashes.forEach(function (f) {
      var t = (now - f.t0) / FLASH_MS;
      var s = toScreen(v, f.x, f.y);
      ctx.beginPath();
      ctx.arc(s.x, s.y, 6 + 34 * t, 0, Math.PI * 2);
      ctx.strokeStyle = 'rgba(255,70,70,' + (1 - t).toFixed(3) + ')';
      ctx.lineWidth = 3 * (1 - t) + 1;
      ctx.stroke();
    });
  }

  function runFlashes() {
    if (flashTimer) return;
    var step = function () {
      flashTimer = null;
      draw();
      if (state.flashes.length && sectionVisible()) flashTimer = (global.requestAnimationFrame || setTimeout)(step);
    };
    flashTimer = (global.requestAnimationFrame || setTimeout)(step);
  }

  // ---- canvas interaction ---------------------------------------------------------------------------------------------

  function localPoint(e) {
    var rect = els.canvas.getBoundingClientRect();
    return { x: e.clientX - rect.left, y: e.clientY - rect.top };
  }

  function hitPlayer(pt) {
    var m = focusMap(), v = state.view;
    if (!m || !v) return null;
    var best = null, bestD = Math.pow(pinRadius(v) + 4, 2);
    (m.players || []).forEach(function (p) {
      if (state.hideGms && p.gm) return;
      var s = toScreen(v, p.x, p.y);
      var d = (s.x - pt.x) * (s.x - pt.x) + (s.y - pt.y) * (s.y - pt.y);
      if (d <= bestD) { bestD = d; best = p; }
    });
    return best;
  }

  function hitMob(pt) {
    var mobs = state.mobs, v = state.view;
    if (state.layers.mobs === 'off' || !mobs || !v) return null;
    var r = mobs.rows, best = -1, bestD = 49;
    for (var i = 0; i + 5 < r.length; i += 6) {
      var s = toScreen(v, r[i + 1], r[i + 2]);
      var d = (s.x - pt.x) * (s.x - pt.x) + (s.y - pt.y) * (s.y - pt.y);
      if (d <= bestD) { bestD = d; best = i; }
    }
    if (best < 0) return null;
    return { name: mobs.names[r[best]] || '?', x: r[best + 1], y: r[best + 2], level: r[best + 3], hp: r[best + 4], boss: !!(r[best + 5] & 1), pet: !!(r[best + 5] & 2) };
  }

  function showTip(html, pt) {
    if (!html) { els.tip.classList.add('hidden'); return; }
    els.tip.innerHTML = html;
    els.tip.classList.remove('hidden');
    var w = els.tip.offsetWidth, h = els.tip.offsetHeight;
    var x = Math.min(els.wrap.clientWidth - w - 4, pt.x + 14);
    var y = pt.y + 14 + h > els.wrap.clientHeight ? pt.y - h - 10 : pt.y + 14;
    els.tip.style.left = Math.max(4, x) + 'px';
    els.tip.style.top = Math.max(4, y) + 'px';
  }

  function wireCanvas() {
    var c = els.canvas;
    var drag = null;

    c.addEventListener('wheel', function (e) {
      var m = focusMap(), v = state.view;
      if (!m || !v) return;
      e.preventDefault();
      var pt = localPoint(e);
      zoomAt(pt, e.deltaY < 0 ? 1.25 : 1 / 1.25);
    }, { passive: false });

    c.addEventListener('mousedown', function (e) {
      if (e.button !== 0 || !state.view) return;
      drag = { x: e.clientX, y: e.clientY, vx: state.view.vx, vy: state.view.vy, moved: false };
    });
    global.addEventListener('mousemove', function (e) {
      if (!drag || !state.view) return;
      var dx = e.clientX - drag.x, dy = e.clientY - drag.y;
      if (!drag.moved && Math.abs(dx) + Math.abs(dy) < 4) return;
      drag.moved = true;
      c.classList.add('mon-dragging');
      var v = state.view;
      v.vx = drag.vx - dx / (3 * v.k);
      v.vy = drag.vy - dy / (2 * v.k);
      var m = focusMap();
      if (m) clampView(m, v);
      showTip(null);
      queueDraw();
    });
    global.addEventListener('mouseup', function (e) {
      if (!drag) return;
      var wasDrag = drag.moved;
      drag = null;
      c.classList.remove('mon-dragging');
      if (wasDrag || e.target !== c) return;
      var p = hitPlayer(localPoint(e));
      if (p) select(String(p.name).toLowerCase() === state.selected ? null : p.name, false);
    });

    c.addEventListener('mousemove', function (e) {
      if (drag && drag.moved) return;
      var pt = localPoint(e);
      var p = hitPlayer(pt);
      if (p) {
        c.style.cursor = 'pointer';
        showTip('<b>' + esc(p.name) + '</b> Lv ' + esc(p.level) + ' ' + esc(p.className || '') +
          (p.guild ? '<br><span class="mir-muted">' + esc(p.guild) + '</span>' : '') +
          '<br>(' + esc(p.x) + ', ' + esc(p.y) + ') · ' + (p.dead ? 'dead' : esc(p.hpPercent) + '% HP') + (p.gm ? ' · GM' : ''), pt);
        return;
      }
      var mob = hitMob(pt);
      c.style.cursor = state.view && state.view.k > state.view.fitK * 1.01 ? 'grab' : 'crosshair';
      if (mob) {
        showTip('<b>' + esc(mob.name) + '</b>' + (mob.boss ? ' <span class="mon-tag mon-tag-boss">Boss</span>' : '') + (mob.pet ? ' <span class="mir-muted">(pet)</span>' : '') +
          '<br>Lv ' + esc(mob.level) + ' · ' + esc(mob.hp) + '% HP · (' + esc(mob.x) + ', ' + esc(mob.y) + ')', pt);
        return;
      }
      var v = state.view, m = focusMap();
      if (v && m) {
        var cell = toCell(v, pt.x, pt.y);
        if (cell.x >= 0 && cell.y >= 0 && cell.x < m.width && cell.y < m.height) { showTip('<span class="mir-muted">' + cell.x + ', ' + cell.y + '</span>', pt); return; }
      }
      showTip(null);
    });
    c.addEventListener('mouseleave', function () { showTip(null); });

    c.addEventListener('dblclick', function (e) { e.preventDefault(); resetView(); });

    c.addEventListener('contextmenu', function (e) {
      var m = focusMap(), v = state.view;
      if (!m || !v) return;
      e.preventDefault();
      showTip(null);
      var pt = localPoint(e);
      var p = hitPlayer(pt);
      if (p) { select(p.name, false); openPlayerMenu(p, e.clientX, e.clientY); return; }
      var cell = toCell(v, pt.x, pt.y);
      if (cell.x < 0 || cell.y < 0 || cell.x >= m.width || cell.y >= m.height) return;
      openMapMenu(m, cell, e.clientX, e.clientY);
    });
  }

  function zoomAt(pt, factor) {
    var m = focusMap(), v = state.view;
    if (!m || !v) return;
    var cx = pt.x / (3 * v.k) + v.vx, cy = pt.y / (2 * v.k) + v.vy;
    v.k *= factor;
    clampView(m, v);
    v.vx = cx - pt.x / (3 * v.k);
    v.vy = cy - pt.y / (2 * v.k);
    clampView(m, v);
    queueDraw();
  }

  function zoomBy(f) {
    var v = state.view;
    if (!v) return;
    var p = state.selected ? playerByName(state.selected) : null;
    if (p && f > 1) { v.k *= f; centreOn(p.x, p.y); return; }
    zoomAt({ x: v.cw / 2, y: v.ch / 2 }, f);
  }

  function centreOn(x, y) {
    var m = focusMap(), v = state.view;
    if (!m || !v) return;
    v.k = Math.max(v.k, v.fitK);
    v.vx = x + 0.5 - v.cw / (6 * v.k);
    v.vy = y + 0.5 - v.ch / (4 * v.k);
    clampView(m, v);
    queueDraw();
  }

  function resetView() {
    var v = state.view, m = focusMap();
    if (!v || !m) return;
    v.k = v.fitK;
    clampView(m, v);
    queueDraw();
  }

  // ---- context menus and GM actions -------------------------------------------------------------------------------------

  var menuEl = null;

  function closeMenu() {
    if (menuEl && menuEl.parentNode) menuEl.parentNode.removeChild(menuEl);
    menuEl = null;
  }

  function openMenu(title, items, x, y) {
    closeMenu();
    var doc = global.document;
    menuEl = doc.createElement('div');
    menuEl.className = 'mon-menu';
    menuEl.setAttribute('role', 'menu');
    var h = doc.createElement('div');
    h.className = 'mon-menu-title';
    h.textContent = title;
    menuEl.appendChild(h);
    items.forEach(function (it) {
      if (it === '-') {
        var hr = doc.createElement('div');
        hr.className = 'mon-menu-sep';
        menuEl.appendChild(hr);
        return;
      }
      var b = doc.createElement('button');
      b.type = 'button';
      b.setAttribute('role', 'menuitem');
      b.className = 'mon-menu-item' + (it.danger ? ' danger' : '');
      b.textContent = it.label;
      if (it.disabled) { b.disabled = true; b.title = it.disabled; }
      b.addEventListener('click', function () {
        closeMenu();
        Promise.resolve().then(it.run).then(function (r) { if (r && r.__ok) setTimeout(function () { refresh(true); }, 400); });
      });
      menuEl.appendChild(b);
    });
    doc.body.appendChild(menuEl);
    var w = menuEl.offsetWidth, hgt = menuEl.offsetHeight;
    menuEl.style.left = Math.max(4, Math.min(global.innerWidth - w - 4, x)) + 'px';
    menuEl.style.top = Math.max(4, Math.min(global.innerHeight - hgt - 4, y)) + 'px';
    var first = menuEl.querySelector('button:not([disabled])');
    if (first) first.focus();
  }

  global.document.addEventListener('mousedown', function (e) { if (menuEl && !menuEl.contains(e.target)) closeMenu(); }, true);
  global.addEventListener('scroll', closeMenu, true);

  function target(p) { return { name: p.name, characterIndex: p.characterIndex, online: true, accountId: p.accountId, accountIndex: p.accountIndex }; }

  function runVerb(action, p) {
    if (!global.PlayerActions) { global.portalToast('Player actions are not loaded.', 'error'); return null; }
    return global.PlayerActions.run(action, target(p));
  }

  function openPlayerMenu(p, x, y) {
    var items = [
      { label: 'Zoom to player', run: function () { select(p.name, false); var v = state.view; if (v) { v.k = Math.max(v.k, v.fitK * 6); centreOn(p.x, p.y); } } },
      { label: 'Character…', run: function () { if (global.PlayerChip) global.PlayerChip.inspect(p.name, { characterIndex: p.characterIndex }); } },
      { label: 'GM Console', run: function () { global.portalOpenDeepLink('gm', { player: p.name }); } },
      { label: 'Chat log', run: function () { global.portalOpenDeepLink('chatlog', { player: p.name }); } },
      '-',
      { label: 'Message…', run: function () { return runVerb('message', p); } },
      { label: 'Teleport…', run: function () { return runVerb('teleport', p); } },
      { label: 'Send to safe zone', run: function () { return runVerb('safezone', p); } }
    ];
    if (p.dead) items.push({ label: 'Revive', run: function () { return runVerb('revive', p); } });
    items.push('-');
    items.push({ label: 'Kick', danger: true, run: function () { return runVerb('kick', p); } });
    items.push({ label: 'Kill', danger: true, disabled: p.dead ? 'Already dead' : null, run: function () { return runVerb('kill', p); } });
    openMenu(p.name + ' · Lv ' + p.level + ' ' + (p.className || ''), items, x, y);
  }

  function openMapMenu(m, cell, x, y) {
    var sel = state.selected ? findPlayerAnywhere(state.selected) : null;
    var items = [];
    items.push({
      label: sel ? 'Teleport ' + sel.player.name + ' here…' : 'Teleport selected player here…',
      disabled: sel ? null : 'Select a player first (click a pin or a row; any map)',
      run: function () { return teleportHere(sel.player, m, cell); }
    });
    items.push({ label: 'Spawn monster here…', run: function () { return spawnHere(m, cell); } });
    items.push('-');
    items.push({ label: 'Clear monsters on this map…', danger: true, run: function () { return clearMobs(m); } });
    items.push('-');
    items.push({ label: 'Copy coordinates', run: function () { copyText(m.title + ' (#' + m.index + ') ' + cell.x + ', ' + cell.y); } });
    openMenu(m.title + ' · ' + cell.x + ', ' + cell.y, items, x, y);
  }

  function copyText(text) {
    var nav = global.navigator;
    if (nav && nav.clipboard && nav.clipboard.writeText) {
      nav.clipboard.writeText(text).then(function () { global.portalToast('Copied ' + text, 'ok'); }, function () { global.portalToast(text, 'info'); });
    } else {
      global.portalToast(text, 'info');
    }
  }

  function mapBody(m, cell) {
    var b = { map: '#' + m.index };
    if (m.instance > 1) b.instance = m.instance;
    if (cell) { b.x = cell.x; b.y = cell.y; }
    return b;
  }

  async function teleportHere(p, m, cell) {
    var v = await global.OpsUI.actionDialog({
      title: 'Teleport — ' + p.name,
      message: 'Moves ' + p.name + ' to ' + m.title + (m.instance > 1 ? ' (instance ' + m.instance + ')' : '') + ' at ' + cell.x + ', ' + cell.y + '. The server refuses a cell that is not walkable.',
      confirmText: 'Teleport',
      fields: [{ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true, placeholder: 'Why?' }]
    });
    if (!v) return null;
    var body = Object.assign({ action: 'teleport', character: p.name, reason: v.reason || '' }, mapBody(m, cell));
    if (p.characterIndex) body.characterIndex = p.characterIndex;
    return global.OpsUI.post('/api/admin/gm/action', body);
  }

  async function spawnHere(m, cell) {
    var v = await global.OpsUI.actionDialog({
      title: 'Spawn monster — ' + m.title + ' ' + cell.x + ', ' + cell.y,
      message: 'Spawns monsters around this cell' + (m.instance > 1 ? ' on instance ' + m.instance : '') + '. The cell must be walkable.',
      confirmText: 'Spawn',
      fields: [
        { key: 'monster', label: 'Monster (index or name)', type: 'text', required: true, placeholder: 'e.g. Hen' },
        { key: 'count', label: 'Count', type: 'number', value: 1, min: 1, max: 50 },
        { key: 'spread', label: 'Spread (cells around the point)', type: 'number', value: 0, min: 0, max: 20 },
        { key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true, placeholder: 'Why?' }
      ]
    });
    if (!v) return null;
    var body = Object.assign({ action: 'spawnmob', monster: String(v.monster || '').trim(), reason: v.reason || '' }, mapBody(m, cell));
    if (v.count) body.count = v.count;
    if (v.spread) body.spread = v.spread;
    return global.OpsUI.post('/api/admin/gm/action', body);
  }

  async function clearMobs(m) {
    var v = await global.OpsUI.actionDialog({
      title: 'Clear monsters — ' + m.title + (m.instance > 1 ? ' (instance ' + m.instance + ')' : ''),
      message: 'Kills every monster on this map (pets are kept). Respawns refill it over time.',
      confirmText: 'Clear monsters',
      danger: true,
      fields: [{ key: 'reason', label: 'Reason (audit trail)', type: 'text', required: true, placeholder: 'Why?' }]
    });
    if (!v) return null;
    return global.OpsUI.post('/api/admin/gm/action', Object.assign({ action: 'clearmobs', reason: v.reason || '' }, mapBody(m, null)));
  }

  // ---- section registration -------------------------------------------------------------------------------------------

  loadPrefs();

  global.AdminSections.register('monitor', {
    load: function (el) { build(el); },
    show: function () {
      if (!stopPoll) stopPoll = global.AdminSections.poll('monitor', function () { return refresh(false); }, POLL_MS);
      state.view = null;
      return refresh(true);
    },
    hide: function () {
      if (stopPoll) { stopPoll(); stopPoll = null; }
      closeMenu();
      showTip(null);
    }
  });

  global.portalRegisterDeepLink('monitor', function (p) {
    var key = p.map ? String(p.map) : null;
    if (key && /^\d+$/.test(key)) key += ':1';
    if (key && /^\d+:\d+$/.test(key) && key !== state.focusKey) setFocus(key, true);
    if (p.player) {
      var hit = findPlayerAnywhere(p.player);
      if (hit && !key && hit.map.key !== state.focusKey) setFocus(hit.map.key, true);
      if (String(p.player).toLowerCase() !== state.selected) select(p.player, true);
    }
  });

  /** Opens the monitor on a player's map (used by other sections). */
  global.LiveMonitor = {
    openPlayer: function (name) { global.portalOpenDeepLink('monitor', { player: name }); },
    openMap: function (index, instance) { global.portalOpenDeepLink('monitor', { map: index + ':' + (instance || 1) }); }
  };
})(window);
