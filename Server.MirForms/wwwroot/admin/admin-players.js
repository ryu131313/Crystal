/**
 * Players Online section (#section-players-online) — STUB. The feature owner replaces this file.
 * Registers with AdminSections and renders a "coming soon" placeholder card.
 */
(function (global) {
  'use strict';
  global.AdminSections.register('players-online', {
    load: function (el) { global.AdminSections.placeholder(el, 'players-online'); }
  });
})(window);
