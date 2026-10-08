/**
 * DB reference lookups for ag-grid dropdown editors. GET /api/admin/lookups/{name} -> { options: [{ value, label, detail? }] }
 * (WoOOL's { index, label } shape is accepted too). Options are normalised to { index, value, label, detail }.
 */
(function (global) {
  var cache = Object.create(null);
  var LIST_LIMIT = 250;
  // Lookups whose options get an icon in the dropdown. MirCraft has no icon endpoint yet: none.
  var ICON_TYPE = {};
  var ICON_URL = null;   // function (type, index) -> url, when an icon endpoint exists

  function iconUrl(lookupName, index) {
    var type = ICON_TYPE[lookupName];
    return type && ICON_URL && Number(index) > 0 ? ICON_URL(type, Number(index)) : null;
  }

  // { value, label, detail } (MirCraft) or { index, label } (WoOOL) -> { index, value, label, detail, ... }; labels made unique
  // (the editor maps the chosen label back to the index) by prefixing "<index> — " to repeated ones.
  function normaliseOptions(options) {
    var list = (options || []).map(function (o) {
      o = o || {};
      var idx = o.index != null ? Number(o.index) : (o.value != null ? Number(o.value) : 0);
      if (isNaN(idx)) idx = 0;
      return Object.assign({}, o, { index: idx, value: idx, label: o.label == null ? '' : String(o.label) });
    });
    var count = Object.create(null);
    list.forEach(function (o) { count[o.label] = (count[o.label] || 0) + 1; });
    list.forEach(function (o) {
      if (o.label && count[o.label] > 1) o.label = o.index + ' — ' + o.label;
    });
    return list;
  }

  function SearchableLookupCellEditor() {}

  SearchableLookupCellEditor.prototype.init = function (params) {
    this.params = params;
    this.options = params.options || [];
    this.selectedLabel = params.value == null ? '' : String(params.value);
    this.activeIndex = -1;

    this.eGui = document.createElement('div');
    this.eGui.className = 'db-lookup-editor';

    this.eInput = document.createElement('input');
    this.eInput.type = 'text';
    this.eInput.className = 'db-lookup-editor__filter';
    this.eInput.value = this.selectedLabel;
    this.eInput.placeholder = 'Type to filter…';
    this.eInput.setAttribute('aria-label', 'Filter lookup options');

    this.eMeta = document.createElement('div');
    this.eMeta.className = 'db-lookup-editor__meta';

    this.eList = document.createElement('div');
    this.eList.className = 'db-lookup-editor__list';

    this.eGui.appendChild(this.eInput);
    this.eGui.appendChild(this.eMeta);
    this.eGui.appendChild(this.eList);

    var self = this;
    this.eInput.addEventListener('input', function () {
      self.activeIndex = -1;
      self.renderList();
    });
    this.eInput.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        self.moveActive(1);
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        self.moveActive(-1);
      } else if (e.key === 'Enter') {
        e.preventDefault();
        self.pickActive();
      } else if (e.key === 'Escape') {
        e.preventDefault();
        self.params.stopEditing(true);
      }
    });

    this.renderList();
  };

  SearchableLookupCellEditor.prototype.getFilteredOptions = function () {
    return filterOptions(this.options, this.eInput.value);
  };

  // §6 step 5 (D-43): the filter matches the label, the detail column (level, AI, boss; item type, price; NPC map) and "#index".
  function filterOptions(options, text) {
    var q = String(text || '').trim().toLowerCase();
    if (!q) return (options || []).slice();
    var idx = /^#?\d+$/.test(q) ? Number(q.replace('#', '')) : -1;
    return (options || []).filter(function (opt) {
      if (idx >= 0 && Number(opt.index) === idx) return true;
      if (String(opt.label || '').toLowerCase().indexOf(q) >= 0) return true;
      return !!opt.detail && String(opt.detail).toLowerCase().indexOf(q) >= 0;
    });
  }

  SearchableLookupCellEditor.prototype.renderList = function () {
    var filtered = this.getFilteredOptions();
    var shown = filtered.slice(0, LIST_LIMIT);
    this.eList.innerHTML = '';
    this.eMeta.textContent = filtered.length + ' match' + (filtered.length === 1 ? '' : 'es')
      + (filtered.length > LIST_LIMIT ? ' (showing first ' + LIST_LIMIT + ')' : '');

    var self = this;
    shown.forEach(function (opt, i) {
      var btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'db-lookup-editor__option';
      if (i === self.activeIndex) btn.classList.add('active');
      if (opt.label === self.selectedLabel) btn.classList.add('selected');
      var icon = iconUrl(self.params.lookup, opt.index);
      if (icon) {
        // Lazy: only the options scrolled into view are fetched.
        var im = document.createElement('img');
        im.className = 'db-lookup-editor__icon';
        im.loading = 'lazy';
        im.alt = '';
        im.src = icon;
        im.onerror = function () { im.style.visibility = 'hidden'; };
        btn.appendChild(im);
        btn.appendChild(document.createTextNode(opt.label || '(none)'));
      } else {
        btn.textContent = opt.label || '(none)';
      }
      if (opt.detail) {
        // D-43: the desktop's extra lookup columns, to tell same-named rows apart.
        var detail = document.createElement('span');
        detail.className = 'db-lookup-editor__detail';
        detail.textContent = '#' + opt.index + ' · ' + opt.detail;
        btn.appendChild(detail);
      }
      btn.addEventListener('mousedown', function (e) {
        e.preventDefault();
        e.stopPropagation();
        self.selectedLabel = opt.label || '';
        self.params.stopEditing();
      });
      self.eList.appendChild(btn);
    });

    if (!shown.length) {
      var empty = document.createElement('div');
      empty.className = 'db-lookup-editor__empty';
      empty.textContent = 'No matches';
      this.eList.appendChild(empty);
    }
  };

  SearchableLookupCellEditor.prototype.moveActive = function (delta) {
    var count = Math.min(this.getFilteredOptions().length, LIST_LIMIT);
    if (!count) {
      this.activeIndex = -1;
      this.renderList();
      return;
    }
    if (this.activeIndex < 0) this.activeIndex = delta > 0 ? 0 : count - 1;
    else this.activeIndex = (this.activeIndex + delta + count) % count;
    this.renderList();
    var active = this.eList.querySelector('.db-lookup-editor__option.active');
    if (active) active.scrollIntoView({ block: 'nearest' });
  };

  SearchableLookupCellEditor.prototype.pickActive = function () {
    var filtered = this.getFilteredOptions().slice(0, LIST_LIMIT);
    if (!filtered.length) return;
    var opt = filtered[this.activeIndex >= 0 ? this.activeIndex : 0];
    this.selectedLabel = opt.label || '';
    this.params.stopEditing();
  };

  SearchableLookupCellEditor.prototype.getGui = function () {
    return this.eGui;
  };

  SearchableLookupCellEditor.prototype.afterGuiAttached = function () {
    this.eInput.focus();
    this.eInput.select();
  };

  SearchableLookupCellEditor.prototype.getValue = function () {
    return this.selectedLabel;
  };

  SearchableLookupCellEditor.prototype.isPopup = function () {
    return true;
  };

  SearchableLookupCellEditor.prototype.isCancelAfterEnd = function () {
    return false;
  };

  function indexLookup(options, name) {
    var byIndex = Object.create(null);
    var byLabel = Object.create(null);
    var labels = [];
    (options || []).forEach(function (opt) {
      var index = opt.index == null ? 0 : Number(opt.index);
      var label = opt.label == null ? '' : String(opt.label);
      byIndex[index] = { index: index, label: label };
      if (label) byLabel[label] = byIndex[index];
      labels.push(label);
    });
    return { options: options || [], byIndex: byIndex, byLabel: byLabel, labels: labels, name: name || null };
  }

  async function fetchLookup(name) {
    if (cache[name]) return cache[name];
    var r = await portalApi('/api/admin/lookups/' + encodeURIComponent(name));
    var j = await r.json().catch(function () { return {}; });
    if (!r.ok) throw new Error(j.message || j.error || r.statusText || ('Lookup failed: ' + name));
    var opts = j.options || (j.data && j.data.options) || [];
    cache[name] = indexLookup(normaliseOptions(opts), name);
    return cache[name];
  }

  async function loadForReferences(references) {
    var names = [];
    (references || []).forEach(function (ref) {
      if (ref.lookup && names.indexOf(ref.lookup) < 0) names.push(ref.lookup);
    });
    var out = Object.create(null);
    for (var i = 0; i < names.length; i++) {
      out[names[i]] = await fetchLookup(names[i]);
    }
    return out;
  }

  function applyReferenceColumns(colDefs, references, lookups) {
    var hideIndex = Object.create(null);
    (references || []).forEach(function (ref) {
      hideIndex[ref.indexField] = true;
    });

    return colDefs.map(function (col) {
      if (hideIndex[col.field]) {
        return Object.assign({}, col, { hide: true, editable: false, suppressColumnsToolPanel: true });
      }

      var ref = null;
      for (var i = 0; i < (references || []).length; i++) {
        if (references[i].displayField === col.field) {
          ref = references[i];
          break;
        }
      }
      if (!ref) return col;

      var lookup = lookups[ref.lookup];
      if (!lookup) return col;

      return Object.assign({}, col, {
        editable: true,
        singleClickEdit: true,
        cellClass: [].concat(col.cellClass || [], 'db-cell-lookup').filter(function (c) { return c !== 'db-cell-display'; }),
        cellEditor: SearchableLookupCellEditor,
        cellEditorParams: optionsFor(ref, lookup),
        minWidth: 100,
        maxWidth: 220,
        tooltipValueGetter: function (p) { return p.value == null ? '' : String(p.value); },
        valueGetter: function (p) {
          var idx = p.data[ref.indexField];
          if (idx != null && lookup.byIndex[idx]) return lookup.byIndex[idx].label;
          return p.data[ref.displayField] || '';
        },
        valueSetter: function (p) {
          var label = p.newValue == null ? '' : String(p.newValue);
          if (!label) {
            p.data[ref.indexField] = 0;
            p.data[ref.displayField] = null;
            return true;
          }
          var opt = lookup.byLabel[label];
          if (!opt) return false;
          p.data[ref.indexField] = opt.index;
          p.data[ref.displayField] = opt.label;
          return true;
        },
        valueFormatter: function (p) {
          return p.value == null ? '' : String(p.value);
        }
      });
    });
  }

  // The editor's options for one row. A map-region lookup (its options carry mapIndex) offers only the regions of the
  // row's map when the row has one (MapIndex), plus "none" and the current value; other lookups offer everything.
  function optionsFor(ref, lookup) {
    var byMap = lookup.options.some(function (o) { return o && o.mapIndex != null; });
    if (!byMap) return lookup.name && ICON_TYPE[lookup.name] ? { options: lookup.options, lookup: lookup.name } : { options: lookup.options };
    return function (params) {
      var map = params && params.data ? Number(params.data.MapIndex) : 0;
      if (!(map > 0)) return { options: lookup.options };
      var current = params.data[ref.indexField];
      return {
        options: lookup.options.filter(function (o) {
          return !o.index || o.mapIndex === map || o.index === current;
        })
      };
    };
  }

  function gridOptionsForReferences(references) {
    if (!references || !references.length) return {};
    // No grid-wide singleClickEdit: the lookup columns set it on their own column (applyReferenceColumns).
    return {
      stopEditingWhenCellsLoseFocus: true
    };
  }

  global.DbLookups = {
    SearchableLookupCellEditor: SearchableLookupCellEditor,
    fetch: fetchLookup,
    loadForReferences: loadForReferences,
    applyReferenceColumns: applyReferenceColumns,
    optionsFor: optionsFor,
    gridOptionsForReferences: gridOptionsForReferences,
    clearCache: function () { cache = Object.create(null); },
    filterOptions: filterOptions,
    normaliseOptions: normaliseOptions,
    iconUrl: iconUrl
  };
})(window);
