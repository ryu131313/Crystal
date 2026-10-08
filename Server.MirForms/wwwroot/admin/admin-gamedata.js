/**
 * Game Data section (#section-gamedata, feature B9): the system tables of the Edit envir as grid editors.
 *
 * Sub-navigation: Items · Monsters · Magic · NPCs · Quests · Maps · Game Shop · Conquests · Dragon.
 * Hash: #section=gamedata&table=<endpoint>&index=<n> (a child endpoint such as maprespawns opens its master tab).
 *
 * - One makeDbGridEditor per table, created the first time its tab is opened and kept afterwards (switching tabs never
 *   throws edits away). "Apply rows + Save DB" goes through persistRows(): only the changed rows are sent; more than one
 *   changed row -> PUT ?dryRun=1 and a preview dialog first; then PUT -> POST /api/admin/database/save; validation and
 *   conflicts are shown verbatim; the grid reloads after the save.
 * - Maps / Conquests: a master grid plus child tabs (Safe zones, Respawns, ... / Guards, Gates, ...) filtered by the
 *   selected master row (setParentFilter); new child rows default to the selected parent.
 * - Dragon: a single-record form.
 * - Monsters: "Apply live to running world" (PUT ?live=1). Items / Monsters: renamed rows offer the reference rewrite
 *   (POST /api/admin/db/rename-propagate, dry run first); the rewritten files take effect at the next world restart (no
 *   live Reload drops / NPCs: the running world still resolves the old name, see noteRenameRestart).
 * - Persistent banner: unsaved Edit changes (GET /api/admin/database/status) with Save to disk / Discard unsaved, and the
 *   pending-reboot list (GET /api/admin/status via AdminStatus) with Reboot world.
 * - Stat_* columns are grouped into one collapsible "Stats" column group; column presets All / Basic / Stats / Flags.
 *
 * Globals: GameData (open / editor / current), DbNav (goto / openChild), DbRegistry (labels, lookup homes, child tables
 * for the row drawer), GameDataLogic (pure helpers).
 */
