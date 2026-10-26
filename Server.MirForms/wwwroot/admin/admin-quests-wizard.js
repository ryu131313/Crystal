/*
 * Quests › New quest wizard: builds a whole quest graph for the common shapes (kill / collect / deliver / talk / go to / reach level,
 * one step per objective) and opens it as an unsaved draft in the Quests editor (QuestsPage.openDraft). It never calls the API itself:
 * hints, map thumbnails, group saving, suggestions and teleport are all in QuestsAssist.
 */
(function (global) {
  'use strict';

  var state = null;
  var previewEl = null;

  var TYPES = [
    { value: 'Kill', label: 'Kill monsters' }, { value: 'Collect', label: 'Collect items' }, { value: 'Deliver', label: 'Deliver an item' },
    { value: 'Talk', label: 'Talk to an NPC' }, { value: 'ReachArea', label: 'Go to a place' }, { value: 'ReachLevel', label: 'Reach a level' }
  ];
  var TEMPLATES = ['Blank', 'Hunt', 'Hunt then report', 'Fetch and deliver', 'Travel chain', 'Daily bounty'];

  function k() { return global.QuestsPage.kit; }
  function A() { return global.QuestsAssist; }
  function n(v) { var x = Number(v); return isFinite(x) ? Math.trunc(x) : 0; }
  function toast(msg, kind) { if (typeof global.portalToast === 'function') global.portalToast(msg, kind || 'info'); }

  function meta() { var P = global.QuestsPage; return (P && P.meta && P.meta()) || {}; }

  function blankStep() {
    return { type: 'Kill', killMode: 'one', targetId: 0, count: 10, place: { map: 0, x: 0, y: 0, radius: 5 }, area: null, picked: [], onlyHere: false, giveOnAccept: false, title: '' };
  }
  function counts(type) { return type === 'Kill' || type === 'Collect' || type === 'Deliver' || type === 'ReachLevel'; }

  function groupMembers(id) {
    var g = (meta().groups || []).filter(function (x) { return x.index === id; })[0];
    return g ? (g.members || []) : [];
  }
  function groupName(id) {
    var g = (meta().groups || []).filter(function (x) { return x.index === id; })[0];
    return g ? g.authorKey : '';
  }
  function npcName(id) { return k().lookupLabel('npcs', id) || 'the NPC'; }
  function itemName(id) { return k().lookupLabel('items', id) || 'the item'; }
  function monsterName(id) { return k().lookupLabel('monsters', id) || 'monsters'; }

  function mapTitle(id) {
    if (!id) return '';
    if (state.mapTitles[id]) return state.mapTitles[id];
    if (!state.mapRequests[id]) {
      state.mapRequests[id] = true;
      if (A()) A().hints('map', id).then(function (m) { if (state && m && m.title) state.mapTitles[id] = m.title; });
    }
    return '';
  }

  function defaultTitle(s) {
    if (s.type === 'Kill') {
      if (s.killMode === 'area') {
        var areaName = s.picked.length === 1 ? monsterName(s.picked[0]) : 'monsters';
        return 'Kill ' + n(s.count) + ' ' + areaName + ' near ' + (mapTitle(s.place.map) || 'the area');
      }
      var mon = s.killMode === 'group' ? (groupName(s.targetId) || 'monsters') : monsterName(s.targetId);
      return 'Kill ' + n(s.count) + ' ' + mon;
    }
    switch (s.type) {
      case 'Collect': return 'Collect ' + n(s.count) + ' ' + itemName(s.targetId);
      case 'Deliver': return 'Deliver ' + itemName(s.targetId) + ' to ' + npcName(state.finisher || state.giver);
      case 'Talk': return 'Talk to ' + npcName(s.targetId);
      case 'ReachArea': return 'Go to ' + (k().lookupLabel('maps', s.place.map) || 'the place');
      case 'ReachLevel': return 'Reach level ' + n(s.count);
    }
    return '';
  }

  function textArea(value, onInput, rows) {
    var K = k();
    var e = K.h('textarea', { cls: 'qe-body', rows: rows || 3 });
    e.value = value || '';
    e.addEventListener('input', function () { onInput(e.value); });
    return e;
  }

  function checkState() {
    var out = [];
    function add(key, message) { out.push({ key: key, message: message }); }
    if (!String(state.name || '').trim()) add('name', 'Give the quest a name.');
    if (!state.noGiver && !state.giver) add('giver', 'Pick a giver NPC, or tick \'No giver\'.');
    if (state.steps.some(function (s) { return s.type === 'Deliver'; }) && !state.finisher) add('finisher', 'Deliver: pick the NPC to turn in to (Turn in to).');
    state.steps.forEach(function (s, i) {
      var at = 'step' + i, no = 'Step ' + (i + 1) + ': ';
      if (s.type === 'ReachArea') {
        if (!s.place.map) add(at + '.place', no + 'pick the place on the map.');
        else if (n(s.place.radius) <= 0) add(at + '.place', no + 'the radius must be above 0.');
      } else if (s.type === 'Kill' && s.killMode === 'area') {
        if (!s.place.map) add(at + '.place', no + 'pick the hunting area.');
        else if (!s.picked.length) add(at + '.picked', no + 'tick at least one monster.');
      } else if (s.type !== 'ReachLevel' && s.targetId <= 0) {
        add(at + '.target', no + 'pick a target.');
      }
      if (counts(s.type) && n(s.count) < 1) add(at + '.count', no + (s.type === 'ReachLevel' ? 'the level must be at least 1.' : 'the count must be at least 1.'));
    });
    state.items.forEach(function (it, i) { if (!it.id) add('item' + i, 'Item reward ' + (i + 1) + ': pick an item.'); });
    return out;
  }

  function toSuggestInput() {
    return {
      levelHint: state.levelHint,
      steps: state.steps.map(function (s) {
        var monsters = [];
        if (s.type === 'Kill') {
          if (s.killMode === 'area') monsters = s.picked.slice();
          else if (s.killMode === 'group') monsters = groupMembers(s.targetId);
          else if (s.targetId) monsters = [s.targetId];
        }
        return { type: s.type, monsters: monsters, count: s.count, item: (s.type === 'Collect' || s.type === 'Deliver') ? s.targetId : 0 };
      }),
    };
  }

  function textSteps() {
    return state.steps.map(function (s) {
      var target = '';
      if (s.type === 'Kill') {
        if (s.killMode === 'area') target = s.picked.length === 1 ? monsterName(s.picked[0]) : 'monsters';
        else target = s.killMode === 'group' ? (groupName(s.targetId) || 'monsters') : monsterName(s.targetId);
      }
      return {
        type: s.type, count: s.count, target: target,
        item: (s.type === 'Collect' || s.type === 'Deliver') ? itemName(s.targetId) : '',
        npc: s.type === 'Deliver' ? npcName(state.finisher || state.giver) : npcName(s.targetId),
        mapTitle: mapTitle(s.place.map) || '',
        place: s.type === 'Kill' && s.killMode === 'area',
      };
    });
  }

  async function resolveAreas() {
    var K = k();
    for (var i = 0; i < state.steps.length; i++) {
      var s = state.steps[i];
      if (s.type !== 'Kill' || s.killMode !== 'area') continue;
      if (!s.picked.length) return false;
      if (s.picked.length === 1) { s.resolvedTarget = { kind: 'Monster', id: s.picked[0] }; continue; }
      var map = await A().hints('map', s.place.map);
      var base = 'area.' + K.slug((map && map.title) || ('map-' + s.place.map)) + '.' + K.slug(monsterName(s.picked[0]));
      var gid = await A().saveGroup(s.picked.slice().sort(function (a, b) { return a - b; }), base);
      if (!gid) return false;
      s.resolvedTarget = { kind: 'MonsterGroup', id: gid };
    }
    return true;
  }

  function build() {
    var K = k(), P = global.QuestsPage;
    var q = P.blankQuest();
    q.steps = [];
    q.category = state.category;
    q.levelHint = state.levelHint;
    q.repeatPolicy = state.repeat;
    q.cooldownSeconds = K.enumLabel('repeatPolicy', state.repeat) === 'Cooldown' ? state.cooldown : 0;

    var base = K.enumLabel('category', state.category).toLowerCase() + '.' + (K.slug(state.name) || 'quest');
    var taken = {};
    P.authorKeys().forEach(function (a) { if (a) taken[a.toLowerCase()] = true; });
    var ak = base;
    for (var i = 2; taken[ak.toLowerCase()]; i++) ak = base + '-' + i;
    q.authorKey = ak;

    function text(fieldKey, body) { if (!String(body || '').trim()) return ''; q.texts[fieldKey] = body; return fieldKey; }
    q.nameKey = text(ak + '.name', state.name.trim());
    q.summaryKey = text(ak + '.summary', state.summary);
    q.completeKey = text(ak + '.complete', state.complete);

    var targetKinds = { Kill: 'Monster', Collect: 'Item', Deliver: 'Item', Talk: 'Npc', ReachArea: 'Map', ReachLevel: 'None' };
    state.steps.forEach(function (s, i) {
      var stepId = K.newTemp();
      var sfx = ak + '.s' + K.idPart(stepId);
      q.steps.push({ index: stepId, order: i + 1, titleKey: text(sfx + '.title', s.title.trim() || defaultTitle(s)), textKey: '', readyTextKey: '',
        completionMode: 0, nRequired: 0, nextStep: 0, timeLimitSeconds: 0, failCondition: 0, onEnterPage: '', onCompletePage: '' });

      var kind = targetKinds[s.type];
      var targetId = s.type === 'ReachLevel' ? 0 : s.targetId;
      var condition = 0, map = 0, x = 0, y = 0, radius = 0;

      if (s.type === 'Kill' && s.killMode === 'area') {
        kind = s.resolvedTarget ? s.resolvedTarget.kind : 'MonsterGroup';
        targetId = s.resolvedTarget ? s.resolvedTarget.id : 0;
        map = s.place.map; x = s.place.x; y = s.place.y; radius = s.place.radius;
      } else if (s.type === 'ReachArea') {
        targetId = s.place.map; x = s.place.x; y = s.place.y; radius = s.place.radius;
      } else if (s.type === 'Kill' && s.killMode === 'group') {
        kind = 'MonsterGroup'; targetId = s.targetId;
        if (s.place.map) { map = s.place.map; x = s.place.x; y = s.place.y; radius = s.place.radius; }
      } else if (s.type === 'Kill' && s.place.map) {
        map = s.place.map; x = s.place.x; y = s.place.y; radius = s.place.radius;
      }

      if (s.type === 'Kill' && s.onlyHere && map) {
        var condId = K.newTemp();
        q.conditions.push({ index: condId, op: K.enumValue('conditionOp', 'Leaf'), parent: 0, order: 0,
          leaf: K.enumValue('conditionLeaf', 'MapIs'), arg1: map, arg2: 0, cmp: K.enumValue('compare', 'Eq') });
        condition = condId;
      }

      q.objectives.push({ index: K.newTemp(), step: stepId, order: 1, type: K.enumValue('objectiveType', s.type), partyCredit: 0,
        targetKind: K.enumValue('targetKind', kind), targetId: targetId, count: counts(s.type) ? s.count : 1,
        map: map, x: x, y: y, radius: radius,
        optional: false, hidden: false, condition: condition, textKey: '', dropChance: 0, params: '' });

      if (s.type === 'Deliver' && s.giveOnAccept) q.rewards.push(reward('Accept', 'Item', s.targetId, s.count));
    });

    var npc = K.enumValue('endpointKind', 'Npc');
    if (!state.noGiver && state.giver) q.endpoints.push({ role: K.enumValue('endpointRole', 'Start'), kind: npc, targetId: state.giver, map: 0 });
    if (state.finisher && state.finisher !== state.giver) q.endpoints.push({ role: K.enumValue('endpointRole', 'Finish'), kind: npc, targetId: state.finisher, map: 0 });
    if (state.exp > 0) q.rewards.push(reward('Quest', 'Exp', 0, state.exp));
    if (state.gold > 0) q.rewards.push(reward('Quest', 'Gold', 0, state.gold));
    var group = state.pickOne && state.items.length > 1 ? 1 : 0;
    state.items.forEach(function (it) { var r = reward('Quest', 'Item', it.id, it.count || 1); r.choiceGroup = group; q.rewards.push(r); });

    if (state.after) {
      var c = K.newTemp();
      q.conditions.push({ index: c, op: K.enumValue('conditionOp', 'Leaf'), parent: 0, order: 0,
        leaf: K.enumValue('conditionLeaf', 'QuestStatus'), arg1: state.after.index, arg2: K.enumValue('runStatus', 'Completed'), cmp: K.enumValue('compare', 'Eq') });
      q.acceptCondition = c;
      q.storylineIndex = state.storylineIndex || 0;
      q.storylineOrder = state.storylineIndex ? (state.storylineOrder || 0) + 1 : 0;
    }
    return q;

    function reward(scopeName, kindName, targetId, amount) {
      return { scope: K.enumValue('rewardScope', scopeName), scopeId: 0, kind: K.enumValue('rewardKind', kindName), targetId: targetId,
        amount: amount, scaling: 0, choiceGroup: 0, condition: 0, firstTimeOnly: false };
    }
  }

  async function prepare(host) {
    var st = state;
    state.tried = true;
    for (var i = 0; i < state.steps.length; i++) {
      var s = state.steps[i];
      if (s.type !== 'Kill' || s.killMode !== 'area' || !s.place.map || s.area || !A()) continue;
      if (s._areaTimer) { clearTimeout(s._areaTimer); s._areaTimer = null; }
      s.area = await A().areaMonsters(s.place);
      if (state !== st) return false;
      s.picked = s.area.filter(function (m) { return !m.boss; }).map(function (m) { return m.monster; });
    }
    state.errors = checkState();
    if (state.errors.length) { render(host); return false; }
    if (!state.levelTouched && A()) {
      var sug = await A().suggest(toSuggestInput());
      if (state !== st) return false;
      if (sug && sug.level) state.levelHint = sug.level;
    }
    if (!await resolveAreas()) {
      if (state !== st) return false;
      state.errors = [{ key: 'steps', message: 'Could not create the monster group for the area.' }];
      render(host);
      return false;
    }
    return state === st;
  }

  async function createDraft(host) {
    if (!await prepare(host)) return;
    var ok = await global.QuestsPage.openDraft(build());
    if (ok) global.QuestsPage.check();
  }

  async function createAndTest(host) {
    var ch = n(state.testChar);
    if (!ch) { toast('Pick an online character first.', 'warn'); return; }
    if (!await prepare(host)) return;
    var ok = await global.QuestsPage.openDraft(build());
    if (!ok) return;
    var r = await global.QuestsPage.saveNow();
    if (!r || !r.index) return;   // the editor shows the Validation tab
    await global.QuestsPage.startOn(ch);
    if (state.teleportToGiver && state.giver && A()) {
      var row = (state.players || []).filter(function (p) { return p.characterIndex === ch; })[0];
      var npc = await A().hints('npc', state.giver);
      if (row && npc && npc.map) await A().teleport(row.name, npc.map, npc.x, npc.y, ch, 'Quests › save & test');
    }
  }

  function move(host, i, delta) {
    var j = i + delta;
    if (j < 0 || j >= state.steps.length) return;
    var s = state.steps.splice(i, 1)[0];
    state.steps.splice(j, 0, s);
    render(host);
  }

  function addStep() {
    var prev = state.steps[state.steps.length - 1];
    var s = blankStep();
    if (prev) {
      s.type = prev.type; s.killMode = prev.killMode; s.count = prev.count;
      s.place = { map: prev.place.map, x: prev.place.x, y: prev.place.y, radius: prev.place.radius };
      s.targetId = 0;
    }
    if (!s.place.map && state.region) s.place.map = state.region;
    state.steps.push(s);
  }

  function talkStep() {
    var s = blankStep();
    s.type = 'Talk'; s.targetId = state.giver || state.finisher || 0; s.count = 1;
    return s;
  }

  function togglePick(s, id, on) {
    var i = s.picked.indexOf(id);
    if (on && i < 0) s.picked.push(id);
    else if (!on && i >= 0) s.picked.splice(i, 1);
    s.picked.sort(function (a, b) { return a - b; });
  }

  function regionList(name) {
    if (!state.region || state.regionAll) return null;
    return { list: name === 'npcs' ? 'qe-dl-region-npcs' : 'qe-dl-region-monsters' };
  }

  function scheduleArea(host, s, areaBox) {
    var st = state;
    if (s._areaTimer) clearTimeout(s._areaTimer);
    s._areaTimer = setTimeout(function () {
      s._areaTimer = null;
      if (!A()) return;
      A().areaMonsters(s.place).then(function (list) {
        if (state !== st) return;
        s.area = list;
        s.picked = list.filter(function (m) { return !m.boss; }).map(function (m) { return m.monster; });
        if (s._refreshTitle) s._refreshTitle();
        if (areaBox && areaBox.isConnected) fillArea(areaBox, host, s);
      });
    }, 350);
  }

  function fillArea(box, host, s) {
    var K = k();
    K.clear(box);
    if (!s.area || !s.area.length) { box.appendChild(K.h('p', { cls: 'mir-muted', text: 'No monster spawns in this area: make it bigger or move it.' })); return; }
    box.appendChild(K.h('p', { cls: 'mir-muted', text: 'Monsters that spawn in this area:' }));
    s.area.forEach(function (m) {
      var cb = K.h('input', { type: 'checkbox', checked: s.picked.indexOf(m.monster) >= 0 });
      cb.addEventListener('change', function () {
        togglePick(s, m.monster, cb.checked);
        if (s._refreshTitle) s._refreshTitle();
        note.textContent = s.picked.length > 1 ? 'A shared group area.… is created (or reused) when you click Create.' : '';
      });
      box.appendChild(K.h('label', { cls: 'qe-check' }, cb, ' ' + m.name + ' (Lv ' + n(m.level) + ', ×' + n(m.count) + (m.boss ? ', boss' : '') + ')'));
    });
    box.appendChild(K.checkbox(s.onlyHere, function (v) { s.onlyHere = v; }, 'Only kills on this map count'));
    box.appendChild(K.h('span', { cls: 'qe-hint', text: 'Kills anywhere count otherwise; the area is shown on the tracker' }));
    var note = K.h('p', { cls: 'mir-muted', text: s.picked.length > 1 ? 'A shared group area.… is created (or reused) when you click Create.' : '' });
    box.appendChild(note);
  }

  function stepCard(host, s, i) {
    var K = k();
    var card = K.h('div', { cls: 'qe-step' });
    var only = state.steps.length === 1;
    var areaBox = null;

    var handle = K.h('span', { cls: 'qe-drag', text: '⠿', draggable: 'true', title: 'Drag to reorder' });
    handle.addEventListener('dragstart', function (e) { e.dataTransfer.setData('text/x-quest-step', String(i)); e.dataTransfer.effectAllowed = 'move'; });
    card.addEventListener('dragover', function (e) { e.preventDefault(); });
    card.addEventListener('drop', function (e) {
      var raw = e.dataTransfer.getData('text/x-quest-step');
      if (!raw) return;
      e.preventDefault();
      var from = Number(raw);
      if (isNaN(from) || from === i) return;
      var moved = state.steps.splice(from, 1)[0];
      state.steps.splice(i, 0, moved);
      render(host);
    });

    card.appendChild(K.h('div', { cls: 'qe-step-head' },
      handle,
      K.h('strong', { text: 'Step ' + (i + 1) }),
      K.h('span', { cls: 'qe-spacer' }),
      K.button('↑', function () { move(host, i, -1); }, null, 'Move up'),
      K.button('↓', function () { move(host, i, 1); }, null, 'Move down'),
      only ? null : K.button('Remove', function () { state.steps.splice(i, 1); render(host); }, 'qe-danger')));

    var titleInput = K.input(s.title, function (v) { s.title = v; }, { placeholder: defaultTitle(s) });
    function refreshTitle() { titleInput.placeholder = defaultTitle(s); }
    s._refreshTitle = refreshTitle;

    var grid = K.h('div', { cls: 'qe-grid' },
      K.field('Type', K.select(s.type, TYPES, function (v) { s.type = v; s.targetId = 0; s.area = null; s.picked = []; render(host); })));

    if (s.type === 'Kill') {
      grid.appendChild(K.field('Target', K.select(s.killMode, [
        { value: 'one', label: 'One monster' }, { value: 'group', label: 'Any monster of a group' }, { value: 'area', label: 'Monsters in an area' },
      ], function (v) { s.killMode = v; s.targetId = 0; s.area = null; s.picked = []; s.onlyHere = v === 'area'; if (v === 'area') s.place.radius = 20; render(host); })));

      if (s.killMode === 'one') {
        var one = K.field('Monster', K.picker('monsters', s.targetId, function (v) { s.targetId = v; refreshTitle(); }, regionList('monsters')));
        one.setAttribute('data-key', 'step' + i + '.target');
        grid.appendChild(one);
      } else if (s.killMode === 'group') {
        var grp = K.field('Monster group', K.targetControl('MonsterGroup', s.targetId, function (v) { s.targetId = v; refreshTitle(); }));
        grp.setAttribute('data-key', 'step' + i + '.target');
        grid.appendChild(grp);
      } else {
        var area = K.field('Area', A().placeLine(s.place, function (p) {
          s.place = p;
          var key = p.map + ':' + n(p.x) + ':' + n(p.y) + ':' + n(p.radius);
          if (key !== s.areaKey) { s.areaKey = key; scheduleArea(host, s, areaBox); }
          refreshTitle();
        }, { radius: true, defaultRadius: 20, title: 'Where do they hunt?' }));
        area.setAttribute('data-key', 'step' + i + '.place');
        grid.appendChild(area);
      }
    } else if (s.type === 'Collect' || s.type === 'Deliver') {
      var item = K.field('Item', K.picker('items', s.targetId, function (v) {
        s.targetId = v;
        var next = state.steps[i + 1];
        if (next && next.type === 'Deliver' && !next.targetId) { next.targetId = v; render(host); return; }
        refreshTitle();
      }));
      item.setAttribute('data-key', 'step' + i + '.target');
      grid.appendChild(item);
    } else if (s.type === 'Talk') {
      var talk = K.field('NPC', K.picker('npcs', s.targetId, function (v) { s.targetId = v; refreshTitle(); }, regionList('npcs')));
      talk.setAttribute('data-key', 'step' + i + '.target');
      grid.appendChild(talk);
      if (!state.finisher && s.targetId) {
        var fin = K.h('span', { cls: 'qe-hint-line' });
        fin.appendChild(K.button('Finish at this NPC', function () { state.finisher = s.targetId; render(host); }));
        grid.appendChild(fin);
      }
    } else if (s.type === 'ReachArea') {
      var place = K.field('Place', A().placeLine(s.place, function (p) { s.place = p; refreshTitle(); }, { radius: true, defaultRadius: 5, title: 'Where must the player go?' }));
      place.setAttribute('data-key', 'step' + i + '.place');
      grid.appendChild(place);
    }

    if (counts(s.type)) {
      var count = K.field(s.type === 'ReachLevel' ? 'Level' : 'How many', K.numberInput(s.count, function (v) { s.count = v; refreshTitle(); }, { min: 1 }));
      count.setAttribute('data-key', 'step' + i + '.count');
      grid.appendChild(count);
    }
    if (s.type === 'Deliver') grid.appendChild(K.checkbox(s.giveOnAccept, function (v) { s.giveOnAccept = v; }, 'Give the item(s) when the quest is accepted'));
    if (s.type === 'Kill' && s.killMode !== 'area') {
      if (s.place.map || s.marker) {
        grid.appendChild(K.field('Marker', A().placeLine(s.place, function (p) { s.place = p; refreshTitle(); }, { radius: true, defaultRadius: 10 })));
        grid.appendChild(K.checkbox(s.onlyHere, function (v) { s.onlyHere = v; }, 'Only kills on this map count'));
      } else {
        grid.appendChild(K.button('Add a map marker', function () { s.marker = true; if (!s.place.map && state.region) s.place.map = state.region; render(host); }));
      }
    }
    grid.appendChild(K.field('Tracker title', titleInput));
    card.appendChild(grid);

    if (s.type === 'Kill' && s.killMode === 'area') {
      areaBox = K.h('div', { cls: 'qe-area', 'data-key': 'step' + i + '.picked' });
      card.appendChild(areaBox);
      if (s.area) fillArea(areaBox, host, s);
      else if (s.place.map) scheduleArea(host, s, areaBox);
    }
    return card;
  }

  async function applyTemplate(name, host) {
    var K = k();
    if (state.steps.some(function (s) { return s.targetId || s.picked.length || s.place.map; })) {
      if (!await global.portalConfirm('Replace the steps?', { title: 'Start from ' + name, confirmLabel: 'Replace', danger: true })) return;
    }
    if (name === 'Hunt' || name === 'Hunt then report' || name === 'Daily bounty') {
      state.steps = [blankStep()];
      state.steps[0].killMode = 'area';
      state.steps[0].onlyHere = true;
      state.steps[0].place.radius = 20;
      state.steps[0].count = name === 'Daily bounty' ? 20 : 10;
      if (name === 'Hunt then report') state.steps.push(talkStep());
      if (name === 'Daily bounty') { state.category = K.enumValue('category', 'Daily'); state.repeat = K.enumValue('repeatPolicy', 'Daily'); }
    } else if (name === 'Fetch and deliver') {
      var c = blankStep(); c.type = 'Collect'; c.count = 5;
      var d = blankStep(); d.type = 'Deliver';
      state.steps = [c, d];
    } else if (name === 'Travel chain') {
      var a = blankStep(); a.type = 'ReachArea';
      var b = blankStep(); b.type = 'Talk';
      var e = blankStep(); e.type = 'ReachArea';
      state.steps = [a, b, e];
    } else {
      state.steps = [blankStep()];
    }
    render(host);
  }

  function isMapIsCondition(c) { return c && k().enumLabel('conditionLeaf', c.leaf) === 'MapIs'; }

  function stateFromGraph(graph, st) {
    var K = k();
    if (!graph) return null;
    var wizardTypes = { Kill: 1, Collect: 1, Deliver: 1, Talk: 1, ReachArea: 1, ReachLevel: 1 };
    var conds = graph.conditions || [];
    var accept = conds.filter(function (c) { return c.index === graph.acceptCondition; })[0];
    var acceptOk = !!accept && K.enumLabel('conditionLeaf', accept.leaf) === 'QuestStatus' &&
      K.enumLabel('runStatus', accept.arg2) === 'Completed' && K.enumLabel('compare', accept.cmp) === 'Eq';
    if ((graph.acceptCondition || 0) && !acceptOk) { toast("This quest's accept condition can't be shown in the wizard; use Duplicate instead.", 'warn'); return null; }
    var bad =
      (graph.branches && graph.branches.length) ||
      (graph.visibleCondition || 0) ||
      conds.some(function (c) { return !isMapIsCondition(c) && !(acceptOk && c.index === accept.index); }) ||
      (graph.rewards || []).some(function (r) { var sc = K.enumLabel('rewardScope', r.scope); return sc === 'Step' || sc === 'Outcome' || sc === 'Objective'; }) ||
      (graph.steps || []).some(function (s) { return (graph.objectives || []).filter(function (o) { return o.step === s.index; }).length > 1; }) ||
      (graph.objectives || []).some(function (o) { return !wizardTypes[K.enumLabel('objectiveType', o.type)]; });
    if (bad) { toast("This quest has parts the wizard can't show; use Duplicate instead.", 'warn'); return null; }
    if (acceptOk) st.after = { index: n(accept.arg1), name: K.lookupLabel('quests', n(accept.arg1)) || ('#' + n(accept.arg1)) };

    st.category = graph.category; st.levelHint = n(graph.levelHint); st.levelTouched = true;
    st.repeat = graph.repeatPolicy; st.cooldown = n(graph.cooldownSeconds);
    var start = (graph.endpoints || []).filter(function (e) { return e.role === K.enumValue('endpointRole', 'Start'); })[0];
    var finish = (graph.endpoints || []).filter(function (e) { return e.role === K.enumValue('endpointRole', 'Finish'); })[0];
    st.giver = start ? start.targetId : 0; st.finisher = finish ? finish.targetId : 0;

    st.steps = (graph.steps || []).slice().sort(function (a, b) { return a.order - b.order; }).map(function (s) {
      var o = (graph.objectives || []).filter(function (x) { return x.step === s.index; })[0];
      var step = blankStep();
      if (!o) { step.type = 'Talk'; return step; }
      step.type = K.enumLabel('objectiveType', o.type);
      step.count = n(o.count) || 1;
      step.targetId = o.targetId;
      if (step.type === 'Kill') step.killMode = K.enumLabel('targetKind', o.targetKind) === 'MonsterGroup' ? 'group' : 'one';
      if (o.map || o.x || o.y || o.radius) step.place = { map: n(o.map), x: n(o.x), y: n(o.y), radius: n(o.radius) };
      if (step.type === 'ReachArea') step.place = { map: n(o.targetId), x: n(o.x), y: n(o.y), radius: n(o.radius) };
      var cond = (graph.conditions || []).filter(function (c) { return c.index === o.condition; })[0];
      if (step.type === 'Kill') step.onlyHere = isMapIsCondition(cond);
      if (step.type === 'Kill' && step.onlyHere) step.place = { map: n(cond.arg1), x: n(o.x), y: n(o.y), radius: n(o.radius) };
      return step;
    });
    if (!st.steps.length) st.steps = [blankStep()];

    (graph.rewards || []).forEach(function (r) {
      var sc = K.enumLabel('rewardScope', r.scope), kind = K.enumLabel('rewardKind', r.kind);
      if (sc === 'Quest') {
        if (kind === 'Exp') st.exp = n(r.amount);
        else if (kind === 'Gold') st.gold = n(r.amount);
        else if (kind === 'Item') st.items.push({ id: r.targetId, count: n(r.amount) || 1 });
      } else if (sc === 'Accept' && kind === 'Item') {
        var step = st.steps.filter(function (x) { return x.targetId === r.targetId; })[0];
        if (step) step.giveOnAccept = true;
      }
    });
    return st;
  }

  function previewRewards() {
    var out = [];
    if (state.exp) out.push(n(state.exp) + ' exp');
    if (state.gold) out.push(n(state.gold) + ' gold');
    state.items.forEach(function (it) { if (it.id) out.push(itemName(it.id) + ' ×' + n(it.count || 1)); });
    if (state.pickOne && state.items.length > 1) out.push('(choose one)');
    return out.length ? out.join(', ') : '(none)';
  }

  function targetLabel(s) {
    if (s.type === 'Kill') {
      if (s.killMode === 'area') return s.picked.length === 1 ? monsterName(s.picked[0]) : 'monsters';
      return s.killMode === 'group' ? (groupName(s.targetId) || 'monsters') : monsterName(s.targetId);
    }
    if (s.type === 'Collect' || s.type === 'Deliver') return itemName(s.targetId);
    if (s.type === 'Talk') return npcName(s.targetId);
    if (s.type === 'ReachArea') return k().lookupLabel('maps', s.place.map) || 'the place';
    return '';
  }

  function renderPreview() {
    if (!previewEl) return;
    var K = k();
    K.clear(previewEl);
    previewEl.appendChild(K.h('h4', { text: 'Preview' }));
    var offer = K.h('div', { cls: 'qe-preview-box' });
    offer.appendChild(K.h('strong', { text: state.name || '(no name)' }));
    offer.appendChild(K.h('p', { text: state.summary || '' }));
    offer.appendChild(K.h('p', { cls: 'mir-muted', text: 'Rewards:' }));
    offer.appendChild(K.h('p', { text: previewRewards() }));
    previewEl.appendChild(offer);

    var tracker = K.h('div', { cls: 'qe-preview-box' });
    state.steps.forEach(function (s, i) {
      var count = counts(s.type) ? n(s.count) : 1;
      var label = targetLabel(s);
      tracker.appendChild(K.h('div', { cls: 'qe-preview-step' }, K.h('strong', { text: (i + 1) + '. ' + (s.title || defaultTitle(s)) }), K.h('span', { text: ' ' + (label ? label + ' ' : '') + '0/' + count })));
    });
    previewEl.appendChild(tracker);
    previewEl.appendChild(K.h('p', { cls: 'qe-hint-line', text: 'Approximate: the game client draws the real dialog.' }));
  }

  function regionDatalist(id, opts) {
    var K = k();
    var dl = K.h('datalist', { id: id });
    opts.forEach(function (o) { dl.appendChild(K.h('option', { value: o.label + ' #' + o.index })); });
    return dl;
  }

  function buildRegionLists(hostEl, mapId) {
    if (!A()) return;
    var K = k();
    A().hints('map', mapId).then(function (m) {
      if (!m || !state || state.region !== mapId) return;
      K.clear(hostEl);
      hostEl.appendChild(regionDatalist('qe-dl-region-npcs', (m.npcs || []).map(function (x) { return { index: x.index, label: x.name }; })));
      var seen = {}, mons = [];
      (m.respawns || []).forEach(function (r) { if (!seen[r.monster]) { seen[r.monster] = true; mons.push({ index: r.monster, label: r.name }); } });
      hostEl.appendChild(regionDatalist('qe-dl-region-monsters', mons));
      if (m.title) state.mapTitles[mapId] = m.title;
    });
  }

  function render(host) {
    var K = k();
    K.clear(host);
    var cols = K.h('div', { cls: 'qe-wizard-cols' });
    var box = K.h('div', { cls: 'qe-wizard' });
    previewEl = K.h('div', { cls: 'qe-preview' });

    box.appendChild(K.h('h4', { text: 'Start from' }));
    box.appendChild(K.h('div', { cls: 'qe-actions' }, TEMPLATES.map(function (name) {
      return K.button(name, function () { applyTemplate(name, host); }, null, 'Replace the steps with a ' + name.toLowerCase() + ' shape');
    })));

    if (state.after) {
      var after = K.h('p', { cls: 'qe-hint-line', text: 'Unlocks after #' + state.after.index + ' ' + state.after.name + ' ' });
      after.appendChild(K.button('×', function () { state.after = null; render(host); }, null, 'Clear the follow-up'));
      box.appendChild(after);
    }

    box.appendChild(K.h('h4', { text: 'Region' }));
    box.appendChild(K.h('div', { cls: 'qe-grid' },
      K.field('Region', K.picker('maps', state.region, function (v) { state.region = v; render(host); }), 'Narrows the NPC / monster suggestions'),
      K.checkbox(state.regionAll, function (v) { state.regionAll = v; render(host); }, 'Show all maps\' NPCs and monsters')));

    box.appendChild(K.h('h4', { text: 'Basics' }));
    var nameField = K.field('Name', K.input(state.name, function (v) { state.name = v; renderPreview(); }, { placeholder: 'e.g. Wolves of Bichon' }));
    nameField.setAttribute('data-key', 'name');
    box.appendChild(K.h('div', { cls: 'qe-grid' },
      nameField,
      K.field('Category', K.select(state.category, K.enumChoices('category'), function (v) { state.category = v; })),
      K.field('Level hint', K.h('span', { cls: 'qe-inline' }, K.numberInput(state.levelHint, function (v) { state.levelHint = v; state.levelTouched = true; }, { min: 0 }))),
      K.field('Repeat', K.select(state.repeat, K.enumChoices('repeatPolicy'), function (v) { state.repeat = v; render(host); })),
      K.enumLabel('repeatPolicy', state.repeat) === 'Cooldown' ? K.field('Cooldown', K.durationInput(state.cooldown, function (v) { state.cooldown = v; })) : null));
    var sugLine = K.h('span', { cls: 'qe-hint-line' });
    box.appendChild(sugLine);
    if (A()) {
      A().suggest(toSuggestInput()).then(function (r) {
        if (!r || !r.level || !sugLine.parentNode) return;
        sugLine.textContent = 'Suggested level: ' + r.level + ' ';
        sugLine.appendChild(K.button('Use', function () { state.levelHint = r.level; state.levelTouched = true; render(host); }));
      });
    }

    box.appendChild(K.h('h4', { text: 'Who' }));
    var giverField = K.field('Giver NPC', K.picker('npcs', state.giver, function (v) { state.giver = v; render(host); }, regionList('npcs')));
    giverField.setAttribute('data-key', 'giver');
    var finisherField = K.field('Turn in to', K.picker('npcs', state.finisher, function (v) { state.finisher = v; render(host); }, regionList('npcs')), 'Empty = back to the giver');
    finisherField.setAttribute('data-key', 'finisher');
    box.appendChild(K.h('div', { cls: 'qe-grid' }, giverField, finisherField,
      K.checkbox(state.noGiver, function (v) { state.noGiver = v; render(host); }, 'No giver (started by a script or a GM)')));
    if (A() && state.giver && state.finisher) {
      var diffLine = K.h('span', { cls: 'qe-hint-line' });
      box.appendChild(diffLine);
      Promise.all([A().hints('npc', state.giver), A().hints('npc', state.finisher)]).then(function (rs) {
        var g = rs[0], f = rs[1];
        if (g && f && g.map !== f.map) {
          diffLine.textContent = 'The turn-in NPC is on ' + (f.mapTitle || ('map #' + f.map)) + ', the giver on ' + (g.mapTitle || ('map #' + g.map)) + '.';
          diffLine.classList.add('qe-warn');
        }
      });
    }

    box.appendChild(K.h('h4', { text: 'Steps' }));
    state.steps.forEach(function (s, i) { box.appendChild(stepCard(host, s, i)); });
    box.appendChild(K.h('div', { cls: 'qe-actions' },
      K.button('+ Add step', function () { addStep(); render(host); }),
      K.button('+ Then talk to…', function () { state.steps.push(talkStep()); render(host); })));

    box.appendChild(K.h('h4', { text: 'Rewards' }));
    box.appendChild(K.h('div', { cls: 'qe-grid' },
      K.field('Exp', K.numberInput(state.exp, function (v) { state.exp = v; renderPreview(); }, { min: 0 })),
      K.field('Gold', K.numberInput(state.gold, function (v) { state.gold = v; renderPreview(); }, { min: 0 })),
      K.button('Suggest', function () { suggestRewards(host); }, null, 'Suggest from the steps')));
    box.appendChild(K.h('p', { cls: 'qe-hint-line', text: 'Server exp / gold rates are applied on top.' }));
    var pickOne = K.checkbox(state.pickOne, function (v) { state.pickOne = v; render(host); }, 'The player picks one of these');
    var pickOneInput = pickOne.querySelector('input');
    if (pickOneInput) pickOneInput.disabled = state.items.length < 2;
    box.appendChild(pickOne);
    state.items.forEach(function (it, i) {
      var row = K.h('div', { cls: 'qe-trow', 'data-key': 'item' + i },
        K.field('Item', K.picker('items', it.id, function (v) { it.id = v; render(host); })),
        K.field('Count', K.numberInput(it.count, function (v) { it.count = v; }, { min: 1 })),
        K.button('×', function () { state.items.splice(i, 1); render(host); }, 'qe-danger', 'Remove this item reward'));
      if (it.id && A()) {
        A().hints('item', it.id).then(function (h) {
          if (h && h.requiredClass && h.requiredClass !== 'None' && row.isConnected && !row.querySelector('.qe-tag')) row.appendChild(K.h('span', { cls: 'qe-tag', text: h.requiredClass }));
        });
      }
      box.appendChild(row);
    });
    box.appendChild(K.button('+ Item reward', function () { state.items.push({ id: 0, count: 1 }); render(host); }));

    box.appendChild(K.h('div', { cls: 'qe-inline' }, K.h('h4', { text: 'Texts' }), K.button('Write the texts for me', async function () { await writeTexts(host); }, null, 'Fill the summary and turn-in text from the steps')));
    box.appendChild(K.field('Summary', textArea(state.summary, function (v) { state.summary = v; renderPreview(); }, 3)));
    box.appendChild(K.field('Turn-in text', textArea(state.complete, function (v) { state.complete = v; renderPreview(); }, 3)));

    var dlHost = K.h('div', { cls: 'hidden' });
    box.appendChild(dlHost);
    if (state.region) buildRegionLists(dlHost, state.region);

    if (state.errors.length) {
      box.appendChild(K.h('ul', { cls: 'qe-problems' }, state.errors.map(function (e) { return K.h('li', { cls: 'qe-bad', text: e.message }); })));
    }

    var actions = K.h('div', { cls: 'qe-wizard-actions' });
    actions.appendChild(K.button('Create draft', function () { createDraft(host); }, 'primary'));
    var rows = state.players || [];
    if (!state.testChar && rows.length) state.testChar = rows[0].characterIndex;
    var picked = rows.filter(function (p) { return p.characterIndex === state.testChar; })[0];
    if (!state.teleportTouched) state.teleportToGiver = !!(picked && picked.isGM);
    var chars = rows.length ? rows.map(function (p) { return { value: p.characterIndex, label: p.name + ' · Lv ' + p.level + (p.isGM ? ' [GM]' : '') }; }) : [{ value: 0, label: '(no one online)' }];
    var charSelect = K.select(state.testChar, chars, function (v) { state.testChar = v; render(host); });
    if (!rows.length) charSelect.disabled = true;
    actions.appendChild(K.field('Test on', charSelect));
    actions.appendChild(K.button('Refresh', function () {
      if (!A()) return;
      var st = state;
      A().players().then(function (list) {
        if (state !== st) return;
        st.players = list;
        if (!list.some(function (p) { return p.characterIndex === st.testChar; })) st.testChar = list.length ? list[0].characterIndex : 0;
        render(host);
      });
    }, null, 'Reload the online characters'));
    var startBtn = K.button('Create, save & start', function () { createAndTest(host); });
    if (!rows.length) startBtn.disabled = true;
    actions.appendChild(startBtn);
    actions.appendChild(K.checkbox(state.teleportToGiver, function (v) { state.teleportToGiver = v; state.teleportTouched = true; }, 'and teleport them to the giver'));
    actions.appendChild(K.button('Blank quest (advanced)', function () { global.QuestsPage.openDraft(global.QuestsPage.blankQuest()); }));
    actions.appendChild(K.button('Cancel', function () {
      state = null;
      previewEl = null;
      K.clear(host);
      host.appendChild(K.h('p', { cls: 'mir-muted', text: 'Pick a quest on the left, or start a new one.' }));
    }));
    box.appendChild(actions);

    cols.appendChild(box);
    cols.appendChild(previewEl);
    host.appendChild(cols);
    renderPreview();

    if (state.tried) {
      checkState().forEach(function (e) {
        var target = box.querySelector('[data-key="' + e.key + '"]');
        if (target) target.classList.add('qe-invalid');
      });
    }
  }

  function suggestRewards(host) {
    if (!A()) return;
    var st = state;
    A().suggest(toSuggestInput()).then(function (r) {
      if (!r || state !== st) return;
      if (r.exp) state.exp = r.exp;
      if (r.gold) state.gold = r.gold;
      render(host);
    });
  }

  async function writeTexts(host) {
    if (!A()) return;
    var last = state.steps[state.steps.length - 1];
    var turnInNpc = state.finisher || state.giver;
    var sentences = A().summarySentences(textSteps(), {
      giverName: npcName(state.giver),
      finisherName: npcName(state.finisher || state.giver),
      lastTalkToTurnIn: !!last && last.type === 'Talk' && last.targetId === turnInNpc,
    });
    var summary = sentences.join(' ');
    var turnIn = (state.exp > 0 || state.gold > 0) ? 'Well done. Take this for your trouble.' : 'Well done. Here is your reward.';
    var has = String(state.summary || '').trim() || String(state.complete || '').trim();
    if (has && !await global.portalConfirm('Overwrite the summary and turn-in text?', { title: 'Write the texts', confirmLabel: 'Overwrite', danger: true })) return;
    state.summary = summary;
    state.complete = turnIn;
    render(host);
  }

  function fresh(prefill) {
    var K = k();
    var p = prefill || {};
    var st = {
      name: '', summary: '', complete: '',
      category: p.category != null ? p.category : K.enumValue('category', 'Side'),
      levelHint: n(p.levelHint), levelTouched: !!p.levelHint,
      repeat: p.repeat != null ? p.repeat : K.enumValue('repeatPolicy', 'Once'),
      cooldown: 0,
      giver: n(p.giver), finisher: 0, noGiver: false,
      region: 0, regionAll: false,
      steps: [blankStep()],
      exp: 0, gold: 0, items: [], pickOne: false,
      after: p.after || null,
      storylineIndex: n(p.storylineIndex), storylineOrder: n(p.storylineOrder),
      errors: [], tried: false, players: null, testChar: 0, teleportToGiver: false, teleportTouched: false,
      mapTitles: {}, mapRequests: {},
    };
    if (p.fromGraph) return stateFromGraph(p.fromGraph, st);
    return st;
  }

  async function open(host, prefill) {
    var st = fresh(prefill);
    if (!st) {
      var K = k();
      K.clear(host);
      host.appendChild(K.h('p', { cls: 'mir-muted', text: 'Pick a quest on the left, or start a new one.' }));
      return;
    }
    state = st;
    if (A()) {
      try { st.players = await A().players(); } catch (e) { st.players = []; }
      if (state !== st) return;
      if (!st.testChar && st.players.length) st.testChar = st.players[0].characterIndex;
    }
    render(host);
  }

  global.QuestsWizard = { open: open, canOpen: function (prefill) { return !!fresh(prefill); } };
})(window);
