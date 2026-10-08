/**
 * Non-blocking status toasts and confirm dialogs for the admin portal.
 *
 * portalToast(message, kind, ms)  kind: 'info' | 'ok' (alias 'success') | 'warn' | 'error'. B69: the duration is honoured;
 *   errors stay 12 s by default and carry a close button.
 * portalConfirm(message, opts)   -> Promise<bool>. opts: { title, confirmLabel, cancelLabel, danger (default true) }.
 *   B62: Enter activates the FOCUSED button (Cancel has the focus when the dialog opens), Escape cancels.
 * portalDialog(opts)             -> Promise<values | null>. A small form dialog (fields: text, textarea, number, select,
 *   checkbox, radio presets) with an explicit Confirm; Cancel, Escape and a click outside resolve null. Used by the ban
 *   dialog (B30); other pages may reuse it.
 * portalDialogKey(e, ctx)        -> the pure keyboard rule both dialogs share (exported for the tests).
 */
(function (global) {
  var root = null;

  var KINDS = {
    error: { bg: '#2a0f11', border: '#e0524d', ms: 12000 },
    ok: { bg: '#0f2018', border: '#5fd38a', ms: 4500 },
    warn: { bg: '#241c0c', border: '#e0a83a', ms: 7000 },
    info: { bg: '#18141c', border: '#c5a059', ms: 4500 }
  };

  function kindOf(kind) {
    if (kind === 'success') return 'ok';
    return KINDS[kind] ? kind : 'info';
  }

  function ensureRoot() {
    if (root) return root;
    root = document.createElement('div');
    root.id = 'portalToastRoot';
    root.setAttribute('aria-live', 'polite');
    root.style.cssText = 'position:fixed;bottom:16px;right:16px;z-index:13000;display:flex;flex-direction:column;gap:8px;max-width:380px';
    document.body.appendChild(root);
    return root;
  }

  function toast(message, kind, ms) {
    ensureRoot();
    var k = kindOf(kind);
    var spec = KINDS[k];
    var el = document.createElement('div');
    el.className = 'portal-toast portal-toast-' + k;
    el.setAttribute('role', k === 'error' ? 'alert' : 'status');
    el.style.cssText = 'display:flex;gap:10px;align-items:flex-start;padding:10px 14px;border-radius:8px;border:1px solid ' + spec.border +
      ';background:' + spec.bg + ';color:#e9e3d8;font-size:13px;box-shadow:0 12px 28px -10px rgba(0,0,0,.9);white-space:pre-line';
    var text = document.createElement('span');
    text.style.cssText = 'flex:1;overflow-wrap:anywhere';
    text.textContent = message == null ? '' : String(message);
    el.appendChild(text);
    function remove() { if (el.parentNode) el.parentNode.removeChild(el); }
    if (k === 'error' || k === 'warn') {
      var close = document.createElement('button');
      close.type = 'button';
      close.setAttribute('aria-label', 'Dismiss');
      close.textContent = '×';
      close.style.cssText = 'background:none;border:0;color:inherit;font-size:16px;line-height:1;cursor:pointer;padding:0 2px';
      close.addEventListener('click', remove);
      el.appendChild(close);
    }
    root.appendChild(el);
    var wait = Number(ms) > 0 ? Number(ms) : spec.ms;
    setTimeout(remove, wait);
    return el;
  }

  global.portalToast = function (message, kind, ms) {
    return toast(message, kind || 'info', ms);
  };

  /**
   * The keyboard rule of the confirm dialogs. ctx: { active, confirm, cancel, multiline }.
   * Returns 'confirm' | 'cancel' | 'none'. Enter never confirms unless the Confirm button itself has the focus (B62); in a
   * form, Enter in a single-line field submits only when ctx.submitOnEnter is set; Ctrl/Cmd+Enter submits from anywhere.
   */
  function dialogKey(e, ctx) {
    if (!e) return 'none';
    if (e.key === 'Escape') return 'cancel';
    if (e.key !== 'Enter') return 'none';
    var active = ctx.active;
    if (active && active === ctx.cancel) return 'cancel';
    if (active && active === ctx.confirm) return 'confirm';
    if (e.ctrlKey || e.metaKey) return ctx.allowCtrlEnter ? 'confirm' : 'none';
    var tag = active && active.tagName ? String(active.tagName).toUpperCase() : '';
    if (ctx.submitOnEnter && (tag === 'INPUT' || tag === 'SELECT')) return 'confirm';
    return 'none';
  }
  global.portalDialogKey = dialogKey;

  function makeButton(text, cls) {
    var b = document.createElement('button');
    b.type = 'button';
    b.className = cls;
    b.textContent = text;
    return b;
  }

  global.portalConfirm = function (message, opts) {
    opts = opts || {};
    return new Promise(function (resolve) {
      var backdrop = document.createElement('div');
      backdrop.className = 'portal-confirm-backdrop';
      backdrop.setAttribute('role', 'presentation');

      var dialog = document.createElement('div');
      dialog.className = 'portal-confirm-dialog';
      dialog.setAttribute('role', 'alertdialog');
      dialog.setAttribute('aria-modal', 'true');
      dialog.setAttribute('aria-labelledby', 'portalConfirmTitle');

      var title = document.createElement('h3');
      title.id = 'portalConfirmTitle';
      title.textContent = opts.title || 'Confirm action';
      var copy = document.createElement('p');
      copy.textContent = message || 'Are you sure?';
      var actions = document.createElement('div');
      actions.className = 'portal-confirm-actions';
      var cancel = makeButton(opts.cancelLabel || 'Cancel', 'mir-btn');
      var confirm = makeButton(opts.confirmLabel || 'Confirm', 'mir-btn ' + (opts.danger === false ? 'primary' : 'danger'));

      var done = false;
      function finish(value) {
        if (done) return;
        done = true;
        document.removeEventListener('keydown', onKey, true);
        if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
        resolve(value);
      }
      function onKey(e) {
        var act = dialogKey(e, { active: document.activeElement, confirm: confirm, cancel: cancel });
        if (act === 'none') {
          // Keep Tab inside the dialog: two buttons, so Tab just swaps the focus.
          if (e.key === 'Tab') {
            if (e.preventDefault) e.preventDefault();
            (document.activeElement === cancel ? confirm : cancel).focus();
          }
          return;
        }
        if (e.preventDefault) e.preventDefault();
        if (e.stopPropagation) e.stopPropagation();
        finish(act === 'confirm');
      }

      cancel.addEventListener('click', function () { finish(false); });
      confirm.addEventListener('click', function () { finish(true); });
      backdrop.addEventListener('click', function (e) { if (e.target === backdrop) finish(false); });
      document.addEventListener('keydown', onKey, true);

      actions.appendChild(cancel);
      actions.appendChild(confirm);
      dialog.appendChild(title);
      dialog.appendChild(copy);
      dialog.appendChild(actions);
      backdrop.appendChild(dialog);
      document.body.appendChild(backdrop);
      cancel.focus();
    });
  };

  var listSeq = 0;

  /**
   * A form dialog. opts: { title, message, confirmLabel, danger, fields: [{ name, label, type, value, options, placeholder,
   * min, max, required, suggestions (text: a type-to-search list) }], validate(values) -> error string | null, submitOnEnter }.
   * type: 'text' | 'password' | 'textarea' | 'number' | 'select' | 'checkbox' | 'presets' (options: [{ value, label }], one chosen).
   * Resolves the values object, or null when cancelled. Confirm stays disabled while validate() returns an error.
   */
  global.portalDialog = function (opts) {
    opts = opts || {};
    return new Promise(function (resolve) {
      var backdrop = document.createElement('div');
      backdrop.className = 'portal-confirm-backdrop';
      backdrop.setAttribute('role', 'presentation');
      var dialog = document.createElement('div');
      dialog.className = 'portal-confirm-dialog portal-form-dialog';
      dialog.setAttribute('role', 'dialog');
      dialog.setAttribute('aria-modal', 'true');
      var title = document.createElement('h3');
      title.textContent = opts.title || 'Confirm';
      dialog.appendChild(title);
      if (opts.message) {
        var p = document.createElement('p');
        p.textContent = opts.message;
        dialog.appendChild(p);
      }

      var inputs = {};
      var first = null;
      (opts.fields || []).forEach(function (f) {
        var wrap = document.createElement('label');
        wrap.className = 'portal-form-field';
        var cap = document.createElement('span');
        cap.className = 'mir-muted';
        cap.textContent = f.label || f.name;
        var input;
        if (f.type === 'checkbox') {
          wrap.className += ' portal-form-check';
          input = document.createElement('input');
          input.type = 'checkbox';
          input.checked = !!f.value;
          wrap.appendChild(input);
          wrap.appendChild(cap);
        } else if (f.type === 'presets') {
          input = document.createElement('div');
          input.className = 'portal-form-presets';
          input.setAttribute('role', 'radiogroup');
          input._value = f.value == null ? '' : String(f.value);
          (f.options || []).forEach(function (o) {
            var b = makeButton(o.label, 'mir-btn portal-form-preset');
            b.setAttribute('data-value', String(o.value));
            b.setAttribute('aria-pressed', String(String(o.value) === input._value));
            if (String(o.value) === input._value) b.classList.add('active');
            b.addEventListener('click', function () {
              input._value = String(o.value);
              Array.prototype.forEach.call(input.children || [], function (c) {
                var on = c.getAttribute('data-value') === input._value;
                c.classList.toggle('active', on);
                c.setAttribute('aria-pressed', String(on));
              });
              refresh();
            });
            input.appendChild(b);
          });
          wrap.appendChild(cap);
          wrap.appendChild(input);
        } else {
          input = document.createElement(f.type === 'textarea' ? 'textarea' : f.type === 'select' ? 'select' : 'input');
          input.className = 'fd-input';
          if (f.type === 'number') input.type = 'number';
          else if (f.type === 'password') { input.type = 'password'; input.autocomplete = 'current-password'; }
          else if (f.type !== 'textarea' && f.type !== 'select') input.type = 'text';
          if (f.type === 'select') {
            (f.options || []).forEach(function (o) {
              var op = document.createElement('option');
              op.value = String(o.value);
              op.textContent = o.label;
              input.appendChild(op);
            });
          }
          if (f.placeholder) input.placeholder = f.placeholder;
          if (Array.isArray(f.suggestions) && f.type !== 'select' && f.type !== 'textarea') {
            // A text field with a type-to-search list (a datalist; any text stays allowed, validate() decides).
            var dl = document.createElement('datalist');
            dl.id = 'portalDialogList_' + (++listSeq);
            f.suggestions.slice(0, 5000).forEach(function (s) { var o = document.createElement('option'); o.value = String(s); dl.appendChild(o); });
            wrap.appendChild(dl);
            input.setAttribute('list', dl.id);
          }
          if (f.min != null) input.min = f.min;
          if (f.max != null) input.max = f.max;
          if (f.type === 'textarea') input.rows = f.rows || 3;
          if (f.value != null) input.value = String(f.value);
          wrap.appendChild(cap);
          wrap.appendChild(input);
        }
        inputs[f.name] = { field: f, el: input };
        if (!first && f.type !== 'presets' && f.type !== 'checkbox') first = input;
        if (input.addEventListener) {
          input.addEventListener('input', refresh);
          input.addEventListener('change', refresh);
        }
        dialog.appendChild(wrap);
      });

      var err = document.createElement('p');
      err.className = 'portal-form-error';
      dialog.appendChild(err);

      var actions = document.createElement('div');
      actions.className = 'portal-confirm-actions';
      var cancel = makeButton('Cancel', 'mir-btn');
      var confirm = makeButton(opts.confirmLabel || 'Confirm', 'mir-btn ' + (opts.danger ? 'danger' : 'primary'));
      actions.appendChild(cancel);
      actions.appendChild(confirm);
      dialog.appendChild(actions);
      backdrop.appendChild(dialog);

      function values() {
        var v = {};
        Object.keys(inputs).forEach(function (k) {
          var f = inputs[k].field, el = inputs[k].el;
          if (f.type === 'checkbox') v[k] = !!el.checked;
          else if (f.type === 'presets') v[k] = el._value;
          else if (f.type === 'number') v[k] = el.value === '' ? null : Number(el.value);
          else v[k] = String(el.value == null ? '' : el.value).trim();
        });
        return v;
      }
      function problem() {
        var v = values();
        var missing = Object.keys(inputs).filter(function (k) {
          return inputs[k].field.required && (v[k] === '' || v[k] == null);
        });
        if (missing.length) return 'Fill in: ' + missing.map(function (k) { return inputs[k].field.label || k; }).join(', ');
        return typeof opts.validate === 'function' ? opts.validate(v) : null;
      }
      function refresh() {
        var msg = problem();
        err.textContent = msg || '';
        confirm.disabled = !!msg;
        if (typeof opts.preview === 'function') {
          var pv = opts.preview(values());
          if (pv != null) err.textContent = msg || pv;
        }
      }

      var done = false;
      function finish(v) {
        if (done) return;
        done = true;
        document.removeEventListener('keydown', onKey, true);
        if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
        resolve(v);
      }
      function onKey(e) {
        var act = dialogKey(e, {
          active: document.activeElement, confirm: confirm, cancel: cancel,
          submitOnEnter: !!opts.submitOnEnter, allowCtrlEnter: true
        });
        if (act === 'none') return;
        if (e.preventDefault) e.preventDefault();
        if (e.stopPropagation) e.stopPropagation();
        if (act === 'cancel') finish(null);
        else if (!problem()) finish(values());
      }
      cancel.addEventListener('click', function () { finish(null); });
      confirm.addEventListener('click', function () { if (!problem()) finish(values()); });
      backdrop.addEventListener('click', function (e) { if (e.target === backdrop) finish(null); });
      document.addEventListener('keydown', onKey, true);
      document.body.appendChild(backdrop);
      refresh();
      (first || cancel).focus();
    });
  };

  global.portalStatus = function (el, message, isError) {
    if (!el) return;
    el.textContent = message || '';
    el.style.color = isError ? 'var(--danger, #f85149)' : '';
  };
})(window);
