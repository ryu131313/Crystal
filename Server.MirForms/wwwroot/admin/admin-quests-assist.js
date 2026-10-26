/*
 * Quests › editor assist: the shared pieces the editor and the wizard both use, and the only place besides admin-quests.js that
 * calls the Quests API for them.
 *   - hints:     GET /api/admin/quests/hints?kind=monster|item|npc|map&id=n  (spawns, drops, an NPC's place, a map's respawns)
 *   - icons:     GET /api/admin/quests/item-icon/{index}
 *   - thumbnails: GET /api/admin/mapeditor/maps/{index}/minimap (falls back to /terrain)
 *   - teleport:  GET /api/admin/players-online, POST /api/admin/gm/action
 *   - groups:    POST /api/admin/quests/groups/save (the "monsters in an area" group)
 * It holds no quest state: the editor and the wizard own that. Reads are cached for the page session.
 */
(function (global) {
  'use strict';

  var API = '/api/admin/quests';
  var cache = {};        // 'kind:id' -> Promise<hint|null>
  var titleCache = {};   // map index -> title

  // First guesses for authors, not balance rules: tune these numbers to the server.
  var TUNING = { killExpFactor: 1.5, stepExpBase: 100, stepExpPerLevelSq: 20, goldPerLevelPerStep: 50, itemPriceFactor: 0.5 };

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function clear(e) { while (e && e.firstChild) e.removeChild(e.firstChild); }
  function button(label, onClick, cls, title) {
    var b = el('button', 'mir-btn' + (cls ? ' ' + cls : ''), label);
    b.type = 'button';
    if (title) b.title = title;
    b.addEventListener('click', onClick);
    return b;
  }
  function num(v) { var n = Number(v); return isFinite(n) ? Math.trunc(n) : 0; }
  function toast(msg, kind) { if (typeof global.portalToast === 'function') global.portalToast(msg, kind || 'info'); }

  function hints(kind, id) {
    id = Number(id) || 0;
    if (!id) return Promise.resolve(null);
    var key = kind + ':' + id;
    if (!cache[key]) {
      cache[key] = global.OpsUI.get(API + '/hints?kind=' + encodeURIComponent(kind) + '&id=' + id).then(function (j) {
        if (!j) delete cache[key];
        return j;
      }, function () { delete cache[key]; return null; });
    }
    return cache[key];
  }
  function clearHints() { cache = {}; titleCache = {}; }
  function itemIconUrl(id) { return API + '/item-icon/' + Number(id); }

  function hintLine(kind, id) {
    var span = el('span', 'qe-hint-line');
    fillHint(span, kind, id);
    return span;
  }

  function fillHint(span, kind, id) {
    clear(span);
    span.className = 'qe-hint-line';
    span.removeAttribute('title');
    hints(kind, id).then(function (hint) {
      if (!hint || !span.parentNode) return;
      if (kind === 'monster') fillMonster(span, hint);
      else if (kind === 'item') fillItem(span, hint);
      else if (kind === 'npc') fillNpc(span, hint);
    });
  }

  function fillMonster(span, hint) {
    var text = 'Lv ' + num(hint.level) + ' · ' + num(hint.experience) + ' exp' + (hint.boss ? ' · boss' : '');
    var spawns = hint.spawns || [];
    if (spawns.length) {
      text += ' · spawns on ' + spawns.slice(0, 3).map(function (s) { return s.mapTitle + ' (×' + num(s.count) + ')'; }).join(', ') + (spawns.length > 3 ? ' …' : '');
    } else {
      text += ' · no spawns';
      span.classList.add('qe-warn');
    }
    span.textContent = text;
  }

  function fillItem(span, hint) {
    var drops = hint.droppedBy || [];
    if (drops.length) {
      var anyGrouped = drops.some(function (d) { return d.grouped; });
      span.textContent = 'Dropped by ' + drops.slice(0, 4).map(function (d) { return d.name + ' 1/' + num(d.chance) + (d.grouped ? '*' : '') + (d.quest ? ' (quest drop)' : ''); }).join(', ') + (drops.length > 4 ? ' …' : '');
      if (anyGrouped) span.title = '* inside a drop group';
      return;
    }
    if (hint.dropsKnown) {
      span.textContent = 'No monster drops it (shop, craft or script only?)';
      span.classList.add('qe-warn');
    }
  }

  function fillNpc(span, hint) {
    span.textContent = (hint.mapTitle || ('map #' + num(hint.map))) + ' (' + num(hint.x) + ', ' + num(hint.y) + ') ';
    span.appendChild(button('Map', function () {
      if (typeof global.portalOpenDeepLink === 'function') global.portalOpenDeepLink('mapeditor', { map: hint.map, x: hint.x, y: hint.y });
      else global.location.hash = '#section=mapeditor&map=' + hint.map + '&x=' + hint.x + '&y=' + hint.y;
    }, null, 'Open this place in the map editor'));
    span.appendChild(document.createTextNode(' '));
    span.appendChild(button('Teleport…', function () { teleportPrompt(hint.map, hint.x, hint.y); }, null, 'Move a character here'));
  }

  function players() {
    return global.OpsUI.get('/api/admin/players-online').then(function (j) {
      var rows = (j && j.rows) || [];
      rows.sort(function (a, b) { return (b.isGM ? 1 : 0) - (a.isGM ? 1 : 0) || String(a.name).localeCompare(String(b.name)); });
      return rows;
    });
  }

  function teleport(character, map, x, y, characterIndex, reason) {
    var body = { action: 'teleport', character: character, map: '#' + map, x: x, y: y, reason: reason || 'Quest editor' };
    if (characterIndex) body.characterIndex = characterIndex;
    return global.OpsUI.post('/api/admin/gm/action', body);
  }

  async function teleportPrompt(map, x, y) {
    var rows = await players();
    if (!rows.length) { toast('No one is online.', 'warn'); return null; }
    var v = await global.OpsUI.actionDialog({
      title: 'Teleport a character',
      message: 'Moves the character to (' + num(x) + ', ' + num(y) + ') on map #' + num(map) + '.',
      confirmText: 'Teleport',
      fields: [
        {
          key: 'who', label: 'Character', type: 'select', value: String(rows[0].characterIndex),
          options: rows.map(function (p) { return { value: String(p.characterIndex), label: p.name + ' · Lv ' + p.level + (p.isGM ? ' (GM)' : '') + (p.mapTitle ? ' · ' + p.mapTitle : '') }; })
        },
        { key: 'reason', label: 'Reason (optional, audit trail)', type: 'text', required: false, placeholder: 'optional' },
      ],
    });
    if (!v) return null;
    var p = rows.filter(function (r) { return String(r.characterIndex) === String(v.who); })[0];
    return p ? teleport(p.name, map, x, y, p.characterIndex, v.reason || 'Quests › teleport') : null;
  }

  function thumbnail(place, withRadius) {
    var c = el('canvas', 'qe-thumb');
    c.width = 160; c.height = 120;
    var ctx = c.getContext('2d');
    ctx.fillStyle = 'rgba(120,130,150,0.18)';
    ctx.fillRect(0, 0, 160, 120);
    if (!place || !place.map) return c;

    var map = place.map;
    var img = new global.Image();
    var size = null;
    var tried = false;

    function draw() {
      if (!size || !size.width || !size.height || !img.width || !img.height) return;
      ctx.clearRect(0, 0, 160, 120);
      ctx.fillStyle = 'rgba(120,130,150,0.18)';
      ctx.fillRect(0, 0, 160, 120);
      var k = Math.min(160 / img.width, 120 / img.height);
      var dw = img.width * k, dh = img.height * k, ox = (160 - dw) / 2, oy = (120 - dh) / 2;
      ctx.imageSmoothingEnabled = false;
      ctx.drawImage(img, ox, oy, dw, dh);
      var mx = ox + (num(place.x) / size.width) * dw;
      var my = oy + (num(place.y) / size.height) * dh;
      if (withRadius && num(place.radius) > 0) {
        var half = Math.max(3, (num(place.radius) / size.width) * dw);
        ctx.strokeStyle = 'rgba(244, 114, 182, 0.95)';
        ctx.lineWidth = 1;
        ctx.strokeRect(mx - half, my - half, half * 2, half * 2);
      }
      ctx.fillStyle = 'rgba(244, 114, 182, 0.95)';
      ctx.beginPath();
      ctx.arc(mx, my, 3, 0, Math.PI * 2);
      ctx.fill();
    }

    hints('map', map).then(function (h) {
      if (h) { size = { width: num(h.width), height: num(h.height) }; if (h.title) titleCache[map] = h.title; draw(); }
    });
    img.onload = draw;
    img.onerror = function () {
      if (tried) return;
      tried = true;
      img.src = '/api/admin/mapeditor/maps/' + map + '/terrain';
    };
    img.src = '/api/admin/mapeditor/maps/' + map + '/minimap';
    return c;
  }

  function placeLine(place, onChange, opts) {
    opts = opts || {};
    var p = place || { map: 0, x: 0, y: 0, radius: opts.defaultRadius || 0 };
    var box = el('div', 'qe-place');
    var line = el('span', 'qe-place-text');
    var thumbEl = null;
    var nums = null;
    var numbersOpen = false;

    function rad() { return Math.max(0, num(p.radius)); }
    function text() {
      var area = opts.radius ? ' · area ' + (2 * rad() + 1) + '×' + (2 * rad() + 1) : '';
      return (p.map ? (titleCache[p.map] || ('map #' + p.map)) : 'Pick a place') + ' (' + num(p.x) + ', ' + num(p.y) + ')' + area;
    }
    function changed() { if (onChange) onChange(p); }
    function refresh() { line.textContent = text(); }
    function sync() {
      if (!nums) return;
      nums.map.value = p.map || '';
      nums.x.value = num(p.x);
      nums.y.value = num(p.y);
      if (nums.radius) nums.radius.value = rad();
    }
    function refreshThumb() {
      var next = thumbnail(p, !!opts.radius);
      if (thumbEl && thumbEl.parentNode === box) box.replaceChild(next, thumbEl);
      else box.insertBefore(next, box.firstChild);
      thumbEl = next;
    }

    function numberBox(value, onInput, attrs) {
      var i = el('input', 'mir-input');
      i.type = 'number';
      i.value = value;
      Object.keys(attrs || {}).forEach(function (k) { i.setAttribute(k, attrs[k]); });
      i.addEventListener('input', function () { onInput(num(i.value)); });
      return i;
    }

    function build() {
      clear(box);
      thumbEl = thumbnail(p, !!opts.radius);
      box.appendChild(thumbEl);
      box.appendChild(line);
      line.textContent = text();
      if (p.map) hints('map', p.map).then(function (m) { if (m && m.title && p.map) { titleCache[p.map] = m.title; line.textContent = text(); } });

      box.appendChild(button('Pick on map…', function () {
        if (!global.MapPicker) { toast("The map picker isn't loaded.", 'warn'); return; }
        global.MapPicker.pick({
          map: p.map || -1, x: num(p.x), y: num(p.y),
          radius: opts.radius ? (rad() || opts.defaultRadius || 5) : null,
          walkableOnly: true, title: opts.title || 'Pick the place',
        }).then(function (res) {
          if (!res) return;
          p.map = res.map; p.x = res.x; p.y = res.y;
          if (opts.radius) p.radius = res.radius != null ? res.radius : (opts.defaultRadius || 5);
          build();
          changed();
        });
      }, null, opts.title || 'Pick the place'));

      var numbers = el('div', 'qe-place-numbers hidden');
      function row(label, input) {
        var f = el('label', 'qe-field');
        f.appendChild(el('span', 'qe-label', label));
        f.appendChild(input);
        return f;
      }
      var mapIn = numberBox(p.map || '', function () { }, { min: 0 });
      mapIn.addEventListener('change', function () {
        p.map = num(mapIn.value);
        refreshThumb();
        refresh();
        changed();
      });
      var xIn = numberBox(num(p.x), function (v) { p.x = v; refresh(); }, { min: 0 });
      xIn.addEventListener('change', function () { p.x = num(xIn.value); refresh(); changed(); });
      var yIn = numberBox(num(p.y), function (v) { p.y = v; refresh(); }, { min: 0 });
      yIn.addEventListener('change', function () { p.y = num(yIn.value); refresh(); changed(); });
      var rIn = opts.radius ? numberBox(rad(), function (v) { p.radius = Math.max(0, v); refresh(); }, { min: 0, max: 50 }) : null;
      if (rIn) rIn.addEventListener('change', function () { p.radius = Math.max(0, num(rIn.value)); refresh(); changed(); });
      numbers.appendChild(row('Map', mapIn));
      numbers.appendChild(row('X', xIn));
      numbers.appendChild(row('Y', yIn));
      if (rIn) numbers.appendChild(row('Radius', rIn));
      if (numbersOpen) numbers.classList.remove('hidden');
      box.appendChild(numbers);

      box.appendChild(button('Numbers', function () { numbersOpen = !numbersOpen; numbers.classList.toggle('hidden', !numbersOpen); }, null, 'Type the coordinates'));

      nums = { map: mapIn, x: xIn, y: yIn, radius: rIn };
      sync();
    }

    build();
    return box;
  }

  function areaMonsters(place) {
    if (!place || !place.map) return Promise.resolve([]);
    return hints('map', place.map).then(function (h) {
      var r = Math.max(0, num(place.radius));
      var byId = {};
      ((h && h.respawns) || []).forEach(function (sp) {
        var spread = num(sp.spread);
        if (Math.abs(num(sp.x) - num(place.x)) > r + spread || Math.abs(num(sp.y) - num(place.y)) > r + spread) return;
        var g = byId[sp.monster];
        if (!g) byId[sp.monster] = g = { monster: sp.monster, name: sp.name, level: sp.level, boss: sp.boss, count: 0, respawns: 0 };
        g.count += num(sp.count);
        g.respawns += 1;
      });
      return Object.keys(byId).map(function (k) { return byId[k]; }).sort(function (a, b) { return b.count - a.count || String(a.name).localeCompare(String(b.name)); });
    });
  }

  function suggest(input) {
    input = input || {};
    var steps = input.steps || [];
    var levelHint = num(input.levelHint);

    var monsterIds = {}, itemIds = {};
    steps.forEach(function (s) {
      (s.monsters || []).forEach(function (id) { if (id) monsterIds[id] = true; });
      if ((s.type === 'Collect' || s.type === 'Deliver') && s.item) itemIds[s.item] = true;
    });

    var mids = Object.keys(monsterIds), iids = Object.keys(itemIds);
    var req = mids.map(function (id) { return hints('monster', Number(id)); }).concat(iids.map(function (id) { return hints('item', Number(id)); }));
    return Promise.all(req).then(function (all) {
      var mInfo = {}, iInfo = {};
      mids.forEach(function (id, n) { mInfo[id] = all[n]; });
      iids.forEach(function (id, n) { iInfo[id] = all[mids.length + n]; });

      var per = steps.map(function (s) {
        var count = num(s.count);
        if (s.type === 'Kill') {
          var infos = (s.monsters || []).map(function (id) { return mInfo[id]; }).filter(Boolean);
          var level = infos.length ? infos.reduce(function (a, m) { return a + num(m.level); }, 0) / infos.length : 0;
          var exp = infos.length ? infos.reduce(function (a, m) { return a + num(m.experience); }, 0) / infos.length : 0;
          return { type: s.type, count: count, level: level, exp: exp * count * TUNING.killExpFactor, item: s.item };
        }
        return { type: s.type, count: count, level: s.type === 'ReachLevel' ? count : 0, exp: 0, item: s.item };
      });

      var candidates = [];
      per.forEach(function (c) {
        if (c.type === 'Kill' && c.level > 0) candidates.push(c.level);
        else if (c.type === 'ReachLevel' && c.count > 0) candidates.push(c.count);
      });
      var level = candidates.length ? Math.round(Math.max.apply(null, candidates)) : 0;

      var exp = 0;
      per.forEach(function (c) {
        if (c.type === 'Kill') { exp += c.exp; return; }
        var l = c.level || levelHint || 1;
        exp += TUNING.stepExpBase + TUNING.stepExpPerLevelSq * l * l;
      });

      var gold = TUNING.goldPerLevelPerStep * (level || levelHint || 1) * steps.length;
      per.forEach(function (c) {
        if (c.type !== 'Collect' && c.type !== 'Deliver') return;
        var it = iInfo[c.item];
        if (it) gold += num(it.price) * c.count * TUNING.itemPriceFactor;
      });

      return { level: level, exp: Math.round(exp), gold: Math.round(gold) };
    });
  }

  function saveGroup(members, keyBase) {
    var P = global.QuestsPage;
    var meta = P && P.meta ? P.meta() : null;
    var groups = (meta && meta.groups) || [];
    var want = (members || []).slice().sort(function (a, b) { return a - b; });

    var same = groups.filter(function (g) {
      if (g.kind !== 0 || !g.members || g.members.length !== want.length) return false;
      var have = g.members.slice().sort(function (a, b) { return a - b; });
      for (var i = 0; i < want.length; i++) if (have[i] !== want[i]) return false;
      return true;
    })[0];
    if (same) return Promise.resolve(same.index);

    var taken = {};
    groups.forEach(function (g) { if (g.authorKey) taken[g.authorKey.toLowerCase()] = true; });
    var base = keyBase || 'area', key = base;
    for (var n = 2; taken[key.toLowerCase()]; n++) key = base + '-' + n;

    return global.OpsUI.post(API + '/groups/save', { group: { index: 0, authorKey: key, kind: 0, members: want } }).then(function (j) {
      if (!j || !j.__ok) return 0;
      var reload = P && P.reloadMeta ? P.reloadMeta() : Promise.resolve();
      return Promise.resolve(reload).then(function () { return (j.data && j.data.index) || 0; });
    });
  }

  function summarySentences(steps, opts) {
    opts = opts || {};
    var out = [];
    (steps || []).forEach(function (s) {
      var n = num(s.count);
      switch (s.type) {
        case 'Kill': out.push('Kill ' + n + ' ' + (s.target || 'monsters') + (s.place && s.mapTitle ? ' near ' + s.mapTitle : '') + '.'); break;
        case 'Collect': out.push('Bring me ' + n + ' ' + (s.item || 'items') + '.'); break;
        case 'Deliver': out.push('Take the ' + (s.item || 'item') + ' to ' + (s.npc || 'the NPC') + '.'); break;
        case 'Talk': out.push('Speak with ' + (s.npc || 'the NPC') + '.'); break;
        case 'ReachArea': out.push('Travel to ' + (s.mapTitle || 'the place') + '.'); break;
        case 'ReachLevel': out.push('Come back when you reach level ' + n + '.'); break;
      }
    });
    if (!opts.lastTalkToTurnIn) {
      var who = opts.finisherName || opts.giverName;
      if (who) out.push('Then return to ' + who + '.');
    }
    return out;
  }

  global.QuestsAssist = {
    hints: hints, clearHints: clearHints, hintLine: hintLine, placeLine: placeLine, thumbnail: thumbnail, areaMonsters: areaMonsters,
    suggest: suggest, saveGroup: saveGroup, players: players, teleport: teleport, teleportPrompt: teleportPrompt,
    itemIconUrl: itemIconUrl, summarySentences: summarySentences, TUNING: TUNING,
  };
})(window);
