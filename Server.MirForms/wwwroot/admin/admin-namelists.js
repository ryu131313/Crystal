/**
 * Name Lists section (#section-namelists) — STUB. The feature owner replaces this file.
 * Registers with AdminSections and renders a "coming soon" placeholder card.
 */
(function (global) {
  'use strict';
  global.AdminSections.register('namelists', {
    load: function (el) { global.AdminSections.placeholder(el, 'namelists'); }
  });
})(window);
