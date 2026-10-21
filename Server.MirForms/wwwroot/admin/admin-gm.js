/*
 * GM Console (#section-gm) — live GM actions against a character or the world.
 *
 * Every verb posts POST /api/admin/gm/action with its `action`; the verbs, groups and argument lists come from
 * GET /api/admin/gm/caps so the server stays the single source of truth. Verbs with arguments show an inline form (with
 * pickers for players / maps / items / monsters), then ONE confirmation dialog collects the reason (and any argument still
 * missing). Cancel sends nothing.
 *
 * Other sections reuse the same dialogs through window.GmConsole.runFor(action, {character, characterIndex}, presetArgs).
 * Deep link: #section=gm&name=<character> (also accepts player= from the player chip, characterIndex=).
 */
(function (global) {
  'use strict';

  var esc = function (s) { return OpsUI.esc(s); };

  var caps = null;
  var capsPromise = null;
  var root = null;
  var target = null;            // last resolved /gm/target DTO (or null)
  var targetSeq = 0;
  var targetTimer = null;
  var activeVerb = null;        // verb whose inline form is open
  var stopRecentPoll = null;
  var mapsLoaded = false;
  var itemsLoaded = false;
  var monstersLoaded = false;

  var REASON_REQUIRED = {}; // reasons are optional everywhere (user, 2026-10-21); the server accepts an empty one

  // ---- caps ---------------------------------------------------------------------------------------------------------

  function loadCaps(force) {
    if (caps && !force) return Promise.resolve(caps);
    if (capsPromise && !force) return capsPromise;
    capsPromise = OpsUI.fetchJson('/api/admin/gm/caps').then(function (j) {
      caps = j || { verbs: [], groups: [] };
      return caps;
    }, function (e) {
      capsPromise = null;
      portalToast('Could not load the GM verbs: ' + ((e && e.message) || e), 'error');
      return null;
    });
    return capsPromise;
  }

  function verbOf(action) {
    if (!caps || !caps.verbs) return null;
    for (var i = 0; i < caps.verbs.length; i++) if (caps.verbs[i].action === action) return caps.verbs[i];
    return null;
  }

  function plainLabel(verb) {
    return String(verb.label || verb.action).replace(/…$/, '');
  }

  // ---- dialog + request -----------------------------------------------------------------------------------------------

  function isEmpty(v) { return v == null || (typeof v === 'string' && v.trim() === ''); }

  /* A dialog field (OpsUI.actionDialog spec) for one verb argument. */
  function dialogField(arg) {
    var f = { key: arg.name, label: arg.label || arg.name, required: !!arg.required };
    switch (arg.type) {
      case 'int':
        f.type = 'number';
        if (arg.min != null) f.min = arg.min;
        if (arg.max != null) f.max = arg.max;
        if (arg['default'] != null) f.value = arg['default'];
        break;
      case 'longtext':
        f.type = 'textarea';
        break;
      case 'duration':
        f.type = 'select';
        f.options = OpsUI.MUTE_PRESETS;
        f.value = '60';
        break;
      case 'spell':
        f.type = 'select';
        f.options = [{ value: '', label: 'Choose a spell…' }].concat((arg.options || []).map(function (o) { return { value: o.value, label: o.label }; }));
        f.value = '';
        break;
      case 'select':
        f.type = 'select';
        f.options = (arg.options || []).map(function (o) { return { value: o.value, label: o.label }; });
        if (arg['default'] != null) f.value = arg['default'];
        break;
      case 'conquest':
        if (caps && caps.conquests && caps.conquests.length) {
          f.type = 'select';
          f.options = caps.conquests.map(function (o) { return { value: o.value, label: o.label }; });
          f.value = caps.conquests[0].value;
        } else {
          f.type = 'number';
          f.min = 1;
          f.hint = 'Conquest index (the world is not running, so the list is unavailable).';
        }
        break;
      default:
        f.type = 'text';
        if (arg.placeholder) f.placeholder = arg.placeholder;
        break;
    }
    if (arg.type === 'map') f.hint = 'Map file name (e.g. 0, 0100) or title; #12 for a map index.';
    if (arg.type === 'item') f.hint = 'Item index or exact name.';
    if (arg.type === 'monster') f.hint = 'Monster index or exact name.';
    return f;
  }

  /* Converts raw argument values (form or dialog) into body members; returns { body } or { error }. Pure. */
  function convertArgs(verb, values) {
    var body = {};
    var args = verb.args || [];
    for (var i = 0; i < args.length; i++) {
      var a = args[i];
      var v = values[a.name];
      if (isEmpty(v)) {
        if (a.required) return { error: (a.label || a.name) + ' is required.' };
        continue;
      }
      if (a.type === 'duration') {
        var d = OpsUI.durationBody('mute', v);
        if (!d) return { error: 'Pick a duration.' };
        Object.assign(body, d);
        continue;
      }
      if (a.type === 'int' || (a.type === 'conquest' && /^\d+$/.test(String(v)))) {
        var n = Number(v);
        if (!isFinite(n) || Math.floor(n) !== n) return { error: (a.label || a.name) + ' must be a whole number.' };
        if (a.min != null && n < a.min) return { error: (a.label || a.name) + ' must be at least ' + a.min + '.' };
        if (a.max != null && n > a.max) return { error: (a.label || a.name) + ' must be at most ' + a.max + '.' };
        body[a.name] = n;
        continue;
      }
      body[a.name] = String(v).trim();
    }
    return { body: body };
  }

  function describeArgs(verb, values) {
    var parts = [];
    (verb.args || []).forEach(function (a) {
      var v = values[a.name];
      if (isEmpty(v)) return;
      if (a.type === 'duration') {
        var p = OpsUI.MUTE_PRESETS.filter(function (x) { return String(x.value) === String(v); })[0];
        v = p ? p.label : v + ' minutes';
      }
      parts.push((a.label || a.name).replace(/\s*\(.*\)$/, '') + ': ' + v);
    });
    return parts.join(' · ');
  }

  function targetLabel(t) {
    if (!t) return '';
    return t.character || (t.characterIndex ? '#' + t.characterIndex : '');
  }

  /*
   * Runs a verb: ONE dialog (reason + any argument not in `preset`), then the POST. Resolves the reply ({__ok, message,
   * data}) or null when cancelled (nothing sent). t = { character, characterIndex } (ignored for world verbs).
   */
  async function runFor(action, t, preset, button) {
    var c = await loadCaps();
    if (!c) return null;
    var verb = verbOf(action);
    if (!verb) { portalToast('Unknown GM action: ' + action, 'error'); return null; }

    var forCharacter = verb.target === 'character';
    if (forCharacter && (!t || (!t.character && !t.characterIndex))) {
      portalToast('Pick a target character first.', 'error');
      return null;
    }

    preset = preset || {};
    var missing = (verb.args || []).filter(function (a) { return !(a.name in preset) || (a.required && isEmpty(preset[a.name])); });
    var fields = missing.map(dialogField);
    fields.push({
      key: 'reason',
      label: REASON_REQUIRED[action] ? 'Reason (required; shown to the player and kept in the audit trail)' : 'Reason (kept in the audit trail)',
      type: 'text',
      required: !!REASON_REQUIRED[action],
      placeholder: REASON_REQUIRED[action] ? '' : 'optional'
    });

    var summary = describeArgs(verb, preset);
    var msg = (verb.description || '') + (summary ? '\n\n' + summary : '');
    if (forCharacter && verb.needsOnline && target && target.name && sameTarget(t) && !target.online)
      msg += '\n\nNote: ' + target.name + ' looks offline; the server will refuse this unless they are online.';

    var who = forCharacter ? targetLabel(t) : '';
    var v = await OpsUI.actionDialog({
      title: plainLabel(verb) + (who ? ': ' + who : ''),
      message: msg,
      confirmText: plainLabel(verb),
      danger: verb.danger ? true : false,
      fields: fields
    });
    if (!v) return null;   // cancelled: nothing sent

    var all = Object.assign({}, preset, v);
    var conv = convertArgs(verb, all);
    if (conv.error) { portalToast(conv.error, 'error'); return null; }

    var body = Object.assign({ action: action }, conv.body);
    if (forCharacter) {
      if (t.characterIndex) body.characterIndex = Number(t.characterIndex);
      else body.character = t.character;
    }
    if (!isEmpty(v.reason)) body.reason = String(v.reason).trim();

    return await OpsUI.withBusy(button, function () {
      return OpsUI.post('/api/admin/gm/action', body);
    }, 'Working…');
  }

  function sameTarget(t) {
    if (!target || !t) return false;
    if (t.characterIndex) return Number(t.characterIndex) === Number(target.characterIndex);
    return String(t.character || '').toLowerCase() === String(target.name || '').toLowerCase();
  }

  // ---- target -------------------------------------------------------------------------------------------------------

  function el(role) { return root ? root.querySelector('[data-role="' + role + '"]') : null; }

  function currentTarget() {
    if (target) return { character: target.name, characterIndex: target.characterIndex };
    var input = el('name');
    var name = input ? input.value.trim() : '';
    return name ? { character: name } : null;
  }

  function pill(text, kind) {
    return '<span class="ops-pill' + (kind ? ' ops-pill-' + kind : '') + '">' + esc(text) + '</span>';
  }

  /* HTML for a /gm/target DTO (escaped). */
  function renderTargetState(s) {
    if (!s) return '';
    var parts = [];
    parts.push(OpsUI.playerLink(s.name, { account: s.accountId, accountIndex: s.accountIndex, online: !!s.online, characterIndex: s.characterIndex }));
    parts.push(s.online ? pill('online', 'ok') : pill('offline'));
    parts.push('<span>Lv ' + esc(s.level) + ' ' + esc(s.className || '') + '</span>');
    if (s.map) parts.push('<span class="mir-muted">' + esc(s.map) + (s.x != null ? ' (' + esc(s.x) + ', ' + esc(s.y) + ')' : '') + '</span>');
    if (s.accountId) parts.push('<span class="mir-muted">account ' + esc(s.accountId) + '</span>');
    if (s.admin) parts.push(pill('admin account'));
    if (s.isGM) parts.push(pill('GM mode'));
    if (s.dead) parts.push(pill('dead', 'danger'));
    if (s.deleted) parts.push(pill('deleted', 'danger'));
    if (s.pk >= 200) parts.push(pill('PK ' + s.pk, 'danger'));
    else if (s.pk >= 100) parts.push(pill('PK ' + s.pk, 'warn'));
    if (s.chatBanned) parts.push(pill('chat banned until ' + OpsUI.fmtDate(s.chatBanUntil), 'warn'));
    if (s.accountBanned) parts.push(pill('account banned until ' + OpsUI.fmtDate(s.banUntil) + (s.banReason ? ': ' + s.banReason : ''), 'danger'));
    parts.push('<button type="button" class="ops-link" data-gm-link="character">Character</button>');
    parts.push('<button type="button" class="ops-link" data-gm-link="chatlog">Chat log</button>');
    return parts.join(' ');
  }

  async function refreshTarget() {
    var input = el('name');
    var state = el('state');
    if (!input || !state) return;
    var name = input.value.trim();
    var seq = ++targetSeq;

    if (!name) {
      target = null;
      state.innerHTML = '<span class="mir-muted">Type a character name (online players are suggested).</span>';
      syncButtons();
      return;
    }

    var url = '/api/admin/gm/target?name=' + encodeURIComponent(name);
    var r = await OpsUI.fetchResult(url);
    if (seq !== targetSeq) return;   // a newer name was typed meanwhile

    if (!r.ok) {
      target = null;
      state.innerHTML = pill(r.status === 404 ? 'No such character' : r.error, 'warn');
      syncButtons();
      return;
    }
    target = r.data;
    state.innerHTML = renderTargetState(target);
    syncButtons();
  }

  function scheduleTargetRefresh() {
    if (targetTimer) clearTimeout(targetTimer);
    target = null;
    syncButtons();
    targetTimer = setTimeout(refreshTarget, 400);
  }

  function setTarget(name) {
    var input = el('name');
    if (!input) return;
    input.value = name || '';
    portalSetHashParams({ name: name || null, player: null }, true);
    refreshTarget();
  }

  // ---- verb buttons + inline arg form ---------------------------------------------------------------------------------

  function verbButtonHtml(v) {
    return '<button type="button" class="mir-btn' + (v.danger ? ' danger' : '') + '" data-gm-action="' + esc(v.action) + '" title="' + esc(v.description || '') + '">' +
      esc(v.label || v.action) + '</button>';
  }

  function renderVerbs() {
    var charHost = el('char-groups');
    var worldHost = el('world-groups');
    if (!caps || !charHost || !worldHost) return;

    var groups = caps.groups || [];
    var charHtml = '', worldHtml = '';
    groups.forEach(function (g) {
      var vs = caps.verbs.filter(function (v) { return v.group === g; });
      if (!vs.length) return;
      var block = '<div class="ops-group gm-group"><h3>' + esc(g) + '</h3><div class="gm-verbs">' + vs.map(verbButtonHtml).join('') + '</div></div>';
      if (vs[0].target === 'world') worldHtml += block;
      else charHtml += block;
    });
    charHost.innerHTML = charHtml;
    worldHost.innerHTML = worldHtml;
    syncButtons();
  }

  function syncButtons() {
    if (!root || !caps) return;
    var btns = root.querySelectorAll('[data-gm-action]');
    for (var i = 0; i < btns.length; i++) {
      var v = verbOf(btns[i].getAttribute('data-gm-action'));
      if (!v || v.target !== 'character') continue;
      var reason = '';
      if (!target) reason = 'Look up a character first.';
      else if (v.needsOnline && !target.online) reason = target.name + ' is offline.';
      btns[i].disabled = !!reason;
      btns[i].title = reason || v.description || '';
    }
    var wnote = el('world-note');
    if (wnote) {
      var st = global.AdminStatus && AdminStatus.last();
      var running = !st || st.world === 'running';
      wnote.textContent = running ? '' : 'The world is not running: world actions are unavailable until it starts.';
    }
  }

  function inputForArg(arg) {
    var id = 'gmArg-' + arg.name;
    var label = '<span>' + esc(arg.label || arg.name) + (arg.required ? ' *' : '') + '</span>';
    var input;
    switch (arg.type) {
      case 'int':
        input = '<input type="number" class="fd-input" id="' + id + '" data-arg="' + esc(arg.name) + '"' +
          (arg.min != null ? ' min="' + esc(arg.min) + '"' : '') + (arg.max != null ? ' max="' + esc(arg.max) + '"' : '') +
          (arg['default'] != null ? ' value="' + esc(arg['default']) + '"' : '') + (arg.placeholder ? ' placeholder="' + esc(arg.placeholder) + '"' : '') + ' />';
        break;
      case 'longtext':
        input = '<textarea class="fd-input ops-textarea" id="' + id + '" data-arg="' + esc(arg.name) + '" maxlength="' + esc(arg.max || 500) + '"></textarea>';
        break;
      case 'duration':
        input = '<select class="fd-input" id="' + id + '" data-arg="' + esc(arg.name) + '">' +
          OpsUI.MUTE_PRESETS.map(function (p) { return '<option value="' + esc(p.value) + '"' + (p.value === '60' ? ' selected' : '') + '>' + esc(p.label) + '</option>'; }).join('') + '</select>';
        break;
      case 'spell':
        input = '<select class="fd-input" id="' + id + '" data-arg="' + esc(arg.name) + '"><option value="">Choose a spell…</option>' +
          (arg.options || []).map(function (o) { return '<option value="' + esc(o.value) + '">' + esc(o.label) + '</option>'; }).join('') + '</select>';
        break;
      case 'select':
        input = '<select class="fd-input" id="' + id + '" data-arg="' + esc(arg.name) + '">' +
          (arg.options || []).map(function (o) { return '<option value="' + esc(o.value) + '"' + (String(o.value) === String(arg['default']) ? ' selected' : '') + '>' + esc(o.label) + '</option>'; }).join('') + '</select>';
        break;
      case 'conquest':
        if (caps && caps.conquests && caps.conquests.length) {
          input = '<select class="fd-input" id="' + id + '" data-arg="' + esc(arg.name) + '">' +
            caps.conquests.map(function (o) { return '<option value="' + esc(o.value) + '">' + esc(o.label) + '</option>'; }).join('') + '</select>';
        } else {
          input = '<input type="number" min="1" class="fd-input" id="' + id + '" data-arg="' + esc(arg.name) + '" placeholder="Conquest index" />';
        }
        break;
      default: {
        var list = arg.type === 'map' ? 'gmMapList' : arg.type === 'item' ? 'gmItemList' : arg.type === 'monster' ? 'gmMonsterList' : '';
        input = '<input type="text" class="fd-input" id="' + id + '" data-arg="' + esc(arg.name) + '" autocomplete="off"' +
          (list ? ' list="' + list + '"' : '') + (arg.placeholder ? ' placeholder="' + esc(arg.placeholder) + '"' : '') +
          (arg.max ? ' maxlength="' + esc(arg.max) + '"' : '') + ' />';
      }
    }
    return '<label class="' + (arg.type === 'longtext' ? 'gm-wide' : '') + '">' + label + input + '</label>';
  }

  function openForm(verb) {
    var host = el(verb.target === 'world' ? 'world-form' : 'char-form');
    closeForms();
    activeVerb = verb;
    if (!host) return;

    if (verb.args && verb.args.some(function (a) { return a.type === 'map'; })) loadMaps();
    if (verb.args && verb.args.some(function (a) { return a.type === 'item'; })) loadLookupList('items', 'gmItemList');
    if (verb.args && verb.args.some(function (a) { return a.type === 'monster'; })) loadLookupList('monsters', 'gmMonsterList');

    var useHere = verb.target === 'character' ? '' : (target && target.online && verb.args.some(function (a) { return a.type === 'map'; })
      ? '<button type="button" class="mir-btn" data-gm-form="here" title="Use the target player\'s map and position">At ' + esc(target.name) + '</button>' : '');

    host.innerHTML =
      '<div class="gm-form">' +
      '  <div class="gm-form-head"><strong>' + esc(plainLabel(verb)) + '</strong> <span class="mir-muted">' + esc(verb.description || '') + '</span></div>' +
      '  <div class="ops-form">' + (verb.args || []).map(inputForArg).join('') + '</div>' +
      '  <div class="gm-form-actions">' +
      '    <button type="button" class="mir-btn ' + (verb.danger ? 'danger' : 'primary') + '" data-gm-form="run">' + esc(plainLabel(verb)) + '…</button>' +
      '    <button type="button" class="mir-btn" data-gm-form="cancel">Cancel</button>' + useHere +
      '  </div>' +
      '</div>';
    host.classList.remove('hidden');
    var first = host.querySelector('[data-arg]');
    if (first && first.focus) first.focus();
  }

  function closeForms() {
    activeVerb = null;
    ['char-form', 'world-form'].forEach(function (r) {
      var h = el(r);
      if (h) { h.innerHTML = ''; h.classList.add('hidden'); }
    });
  }

  function formValues(host) {
    var out = {};
    var inputs = host.querySelectorAll('[data-arg]');
    for (var i = 0; i < inputs.length; i++) out[inputs[i].getAttribute('data-arg')] = inputs[i].value;
    return out;
  }

  async function onVerbClick(action, button) {
    await loadCaps();
    var verb = verbOf(action);
    if (!verb) return;
    if (verb.args && verb.args.length) { openForm(verb); return; }
    await runAndReport(verb, {}, button);
  }

  async function runAndReport(verb, values, button) {
    var t = verb.target === 'character' ? currentTarget() : null;
    var j = await runFor(verb.action, t, values, button);
    if (!j) return;
    var res = el('result');
    if (res) {
      res.textContent = j.message || '';
      res.className = 'gm-result ' + (j.__ok ? 'ok' : 'error');
    }
    if (j.__ok) closeForms();
    loadRecent();
    if (verb.target === 'character') refreshTarget();
  }

  // ---- pickers ------------------------------------------------------------------------------------------------------

  function fillDatalist(id, options) {
    var dl = document.getElementById(id);
    if (!dl) return;
    dl.innerHTML = options.map(function (o) {
      return '<option value="' + esc(o.value) + '"' + (o.label ? ' label="' + esc(o.label) + '"' : '') + '></option>';
    }).join('');
  }

  async function loadPlayers() {
    var j = await OpsUI.get('/api/admin/gm/players');
    if (!j || !j.players) return;
    fillDatalist('gmPlayerList', j.players.map(function (p) {
      return { value: p.name, label: 'Lv ' + p.level + ' ' + (p.className || '') + (p.map ? ' · ' + p.map : '') + (p.accountId ? ' · ' + p.accountId : '') };
    }));
  }

  async function loadMaps() {
    if (mapsLoaded) return;
    var j = await OpsUI.get('/api/admin/gm/maps');
    if (!j || !j.maps) return;
    mapsLoaded = true;
    fillDatalist('gmMapList', j.maps.map(function (m) {
      return { value: m.fileName || String(m.index), label: '#' + m.index + ' ' + (m.title || '') + (m.instances > 1 ? ' (' + m.instances + ' instances)' : '') };
    }));
  }

  async function loadLookupList(name, listId) {
    if ((name === 'items' && itemsLoaded) || (name === 'monsters' && monstersLoaded)) return;
    if (!global.DbLookups || typeof DbLookups.fetch !== 'function') return;
    try {
      var lk = await DbLookups.fetch(name);
      var opts = (lk && lk.options) || [];
      fillDatalist(listId, opts.slice(0, 5000).map(function (o) { return { value: String(o.index), label: o.label }; }));
      if (name === 'items') itemsLoaded = true; else monstersLoaded = true;
    } catch (e) { /* free text still works */ }
  }

  // ---- recent actions ------------------------------------------------------------------------------------------------

  function detailText(e) {
    var d = e.details || {};
    var bits = [];
    ['amount', 'item', 'given', 'spell', 'level', 'to', 'map', 'x', 'y', 'monster', 'spawned', 'killed', 'conquest', 'until', 'text'].forEach(function (k) {
      if (d[k] == null || d[k] === '') return;
      var v = k === 'until' ? OpsUI.fmtDate(d[k]) : d[k];
      bits.push(k + ': ' + v);
    });
    return bits.join(' · ');
  }

  async function loadRecent() {
    var host = el('recent');
    if (!host) return;
    var j = await OpsUI.get('/api/admin/gm/recent?limit=40');
    var entries = (j && j.entries) || [];
    if (!entries.length) { host.innerHTML = '<p class="mir-muted gm-empty">No GM actions in the last 30 days.</p>'; return; }
    host.innerHTML = entries.map(function (e) {
      var d = e.details || {};
      var who = d.character ? OpsUI.playerLink(d.character, { account: d.account, characterIndex: d.characterIndex }) : esc(e.target || '');
      var text = detailText(e);
      return '<div class="ops-row">' +
        '<span class="ops-row-time" title="' + esc(OpsUI.fmtDate(e.ts)) + '">' + esc(OpsUI.ago(e.ts)) + '</span>' +
        '<span class="ops-row-tag">' + esc(String(e.action || '').replace(/^gm\./, '')) + '</span>' +
        '<span class="ops-row-who">' + who + '</span>' +
        '<span class="ops-row-text">' + esc(text) + (e.reason ? ' <span class="mir-muted">— ' + esc(e.reason) + '</span>' : '') +
        ' <span class="mir-muted">by ' + esc(e.admin || '') + '</span></span>' +
        '</div>';
    }).join('');
  }

  // ---- section ------------------------------------------------------------------------------------------------------

  function render(sectionEl) {
    sectionEl.innerHTML =
      '<div class="mir-card gm-target-card">' +
      '  <div class="toolbar"><h2>GM Console</h2></div>' +
      '  <div class="ops-form">' +
      '    <label>Target character<input type="text" class="fd-input" data-role="name" list="gmPlayerList" autocomplete="off" placeholder="Character name" /></label>' +
      '    <button type="button" class="mir-btn primary" data-role="lookup">Look up</button>' +
      '    <button type="button" class="mir-btn" data-role="clear">Clear</button>' +
      '  </div>' +
      '  <div class="gm-target-state" data-role="state"></div>' +
      '  <p class="gm-result" data-role="result" aria-live="polite"></p>' +
      '</div>' +
      '<div class="gm-columns">' +
      '  <div class="mir-card"><div class="toolbar"><h2>Character actions</h2></div>' +
      '    <div data-role="char-groups"></div><div class="gm-form-host hidden" data-role="char-form"></div></div>' +
      '  <div class="mir-card"><div class="toolbar"><h2>World actions</h2></div>' +
      '    <p class="mir-muted" data-role="world-note"></p>' +
      '    <div data-role="world-groups"></div><div class="gm-form-host hidden" data-role="world-form"></div></div>' +
      '</div>' +
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Recent GM actions</h2><button type="button" class="mir-btn" data-role="refresh-recent">Refresh</button></div>' +
      '  <div class="ops-log" data-role="recent"></div>' +
      '</div>' +
      '<datalist id="gmPlayerList"></datalist><datalist id="gmMapList"></datalist>' +
      '<datalist id="gmItemList"></datalist><datalist id="gmMonsterList"></datalist>';
  }

  function wire(sectionEl) {
    var input = el('name');
    input.addEventListener('input', scheduleTargetRefresh);
    input.addEventListener('change', scheduleTargetRefresh);
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); setTarget(input.value.trim()); }
    });
    el('lookup').addEventListener('click', function () { setTarget(input.value.trim()); });
    el('clear').addEventListener('click', function () { closeForms(); setTarget(''); });
    el('refresh-recent').addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, loadRecent); });

    sectionEl.addEventListener('click', function (e) {
      var t = e.target;
      var verbBtn = t.closest ? t.closest('[data-gm-action]') : null;
      if (verbBtn && sectionEl.contains(verbBtn)) {
        onVerbClick(verbBtn.getAttribute('data-gm-action'), verbBtn);
        return;
      }
      var formBtn = t.closest ? t.closest('[data-gm-form]') : null;
      if (formBtn && activeVerb) {
        var kind = formBtn.getAttribute('data-gm-form');
        var host = formBtn.closest('.gm-form-host');
        if (kind === 'cancel') { closeForms(); return; }
        if (kind === 'here' && target && host) {
          var set = function (name, v) { var f = host.querySelector('[data-arg="' + name + '"]'); if (f) f.value = v == null ? '' : String(v); };
          set('map', target.mapFile || target.mapIndex);
          set('x', target.x);
          set('y', target.y);
          return;
        }
        if (kind === 'run' && host) runAndReport(activeVerb, formValues(host), formBtn);
        return;
      }
      var link = t.closest ? t.closest('[data-gm-link]') : null;
      if (link && target) {
        if (link.getAttribute('data-gm-link') === 'character') portalOpenDeepLink('characters', { index: target.characterIndex, q: target.name });
        else portalOpenDeepLink('chatlog', { name: target.name });
      }
    });
  }

  global.AdminSections.register('gm', {
    load: async function (sectionEl) {
      root = sectionEl;
      render(sectionEl);
      wire(sectionEl);
      var c = await loadCaps();
      if (c) renderVerbs();
      refreshTarget();
    },
    show: function () {
      loadPlayers();
      loadRecent();
      if (!caps) loadCaps().then(function (c) { if (c) renderVerbs(); });
      else loadCaps(true).then(function (c) { if (c) renderVerbs(); });
      syncButtons();
      if (!stopRecentPoll) stopRecentPoll = global.AdminSections.poll('gm', function () { syncButtons(); return loadRecent(); }, 15000);
    },
    hide: function () {
      if (stopRecentPoll) { stopRecentPoll(); stopRecentPoll = null; }
    }
  });

  portalRegisterDeepLink('gm', function (p) {
    var name = p.name || p.player || '';
    if (!name && !p.characterIndex) return;
    if (!root) return;
    var input = el('name');
    if (name && input && input.value.trim().toLowerCase() === String(name).toLowerCase() && target) return;
    if (name) setTarget(name);
    else if (p.characterIndex) {
      OpsUI.fetchResult('/api/admin/gm/target?characterIndex=' + encodeURIComponent(p.characterIndex)).then(function (r) {
        if (r.ok && r.data && r.data.name) setTarget(r.data.name);
      });
    }
  });

  /* Shared entry points (chat log, players online, characters, security). */
  global.GmConsole = {
    open: function (name) { portalOpenDeepLink('gm', { name: name || '' }); },
    runFor: runFor,
    loadCaps: loadCaps,
    renderTargetState: renderTargetState,
    convertArgs: convertArgs
  };
})(window);
