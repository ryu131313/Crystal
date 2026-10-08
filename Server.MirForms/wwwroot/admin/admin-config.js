/**
 * Server Config section (#section-config) — STUB. The feature owner replaces this file.
 * Registers with AdminSections and renders a "coming soon" placeholder card.
 */
(function (global) {
  'use strict';
  global.AdminSections.register('config', {
    load: function (el) { global.AdminSections.placeholder(el, 'config'); }
  });
})(window);
