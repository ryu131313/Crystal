/**
 * Maps editor (admin section "mapeditor", Game Data group): a map list, the map canvas (admin-mapeditor-view.js) with every
 * placed object (safe zones, respawns, movements, inbound movements, mine zones, NPCs), an object list / details column, the
 * map's own fields and its child tables as grids.
 *
 * Reads: GET /api/admin/mapeditor/maps, GET /api/admin/mapeditor/maps/{i}/overlay (+ terrain / minimap / tiles / players images
 * and JSON), all from the editor copy and the server's map file, so the page works while the world is stopped.
 * Writes: never a route of its own. The grids are Game Data grids (makeDbGridEditor over /api/admin/db/{maps,mapsafezones,
 * maprespawns,mapmovements,mapminezones,npcs}) and save through GameData.persist: one LiveDb batch (dry run, preview, apply live)
 * when [LiveDb] is on, else PUT + database/save. The cell right-click menu acts on the running world through the GM verbs
 * (POST /api/admin/gm/action: teleport a GM / a player / everyone here, spawn a monster), on instance 1 of the map.
 *
 * Unsaved edits use DbSave dirty keys "mapeditor:<endpoint>", so the shell's leave-section and beforeunload guards cover them and
 * switching maps asks first. Deep link: #section=mapeditor&map=<index>[&sel=<kind>:<rowIndex>][&x=&y=].
 */
