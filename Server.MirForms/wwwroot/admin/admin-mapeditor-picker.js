/**
 * Maps editor — pieces other sections use:
 *   - MapPicker.pick({ map, x, y, title, walkableOnly, lockMap }) -> Promise<{ map, x, y } | null>: a modal with a map list and the
 *     map canvas (MapEditorView); click a cell or type X / Y; "Use this cell" (Enter) confirms, Esc cancels.
 *   - A "Pick on map…" button next to every <Name>_X / <Name>_Y pair in the row form drawer (FormDrawer.EXTENSIONS). The map is the
 *     row's DestinationMapIndex for Destination_X/Y, else its MapIndex; NPC locations must be walkable.
 *   - Grid hover previews (installed as window.ImagePreview when no other module provides one): a ...MapIndex cell shows that map,
 *     an X / Y cell of a row with a map shows the map with the point marked.
 * Reads only GET /api/admin/mapeditor/maps[/{i}/overlay|terrain]; writes nothing (the caller applies the picked cell).
 */
(function (global) {
  'use strict';

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
  function num(v, d) { var n = Number(v); return isFinite(n) ? n : (d == null ? NaN : d); }
  function toast(msg, kind) { if (typeof global.portalToast === 'function') global.portalToast(msg, kind || 'info'); }

  // ---- shared map list (titles for pickers and previews) -------------------------------------------------------------------

  var mapsPromise = null, mapsAt = 0;
  function maps() {
    if (!mapsPromise || Date.now() - mapsAt > 60000) {
      mapsAt = Date.now();
      mapsPromise = global.OpsUI.fetchResult('/api/admin/mapeditor/maps').then(function (res) {
        return res.ok && res.data && Array.isArray(res.data.maps) ? res.data.maps : [];
      }, function () { return []; });
    }
    return mapsPromise;
  }
  function mapLabel(m) { return m ? (m.index + ' — ' + (m.title || m.fileName || '?')) : ''; }

  // ---- the picker ------------------------------------------------------------------------------------------------------------

  var open = null;   // the open picker (one at a time)

  function pick(opts) {
    opts = opts || {};
    if (open) open.cancel();
    return new Promise(function (resolve) {
      var backdrop = el('div', 'portal-confirm-backdrop me-picker-backdrop');
      var dlg = el('div', 'portal-confirm-dialog me-picker');
      dlg.setAttribute('role', 'dialog');
      dlg.setAttribute('aria-modal', 'true');
      dlg.appendChild(el('h3', 'portal-confirm-title', opts.title || 'Pick a cell on the map'));

      var body = el('div', 'me-picker-body');
      var side = el('div', 'me-picker-side');
      var filter = el('input', 'mir-input');
      filter.type = 'search';
      filter.placeholder = 'Filter maps…';
      filter.setAttribute('aria-label', 'Filter maps');
      var list = el('div', 'me-picker-maps');
      list.setAttribute('role', 'listbox');
      side.appendChild(filter);
      side.appendChild(list);
      if (!opts.lockMap) body.appendChild(side);
      var host = el('div', 'me-picker-canvas');
      body.appendChild(host);
      dlg.appendChild(body);

      var foot = el('div', 'me-picker-foot');
      var mapName = el('span', 'me-picker-map mir-muted', '');
      var xi = el('input', 'mir-input me-picker-xy');
      xi.type = 'number'; xi.min = '0'; xi.setAttribute('aria-label', 'X');
      var yi = el('input', 'mir-input me-picker-xy');
      yi.type = 'number'; yi.min = '0'; yi.setAttribute('aria-label', 'Y');
      var note = el('span', 'me-picker-note', '');
      var cancel = button('Cancel', '');
      var ok = button('Use this cell', 'primary');
      foot.appendChild(mapName);
      foot.appendChild(el('span', 'mir-muted', 'X'));
      foot.appendChild(xi);
      foot.appendChild(el('span', 'mir-muted', 'Y'));
      foot.appendChild(yi);
      foot.appendChild(note);
      foot.appendChild(el('span', 'me-spacer'));
      foot.appendChild(cancel);
      foot.appendChild(ok);
      dlg.appendChild(foot);
      backdrop.appendChild(dlg);
      document.body.appendChild(backdrop);

      var state = { map: num(opts.map, -1), x: num(opts.x, 0), y: num(opts.y, 0), overlay: null, all: [] };
      xi.value = state.x;
      yi.value = state.y;

      var view = global.MapEditorView.create(host, {
        onCellClick: function (e, c) { if (c) setCell(c.x, c.y, false); return true; },
        onSelect: function () { },
        onHover: function () { }
      });
      view.setLayers({ safezones: true, respawns: true, movements: true, inbound: false, mines: true, npcs: true, grid: true, labels: true, walk: !!opts.walkableOnly });
      view.setBase('art');

      function setCell(x, y, centre) {
        state.x = x; state.y = y;
        xi.value = x; yi.value = y;
        if (centre) view.showCell(x, y, true, true);   // typed / opened: bring the cell into view
        else view.pin(x, y);                          // clicked: the view stays where it is
        check();
      }

      function check() {
        var s = view.size();
        var bad = '';
        if (state.map < 0 || !state.overlay) bad = 'Pick a map.';
        else if (s.noFile) bad = '';
        else if (state.x < 0 || state.y < 0 || state.x >= s.w || state.y >= s.h) bad = 'Outside the map (' + s.w + '×' + s.h + ').';
        var w = view.walkable(state.x, state.y);
        if (!bad && w === false) bad = opts.walkableOnly ? 'That cell is blocked: pick a walkable cell.' : 'Blocked cell.';
        note.textContent = bad;
        note.classList.toggle('me-bad', !!bad);
        ok.disabled = state.map < 0 || !state.overlay || /Outside|Pick a map/.test(bad) || (opts.walkableOnly && w === false);
      }

      async function loadMap(index, keepCell) {
        state.map = index;
        state.overlay = null;
        var m = state.all.filter(function (x) { return x.index === index; })[0];
        mapName.textContent = m ? mapLabel(m) : ('map #' + index);
        renderList();
        var res = await global.OpsUI.fetchResult('/api/admin/mapeditor/maps/' + encodeURIComponent(index) + '/overlay');
        if (state.map !== index) return;
        if (!res.ok) { note.textContent = res.error; return; }
        state.overlay = res.data;
        view.setMap(res.data, res.data.art, String(Date.now()));
        view.setObjects(global.MapEditorGeo.objectsFromOverlay(res.data));
        if (keepCell) { xi.value = state.x; yi.value = state.y; view.showCell(state.x, state.y, true, false); }   // zoom in on the current cell
        else { state.x = Math.floor(num(res.data.width, 0) / 2); state.y = Math.floor(num(res.data.height, 0) / 2); xi.value = state.x; yi.value = state.y; view.fit(); }
        check();
      }

      function renderList() {
        var f = filter.value.trim().toLowerCase();
        list.innerHTML = '';
        state.all.filter(function (m) {
          return !f || (m.index + ' ' + (m.fileName || '') + ' ' + (m.title || '')).toLowerCase().indexOf(f) >= 0;
        }).slice(0, 400).forEach(function (m) {
          var b = el('button', 'me-pick-item' + (m.index === state.map ? ' me-pick-active' : ''));
          b.type = 'button';
          b.setAttribute('role', 'option');
          b.appendChild(el('span', 'me-pick-idx', '#' + m.index));
          b.appendChild(el('span', null, m.title || m.fileName));
          b.addEventListener('click', function () { loadMap(m.index, false); });
          list.appendChild(b);
        });
      }

      function done(v) {
        document.removeEventListener('keydown', onKey, true);
        view.destroy();
        if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
        open = null;
        resolve(v);
      }
      function confirm() {
        if (ok.disabled) return;
        done({ map: state.map, x: state.x, y: state.y });
      }
      function onKey(e) {
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); done(null); }
        else if (e.key === 'Enter' && e.target !== filter) { e.preventDefault(); e.stopPropagation(); confirm(); }
      }
      function typed() {
        var x = parseInt(xi.value, 10), y = parseInt(yi.value, 10);
        if (isNaN(x) || isNaN(y)) return;
        setCell(x, y, true);
      }

      document.addEventListener('keydown', onKey, true);
      filter.addEventListener('input', renderList);
      xi.addEventListener('change', typed);
      yi.addEventListener('change', typed);
      cancel.addEventListener('click', function () { done(null); });
      ok.addEventListener('click', confirm);
      backdrop.addEventListener('mousedown', function (e) { if (e.target === backdrop) done(null); });
      open = { cancel: function () { done(null); } };

      maps().then(function (all) {
        state.all = all;
        renderList();
        if (state.map >= 0) loadMap(state.map, true);
        else { note.textContent = 'Pick a map.'; ok.disabled = true; filter.focus(); }
      });
    });
  }

  global.MapPicker = { pick: pick };

  // ---- row form drawer: "Pick on map…" next to X / Y pairs -----------------------------------------------------------------

  function rowMap(row, base) {
    if (base === 'Destination' && row.DestinationMapIndex != null) return num(row.DestinationMapIndex, -1);
    if (row.MapIndex != null) return num(row.MapIndex, -1);
    return -1;
  }

  if (global.FormDrawer && Array.isArray(global.FormDrawer.EXTENSIONS)) {
    global.FormDrawer.EXTENSIONS.push(function (ctx) {
      if (ctx.readOnly) return;
      Object.keys(ctx.inputs).forEach(function (name) {
        var m = /^(.*)_X$/.exec(name);
        if (!m || !ctx.inputs[m[1] + '_Y']) return;
        var base = m[1];
        var X = ctx.inputs[name], Y = ctx.inputs[base + '_Y'];
        var b = button('Pick on map…', 'fd-browse me-fd-pick', 'Choose ' + base + ' by clicking the map');
        b.addEventListener('click', async function () {
          var map = rowMap(ctx.row, base);
          var res = await pick({
            map: map, x: X.input.value, y: Y.input.value,
            title: base + ' — ' + (ctx.row.Name || ctx.row.Title || ('#' + ctx.row.Index)),
            walkableOnly: ctx.endpoint === 'npcs' && base === 'Location',
            lockMap: map >= 0
          });
          if (!res) return;
          if (map >= 0 && res.map !== map) toast('The row stays on map #' + map + '; only X / Y were set.', 'info');
          X.set(res.x);
          Y.set(res.y);
        });
        Y.wrap.appendChild(b);
      });
    });
  }

  // ---- grid hover previews ------------------------------------------------------------------------------------------------

  var terrain = new Map();   // map index -> { img, ok }
  var tip = null, hoverSeq = 0;

  function terrainOf(index) {
    var t = terrain.get(index);
    if (t) return t;
    var img = new global.Image();
    t = { img: img, ok: false, failed: false, waiters: [] };
    img.onload = function () { t.ok = true; t.waiters.splice(0).forEach(function (f) { f(); }); };
    img.onerror = function () { t.failed = true; t.waiters.splice(0).forEach(function (f) { f(); }); };
    img.src = '/api/admin/mapeditor/maps/' + encodeURIComponent(index) + '/terrain';
    terrain.set(index, t);
    if (terrain.size > 40) terrain.delete(terrain.keys().next().value);
    return t;
  }

  function previewTarget(field, value, row) {
    if (!field || !row) return null;
    if (/MapIndex$/.test(field) && field !== 'MineIndex') {
      var i = num(value, -1);
      return i > 0 ? { map: i } : null;
    }
    var m = /^(.*)_(X|Y)$/.exec(field);
    if (!m) return null;
    var map = rowMap(row, m[1]);
    var x = num(row[m[1] + '_X'], NaN), y = num(row[m[1] + '_Y'], NaN);
    return map > 0 && isFinite(x) && isFinite(y) ? { map: map, x: x, y: y } : null;
  }

  function show(target, evt) {
    var seq = ++hoverSeq;
    maps().then(function (all) {
      if (seq !== hoverSeq) return;
      var m = all.filter(function (x) { return x.index === target.map; })[0];
      var t = terrainOf(target.map);
      var draw = function () {
        if (seq !== hoverSeq) return;
        if (!tip) { tip = el('div', 'me-hover-preview'); document.body.appendChild(tip); }
        tip.innerHTML = '';
        tip.appendChild(el('div', 'me-hover-title', m ? mapLabel(m) : 'map #' + target.map));
        if (t.ok) {
          var w = t.img.naturalWidth, h = t.img.naturalHeight;
          var k = Math.min(240 / (w * 3), 180 / (h * 2), 4);
          var c = el('canvas');
          c.width = Math.max(1, Math.round(w * 3 * k));
          c.height = Math.max(1, Math.round(h * 2 * k));
          var cx = c.getContext('2d');
          cx.imageSmoothingEnabled = false;
          cx.drawImage(t.img, 0, 0, c.width, c.height);
          if (target.x != null) {
            var px = (target.x + 0.5) * 3 * k, py = (target.y + 0.5) * 2 * k;
            cx.strokeStyle = '#f472b6'; cx.lineWidth = 2;
            cx.beginPath(); cx.arc(px, py, 5, 0, Math.PI * 2); cx.stroke();
            cx.beginPath(); cx.moveTo(px - 9, py); cx.lineTo(px + 9, py); cx.moveTo(px, py - 9); cx.lineTo(px, py + 9); cx.stroke();
          }
          tip.appendChild(c);
          if (target.x != null) tip.appendChild(el('div', 'me-hover-xy mir-muted', target.x + ', ' + target.y));
        } else if (t.failed) {
          tip.appendChild(el('div', 'mir-muted', 'No map file.'));
        } else {
          t.waiters.push(draw);
          tip.appendChild(el('div', 'mir-muted', 'Loading…'));
        }
        var vx = (evt && evt.clientX) || 0, vy = (evt && evt.clientY) || 0;
        tip.style.left = Math.min((global.innerWidth || 1200) - 270, vx + 16) + 'px';
        tip.style.top = Math.min((global.innerHeight || 800) - 240, vy + 16) + 'px';
        tip.classList.remove('hidden');
      };
      draw();
    });
  }

  function hide() {
    hoverSeq++;
    if (tip) tip.classList.add('hidden');
  }

  var previews = {
    onCell: function (entity, field, value, evt, row) {
      var t = previewTarget(field, value, row);
      if (!t) { hide(); return; }
      show(t, evt);
    },
    hide: hide
  };
  global.MapPreview = previews;
  // The grid factory calls window.ImagePreview on every cell hover; provide it unless another module (a Library Viewer) did.
  if (!global.ImagePreview) global.ImagePreview = previews;
})(window);
