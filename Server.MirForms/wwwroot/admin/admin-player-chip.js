/**
 * PlayerChip: one clickable chip for a player (character) name, everywhere.
 *
 *   PlayerChip.html(name, { account, accountIndex, online, characterIndex, ip }) -> an ESCAPED HTML string.
 *   PlayerChip.el(name, opts)       -> the same chip as an element (for pages that build DOM).
 *   PlayerChip.menuItems(data)      -> the menu entries for a chip's data (pure).
 *   PlayerChip.inspect(name, opts)  -> opens the Characters section on that character.
 *
 * A document-level delegated click handler opens a small menu on any [data-player-chip]: Characters, GM Console,
 * Chat log, Accounts (when the account is known) and Audit trail. Each entry is a deep link (portalOpenDeepLink):
 *   characters  #section=characters&q=<name>[&character=<characterIndex>]
 *   gm          #section=gm&player=<name>
 *   chatlog     #section=chatlog&player=<name>
 *   accounts    #section=accounts&q=<accountId>[&account=<accountIndex>]
 *   auditlog    #section=auditlog&q=<name>
 * Section modules read these with portalRegisterDeepLink(sectionId, fn(params)).
 */
(function (global) {
  'use strict';

  function esc(v) {
    return String(v == null ? '' : v)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function attrs(name, o) {
    o = o || {};
    var a = ' data-player-chip="' + esc(name) + '"';
    if (o.account) a += ' data-account="' + esc(o.account) + '"';
    if (o.accountIndex) a += ' data-account-index="' + esc(Number(o.accountIndex) || 0) + '"';
    if (o.characterIndex) a += ' data-character-index="' + esc(Number(o.characterIndex) || 0) + '"';
    if (o.ip) a += ' data-ip="' + esc(o.ip) + '"';
    if (o.online === true) a += ' data-online="1"';
    else if (o.online === false) a += ' data-online="0"';
    return a;
  }

  function html(name, opts) {
    if (name == null || name === '') return '';
    opts = opts || {};
    var cls = 'player-chip' + (opts.online === true ? ' online' : '');
    var title = 'Actions for ' + name + (opts.account ? ' (' + opts.account + ')' : '');
    return '<button type="button" class="' + cls + '"' + attrs(name, opts) + ' title="' + esc(title) + '" aria-haspopup="menu">' +
      (opts.online === true ? '<span class="player-chip-dot" aria-hidden="true"></span>' : '') + esc(name) + '</button>';
  }

  function el(name, opts) {
    opts = opts || {};
    var b = document.createElement('button');
    b.type = 'button';
    b.className = 'player-chip' + (opts.online === true ? ' online' : '');
    b.setAttribute('data-player-chip', String(name == null ? '' : name));
    if (opts.account) b.setAttribute('data-account', String(opts.account));
    if (opts.accountIndex) b.setAttribute('data-account-index', String(Number(opts.accountIndex) || 0));
    if (opts.characterIndex) b.setAttribute('data-character-index', String(Number(opts.characterIndex) || 0));
    if (opts.ip) b.setAttribute('data-ip', String(opts.ip));
    if (opts.online === true) b.setAttribute('data-online', '1');
    else if (opts.online === false) b.setAttribute('data-online', '0');
    b.setAttribute('aria-haspopup', 'menu');
    b.title = 'Actions for ' + name + (opts.account ? ' (' + opts.account + ')' : '');
    b.textContent = String(name == null ? '' : name);
    return b;
  }

  /** The chip's data from its attributes. */
  function dataOf(node) {
    var g = function (k) { return node.getAttribute ? node.getAttribute(k) : null; };
    var on = g('data-online');
    return {
      name: g('data-player-chip') || '',
      account: g('data-account') || '',
      accountIndex: Number(g('data-account-index')) || 0,
      characterIndex: Number(g('data-character-index')) || 0,
      ip: g('data-ip') || '',
      online: on === '1' ? true : on === '0' ? false : null
    };
  }

  function show(section, params) {
    if (global.portalOpenDeepLink) global.portalOpenDeepLink(section, params || {});
    else if (global.portalShowSection) global.portalShowSection(section);
  }

  /** Opens the Characters section on the character (deep link q=<name>, character=<index> when known). */
  function inspect(name, opts) {
    if (!name) return;
    opts = opts || {};
    var p = { q: name };
    if (opts.characterIndex) p.character = String(opts.characterIndex);
    show('characters', p);
  }

  /** Pure: the menu for a chip's data. Each entry: { id, label, run? } (run is attached by the click handler). */
  function menuItems(d) {
    var items = [];
    items.push({ id: 'character', label: d.online === true ? 'Character (online)' : d.online === false ? 'Character (offline)' : 'Character' });
    items.push({ id: 'gm', label: 'GM Console' });
    items.push({ id: 'chat', label: 'Chat log' });
    if (d.account || d.accountIndex) items.push({ id: 'account', label: 'Account' });
    items.push({ id: 'audit', label: 'Audit trail' });
    return items;
  }

  function runItem(id, d) {
    switch (id) {
      case 'character': return inspect(d.name, { characterIndex: d.characterIndex });
      case 'gm': return show('gm', { player: d.name });
      case 'chat': return show('chatlog', { player: d.name });
      case 'account': {
        var p = { q: d.account || '' };
        if (d.accountIndex) p.account = String(d.accountIndex);
        return show('accounts', p);
      }
      case 'audit': return show('auditlog', { q: d.name });
      default: return null;
    }
  }

  // ---- the menu ---------------------------------------------------------------------------------------------------------

  var menu = null;

  function closeMenu() {
    if (menu && menu.parentNode) menu.parentNode.removeChild(menu);
    menu = null;
  }

  function openMenu(anchor) {
    var d = dataOf(anchor);
    var same = menu && menu._anchor === anchor;
    closeMenu();
    if (same) return;
    menu = document.createElement('div');
    menu.className = 'player-chip-menu';
    menu.setAttribute('role', 'menu');
    menu._anchor = anchor;
    var head = document.createElement('div');
    head.className = 'player-chip-menu-head';
    head.textContent = d.name + (d.account ? ' · ' + d.account : '');
    menu.appendChild(head);
    var first = null;
    menuItems(d).forEach(function (it) {
      var b = document.createElement('button');
      b.type = 'button';
      b.setAttribute('role', 'menuitem');
      b.textContent = it.label;
      b.addEventListener('click', function (e) {
        if (e && e.stopPropagation) e.stopPropagation();
        closeMenu();
        runItem(it.id, d);
      });
      menu.appendChild(b);
      if (!first) first = b;
    });
    document.body.appendChild(menu);
    if (anchor.getBoundingClientRect) {
      var r = anchor.getBoundingClientRect();
      var h = menu.offsetHeight || 0, w = menu.offsetWidth || 0;
      var top = r.bottom + 4;
      if (global.innerHeight && top + h > global.innerHeight - 8) top = Math.max(8, r.top - h - 4);
      var left = r.left;
      if (global.innerWidth && left + w > global.innerWidth - 8) left = Math.max(8, global.innerWidth - w - 8);
      menu.style.top = top + 'px';
      menu.style.left = left + 'px';
    }
    if (first) first.focus();
  }

  document.addEventListener('click', function (e) {
    var t = e.target;
    var chip = t && t.closest ? t.closest('[data-player-chip]') : null;
    if (chip) {
      if (e.preventDefault) e.preventDefault();
      if (e.stopPropagation) e.stopPropagation();
      openMenu(chip);
      return;
    }
    if (menu && !(menu.contains && menu.contains(t))) closeMenu();
  }, true);
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && menu) closeMenu(); });
  if (global.addEventListener) {
    global.addEventListener('resize', closeMenu);
    global.addEventListener('scroll', function () { if (menu) closeMenu(); }, true);
  }

  global.PlayerChip = {
    html: html,
    el: el,
    esc: esc,
    menuItems: menuItems,
    dataOf: dataOf,
    run: runItem,
    inspect: inspect,
    close: closeMenu
  };
})(window);
