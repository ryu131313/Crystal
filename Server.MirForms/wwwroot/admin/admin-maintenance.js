/*
 * Maintenance (#section-maintenance).
 *
 *   GET  /api/admin/maintenance                        {active, since, by, message, kickNonAdmins, kickScheduledUtc, secondsUntilKick, ...}
 *   POST /api/admin/maintenance {action:'start', message, kickNonAdmins, minutes (0-60), announce}
 *   POST /api/admin/maintenance {action:'lift', announce}
 *
 * Start installs a login gate that refuses non-admin game accounts (admins can always log in); with "disconnect" the
 * server announces a countdown and then disconnects non-admin players. The state is shown as the server reports it, and the
 * sidebar badge (#navMaintenanceBadge) follows it on every page (checked with the topbar status poll, at most every 30 s).
 */
(function (global) {
  'use strict';

  var esc = function (s) { return OpsUI.esc(s); };
  var BADGE_EVERY_MS = 30000;

  var root = null;
  var state = null;
  var stopPoll = null;
  var tickTimer = null;
  var lastBadgeCheck = 0;
  var stateAt = 0;               // Date.now() when `state` arrived (for the local countdown)

  function el(role) { return root ? root.querySelector('[data-role="' + role + '"]') : null; }

  function setBadge(s) {
    var b = document.getElementById('navMaintenanceBadge');
    if (!b) return;
    var on = !!(s && s.active);
    b.classList.toggle('hidden', !on);
    b.textContent = on ? (s.kickScheduledUtc ? 'soon' : 'on') : 'on';
    b.title = on ? 'Maintenance mode: non-admin logins are refused' : '';
  }

  function countdownText(seconds) {
    seconds = Math.max(0, Math.round(seconds));
    var m = Math.floor(seconds / 60), s = seconds % 60;
    return m + ':' + (s < 10 ? '0' : '') + s;
  }

  /* The start confirmation, following the disconnect choice and countdown (pure). */
  function startWarning(minutes, kick) {
    if (!kick) return 'Refuse new game logins for every non-admin account now?\n\nPlayers already online stay connected.';
    if (minutes > 0)
      return 'Start maintenance with a ' + minutes + ' minute countdown?\n\nNew non-admin logins are refused right away; players are warned in game and every non-admin player is disconnected when the countdown ends.';
    return 'Start maintenance and DISCONNECT every non-admin player right now?';
  }

  /* The lift confirmation (pure). */
  function liftWarning(s) {
    var lines = ['Lift maintenance?', '', '- Game logins re-open for everyone.'];
    if (s && s.kickScheduledUtc) lines.push('- The pending disconnect is CANCELLED.');
    return lines.join('\n');
  }

  function renderState() {
    var host = el('state');
    if (!host) return;
    var s = state;
    if (!s) { host.innerHTML = '<span class="mir-muted">Unavailable.</span>'; return; }

    var parts = [];
    if (s.active) parts.push('<span class="ops-pill ops-pill-danger">Logins blocked for non-admins</span>');
    else parts.push('<span class="ops-pill ops-pill-ok">Server open</span>');

    var rows = [];
    if (s.active) {
      rows.push(['Since', OpsUI.fmtDate(s.since) + ' (' + OpsUI.ago(s.since) + ')']);
      rows.push(['Started by', s.by || '']);
      rows.push(['Message', s.message || '']);
      rows.push(['Players', s.kickNonAdmins ? 'Non-admins are disconnected' : 'Players already online stay connected']);
      if (s.kickScheduledUtc) rows.push(['Disconnect in', '<span data-role="countdown" class="mt-countdown"></span> (at ' + esc(OpsUI.fmtDate(s.kickScheduledUtc)) + ')']);
      if (s.kickedUtc) rows.push(['Disconnected', esc(s.kickedCount) + ' connection(s) at ' + esc(OpsUI.fmtDate(s.kickedUtc))]);
      rows.push(['Refused logins', String(s.refusedLogins || 0)]);
    } else if (s.liftedUtc) {
      rows.push(['Last lifted', OpsUI.fmtDate(s.liftedUtc) + (s.liftedBy ? ' by ' + s.liftedBy : '')]);
    }

    host.innerHTML = '<div class="mt-pills">' + parts.join(' ') + '</div>' +
      (rows.length ? '<dl class="kv-list mt-kv">' + rows.map(function (r) {
        var html = r[0] === 'Disconnect in' || r[0] === 'Disconnected' ? r[1] : esc(r[1]);
        return '<dt>' + esc(r[0]) + '</dt><dd>' + html + '</dd>';
      }).join('') + '</dl>' : '');

    var warn = el('world-warning');
    if (warn) {
      warn.textContent = s.world && s.world !== 'running'
        ? 'The world is ' + s.world + '. The login gate still applies when it starts; announcements and disconnects need a running world.'
        : '';
      warn.classList.toggle('hidden', !warn.textContent);
    }

    el('start-card').classList.toggle('hidden', !!s.active);
    el('lift-card').classList.toggle('hidden', !s.active);
    var msg = el('message');
    if (msg && !msg.value && s.defaultMessage) msg.placeholder = s.defaultMessage;
    var minutes = el('minutes');
    if (minutes && s.maxMinutes) minutes.max = s.maxMinutes;
    tickCountdown();
  }

  function tickCountdown() {
    var c = el('countdown');
    if (!c || !state || state.secondsUntilKick == null) return;
    var left = state.secondsUntilKick - (Date.now() - stateAt) / 1000;
    c.textContent = left > 0 ? countdownText(left) : 'now';
  }

  function apply(s) {
    if (!s) return;
    state = s;
    stateAt = Date.now();
    setBadge(s);
    renderState();
  }

  async function refresh() {
    var r = await OpsUI.fetchResult('/api/admin/maintenance');
    if (!r.ok) {
      var host = el('state');
      if (host) host.innerHTML = '<span class="ops-pill ops-pill-warn">' + esc('Could not load the maintenance state: ' + r.error) + '</span>';
      return;
    }
    apply(r.data);
  }

  async function start(button) {
    var kick = !!el('kick').checked;
    var minutes = kick ? Math.max(0, Math.min(60, Math.floor(Number(el('minutes').value) || 0))) : 0;
    var message = el('message').value.trim();
    var announce = !!el('announce').checked;
    if (!await portalConfirm(startWarning(minutes, kick), { title: 'Start maintenance', confirmLabel: 'Start', danger: true })) return;

    var j = await OpsUI.withBusy(button, function () {
      return OpsUI.post('/api/admin/maintenance', { action: 'start', message: message, kickNonAdmins: kick, minutes: minutes, announce: announce });
    }, 'Starting…');
    if (j && j.data) apply(j.data); else refresh();
  }

  async function lift(button) {
    await refresh();   // confirm against the server's current view
    if (!await portalConfirm(liftWarning(state), { title: 'Lift maintenance', confirmLabel: 'Lift' })) return;
    var j = await OpsUI.withBusy(button, function () {
      return OpsUI.post('/api/admin/maintenance', { action: 'lift', announce: !!el('lift-announce').checked });
    }, 'Lifting…');
    if (j && j.data) apply(j.data); else refresh();
  }

  function syncKick() {
    var kick = !!el('kick').checked;
    el('minutes').disabled = !kick;
    el('minutes-presets').classList.toggle('mt-disabled', !kick);
    var m = Number(el('minutes').value) || 0;
    el('start').textContent = !kick ? 'Block logins…' : (m > 0 ? 'Start ' + m + ' min countdown…' : 'Block logins and disconnect…');
  }

  function render(sectionEl) {
    sectionEl.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar"><h2>Maintenance</h2><button type="button" class="mir-btn" data-role="refresh">Refresh</button></div>' +
      '  <div data-role="state"></div>' +
      '  <p class="mt-warning hidden" data-role="world-warning"></p>' +
      '  <p class="mir-muted">Maintenance refuses game logins from non-admin accounts (they see the "login disabled" message). Admin accounts can always log in, and this web panel is unaffected. It lasts until lifted, also across world restarts, but not across a restart of Server.exe.</p>' +
      '</div>' +
      '<div class="mir-card" data-role="start-card">' +
      '  <div class="toolbar"><h2>Start maintenance</h2></div>' +
      '  <label class="ops-block">Message (announced in game and recorded as the refusal reason)' +
      '    <textarea class="fd-input ops-textarea" data-role="message" maxlength="300" rows="2"></textarea></label>' +
      '  <div class="ops-form">' +
      '    <label class="mir-toggle"><input type="checkbox" data-role="kick" /> Disconnect non-admin players</label>' +
      '    <label>Countdown (minutes, 0 = now)<input type="number" class="fd-input" data-role="minutes" min="0" max="60" value="5" /></label>' +
      '    <span class="ops-dialog-presets" data-role="minutes-presets">' +
      [0, 1, 5, 10, 15, 30, 60].map(function (m) { return '<button type="button" class="mir-btn ops-row-btn" data-minutes="' + m + '">' + (m === 0 ? 'now' : m + ' min') + '</button>'; }).join('') +
      '    </span>' +
      '    <label class="mir-toggle"><input type="checkbox" data-role="announce" checked /> Announce in game</label>' +
      '  </div>' +
      '  <div class="mt-actions"><button type="button" class="mir-btn danger" data-role="start">Block logins…</button></div>' +
      '</div>' +
      '<div class="mir-card hidden" data-role="lift-card">' +
      '  <div class="toolbar"><h2>Lift maintenance</h2></div>' +
      '  <div class="ops-form"><label class="mir-toggle"><input type="checkbox" data-role="lift-announce" checked /> Announce "logins are open again" in game</label></div>' +
      '  <div class="mt-actions"><button type="button" class="mir-btn primary" data-role="lift">Lift maintenance…</button></div>' +
      '</div>';
  }

  function wire(sectionEl) {
    el('refresh').addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, refresh); });
    el('kick').addEventListener('change', syncKick);
    el('minutes').addEventListener('input', syncKick);
    el('minutes-presets').addEventListener('click', function (e) {
      var b = e.target.closest ? e.target.closest('[data-minutes]') : null;
      if (!b || el('minutes').disabled) return;
      el('minutes').value = b.getAttribute('data-minutes');
      syncKick();
    });
    el('start').addEventListener('click', function (e) { start(e.currentTarget); });
    el('lift').addEventListener('click', function (e) { lift(e.currentTarget); });
    syncKick();
  }

  global.AdminSections.register('maintenance', {
    load: function (sectionEl) {
      root = sectionEl;
      render(sectionEl);
      wire(sectionEl);
    },
    show: function () {
      if (!stopPoll) stopPoll = global.AdminSections.poll('maintenance', refresh, 5000);
      if (!tickTimer) tickTimer = setInterval(tickCountdown, 1000);
      return refresh();
    },
    hide: function () {
      if (stopPoll) { stopPoll(); stopPoll = null; }
      if (tickTimer) { clearInterval(tickTimer); tickTimer = null; }
    }
  });

  // Sidebar badge on every page: piggy-back on the topbar status poll (it only runs once signed in).
  async function checkBadge() {
    if (Date.now() - lastBadgeCheck < BADGE_EVERY_MS) return;
    lastBadgeCheck = Date.now();
    var j = await OpsUI.get('/api/admin/maintenance');
    if (j) setBadge(j);
  }
  document.addEventListener('DOMContentLoaded', function () {
    if (global.AdminStatus && typeof AdminStatus.onChange === 'function') AdminStatus.onChange(function (st) { if (st) checkBadge(); });
  });

  global.MaintenancePage = { startWarning: startWarning, liftWarning: liftWarning, refresh: refresh };
})(window);