(function (global) {
  'use strict';

  var SECTION = 'mapeditor';
  var LS = 'mircraft_mapeditor_';
  var Geo = global.MapEditorGeo;

  var TABS = [
    { id: 'info', label: 'Map', endpoint: 'maps' },
    { id: 'safezone', label: 'Safe zones', endpoint: 'mapsafezones' },
    { id: 'respawn', label: 'Respawns', endpoint: 'maprespawns' },
    { id: 'movement', label: 'Movements', endpoint: 'mapmovements' },
    { id: 'mine', label: 'Mine zones', endpoint: 'mapminezones' },
    { id: 'npc', label: 'NPCs', endpoint: 'npcs' }
  ];
  var TAB_OF_KIND = { safezone: 'safezone', respawn: 'respawn', movement: 'movement', mine: 'mine', npc: 'npc' };

  var LAYERS = [
    { id: 'safezones', label: 'Safe zones', def: true },
    { id: 'respawns', label: 'Respawns', def: true },
    { id: 'movements', label: 'Portals', def: true },
    { id: 'inbound', label: 'Inbound', def: true },
    { id: 'mines', label: 'Mine zones', def: true },
    { id: 'npcs', label: 'NPCs', def: true },
    { id: 'players', label: 'Players', def: false },
    { id: 'walk', label: 'Blocked cells', def: false },
    { id: 'grid', label: 'Grid', def: true },
    { id: 'labels', label: 'Labels', def: true }
  ];

  // Summary chips of the map row: [field, label, kind]
  var CHIPS = [
    ['Light', 'Light'], ['MapDarkLight', 'Darkness'], ['WeatherParticles', 'Weather'], ['Music', 'Music'], ['MiniMap', 'Minimap'],
    ['BigMap', 'Big map'], ['MineIndex', 'Mine set'], ['RequiredGroupSize', 'Group size', 'nz'], ['FireDamage', 'Fire dmg', 'nz'],
    ['LightningDamage', 'Lightning dmg', 'nz'], ['FireWallCount', 'Fire walls', 'nz'], ['NoReconnectMap', 'Reconnect map', 'nz']
  ];

  // ---- small DOM helpers ------------------------------------------------------------------------------------------------

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function button(label, cls, title) {
    var b = el('button', 'mir-btn' + (cls ? ' ' + cls : ''), label);
    b.type = 'button';
    if (title) b.title = title;
    return b;
  }
  function lsGet(k, d) { try { var v = global.localStorage.getItem(LS + k); return v == null ? d : v; } catch (e) { return d; } }
  function lsSet(k, v) { try { global.localStorage.setItem(LS + k, String(v)); } catch (e) { /* private mode */ } }
  function num(v, d) { var n = Number(v); return isFinite(n) ? n : (d || 0); }
  function toast(msg, kind) { if (typeof global.portalToast === 'function') global.portalToast(msg, kind || 'info'); }

  function copyText(text) {
    var done = function () { toast('Copied ' + text, 'ok'); };
    if (global.navigator && navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(done, function () { fallbackCopy(text) && done(); });
    } else if (fallbackCopy(text)) done();
  }
  function fallbackCopy(text) {
    try {
      var ta = el('textarea');
      ta.value = text;
      ta.style.position = 'fixed'; ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      var ok = document.execCommand('copy');
      document.body.removeChild(ta);
      return ok;
    } catch (e) { return false; }
  }

  function typingTarget(e) {
    var t = e.target;
    if (!t) return false;
    var tag = (t.tagName || '').toUpperCase();
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || t.isContentEditable ||
      !!(t.closest && t.closest('.ag-root-wrapper'));
  }
  function modalOpen() {
    return !!document.querySelector('.portal-confirm-backdrop, #formDrawer:not(.hidden)');
  }

  // ---- state ------------------------------------------------------------------------------------------------------------

  var root = null;
  var ui = {};
  var view = null;
  var maps = [];              // GET /maps rows
  var mapsInfo = { art: null, world: null };
  var current = null;         // { index, overlay, objects, ver }
  var selectedKey = null;
  var activeTab = 'info';
  var grids = Object.create(null);   // tab id -> { ed, el, mapIndex }
  var layers = {};
  var playersStop = null;
  var listFocus = -1;
  var sideTab = 'objects';
  var objFilter = '';
  var hooks = { };            // phase 2 / 3 extensions (admin-mapeditor-edit.js)

  function dirtyKey(endpoint) { return 'mapeditor:' + endpoint; }
  var EDITS_KEY = 'mapeditor:edits';   // DbSave key of the unapplied map edits (admin-mapeditor-edit.js)
  function myDirtyKeys() {
    if (!(global.DbSave && DbSave.dirtyKeys)) return [];
    return DbSave.dirtyKeys().filter(function (k) { return k.indexOf('mapeditor:') === 0; });
  }
  function pendingEditCount() {
    var n = 0;
    Object.keys(grids).forEach(function (k) { var g = grids[k]; if (g && g.ed && g.ed.pendingCount) n += g.ed.pendingCount(); });
    if (hooks.pendingCount) n += hooks.pendingCount();
    return n;
  }

  // ---- layout -----------------------------------------------------------------------------------------------------------

  function build(el0) {
    root = el0;
    root.innerHTML = '';
    var wrap = el('div', 'me-wrap');

    var head = el('div', 'me-head');
    head.appendChild(el('h2', 'me-title', 'Maps'));
    ui.headNote = el('span', 'mir-muted me-head-note', '');
    head.appendChild(ui.headNote);
    var headTools = el('div', 'me-head-tools');
    ui.reloadList = button('Reload list', '', 'Read the map list again from the editor copy');
    ui.reloadList.addEventListener('click', function () { loadMaps(true); });
    headTools.appendChild(ui.reloadList);
    ui.openGrid = button('Open in Game Data', '', 'The Maps table in the Game Data grids');
    ui.openGrid.addEventListener('click', function () {
      if (global.DbNav) DbNav.goto('maps', current ? current.index : undefined);
    });
    headTools.appendChild(ui.openGrid);
    head.appendChild(headTools);
    wrap.appendChild(head);

    var body = el('div', 'me-body');
    ui.body = body;

    // map list
    var list = el('aside', 'me-list mir-card');
    ui.list = list;
    var lhead = el('div', 'me-list-head');
    ui.filter = el('input', 'mir-input me-filter');
    ui.filter.type = 'search';
    ui.filter.placeholder = 'Filter maps (index, file, title)…';
    ui.filter.setAttribute('aria-label', 'Filter maps');
    ui.filter.addEventListener('input', function () { listFocus = -1; renderList(); });
    ui.filter.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown') { e.preventDefault(); focusListItem(0); }
      else if (e.key === 'Enter') { e.preventDefault(); var f = filteredMaps()[0]; if (f) openMap(f.index); }
      else if (e.key === 'Escape') { if (ui.filter.value) { ui.filter.value = ''; renderList(); } else closeDrop(); }
    });
    lhead.appendChild(ui.filter);
    ui.pin = button('Pin', 'me-pin', 'Keep the map list docked beside the map');
    ui.pin.setAttribute('aria-pressed', 'false');
    ui.pin.addEventListener('click', function () { setPinned(!pinned()); });
    lhead.appendChild(ui.pin);
    var thumbLab = el('label', 'me-thumbs-toggle', ' Thumbnails');
    var thumbCb = el('input');
    thumbCb.type = 'checkbox';
    thumbCb.checked = thumbsOn();
    thumbCb.addEventListener('change', function () { lsSet('thumbs', thumbCb.checked ? '1' : '0'); renderList(); });
    thumbLab.insertBefore(thumbCb, thumbLab.firstChild);
    thumbLab.title = 'Show a small walkability picture of every map in the list';
    list.appendChild(lhead);
    list.appendChild(thumbLab);
    ui.ul = el('ul', 'me-map-list');
    ui.ul.setAttribute('role', 'listbox');
    ui.ul.setAttribute('aria-label', 'Maps');
    ui.ul.addEventListener('keydown', onListKey);
    list.appendChild(ui.ul);
    ui.listStatus = el('div', 'me-list-status mir-muted', '');
    list.appendChild(ui.listStatus);
    body.appendChild(list);

    // main column
    var main = el('div', 'me-main');
    ui.main = main;
    var mapCard = el('div', 'me-map-card mir-card');
    ui.mapCard = mapCard;

    var tb = el('div', 'me-toolbar');
    ui.picker = button('▾ Pick a map', 'me-map-picker', 'Choose another map (the list)');
    ui.picker.setAttribute('aria-haspopup', 'listbox');
    ui.picker.addEventListener('click', function (e) { e.stopPropagation(); toggleDrop(); });
    tb.appendChild(ui.picker);

    var baseLab = el('label', 'me-tb-label', 'Base ');
    ui.base = el('select', 'mir-input me-base');
    ui.base.setAttribute('aria-label', 'Base image');
    ui.base.addEventListener('change', function () { lsSet('base', ui.base.value); if (view) view.setBase(ui.base.value); });
    baseLab.appendChild(ui.base);
    tb.appendChild(baseLab);

    ui.layersBtn = button('Layers ▾', 'me-layers-btn', 'Show or hide map layers');
    ui.layersBtn.setAttribute('aria-haspopup', 'true');
    ui.layersPop = el('div', 'me-layers-pop hidden');
    LAYERS.forEach(function (l) {
      var lab = el('label', 'me-layer');
      var cb = el('input');
      cb.type = 'checkbox';
      cb.checked = !!layers[l.id];
      cb.addEventListener('change', function () {
        layers[l.id] = cb.checked;
        lsSet('layer_' + l.id, cb.checked ? '1' : '0');
        if (view) view.setLayers(layers);
        if (l.id === 'players') syncPlayersPoll();
        renderObjects();
      });
      lab.appendChild(cb);
      lab.appendChild(document.createTextNode(' ' + l.label));
      ui.layersPop.appendChild(lab);
    });
    ui.layersBtn.addEventListener('click', function (e) { e.stopPropagation(); ui.layersPop.classList.toggle('hidden'); });
    var layWrap = el('span', 'me-layers-wrap');
    layWrap.appendChild(ui.layersBtn);
    layWrap.appendChild(ui.layersPop);
    tb.appendChild(layWrap);

    ui.coords = button('—', 'me-coords', 'Cell under the mouse. Click to copy it.');
    ui.coords.addEventListener('click', function () { if (ui.coords.dataset.cell) copyText(ui.coords.dataset.cell); });
    tb.appendChild(ui.coords);

    ui.goto = el('input', 'mir-input me-goto');
    ui.goto.type = 'text';
    ui.goto.placeholder = 'Go to x, y';
    ui.goto.title = 'Type "x, y" and press Enter to centre the map there (G)';
    ui.goto.setAttribute('aria-label', 'Go to cell');
    ui.goto.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { ui.goto.value = ''; ui.goto.blur(); return; }
      if (e.key !== 'Enter') return;
      e.preventDefault();
      var m = /^\s*(\d+)\s*[, ]\s*(\d+)\s*$/.exec(ui.goto.value);
      if (!m) { toast('Type a cell as "x, y".', 'error'); return; }
      if (!current) return;
      view.showCell(num(m[1]), num(m[2]), true);
      global.portalSetHashParams({ x: m[1], y: m[2] }, true);
    });
    tb.appendChild(ui.goto);
    var spacer = el('span', 'me-spacer');
    tb.appendChild(spacer);
    ui.edit = el('span', 'me-edit-tools');   // phase 2 fills this
    tb.appendChild(ui.edit);
    var zoomOut = button('−', 'me-icon-btn', 'Zoom out (-)');
    zoomOut.addEventListener('click', function () { if (view) view.zoomBy(1 / 1.4); });
    var zoomIn = button('+', 'me-icon-btn', 'Zoom in (+)');
    zoomIn.addEventListener('click', function () { if (view) view.zoomBy(1.4); });
    var fit = button('Fit', '', 'Fit the whole map (0)');
    fit.addEventListener('click', function () { if (view) view.fit(); });
    ui.mapOnly = button('Map only', '', 'Hide the list and the side column (\\)');
    ui.mapOnly.addEventListener('click', function () { setMapOnly(!root.classList.contains('me-map-only')); });
    ui.full = button('Full screen', '', 'Fill the window (Esc leaves)');
    ui.full.addEventListener('click', function () { setFull(!mapCard.classList.contains('me-fullscreen')); });
    var help = button('?', 'me-icon-btn', 'Keys and mouse (?)');
    help.addEventListener('click', showHelp);
    [zoomOut, zoomIn, fit, ui.mapOnly, ui.full, help].forEach(function (b) { tb.appendChild(b); });
    mapCard.appendChild(tb);
    ui.editRow = el('div', 'me-edit-row hidden');   // edit-mode tools (admin-mapeditor-edit.js)
    mapCard.appendChild(ui.editRow);

    var stage = el('div', 'me-stage');
    ui.canvasHost = el('div', 'me-canvas-host');
    ui.canvasHost.style.height = lsGet('height', '') || '';
    stage.appendChild(ui.canvasHost);

    var side = el('div', 'me-side');
    ui.side = side;
    var stabs = el('div', 'database-tabs me-side-tabs');
    stabs.setAttribute('role', 'tablist');
    ui.sideTabs = {};
    [['objects', 'Objects'], ['details', 'Details']].forEach(function (t) {
      var b = button(t[1], 'me-side-tab');
      b.setAttribute('role', 'tab');
      b.addEventListener('click', function () { showSideTab(t[0]); });
      ui.sideTabs[t[0]] = b;
      stabs.appendChild(b);
    });
    side.appendChild(stabs);
    ui.objectsPane = el('div', 'me-side-pane');
    var of = el('input', 'mir-input me-obj-filter');
    of.type = 'search';
    of.placeholder = 'Filter objects…';
    of.setAttribute('aria-label', 'Filter objects');
    of.addEventListener('input', function () { objFilter = of.value.trim().toLowerCase(); renderObjects(); });
    ui.objectsPane.appendChild(of);
    ui.objList = el('div', 'me-obj-list');
    ui.objectsPane.appendChild(ui.objList);
    side.appendChild(ui.objectsPane);
    ui.detailsPane = el('div', 'me-side-pane hidden');
    side.appendChild(ui.detailsPane);
    stage.appendChild(side);
    mapCard.appendChild(stage);

    ui.infoBar = el('div', 'me-infobar mir-muted', '');
    mapCard.appendChild(ui.infoBar);
    ui.pendingBox = el('div', 'me-pending hidden');   // phase 2
    mapCard.appendChild(ui.pendingBox);
    main.appendChild(mapCard);

    // tables
    var tables = el('div', 'me-tables mir-card');
    var ttabs = el('div', 'database-tabs gd-tabs me-table-tabs');
    ttabs.setAttribute('role', 'tablist');
    ui.tableTabs = {};
    TABS.forEach(function (t) {
      var b = button(t.label, 'me-table-tab');
      b.setAttribute('role', 'tab');
      b.addEventListener('click', function () { showTable(t.id); });
      ui.tableTabs[t.id] = b;
      ttabs.appendChild(b);
    });
    tables.appendChild(ttabs);
    ui.chips = el('div', 'me-chips');
    tables.appendChild(ui.chips);
    ui.tableHost = el('div', 'me-table-host');
    tables.appendChild(ui.tableHost);
    main.appendChild(tables);
    body.appendChild(main);
    wrap.appendChild(body);
    root.appendChild(wrap);

    ui.drop = list;   // the list doubles as the picker's drop-down when not pinned

    view = global.MapEditorView.create(ui.canvasHost, {
      onHover: function (c) {
        if (!c) { ui.coords.textContent = '—'; delete ui.coords.dataset.cell; return; }
        var w = view.walkable(c.x, c.y);
        ui.coords.textContent = c.x + ', ' + c.y + (w === false ? ' (blocked)' : '');
        ui.coords.dataset.cell = c.x + ', ' + c.y;
      },
      onSelect: function (o) { setSelected(o ? o.key : null, { from: 'map' }); },
      onActivate: activate,
      onContext: function (c, e) {
        if (hooks.onContext && hooks.onContext(c)) return;
        openCellMenu(c, e);
      },
      onWalkability: function () { renderInfoBar(); if (hooks.onWalkability) hooks.onWalkability(); },
      onBaseChange: renderBaseOptions,
      onPointerDown: function (e, c, p) { return hooks.onPointerDown ? hooks.onPointerDown(e, c, p) : false; },
      onCellClick: function (e, c) { return hooks.onCellClick ? hooks.onCellClick(e, c) : false; },
      drawSelection: function (ctx, v, o, h) { if (hooks.drawSelection) hooks.drawSelection(ctx, v, o, h); },
      cursor: function (c, e) { return hooks.cursor ? hooks.cursor(c, e) : ''; }
    });
    view.setLayers(layers);
    view.setBase(lsGet('base', 'art'));

    // Height: the resize corner of the canvas host is remembered.
    if (typeof global.ResizeObserver === 'function') {
      var t = null;
      new global.ResizeObserver(function () {
        clearTimeout(t);
        t = setTimeout(function () {
          if (mapCard.classList.contains('me-fullscreen') || !ui.canvasHost.style.height) return;
          lsSet('height', ui.canvasHost.style.height);
        }, 400);
      }).observe(ui.canvasHost);
    }

    document.addEventListener('mousedown', function (e) {
      if (!ui.layersPop.classList.contains('hidden') && !layWrap.contains(e.target)) ui.layersPop.classList.add('hidden');
      if (root.classList.contains('me-drop-open') && !ui.list.contains(e.target) && e.target !== ui.picker) closeDrop();
    });
    document.addEventListener('keydown', onKey);

    if (global.DbSave && DbSave.onSaved) DbSave.onSaved(onTableSaved);
    // Dirty marks on the table tabs (the grids set their DbSave keys on every cell edit).
    if (global.AdminSections && AdminSections.poll) {
      AdminSections.poll(SECTION, renderTableTabs, 1500);
      AdminSections.poll(SECTION, refreshOnline, 10000);
    }

    setPinned(pinned());
    setMapOnly(lsGet('maponly', '0') === '1', true);
    showSideTab('objects');
    renderTableTabs();
    renderBaseOptions();
    if (global.MapEditorEdit && typeof global.MapEditorEdit.attach === 'function') global.MapEditorEdit.attach(api);
  }

  function readLayers() {
    LAYERS.forEach(function (l) {
      var v = lsGet('layer_' + l.id, null);
      layers[l.id] = v == null ? l.def : v === '1';
    });
  }

  // ---- list -------------------------------------------------------------------------------------------------------------

  function pinned() { return lsGet('pinned', '1') === '1'; }
  function setPinned(on) {
    lsSet('pinned', on ? '1' : '0');
    root.classList.toggle('me-list-pinned', on);
    ui.pin.setAttribute('aria-pressed', on ? 'true' : 'false');
    ui.pin.textContent = on ? 'Unpin' : 'Pin';
    closeDrop();
    if (view) view.redraw();
  }
  function toggleDrop() {
    if (pinned() && !root.classList.contains('me-map-only')) { ui.filter.focus(); return; }
    var open = !root.classList.contains('me-drop-open');
    root.classList.toggle('me-drop-open', open);
    if (open) { ui.filter.focus(); ui.filter.select(); }
  }
  function closeDrop() { if (root) root.classList.remove('me-drop-open'); }

  function filteredMaps() {
    var f = (ui.filter && ui.filter.value || '').trim().toLowerCase();
    if (!f) return maps;
    return maps.filter(function (m) {
      return (String(m.index) + ' ' + (m.fileName || '') + ' ' + (m.title || '')).toLowerCase().indexOf(f) >= 0;
    });
  }

  function renderList() {
    if (!ui.ul) return;
    var rows = filteredMaps();
    ui.ul.innerHTML = '';
    var frag = document.createDocumentFragment();
    rows.forEach(function (m, i) {
      var li = el('li', 'me-map-item');
      li.setAttribute('role', 'option');
      li.setAttribute('data-index', m.index);
      li.tabIndex = i === Math.max(0, listFocus) ? 0 : -1;
      var sel = current && current.index === m.index;
      li.setAttribute('aria-selected', sel ? 'true' : 'false');
      if (sel) li.classList.add('me-current');
      li.appendChild(el('span', 'me-map-idx', String(m.index)));
      var name = el('span', 'me-map-name', m.title || m.fileName || '(no title)');
      name.title = (m.title || '') + (m.fileName ? ' — ' + m.fileName : '');
      li.appendChild(name);
      var meta = [];
      if (m.respawns) meta.push(m.respawns + ' spawn');
      if (m.movements) meta.push(m.movements + ' portal');
      if (m.npcs) meta.push(m.npcs + ' NPC');
      if (meta.length) li.appendChild(el('span', 'me-map-meta', meta.join(' · ')));
      var pb = el('span', 'me-badge');
      li.appendChild(pb);
      setBadge(pb, online[m.index] || 0);
      if (thumbsOn()) {
        var th = el('img', 'me-thumb');
        th.alt = '';
        th.setAttribute('data-src', '/api/admin/mapeditor/maps/' + encodeURIComponent(m.index) + '/terrain');
        th.addEventListener('load', function () {
          // one pixel per cell; cells are 3:2 on screen
          var w = th.naturalWidth || 1, h = th.naturalHeight || 1;
          th.style.height = Math.max(8, Math.min(120, Math.round(150 * (2 * h) / (3 * w)))) + 'px';
        });
        th.addEventListener('error', function () { th.classList.add('hidden'); });
        li.appendChild(th);
      }
      li.addEventListener('click', function () { openMap(m.index); });
      frag.appendChild(li);
    });
    ui.ul.appendChild(frag);
    observeThumbs();
    ui.listStatus.textContent = rows.length === maps.length ? maps.length + ' map(s)' : rows.length + ' / ' + maps.length + ' map(s)';
  }

  function setBadge(pb, n) {
    pb.textContent = n ? String(n) : '';
    pb.classList.toggle('hidden', !n);
    pb.title = n ? n + ' player(s) online on this map' : '';
  }

  // ---- online players per map (list badges), every 10 s while the section is visible ----
  var online = Object.create(null);
  async function refreshOnline() {
    var res = await global.OpsUI.fetchResult('/api/admin/mapeditor/online');
    if (!res.ok || !res.data) return;
    var counts = res.data.counts || {};
    online = Object.create(null);
    Object.keys(counts).forEach(function (k) { online[k] = Number(counts[k]) || 0; });
    if (!ui.ul) return;
    Array.prototype.forEach.call(ui.ul.querySelectorAll('.me-map-item'), function (li) {
      var b = li.querySelector('.me-badge');
      if (b) setBadge(b, online[li.getAttribute('data-index')] || 0);
    });
    var total = Object.keys(online).reduce(function (n, k) { return n + online[k]; }, 0);
    ui.listStatus.textContent = ui.listStatus.textContent.replace(/ · \d+ online$/, '') + (total ? ' · ' + total + ' online' : '');
  }

  // ---- list thumbnails (walkability images, loaded when scrolled into view) ----
  function thumbsOn() { return lsGet('thumbs', '0') === '1'; }
  var thumbObserver = null;
  function observeThumbs() {
    if (!ui.ul || !thumbsOn()) return;
    var imgs = ui.ul.querySelectorAll('img.me-thumb[data-src]');
    if (typeof global.IntersectionObserver !== 'function') {
      Array.prototype.forEach.call(imgs, function (img) { img.src = img.getAttribute('data-src'); img.removeAttribute('data-src'); });
      return;
    }
    if (!thumbObserver) {
      thumbObserver = new global.IntersectionObserver(function (entries) {
        entries.forEach(function (en) {
          if (!en.isIntersecting) return;
          var img = en.target;
          thumbObserver.unobserve(img);
          if (img.getAttribute('data-src')) { img.src = img.getAttribute('data-src'); img.removeAttribute('data-src'); }
        });
      }, { root: ui.ul, rootMargin: '200px' });
    }
    Array.prototype.forEach.call(imgs, function (img) { thumbObserver.observe(img); });
  }

  function listItems() { return ui.ul ? Array.prototype.slice.call(ui.ul.querySelectorAll('.me-map-item')) : []; }
  function focusListItem(i) {
    var items = listItems();
    if (!items.length) return;
    i = Math.max(0, Math.min(items.length - 1, i));
    items.forEach(function (it, n) { it.tabIndex = n === i ? 0 : -1; });
    listFocus = i;
    items[i].focus();
    if (items[i].scrollIntoView) items[i].scrollIntoView({ block: 'nearest' });
  }
  function onListKey(e) {
    var items = listItems();
    var i = items.indexOf(document.activeElement);
    if (e.key === 'ArrowDown') { e.preventDefault(); focusListItem(i + 1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); if (i <= 0) ui.filter.focus(); else focusListItem(i - 1); }
    else if (e.key === 'Home') { e.preventDefault(); focusListItem(0); }
    else if (e.key === 'End') { e.preventDefault(); focusListItem(items.length - 1); }
    else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      if (i >= 0) openMap(num(items[i].getAttribute('data-index')));
    } else if (e.key === 'Escape') { closeDrop(); }
  }

  async function loadMaps(force) {
    var res = await global.OpsUI.fetchResult('/api/admin/mapeditor/maps');
    if (!res.ok) { toast('Maps: ' + res.error, 'error'); return; }
    var d = res.data || {};
    maps = Array.isArray(d.maps) ? d.maps : [];
    mapsInfo = { art: d.art || null, world: d.world || null };
    renderHeadNote();
    renderList();
    refreshOnline();
    if (force && current) await reloadOverlay(true);
  }

  function renderHeadNote() {
    if (!ui.headNote) return;
    var live = global.MirLiveDb && MirLiveDb.enabled && MirLiveDb.enabled();
    var parts = [];
    parts.push(live
      ? 'LiveDb: saves apply to the running world (map file and new / deleted maps after a restart).'
      : 'Saves go to the editor copy and the database file; the running world picks them up after a restart.');
    var art = mapsInfo.art;
    if (!art || !art.tiles) parts.push('Map art is off ([WebAdmin] ClientDataPath): showing walkability only.');
    ui.headNote.textContent = parts.join(' ');
  }

  // ---- opening a map ----------------------------------------------------------------------------------------------------

  async function confirmLeaveMap() {
    var keys = myDirtyKeys();
    var pend = pendingEditCount();
    if (!keys.length && !pend) return true;
    var what = [];
    keys.forEach(function (k) {
      if (k === EDITS_KEY) return;   // the map edits: described by hooks.pendingSummary
      var ep = k.slice('mapeditor:'.length);
      var t = TABS.filter(function (x) { return x.endpoint === ep; })[0];
      what.push(t ? t.label + ' grid' : ep);
    });
    if (hooks.pendingSummary) { var s = hooks.pendingSummary(); if (s) what.unshift(s); }
    var ok = await global.portalConfirm('This map has unsaved edits (' + (what.join(', ') || pend + ' change(s)') + '). Switching maps discards them.',
      { title: 'Unsaved map edits', confirmLabel: 'Discard and switch' });
    if (ok) discardAll();
    return ok;
  }

  function discardAll() {
    destroyGrids();
    if (hooks.discard) hooks.discard();
  }

  async function openMap(index, opts) {
    opts = opts || {};
    index = num(index, -1);
    if (index < 0) return;
    closeDrop();
    if (current && current.index === index && !opts.force) {
      applyLinkOpts(opts);
      return;
    }
    if (current && current.index !== index && !(await confirmLeaveMap())) {
      if (current) global.portalSetHashParams({ map: current.index }, true);
      return;
    }
    destroyGrids();
    if (hooks.discard) hooks.discard();
    current = { index: index, overlay: null, objects: [], ver: String(Date.now()) };
    selectedKey = null;
    global.portalSetHashParams({ map: index, sel: null, x: null, y: null }, !!opts.replaceHash);
    renderList();
    var m = maps.filter(function (x) { return x.index === index; })[0];
    ui.picker.textContent = '▾ ' + index + ' — ' + (m ? (m.title || m.fileName) : 'map');
    await reloadOverlay(false);
    showTable(activeTab, true);
    applyLinkOpts(opts);
    lsSet('last', index);
  }

  function applyLinkOpts(opts) {
    if (!opts || !current) return;
    if (opts.sel) {
      var o = view.find(opts.sel);
      if (o) { setSelected(o.key, { from: 'link' }); view.showCell(o.x, o.y, false, false); }
      else global.portalSetHashParams({ sel: null }, true);   // e.g. a new object that was never applied
    } else if (opts.x != null && opts.y != null && isFinite(Number(opts.x)) && isFinite(Number(opts.y))) {
      view.showCell(num(opts.x), num(opts.y), true);
    }
  }

  /** Re-read the overlay of the current map (keepView: same zoom/position, e.g. after a save). */
  async function reloadOverlay(keepView) {
    if (!current) return;
    var idx = current.index;
    var res = await global.OpsUI.fetchResult('/api/admin/mapeditor/maps/' + encodeURIComponent(idx) + '/overlay');
    if (!current || current.index !== idx) return;
    if (!res.ok) { toast('Map #' + idx + ': ' + res.error, 'error'); return; }
    var ov = res.data;
    var prevView = keepView ? view.view() : null;
    var sel = selectedKey;
    current.overlay = ov;
    current.objects = Geo.objectsFromOverlay(ov);
    // A reload of the same map (after a save) keeps the images, tiles and the view; only the objects change.
    if (!keepView || !current.loadedOnce) {
      current.ver = String(Date.now());
      view.setMap(ov, ov.art, current.ver);
    }
    current.loadedOnce = true;
    view.setObjects(current.objects);
    if (prevView) { var nv = view.view(); if (nv && nv !== prevView) { nv.k = prevView.k; nv.vx = prevView.vx; nv.vy = prevView.vy; } }
    // The editor re-applies its pending edits first, so a selected new (not yet applied) object is still found.
    if (hooks.onOverlay) hooks.onOverlay(ov);
    if (sel && view.find(sel)) { view.select(sel); selectedKey = sel; } else {
      selectedKey = null;
      view.select(null);
      if (sel) global.portalSetHashParams({ sel: null }, true);
    }
    renderBaseOptions();
    renderInfoBar();
    renderObjects();
    renderDetails();
    renderChips();
    syncPlayersPoll();
  }

  // ---- base / info bar ---------------------------------------------------------------------------------------------------

  function renderBaseOptions() {
    if (!ui.base) return;
    var av = view ? view.baseAvailable() : { art: false, walk: true, minimap: false };
    var want = lsGet('base', 'art');
    var opts = [];
    if (av.art) opts.push(['art', 'Map art (tiles)']);
    if (av.walk) opts.push(['walk', 'Walkability']);
    if (av.minimap) opts.push(['minimap', 'Minimap']);
    if (!opts.length) opts.push(['walk', 'No image available']);
    ui.base.innerHTML = '';
    opts.forEach(function (o) {
      var op = el('option', null, o[1]);
      op.value = o[0];
      ui.base.appendChild(op);
    });
    var has = opts.some(function (o) { return o[0] === want; });
    ui.base.value = has ? want : opts[0][0];
    if (view) view.setBase(ui.base.value);
  }

  function renderInfoBar() {
    if (!ui.infoBar) return;
    ui.infoBar.innerHTML = '';
    if (!current || !current.overlay) return;
    var ov = current.overlay, m = ov.map || {};
    var parts = [];
    if (ov.hasMapFile) parts.push(ov.width + '×' + ov.height + ' cells · ' + num(ov.walkableCells).toLocaleString() + ' walkable');
    else parts.push('map file "' + (ov.fileName || '') + '.map" not found');
    var spawnMobs = (ov.respawns || []).reduce(function (n, r) { return n + num(r.Count); }, 0);
    parts.push((ov.respawns || []).length + ' respawns (' + spawnMobs + ' monsters)');
    parts.push((ov.safeZones || []).length + ' safe zones');
    parts.push((ov.movements || []).length + ' portals out · ' + (ov.inbound || []).length + ' in');
    parts.push((ov.mineZones || []).length + ' mine zones');
    parts.push((ov.npcs || []).length + ' NPCs');
    var flags = [];
    if (m.Fight) flags.push('Fight');
    if (m.NoTeleport) flags.push('No teleport');
    if (m.NoRandom) flags.push('No random');
    if (m.NoRecall) flags.push('No recall');
    if (m.NoEscape) flags.push('No escape');
    if (m.NoPosition) flags.push('No position');
    if (m.NeedHole) flags.push('Need hole');
    if (m.Fire) flags.push('Fire');
    if (m.Lightning) flags.push('Lightning');
    if (m.NoMount) flags.push('No mount');
    if (m.NoFight) flags.push('No fight');
    if (m.NoExperience) flags.push('No exp');
    ui.infoBar.appendChild(el('span', null, parts.join(' · ')));
    if (flags.length) ui.infoBar.appendChild(el('span', 'me-flags', flags.join(' · ')));
    if (hooks.renderInfo) hooks.renderInfo(ui.infoBar);
  }

  // ---- side column ------------------------------------------------------------------------------------------------------

  function showSideTab(id) {
    sideTab = id;
    Object.keys(ui.sideTabs).forEach(function (k) {
      ui.sideTabs[k].classList.toggle('primary', k === id);
      ui.sideTabs[k].setAttribute('aria-selected', k === id ? 'true' : 'false');
    });
    ui.objectsPane.classList.toggle('hidden', id !== 'objects');
    ui.detailsPane.classList.toggle('hidden', id !== 'details');
  }

  var KIND_ORDER = ['npc', 'safezone', 'respawn', 'movement', 'inbound', 'mine'];
  var LAYER_OF_KIND = { safezone: 'safezones', respawn: 'respawns', movement: 'movements', inbound: 'inbound', mine: 'mines', npc: 'npcs' };

  function renderObjects() {
    if (!ui.objList) return;
    ui.objList.innerHTML = '';
    if (!current) return;
    var objs = current.objects;
    var frag = document.createDocumentFragment();
    KIND_ORDER.forEach(function (kind) {
      var list = objs.filter(function (o) {
        if (o.kind !== kind) return false;
        if (!objFilter) return true;
        return (o.name + ' ' + o.x + ',' + o.y + ' #' + o.index).toLowerCase().indexOf(objFilter) >= 0;
      });
      if (!list.length) return;
      var hidden = layers[LAYER_OF_KIND[kind]] === false;
      var h = el('div', 'me-obj-group' + (hidden ? ' me-obj-hidden' : ''), Geo.KIND[kind].plural + ' (' + list.length + ')' + (hidden ? ' — layer hidden' : ''));
      frag.appendChild(h);
      list.forEach(function (o) {
        var b = el('button', 'me-obj me-k-' + o.kind + (o.key === selectedKey ? ' me-obj-sel' : ''));
        b.type = 'button';
        b.setAttribute('data-key', o.key);
        b.appendChild(el('span', 'me-swatch'));
        b.appendChild(el('span', 'me-obj-name', o.name));
        b.appendChild(el('span', 'me-obj-xy mir-muted', o.x + ',' + o.y + (o.r ? ' ±' + o.r : '')));
        b.addEventListener('click', function () { setSelected(o.key, { from: 'list' }); view.showCell(o.x, o.y, false, true); });
        b.addEventListener('dblclick', function () { view.showCell(o.x, o.y, false, false); });
        frag.appendChild(b);
      });
    });
    if (!frag.childNodes.length) frag.appendChild(el('p', 'mir-muted', objFilter ? 'Nothing matches.' : 'Nothing is placed on this map.'));
    ui.objList.appendChild(frag);
  }

  function setSelected(key, opts) {
    opts = opts || {};
    selectedKey = key || null;
    view.select(selectedKey);
    global.portalSetHashParams({ sel: selectedKey }, true);
    Array.prototype.forEach.call(ui.objList.querySelectorAll('.me-obj'), function (b) {
      var on = b.getAttribute('data-key') === selectedKey;
      b.classList.toggle('me-obj-sel', on);
      if (on && opts.from === 'map' && b.scrollIntoView) b.scrollIntoView({ block: 'nearest' });
    });
    renderDetails();
    if (selectedKey && opts.from === 'map') showSideTab('details');
    if (hooks.onSelect) hooks.onSelect(view.find(selectedKey));
  }

  function fieldLine(dl, label, value) {
    dl.appendChild(el('dt', null, label));
    dl.appendChild(el('dd', null, value == null || value === '' ? '—' : String(value)));
  }

  function renderDetails() {
    var pane = ui.detailsPane;
    if (!pane) return;
    pane.innerHTML = '';
    var o = selectedKey ? view.find(selectedKey) : null;
    if (!o) {
      pane.appendChild(el('p', 'mir-muted', 'Click an object on the map or in the Objects list.'));
      return;
    }
    var k = Geo.KIND[o.kind];
    var head = el('div', 'me-det-head');
    head.appendChild(el('span', 'me-det-kind me-k-' + o.kind, k.label));
    head.appendChild(el('strong', null, o.name));
    pane.appendChild(head);
    var dl = el('dl', 'me-det');
    var r = o.row;
    fieldLine(dl, o.kind === 'movement' ? 'Source' : o.kind === 'inbound' ? 'Arrives at' : 'Location', o.x + ', ' + o.y);
    if (o.kind === 'safezone') { fieldLine(dl, 'Size', r.Size); fieldLine(dl, 'Start point', r.StartPoint ? 'yes (new characters / revive)' : 'no'); }
    if (o.kind === 'mine') { fieldLine(dl, 'Size', r.Size); fieldLine(dl, 'Mine set', r.Mine); }
    if (o.kind === 'respawn') {
      fieldLine(dl, 'Monster', (r.__monster || '?') + ' (#' + r.MonsterIndex + ')' + (r.__boss ? ' — boss' : ''));
      fieldLine(dl, 'Count', r.Count);
      fieldLine(dl, 'Spread', r.Spread);
      fieldLine(dl, 'Delay', r.Delay + ' min' + (num(r.RandomDelay) ? ' (+0–' + r.RandomDelay + ' random)' : ''));
      fieldLine(dl, 'Direction', r.Direction);
      if (r.RoutePath) fieldLine(dl, 'Route', r.RoutePath);
      if (r.SaveRespawnTime) fieldLine(dl, 'Saved timer', (r.RespawnTicks || 0) + ' ticks');
      var walk = view.walk(), sz = view.size();
      var wc = Geo.walkableIn(o, walk, sz.w, sz.h);
      if (wc >= 0) fieldLine(dl, 'Walkable cells', wc + (wc === 0 ? ' — nothing can spawn here' : ''));
    }
    if (o.kind === 'movement') {
      fieldLine(dl, 'Destination', (o.dest.same ? 'this map' : (o.dest.title || '?') + ' (#' + o.dest.map + ')') + ' at ' + o.dest.x + ', ' + o.dest.y);
      var flags = [];
      if (r.NeedHole) flags.push('needs a hole');
      if (r.NeedMove) flags.push('needs a script move');
      if (r.ShowOnBigMap) flags.push('shown on the big map (icon ' + r.Icon + ')');
      if (num(r.ConquestIndex)) flags.push('conquest #' + r.ConquestIndex);
      fieldLine(dl, 'Options', flags.join(', ') || 'none');
    }
    if (o.kind === 'inbound') {
      fieldLine(dl, 'From', (o.from.title || '?') + ' (#' + o.from.map + ') at ' + o.from.x + ', ' + o.from.y);
    }
    if (o.kind === 'npc') {
      fieldLine(dl, 'File', r.FileName);
      fieldLine(dl, 'Image', r.Image);
      if (view.walkable(o.x, o.y) === false) fieldLine(dl, 'Warning', 'stands on a blocked cell: it is not placed');
    }
    var w = view.walkable(o.x, o.y);
    if (w === false && o.kind !== 'npc') fieldLine(dl, 'Cell', 'blocked');
    if (o.dupKey) fieldLine(dl, 'Warning', 'another ' + Geo.KIND[o.kind].label.toLowerCase() + ' row has the same key (#' + o.index + '): the grid cannot save this row until the keys are made unique');
    pane.appendChild(dl);

    var acts = el('div', 'me-det-actions');
    if (o.kind !== 'inbound') {
      var row = button('Show row', '', 'Select this row in the table below');
      row.addEventListener('click', function () { focusRowOf(o); });
      acts.appendChild(row);
    }
    if (o.kind === 'movement') {
      var go = button(o.dest.same ? 'Go to destination' : 'Open destination map', '', 'Double-click on the map does the same');
      go.addEventListener('click', function () { activate(o); });
      acts.appendChild(go);
    }
    if (o.kind === 'inbound') {
      var from = button('Open source map', '');
      from.addEventListener('click', function () { activate(o); });
      acts.appendChild(from);
    }
    if (o.kind === 'respawn' && global.DbNav) {
      var mon = button('Open monster', '', 'The monster in Game Data');
      mon.addEventListener('click', function () { DbNav.goto('monsters', r.MonsterIndex); });
      acts.appendChild(mon);
    }
    var copy = button('Copy x, y', '');
    copy.addEventListener('click', function () { copyText(o.x + ', ' + o.y); });
    acts.appendChild(copy);
    pane.appendChild(acts);
    if (hooks.renderDetails) hooks.renderDetails(pane, o);
  }

  function activate(o) {
    if (!o) return;
    if (o.kind === 'movement') {
      if (o.dest.same) { view.showCell(o.dest.x, o.dest.y, true, true); return; }
      openMap(o.dest.map, { x: o.dest.x, y: o.dest.y });
    } else if (o.kind === 'inbound') {
      openMap(o.from.map, { sel: 'movement:' + o.index });
    } else {
      focusRowOf(o);
    }
  }

  // ---- tables -----------------------------------------------------------------------------------------------------------

  function renderTableTabs() {
    var ov = current && current.overlay;
    TABS.forEach(function (t) {
      var b = ui.tableTabs[t.id];
      var n = !ov ? null : t.id === 'safezone' ? (ov.safeZones || []).length : t.id === 'respawn' ? (ov.respawns || []).length :
        t.id === 'movement' ? (ov.movements || []).length : t.id === 'mine' ? (ov.mineZones || []).length : t.id === 'npc' ? (ov.npcs || []).length : null;
      b.textContent = t.label + (n != null ? ' (' + n + ')' : '');
      var dirty = global.DbSave && DbSave.isDirty && DbSave.isDirty(dirtyKey(t.endpoint));
      b.classList.toggle('me-tab-dirty', !!dirty);
      b.classList.toggle('primary', t.id === activeTab);
      b.setAttribute('aria-selected', t.id === activeTab ? 'true' : 'false');
    });
  }

  function destroyGrids() {
    Object.keys(grids).forEach(function (k) {
      var g = grids[k];
      if (g && g.ed && g.ed.destroy) g.ed.destroy();
      if (g && g.el && g.el.parentNode) g.el.parentNode.removeChild(g.el);
    });
    grids = Object.create(null);
  }

  function ensureGrid(tabId) {
    if (!current) return null;
    var g = grids[tabId];
    if (g && g.mapIndex === current.index) return g;
    if (g) { if (g.ed && g.ed.destroy) g.ed.destroy(); if (g.el.parentNode) g.el.parentNode.removeChild(g.el); }
    var t = TABS.filter(function (x) { return x.id === tabId; })[0];
    var pane = el('div', 'me-table-pane hidden');
    var gridEl = el('div', 'ag-theme-quartz-dark grid-panel me-grid' + (tabId === 'info' ? ' me-grid-one' : ''));
    gridEl.id = 'meGrid_' + tabId + '_' + current.index + '_' + Date.now();
    pane.appendChild(gridEl);
    ui.tableHost.appendChild(pane);
    if (typeof global.makeDbGridEditor !== 'function' || typeof agGrid === 'undefined') {
      pane.appendChild(el('p', 'mir-muted', 'The grid library did not load (check the connection to cdn.jsdelivr.net).'));
      grids[tabId] = { ed: null, el: pane, mapIndex: current.index };
      return grids[tabId];
    }
    var mapIndex = current.index;
    var ed = global.makeDbGridEditor({
      endpoint: t.endpoint,
      entity: t.endpoint,
      gridId: gridEl.id,
      title: t.id === 'info' ? 'Map #' + mapIndex : t.label,
      compact: true,
      wireDom: false,
      register: false,
      dirtyKey: dirtyKey(t.endpoint),
      layoutKey: 'mapeditor_' + t.endpoint,
      query: t.id === 'info' ? 'Index=' + mapIndex : 'MapIndex=' + mapIndex,
      newRowDefaults: t.id === 'info' ? undefined : function () { return { MapIndex: mapIndex }; },
      onRowClicked: function (e) { if (e && e.data) spotlightRow(t.id, e.data); },
      persistLegacy: function (rows) {
        if (!(global.GameData && GameData.persist)) {
          toast('Game Data is not loaded: cannot save.', 'error');
          return Promise.resolve({ ok: false });
        }
        return GameData.persist(t.endpoint, rows, ed);
      }
    });
    grids[tabId] = { ed: ed, el: pane, mapIndex: mapIndex };
    return grids[tabId];
  }

  async function showTable(tabId, force) {
    activeTab = tabId;
    renderTableTabs();
    ui.chips.classList.toggle('hidden', tabId !== 'info');
    if (!current) return;
    var g = ensureGrid(tabId);
    Object.keys(grids).forEach(function (k) { grids[k].el.classList.toggle('hidden', k !== tabId); });
    if (g && g.ed) {
      try { await g.ed.show(); } catch (e) { /* the grid reports its own errors */ }
    }
    if (tabId === 'info') renderChips();
  }

  function renderChips() {
    var box = ui.chips;
    if (!box) return;
    box.innerHTML = '';
    if (!current || !current.overlay || !current.overlay.map) return;
    var m = current.overlay.map;
    var title = el('div', 'me-chip-title');
    title.appendChild(el('strong', null, (m.Title || '(no title)')));
    title.appendChild(el('span', 'mir-muted', ' #' + current.index + ' · ' + (m.FileName || '') + '.map'));
    box.appendChild(title);
    CHIPS.forEach(function (c) {
      var v = m[c[0]];
      if (c[2] === 'nz' && (v == null || v === 0 || v === '' || v === '0')) return;
      var chip = el('span', 'me-chip');
      chip.appendChild(el('span', 'mir-muted', c[1] + ' '));
      chip.appendChild(el('span', null, v == null || v === '' ? '—' : String(v)));
      box.appendChild(chip);
    });
    var on = Object.keys(m).filter(function (k) { return m[k] === true; });
    if (on.length) {
      var f = el('span', 'me-chip me-chip-flags');
      f.appendChild(el('span', 'mir-muted', 'On: '));
      f.appendChild(el('span', null, on.join(', ')));
      box.appendChild(f);
    }
    var all = button('Edit all fields…', 'primary', 'Every field of the map in the row form (Apply, then "Apply changes" above the grid saves)');
    all.addEventListener('click', openMapForm);
    box.appendChild(all);
  }

  async function openMapForm() {
    var g = ensureGrid('info');
    if (activeTab !== 'info') await showTable('info');
    if (!g || !g.ed) return;
    try { await g.ed.show(); } catch (e) { /* reported by the grid */ }
    var api = g.ed.getApi();
    var node = api && api.getRowNode(String(current.index));
    if (!node || !global.FormDrawer) { toast('The map row is not loaded yet.', 'info'); return; }
    FormDrawer.open({ endpoint: 'maps', entity: 'maps', rowNode: node, dirtyKey: dirtyKey('maps') });
  }

  function keyOfRow(tabId, data) {
    var kind = tabId === 'safezone' ? 'safezone' : tabId === 'respawn' ? 'respawn' : tabId === 'movement' ? 'movement' :
      tabId === 'mine' ? 'mine' : tabId === 'npc' ? 'npc' : null;
    return kind && data && data.Index != null ? kind + ':' + data.Index : null;
  }

  function spotlightRow(tabId, data) {
    var key = keyOfRow(tabId, data);
    var o = key ? view.find(key) : null;
    if (!o) { view.spotlight(null); return; }
    setSelected(o.key, { from: 'grid' });
    view.showCell(o.x, o.y, false, true);
  }

  async function focusRowOf(o) {
    var tab = TAB_OF_KIND[o.kind];
    if (!tab) return;
    await showTable(tab);
    var g = grids[tab];
    if (g && g.ed && g.ed.focusRow) g.ed.focusRow(o.index);
    if (ui.tableHost.scrollIntoView) ui.tableHost.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  }

  function onTableSaved(endpoint) {
    if (!current) return;
    if (TABS.some(function (t) { return t.endpoint === endpoint; })) {
      reloadOverlay(true);
      loadMaps(false);
    }
    renderTableTabs();
  }

  // ---- right-click on a cell: live GM actions (instance 1 of the map in the running world) ----------------------------------

  async function openCellMenu(c, e) {
    var m = current && maps.filter(function (q) { return q.index === current.index; })[0];
    if (!m || !e || !global.OpsUI || !OpsUI.contextMenu) { copyText(c.x + ', ' + c.y); return; }
    var mapIndex = m.index;
    var title = (m.title || m.fileName || '#' + mapIndex) + ' · ' + c.x + ', ' + c.y;

    var res = await OpsUI.fetchResult('/api/admin/players-online');
    if (!current || current.index !== mapIndex) return;
    var data = res.ok ? (OpsUI.unwrap(res.data) || {}) : {};
    var rows = Array.isArray(data.rows) ? data.rows : [];
    var down = !res.ok ? 'Could not read the online players' : (data.world && data.world !== 'running' ? 'The world is ' + data.world : null);
    var blocked = view && view.walkable(c.x, c.y) === false ? 'This cell is not walkable' : null;
    var why = down || blocked;
    var gms = rows.filter(function (r) { return r.isGM; });

    var items = [];
    if (!gms.length) {
      items.push({ label: 'Teleport GM here', disabled: why || 'No GM is online' });
    } else {
      gms.slice(0, 8).forEach(function (g) {
        items.push({ label: 'Teleport ' + g.name + ' (GM) here', disabled: why, run: function () { return teleportOne(g, m, c, 'Map editor: GM teleport'); } });
      });
    }
    items.push({ label: 'Teleport a player here…', disabled: why || (rows.length ? null : 'Nobody is online'), run: function () { return pickAndTeleport(rows, m, c); } });
    items.push('-');
    items.push({ label: 'Teleport all online players here…', danger: true, disabled: why || (rows.length ? null : 'Nobody is online'), run: function () { return massTeleport('all', m, c); } });
    items.push({ label: 'Gather players on this map here…', danger: true, disabled: why, run: function () { return massTeleport('map', m, c); } });
    items.push({ label: 'Spawn monster here…', disabled: why, run: function () { return spawnHere(m, c); } });
    items.push('-');
    if (global.LiveMonitor) items.push({ label: 'Open in Live Monitor', run: function () { global.LiveMonitor.openMap(mapIndex, 1); } });
    items.push({ label: 'Copy coordinates', run: function () { copyText(c.x + ', ' + c.y); } });
    OpsUI.contextMenu(title, items, e.clientX, e.clientY);
  }

  function mapRefBody(m, c) { return { map: '#' + m.index, x: c.x, y: c.y }; }

  function afterLiveAction(r) {
    if (r && r.__ok && layers.players) setTimeout(function () { pollPlayers(); }, 400);
    return r;
  }

  async function teleportOne(p, m, c, reason) {
    var body = Object.assign({ action: 'teleport', character: p.name, reason: reason }, mapRefBody(m, c));
    if (p.characterIndex) body.characterIndex = p.characterIndex;
    return afterLiveAction(await OpsUI.post('/api/admin/gm/action', body));
  }

  async function pickAndTeleport(rows, m, c) {
    var sorted = rows.slice().sort(function (a, b) { return String(a.name).localeCompare(String(b.name)); });
    var v = await OpsUI.actionDialog({
      title: 'Teleport a player — ' + (m.title || m.fileName) + ' ' + c.x + ', ' + c.y,
      message: 'Moves the player to this cell (instance 1).',
      confirmText: 'Teleport',
      fields: [
        { key: 'who', label: 'Player', type: 'select', value: String(sorted[0].characterIndex),
          options: sorted.map(function (r) { return { value: String(r.characterIndex), label: r.name + ' · Lv ' + r.level + (r.isGM ? ' (GM)' : '') + (r.mapTitle ? ' · ' + r.mapTitle : '') }; }) },
        { key: 'reason', label: 'Reason (optional, audit trail)', type: 'text', required: false, placeholder: 'optional' }
      ]
    });
    if (!v) return null;
    var p = sorted.filter(function (r) { return String(r.characterIndex) === String(v.who); })[0];
    return p ? teleportOne(p, m, c, v.reason || '') : null;
  }

  async function massTeleport(who, m, c) {
    var where = (m.title || m.fileName) + ' (' + c.x + ', ' + c.y + ')';
    var v = await OpsUI.actionDialog({
      title: who === 'map' ? 'Gather players on this map' : 'Teleport all online players',
      message: (who === 'map' ? 'Moves every player already on this map' : 'Moves EVERY online player, GMs included,') + ' to ' + where + '. Dead players are skipped.',
      confirmText: 'Teleport',
      danger: true,
      fields: [
        { key: 'spread', label: 'Spread (cells around the point)', type: 'number', value: 3, min: 0, max: 20 },
        { key: 'reason', label: 'Reason (optional, audit trail)', type: 'text', required: false, placeholder: 'optional' }
      ]
    });
    if (!v) return null;
    var body = Object.assign({ action: 'teleportall', who: who, reason: v.reason || '' }, mapRefBody(m, c));
    if (v.spread) body.spread = v.spread;
    return afterLiveAction(await OpsUI.post('/api/admin/gm/action', body));
  }

  async function spawnHere(m, c) {
    var v = await OpsUI.actionDialog({
      title: 'Spawn monster — ' + (m.title || m.fileName) + ' ' + c.x + ', ' + c.y,
      message: 'Spawns monsters around this cell (instance 1) in the running world.',
      confirmText: 'Spawn',
      fields: [
        { key: 'monster', label: 'Monster (index or name)', type: 'text', required: true, placeholder: 'e.g. Hen' },
        { key: 'count', label: 'Count', type: 'number', value: 1, min: 1, max: 50 },
        { key: 'spread', label: 'Spread (cells around the point)', type: 'number', value: 0, min: 0, max: 20 },
        { key: 'reason', label: 'Reason (optional, audit trail)', type: 'text', required: false, placeholder: 'optional' }
      ]
    });
    if (!v) return null;
    var body = Object.assign({ action: 'spawnmob', monster: String(v.monster || '').trim(), reason: v.reason || '' }, mapRefBody(m, c));
    if (v.count) body.count = v.count;
    if (v.spread) body.spread = v.spread;
    return OpsUI.post('/api/admin/gm/action', body);
  }

  // ---- players (phase 3 layer) -------------------------------------------------------------------------------------------

  function syncPlayersPoll() {
    var want = !!(layers.players && current);
    if (want && !playersStop && global.AdminSections && AdminSections.poll) {
      playersStop = AdminSections.poll(SECTION, pollPlayers, 5000);
      pollPlayers();
    } else if (!want && playersStop) {
      playersStop();
      playersStop = null;
      if (view) view.setPlayers([]);
    }
  }

  async function pollPlayers() {
    if (!current || !layers.players) return;
    var idx = current.index;
    var res = await global.OpsUI.fetchResult('/api/admin/mapeditor/maps/' + encodeURIComponent(idx) + '/players');
    if (!current || current.index !== idx || !res.ok) return;
    var list = (res.data && res.data.players) || [];
    current.players = list;
    view.setPlayers(list);
    if (hooks.onPlayers) hooks.onPlayers(list);
  }

  // ---- map only / full screen / keys ------------------------------------------------------------------------------------

  function setMapOnly(on, quiet) {
    root.classList.toggle('me-map-only', on);
    ui.mapOnly.classList.toggle('primary', on);
    if (!quiet) lsSet('maponly', on ? '1' : '0');
    if (view) view.redraw();
  }
  function setFull(on) {
    ui.mapCard.classList.toggle('me-fullscreen', on);
    document.body.classList.toggle('me-fs-on', on);
    ui.full.classList.toggle('primary', on);
    if (view) view.redraw();
  }

  var HELP = [
    ['Map', [['Scroll', 'zoom at the mouse'], ['Drag', 'pan'], ['Click', 'select (click again: the next object on that cell)'],
      ['Double-click', 'follow a portal / show the object\'s row'], ['Right-click', 'copy "x, y"'], ['+ / - / 0', 'zoom in / out / fit'],
      ['G', 'go to a cell'], ['\\', 'map only'], ['Esc', 'leave full screen / clear the selection'], ['?', 'this list']]],
    ['Map list', [['Up / Down / Home / End', 'move'], ['Enter', 'open the map'], ['Down in the filter', 'into the list']]],
    ['Edit mode', [['Drag', 'move the object under the mouse'], ['Drag a handle', 'Size / Spread; a portal\'s destination ring'],
      ['+ buttons', 'click on the map to place (Shift+click: keep placing)'], ['Arrow keys', 'nudge (Shift: 10 cells)'],
      ['[ / ]', 'shrink / grow'], ['Del', 'delete the selection'], ['Ctrl+Z / Ctrl+Y', 'undo / redo'], ['Esc', 'drop the add tool']]]
  ];

  function showHelp() {
    if (document.querySelector('.me-help-dialog')) return;
    var backdrop = el('div', 'portal-confirm-backdrop');
    var dlg = el('div', 'portal-confirm-dialog me-help-dialog');
    dlg.setAttribute('role', 'dialog');
    dlg.setAttribute('aria-modal', 'true');
    dlg.appendChild(el('h3', 'portal-confirm-title', 'Maps editor: keys and mouse'));
    HELP.forEach(function (g) {
      dlg.appendChild(el('h4', 'me-help-h', g[0]));
      var dl = el('dl', 'me-help');
      g[1].forEach(function (k) { dl.appendChild(el('dt', null, k[0])); dl.appendChild(el('dd', null, k[1])); });
      dlg.appendChild(dl);
    });
    var close = button('Close', 'primary');
    var foot = el('div', 'portal-confirm-actions');
    foot.appendChild(close);
    dlg.appendChild(foot);
    backdrop.appendChild(dlg);
    document.body.appendChild(backdrop);
    var onEsc = function (e) { if (e.key === 'Escape' || e.key === '?') { e.preventDefault(); e.stopPropagation(); done(); } };
    var done = function () {
      document.removeEventListener('keydown', onEsc, true);
      if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
    };
    document.addEventListener('keydown', onEsc, true);
    close.addEventListener('click', done);
    backdrop.addEventListener('mousedown', function (e) { if (e.target === backdrop) done(); });
    close.focus();
  }

  function sectionVisible() {
    return !!(root && global.AdminSections && AdminSections.current && AdminSections.current() === SECTION);
  }

  function onKey(e) {
    if (!sectionVisible() || modalOpen()) return;
    if (hooks.onKey && hooks.onKey(e)) return;
    if (typingTarget(e)) return;
    if (e.ctrlKey || e.metaKey || e.altKey) return;
    if (e.key === '\\') { e.preventDefault(); setMapOnly(!root.classList.contains('me-map-only')); }
    else if (e.key === 'Escape') {
      if (ui.mapCard.classList.contains('me-fullscreen')) { e.preventDefault(); setFull(false); }
      else if (root.classList.contains('me-drop-open')) closeDrop();
      else if (selectedKey) setSelected(null);
    }
    else if (e.key === '+' || e.key === '=') { e.preventDefault(); view.zoomBy(1.4); }
    else if (e.key === '-' || e.key === '_') { e.preventDefault(); view.zoomBy(1 / 1.4); }
    else if (e.key === '0') { e.preventDefault(); view.fit(); }
    else if (e.key === 'g' || e.key === 'G') { e.preventDefault(); ui.goto.focus(); ui.goto.select(); }
    else if (e.key === '?') { e.preventDefault(); showHelp(); }
  }

  // ---- section ------------------------------------------------------------------------------------------------------------

  // The API the edit / extras module (admin-mapeditor-edit.js) and other sections use.
  var api = {
    SECTION: SECTION,
    hooks: hooks,
    ui: function () { return ui; },
    view: function () { return view; },
    current: function () { return current; },
    layers: function () { return layers; },
    reloadOverlay: reloadOverlay,
    renderObjects: renderObjects,
    renderDetails: renderDetails,
    renderInfoBar: renderInfoBar,
    renderTableTabs: renderTableTabs,
    setSelected: setSelected,
    selected: function () { return selectedKey ? view.find(selectedKey) : null; },
    openMap: openMap,
    maps: function () { return maps; },
    copyText: copyText,
    focusRowOf: focusRowOf,
    /** The page's grid editor of a table tab ('info', 'safezone', 'respawn', 'movement', 'mine', 'npc'), or null. */
    grid: function (tabId) { var g = grids[tabId]; return g ? g.ed : null; },
    showTable: showTable,
    dirtyKey: dirtyKey,
    EDITS_KEY: EDITS_KEY,
    isTyping: typingTarget,
    modalOpen: modalOpen,
    toast: toast,
    el: el,
    button: button
  };

  global.MapEditorPage = {
    selectByIndex: function (i, opts) { return openMap(i, opts); },
    selectByFile: function (file) {
      var m = maps.filter(function (x) { return String(x.fileName).toLowerCase() === String(file).toLowerCase(); })[0];
      return m ? openMap(m.index) : Promise.resolve();
    },
    showCell: function (i, x, y) { global.portalOpenDeepLink(SECTION, { map: i, x: x, y: y }); },
    hasPending: function () { return myDirtyKeys().length > 0 || pendingEditCount() > 0; },
    confirmLeaveMap: confirmLeaveMap,
    api: api
  };

  global.AdminSections.register(SECTION, {
    load: async function (el0) {
      readLayers();
      build(el0);
      if (global.MirLiveDb && MirLiveDb.refresh) { try { await MirLiveDb.refresh(); } catch (e) { /* status shown as off */ } }
      await loadMaps(false);
    },
    show: function (el0, params) {
      if (params && params.map != null && params.map !== '') return;   // the deep link opens it
      if (!current) {
        var last = num(lsGet('last', ''), -1);
        var first = maps.filter(function (m) { return m.index === last; })[0] || maps[0];
        if (first) return openMap(first.index, { replaceHash: true });
      } else if (view) {
        view.redraw();
      }
    },
    hide: function () {
      closeDrop();
      if (ui.mapCard && ui.mapCard.classList.contains('me-fullscreen')) setFull(false);
      var fd = document.getElementById('formDrawer');
      if (fd && !fd.classList.contains('hidden') && global.FormDrawer && typeof FormDrawer.close === 'function') FormDrawer.close();
    }
  });

  global.portalRegisterDeepLink(SECTION, function (params) {
    if (!params || params.map == null || params.map === '') return;
    openMap(num(params.map, -1), { sel: params.sel || null, x: params.x, y: params.y, replaceHash: true });
  });
})(window);
