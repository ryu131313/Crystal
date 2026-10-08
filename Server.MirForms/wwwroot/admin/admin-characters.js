/**
 * Characters section (#section-characters) — STUB. The feature owner replaces this file.
 * Registers with AdminSections and renders a "coming soon" placeholder card.
 */
(function (global) {
  'use strict';
  global.AdminSections.register('characters', {
    load: function (el) { global.AdminSections.placeholder(el, 'characters'); }
  });
})(window);
