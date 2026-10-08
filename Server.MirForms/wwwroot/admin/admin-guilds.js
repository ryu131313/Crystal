/**
 * Guilds section (#section-guilds) — STUB. The feature owner replaces this file.
 * Registers with AdminSections and renders a "coming soon" placeholder card.
 */
(function (global) {
  'use strict';
  global.AdminSections.register('guilds', {
    load: function (el) { global.AdminSections.placeholder(el, 'guilds'); }
  });
})(window);
