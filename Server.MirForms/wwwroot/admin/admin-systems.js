/**
 * Systems section (#section-systems) — MirCraft Admin feature B7.
 * Mirrors SystemInfoForm (Fishing, Mail, Goods, Refining, Relationship, Mentor, Gems, Heroes, Spawn tick), GuildInfoForm (guild
 * settings) and MiningInfoForm (mine sets). Backend: /api/admin/systems/* (SystemsRoutes.cs). Uses window.BsUI from
 * admin-balance.js (resolved lazily at load time).
 */
(function (global) {
  'use strict';

  var API = '/api/admin/systems';
  var UI = null;     // window.BsUI
  var esc = null;

  var TABS = [
    { id: 'fishing', label: 'Fishing', kind: 'group' },
    { id: 'mail', label: 'Mail', kind: 'group' },
    { id: 'goods', label: 'Goods', kind: 'group' },
    { id: 'refine', label: 'Refining', kind: 'group' },
    { id: 'marriage', label: 'Relationship', kind: 'group' },
    { id: 'mentor', label: 'Mentor', kind: 'group' },
    { id: 'gem', label: 'Gems', kind: 'group' },
    { id: 'heroes', label: 'Heroes', kind: 'group' },
    { id: 'guilds', label: 'Guilds', kind: 'guilds' },
    { id: 'mining', label: 'Mining', kind: 'mining' },
    { id: 'spawntick', label: 'Spawn tick', kind: 'spawntick' }
  ];

  var strip = null;
  var panes = {};
  var editors = {};
  var activeTab = 'fishing';
  var stopPoll = null;

  // ---- small model helpers --------------------------------------------------------------------------------------------

  function clone(o) { return JSON.parse(JSON.stringify(o)); }

  function setPath(obj, path, value) {
    var parts = String(path).split('.');
    var cur = obj;
    for (var i = 0; i < parts.length - 1; i++) {
      var k = /^\d+$/.test(parts[i]) ? Number(parts[i]) : parts[i];
      if (cur[k] == null) return;
      cur = cur[k];
    }
    var last = parts[parts.length - 1];
    cur[/^\d+$/.test(last) ? Number(last) : last] = value;
  }

  /** The value of a bound input: checkbox → bool, number → Number (raw text when not numeric), text → trimmed string. */
  function inputValue(el) {
    if (el.type === 'checkbox') return !!el.checked;
    if (el.type === 'number') {
      var raw = String(el.value || '').trim();
      var n = Number(raw);
      var bad = raw === '' || !isFinite(n) || (el.step !== 'any' && Math.floor(n) !== n) ||
        (el.min !== '' && n < Number(el.min)) || (el.max !== '' && n > Number(el.max));
      el.classList.toggle('cfg-rejected', bad);
      return raw === '' || !isFinite(n) ? raw : n;
    }
    return String(el.value || '');
  }

  function numInput(path, value, min, max, step, extra) {
    return '<input type="number" class="fd-input bs-input-num" data-path="' + esc(path) + '" value="' + esc(value) + '"' +
      (min != null ? ' min="' + esc(min) + '"' : '') + (max != null ? ' max="' + esc(max) + '"' : '') +
      ' step="' + esc(step || 1) + '"' + (extra || '') + '>';
  }

  function textInput(path, value, list, maxLength, placeholder) {
    return '<input type="text" class="fd-input bs-input-wide" data-path="' + esc(path) + '" value="' + esc(value) + '"' +
      (list ? ' list="' + esc(list) + '"' : '') + ' maxlength="' + esc(maxLength || 50) + '"' +
      (placeholder ? ' placeholder="' + esc(placeholder) + '"' : '') + ' autocomplete="off">';
  }

  function checkInput(path, value, label) {
    return '<label class="bs-switch"><input type="checkbox" data-path="' + esc(path) + '"' + (value ? ' checked' : '') + '> <span>' + esc(label) + '</span></label>';
  }

  function field(label, input, help) {
    return '<label class="bs-field"><span class="bs-field-cap">' + esc(label) + '</span>' + input + (help ? '<span class="mir-muted bs-help">' + esc(help) + '</span>' : '') + '</label>';
  }

  /** Common plumbing for the hand-built editors: toolbar, dirty state, issue box, reload/save wiring. */
  function scaffold(pane, key, title, introHtml, bodyHtml) {
    pane.innerHTML = '<div class="mir-card bs-card">' + UI.toolbarHtml(key, '<span class="db-pane-title">' + esc(title) + '</span>') +
      (introHtml ? '<div class="mir-muted bs-intro">' + introHtml + '</div>' : '') +
      '<div class="bs-issues hidden" data-bs="' + key + '-issues"></div>' + bodyHtml + '</div>';

    var ctx = {
      key: key,
      issues: UI.q(pane, key + '-issues'),
      saveBtn: UI.q(pane, key + '-save'),
      pill: UI.q(pane, key + '-dirty'),
      reloadBtn: UI.q(pane, key + '-reload'),
      dirty: false,
      markDirty: function (on) {
        ctx.dirty = !!on;
        if (editors[key]) editors[key].dirty = ctx.dirty;
        ctx.saveBtn.disabled = !on;
        ctx.pill.classList.toggle('hidden', !on);
        UI.setDirty('systems-' + key, on);
        if (strip) strip.markDirty(key, on);
      }
    };
    return ctx;
  }

  // ---- generic scalar groups ------------------------------------------------------------------------------------------

  function GroupEditor(pane, key, label) {
    var ctx = scaffold(pane, key, label, '<span data-bs="' + key + '-intro"></span>', '<div data-bs="' + key + '-form"><p class="mir-muted">Loading…</p></div>');
    var form = null;
    var rev = null;
    var loadSeq = 0;

    async function load() {
      var seq = ++loadSeq;
      UI.clearIssues(ctx.issues);
      var host = UI.q(pane, key + '-form');
      try {
        var j = await UI.getJson(API + '/' + encodeURIComponent(key));
        if (seq !== loadSeq) return;
        rev = j.rev;
        UI.q(pane, key + '-intro').textContent = (j.note ? j.note + ' ' : '') + 'Changes apply immediately and are saved to Configs\\' + j.file + '.';
        if (!form) form = UI.fieldForm(host, j.fields || [], function () { ctx.markDirty(form.isDirty()); });
        else form.reset(j.fields || []);
        ctx.markDirty(false);
      } catch (e) {
        if (seq === loadSeq) host.innerHTML = '<p class="cfg-error">' + esc(e.message) + '</p>';
      }
    }

    ctx.reloadBtn.addEventListener('click', async function () {
      if (ctx.dirty && !(await UI.confirmDiscard(label.toLowerCase() + ' edits'))) return;
      load();
    });

    ctx.saveBtn.addEventListener('click', function () {
      if (!form) return;
      var changes = form.changes();
      if (changes === null) { UI.toast('Fix the highlighted fields first.', 'error'); return; }
      if (!Object.keys(changes).length) { ctx.markDirty(false); return; }
      UI.saving(ctx.saveBtn, async function () {
        var r = await UI.request('PUT', API + '/' + encodeURIComponent(key), { values: changes, rev: rev, reason: UI.note(pane, key) });
        if (await UI.handleResult(r, ctx.issues, load)) {
          UI.clearNote(pane, key);
          await load();
        }
      }, function () { return ctx.dirty; });
    });

    return { dirty: false, load: load };
  }

  // ---- guilds -----------------------------------------------------------------------------------------------------------

  function GuildsEditor(pane) {
    var ctx = scaffold(pane, 'guilds', 'Guild settings', '<span data-bs="guilds-intro">Saved to Configs\\GuildSettings.ini; changes apply immediately.</span><ul class="bs-notes" data-bs="guilds-notes"></ul>',
      '<div data-bs="guilds-body"><p class="mir-muted">Loading…</p></div>');
    var model = null;
    var rev = null;
    var buffStats = [];
    var selectedBuff = -1;
    var loadSeq = 0;
    var body = UI.q(pane, 'guilds-body');
    var items = UI.datalist('items');

    function render() {
      body.innerHTML =
        '<div class="bs-sections">' +
        '<section class="ops-group"><h3>Creation</h3><div class="bs-fields">' +
        field('Minimum owner level', numInput('requiredLevel', model.requiredLevel, 0, 255)) +
        '</div><table class="user-table bs-grid bs-grid-narrow"><thead><tr><th>Cost item (blank = gold)</th><th>Amount</th><th></th></tr></thead><tbody data-bs="guild-costs">' +
        (model.creationCosts.map(function (c, i) {
          return '<tr><td>' + textInput('creationCosts.' + i + '.itemName', c.itemName, items, 50, 'Gold') + '</td>' +
            '<td>' + numInput('creationCosts.' + i + '.amount', c.amount, 1, 4294967295) + '</td>' +
            '<td><button type="button" class="mir-btn ops-row-btn" data-act="cost-del" data-i="' + i + '" title="Remove">✕</button></td></tr>';
        }).join('') || '<tr><td colspan="3" class="mir-muted">Creating a guild is free.</td></tr>') +
        '</tbody></table><button type="button" class="mir-btn bs-add" data-act="cost-add">+ Add cost</button></section>' +

        '<section class="ops-group"><h3>Newbie guild</h3><div class="bs-fields">' +
        field('Newbie guild EXP buff', checkInput('newbieGuildBuffEnabled', model.newbieGuildBuffEnabled, 'Enabled')) +
        field('EXP bonus (%)', numInput('newbieGuildExpBuff', model.newbieGuildExpBuff, 0, 10000)) +
        '</div></section>' +

        '<section class="ops-group"><h3>Levels</h3><div class="bs-fields">' +
        field('Points per level', numInput('pointPerLevel', model.pointPerLevel, 0, 255)) +
        field('Guild EXP rate', numInput('expRate', model.expRate, 0, 1000, 'any'), 'Share of member EXP that goes to the guild (0.01 = 1%).') +
        '</div><div class="ops-table-scroll bs-scroll"><table class="user-table bs-grid bs-grid-narrow"><thead><tr><th>Level</th><th>Experience to next</th><th>Member cap (0 = unlimited)</th></tr></thead><tbody>' +
        (model.levels.map(function (l, i) {
          return '<tr><td class="mono">' + i + '</td><td>' + numInput('levels.' + i + '.experience', l.experience, 0, null) + '</td>' +
            '<td>' + numInput('levels.' + i + '.memberCap', l.memberCap, 0, 2147483647) + '</td></tr>';
        }).join('') || '<tr><td colspan="3" class="mir-muted">No levels defined.</td></tr>') +
        '</tbody></table></div><div class="bs-row-actions"><button type="button" class="mir-btn bs-add" data-act="level-add">+ Add level</button>' +
        '<button type="button" class="mir-btn bs-add" data-act="level-del"' + (model.levels.length ? '' : ' disabled') + '>− Remove last level</button></div></section>' +

        '<section class="ops-group"><h3>Wars</h3><div class="bs-fields">' +
        field('War length (minutes)', numInput('warTime', model.warTime, 1, 2147483647)) +
        field('War cost (gold)', numInput('warCost', model.warCost, 0, 4294967295)) +
        '</div></section>' +

        '<section class="ops-group"><h3>Guild territory <span class="ops-pill ops-pill-warn" title="Crystal never writes these to an INI file">live only</span></h3><div class="bs-fields">' +
        field('Buy price (gold)', numInput('buyGTGold', model.buyGTGold, 0, 2147483647)) +
        field('Extend price (gold)', numInput('extendGT', model.extendGT, 0, 2147483647)) +
        field('Rental days', numInput('gtDays', model.gtDays, 1, 3650)) +
        '</div></section>' +

        '<section class="ops-group"><h3>Buffs</h3><div class="bs-split">' +
        '<div class="bs-split-main"><div class="ops-table-scroll bs-scroll"><table class="user-table bs-grid"><thead><tr><th>Id</th><th>Name</th><th>Level</th><th>Points</th><th>Time (min)</th><th>Cost</th></tr></thead><tbody data-bs="guild-buffs">' +
        (model.buffs.map(function (b, i) {
          return '<tr class="bs-click' + (i === selectedBuff ? ' bs-selected' : '') + '" data-buff="' + i + '"><td class="mono">' + esc(b.id == null ? 'new' : b.id) + '</td><td>' + esc(b.name) + '</td>' +
            '<td>' + esc(b.levelRequirement) + '</td><td>' + esc(b.pointsRequirement) + '</td><td>' + esc(b.timeLimit) + '</td><td>' + esc(b.activationCost) + '</td></tr>';
        }).join('') || '<tr><td colspan="6" class="mir-muted">No guild buffs.</td></tr>') +
        '</tbody></table></div><div class="bs-row-actions"><button type="button" class="mir-btn bs-add" data-act="buff-add">+ Add buff</button>' +
        '<button type="button" class="mir-btn danger bs-add" data-act="buff-del"' + (selectedBuff >= 0 ? '' : ' disabled') + '>Remove selected buff</button></div></div>' +
        '<aside class="bs-split-side">' + buffForm() + '</aside></div></section>' +
        '</div>';
    }

    function buffForm() {
      var b = model.buffs[selectedBuff];
      if (!b) return '<p class="mir-muted">Select a buff to edit it.</p>';
      var p = 'buffs.' + selectedBuff + '.';
      return '<h3 class="bs-h">Buff ' + esc(b.id != null ? '#' + b.id : '(new)') + '</h3><div class="bs-fields bs-fields-compact">' +
        field('Name (1-20)', textInput(p + 'name', b.name, null, 20)) +
        field('Icon', numInput(p + 'icon', b.icon, 0, 2147483647)) +
        field('Guild level', numInput(p + 'levelRequirement', b.levelRequirement, 0, 255)) +
        field('Points', numInput(p + 'pointsRequirement', b.pointsRequirement, 0, 255)) +
        field('Time limit (min)', numInput(p + 'timeLimit', b.timeLimit, 0, 2147483647)) +
        field('Activation cost', numInput(p + 'activationCost', b.activationCost, 0, 2147483647)) +
        '</div><h4 class="bs-h4">Stats</h4><div class="bs-fields bs-fields-compact">' +
        buffStats.map(function (s) {
          return field(s.label, numInput(p + 'stats.' + s.stat, b.stats[s.stat] || 0, 0, s.max));
        }).join('') + '</div>';
    }

    async function load() {
      var seq = ++loadSeq;
      UI.clearIssues(ctx.issues);
      try {
        var j = await UI.getJson(API + '/guilds');
        if (seq !== loadSeq) return;
        model = clone(j.settings);
        rev = model.rev;
        delete model.rev;
        buffStats = j.buffStats || [];
        if (selectedBuff >= model.buffs.length) selectedBuff = model.buffs.length - 1;
        UI.q(pane, 'guilds-notes').innerHTML = (j.notes || []).map(function (n) { return '<li>' + esc(n) + '</li>'; }).join('');
        render();
        ctx.markDirty(false);
      } catch (e) {
        if (seq === loadSeq) body.innerHTML = '<p class="cfg-error">' + esc(e.message) + '</p>';
      }
    }

    body.addEventListener('input', function (e) {
      var path = e.target.getAttribute && e.target.getAttribute('data-path');
      if (!path || !model) return;
      setPath(model, path, inputValue(e.target));
      ctx.markDirty(true);
      // keep the buff list row in sync with the detail form
      var m = /^buffs\.(\d+)\.(name|levelRequirement|pointsRequirement|timeLimit|activationCost)$/.exec(path);
      if (m) {
        var row = body.querySelector('[data-buff="' + m[1] + '"]');
        var b = model.buffs[Number(m[1])];
        if (row && b) {
          var cells = row.querySelectorAll('td');
          cells[1].textContent = b.name; cells[2].textContent = b.levelRequirement; cells[3].textContent = b.pointsRequirement;
          cells[4].textContent = b.timeLimit; cells[5].textContent = b.activationCost;
        }
      }
    });

    body.addEventListener('click', async function (e) {
      if (!model) return;
      var t = e.target.closest ? e.target.closest('[data-act],[data-buff]') : null;
      if (!t) return;
      var act = t.getAttribute('data-act');
      if (!act && t.hasAttribute('data-buff')) {
        selectedBuff = Number(t.getAttribute('data-buff'));
        render();
        return;
      }
      if (act === 'cost-add') { model.creationCosts.push({ itemName: '', amount: 1 }); }
      else if (act === 'cost-del') { model.creationCosts.splice(Number(t.getAttribute('data-i')), 1); }
      else if (act === 'level-add') {
        var last = model.levels[model.levels.length - 1];
        model.levels.push({ level: model.levels.length, experience: last ? last.experience : 0, memberCap: last ? last.memberCap : 0 });
      }
      else if (act === 'level-del') {
        if (!model.levels.length) return;
        if (!(await global.portalConfirm('Remove guild level ' + (model.levels.length - 1) + '? (Applies when you save.)', { title: 'Remove level', confirmLabel: 'Remove' }))) return;
        model.levels.pop();
      }
      else if (act === 'buff-add') {
        var stats = {};
        buffStats.forEach(function (s) { stats[s.stat] = 0; });
        model.buffs.push({ id: null, name: 'New buff', icon: 0, levelRequirement: 0, pointsRequirement: 1, timeLimit: 0, activationCost: 0, stats: stats });
        selectedBuff = model.buffs.length - 1;
      }
      else if (act === 'buff-del') {
        var b = model.buffs[selectedBuff];
        if (!b) return;
        if (!(await global.portalConfirm('Remove guild buff "' + b.name + '"' + (b.id != null ? ' (#' + b.id + ')' : '') + '? Guilds that have it active lose it. (Applies when you save.)', { title: 'Remove guild buff', confirmLabel: 'Remove' }))) return;
        model.buffs.splice(selectedBuff, 1);
        selectedBuff = Math.min(selectedBuff, model.buffs.length - 1);
      }
      else return;
      render();
      ctx.markDirty(true);
    });

    ctx.reloadBtn.addEventListener('click', async function () {
      if (ctx.dirty && !(await UI.confirmDiscard('guild setting edits'))) return;
      load();
    });

    ctx.saveBtn.addEventListener('click', function () {
      if (!model) return;
      UI.saving(ctx.saveBtn, async function () {
        var payload = clone(model);
        payload.rev = rev;
        payload.reason = UI.note(pane, 'guilds');
        var r = await UI.request('PUT', API + '/guilds', payload);
        if (await UI.handleResult(r, ctx.issues, load)) {
          UI.clearNote(pane, 'guilds');
          await load();
        }
      }, function () { return ctx.dirty; });
    });

    return { dirty: false, load: load };
  }

  // ---- mining -----------------------------------------------------------------------------------------------------------

  var DROP_COLS = [
    { f: 'minSlot', label: 'Min slot', max: 254 },
    { f: 'maxSlot', label: 'Max slot', max: 255 },
    { f: 'minDura', label: 'Min quality', max: 255 },
    { f: 'maxDura', label: 'Max quality', max: 255 },
    { f: 'bonusChance', label: 'Bonus chance', max: 255 },
    { f: 'maxBonusDura', label: 'Max bonus quality', max: 255 }
  ];

  function MiningEditor(pane) {
    var ctx = scaffold(pane, 'mining', 'Mine sets',
      'Saved to Configs\\Mines.ini.<ul class="bs-notes" data-bs="mining-notes"></ul>',
      '<div class="bs-split"><aside class="bs-list" data-bs="mining-list"></aside><div class="bs-split-main" data-bs="mining-editor"><p class="mir-muted">Loading…</p></div></div>');
    var sets = [];
    var originalCount = 0;
    var rev = null;
    var current = 0;
    var loadSeq = 0;
    var listEl = UI.q(pane, 'mining-list');
    var editorEl = UI.q(pane, 'mining-editor');
    var items = UI.datalist('items');

    function renderList() {
      listEl.innerHTML = sets.map(function (s, i) {
        var used = (s.usedBy || []).length;
        return '<button type="button" class="bs-list-item' + (i === current ? ' active' : '') + '" data-set="' + i + '">' +
          '<span><span class="mono">' + (i + 1) + '.</span> ' + esc(s.name || '(unnamed)') + '</span>' +
          '<span class="mir-muted">' + (i >= originalCount ? 'new' : used ? used + ' map' + (used === 1 ? '' : 's') : 'unused') + '</span></button>';
      }).join('') +
        '<div class="bs-list-actions"><button type="button" class="mir-btn" data-act="set-add">+ Add set</button>' +
        '<button type="button" class="mir-btn danger" data-act="set-del"' + (sets.length && !(sets[sets.length - 1].usedBy || []).length ? '' : ' disabled title="The last set is used by a map"') + '>Remove last set</button></div>';
    }

    function renderEditor() {
      var s = sets[current];
      if (!s) { editorEl.innerHTML = '<p class="mir-muted">No mine sets. Add one to start.</p>'; return; }
      var p = 'sets.' + current + '.';
      editorEl.innerHTML = '<h3 class="bs-h">Mine ' + (current + 1) + '</h3>' +
        ((s.usedBy || []).length ? '<p class="mir-muted">Used by: ' + esc(s.usedBy.join(', ')) + '</p>' : '') +
        '<div class="bs-fields">' +
        field('Name', textInput(p + 'name', s.name, null, 50)) +
        field('Regen delay (minutes)', numInput(p + 'spotRegenRate', s.spotRegenRate, 0, 254)) +
        field('Attempts per spot', numInput(p + 'maxStones', s.maxStones, 0, 255)) +
        field('Hit rate', numInput(p + 'hitRate', s.hitRate, 0, 255)) +
        field('Drop rate', numInput(p + 'dropRate', s.dropRate, 0, 255)) +
        field('Total slots', numInput(p + 'totalSlots', s.totalSlots, 0, 255), 'Each dig rolls a slot 0…total-1; a drop wins when the slot is in its range.') +
        '</div><h4 class="bs-h4">Drops</h4><div class="ops-table-scroll"><table class="user-table bs-grid"><thead><tr><th>Item</th>' +
        DROP_COLS.map(function (c) { return '<th>' + esc(c.label) + '</th>'; }).join('') + '<th></th></tr></thead><tbody>' +
        (s.drops.map(function (d, j) {
          return '<tr><td>' + textInput(p + 'drops.' + j + '.itemName', d.itemName, items, 50) + '</td>' +
            DROP_COLS.map(function (c) { return '<td>' + numInput(p + 'drops.' + j + '.' + c.f, d[c.f], 0, c.max) + '</td>'; }).join('') +
            '<td><button type="button" class="mir-btn ops-row-btn" data-act="drop-del" data-i="' + j + '" title="Remove">✕</button></td></tr>';
        }).join('') || '<tr><td colspan="8" class="mir-muted">No drops.</td></tr>') +
        '</tbody></table></div><button type="button" class="mir-btn bs-add" data-act="drop-add">+ Add drop</button>';
    }

    function render() { renderList(); renderEditor(); }

    async function load() {
      var seq = ++loadSeq;
      UI.clearIssues(ctx.issues);
      try {
        var j = await UI.getJson(API + '/mining');
        if (seq !== loadSeq) return;
        sets = clone(j.sets || []);
        originalCount = sets.length;
        rev = j.rev;
        if (current >= sets.length) current = Math.max(0, sets.length - 1);
        UI.q(pane, 'mining-notes').innerHTML = (j.notes || []).map(function (n) { return '<li>' + esc(n) + '</li>'; }).join('');
        render();
        ctx.markDirty(false);
      } catch (e) {
        if (seq === loadSeq) editorEl.innerHTML = '<p class="cfg-error">' + esc(e.message) + '</p>';
      }
    }

    editorEl.addEventListener('input', function (e) {
      var path = e.target.getAttribute && e.target.getAttribute('data-path');
      if (!path) return;
      setPath({ sets: sets }, path, inputValue(e.target));
      ctx.markDirty(true);
      if (/\.name$/.test(path) && !/drops/.test(path)) renderList();
    });

    pane.addEventListener('click', async function (e) {
      var t = e.target.closest ? e.target.closest('[data-act],[data-set]') : null;
      if (!t || !pane.contains(t)) return;
      var act = t.getAttribute('data-act');
      if (!act && t.hasAttribute('data-set')) { current = Number(t.getAttribute('data-set')); render(); return; }
      var s = sets[current];
      if (act === 'set-add') {
        sets.push({ index: sets.length + 1, name: 'Mine ' + (sets.length + 1), spotRegenRate: 5, maxStones: 80, hitRate: 25, dropRate: 10, totalSlots: 100, drops: [], usedBy: [] });
        current = sets.length - 1;
      } else if (act === 'set-del') {
        var last = sets[sets.length - 1];
        if (!last) return;
        if ((last.usedBy || []).length) { UI.toast('Mine ' + sets.length + ' is used by ' + last.usedBy.join(', ') + '.', 'error'); return; }
        if (!(await global.portalConfirm('Remove mine set ' + sets.length + ' (' + (last.name || 'unnamed') + ')? (Applies when you save.)', { title: 'Remove mine set', confirmLabel: 'Remove' }))) return;
        sets.pop();
        current = Math.min(current, sets.length - 1);
      } else if (act === 'drop-add' && s) {
        var prev = s.drops[s.drops.length - 1];
        var start = prev ? Math.min(254, Number(prev.maxSlot) + 1 || 0) : 1;
        s.drops.push({ itemName: '', minSlot: start, maxSlot: start, minDura: 1, maxDura: 1, bonusChance: 0, maxBonusDura: 1 });
      } else if (act === 'drop-del' && s) {
        s.drops.splice(Number(t.getAttribute('data-i')), 1);
      } else return;
      render();
      ctx.markDirty(true);
    });

    ctx.reloadBtn.addEventListener('click', async function () {
      if (ctx.dirty && !(await UI.confirmDiscard('mine set edits'))) return;
      load();
    });

    ctx.saveBtn.addEventListener('click', function () {
      UI.saving(ctx.saveBtn, async function () {
        var payload = {
          sets: sets.map(function (s) {
            return {
              name: s.name, spotRegenRate: s.spotRegenRate, maxStones: s.maxStones, hitRate: s.hitRate, dropRate: s.dropRate, totalSlots: s.totalSlots,
              drops: s.drops.map(function (d) {
                return { itemName: d.itemName, minSlot: d.minSlot, maxSlot: d.maxSlot, minDura: d.minDura, maxDura: d.maxDura, bonusChance: d.bonusChance, maxBonusDura: d.maxBonusDura };
              })
            };
          }),
          rev: rev,
          reason: UI.note(pane, 'mining')
        };
        var r = await UI.request('PUT', API + '/mining', payload);
        if (await UI.handleResult(r, ctx.issues, load)) {
          UI.clearNote(pane, 'mining');
          await load();
        }
      }, function () { return ctx.dirty; });
    });

    return { dirty: false, load: load };
  }

  // ---- spawn tick -------------------------------------------------------------------------------------------------------

  function SpawnTickEditor(pane) {
    var ctx = scaffold(pane, 'spawntick', 'Respawn tick',
      'Monsters respawn on a global tick every <b>base rate</b> minutes; with more players online the delay is multiplied by the smallest ' +
      '<b>delay multiplier</b> of the options whose user count has been reached.<ul class="bs-notes" data-bs="spawntick-notes"></ul>',
      '<div data-bs="spawntick-body"><p class="mir-muted">Loading…</p></div>');
    var model = null;
    var rev = null;
    var last = null;   // the last GET body (editLoaded, systemDataStale, live, world)
    var loadSeq = 0;
    var body = UI.q(pane, 'spawntick-body');

    function liveHtml(live, world) {
      if (!live) return '<p class="mir-muted">World ' + esc(world || 'stopped') + ': no live respawn tick.</p>';
      return '<dl class="kv-list">' +
        '<dt>Base rate</dt><dd>' + esc(live.baseSpawnRate) + ' min</dd>' +
        '<dt>Options</dt><dd>' + esc((live.options || []).map(function (o) { return o.userCount + '+ users → ×' + o.delayLoss; }).join(', ') || 'none') + '</dd>' +
        '<dt>Players online</dt><dd>' + esc(live.players) + '</dd>' +
        '<dt>Multiplier now</dt><dd>×' + esc(live.multiplier) + '</dd>' +
        '<dt>Current delay</dt><dd>' + esc((Number(live.currentDelayMs) / 60000).toFixed(2)) + ' min</dd>' +
        '<dt>Tick counter</dt><dd class="mono">' + esc(live.tickCounter) + '</dd></dl>';
    }

    function render(j) {
      body.innerHTML = (j.editLoaded === false ? '<p class="cfg-error">The editor database is not loaded; the respawn tick cannot be saved until the server has started once.</p>' : '') +
        (j.systemDataStale ? '<p class="mir-muted"><span class="ops-pill ops-pill-warn">pending restart</span> The editor database has been saved since the world loaded it.</p>' : '') +
        '<div class="bs-split"><div class="bs-split-main"><div class="bs-fields">' +
        field('Base rate (minutes)', numInput('baseSpawnRate', model.baseSpawnRate, 1, 255)) +
        '</div><table class="user-table bs-grid bs-grid-narrow"><thead><tr><th>From users online</th><th>Delay multiplier (0-1]</th><th></th></tr></thead><tbody>' +
        (model.options.map(function (o, i) {
          return '<tr><td>' + numInput('options.' + i + '.userCount', o.userCount, 0, 2147483647) + '</td>' +
            '<td>' + numInput('options.' + i + '.delayLoss', o.delayLoss, 0.01, 1, 'any') + '</td>' +
            '<td><button type="button" class="mir-btn ops-row-btn" data-act="opt-del" data-i="' + i + '" title="Remove">✕</button></td></tr>';
        }).join('') || '<tr><td colspan="3" class="mir-muted">No options: the base rate always applies.</td></tr>') +
        '</tbody></table><button type="button" class="mir-btn bs-add" data-act="opt-add">+ Add option</button>' +
        '<div class="bs-fields">' + field('After saving', checkInput('applyLive', model.applyLive, 'Apply to the running world now'), 'Otherwise the new tick is used after the next world restart.') + '</div>' +
        '</div><aside class="bs-split-side"><h3 class="bs-h">Running world</h3><div data-bs="spawntick-live">' + liveHtml(j.live, j.world) + '</div></aside></div>';
    }

    async function load() {
      var seq = ++loadSeq;
      UI.clearIssues(ctx.issues);
      try {
        var j = await UI.getJson(API + '/spawntick');
        if (seq !== loadSeq) return;
        model = { baseSpawnRate: j.baseSpawnRate, options: clone(j.options || []), applyLive: model ? !!model.applyLive : false };
        rev = j.rev;
        last = j;
        UI.q(pane, 'spawntick-notes').innerHTML = (j.notes || []).map(function (n) { return '<li>' + esc(n) + '</li>'; }).join('');
        render(j);
        ctx.markDirty(false);
      } catch (e) {
        if (seq === loadSeq) body.innerHTML = '<p class="cfg-error">' + esc(e.message) + '</p>';
      }
    }

    /** Poll: refresh only the "Running world" panel. */
    async function refreshLive() {
      var box = UI.q(pane, 'spawntick-live');
      if (!box) return;
      var r = await UI.request('GET', API + '/spawntick');
      if (!r.ok) return;
      if (last) { last.live = r.body.live; last.world = r.body.world; }
      box.innerHTML = liveHtml(r.body.live, r.body.world);
    }

    body.addEventListener('input', function (e) {
      var path = e.target.getAttribute && e.target.getAttribute('data-path');
      if (!path || !model) return;
      setPath(model, path, inputValue(e.target));
      if (path !== 'applyLive') ctx.markDirty(true);
    });

    body.addEventListener('click', function (e) {
      var t = e.target.closest ? e.target.closest('[data-act]') : null;
      if (!t || !model) return;
      var act = t.getAttribute('data-act');
      if (act === 'opt-add') {
        var prevOpt = model.options[model.options.length - 1];
        model.options.push({ userCount: prevOpt ? Number(prevOpt.userCount) + 10 : 0, delayLoss: prevOpt ? Math.max(0.1, Math.round((Number(prevOpt.delayLoss) - 0.1) * 100) / 100) : 1 });
      } else if (act === 'opt-del') {
        model.options.splice(Number(t.getAttribute('data-i')), 1);
      } else return;
      render(last || {});
      ctx.markDirty(true);
    });

    ctx.reloadBtn.addEventListener('click', async function () {
      if (ctx.dirty && !(await UI.confirmDiscard('respawn tick edits'))) return;
      load();
    });

    ctx.saveBtn.addEventListener('click', async function () {
      if (!model) return;
      var ok = await global.portalConfirm('Saving the respawn tick writes the whole editor database (Server.MirDB), including any unsaved Game Data edits. ' +
        (model.applyLive ? 'The new tick is also applied to the running world.' : 'The new tick is used after the next world restart.'),
        { title: 'Save respawn tick', confirmLabel: 'Save', danger: false });
      if (!ok) return;
      UI.saving(ctx.saveBtn, async function () {
        var r = await UI.request('PUT', API + '/spawntick', {
          baseSpawnRate: model.baseSpawnRate,
          options: model.options.map(function (o) { return { userCount: o.userCount, delayLoss: o.delayLoss }; }),
          applyLive: !!model.applyLive,
          rev: rev,
          reason: UI.note(pane, 'spawntick')
        });
        if (await UI.handleResult(r, ctx.issues, load)) {
          UI.clearNote(pane, 'spawntick');
          if (global.AdminStatus && typeof global.AdminStatus.refresh === 'function') global.AdminStatus.refresh();
          await load();
        }
      }, function () { return ctx.dirty; });
    });

    return { dirty: false, load: load, refreshLive: refreshLive };
  }

  // ---- section wiring ---------------------------------------------------------------------------------------------------

  function showTab(id) {
    activeTab = id;
    Object.keys(panes).forEach(function (k) { panes[k].classList.toggle('hidden', k !== id); });
    if (strip) strip.select(id);
    var ed = editors[id];
    if (ed && !ed.loaded) { ed.loaded = true; ed.load(); }
  }

  function load(el, params) {
    UI = global.BsUI;
    if (!UI) throw new Error('admin-balance.js (BsUI) is not loaded');
    esc = UI.esc;

    el.innerHTML = '<div class="bs-head"><h2 class="gd-group-title">Systems</h2><div data-bs="tabs"></div></div>' +
      TABS.map(function (t) { return '<div class="bs-pane hidden" data-pane="' + t.id + '"></div>'; }).join('');

    TABS.forEach(function (t) {
      var pane = el.querySelector('[data-pane="' + t.id + '"]');
      panes[t.id] = pane;
      if (t.kind === 'group') editors[t.id] = GroupEditor(pane, t.id, t.label);
      else if (t.kind === 'guilds') editors[t.id] = GuildsEditor(pane);
      else if (t.kind === 'mining') editors[t.id] = MiningEditor(pane);
      else editors[t.id] = SpawnTickEditor(pane);
    });

    strip = UI.tabStrip(el.querySelector('[data-bs="tabs"]'), TABS, function (id) {
      showTab(id);
      if (global.portalSetHashParams) global.portalSetHashParams({ tab: id }, true);
      return true;
    });

    showTab(params && params.tab && panes[params.tab] ? params.tab : 'fishing');

    stopPoll = global.AdminSections.poll('systems', function () {
      if (activeTab !== 'spawntick' || !editors.spawntick.loaded) return null;
      return editors.spawntick.refreshLive();
    }, 10000);
  }

  var shownOnce = false;

  global.AdminSections.register('systems', {
    load: load,
    show: function () {
      if (!shownOnce) { shownOnce = true; return; }
      var ed = editors[activeTab];
      if (ed && ed.loaded && !ed.dirty) ed.load();
    }
  });

  if (typeof global.portalRegisterDeepLink === 'function') {
    global.portalRegisterDeepLink('systems', function (params) {
      if (params && params.tab && panes[params.tab] && params.tab !== activeTab) showTab(params.tab);
    });
  }
})(window);