(function (global) {
  'use strict';

  // Every dynamic string reaches the DOM through textContent / DOM nodes (no innerHTML with data).
  var SECTION = 'gamedata';

  // ---- table registry -------------------------------------------------------------------------------------------------

  var TABS = [
    { id: 'items', label: 'Items', kind: 'grid' },
    { id: 'monsters', label: 'Monsters', kind: 'grid' },
    { id: 'magic', label: 'Magic', kind: 'grid', noInsert: true, noDelete: true },
    { id: 'npcs', label: 'NPCs', kind: 'grid' },
    { id: 'quests', label: 'Quests', kind: 'grid' },
    { id: 'maps', label: 'Maps', kind: 'master', parentField: 'MapIndex', parentLabel: 'Map' },
    { id: 'gameshop', label: 'Game Shop', kind: 'grid' },
    { id: 'conquests', label: 'Conquests', kind: 'master', parentField: 'ConquestIndex', parentLabel: 'Conquest' },
    { id: 'dragon', label: 'Dragon', kind: 'form' }
  ];

  var CHILDREN = {
    maps: [
      { endpoint: 'mapsafezones', label: 'Safe zones' },
      { endpoint: 'maprespawns', label: 'Respawns' },
      // Parent column MapIndex = the OWNING (source) map, i.e. the MapInfo whose Movements list holds the row. B8 exposes
      // Crystal's MovementInfo.MapIndex (the destination map) as DestinationMapIndex, so MapIndex here is never the destination.
      { endpoint: 'mapmovements', label: 'Movements' },
      { endpoint: 'mapminezones', label: 'Mine zones' }
    ],
    conquests: [
      { endpoint: 'conquestguards', label: 'Guards' },
      { endpoint: 'conquestgates', label: 'Gates' },
      { endpoint: 'conquestwalls', label: 'Walls' },
      { endpoint: 'conquestsieges', label: 'Sieges' },
      { endpoint: 'conquestflags', label: 'Flags' },
      { endpoint: 'conquestcontrolpoints', label: 'Control points' }
    ]
  };

  // Lookup name (schema `lookup`) -> the endpoint that holds its rows (go-to arrows on lookup cells).
  var LOOKUP_HOME = { items: 'items', monsters: 'monsters', maps: 'maps', npcs: 'npcs', quests: 'quests', magics: 'magic', magic: 'magic', conquests: 'conquests' };

  // "Basic" column preset per table (Crystal field names; a lookup's display column counts for its index field).
  var BASIC = {
    items: ['Name', 'Type', 'Grade', 'RequiredType', 'RequiredAmount', 'RequiredClass', 'RequiredGender', 'Set', 'Shape', 'Image',
      'Durability', 'Price', 'StackSize', 'Weight', 'Light', 'Effect', 'ToolTip'],
    monsters: ['Name', 'Image', 'AI', 'Effect', 'Level', 'Light', 'AttackSpeed', 'MoveSpeed', 'ViewRange', 'CoolEye', 'Experience',
      'DropPath', 'IsBoss', 'CanTame', 'CanPush', 'AutoRev', 'Undead', 'CanRecall'],
    magic: ['Name', 'Spell', 'Icon', 'Level1', 'Level2', 'Level3', 'Need1', 'Need2', 'Need3', 'BaseCost', 'LevelCost', 'Range'],
    npcs: ['FileName', 'Name', 'MapIndex', 'Location_X', 'Location_Y', 'Image', 'Rate', 'MinLev', 'MaxLev'],
    quests: ['Name', 'Group', 'Type', 'FileName', 'RequiredMinLevel', 'RequiredMaxLevel', 'RequiredQuest', 'RequiredClass'],
    maps: ['FileName', 'Title', 'MiniMap', 'BigMap', 'Music', 'Light', 'MapDarkLight', 'MineIndex'],
    gameshop: ['ItemIndex', 'GoldPrice', 'CreditPrice', 'Count', 'Stock', 'iStock', 'Class', 'Category', 'TopItem', 'Deal'],
    conquests: ['Name', 'MapIndex', 'PalaceIndex', 'Type', 'Game', 'StartHour', 'WarLength']
  };

  var tabById = Object.create(null);
  TABS.forEach(function (t) { tabById[t.id] = t; });
  var childOwner = Object.create(null);   // child endpoint -> master tab id
  Object.keys(CHILDREN).forEach(function (m) { CHILDREN[m].forEach(function (c) { childOwner[c.endpoint] = m; }); });

  function staticLabel(endpoint) {
    if (tabById[endpoint]) return tabById[endpoint].label;
    var m = childOwner[endpoint];
    if (m) {
      var c = CHILDREN[m].filter(function (x) { return x.endpoint === endpoint; })[0];
      return tabById[m].label + ' › ' + c.label;
    }
    return null;
  }

  // ---- pure helpers (GameDataLogic) -----------------------------------------------------------------------------------

  /** The payload of a PUT / dry-run reply, accepting { ok, applied, ... } or an { ok, message, data: {...} } envelope. */
  function normalizeResult(j) {
    var src = j && typeof j === 'object' ? j : {};
    if (src.applied === undefined && src.data && typeof src.data === 'object' && !Array.isArray(src.data) &&
      (src.data.applied !== undefined || src.data.conflicts || src.data.validation)) {
      src = Object.assign({ ok: j.ok, message: j.message }, j.data);
    }
    return {
      ok: src.ok !== false,
      message: src.message || src.error || '',
      applied: Number(src.applied) || 0,
      conflicts: Array.isArray(src.conflicts) ? src.conflicts : [],
      validation: Array.isArray(src.validation) ? src.validation : [],
      ignoredFields: Array.isArray(src.ignoredFields) ? src.ignoredFields : [],
      renames: Array.isArray(src.renames) ? src.renames : null,
      live: src.live && typeof src.live === 'object' ? src.live : null,
      pendingReboot: src.pendingReboot !== false,
      changes: Array.isArray(src.changes) ? src.changes : null
    };
  }

  /**
   * The rows a save sends: rows with tracked edits plus new rows (Index <= 0). Every grid edit (cell edits, paste, the row
   * drawer via DbSave.onEdit) goes through the grid's edit log, so an empty log means there is nothing to send — even when
   * the grid's dirty flag is still set (an edit typed back to its original value removes its log entry, not the flag).
   */
  function changedRows(rows, pending) {
    var keys = Object.create(null);
    (pending || []).forEach(function (e) { keys[String(e.index)] = true; });
    return (rows || []).filter(function (r) { return r && (keys[String(r.Index)] || !(Number(r.Index) > 0)); });
  }

  /** Name changes of existing rows recorded in the grid's edit log -> [{ endpoint, old, new }]. */
  function localRenames(endpoint, pending) {
    var out = [];
    (pending || []).forEach(function (e) {
      if (!e || e.added || !(Number(e.index) > 0) || !e.fields || !e.fields.Name) return;
      var f = e.fields.Name;
      var a = f.before == null ? '' : String(f.before), b = f.after == null ? '' : String(f.after);
      if (a && b && a !== b) out.push({ endpoint: endpoint, old: a, 'new': b });
    });
    return out;
  }

  /** Unsaved Edit changes from /api/admin/database/status: a number, true, or 0. */
  function unsavedCount(st) {
    if (!st) return 0;
    var u = st.unsavedChanges;
    if (typeof u === 'number') return Math.max(0, u);
    if (u === true) return 1;
    if (Array.isArray(u)) return u.length;
    return 0;
  }

  /** "Stat_MaxAC" -> "MaxAC". */
  function statName(field) { return String(field || '').replace(/^Stat_/, ''); }

  /** Non-zero stats of a row as "MaxAC 5 · MinDC 2". */
  function statSummary(row, fields, sep) {
    if (!row) return '';
    var parts = [];
    (fields || []).forEach(function (f) {
      var v = Number(row[f]);
      if (v && isFinite(v)) parts.push(statName(f) + ' ' + v);
    });
    return parts.join(sep || ' · ');
  }

  /** A row's display name for chips and labels. */
  function rowName(row) {
    if (!row) return '';
    return row.Title || row.Name || row.ItemName || row.MonsterName || row.FileName || row.Description || ('#' + row.Index);
  }

  /** Which kinds of game file (drops / NPCs / other) a set of rewritten files touches. */
  function reloadKinds(files) {
    var k = { drops: false, npcs: false, needsReboot: false };
    (files || []).forEach(function (f) {
      var p = String(f && (f.path || f.file || f.name) || '').replace(/\//g, '\\').toLowerCase();
      if (p.indexOf('\\drops\\') >= 0 || p.indexOf('drops\\') === 0) k.drops = true;
      else if (p.indexOf('\\npcs\\') >= 0 || p.indexOf('npcs\\') === 0) k.npcs = true;
      else if (p) k.needsReboot = true;
    });
    return k;
  }

  global.GameDataLogic = {
    normalizeResult: normalizeResult, changedRows: changedRows, localRenames: localRenames, unsavedCount: unsavedCount,
    statName: statName, statSummary: statSummary, rowName: rowName, reloadKinds: reloadKinds
  };

  // ---- state ----------------------------------------------------------------------------------------------------------

  var root = null;            // the section element
  var ui = {};                // banner, tabs, panes
  var panes = Object.create(null);     // tab id -> { el, ... }
  var editors = Object.create(null);   // endpoint -> grid editor
  var built = Object.create(null);     // endpoint -> true once its grid exists and was decorated
  var masters = Object.create(null);   // master tab id -> { selected: {index, label} | null, activeChild, childPanes }
  var tablesInfo = Object.create(null);
  var tablesPromise = null;
  var currentTab = null;
  var dbStatus = null;
  var dragon = null;          // { schema, row, inputs, dirty }
  var bannerSig = '';
  var liveMonsters = false;

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

  function label(endpoint) {
    var t = tablesInfo[endpoint];
    return staticLabel(endpoint) || (t && t.label) || endpoint;
  }

  function parentFieldOf(child) {
    var t = tablesInfo[child];
    if (t && t.parent && t.parent.field) return t.parent.field;
    var m = childOwner[child];
    return m ? tabById[m].parentField : null;
  }

  // ---- registries other modules use ------------------------------------------------------------------------------------

  global.DbRegistry = {
    DISPLAY_FIELDS: ['ItemName', 'MonsterName', 'MapName', 'NPCName', 'QuestName', 'ConquestName', 'DestinationMapName', 'SourceMapName'],
    label: function (endpoint) { return label(endpoint); },
    homeOfLookup: function (lookup) { return LOOKUP_HOME[String(lookup || '').toLowerCase()] || null; },
    children: function (endpoint) {
      return (CHILDREN[endpoint] || []).map(function (c) { return { label: c.label, endpoint: c.endpoint, field: parentFieldOf(c.endpoint) }; });
    },
    tabOf: function (endpoint) { return tabById[endpoint] ? endpoint : (childOwner[endpoint] || null); }
  };

  // Row drawer: links from a row to the single row it points at.
  if (global.FormDrawer && global.FormDrawer.LINKS) {
    var L = global.FormDrawer.LINKS;
    L.gameshop = [{ title: 'Item', field: 'ItemIndex', endpoint: 'items' }];
    L.maprespawns = [{ title: 'Monster', field: 'MonsterIndex', endpoint: 'monsters' }];
    L.npcs = [{ title: 'Map', field: 'MapIndex', endpoint: 'maps' }];
    L.conquests = [{ title: 'Map', field: 'MapIndex', endpoint: 'maps' }];
  }

  // ---- server calls ---------------------------------------------------------------------------------------------------

  function fetchTables(force) {
    if (!tablesPromise || force) {
      tablesPromise = (async function () {
        var res = await global.OpsUI.fetchResult('/api/admin/db/tables');
        var body = res.ok ? global.OpsUI.unwrap(res.data) : null;
        var list = body && Array.isArray(body.tables) ? body.tables : (res.data && Array.isArray(res.data.tables) ? res.data.tables : []);
        list.forEach(function (t) { if (t && t.endpoint) tablesInfo[String(t.endpoint).toLowerCase()] = t; });
        renderTabCounts();
        return list;
      })();
    }
    return tablesPromise;
  }

  async function refreshDbStatus() {
    var res = await global.OpsUI.fetchResult('/api/admin/database/status');
    if (res.ok) {
      var d = global.OpsUI.unwrap(res.data);
      dbStatus = d && typeof d === 'object' ? d : null;
    }
    renderBanner();
    updateBadge();
    return dbStatus;
  }

  /** PUT rows (dry run or real). Returns the normalised reply, or null after a transport / server error (toasted). */
  async function putRows(endpoint, rows, opts) {
    opts = opts || {};
    var q = [];
    if (opts.dryRun) q.push('dryRun=1');
    if (opts.live) q.push('live=1');
    var url = '/api/admin/db/' + encodeURIComponent(endpoint) + (q.length ? '?' + q.join('&') : '');
    var body = { rows: rows };
    if (opts.reason) body.reason = opts.reason;
    var r, j;
    try {
      r = await portalApi(url, { method: 'PUT', body: body });
      j = await r.json().catch(function () { return {}; });
    } catch (e) {
      portalToast('Request failed: ' + ((e && e.message) || e), 'error');
      return null;
    }
    var res = normalizeResult(j);
    if (!r.ok && !res.conflicts.length && !res.validation.length) {
      portalToast(res.message || ((opts.dryRun ? 'Dry run' : 'Save') + ' failed (HTTP ' + r.status + ').'), 'error');
      return null;
    }
    res.status = r.status;
    return res;
  }

  /** POST /api/admin/database/save. Returns true when the Edit envir was written to disk. */
  async function saveDatabase(quiet) {
    var j = await global.OpsUI.post('/api/admin/database/save', {}, { quiet: true });
    if (!j.__ok) return false;
    if (!quiet) portalToast(j.message || 'Database saved.', 'ok');
    return true;
  }

  // ---- dialogs --------------------------------------------------------------------------------------------------------

  /**
   * A modal built on the shared portal-confirm classes. spec: { title, build(body), confirmLabel?, danger?, reason? }.
   * Resolves { reason } on confirm, null on Cancel / Escape / backdrop. Enter never confirms (only a click on Confirm).
   */
  function modal(spec) {
    return new Promise(function (resolve) {
      var backdrop = el('div', 'portal-confirm-backdrop');
      var dialog = el('div', 'portal-confirm-dialog gd-dialog');
      dialog.setAttribute('role', 'dialog');
      dialog.setAttribute('aria-modal', 'true');
      dialog.appendChild(el('h3', null, spec.title || 'Game Data'));
      var body = el('div', 'gd-dialog-body');
      if (typeof spec.build === 'function') spec.build(body);
      dialog.appendChild(body);
      var reason = null;
      if (spec.reason) {
        var wrap = el('label', 'portal-form-field gd-dialog-reason');
        wrap.appendChild(el('span', 'mir-muted', 'Reason (optional, kept in the audit trail)'));
        reason = el('input', 'fd-input');
        reason.type = 'text';
        reason.maxLength = 300;
        wrap.appendChild(reason);
        dialog.appendChild(wrap);
      }
      var actions = el('div', 'portal-confirm-actions');
      var cancel = button(spec.confirmLabel ? 'Cancel' : 'Close');
      actions.appendChild(cancel);
      var confirm = null;
      if (spec.confirmLabel) {
        confirm = button(spec.confirmLabel, spec.danger ? 'danger' : 'primary');
        if (spec.confirmDisabled) { confirm.disabled = true; if (spec.confirmDisabledTitle) confirm.title = spec.confirmDisabledTitle; }
        actions.appendChild(confirm);
      }
      dialog.appendChild(actions);
      backdrop.appendChild(dialog);
      var done = false;
      function finish(v) {
        if (done) return;
        done = true;
        document.removeEventListener('keydown', onKey, true);
        if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
        resolve(v);
      }
      function onKey(e) {
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); finish(null); }
      }
      cancel.addEventListener('click', function () { finish(null); });
      if (confirm) confirm.addEventListener('click', function () { finish({ reason: reason ? String(reason.value || '').trim() : '' }); });
      backdrop.addEventListener('click', function (e) { if (e.target === backdrop) finish(null); });
      document.addEventListener('keydown', onKey, true);
      document.body.appendChild(backdrop);
      (reason || cancel).focus();
    });
  }

  function fmtValue(v) {
    if (v == null || v === '') return '(empty)';
    var t = typeof v === 'object' ? JSON.stringify(v) : String(v);
    t = t.replace(/\r?\n/g, ' ↵ ');
    return t.length > 80 ? t.slice(0, 77) + '…' : t;
  }

  // Validation / conflicts / ignored fields exactly as the server sent them.
  function appendIssues(body, res) {
    if (res.validation.length) {
      body.appendChild(el('h4', 'gd-dialog-h gd-bad', 'Validation (' + res.validation.length + ')'));
      var ul = el('ul', 'gd-issue-list');
      res.validation.slice(0, 300).forEach(function (v) {
        ul.appendChild(el('li', null, '#' + v.index + (v.field ? ' ' + v.field : '') + ': ' + (v.message || '')));
      });
      if (res.validation.length > 300) ul.appendChild(el('li', 'mir-muted', '… and ' + (res.validation.length - 300) + ' more'));
      body.appendChild(ul);
    }
    if (res.conflicts.length) {
      body.appendChild(el('h4', 'gd-dialog-h gd-bad', 'Conflicts (' + res.conflicts.length + ')'));
      var uc = el('ul', 'gd-issue-list');
      res.conflicts.slice(0, 300).forEach(function (c) {
        uc.appendChild(el('li', null, '#' + c.index + ': ' + (c.message || 'changed on the server since you loaded it')));
      });
      body.appendChild(uc);
      body.appendChild(el('p', 'mir-muted', 'Conflicting rows were changed on the server after you loaded them: reload the grid and re-apply those edits.'));
    }
    if (res.ignoredFields.length) {
      body.appendChild(el('p', 'mir-muted', 'Ignored field(s): ' + res.ignoredFields.join(', ')));
    }
  }

  function appendChanges(body, pending, rowsSent) {
    var byIndex = Object.create(null);
    (pending || []).forEach(function (e) { byIndex[String(e.index)] = e; });
    var added = rowsSent.filter(function (r) { return !(Number(r.Index) > 0); });
    var edited = rowsSent.filter(function (r) { return Number(r.Index) > 0; });
    body.appendChild(el('h4', 'gd-dialog-h', 'Changes (' + edited.length + ' edited, ' + added.length + ' new)'));
    var ul = el('ul', 'gd-change-list');
    edited.slice(0, 200).forEach(function (r) {
      var e = byIndex[String(r.Index)];
      var li = el('li');
      li.appendChild(el('strong', null, '#' + r.Index + ' ' + rowName(r)));
      var fields = e && e.fields ? Object.keys(e.fields) : [];
      if (!fields.length) li.appendChild(el('div', 'gd-change-field mir-muted', 'whole row'));
      fields.forEach(function (f) {
        li.appendChild(el('div', 'gd-change-field', f + ': ' + fmtValue(e.fields[f].before) + ' → ' + fmtValue(e.fields[f].after)));
      });
      ul.appendChild(li);
    });
    added.slice(0, 50).forEach(function (r) {
      var li = el('li');
      li.appendChild(el('strong', null, 'new row ' + rowName(r)));
      ul.appendChild(li);
    });
    if (edited.length > 200) ul.appendChild(el('li', 'mir-muted', '… and ' + (edited.length - 200) + ' more edited row(s)'));
    body.appendChild(ul);
  }

  function showIssues(title, res) {
    return modal({ title: title, build: function (body) { appendIssues(body, res); } });
  }

  // ---- save flow ------------------------------------------------------------------------------------------------------

  function worldRunning() {
    var st = global.AdminStatus && global.AdminStatus.last ? global.AdminStatus.last() : null;
    return !!(st && st.world === 'running');
  }

  // B8's live apply runs on the game thread and answers 503 (WorldNotRunning) while the world is stopped, which would fail
  // the whole save: only ask for it while the world runs.
  function liveFor(endpoint) { return endpoint === 'monsters' && liveMonsters && worldRunning(); }

  /** Enable the monsters live checkbox only while the world runs (called on build and on every AdminStatus change). */
  function syncLiveToggle() {
    var cb = ui.liveCb;
    if (!cb) return;
    var on = worldRunning();
    cb.disabled = !on;
    if (ui.liveLabel) {
      ui.liveLabel.classList.toggle('gd-live-off', !on);
      ui.liveLabel.title = on
        ? 'Also apply changed stats of existing monsters to the running world and refresh every spawned monster of that kind (like the Monster Tuner). The database is saved as usual.'
        : 'The world is not running: changes are saved to the database only and take effect when it starts.';
    }
  }

  /** persistLegacy for every grid: dry run + preview when > 1 row, PUT, database/save, renames. -> { ok, clean } */
  async function persistRows(endpoint, rows) {
    var ed = editors[endpoint];
    var pending = ed ? ed.pendingEdits() : [];
    var send = changedRows(rows, pending);
    if (!send.length) {
      // Edits that were all reverted leave the dirty flag on: clear it so the grid stops asking before reloads.
      if (global.DbSave && global.DbSave.setDirty) global.DbSave.setDirty(endpoint, false);
      portalToast('No changes to apply in ' + label(endpoint) + '.', 'info');
      return { ok: false };
    }
    var live = liveFor(endpoint);
    var renamesLocal = (endpoint === 'items' || endpoint === 'monsters') ? localRenames(endpoint, pending) : [];
    var reason = '';

    if (send.length > 1) {
      var dry = await putRows(endpoint, send, { dryRun: true });
      if (!dry) return { ok: false };
      var problems = dry.validation.length + dry.conflicts.length;
      var blocked = problems > 0 && dry.applied <= 0;
      var choice = await modal({
        title: 'Preview: ' + label(endpoint),
        confirmLabel: 'Apply ' + (problems ? dry.applied : send.length) + ' row(s) and save',
        confirmDisabled: blocked,
        confirmDisabledTitle: 'No row would apply: fix the problems listed above first.',
        reason: !blocked,
        build: function (body) {
          body.appendChild(el('p', null, 'Dry run on the server: ' + dry.applied + ' row(s) would apply' +
            (problems ? ', ' + problems + ' would be skipped' : '') + '. Nothing has been changed yet.'));
          if (live) body.appendChild(el('p', 'gd-live-note', 'Live: changed stats of existing monsters are also applied to the running world.'));
          appendIssues(body, dry);
          appendChanges(body, pending, send);
          body.appendChild(el('p', 'mir-muted', 'The database is backed up before it is written. Most game data takes effect after the next world restart.'));
        }
      });
      if (!choice) return { ok: false };
      reason = choice.reason || '';
    }

    var res = await putRows(endpoint, send, { live: live, reason: reason });
    if (!res) return { ok: false };
    var skipped = res.validation.length + res.conflicts.length;
    if (res.applied <= 0) {
      if (skipped) await showIssues('Nothing applied to ' + label(endpoint), res);
      else portalToast(res.message || 'Nothing was applied.', 'error');
      return { ok: false };
    }

    var saved = await saveDatabase(true);
    var msg = 'Applied ' + res.applied + ' row(s) to ' + label(endpoint) + (saved ? ' and saved the database.' : '.');
    if (res.live) {
      msg += '\nLive: ' + (Number(res.live.applied) || 0) + ' monster(s) updated, ' + (Number(res.live.refreshedMonsters) || 0) + ' spawned monster(s) refreshed.';
    }
    if (saved && res.pendingReboot) msg += '\nChanges take effect after the next world restart.';
    if (!saved) {
      portalToast('Applied ' + res.applied + ' row(s) to the editor copy, but writing the database to disk failed. Use "Save to disk" in the banner to retry.', 'error');
    } else {
      portalToast(msg, skipped ? 'info' : 'ok');
    }
    var rejected = skipped ? rejectedEdits(res, pending, send) : [];
    if (skipped) {
      modal({
        title: 'Applied ' + res.applied + ' row(s); ' + skipped + ' skipped',
        build: function (body) {
          appendIssues(body, res);
          if (rejected.length) {
            body.appendChild(el('p', 'mir-muted', 'Your edits of the ' + rejected.length + ' row(s) rejected by validation are kept in the grid: ' +
              'fix them and apply again.'));
          }
        }
      });
    }
    // clean:false below makes the grid factory clear its edit log and reload every row; put the rejected edits back after.
    if (rejected.length) setTimeout(function () { restoreRejected(endpoint, rejected); }, 0);

    afterServerChange();
    var renames = res.renames != null ? res.renames : renamesLocal;
    if (renames && renames.length) setTimeout(function () { handleRenames(renames); }, 0);
    // clean:false -> the grid factory reloads the rows (new __rev, server-allocated indexes).
    return { ok: true, clean: false };
  }

  /**
   * The edits of rows the server rejected by validation in a partly applied save: [{ index, fields } | { index, row }]
   * (fields = the edit log's { field: { before, after } } of an existing row; row = a copy of a new row as it was sent).
   * Conflicting rows are not kept: they changed on the server, so their edits must be redone against the new values.
   */
  function rejectedEdits(res, pending, send) {
    var bad = Object.create(null);
    res.validation.forEach(function (v) { if (v && v.index != null) bad[String(v.index)] = true; });
    var byIndex = Object.create(null);
    (pending || []).forEach(function (e) { if (e) byIndex[String(e.index)] = e; });
    var out = [];
    (send || []).forEach(function (r) {
      var k = String(r.Index);
      if (!bad[k]) return;
      if (!(Number(r.Index) > 0)) {
        var copy = JSON.parse(JSON.stringify(r));
        Object.keys(copy).forEach(function (f) { if (f.indexOf('__') === 0) delete copy[f]; });
        out.push({ index: r.Index, row: copy });
      } else if (byIndex[k] && byIndex[k].fields && Object.keys(byIndex[k].fields).length) {
        out.push({ index: r.Index, fields: byIndex[k].fields });
      }
    });
    return out;
  }

  /** After the post-save reload: re-apply the rejected edits (cell edits go through cellValueChanged -> edit log + dirty). */
  async function restoreRejected(endpoint, rejected) {
    var ed = editors[endpoint];
    if (!ed) return;
    try { await ed.load(); } catch (e) { /* the grid reports its own load errors */ }
    var api = ed.getApi();
    if (!api) return;
    var lost = [], added = [];
    rejected.forEach(function (r) {
      if (r.row) { added.push(r.row); return; }
      var node = api.getRowNode(String(r.index));
      if (!node) { lost.push(r.index); return; }
      Object.keys(r.fields).forEach(function (f) {
        var col = api.getColumn ? api.getColumn(f) : null;
        if (!col || !node.setDataValue(f, r.fields[f].after)) {
          if (!col || !sameish(node.data[f], r.fields[f].after)) lost.push(r.index + ' ' + f);
        }
      });
    });
    if (added.length) {
      api.applyTransaction({ add: added, addIndex: 0 });
      if (global.DbSave && global.DbSave.setDirty) global.DbSave.setDirty(endpoint, true);
      if (global.DbSave && global.DbSave.noteEdit) added.forEach(function (row) { global.DbSave.noteEdit(endpoint, row.Index, []); });
    }
    if (lost.length) {
      portalToast('Could not restore the rejected edit(s) of ' + lost.map(function (x) { return '#' + x; }).join(', ') + ': retype them.', 'error');
    }
  }

  function sameish(a, b) { return String(a == null ? '' : a) === String(b == null ? '' : b); }

  function afterServerChange() {
    refreshDbStatus();
    fetchTables(true);
    if (global.AdminStatus && global.AdminStatus.refresh) global.AdminStatus.refresh();
  }

  // ---- rename propagation --------------------------------------------------------------------------------------------

  async function handleRenames(renames) {
    var touched = [];
    for (var i = 0; i < renames.length; i++) {
      var rn = renames[i];
      if (!rn || !rn.old || !rn['new']) continue;
      var ep = rn.endpoint || 'items';
      var ask = await portalConfirm('"' + rn.old + '" was renamed to "' + rn['new'] + '". Drop, recipe, quest and NPC files still name it "' +
        rn.old + '". Check which files reference it?', { title: 'Update references in drop/recipe/quest/NPC files…', confirmLabel: 'Check files', danger: false });
      if (!ask) continue;
      var dry = await global.OpsUI.post('/api/admin/db/rename-propagate', { endpoint: ep, old: rn.old, 'new': rn['new'], dryRun: true }, { quiet: true });
      if (!dry.__ok) continue;
      var dd = global.OpsUI.unwrap(dry) || {};
      var files = (Array.isArray(dd.files) ? dd.files : (Array.isArray(dry.files) ? dry.files : [])).filter(function (f) { return f && Number(f.replacements) !== 0; });
      if (!files.length) { portalToast('No drop, recipe, quest or NPC file references "' + rn.old + '".', 'info'); continue; }
      var total = files.reduce(function (n, f) { return n + (Number(f.replacements) || 0); }, 0);
      var go = await modal({
        title: 'Rewrite references to "' + rn.old + '"',
        confirmLabel: 'Rewrite ' + files.length + ' file(s)',
        build: function (body) {
          body.appendChild(el('p', null, total + ' reference(s) in ' + files.length + ' file(s) will be changed to "' + rn['new'] + '". Each file is backed up first.'));
          var ul = el('ul', 'gd-file-list');
          files.slice(0, 400).forEach(function (f) {
            var li = el('li');
            li.appendChild(el('code', null, String(f.path || f.file || f.name || '')));
            li.appendChild(el('span', 'mir-muted', ' — ' + (f.replacements != null ? f.replacements + ' replacement(s)' : 'changed')));
            ul.appendChild(li);
          });
          if (files.length > 400) ul.appendChild(el('li', 'mir-muted', '… and ' + (files.length - 400) + ' more'));
          body.appendChild(ul);
        }
      });
      if (!go) continue;
      var done = await global.OpsUI.post('/api/admin/db/rename-propagate', { endpoint: ep, old: rn.old, 'new': rn['new'], dryRun: false }, { quiet: true });
      if (!done.__ok) continue;
      var dn = global.OpsUI.unwrap(done) || {};
      var written = Array.isArray(dn.files) ? dn.files : (Array.isArray(done.files) ? done.files : files);
      touched = touched.concat(written);
      portalToast('Updated references to "' + rn['new'] + '" in ' + written.length + ' file(s).', 'ok');
    }
    if (touched.length) noteRenameRestart(reloadKinds(touched));
  }

  // No live reload after a rename: the running world (Envir.Main) still has the OLD ItemInfo / MonsterInfo names — the rename
  // only exists in the Edit envir until a restart. DropInfo.FromLine and NPC [Trade] goods resolve names through
  // Envir.Main.GetItemInfo, and Envir.ReloadDrops builds drop-file paths from Main's monster names, so reloading the rewritten
  // drop / NPC files now would silently drop the renamed item (or a renamed monster's whole drop list) until the next restart.
  function noteRenameRestart(kinds) {
    var st = global.AdminStatus && global.AdminStatus.last ? global.AdminStatus.last() : null;
    var running = !!(st && st.world === 'running');
    var what = [];
    if (kinds.drops) what.push('drop');
    if (kinds.npcs) what.push('NPC');
    if (kinds.needsReboot) what.push('quest / recipe');
    var files = what.length ? 'The rewritten ' + what.join(', ') + ' files' : 'The rewritten files';
    portalToast(running
      ? files + ' take effect at the next world restart, together with the rename (the running world still uses the old name, ' +
        'so do not reload drops or NPCs before then). Use "Reboot world" in the banner when ready.'
      : files + ' are read when the world starts.', 'info');
  }

  // ---- column groups and presets --------------------------------------------------------------------------------------

  // columnsHook: every Stat_* column goes into one "Stats" group, collapsed to a summary column by default.
  function statsHook(defs) {
    var stats = [];
    function strip(list) {
      var out = [];
      (list || []).forEach(function (c) {
        if (c && Array.isArray(c.children)) {
          var kids = strip(c.children);
          if (!kids.length) return;
          if (!kids.some(function (k) { return !k.columnGroupShow; })) kids[0] = Object.assign({}, kids[0], { columnGroupShow: undefined });
          out.push(Object.assign({}, c, { children: kids }));
        } else if (c && /^Stat_/.test(c.field || '')) {
          stats.push(c);
        } else if (c) {
          out.push(c);
        }
      });
      return out;
    }
    var rest = strip(defs);
    if (!stats.length) return defs;
    var fields = stats.map(function (c) { return c.field; });
    var summary = {
      colId: '__statsum', headerName: 'Stats', editable: false, sortable: false, filter: true, minWidth: 140, maxWidth: 320,
      columnGroupShow: 'closed', cellClass: 'db-cell-display',
      valueGetter: function (p) { return statSummary(p.data, fields); },
      tooltipValueGetter: function (p) { return statSummary(p.data, fields, '\n'); }
    };
    rest.push({
      headerName: 'Stats (' + stats.length + ')', groupId: 'gd_stats', openByDefault: false, marryChildren: true, headerClass: 'db-col-group',
      children: [summary].concat(stats.map(function (c) {
        return Object.assign({}, c, { columnGroupShow: 'open', headerName: String(c.headerName || c.field).replace(/^Stat[\s_]+/, '') });
      }))
    });
    return rest;
  }

  function columnKind(d) {
    var field = d.field || '';
    return {
      index: field === 'Index',
      name: field === 'Name' || field === 'Title' || [].concat(d.cellClass || []).indexOf('db-cell-name') >= 0,
      stat: /^Stat_/.test(field) || d.colId === '__statsum',
      flag: d.cellEditor === 'agCheckboxCellEditor' || (global.DbSchema && d.cellEditor === global.DbSchema.FlagsCellEditor)
    };
  }

  function inList(list, d) {
    if (!list) return false;
    var f = d.field || '';
    if (list.indexOf(f) >= 0) return true;
    if (d.__refIndexField && list.indexOf(d.__refIndexField) >= 0) return true;
    return /Name$/.test(f) && list.indexOf(f.replace(/Name$/, 'Index')) >= 0;
  }

  /** Which presets a grid offers: Stats when it has Stat_* columns, Flags when it has bool / [Flags] columns. */
  function presetsFor(api) {
    var has = { stats: false, flags: false };
    (api.getColumns() || []).forEach(function (c) {
      var k = columnKind(c.getColDef() || {});
      if (k.stat) has.stats = true;
      if (k.flag) has.flags = true;
    });
    return has;
  }

  function applyPreset(endpoint, preset) {
    var ed = editors[endpoint];
    var api = ed && ed.getApi();
    if (!api || !api.getColumns) return;
    var show = [], hide = [];
    var basic = BASIC[endpoint];
    (api.getColumns() || []).forEach(function (c) {
      var d = c.getColDef() || {};
      if (d.__select || d.hide === true) return;   // the checkbox column; index columns a lookup column stands in for
      var k = columnKind(d);
      var on;
      if (preset === 'all') on = true;
      else if (k.index || k.name) on = true;
      else if (preset === 'stats') on = k.stat;
      else if (preset === 'flags') on = k.flag;
      else on = basic ? inList(basic, d) : (!k.stat && !k.flag);
      (on ? show : hide).push(c.getColId());
    });
    if (show.length) api.setColumnsVisible(show, true);
    if (hide.length) api.setColumnsVisible(hide, false);
    if (api.setColumnGroupOpened) {
      try { api.setColumnGroupOpened('gd_stats', preset === 'stats'); } catch (e) { /* no stats group */ }
    }
    var bar = panes[endpoint] ? panes[endpoint].presets : (masterPresetBar(endpoint));
    if (bar) Array.prototype.forEach.call(bar.querySelectorAll('[data-preset]'), function (b) {
      b.classList.toggle('primary', b.getAttribute('data-preset') === preset);
    });
  }

  function masterPresetBar(endpoint) {
    var m = childOwner[endpoint];
    var st = m ? masters[m] : null;
    var cp = st && st.childPanes[endpoint];
    return cp ? cp.presets : null;
  }

  function presetBar(endpoint) {
    var bar = el('div', 'gd-presets hidden');
    bar.appendChild(el('span', 'mir-muted gd-presets-label', 'Columns:'));
    [['all', 'All'], ['basic', 'Basic'], ['stats', 'Stats'], ['flags', 'Flags']].forEach(function (p) {
      var b = button(p[1], '', 'Show the ' + p[1].toLowerCase() + ' columns');
      b.setAttribute('data-preset', p[0]);
      b.addEventListener('click', function () { applyPreset(endpoint, p[0]); });
      bar.appendChild(b);
    });
    return bar;
  }

  // After the grid exists: show the presets that apply, and disable Add / Delete where the table does not allow them.
  function decorate(endpoint, bar, container) {
    var ed = editors[endpoint];
    var api = ed && ed.getApi();
    if (!api || built[endpoint]) return;
    built[endpoint] = true;
    if (bar) {
      var has = presetsFor(api);
      bar.querySelector('[data-preset="stats"]').classList.toggle('hidden', !has.stats);
      bar.querySelector('[data-preset="flags"]').classList.toggle('hidden', !has.flags);
      bar.classList.remove('hidden');
    }
    var info = tablesInfo[endpoint] || {};
    var tab = tabById[endpoint] || {};
    var noInsert = info.insertable === false || !!tab.noInsert;
    var noDelete = info.deletable === false || !!tab.noDelete;
    if ((noInsert || noDelete) && container) {
      Array.prototype.forEach.call(container.querySelectorAll('.db-tools-bar button'), function (b) {
        var t = String(b.textContent || '');
        if (noInsert && (t === 'Add row' || t === 'Duplicate selected')) { b.disabled = true; b.title = label(endpoint) + ' rows cannot be added here.'; }
        if (noDelete && t === 'Delete selected') { b.disabled = true; b.title = label(endpoint) + ' rows cannot be deleted.'; }
      });
    }
  }

  // ---- grid editors ---------------------------------------------------------------------------------------------------

  var gridSeq = 0;
  function makeEditor(endpoint, gridEl, extra) {
    if (typeof global.makeDbGridEditor !== 'function') return null;
    gridEl.id = gridEl.id || ('gdGrid_' + endpoint + '_' + (++gridSeq));
    var opts = Object.assign({
      endpoint: endpoint,
      entity: endpoint,
      gridId: gridEl.id,
      title: label(endpoint),
      compact: true,
      wireDom: false,
      columnsHook: statsHook,
      persistLegacy: function (rows) { return persistRows(endpoint, rows); }
    }, extra || {});
    var ed = global.makeDbGridEditor(opts);
    editors[endpoint] = ed;
    return ed;
  }

  function gridMissing(container) {
    container.appendChild(el('p', 'mir-muted', 'The grid library did not load (check the connection to cdn.jsdelivr.net), so this table cannot be shown.'));
  }

  // A plain table tab: presets (+ live checkbox for monsters) and one grid.
  function buildGridPane(tab) {
    var pane = el('div', 'db-pane gd-pane hidden');
    pane.setAttribute('data-tab', tab.id);
    var bar = el('div', 'gd-pane-bar');
    var presets = presetBar(tab.id);
    bar.appendChild(presets);
    if (tab.id === 'monsters') {
      var lab = el('label', 'gd-live-toggle');
      var cb = el('input');
      cb.type = 'checkbox';
      cb.checked = liveMonsters;
      cb.addEventListener('change', function () { liveMonsters = cb.checked; });
      lab.appendChild(cb);
      lab.appendChild(document.createTextNode(' Apply live to running world'));
      ui.liveCb = cb;
      ui.liveLabel = lab;
      syncLiveToggle();
      bar.appendChild(lab);
    }
    pane.appendChild(bar);
    var grid = el('div', 'ag-theme-quartz-dark grid-panel db-grid gd-grid');
    pane.appendChild(grid);
    ui.panes.appendChild(pane);
    var ed = makeEditor(tab.id, grid, {
      onRowClicked: function (e) {
        // table= too: a tab restored from localStorage never wrote it, and an index without its table is not a usable link.
        if (currentTab === tab.id && e && e.data && Number(e.data.Index) > 0) global.portalSetHashParams({ table: tab.id, index: e.data.Index }, true);
      }
    });
    if (!ed) gridMissing(pane);
    return { el: pane, presets: presets };
  }

  // Maps / Conquests: master grid, child tab strip, one child grid per child table.
  function buildMasterPane(tab) {
    var pane = el('div', 'db-pane gd-pane gd-master hidden');
    pane.setAttribute('data-tab', tab.id);
    var st = { selected: null, activeChild: null, childPanes: Object.create(null), tab: tab };
    masters[tab.id] = st;

    var bar = el('div', 'gd-pane-bar');
    var presets = presetBar(tab.id);
    bar.appendChild(presets);
    pane.appendChild(bar);
    var grid = el('div', 'ag-theme-quartz-dark grid-panel gd-master-grid');
    pane.appendChild(grid);

    var childHead = el('div', 'gd-child-head');
    var strip = el('div', 'database-tabs gd-tabs gd-child-tabs');
    CHILDREN[tab.id].forEach(function (c) {
      var b = button(c.label, 'gd-child-tab');
      b.setAttribute('data-child', c.endpoint);
      b.addEventListener('click', function () {
        showChild(tab.id, c.endpoint);
        global.portalSetHashParams({ table: c.endpoint, index: null }, true);
      });
      strip.appendChild(b);
    });
    childHead.appendChild(strip);
    st.scope = el('span', 'gd-child-scope mir-muted');
    childHead.appendChild(st.scope);
    pane.appendChild(childHead);
    st.childHost = el('div', 'gd-child-host');
    pane.appendChild(st.childHost);
    st.strip = strip;
    ui.panes.appendChild(pane);

    var ed = makeEditor(tab.id, grid, {
      onRowClicked: function (e) {
        if (!e || !e.data) return;
        selectParent(tab.id, e.data);
        if (Number(e.data.Index) > 0) global.portalSetHashParams({ table: tab.id, index: e.data.Index }, true);
      }
    });
    if (!ed) gridMissing(pane);
    renderScope(tab.id);
    return { el: pane, presets: presets };
  }

  function renderScope(masterId) {
    var st = masters[masterId];
    if (!st || !st.scope) return;
    st.scope.textContent = st.selected
      ? 'Showing rows of ' + st.tab.parentLabel + ' #' + st.selected.index + ' ' + st.selected.label
      : 'Click a ' + st.tab.parentLabel.toLowerCase() + ' row above to show only its rows; new rows belong to the selected ' + st.tab.parentLabel.toLowerCase() + '.';
  }

  function childFilter(masterId, child) {
    var st = masters[masterId];
    if (!st || !st.selected) return null;
    return { field: parentFieldOf(child), value: st.selected.index, label: st.tab.parentLabel + ' = ' + st.selected.label };
  }

  function selectParent(masterId, data) {
    var st = masters[masterId];
    if (!st || !data || !(Number(data.Index) > 0)) return;
    st.selected = { index: Number(data.Index), label: rowName(data) };
    Object.keys(st.childPanes).forEach(function (child) {
      var ed = editors[child];
      if (ed && ed.getApi()) ed.setParentFilter(childFilter(masterId, child));
    });
    renderScope(masterId);
  }

  function ensureChildPane(masterId, child) {
    var st = masters[masterId];
    if (st.childPanes[child]) return st.childPanes[child];
    var pane = el('div', 'gd-child-pane hidden');
    var presets = presetBar(child);
    pane.appendChild(presets);
    var grid = el('div', 'ag-theme-quartz-dark grid-panel gd-child-grid');
    pane.appendChild(grid);
    st.childHost.appendChild(pane);
    var field = parentFieldOf(child);
    var ed = makeEditor(child, grid, {
      newRowDefaults: function () {
        var d = {};
        if (st.selected && field) d[field] = st.selected.index;
        return d;
      }
    });
    if (!ed) gridMissing(pane);
    st.childPanes[child] = { el: pane, presets: presets };
    return st.childPanes[child];
  }

  async function showChild(masterId, child) {
    var st = masters[masterId];
    if (!st) return;
    var cp = ensureChildPane(masterId, child);
    st.activeChild = child;
    Object.keys(st.childPanes).forEach(function (k) { st.childPanes[k].el.classList.toggle('hidden', k !== child); });
    Array.prototype.forEach.call(st.strip.querySelectorAll('[data-child]'), function (b) {
      b.classList.toggle('primary', b.getAttribute('data-child') === child);
    });
    var ed = editors[child];
    if (!ed) return;
    var first = !ed.getApi();
    await ed.show();
    decorate(child, cp.presets, cp.el);
    if (first || st.selected) {
      var f = childFilter(masterId, child);
      var cur = ed.getParentFilter ? ed.getParentFilter() : null;
      if (f && (!cur || cur.value !== f.value || cur.field !== f.field)) ed.setParentFilter(f);
    }
  }

  // ---- Dragon form ----------------------------------------------------------------------------------------------------

  function buildDragonPane() {
    var pane = el('div', 'db-pane gd-pane gd-dragon hidden');
    pane.setAttribute('data-tab', 'dragon');
    var card = el('div', 'mir-card gd-dragon-card');
    var head = el('div', 'gd-dragon-head');
    head.appendChild(el('h3', 'db-pane-title', 'Dragon'));
    var meta = el('span', 'mir-muted db-pane-meta', '');
    head.appendChild(meta);
    var end = el('span', 'db-tools-end');
    var reload = button('Reload');
    var save = button('Save', 'primary');
    end.appendChild(reload);
    end.appendChild(save);
    head.appendChild(end);
    card.appendChild(head);
    var form = el('div', 'gd-dragon-form');
    card.appendChild(form);
    pane.appendChild(card);
    ui.panes.appendChild(pane);
    dragon = { el: pane, form: form, meta: meta, save: save, schema: null, row: null, inputs: [], dirty: false, loaded: false };
    reload.addEventListener('click', async function () {
      if (dragon.dirty && !(await portalConfirm('Reload the Dragon settings? Your unsaved changes are lost.', { title: 'Reload Dragon' }))) return;
      loadDragon();
    });
    save.addEventListener('click', function () { global.OpsUI.withBusy(save, saveDragon, 'Saving…'); });
    return { el: pane };
  }

  function setDragonDirty(on) {
    dragon.dirty = !!on;
    dragon.save.classList.toggle('db-has-pending', !!on);
    if (global.DbSave && global.DbSave.setDirty) global.DbSave.setDirty('dragon', !!on);
    updateBadge();
  }

  var DRAGON_GROUPS = [
    { label: 'General', test: /^(Enabled|MapFileName|MonsterName|BodyName)$/ },
    { label: 'Positions', test: /(Location|DropArea)/ },
    { label: 'Experience per level', test: /^Exps/ },
    { label: 'Current state (read-only)', test: /^(Level|Experience)$/ }
  ];

  async function loadDragon() {
    var form = dragon.form;
    form.innerHTML = '';
    form.appendChild(el('p', 'mir-muted', 'Loading…'));
    var schema = global.DbSchema ? await global.DbSchema.fetch('dragon') : null;
    var res = await global.OpsUI.fetchResult('/api/admin/db/dragon');
    form.innerHTML = '';
    if (!res.ok) { form.appendChild(el('p', 'mir-muted', 'Could not load the Dragon settings: ' + res.error)); return; }
    var body = res.data && Array.isArray(res.data.rows) ? res.data : (global.OpsUI.unwrap(res.data) || {});
    var row = Array.isArray(body.rows) && body.rows.length ? body.rows[0] : null;
    if (!row) { form.appendChild(el('p', 'mir-muted', 'The server returned no Dragon record.')); return; }
    dragon.schema = schema;
    dragon.row = row;
    dragon.inputs = [];
    dragon.loaded = true;
    renderDragonForm();
    setDragonDirty(false);
    dragon.meta.textContent = 'Single record' + (row.Enabled === false ? ' · disabled' : '');
  }

  var monsterNames = null;
  async function monsterSuggestions() {
    if (monsterNames) return monsterNames;
    monsterNames = [];
    try {
      var r = global.DbLookups ? await global.DbLookups.fetch('monsters') : null;
      var opts = r && (r.options || r) || [];
      if (Array.isArray(opts)) monsterNames = opts.map(function (o) { return String(o.label || '').replace(/^\d+ — /, ''); }).filter(Boolean);
    } catch (e) { monsterNames = []; }
    return monsterNames;
  }

  function renderDragonForm() {
    var form = dragon.form;
    var row = dragon.row;
    var schema = dragon.schema;
    var props = Object.create(null);
    (schema && schema.properties || []).forEach(function (p) { props[p.name] = p; });
    var keys = Object.keys(row).filter(function (k) { return k.indexOf('__') !== 0; });
    if (schema && global.DbSchema && global.DbSchema.orderKeys) keys = global.DbSchema.orderKeys(schema, keys);
    var groups = DRAGON_GROUPS.map(function (g) { return { label: g.label, test: g.test, keys: [] }; });
    var other = { label: 'Other', keys: [] };
    keys.forEach(function (k) {
      if (k === 'Index') return;
      var g = groups.filter(function (x) { return x.test.test(k); })[0] || other;
      g.keys.push(k);
    });
    groups.push(other);
    var listId = 'gdDragonMonsters';
    var dl = el('datalist');
    dl.id = listId;
    form.appendChild(dl);
    monsterSuggestions().then(function (names) {
      names.slice(0, 5000).forEach(function (n) { var o = el('option'); o.value = n; dl.appendChild(o); });
    });
    groups.forEach(function (g) {
      if (!g.keys.length) return;
      var fs = el('fieldset', 'gd-dragon-group');
      fs.appendChild(el('legend', null, g.label));
      var grid = el('div', 'fd-group-grid');
      g.keys.forEach(function (k) { grid.appendChild(dragonField(k, props[k], row[k], listId)); });
      fs.appendChild(grid);
      form.appendChild(fs);
    });
  }

  function dragonField(key, p, value, listId) {
    var wrap = el('label', 'fd-field');
    // With a schema, a key it does not describe is a display column; Level / Experience are the dragon's live progress.
    var ro = (dragon.schema ? (!p || !!p.readOnly) : false) || /^(Level|Experience)$/.test(key);
    var caption = el('span', 'fd-label', global.DbSchema ? global.DbSchema.headerName(key) : key);
    var kind = p ? p.kind : (typeof value === 'boolean' ? 'bool' : typeof value === 'number' ? 'number' : (value && typeof value === 'object' ? 'point' : 'string'));
    var input, read;
    if (kind === 'bool') {
      wrap.className = 'fd-field fd-field-bool';
      input = el('input');
      input.type = 'checkbox';
      input.checked = !!value;
      read = function () { return !!input.checked; };
      wrap.appendChild(input);
      wrap.appendChild(caption);
    } else if (kind === 'point' && value && typeof value === 'object') {
      wrap.appendChild(caption);
      var pw = el('span', 'fd-point-wrap');
      var x = el('input', 'fd-input fd-point'), y = el('input', 'fd-input fd-point');
      x.type = y.type = 'number';
      x.value = value.X != null ? value.X : 0;
      y.value = value.Y != null ? value.Y : 0;
      pw.appendChild(x); pw.appendChild(y);
      wrap.appendChild(pw);
      input = pw;
      [x, y].forEach(function (i) { i.disabled = ro; i.addEventListener('input', function () { setDragonDirty(true); }); });
      read = function () {
        var nx = Number(x.value), ny = Number(y.value);
        if (!isFinite(nx) || !isFinite(ny) || Math.floor(nx) !== nx || Math.floor(ny) !== ny) throw new Error(key + ': X and Y must be whole numbers.');
        return { X: nx, Y: ny };
      };
    } else if (kind === 'enum' && p && global.DbSchema) {
      wrap.appendChild(caption);
      input = el('select', 'fd-input');
      global.DbSchema.memberList(p).forEach(function (m) {
        var o = el('option', null, m.name);
        o.value = String(typeof value === 'number' ? m.value : m.name);
        input.appendChild(o);
      });
      input.value = String(value);
      read = function () { return typeof value === 'number' ? Number(input.value) : input.value; };
      wrap.appendChild(input);
    } else if (kind === 'number') {
      wrap.appendChild(caption);
      input = el('input', 'fd-input');
      input.type = 'number';
      input.value = value == null ? '' : String(value);
      read = function () {
        var r = global.DbSchema ? global.DbSchema.parseNumber(p ? p.type : 'Int64', input.value) : { ok: isFinite(Number(input.value)), value: Number(input.value), error: 'not a number' };
        if (!r.ok) throw new Error(key + ': ' + r.error + '.');
        return r.value;
      };
      wrap.appendChild(input);
    } else {
      wrap.appendChild(caption);
      input = el('input', 'fd-input');
      input.type = 'text';
      input.value = value == null ? '' : (typeof value === 'object' ? JSON.stringify(value) : String(value));
      if (/MonsterName|BodyName/.test(key)) input.setAttribute('list', listId);
      read = function () { return typeof value === 'object' && value !== null ? JSON.parse(input.value) : input.value; };
      wrap.appendChild(input);
    }
    if (input && input.tagName) {
      input.disabled = ro;
      input.addEventListener('input', function () { setDragonDirty(true); });
      input.addEventListener('change', function () { setDragonDirty(true); });
    }
    if (ro) wrap.classList.add('fd-readonly');
    if (!ro) dragon.inputs.push({ key: key, read: read });
    return wrap;
  }

  async function saveDragon() {
    if (!dragon || !dragon.row) return;
    var orig = dragon.row;
    var next = Object.assign({}, orig);
    var changes = [];
    try {
      dragon.inputs.forEach(function (i) {
        var v = i.read();
        if (JSON.stringify(v) !== JSON.stringify(orig[i.key])) { next[i.key] = v; changes.push({ field: i.key, before: orig[i.key], after: v }); }
      });
    } catch (e) {
      portalToast(e.message || String(e), 'error');
      return;
    }
    if (!changes.length) { portalToast('No changes to save.', 'info'); setDragonDirty(false); return; }
    var go = await modal({
      title: 'Save Dragon settings',
      confirmLabel: 'Save',
      reason: true,
      build: function (body) {
        var ul = el('ul', 'gd-change-list');
        changes.forEach(function (c) { ul.appendChild(el('li', 'gd-change-field', c.field + ': ' + fmtValue(c.before) + ' → ' + fmtValue(c.after))); });
        body.appendChild(ul);
        body.appendChild(el('p', 'mir-muted', 'The Dragon reads these settings when the world starts: restart the world to apply them.'));
      }
    });
    if (!go) return;
    var res = await putRows('dragon', [next], { reason: go.reason });
    if (!res) return;
    if (res.applied <= 0) {
      if (res.validation.length || res.conflicts.length) await showIssues('Dragon settings not saved', res);
      else portalToast(res.message || 'Nothing was applied.', 'error');
      return;
    }
    var saved = await saveDatabase(true);
    portalToast(saved ? 'Dragon settings saved. Restart the world to apply them.' : 'Applied to the editor copy, but writing the database failed. Use "Save to disk" in the banner.', saved ? 'ok' : 'error');
    afterServerChange();
    setDragonDirty(false);
    loadDragon();
  }

  // ---- tabs -----------------------------------------------------------------------------------------------------------

  function ensurePane(tabId) {
    if (panes[tabId]) return panes[tabId];
    var tab = tabById[tabId];
    if (!tab) return null;
    panes[tabId] = tab.kind === 'master' ? buildMasterPane(tab) : tab.kind === 'form' ? buildDragonPane() : buildGridPane(tab);
    return panes[tabId];
  }

  function renderTabs() {
    ui.tabs.innerHTML = '';
    TABS.forEach(function (t) {
      var b = button('', 'gd-tab');
      b.setAttribute('data-tab', t.id);
      b.appendChild(document.createTextNode(t.label));
      var count = el('span', 'gd-tab-count');
      b.appendChild(count);
      b.addEventListener('click', function () {
        if (currentTab === t.id) return;
        showTab(t.id);
        global.portalSetHashParams({ table: t.id, index: null }, false);
      });
      ui.tabs.appendChild(b);
    });
    renderTabCounts();
  }

  function renderTabCounts() {
    if (!ui.tabs) return;
    Array.prototype.forEach.call(ui.tabs.querySelectorAll('[data-tab]'), function (b) {
      var id = b.getAttribute('data-tab');
      var info = tablesInfo[id];
      var c = b.querySelector('.gd-tab-count');
      if (c) c.textContent = info && typeof info.count === 'number' && id !== 'dragon' ? ' ' + info.count.toLocaleString() : '';
      b.classList.toggle('primary', id === currentTab);
    });
  }

  async function showTab(tabId) {
    if (!tabById[tabId]) tabId = 'items';
    var p = ensurePane(tabId);
    currentTab = tabId;
    Object.keys(panes).forEach(function (k) { panes[k].el.classList.toggle('hidden', k !== tabId); });
    renderTabCounts();
    if (global.portalSyncNav) global.portalSyncNav(SECTION, tabId);
    try { global.localStorage && global.localStorage.setItem('mircraft_gamedata_tab', tabId); } catch (e) { /* storage off */ }
    var tab = tabById[tabId];
    if (tab.kind === 'form') {
      if (!dragon.loaded) await loadDragon();
      return;
    }
    var ed = editors[tabId];
    if (ed) {
      await ed.show();
      decorate(tabId, p.presets, p.el);
    }
    if (tab.kind === 'master') {
      var st = masters[tabId];
      await showChild(tabId, st.activeChild || CHILDREN[tabId][0].endpoint);
    }
  }

  /** Open a table (its master tab for a child table); opts: { select: index, filter: { field, value, label } }. */
  async function open(endpoint, opts) {
    endpoint = String(endpoint || '').toLowerCase();
    opts = opts || {};
    var tabId = tabById[endpoint] ? endpoint : childOwner[endpoint];
    if (!tabId) { portalToast('Unknown game data table: ' + endpoint, 'error'); return; }
    // A tab already on screen is not reloaded (show() would refresh its rows); a link only selects / filters in it.
    var tabEd = editors[tabId];
    var isChild = endpoint !== tabId;
    if (currentTab !== tabId || (tabById[tabId].kind !== 'form' && !(tabEd && tabEd.getApi()))) {
      if (isChild) {
        ensurePane(tabId);
        if (masters[tabId]) masters[tabId].activeChild = endpoint;   // the master tab opens on this child directly
      }
      await showTab(tabId);
    }
    if (isChild) {
      var mst = masters[tabId];
      var ced = editors[endpoint];
      if (!mst || mst.activeChild !== endpoint || !(ced && ced.getApi())) await showChild(tabId, endpoint);
    }
    var ed = editors[endpoint];
    if (!ed || !ed.getApi()) return;
    var masterEd = isChild ? editors[tabId] : null;

    if (opts.filter && opts.filter.field) {
      if (isChild && masterEd && masterEd.getApi()) {
        var pnode = masterEd.getApi().getRowNode(String(opts.filter.value));
        if (pnode && pnode.data) { selectParent(tabId, pnode.data); masterEd.focusRow(opts.filter.value); }
        else ed.setParentFilter(opts.filter);
      } else {
        ed.setParentFilter(opts.filter);
      }
    }
    if (opts.select != null && Number(opts.select) !== 0) {
      var idx = Number(opts.select);
      if (isChild && masterEd && masterEd.getApi()) {
        var node = ed.getApi().getRowNode(String(idx));
        var pf = parentFieldOf(endpoint);
        if (node && node.data && pf) {
          var parent = masterEd.getApi().getRowNode(String(node.data[pf]));
          if (parent && parent.data) { selectParent(tabId, parent.data); masterEd.focusRow(parent.data.Index); }
        }
      }
      if (ed.focusRow(idx) && tabById[endpoint] && tabById[endpoint].kind === 'master') {
        var mnode = ed.getApi().getRowNode(String(idx));
        if (mnode && mnode.data) selectParent(endpoint, mnode.data);
      }
    }
  }

  // ---- banner (unsaved Edit changes + pending reboot) -----------------------------------------------------------------

  function pendingReboot() {
    var st = global.AdminStatus && global.AdminStatus.last ? global.AdminStatus.last() : null;
    var p = st && st.pendingReboot;
    return Array.isArray(p) ? p : [];
  }

  function renderBanner() {
    if (!ui.banner) return;
    var unsaved = unsavedCount(dbStatus);
    var pend = pendingReboot();
    var st = global.AdminStatus && global.AdminStatus.last ? global.AdminStatus.last() : null;
    var world = st ? String(st.world || '') : '';
    var stale = !!((dbStatus && dbStatus.systemDataStale) || (st && st.systemDataStale));
    var lastSaved = dbStatus ? dbStatus.lastSavedUtc : null;
    var sig = JSON.stringify([unsaved, pend.map(function (p) { return [p.area, p.description, p.admin, p.utc]; }), world, stale, lastSaved]);
    if (sig === bannerSig) return;
    bannerSig = sig;
    var b = ui.banner;
    b.innerHTML = '';
    if (!unsaved && !pend.length && !stale) { b.classList.add('hidden'); return; }
    b.classList.remove('hidden');

    if (unsaved) {
      var row = el('div', 'gd-banner-row gd-banner-unsaved');
      row.appendChild(el('span', 'ops-pill ops-pill-warn', 'Unsaved'));
      row.appendChild(el('span', 'gd-banner-text', 'The editor copy of the game data has ' + (typeof dbStatus.unsavedChanges === 'number' ? unsaved + ' change(s)' : 'changes') +
        ' that are not written to disk yet.'));
      var acts = el('span', 'gd-banner-actions');
      var saveBtn = button('Save to disk', 'primary', 'Write the editor copy to Server.MirDB (backed up first)');
      saveBtn.addEventListener('click', function () {
        global.OpsUI.withBusy(saveBtn, async function () { if (await saveDatabase(false)) afterServerChange(); }, 'Saving…');
      });
      var discard = button('Discard unsaved', 'danger', 'Reload the editor copy from disk; unsaved changes are lost');
      discard.addEventListener('click', function () { global.OpsUI.withBusy(discard, discardUnsaved, 'Discarding…'); });
      acts.appendChild(saveBtn);
      acts.appendChild(discard);
      row.appendChild(acts);
      b.appendChild(row);
    }

    if (pend.length) {
      var prow = el('div', 'gd-banner-row gd-banner-pending');
      prow.appendChild(el('span', 'ops-pill ops-pill-warn', 'Restart needed'));
      var det = el('details', 'gd-banner-text');
      det.appendChild(el('summary', null, pend.length + ' saved change(s) take effect after the next world restart'));
      var ul = el('ul', 'gd-pending-list');
      pend.slice().reverse().slice(0, 50).forEach(function (p) {
        var li = el('li');
        if (p.area) li.appendChild(el('span', 'ops-row-tag', p.area));
        li.appendChild(document.createTextNode(' ' + (p.description || '')));
        var who = [p.admin, p.utc ? global.OpsUI.ago(p.utc) : ''].filter(Boolean).join(' · ');
        if (who) li.appendChild(el('span', 'mir-muted', ' — ' + who));
        ul.appendChild(li);
      });
      det.appendChild(ul);
      prow.appendChild(det);
      var pacts = el('span', 'gd-banner-actions');
      var reboot = button('Reboot world', 'danger', world === 'running' ? 'Restart the world so it loads the saved game data' : 'The world is not running');
      reboot.disabled = world !== 'running';
      reboot.addEventListener('click', function () { global.OpsUI.withBusy(reboot, rebootWorld, 'Rebooting…'); });
      pacts.appendChild(reboot);
      prow.appendChild(pacts);
      b.appendChild(prow);
    } else if (stale) {
      var srow = el('div', 'gd-banner-row');
      srow.appendChild(el('span', 'ops-pill', 'Note'));
      srow.appendChild(el('span', 'gd-banner-text mir-muted', 'Game data was saved since the world last loaded it: the running world keeps its own copy until it restarts.'));
      b.appendChild(srow);
    }
    if (lastSaved && global.OpsUI.toDate(lastSaved)) {
      b.appendChild(el('div', 'gd-banner-foot mir-muted', 'Database last written ' + global.OpsUI.ago(lastSaved) + ' (' + global.OpsUI.fmtDate(lastSaved) + ').'));
    }
  }

  function localPendingCount() {
    var n = 0;
    Object.keys(editors).forEach(function (k) {
      var ed = editors[k];
      if (ed && ed.getApi && ed.getApi()) n += ed.pendingCount();
    });
    if (dragon && dragon.dirty) n++;
    return n;
  }

  function updateBadge() {
    var badge = document.getElementById('navGameDataBadge');
    if (!badge) return;
    var local = localPendingCount();
    var unsaved = unsavedCount(dbStatus);
    var n = local + unsaved;
    badge.classList.toggle('hidden', n <= 0);
    badge.textContent = String(n);
    var tip = [];
    if (local) tip.push(local + ' edited row(s) not applied yet');
    if (unsaved) tip.push('unsaved changes in the editor copy');
    badge.title = tip.join('; ');
  }

  async function rebootWorld() {
    var dirtyGrids = localPendingCount();
    var ok = await portalConfirm('Reboot the world now? Every player is disconnected while the world restarts and loads the saved game data.' +
      (unsavedCount(dbStatus) ? ' The editor copy has unsaved changes: save them first or they are not part of the restart.' : '') +
      (dirtyGrids ? ' ' + dirtyGrids + ' edited row(s) in the grids are not applied yet.' : ''),
    { title: 'Reboot world', confirmLabel: 'Reboot', danger: true });
    if (!ok) return;
    var j = await global.OpsUI.post('/api/admin/server/reboot', {});
    if (j.__ok && global.AdminStatus) setTimeout(function () { global.AdminStatus.refresh(); }, 1500);
  }

  async function discardUnsaved() {
    var ok = await portalConfirm('Discard every unsaved change of the editor copy and reload it from disk? The open game data grids are reloaded too; edits not applied yet are lost.',
      { title: 'Discard unsaved changes', confirmLabel: 'Discard', danger: true });
    if (!ok) return;
    var j = await global.OpsUI.post('/api/admin/database/reload-edit', {});
    if (!j.__ok) return;
    Object.keys(editors).forEach(function (k) {
      var ed = editors[k];
      if (ed && ed.getApi && ed.getApi()) ed.load();
    });
    if (dragon && dragon.loaded) { setDragonDirty(false); loadDragon(); }
    if (global.DbLookups && global.DbLookups.clearCache) global.DbLookups.clearCache();
    afterServerChange();
  }

  // ---- section lifecycle ----------------------------------------------------------------------------------------------

  function buildSection(el0) {
    root = el0;
    root.innerHTML = '';
    var wrap = el('div', 'gd-wrap');
    var head = el('div', 'gd-head');
    head.appendChild(el('h2', 'gd-group-title', 'Game Data'));
    head.appendChild(el('span', 'mir-muted gd-head-note', 'Edits go to the editor copy of the database and are saved to disk; the running world picks them up after a restart.'));
    wrap.appendChild(head);
    ui.banner = el('div', 'gd-banner hidden');
    ui.banner.setAttribute('role', 'status');
    wrap.appendChild(ui.banner);
    var strip = el('div', 'gd-strip');
    ui.tabs = el('div', 'database-tabs gd-tabs gd-main-tabs');
    strip.appendChild(ui.tabs);
    wrap.appendChild(strip);
    ui.panes = el('div', 'gd-panes');
    ui.panes.id = 'gdPanes';
    wrap.appendChild(ui.panes);
    root.appendChild(wrap);
    renderTabs();
  }

  function initialTab() {
    var t = null;
    try { t = global.localStorage && global.localStorage.getItem('mircraft_gamedata_tab'); } catch (e) { t = null; }
    return tabById[t] ? t : 'items';
  }

  function hashTable(params) {
    var t = params && (params.table || params.dbTab);
    return t ? String(t).toLowerCase() : null;
  }

  var hooked = false;
  function hookGlobals() {
    if (hooked) return;
    hooked = true;
    if (global.AdminStatus && global.AdminStatus.onChange) {
      global.AdminStatus.onChange(function () { if (root) renderBanner(); updateBadge(); syncLiveToggle(); });
    }
    if (global.AdminSections && global.AdminSections.poll) {
      // Local edit counts (badge) while the section is visible; the editor copy's unsaved state every 15 s.
      global.AdminSections.poll(SECTION, function () { updateBadge(); }, 1500);
      global.AdminSections.poll(SECTION, refreshDbStatus, 15000);
    }
  }

  global.AdminSections.register(SECTION, {
    load: function (el0) {
      buildSection(el0);
      hookGlobals();
      return fetchTables(false);
    },
    show: function (el0, params) {
      refreshDbStatus();
      renderBanner();
      if (hashTable(params)) return;   // the deep link opens the table
      if (!currentTab) return showTab(initialTab());
      var ed = editors[currentTab];
      if (ed && ed.getApi()) return ed.show();
    }
  });

  global.portalRegisterDeepLink(SECTION, function (params) {
    var table = hashTable(params);
    if (!table) { if (!currentTab) showTab(initialTab()); return; }
    var opts = {};
    var idx = params.index != null && params.index !== '' ? params.index : params.row;
    if (idx != null && idx !== '' && isFinite(Number(idx))) opts.select = Number(idx);
    var pf = parentFieldOf(table);
    if (pf && params[pf] != null && isFinite(Number(params[pf]))) {
      opts.filter = { field: pf, value: Number(params[pf]), label: (childOwner[table] ? tabById[childOwner[table]].parentLabel : pf) + ' #' + params[pf] };
    }
    open(table, opts);
  });

  // ---- globals --------------------------------------------------------------------------------------------------------

  function viaSection(endpoint, params) {
    if (global.AdminSections.current() === SECTION && global.AdminSections.isLoaded(SECTION)) return false;
    global.portalOpenDeepLink(SECTION, Object.assign({ table: endpoint }, params || {}));
    return true;
  }

  global.DbNav = {
    goto: function (endpoint, index) {
      endpoint = String(endpoint || '').toLowerCase();
      if (viaSection(endpoint, { index: index })) return;
      global.portalSetHashParams({ table: endpoint, index: index }, false);
      open(endpoint, { select: index });
    },
    openChild: function (childEndpoint, field, value, lbl) {
      childEndpoint = String(childEndpoint || '').toLowerCase();
      var p = { };
      p[field] = value;
      if (viaSection(childEndpoint, p)) return;
      global.portalSetHashParams(Object.assign({ table: childEndpoint, index: null }, p), false);
      open(childEndpoint, { filter: { field: field, value: Number(value), label: lbl } });
    }
  };

  global.GameData = {
    open: open,
    show: showTab,
    editor: function (endpoint) { return editors[endpoint] || null; },
    current: function () { return currentTab; },
    refreshStatus: refreshDbStatus
  };
})(window);
