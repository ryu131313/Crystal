/**
 * Game Data section (#section-gamedata) — STUB. The feature owner replaces this file.
 * Registers with AdminSections and renders a "coming soon" placeholder card.
 */
(function (global) {
  'use strict';
  global.AdminSections.register('gamedata', {
    load: function (el) { global.AdminSections.placeholder(el, 'gamedata'); }
  });
})(window);
