/**
 * MirCraft Admin shell: shared fetch (cookies + CSRF), auth-failure redirects, grid persistence (DbSave), the section
 * registry (AdminSections), hash routing with deep links, sidebar highlight and breadcrumbs.
 *
 * Ported from the WoOOLcraft admin shell. Section modules register with AdminSections instead of window.__loadX hooks.
 */
(function (global) {
  'use strict';

  // Section id -> { label, group }. Mirrors the sidebar in index.html (contract §3.3).
  var SECTIONS = {
    dashboard: { label: 'Dashboard', group: 'Monitoring' },
    server: { label: 'Server Control', group: 'Monitoring' },
    'players-online': { label: 'Players Online', group: 'Monitoring' },
    security: { label: 'Security', group: 'Monitoring' },
    gm: { label: 'GM Console', group: 'Operations' },
    broadcast: { label: 'Broadcast', group: 'Operations' },
    chatlog: { label: 'Chat Log', group: 'Operations' },
    maintenance: { label: 'Maintenance', group: 'Operations' },
    auditlog: { label: 'Audit Trail', group: 'Operations' },
    accounts: { label: 'Accounts', group: 'Players' },
    characters: { label: 'Characters', group: 'Players' },
    guilds: { label: 'Guilds', group: 'Players' },
    market: { label: 'Market', group: 'Players' },
    namelists: { label: 'Name Lists', group: 'Players' },
    gamedata: { label: 'Game Data', group: 'Game Data' },
    scripts: { label: 'Scripts & Files', group: 'Game Data' },
    backups: { label: 'Backups & History', group: 'Game Data' },
    config: { label: 'Server Config', group: 'Config' },
    balance: { label: 'Balance', group: 'Config' },
    systems: { label: 'Systems', group: 'Config' }
  };
  var DEFAULT_SECTION = 'dashboard';

  // Sub-tab labels for the breadcrumb (Game Data sub-nav). Unknown tabs are title-cased.
  var TAB_LABELS = {
    items: 'Items', monsters: 'Monsters', magic: 'Magic', npcs: 'NPCs', quests: 'Quests', maps: 'Maps',
    gameshop: 'Game Shop', conquests: 'Conquests', dragon: 'Dragon', respawntick: 'Respawn Tick'
  };

  function sectionLabel(id) { return SECTIONS[id] ? SECTIONS[id].label : (id || ''); }

  // ---- Cookies, CSRF, auth failures ---------------------------------------------------------------------------------

  function getCookie(name) {
    var m = document.cookie.match(new RegExp('(?:^|; )' + name.replace(/[.$?*|{}()[\]\\/+^]/g, '\\$&') + '=([^;]*)'));
    if (!m) return '';
    try { return decodeURIComponent(m[1]); } catch (e) { return m[1]; }
  }

  var CSRF_COOKIE = 'MirCraftAdminCsrf';
  var CSRF_HEADER = 'X-MirCraft-CSRF';

  var csrfFallback = '';   // the token /api/auth/me reported (used only when the cookie cannot be read)
  function csrfToken() { return getCookie(CSRF_COOKIE) || csrfFallback; }
  global.portalCsrfToken = csrfToken;
  global.portalSetCsrfToken = function (t) { csrfFallback = t ? String(t) : ''; };

  // Where an expired / missing session goes. The current hash rides along (?next=) and login.html comes back to it.
  function loginUrl(hash, denied) {
    var h = String(hash || '');
    if (h && h.charAt(0) !== '#') h = '#' + h;
    var q = [];
    if (denied) q.push('denied=1');
    if (h.length > 1) q.push('next=' + encodeURIComponent(h));
    return '/admin/login.html' + (q.length ? '?' + q.join('&') : '');
  }
  global.portalLoginUrl = loginUrl;

  var redirecting = false;
  function goLogin(denied) {
    if (redirecting) return;
    redirecting = true;
    // Unsaved grid edits still get the browser's "leave site?" prompt (beforeunload below): the operator can stay and copy them.
    var target = loginUrl(global.location ? global.location.hash : '', denied);
    if (global.location) global.location.href = target;
    // If the operator chose to stay (unsaved edits), a later 401 may ask again.
    setTimeout(function () { redirecting = false; }, 3000);
  }
  global.portalGoLogin = goLogin;

  /**
   * 401 (no / expired session) -> sign in again, then come back to the same hash; 403 "Admin privileges required" (the
   * account lost its admin flag or was banned) -> login?denied=1. Other 403s (CSRF, refused actions) are left to the caller.
   * Returns true when it is redirecting.
   */
  async function handleAuthFailure(r, url) {
    if (!r || (r.status !== 401 && r.status !== 403)) return false;
    if (String(url || '').indexOf('/api/auth/') === 0) return false;
    if (r.status === 401) { goLogin(false); return true; }
    var body = null;
    try { body = typeof r.clone === 'function' ? await r.clone().json() : null; } catch (e) { body = null; }
    if (body && /admin privileges required/i.test(String(body.message || body.error || ''))) { goLogin(true); return true; }
    return false;
  }
  global.portalHandleAuthFailure = handleAuthFailure;

  /**
   * fetch() with the session cookie, the CSRF header on mutating /api/ calls, and the 401/403 redirects.
   * A plain-object / array body is JSON-encoded (Content-Type set); a string body gets Content-Type application/json unless
   * the caller set one. Never throws on an HTTP status: the caller gets the Response.
   */
  global.portalApi = async function portalApi(url, opts) {
    opts = opts || {};
    opts.credentials = 'include';
    var method = (opts.method || 'GET').toUpperCase();
    var mut = method === 'POST' || method === 'PUT' || method === 'PATCH' || method === 'DELETE';
    opts.headers = Object.assign({}, opts.headers || {});
    var hasType = Object.keys(opts.headers).some(function (k) { return k.toLowerCase() === 'content-type'; });
    var b = opts.body;
    var isRaw = b == null || typeof b === 'string' ||
      (typeof FormData !== 'undefined' && b instanceof FormData) ||
      (typeof Blob !== 'undefined' && b instanceof Blob) ||
      (typeof ArrayBuffer !== 'undefined' && (b instanceof ArrayBuffer || ArrayBuffer.isView(b))) ||
      (typeof URLSearchParams !== 'undefined' && b instanceof URLSearchParams);
    if (!isRaw) {
      opts.body = JSON.stringify(b);
      if (!hasType) opts.headers['Content-Type'] = 'application/json';
    } else if (typeof b === 'string' && !hasType) {
      opts.headers['Content-Type'] = 'application/json';
    }
    if (mut && String(url).indexOf('/api/') === 0) {
      opts.headers[CSRF_HEADER] = csrfToken();
    }
    var r = await fetch(url, opts);
    await handleAuthFailure(r, url);
    return r;
  };

  // ---- DbSave: grid persistence + per-grid dirty registry -----------------------------------------------------------

  /**
   * PUT rows then POST database/save, interpreting { ok, applied, conflicts, validation, ignoredFields, pendingReboot }.
   * Surfaces conflicts and validation errors via toasts so a partial / blocked save is never mistaken for a clean one.
   * Returns { ok, applied, clean, conflicts, validation, ignoredFields, pendingReboot }.
   *   ok    — at least the disk save succeeded (some rows applied)
   *   clean — every submitted row applied with no conflict / validation skips
   */
  global.DbSave = {
    persist: async function (endpoint, rows, opts) {
      opts = opts || {};
      var body = { rows: rows };
      if (opts.reason) body.reason = opts.reason;
      var put = await portalApi('/api/admin/db/' + encodeURIComponent(endpoint), {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
      var j = await put.json().catch(function () { return {}; });

      if (put.status === 503 || put.status === 504 || put.status === 413 || put.status === 400 || put.status >= 500) {
        portalToast(j.message || j.error || ('Save failed (HTTP ' + put.status + ').'), 'error');
        return { ok: false, applied: 0 };
      }

      var conflicts = j.conflicts || [];
      var validation = j.validation || [];
      var ignored = j.ignoredFields || [];
      var applied = j.applied || 0;

      if (validation.length) {
        var vmsg = validation.slice(0, 4).map(function (v) { return '#' + v.index + ' ' + v.field + ': ' + v.message; }).join('; ');
        portalToast('Validation failed — ' + vmsg + (validation.length > 4 ? ' …' : ''), 'error');
      }
      if (conflicts.length) {
        var cidx = conflicts.map(function (c) { return '#' + c.index; }).join(', ');
        portalToast('Conflict: row(s) ' + cidx + ' changed since you loaded them. Reload, then re-apply your edits.', 'error');
      }
      if (ignored.length) {
        portalToast('Ignored unknown field(s): ' + ignored.join(', '), 'info');
      }

      if (applied <= 0) {
        if (!validation.length && !conflicts.length) portalToast(j.message || j.error || 'Nothing was applied.', 'error');
        return { ok: false, applied: 0, conflicts: conflicts, validation: validation, ignoredFields: ignored };
      }

      var save = await portalApi('/api/admin/database/save', { method: 'POST' });
      var js = await save.json().catch(function () { return {}; });
      if (!save.ok || js.ok === false) {
        var smsg = js.message || js.error || 'Save failed';
        if (js.blockers && js.blockers.length) smsg += ' - ' + js.blockers.join('; ');
        smsg += '. Your ' + applied + ' edit(s) were applied to the editor copy but not yet written to disk.';
        portalToast(smsg, 'error');
        return { ok: false, applied: applied, conflicts: conflicts, validation: validation, ignoredFields: ignored };
      }

      var clean = conflicts.length === 0 && validation.length === 0;
      var reboot = j.pendingReboot !== false;
      portalToast((clean
        ? ('Applied ' + applied + ' row(s) and saved the database.')
        : ('Applied ' + applied + ' row(s) and saved; ' + (conflicts.length + validation.length) + ' skipped.')) +
        (reboot ? '\nChanges take effect after the next world restart.' : ''),
        clean ? 'ok' : 'info');
      if (global.AdminStatus && typeof global.AdminStatus.refresh === 'function') global.AdminStatus.refresh();
      return { ok: true, applied: applied, clean: clean, conflicts: conflicts, validation: validation, ignoredFields: ignored, pendingReboot: reboot };
    }
  };

  // Unsaved-edit state per grid (key = the grid's endpoint or a custom dirtyKey), so one grid's save never clears another
  // grid's edits. window.__gridDirty stays as a compatibility alias.
  var dirtyKeys = Object.create(null);
  var dirtySeq = 0;   // bumps on every new edit
  var ackSeq = 0;     // the dirtySeq the user last answered "leave anyway" for
  global.DbSave.setDirty = function (key, on) {
    key = key || 'legacy';
    if (on) { dirtyKeys[key] = true; dirtySeq++; } else delete dirtyKeys[key];
  };
  global.DbSave.isDirty = function (key) { return !!dirtyKeys[key || 'legacy']; };
  global.DbSave.anyDirty = function () { for (var k in dirtyKeys) return true; return false; };
  global.DbSave.dirtyKeys = function () { return Object.keys(dirtyKeys); };
  Object.defineProperty(global, '__gridDirty', {
    configurable: true,
    enumerable: true,
    get: function () { return global.DbSave.anyDirty(); },
    set: function (v) { global.DbSave.setDirty('legacy', !!v); }
  });

  // The row drawer tells its grid which fields it changed (rowNode.setData fires no cellValueChanged).
  var editListeners = Object.create(null);
  global.DbSave.onEdit = function (key, fn) {
    if (!key) return;
    if (typeof fn === 'function') editListeners[key] = fn; else delete editListeners[key];
  };
  global.DbSave.noteEdit = function (key, index, changes) {
    global.DbSave.setDirty(key, true);
    if (editListeners[key]) editListeners[key](index, changes || []);
  };

  // A table was saved: grids that summarise it refresh.
  var savedListeners = [];
  global.DbSave.onSaved = function (fn) { if (typeof fn === 'function') savedListeners.push(fn); };
  global.DbSave.offSaved = function (fn) { var i = savedListeners.indexOf(fn); if (i >= 0) savedListeners.splice(i, 1); };
  global.DbSave.notifySaved = function (endpoint) { savedListeners.slice().forEach(function (fn) { try { fn(endpoint); } catch (e) { /* a listener never breaks a save */ } }); };

  // Endpoint / tab id -> label for titles and breadcrumbs.
  global.__dbTabLabel = function (tab) {
    if (!tab) return '';
    if (global.DbRegistry && typeof global.DbRegistry.label === 'function') return global.DbRegistry.label(tab);
    return TAB_LABELS[tab] || (tab.charAt(0).toUpperCase() + tab.slice(1));
  };

  function setBreadcrumb(id, dbTab) {
    var el = document.getElementById('adminBreadcrumb');
    if (!el) return;
    var info = SECTIONS[id];
    var parts = [];
    if (info && info.group && info.group !== info.label) parts.push(info.group);
    parts.push(sectionLabel(id));
    if (dbTab) parts.push(global.__dbTabLabel(dbTab));
    el.textContent = parts.join(' › ');
  }

  // ---- AdminSections: lazy load / show / hide per section (contract §3.2) -------------------------------------------

  var registry = Object.create(null);   // id -> { def, loaded, loading (Promise|null) }
  var started = false;                  // app.js starts the registry after the auth gate
  var activeId = null;                  // the section whose show() ran last (hide() goes to it)
  var activateSeq = 0;

  function sectionEl(id) { return document.getElementById('section-' + id); }

  function reportError(id, phase, e) {
    if (global.console) console.error('[AdminSections] ' + id + '.' + phase, e);
    if (typeof global.portalToast === 'function') global.portalToast(sectionLabel(id) + ': failed to ' + phase + ' (' + ((e && e.message) || e) + ')', 'error');
  }

  function callHide(id) {
    var entry = registry[id];
    if (!entry || !entry.loaded || typeof entry.def.hide !== 'function') return;
    try { entry.def.hide(sectionEl(id)); } catch (e) { reportError(id, 'hide', e); }
  }

  // Runs load (first time only) then show for the section, then its deep links. Returns a Promise.
  function activate(id, params) {
    var seq = ++activateSeq;
    params = params || parseHash() || {};
    var entry = registry[id];
    var el = sectionEl(id);
    activeId = id;
    var chain = Promise.resolve();
    if (entry && el) {
      if (!entry.loaded) {
        if (!entry.loading) {
          entry.loading = Promise.resolve().then(function () {
            return typeof entry.def.load === 'function' ? entry.def.load(el, params) : null;
          }).then(function () { entry.loaded = true; entry.loading = null; return true; },
            function (e) { entry.loading = null; reportError(id, 'load', e); return false; });   // next show retries the load
        }
        chain = entry.loading;
      } else {
        chain = Promise.resolve(true);
      }
      chain = chain.then(function (ok) {
        if (ok === false) return;
        if (seq !== activateSeq || activeId !== id) return;   // the user moved on while loading
        if (typeof entry.def.show === 'function') {
          try { return entry.def.show(el, params); } catch (e) { reportError(id, 'show', e); }
        }
      }).catch(function (e) { reportError(id, 'show', e); });
    }
    return chain.then(function () {
      if (seq !== activateSeq || activeId !== id) return;
      global.portalDispatchDeepLink(id, params);
    });
  }

  var pollers = [];   // { id, fn, ms, timer, busy }

  function sectionIsVisible(id) {
    var el = sectionEl(id);
    return !!(el && !el.classList.contains('hidden') && (!document.visibilityState || document.visibilityState === 'visible'));
  }

  function runPoller(p) {
    if (p.busy || !started || !sectionIsVisible(p.id)) return;
    p.busy = true;
    var done = function () { p.busy = false; };
    try {
      var r = p.fn();
      if (r && typeof r.then === 'function') r.then(done, function (e) { done(); if (global.console) console.warn('[poll ' + p.id + ']', e); });
      else done();
    } catch (e) { done(); if (global.console) console.warn('[poll ' + p.id + ']', e); }
  }

  global.AdminSections = {
    /**
     * register(id, { load(el, params), show(el, params), hide(el) }) — all optional. load runs once on first show (may
     * return a Promise; show waits for it); show runs on every show; hide when leaving the section. el is
     * document.getElementById('section-<id>'). params = the hash parameters ({section, dbTab, ...}).
     */
    register: function (id, def) {
      if (!id || !def) return;
      var had = registry[id];
      registry[id] = { def: def, loaded: false, loading: null };
      if (had && had.loaded && typeof had.def.hide === 'function') { try { had.def.hide(sectionEl(id)); } catch (e) { /* replaced */ } }
      // Registered after the section is already on screen (late script): bring it up now.
      if (started && currentSectionId() === id) activate(id);
    },
    has: function (id) { return !!registry[id]; },
    isLoaded: function (id) { return !!(registry[id] && registry[id].loaded); },
    /** The id of the visible section (or null). */
    current: function () { return currentSectionId(); },
    /** True while the section is on screen and the browser tab is visible. */
    isVisible: sectionIsVisible,
    /** Section ids with labels and groups (sidebar order). */
    list: function () { return Object.keys(SECTIONS).map(function (k) { return { id: k, label: SECTIONS[k].label, group: SECTIONS[k].group }; }); },
    label: sectionLabel,
    /** Re-runs show() for the visible section (e.g. after an action elsewhere changed its data). */
    refresh: function (id) {
      id = id || currentSectionId();
      if (!started || !id || id !== currentSectionId()) return Promise.resolve();
      return activate(id);
    },
    /**
     * poll(id, fn, ms) -> stop(): calls fn every ms only while section `id` is visible and the browser tab is visible; also
     * once when the tab becomes visible again. A fn returning a Promise is never overlapped with itself.
     */
    poll: function (id, fn, ms) {
      var p = { id: id, fn: fn, ms: Math.max(500, ms || 5000), timer: null, busy: false };
      p.timer = setInterval(function () { runPoller(p); }, p.ms);
      pollers.push(p);
      return function stop() {
        clearInterval(p.timer);
        var i = pollers.indexOf(p);
        if (i >= 0) pollers.splice(i, 1);
      };
    },
    /** app.js calls this once the session is verified: the visible section loads (or the default one is shown). */
    start: function () {
      if (started) return;
      started = true;
      var params = parseHash();
      var id = params && params.section && sectionEl(params.section) ? params.section : null;
      routedHash = location.hash || '';
      if (id) {
        if (currentSectionId() !== id) showSectionDom(id, params.dbTab);
        activate(id, params);
      } else {
        global.portalShowSection(DEFAULT_SECTION, null, true);
      }
    },
    started: function () { return started; },
    /** Renders the "<Label> — coming soon" placeholder card into el (used by the section stubs). */
    placeholder: function (el, id) {
      if (!el) return;
      var card = document.createElement('div');
      card.className = 'mir-card admin-stub-card';
      var h = document.createElement('h2');
      h.textContent = sectionLabel(id) + ' — coming soon';
      var p = document.createElement('p');
      p.className = 'mir-muted admin-stub-note';
      p.textContent = 'This section has not been built yet.';
      card.appendChild(h);
      card.appendChild(p);
      el.innerHTML = '';
      el.appendChild(card);
    }
  };

  // ---- Hash: query parameters, routing and the deep-link registry ---------------------------------------------------

  function safeDecode(s) {
    try { return decodeURIComponent(String(s).replace(/\+/g, ' ')); } catch (e) { return String(s); }
  }

  /** All key/values of a hash ("#a=1&b=x%20y" -> { a: '1', b: 'x y' }); a value may itself contain '='. */
  function hashParamsOf(hash) {
    var h = String(hash || '').replace(/^#/, '');
    var out = {};
    if (!h) return out;
    h.split('&').forEach(function (p) {
      if (!p) return;
      var i = p.indexOf('=');
      var k = safeDecode(i < 0 ? p : p.slice(0, i));
      if (k) out[k] = i < 0 ? '' : safeDecode(p.slice(i + 1));
    });
    return out;
  }
  global.portalHashParamsOf = hashParamsOf;

  /** The hash text for a params object: section, dbTab first, then the rest in their order; empty values are left out. */
  function buildHash(params) {
    var keys = Object.keys(params || {});
    var lead = ['section', 'dbTab'];
    var ordered = lead.filter(function (k) { return keys.indexOf(k) >= 0; })
      .concat(keys.filter(function (k) { return lead.indexOf(k) < 0; }));
    return ordered.filter(function (k) {
      var v = params[k];
      return v != null && v !== '';
    }).map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(String(params[k])); }).join('&');
  }
  global.portalBuildHash = buildHash;

  function parseHash() {
    var h = (location.hash || '').replace(/^#/, '');
    if (!h) return null;
    return hashParamsOf(h);
  }
  global.portalParseHash = parseHash;

  // The hash the page last routed to or wrote itself: a hashchange to it is not a navigation.
  var routedHash = null;

  function writeHash(h, replace) {
    var full = '#' + h;
    if (location.hash === full) { routedHash = full; return; }
    routedHash = full;
    // pushState / replaceState do not fire hashchange, so writing the hash never re-routes the page; Back / Forward do.
    if (global.history && typeof global.history.pushState === 'function') {
      try {
        global.history[replace ? 'replaceState' : 'pushState'](null, '', full);
        return;
      } catch (e) { /* file:// or sandboxed: fall through */ }
    }
    location.hash = h;
  }

  function updateHash(id, dbTab) {
    var p = { section: id };
    if (dbTab) p.dbTab = dbTab;
    writeHash(buildHash(p), false);
  }

  /** The current hash as an object (section, dbTab, and any page parameters such as q, account, character). */
  global.portalHashParams = function () { return parseHash() || {}; };

  /**
   * Merges `obj` into the hash without re-routing (a null / '' value removes the key). `replace` replaces the history entry
   * (typing in a search box) instead of adding one (choosing something worth a Back).
   */
  global.portalSetHashParams = function (obj, replace) {
    var cur = hashParamsOf(location.hash);
    Object.keys(obj || {}).forEach(function (k) {
      if (obj[k] == null || obj[k] === '') delete cur[k];
      else cur[k] = String(obj[k]);
    });
    writeHash(buildHash(cur), !!replace);
  };

  // sectionId -> [fn(params)]. Called after the section is shown and loaded (initial load, nav, Back / Forward, a pasted link).
  var deepLinks = Object.create(null);
  global.portalRegisterDeepLink = function (sectionId, fn) {
    if (!sectionId || typeof fn !== 'function') return;
    (deepLinks[sectionId] = deepLinks[sectionId] || []).push(fn);
  };
  global.portalHasDeepLink = function (sectionId) { return !!(deepLinks[sectionId] && deepLinks[sectionId].length); };
  global.portalDispatchDeepLink = function (sectionId, params) {
    params = params || global.portalHashParams();
    var fns = deepLinks[sectionId] || [];
    fns.forEach(function (fn) {
      try { fn(params); } catch (e) { if (global.console) console.error('deep link ' + sectionId, e); }
    });
    return fns.length;
  };

  /** Opens a section with page parameters as one navigation (a history entry; Back returns here). */
  global.portalOpenDeepLink = function (sectionId, params) {
    var p = Object.assign({ section: sectionId }, params || {});
    var h = buildHash(p);
    if (location.hash === '#' + h) { routeHash(true); return; }
    writeHash(h, false);
    routeHash(true);
  };

  function currentSectionId() {
    var shown = document.querySelector('.admin-section:not(.hidden)');
    return shown && shown.id ? shown.id.replace(/^section-/, '') : null;
  }

  /**
   * Routes the page to the hash. Shows the section when it changed, otherwise only dispatches the deep link. A refused
   * navigation (unsaved edits, "stay") puts the previous hash back.
   */
  function routeHash(force) {
    var full = location.hash || '';
    if (!force && full === routedHash) return;
    var previous = routedHash;
    var params = parseHash();
    routedHash = full;
    if (!params || !params.section) return;
    var same = currentSectionId() === params.section;
    if (!same) {
      var ok = global.portalShowSection(params.section, params.dbTab, true);
      if (ok === false && previous != null) writeHash(previous.replace(/^#/, ''), true);
      return;
    }
    if (started) global.portalDispatchDeepLink(params.section, params);
  }
  global.portalRouteHash = routeHash;

  // Sidebar highlight + breadcrumb for the given section / tab (also used by in-section tab strips).
  function syncNav(id, dbTab) {
    var links = Array.prototype.slice.call(document.querySelectorAll('#adminNav .nav-link'));
    var exact = dbTab ? links.filter(function (b) {
      return b.getAttribute('data-section') === id && b.getAttribute('data-db-tab') === dbTab;
    }) : [];
    links.forEach(function (b) {
      var match;
      if (b.getAttribute('data-section') !== id) match = false;
      else if (exact.length) match = exact.indexOf(b) >= 0;
      else match = !b.getAttribute('data-db-tab') || links.filter(function (x) { return x.getAttribute('data-section') === id; })[0] === b;
      b.classList.toggle('active', match);
    });
    setBreadcrumb(id, dbTab);
  }
  global.portalSyncNav = syncNav;

  function showSectionDom(id, dbTab) {
    document.querySelectorAll('.admin-section').forEach(function (el) { el.classList.add('hidden'); });
    var sec = sectionEl(id);
    if (sec) sec.classList.remove('hidden');
    syncNav(id, dbTab);
  }

  /**
   * Shows section `id` (optionally a sub-tab), updates the hash unless skipHash, and runs the section's hide / load / show
   * hooks. Returns false when the user refused to leave unsaved grid edits, true otherwise.
   */
  global.portalShowSection = function (id, dbTab, skipHash) {
    if (!id || !sectionEl(id)) id = DEFAULT_SECTION;
    var prev = currentSectionId();
    var leaving = prev !== id;
    // Unsaved grid edits stay in their grid (hidden sections are not destroyed); ask once per new edit.
    if (leaving && global.DbSave.anyDirty() && dirtySeq > ackSeq) {
      if (!global.confirm('You have unsaved grid edits. They stay in their grid until you save or reload it. Leave this section anyway?')) return false;
      ackSeq = dirtySeq;
    }
    if (leaving && started && activeId) callHide(activeId);
    showSectionDom(id, dbTab);
    if (!skipHash) updateHash(id, dbTab);
    if (started) {
      var params = parseHash() || {};
      if (params.section !== id) params = { section: id, dbTab: dbTab || undefined };
      activate(id, params);
    }
    return true;
  };

  // Closing or refreshing the browser tab drops every grid's unsaved edits: ask first.
  if (global.addEventListener) {
    global.addEventListener('beforeunload', function (event) {
      if (!global.DbSave.anyDirty()) return;
      event.preventDefault();
      event.returnValue = '';
    });
  }

  // Browser tab shown again: run the visible section's pollers at once (they skip their ticks while the tab is hidden).
  document.addEventListener('visibilitychange', function () {
    if (!started) return;
    var id = currentSectionId();
    if (document.visibilityState === 'visible') {
      pollers.forEach(function (p) { if (p.id === id) runPoller(p); });
    }
  });

  document.addEventListener('DOMContentLoaded', function () {
    var nav = document.getElementById('adminNav');
    if (nav) {
      nav.addEventListener('click', function (e) {
        var t = e.target;
        if (t && t.closest) t = t.closest('[data-section]') || t;
        if (!t || !t.getAttribute) return;
        var sec = t.getAttribute('data-section');
        if (!sec) return;
        e.preventDefault();
        global.portalShowSection(sec, t.getAttribute('data-db-tab'), false);
      });
    }

    // Initial DOM state from the hash (the sections themselves load once app.js has verified the session).
    var hash = parseHash();
    routedHash = location.hash || '';
    if (hash && hash.section && sectionEl(hash.section)) showSectionDom(hash.section, hash.dbTab);
    else syncNav(DEFAULT_SECTION, null);

    // Back / Forward and a pasted link change the hash; route the page to it.
    if (global.addEventListener) global.addEventListener('hashchange', function () { routeHash(false); });

    // "/" focuses the current section's search box, unless typing somewhere.
    document.addEventListener('keydown', function (e) {
      if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
      if (isEditableTarget(e.target)) return;
      var box = sectionSearchBox();
      if (!box) return;
      e.preventDefault();
      box.focus();
      if (typeof box.select === 'function') box.select();
    });
  });

  function isEditableTarget(t) {
    if (!t) return false;
    var tag = String(t.tagName || '').toUpperCase();
    return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || !!t.isContentEditable;
  }
  global.portalIsEditableTarget = isEditableTarget;

  /** The search box of the visible section: [data-section-search] first, then the first search input. */
  function sectionSearchBox() {
    var shown = document.querySelector('.admin-section:not(.hidden)');
    if (!shown) return null;
    return shown.querySelector('[data-section-search]') || shown.querySelector('input[type=search]');
  }
  global.portalSectionSearchBox = sectionSearchBox;

  // ---- AdminInspect: the generic right-hand inspect drawer (#inspectDrawer) -----------------------------------------

  var inspectOnClose = null;
  global.AdminInspect = {
    /**
     * open({ title, node?, html?, text?, onClose? }) -> the drawer body element. `html` must already be escaped (OpsUI.esc);
     * prefer `node` (a DOM element) or `text`. Replaces whatever the drawer showed.
     */
    open: function (opts) {
      opts = opts || {};
      var d = document.getElementById('inspectDrawer');
      var t = document.getElementById('inspectDrawerTitle');
      var b = document.getElementById('inspectDrawerBody');
      if (!d || !b) return null;
      if (inspectOnClose) { var prevFn = inspectOnClose; inspectOnClose = null; try { prevFn(); } catch (e) { /* listener */ } }
      if (t) t.textContent = opts.title || 'Details';
      b.innerHTML = '';
      if (opts.node) b.appendChild(opts.node);
      else if (opts.html != null) b.innerHTML = opts.html;
      else if (opts.text != null) b.textContent = String(opts.text);
      inspectOnClose = typeof opts.onClose === 'function' ? opts.onClose : null;
      d.classList.remove('hidden');
      return b;
    },
    close: function () {
      var d = document.getElementById('inspectDrawer');
      if (d) d.classList.add('hidden');
      if (inspectOnClose) { var fn = inspectOnClose; inspectOnClose = null; try { fn(); } catch (e) { /* listener */ } }
    },
    isOpen: function () {
      var d = document.getElementById('inspectDrawer');
      return !!(d && !d.classList.contains('hidden'));
    },
    body: function () { return document.getElementById('inspectDrawerBody'); }
  };
  document.addEventListener('DOMContentLoaded', function () {
    var c = document.getElementById('inspectDrawerClose');
    if (c) c.addEventListener('click', function () { global.AdminInspect.close(); });
    document.addEventListener('keydown', function (e) {
      if (e.key !== 'Escape' || !global.AdminInspect.isOpen()) return;
      var fd = document.getElementById('formDrawer');
      if (fd && !fd.classList.contains('hidden')) return;   // the row drawer closes first
      if (document.querySelector('.portal-confirm-backdrop')) return;   // a dialog is open
      global.AdminInspect.close();
    });
  });

  /** Section id -> label. */
  global.portalSectionLabels = function () {
    var out = {};
    Object.keys(SECTIONS).forEach(function (k) { out[k] = SECTIONS[k].label; });
    return out;
  };
})(window);
