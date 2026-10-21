/**
 * Maps editor — the map canvas (MapEditorView) and its pure geometry (MapEditorGeo).
 *
 * One <canvas> redrawn on demand (requestAnimationFrame), like the Live Monitor: a cell is 3k x 2k CSS px (48 x 32 in game),
 * view = { k, vx, vy } (scale and the top-left cell). Layers, bottom to top: base image (walkability PNG or the client minimap),
 * real map-art tiles once zoomed in (GET /api/admin/mapeditor/maps/{i}/tiles/{z}/{tx}/{ty}), blocked-cell shading, grid, mine
 * zones, safe zones, respawn areas, movements (portals) and inbound portals, NPCs, players, then hover / selection.
 *
 * Crystal's map objects are a point plus a square half-size (safe zone Size, respawn Spread, mine zone Size), so an object's
 * area is the square [x - r, x + r] x [y - r, y + r]; movements, inbound portals and NPCs are single cells.
 *
 * MapEditorView.create(canvasHost, hooks) -> view. hooks: onHover(cell | null, objects), onSelect(obj | null),
 * onActivate(obj) (double click), onContext(cell). The page owns the data: view.setMap(overlay, art), view.setObjects(list).
 */
(function (global) {
  'use strict';

  // ---- pure geometry (tested in Server.WebAdmin.Tests via the static contract only; kept free of DOM use) ----------------

  function num(v, d) { var n = Number(v); return isFinite(n) ? n : (d || 0); }

  var KIND = {
    safezone: { label: 'Safe zone', plural: 'Safe zones', endpoint: 'mapsafezones', order: 2 },
    respawn: { label: 'Respawn', plural: 'Respawns', endpoint: 'maprespawns', order: 1 },
    movement: { label: 'Movement', plural: 'Movements', endpoint: 'mapmovements', order: 3 },
    inbound: { label: 'Inbound movement', plural: 'Inbound movements', endpoint: 'mapmovements', order: 4 },
    mine: { label: 'Mine zone', plural: 'Mine zones', endpoint: 'mapminezones', order: 5 },
    npc: { label: 'NPC', plural: 'NPCs', endpoint: 'npcs', order: 0 }
  };

  /** The drawable objects of an overlay reply (every kind; the page filters by layer). */
  function objectsFromOverlay(ov) {
    var out = [];
    if (!ov) return out;
    (ov.safeZones || []).forEach(function (r) {
      out.push({ key: 'safezone:' + r.Index, kind: 'safezone', index: r.Index, row: r,
        x: num(r.Location_X), y: num(r.Location_Y), r: Math.max(0, num(r.Size)),
        name: (r.StartPoint ? 'Start point' : 'Safe zone') + ' (' + num(r.Size) + ')' });
    });
    (ov.respawns || []).forEach(function (r) {
      out.push({ key: 'respawn:' + r.Index, kind: 'respawn', index: r.Index, row: r,
        x: num(r.Location_X), y: num(r.Location_Y), r: Math.max(0, num(r.Spread)),
        boss: !!r.__boss,
        name: (r.__monster || ('Monster #' + r.MonsterIndex)) + ' ×' + num(r.Count) });
    });
    (ov.mineZones || []).forEach(function (r) {
      out.push({ key: 'mine:' + r.Index, kind: 'mine', index: r.Index, row: r,
        x: num(r.Location_X), y: num(r.Location_Y), r: Math.max(0, num(r.Size)),
        name: 'Mine zone ' + num(r.Mine) + ' (' + num(r.Size) + ')' });
    });
    (ov.movements || []).forEach(function (r) {
      var same = num(r.DestinationMapIndex, -1) === num(ov.index, -2);
      out.push({ key: 'movement:' + r.Index, kind: 'movement', index: r.Index, row: r,
        x: num(r.Source_X), y: num(r.Source_Y), r: 0,
        dest: { map: num(r.DestinationMapIndex, -1), x: num(r.Destination_X), y: num(r.Destination_Y), same: same, title: r.__destTitle || '' },
        name: '→ ' + (same ? 'here' : (r.__destTitle || ('map #' + r.DestinationMapIndex))) + ' (' + num(r.Destination_X) + ', ' + num(r.Destination_Y) + ')' });
    });
    (ov.inbound || []).forEach(function (r) {
      out.push({ key: 'inbound:' + r.rowIndex, kind: 'inbound', index: r.rowIndex, row: r,
        x: num(r.Destination_X != null ? r.Destination_X : r.destX), y: num(r.Destination_Y != null ? r.Destination_Y : r.destY), r: 0,
        from: { map: num(r.fromMapIndex, -1), x: num(r.sourceX), y: num(r.sourceY), title: r.fromTitle || '' },
        name: '← from ' + (r.fromTitle || ('map #' + r.fromMapIndex)) });
    });
    (ov.npcs || []).forEach(function (r) {
      out.push({ key: 'npc:' + r.Index, kind: 'npc', index: r.Index, row: r,
        x: num(r.Location_X), y: num(r.Location_Y), r: 0, name: r.Name || ('NPC #' + r.Index) });
    });
    // Real data can hold duplicate RespawnIndex values (the grid key of respawns): keep each object selectable on the map and
    // mark it, since the grid / batch cannot address such a row unambiguously.
    var seen = Object.create(null);
    out.forEach(function (o) {
      if (seen[o.key]) {
        seen[o.key].dupKey = true;
        o.dupKey = true;
        o.key = o.key + '~' + out.indexOf(o);
      } else {
        seen[o.key] = o;
      }
    });
    return out;
  }

  /** The cell rectangle an object covers (inclusive). */
  function rectOf(o) { return { x0: o.x - o.r, y0: o.y - o.r, x1: o.x + o.r, y1: o.y + o.r }; }

  function contains(o, x, y) { return x >= o.x - o.r && x <= o.x + o.r && y >= o.y - o.r && y <= o.y + o.r; }

  /** Objects covering cell (x, y), smallest area first (point objects before areas), then kind order. */
  function objectsAt(list, x, y) {
    return (list || []).filter(function (o) { return contains(o, x, y); }).sort(function (a, b) {
      var d = a.r - b.r;
      if (d) return d;
      return (KIND[a.kind] ? KIND[a.kind].order : 9) - (KIND[b.kind] ? KIND[b.kind].order : 9);
    });
  }

  /** Click-again cycling: the object after `prevKey` in `hits` (or the first). */
  function cyclePick(hits, prevKey) {
    if (!hits || !hits.length) return null;
    var i = -1;
    for (var n = 0; n < hits.length; n++) if (hits[n].key === prevKey) { i = n; break; }
    return hits[(i + 1) % hits.length];
  }

  /** Walkable cells inside an object's square (walk: Uint8Array w*h row-major, 1 = walkable), or -1 without walkability. */
  function walkableIn(o, walk, w, h) {
    if (!walk || !w || !h) return -1;
    var r = rectOf(o), n = 0;
    for (var y = Math.max(0, r.y0); y <= Math.min(h - 1, r.y1); y++)
      for (var x = Math.max(0, r.x0); x <= Math.min(w - 1, r.x1); x++)
        if (walk[y * w + x]) n++;
    return n;
  }

  /** Squares overlap (inclusive). */
  function overlaps(a, b) {
    var p = rectOf(a), q = rectOf(b);
    return p.x0 <= q.x1 && q.x0 <= p.x1 && p.y0 <= q.y1 && q.y0 <= p.y1;
  }

  global.MapEditorGeo = {
    KIND: KIND, objectsFromOverlay: objectsFromOverlay, rectOf: rectOf, contains: contains, objectsAt: objectsAt,
    cyclePick: cyclePick, walkableIn: walkableIn, overlaps: overlaps
  };

  // ---- colours ----------------------------------------------------------------------------------------------------------

  var COL = {
    bg: '#0b0d11', mapBg: '#161a22',
    safe: [74, 222, 128], start: [250, 204, 21], respawn: [248, 113, 113], boss: [251, 191, 36],
    mine: [34, 211, 238], move: [56, 189, 248], away: [167, 139, 250], inbound: [74, 222, 128], npc: [250, 204, 21],
    player: [255, 255, 255], dead: [239, 68, 68], sel: [255, 255, 255], hover: [255, 255, 255]
  };
  function rgba(c, a) { return 'rgba(' + c[0] + ',' + c[1] + ',' + c[2] + ',' + a + ')'; }

  var TILE_CONCURRENCY = 3, TILE_CACHE_MAX = 260;

  // ---- the view ---------------------------------------------------------------------------------------------------------

  function create(host, hooks) {
    hooks = hooks || {};
    var canvas = document.createElement('canvas');
    canvas.className = 'me-canvas';
    canvas.tabIndex = 0;
    canvas.setAttribute('aria-label', 'Map');
    host.appendChild(canvas);
    var tip = document.createElement('div');
    tip.className = 'me-tip hidden';
    host.appendChild(tip);

    var st = {
      ov: null, art: null, w: 0, h: 0, objects: [], players: [], pending: [],
      base: 'art', layers: {}, sel: null, hover: null, spot: null, pin: null,
      view: null, img: null, mini: null, walk: null, hatch: null,
      tiles: new Map(), tileQueue: [], tilesInFlight: 0, ver: '',
      drag: null, queued: false, destroyed: false
    };

    // ---- data ----
    function setMap(ov, art, ver) {
      st.ov = ov;
      st.art = art || null;
      st.ver = ver || String(Date.now());
      st.w = num(ov && ov.width);
      st.h = num(ov && ov.height);
      st.objects = [];
      st.sel = null; st.hover = null; st.spot = null; st.pin = null; st.players = []; st.pending = [];
      st.walk = null; st.hatch = null; st.img = null; st.mini = null;
      st.tiles = new Map(); st.tileQueue = [];
      st.view = null;
      if (!st.w || !st.h) {
        // No map file: size the canvas to the objects so they can still be seen and edited.
        var mx = 40, my = 40;
        objectsFromOverlay(ov).forEach(function (o) { mx = Math.max(mx, o.x + o.r + 10); my = Math.max(my, o.y + o.r + 10); });
        st.w = mx; st.h = my;
        st.noFile = true;
      } else {
        st.noFile = false;
        loadTerrain(ov.index);
      }
      if (ov && st.art && st.art.minimap && num(ov.map && ov.map.MiniMap) > 0) loadMini(ov.index);
      queueDraw();
    }

    function loadTerrain(index) {
      var img = new global.Image();
      var rec = { el: img, ok: false, index: index };
      st.img = rec;
      img.onload = function () {
        if (st.img !== rec) return;
        rec.ok = true;
        buildWalk(img);
        if (hooks.onWalkability) hooks.onWalkability(st.walk);
        queueDraw();
      };
      img.onerror = function () { if (st.img === rec) { rec.failed = true; queueDraw(); } };
      img.src = '/api/admin/mapeditor/maps/' + encodeURIComponent(index) + '/terrain?v=' + encodeURIComponent(st.ver);
    }

    function loadMini(index) {
      var img = new global.Image();
      var rec = { el: img, ok: false, index: index };
      st.mini = rec;
      img.onload = function () { if (st.mini === rec) { rec.ok = true; if (hooks.onBaseChange) hooks.onBaseChange(); queueDraw(); } };
      img.onerror = function () { if (st.mini === rec) { rec.failed = true; if (hooks.onBaseChange) hooks.onBaseChange(); queueDraw(); } };
      img.src = '/api/admin/mapeditor/maps/' + encodeURIComponent(index) + '/minimap?v=' + encodeURIComponent(st.ver);
    }

    // One pixel per cell: palette index 1 (38,66,95) = walkable.
    function buildWalk(img) {
      var w = st.w, h = st.h;
      try {
        var c = document.createElement('canvas');
        c.width = w; c.height = h;
        var cx = c.getContext('2d');
        cx.drawImage(img, 0, 0);
        var data = cx.getImageData(0, 0, w, h).data;
        var walk = new Uint8Array(w * h);
        var hatch = document.createElement('canvas');
        hatch.width = w; hatch.height = h;
        var hx = hatch.getContext('2d');
        var out = hx.createImageData(w, h);
        var o = out.data;
        for (var i = 0, p = 0; i < w * h; i++, p += 4) {
          var ok = data[p] > 24;
          walk[i] = ok ? 1 : 0;
          if (!ok) { o[p] = 0; o[p + 1] = 0; o[p + 2] = 0; o[p + 3] = 150; }
        }
        hx.putImageData(out, 0, 0);
        st.walk = walk;
        st.hatch = hatch;
      } catch (e) {
        st.walk = null;
        st.hatch = null;
      }
    }

    function setObjects(list) { st.objects = list || []; queueDraw(); }
    function setPending(list) { st.pending = list || []; queueDraw(); }
    function setPlayers(list) { st.players = list || []; queueDraw(); }
    function setLayers(l) { st.layers = Object.assign({}, l); queueDraw(); }
    function setBase(b) { st.base = b; queueDraw(); }
    function select(key) { st.sel = key || null; queueDraw(); }
    function spotlight(key) { st.spot = key || null; queueDraw(); }

    function baseAvailable() {
      return {
        art: !!(st.art && st.art.tiles && !st.noFile),
        walk: !st.noFile,
        minimap: !!(st.mini && !st.mini.failed)
      };
    }

    // ---- view math ----
    function size() {
      var w = Math.max(200, host.clientWidth || 600), h = Math.max(200, host.clientHeight || 480);
      return { cw: w, ch: h };
    }
    function fitK(s) { return Math.min(s.cw / (3 * st.w), s.ch / (2 * st.h)); }

    function ensureView() {
      var s = size();
      var v = st.view;
      if (!v) {
        var k = fitK(s);
        v = st.view = { k: k, fitK: k, vx: 0, vy: 0, cw: s.cw, ch: s.ch };
        v.vx = st.w / 2 - s.cw / (6 * k);
        v.vy = st.h / 2 - s.ch / (4 * k);
      } else if (v.cw !== s.cw || v.ch !== s.ch) {
        var cx = v.vx + v.cw / (6 * v.k), cy = v.vy + v.ch / (4 * v.k);
        v.cw = s.cw; v.ch = s.ch;
        v.fitK = fitK(s);
        v.vx = cx - s.cw / (6 * v.k);
        v.vy = cy - s.ch / (4 * v.k);
      }
      clamp(v);
      return v;
    }

    function clamp(v) {
      v.k = Math.max(v.fitK * 0.5, Math.min(Math.max(v.fitK * 64, 32), v.k));
      var visW = v.cw / (3 * v.k), visH = v.ch / (2 * v.k);
      v.vx = visW >= st.w ? (st.w - visW) / 2 : Math.max(-visW * 0.25, Math.min(st.w - visW * 0.75, v.vx));
      v.vy = visH >= st.h ? (st.h - visH) / 2 : Math.max(-visH * 0.25, Math.min(st.h - visH * 0.75, v.vy));
    }

    function sx(v, x) { return (x - v.vx) * 3 * v.k; }
    function sy(v, y) { return (y - v.vy) * 2 * v.k; }
    function toCell(v, px, py) { return { x: Math.floor(px / (3 * v.k) + v.vx), y: Math.floor(py / (2 * v.k) + v.vy) }; }

    function zoomAt(factor, px, py) {
      var v = ensureView();
      if (px == null) { px = v.cw / 2; py = v.ch / 2; }
      var cx = px / (3 * v.k) + v.vx, cy = py / (2 * v.k) + v.vy;
      v.k *= factor;
      clamp(v);
      v.vx = cx - px / (3 * v.k);
      v.vy = cy - py / (2 * v.k);
      clamp(v);
      queueDraw();
    }
    function fit() { if (st.view) { st.view.k = st.view.fitK; st.view.vx = -1e9; st.view.vy = -1e9; } st.view = null; queueDraw(); }

    /** Centre on a cell and zoom in to about 24 px per cell width (pin optional). */
    function showCell(x, y, pin, keepZoom) {
      var v = ensureView();
      if (!keepZoom) v.k = Math.max(v.k, 8);
      v.vx = x + 0.5 - v.cw / (6 * v.k);
      v.vy = y + 0.5 - v.ch / (4 * v.k);
      clamp(v);
      if (pin) st.pin = { x: x, y: y };
      queueDraw();
    }

    // ---- drawing ----
    function queueDraw() {
      if (st.queued || st.destroyed) return;
      st.queued = true;
      (global.requestAnimationFrame || function (f) { return setTimeout(f, 16); })(function () { st.queued = false; draw(); });
    }

    function sizeCanvas(w, h, dpr) {
      var pw = Math.round(w * dpr), ph = Math.round(h * dpr);
      if (canvas.width !== pw) canvas.width = pw;
      if (canvas.height !== ph) canvas.height = ph;
      canvas.style.width = w + 'px';
      canvas.style.height = h + 'px';
    }

    function draw() {
      if (st.destroyed || !host.isConnected) return;
      var ctx = canvas.getContext('2d');
      var dpr = global.devicePixelRatio || 1;
      if (!st.ov) {
        var s0 = size();
        sizeCanvas(s0.cw, s0.ch, dpr);
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.fillStyle = COL.bg;
        ctx.fillRect(0, 0, s0.cw, s0.ch);
        ctx.fillStyle = '#7d8696';
        ctx.font = '13px Inter, sans-serif';
        ctx.textAlign = 'center';
        ctx.fillText('Pick a map from the list.', s0.cw / 2, s0.ch / 2);
        return;
      }
      var v = ensureView();
      sizeCanvas(v.cw, v.ch, dpr);
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.fillStyle = COL.bg;
      ctx.fillRect(0, 0, v.cw, v.ch);

      var ox = sx(v, 0), oy = sy(v, 0), mw = st.w * 3 * v.k, mh = st.h * 2 * v.k;
      ctx.fillStyle = COL.mapBg;
      ctx.fillRect(ox, oy, mw, mh);

      var base = st.base;
      var av = baseAvailable();
      if (base === 'art' && !av.art) base = av.minimap ? 'minimap' : 'walk';
      if (base === 'minimap' && !av.minimap) base = 'walk';
      if (base === 'minimap' || (base === 'art' && st.mini && st.mini.ok)) {
        if (st.mini && st.mini.ok) { ctx.imageSmoothingEnabled = true; ctx.drawImage(st.mini.el, ox, oy, mw, mh); }
      } else if (st.img && st.img.ok) {
        ctx.imageSmoothingEnabled = false;
        ctx.drawImage(st.img.el, ox, oy, mw, mh);
      }
      if (base === 'art') drawTiles(ctx, v);
      else st.tileQueue = [];

      if (st.layers.walk && st.hatch) {
        ctx.imageSmoothingEnabled = false;
        ctx.drawImage(st.hatch, ox, oy, mw, mh);
        drawBlockedCrosses(ctx, v);
      }
      if (st.layers.grid) drawGrid(ctx, v);

      var labels = [];
      var vis = visibleObjects();
      drawAreas(ctx, v, vis);
      drawMovements(ctx, v, vis, labels);
      drawMarkers(ctx, v, vis, labels);
      drawPending(ctx, v, labels);
      if (st.layers.players) drawPlayers(ctx, v, labels);
      drawHighlight(ctx, v);
      if (st.pin) drawPin(ctx, v, st.pin);
      if (st.layers.labels !== false) drawLabels(ctx, v, labels);

      if (st.noFile) banner(ctx, v, 'The map file was not found on the server: showing the objects only.');
      if (v.k > v.fitK * 1.01 || v.k < v.fitK * 0.99) {
        ctx.fillStyle = 'rgba(11,13,17,0.75)';
        ctx.fillRect(6, v.ch - 22, 64, 16);
        ctx.fillStyle = '#c9d1dc';
        ctx.font = '11px Inter, sans-serif';
        ctx.textAlign = 'left';
        ctx.fillText('× ' + (v.k / v.fitK).toFixed(1), 12, v.ch - 10);
      }
      if (hooks.onDraw) hooks.onDraw(v);
    }

    function banner(ctx, v, text) {
      ctx.font = '12px Inter, sans-serif';
      var w = ctx.measureText(text).width + 16;
      ctx.fillStyle = 'rgba(120, 53, 15, 0.85)';
      ctx.fillRect(8, 8, w, 22);
      ctx.fillStyle = '#fde68a';
      ctx.textAlign = 'left';
      ctx.fillText(text, 16, 23);
    }

    function visibleObjects() {
      var l = st.layers;
      return st.objects.filter(function (o) {
        if (o.kind === 'safezone') return l.safezones !== false;
        if (o.kind === 'respawn') return l.respawns !== false;
        if (o.kind === 'mine') return l.mines !== false;
        if (o.kind === 'movement') return l.movements !== false;
        if (o.kind === 'inbound') return l.inbound !== false;
        if (o.kind === 'npc') return l.npcs !== false;
        return true;
      });
    }

    // ---- tiles ----
    function tileZoom(v) {
      if (!st.art || !st.art.tiles) return -1;
      var dpr = global.devicePixelRatio || 1;
      var cellPx = 3 * v.k * dpr;
      if (cellPx < 4) return -1;
      var z = Math.floor(Math.log2(48 / cellPx));
      return Math.max(0, Math.min(st.art.maxZoom || 3, z));
    }

    function drawTiles(ctx, v) {
      var z = tileZoom(v);
      st.tileQueue = [];
      if (z < 0) return;
      var cells = (st.art.tileCells || 16) << z;
      var visW = v.cw / (3 * v.k), visH = v.ch / (2 * v.k);
      var tx0 = Math.max(0, Math.floor(v.vx / cells)), ty0 = Math.max(0, Math.floor(v.vy / cells));
      var tx1 = Math.min(Math.ceil(st.w / cells) - 1, Math.floor((v.vx + visW) / cells));
      var ty1 = Math.min(Math.ceil(st.h / cells) - 1, Math.floor((v.vy + visH) / cells));
      var dpr = global.devicePixelRatio || 1;
      var now = Date.now();
      var want = [];
      ctx.save();
      ctx.beginPath();
      ctx.rect(sx(v, 0), sy(v, 0), st.w * 3 * v.k, st.h * 2 * v.k);
      ctx.clip();
      ctx.imageSmoothingEnabled = 3 * v.k * dpr < (48 >> z);
      for (var ty = ty0; ty <= ty1; ty++) {
        for (var tx = tx0; tx <= tx1; tx++) {
          var key = z + '/' + tx + '/' + ty;
          var t = st.tiles.get(key);
          if (t && t.ok) {
            ctx.drawImage(t.img, sx(v, tx * cells), sy(v, ty * cells), cells * 3 * v.k, cells * 2 * v.k);
          } else {
            // a coarser tile that is already loaded stands in while this one loads
            for (var zz = z + 1; zz <= (st.art.maxZoom || 3); zz++) {
              var cc = (st.art.tileCells || 16) << zz;
              var ptx = Math.floor(tx * cells / cc), pty = Math.floor(ty * cells / cc);
              var p = st.tiles.get(zz + '/' + ptx + '/' + pty);
              if (!p || !p.ok) continue;
              var f = cells / cc, iw = p.img.naturalWidth, ih = p.img.naturalHeight;
              var srcX = ((tx * cells - ptx * cc) / cc) * iw, srcY = ((ty * cells - pty * cc) / cc) * ih;
              ctx.drawImage(p.img, srcX, srcY, iw * f, ih * f, sx(v, tx * cells), sy(v, ty * cells), cells * 3 * v.k, cells * 2 * v.k);
              break;
            }
            if (!t || (t.failed && now - t.at > 30000)) {
              var dx = (tx + 0.5) * cells - (v.vx + visW / 2), dy = (ty + 0.5) * cells - (v.vy + visH / 2);
              want.push({ key: key, d: dx * dx + dy * dy });
            }
          }
        }
      }
      ctx.restore();
      want.sort(function (a, b) { return a.d - b.d; });
      st.tileQueue = want.map(function (w) { return w.key; });
      pumpTiles();
    }

    function pumpTiles() {
      while (st.tilesInFlight < TILE_CONCURRENCY && st.tileQueue.length) {
        var key = st.tileQueue.shift();
        var t = st.tiles.get(key);
        if (t && (t.ok || !t.failed)) continue;
        loadTile(key);
      }
    }

    function loadTile(key) {
      var parts = key.split('/');
      var img = new global.Image();
      var rec = { img: img, ok: false, failed: false, at: Date.now() };
      var mapAt = st.ov;
      st.tiles.delete(key);
      st.tiles.set(key, rec);
      while (st.tiles.size > TILE_CACHE_MAX) st.tiles.delete(st.tiles.keys().next().value);
      st.tilesInFlight++;
      var done = function (ok) {
        st.tilesInFlight = Math.max(0, st.tilesInFlight - 1);
        if (st.ov !== mapAt) { pumpTiles(); return; }
        rec.ok = ok; rec.failed = !ok; rec.at = Date.now();
        queueDraw();
        pumpTiles();
      };
      img.onload = function () { done(true); };
      img.onerror = function () { done(false); };
      img.src = '/api/admin/mapeditor/maps/' + encodeURIComponent(st.ov.index) + '/tiles/' + parts[0] + '/' + parts[1] + '/' + parts[2] +
        '?v=' + encodeURIComponent(st.ver);
    }

    // ---- shading / grid ----
    function visRange(v) {
      var visW = v.cw / (3 * v.k), visH = v.ch / (2 * v.k);
      return {
        x0: Math.max(0, Math.floor(v.vx)), y0: Math.max(0, Math.floor(v.vy)),
        x1: Math.min(st.w - 1, Math.ceil(v.vx + visW)), y1: Math.min(st.h - 1, Math.ceil(v.vy + visH))
      };
    }

    function drawBlockedCrosses(ctx, v) {
      if (!st.walk || 3 * v.k < 14) return;
      var r = visRange(v);
      if ((r.x1 - r.x0 + 1) * (r.y1 - r.y0 + 1) > 40000) return;
      ctx.strokeStyle = 'rgba(239, 68, 68, 0.45)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      for (var y = r.y0; y <= r.y1; y++) {
        for (var x = r.x0; x <= r.x1; x++) {
          if (st.walk[y * st.w + x]) continue;
          var a = sx(v, x), b = sy(v, y);
          ctx.moveTo(a + 2, b + 2); ctx.lineTo(a + 3 * v.k - 2, b + 2 * v.k - 2);
        }
      }
      ctx.stroke();
    }

    function drawGrid(ctx, v) {
      var cw = 3 * v.k;
      if (cw < 9) return;
      var r = visRange(v);
      ctx.strokeStyle = 'rgba(255,255,255,' + Math.min(0.22, 0.05 + (cw - 9) / 160) + ')';
      ctx.lineWidth = 1;
      ctx.beginPath();
      for (var x = r.x0; x <= r.x1 + 1; x++) { var a = Math.round(sx(v, x)) + 0.5; ctx.moveTo(a, sy(v, r.y0)); ctx.lineTo(a, sy(v, r.y1 + 1)); }
      for (var y = r.y0; y <= r.y1 + 1; y++) { var b = Math.round(sy(v, y)) + 0.5; ctx.moveTo(sx(v, r.x0), b); ctx.lineTo(sx(v, r.x1 + 1), b); }
      ctx.stroke();
    }

    // ---- objects ----
    function rectPx(v, o) {
      var r = rectOf(o);
      return { x: sx(v, r.x0), y: sy(v, r.y0), w: (r.x1 - r.x0 + 1) * 3 * v.k, h: (r.y1 - r.y0 + 1) * 2 * v.k };
    }
    function centre(v, x, y) { return { x: sx(v, x + 0.5), y: sy(v, y + 0.5) }; }

    function drawAreas(ctx, v, vis) {
      vis.forEach(function (o) {
        if (o.kind !== 'mine' && o.kind !== 'safezone' && o.kind !== 'respawn') return;
        var p = rectPx(v, o);
        var c = o.kind === 'mine' ? COL.mine : o.kind === 'safezone' ? COL.safe : (o.boss ? COL.boss : COL.respawn);
        // Respawn areas overlap a lot on busy maps: a light fill so stacked areas stay readable.
        ctx.fillStyle = rgba(c, o.kind === 'safezone' ? 0.2 : o.kind === 'mine' ? 0.14 : 0.05);
        ctx.fillRect(p.x, p.y, p.w, p.h);
        ctx.strokeStyle = rgba(o.kind === 'safezone' && o.row.StartPoint ? COL.start : c, o.kind === 'respawn' ? 0.45 : 0.75);
        ctx.lineWidth = 1;
        if (o.kind === 'safezone' && o.row.StartPoint) ctx.setLineDash([5, 3]);
        ctx.strokeRect(Math.round(p.x) + 0.5, Math.round(p.y) + 0.5, Math.round(p.w) - 1, Math.round(p.h) - 1);
        ctx.setLineDash([]);
      });
    }

    function markerR(v) { return Math.max(4, Math.min(9, 3 + v.k * 0.9)); }

    function arrow(ctx, a, b, color, dashed) {
      var dx = b.x - a.x, dy = b.y - a.y, len = Math.sqrt(dx * dx + dy * dy);
      if (len < 2) return;
      ctx.strokeStyle = color;
      ctx.fillStyle = color;
      ctx.lineWidth = 1.5;
      if (dashed) ctx.setLineDash([5, 4]);
      ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y); ctx.stroke();
      ctx.setLineDash([]);
      var ux = dx / len, uy = dy / len, s = 7;
      ctx.beginPath();
      ctx.moveTo(b.x, b.y);
      ctx.lineTo(b.x - ux * s - uy * s * 0.6, b.y - uy * s + ux * s * 0.6);
      ctx.lineTo(b.x - ux * s + uy * s * 0.6, b.y - uy * s - ux * s * 0.6);
      ctx.closePath();
      ctx.fill();
    }

    function drawMovements(ctx, v, vis, labels) {
      vis.forEach(function (o) {
        if (o.kind !== 'movement') return;
        var a = centre(v, o.x, o.y);
        if (o.dest.same) {
          arrow(ctx, a, centre(v, o.dest.x, o.dest.y), rgba(COL.move, 0.8), false);
        } else {
          var b = { x: a.x + 26, y: a.y - 18 };
          arrow(ctx, a, b, rgba(COL.away, 0.85), true);
          labels.push({ text: '→ ' + (o.dest.title || ('map #' + o.dest.map)), x: b.x + 3, y: b.y - 2, prio: 2, color: '#c4b5fd', key: o.key });
        }
      });
    }

    function drawMarkers(ctx, v, vis, labels) {
      var r = markerR(v);
      vis.forEach(function (o) {
        var c = centre(v, o.x, o.y);
        if (c.x < -40 || c.y < -40 || c.x > v.cw + 40 || c.y > v.ch + 40) return;
        ctx.lineWidth = 1.5;
        if (o.kind === 'respawn') {
          ctx.fillStyle = rgba(o.boss ? COL.boss : COL.respawn, 0.9);
          ctx.beginPath();
          if (o.boss) { ctx.moveTo(c.x, c.y - r); ctx.lineTo(c.x + r, c.y); ctx.lineTo(c.x, c.y + r); ctx.lineTo(c.x - r, c.y); ctx.closePath(); }
          else ctx.arc(c.x, c.y, r * 0.8, 0, Math.PI * 2);
          ctx.fill();
          ctx.strokeStyle = 'rgba(0,0,0,0.6)'; ctx.stroke();
          labels.push({ text: o.name, x: c.x + r + 2, y: c.y + 4, prio: o.boss ? 3 : 1, color: '#fecaca', key: o.key });
        } else if (o.kind === 'safezone' || o.kind === 'mine') {
          ctx.fillStyle = rgba(o.kind === 'mine' ? COL.mine : (o.row.StartPoint ? COL.start : COL.safe), 0.9);
          ctx.fillRect(c.x - r * 0.6, c.y - r * 0.6, r * 1.2, r * 1.2);
          ctx.strokeStyle = 'rgba(0,0,0,0.6)'; ctx.strokeRect(c.x - r * 0.6, c.y - r * 0.6, r * 1.2, r * 1.2);
          labels.push({ text: o.name, x: c.x + r + 2, y: c.y + 4, prio: 1, color: o.kind === 'mine' ? '#a5f3fc' : '#bbf7d0', key: o.key });
        } else if (o.kind === 'movement') {
          ctx.fillStyle = rgba(o.dest.same ? COL.move : COL.away, 0.95);
          ctx.beginPath(); ctx.moveTo(c.x, c.y - r); ctx.lineTo(c.x + r, c.y); ctx.lineTo(c.x, c.y + r); ctx.lineTo(c.x - r, c.y); ctx.closePath();
          ctx.fill(); ctx.strokeStyle = 'rgba(0,0,0,0.6)'; ctx.stroke();
        } else if (o.kind === 'inbound') {
          ctx.fillStyle = rgba(COL.inbound, 0.9);
          ctx.beginPath(); ctx.moveTo(c.x, c.y - r); ctx.lineTo(c.x + r, c.y + r * 0.8); ctx.lineTo(c.x - r, c.y + r * 0.8); ctx.closePath();
          ctx.fill(); ctx.strokeStyle = 'rgba(0,0,0,0.6)'; ctx.stroke();
          labels.push({ text: o.name, x: c.x + r + 2, y: c.y + 4, prio: 0, color: '#bbf7d0', key: o.key });
        } else if (o.kind === 'npc') {
          ctx.fillStyle = rgba(COL.npc, 0.95);
          roundRect(ctx, c.x - r * 0.7, c.y - r * 0.7, r * 1.4, r * 1.4, 2);
          ctx.fill(); ctx.strokeStyle = 'rgba(0,0,0,0.7)'; ctx.stroke();
          labels.push({ text: o.name, x: c.x + r + 2, y: c.y + 4, prio: 2, color: '#fde68a', key: o.key });
        }
      });
    }

    function roundRect(ctx, x, y, w, h, rr) {
      ctx.beginPath();
      ctx.moveTo(x + rr, y); ctx.lineTo(x + w - rr, y); ctx.quadraticCurveTo(x + w, y, x + w, y + rr);
      ctx.lineTo(x + w, y + h - rr); ctx.quadraticCurveTo(x + w, y + h, x + w - rr, y + h);
      ctx.lineTo(x + rr, y + h); ctx.quadraticCurveTo(x, y + h, x, y + h - rr);
      ctx.lineTo(x, y + rr); ctx.quadraticCurveTo(x, y, x + rr, y);
      ctx.closePath();
    }

    // Pending (unapplied) edits drawn by the editor: { obj, was } -> the old square dashed and an arrow to the new place;
    // { obj, isNew } -> a dashed amber frame; { was, deleted } -> a red cross where the object was.
    function drawPending(ctx, v, labels) {
      st.pending.forEach(function (p) {
        if (!p) return;
        if (p.deleted && p.was) {
          var d = rectPx(v, p.was), c0 = centre(v, p.was.x, p.was.y), s = 7;
          ctx.strokeStyle = 'rgba(239, 68, 68, 0.95)';
          ctx.lineWidth = 2;
          ctx.setLineDash([4, 3]);
          ctx.strokeRect(d.x + 0.5, d.y + 0.5, d.w - 1, d.h - 1);
          ctx.setLineDash([]);
          ctx.beginPath();
          ctx.moveTo(c0.x - s, c0.y - s); ctx.lineTo(c0.x + s, c0.y + s);
          ctx.moveTo(c0.x + s, c0.y - s); ctx.lineTo(c0.x - s, c0.y + s);
          ctx.stroke();
          labels.push({ text: 'delete', x: c0.x + s + 2, y: c0.y + 4, prio: 3, color: '#fca5a5' });
          return;
        }
        if (p.isNew && p.obj) {
          var n = rectPx(v, p.obj), pad = p.obj.r === 0 ? 4 : 1;
          ctx.strokeStyle = 'rgba(251, 191, 36, 0.95)';
          ctx.lineWidth = 1.5;
          ctx.setLineDash([4, 3]);
          ctx.strokeRect(n.x - pad, n.y - pad, n.w + pad * 2, n.h + pad * 2);
          ctx.setLineDash([]);
          return;
        }
        if (!p.was) return;
        var a = centre(v, p.was.x, p.was.y), b = centre(v, p.obj.x, p.obj.y);
        var q = rectPx(v, p.was);
        ctx.strokeStyle = 'rgba(251, 191, 36, 0.8)';
        ctx.setLineDash([4, 3]);
        ctx.lineWidth = 1;
        ctx.strokeRect(q.x + 0.5, q.y + 0.5, q.w - 1, q.h - 1);
        ctx.setLineDash([]);
        if (p.was.x !== p.obj.x || p.was.y !== p.obj.y) arrow(ctx, a, b, 'rgba(251, 191, 36, 0.8)', true);
      });
    }

    function drawPlayers(ctx, v, labels) {
      st.players.forEach(function (p) {
        var c = centre(v, num(p.x), num(p.y));
        ctx.fillStyle = rgba(p.dead ? COL.dead : COL.player, 0.95);
        ctx.beginPath(); ctx.arc(c.x, c.y, 3.5, 0, Math.PI * 2); ctx.fill();
        ctx.strokeStyle = 'rgba(0,0,0,0.7)'; ctx.lineWidth = 1; ctx.stroke();
        labels.push({ text: p.name + ' ' + p.level, x: c.x + 5, y: c.y - 4, prio: 0, color: '#e5e7eb' });
      });
    }

    function findObj(key) {
      if (!key) return null;
      for (var i = 0; i < st.objects.length; i++) if (st.objects[i].key === key) return st.objects[i];
      return null;
    }

    function outline(ctx, v, o, color, width) {
      var p = rectPx(v, o);
      var pad = o.r === 0 ? 3 : 0;
      ctx.strokeStyle = color;
      ctx.lineWidth = width;
      ctx.strokeRect(p.x - pad, p.y - pad, p.w + pad * 2, p.h + pad * 2);
    }

    function drawHighlight(ctx, v) {
      var h = findObj(st.hover);
      if (h && h.key !== st.sel) outline(ctx, v, h, 'rgba(255,255,255,0.7)', 1);
      var spot = findObj(st.spot);
      if (spot && spot.key !== st.sel) outline(ctx, v, spot, 'rgba(96,165,250,0.95)', 2);
      var s = findObj(st.sel);
      if (s) {
        outline(ctx, v, s, 'rgba(0,0,0,0.8)', 4);
        outline(ctx, v, s, 'rgba(250, 250, 250, 1)', 2);
        if (s.kind === 'movement' && s.dest.same) arrow(ctx, centre(v, s.x, s.y), centre(v, s.dest.x, s.dest.y), 'rgba(255,255,255,0.95)', false);
        if (hooks.drawSelection) hooks.drawSelection(ctx, v, s, { sx: sx, sy: sy, rectPx: rectPx });
      }
    }

    function drawPin(ctx, v, p) {
      var c = centre(v, p.x, p.y);
      ctx.strokeStyle = 'rgba(244, 114, 182, 0.95)';
      ctx.lineWidth = 2;
      ctx.beginPath(); ctx.arc(c.x, c.y, 6, 0, Math.PI * 2); ctx.stroke();
      ctx.beginPath(); ctx.arc(c.x, c.y, 11, 0, Math.PI * 2); ctx.stroke();
    }

    // Greedy label placement: higher priority first, the selected object's label always.
    function drawLabels(ctx, v, labels) {
      var cw = 3 * v.k;
      ctx.font = '11px Inter, sans-serif';
      ctx.textAlign = 'left';
      var placed = [];
      labels.sort(function (a, b) { return (b.key === st.sel) - (a.key === st.sel) || b.prio - a.prio; });
      labels.forEach(function (l) {
        if (cw < 4 && l.key !== st.sel && l.prio < 3) return;
        if (l.x > v.cw || l.y < 0 || l.y > v.ch + 12) return;
        var w = ctx.measureText(l.text).width;
        var box = { x0: l.x - 2, y0: l.y - 11, x1: l.x + w + 2, y1: l.y + 3 };
        if (l.key !== st.sel && placed.some(function (p) { return box.x0 < p.x1 && p.x0 < box.x1 && box.y0 < p.y1 && p.y0 < box.y1; })) return;
        placed.push(box);
        ctx.fillStyle = 'rgba(11,13,17,0.72)';
        ctx.fillRect(box.x0, box.y0, box.x1 - box.x0, box.y1 - box.y0);
        ctx.fillStyle = l.color || '#e5e7eb';
        ctx.fillText(l.text, l.x, l.y);
      });
    }

    // ---- input ----
    function local(e) {
      var r = canvas.getBoundingClientRect();
      return { x: e.clientX - r.left, y: e.clientY - r.top };
    }

    function cellAt(e) {
      if (!st.ov || !st.view) return null;
      var p = local(e);
      var c = toCell(st.view, p.x, p.y);
      if (c.x < 0 || c.y < 0 || c.x >= st.w || c.y >= st.h) return null;
      return c;
    }

    function hitsAt(c) { return c ? objectsAt(visibleObjects(), c.x, c.y) : []; }

    function walkable(x, y) {
      if (!st.walk) return null;
      if (x < 0 || y < 0 || x >= st.w || y >= st.h) return false;
      return !!st.walk[y * st.w + x];
    }

    function showTip(text, e) {
      if (!text) { tip.classList.add('hidden'); return; }
      tip.textContent = text;
      tip.classList.remove('hidden');
      var p = local(e);
      var hw = host.clientWidth || 600;
      tip.style.left = Math.min(hw - 240, p.x + 14) + 'px';
      tip.style.top = (p.y + 16) + 'px';
    }

    canvas.addEventListener('wheel', function (e) {
      if (!st.ov) return;
      e.preventDefault();
      var p = local(e);
      zoomAt(Math.pow(1.0015, -e.deltaY), p.x, p.y);
    }, { passive: false });

    canvas.addEventListener('mousedown', function (e) {
      if (!st.ov) return;
      canvas.focus({ preventScroll: true });
      var p = local(e);
      if (hooks.onPointerDown && hooks.onPointerDown(e, cellAt(e), p)) return;   // the editor took it (drag a handle / object)
      if (e.button === 0 || e.button === 1) {
        st.drag = { x: p.x, y: p.y, vx: st.view.vx, vy: st.view.vy, moved: false, button: e.button };
        if (e.button === 1) e.preventDefault();
      }
    });

    function onMove(e) {
      if (st.destroyed) return;
      if (st.drag) {
        var p = local(e);
        var dx = p.x - st.drag.x, dy = p.y - st.drag.y;
        if (!st.drag.moved && dx * dx + dy * dy > 16) st.drag.moved = true;
        if (st.drag.moved) {
          st.view.vx = st.drag.vx - dx / (3 * st.view.k);
          st.view.vy = st.drag.vy - dy / (2 * st.view.k);
          clamp(st.view);
          canvas.classList.add('me-panning');
          queueDraw();
        }
      }
    }

    function onUp(e) {
      if (st.destroyed) return;
      var d = st.drag;
      st.drag = null;
      canvas.classList.remove('me-panning');
      if (!d || d.moved || d.button !== 0) return;
      var c = cellAt(e);
      if (hooks.onCellClick && hooks.onCellClick(e, c)) return;   // the editor took it (place a new object)
      var hits = hitsAt(c);
      var pick = cyclePick(hits, st.sel);
      st.sel = pick ? pick.key : null;
      queueDraw();
      if (hooks.onSelect) hooks.onSelect(pick, c, hits.length);
    }

    global.addEventListener('mousemove', onMove);
    global.addEventListener('mouseup', onUp);

    canvas.addEventListener('mousemove', function (e) {
      if (st.drag && st.drag.moved) return;
      var c = cellAt(e);
      var hits = hitsAt(c);
      var nextSel = hits.length ? cyclePick(hits, hits.some(function (h) { return h.key === st.sel; }) ? st.sel : null) : null;
      var hk = nextSel ? nextSel.key : null;
      if (hk !== st.hover) { st.hover = hk; queueDraw(); }
      if (hooks.onHover) hooks.onHover(c, hits);
      if (c && hits.length) {
        var t = hits.map(function (h) { return KIND[h.kind].label + ': ' + h.name; }).slice(0, 4).join('\n');
        if (hits.length > 1) t += '\n(' + hits.length + ' here — click again to cycle)';
        showTip(t, e);
      } else {
        showTip(null);
      }
      if (hooks.cursor) canvas.style.cursor = hooks.cursor(c, e) || '';
    });
    canvas.addEventListener('mouseleave', function () {
      showTip(null);
      if (st.hover) { st.hover = null; queueDraw(); }
      if (hooks.onHover) hooks.onHover(null, []);
    });
    canvas.addEventListener('dblclick', function (e) {
      var c = cellAt(e);
      var s = findObj(st.sel);
      if (s && c && contains(s, c.x, c.y) && hooks.onActivate) hooks.onActivate(s);
    });
    canvas.addEventListener('contextmenu', function (e) {
      if (!st.ov) return;
      e.preventDefault();
      var c = cellAt(e);
      if (c && hooks.onContext) hooks.onContext(c, e);
    });

    var ro = typeof global.ResizeObserver === 'function' ? new global.ResizeObserver(function () { queueDraw(); }) : null;
    if (ro) ro.observe(host);

    function destroy() {
      st.destroyed = true;
      global.removeEventListener('mousemove', onMove);
      global.removeEventListener('mouseup', onUp);
      if (ro) ro.disconnect();
    }

    return {
      canvas: canvas,
      setMap: setMap, setObjects: setObjects, setPending: setPending, setPlayers: setPlayers, setLayers: setLayers, setBase: setBase,
      select: select, spotlight: spotlight, selected: function () { return findObj(st.sel); }, find: findObj,
      fit: fit, zoomBy: function (f) { zoomAt(f); }, showCell: showCell, redraw: queueDraw,
      clearPin: function () { st.pin = null; queueDraw(); },
      pin: function (x, y) { st.pin = { x: x, y: y }; queueDraw(); },
      baseAvailable: baseAvailable, walkable: walkable, walk: function () { return st.walk; },
      size: function () { return { w: st.w, h: st.h, noFile: !!st.noFile }; },
      view: function () { return st.view; }, cellAt: cellAt, toCell: function (px, py) { return st.view ? toCell(st.view, px, py) : null; },
      local: local, destroy: destroy
    };
  }

  global.MapEditorView = { create: create };
})(window);
