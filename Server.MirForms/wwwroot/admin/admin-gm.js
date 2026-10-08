/**
 * GM Console section (#section-gm) — STUB. The feature owner replaces this file.
 * Registers with AdminSections and renders a "coming soon" placeholder card.
 */
(function (global) {
  'use strict';
  global.AdminSections.register('gm', {
    load: function (el) { global.AdminSections.placeholder(el, 'gm'); }
  });
})(window);
