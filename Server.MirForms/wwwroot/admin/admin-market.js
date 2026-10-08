/**
 * Market section (#section-market) — B5. Player consignments / auctions in Envir.Main.Auctions.
 *
 *   GET  /api/admin/market?q=&by=any|item|seller|buyer&status=active|expired|sold|all&sort=&dir=&page=&pageSize=
 *        → {rows, total, page, pages, counts:{active, expired, sold, all}, listingDays, commission, world}
 *   POST /api/admin/market/{auctionId}/expire {reason?}             end the listing; the seller collects it at the market
 *   POST /api/admin/market/{auctionId}/remove {reason, returnItem}  take it down (item / earnings mailed back, bids refunded)
 *
 * Mutations need a running world (503 otherwise) and are audited server-side. The table polls every 15 s while visible and is
 * only rebuilt when the data changed. Deep link: #section=market&q=<text>&by=<item|seller|buyer>&status=<…>.
 */
(function (global) {
  'use strict';

  var SECTION = 'market';
  var POLL_MS = 15000;
  var PAGE_SIZE = 50;
  var TYPE_NAMES = { 0: 'Consign', 1: 'Auction', 2: 'GameShop' };

  var state = { page: 1, pages: 1, sort: '', dir: 'desc', q: '', by: 'any', status: 'active', seq: 0, signature: '', busy: false };
  var root = null;
  var stopPoll = null;
  var searchTimer = null;
  var rowsById = {};
  var lastRows = [];
  var meta = { listingDays: 7, commission: 0.05, world: 'running' };

  function esc(v) { return OpsUI.esc(v); }
  function num(v) { return (Number(v) || 0).toLocaleString('en-US'); }
  function q(sel) { return root ? root.querySelector(sel) : null; }

  function chip(name, opts) {
    if (!name) return '<span class="mir-muted">—</span>';
    return OpsUI.playerLink(name, opts || {});
  }

  function statusPill(r) {
    if (r.status === 'sold') return '<span class="ops-pill ops-pill-ok">sold</span>';
    if (r.status === 'expired') return '<span class="ops-pill ops-pill-warn">expired</span>';
    var exp = OpsUI.toDate(r.expiry);
    if (exp && exp.getTime() < Date.now()) return '<span class="ops-pill ops-pill-warn" title="Past its end date; the server ends it on the next auction tick">ending</span>';
    return '<span class="ops-pill">active</span>';
  }

  function render(el) {
    el.innerHTML =
      '<div class="mir-card">' +
      '  <div class="toolbar">' +
      '    <h2>Market</h2>' +
      '    <div class="mkt-tools">' +
      '      <input type="search" class="fd-input" data-section-search data-role="search" placeholder="Search item, seller, buyer or #id" aria-label="Search listings" />' +
      '      <select class="fd-input" data-role="by" aria-label="Search in">' +
      '        <option value="any">Anything</option><option value="item">Item</option><option value="seller">Seller</option><option value="buyer">Bidder / buyer</option>' +
      '      </select>' +
      '      <button type="button" class="mir-btn" data-role="refresh">Refresh</button>' +
      '      <button type="button" class="mir-btn" data-role="csv" title="Export the current page as CSV">CSV</button>' +
      '    </div>' +
      '  </div>' +
      '  <div class="mkt-chips" role="tablist" data-role="chips">' +
      '    <button type="button" class="mir-btn" data-status="active">Active <span data-count="active"></span></button>' +
      '    <button type="button" class="mir-btn" data-status="expired">Expired <span data-count="expired"></span></button>' +
      '    <button type="button" class="mir-btn" data-status="sold">Sold <span data-count="sold"></span></button>' +
      '    <button type="button" class="mir-btn" data-status="all">All <span data-count="all"></span></button>' +
      '    <span class="mir-muted mkt-hint" data-role="hint"></span>' +
      '  </div>' +
      '  <p class="mir-muted mkt-world hidden" data-role="world-note">The world is not running: listings are read-only until it starts.</p>' +
      '  <div class="ops-table-scroll">' +
      '    <table class="user-table users-table mkt-table" data-role="table">' +
      '      <thead><tr>' +
      '        <th data-sort="id">ID</th>' +
      '        <th data-sort="item">Item</th>' +
      '        <th data-sort="count">Qty</th>' +
      '        <th data-sort="type">Type</th>' +
      '        <th data-sort="price">Price</th>' +
      '        <th data-sort="bid">Bid</th>' +
      '        <th data-sort="seller">Seller</th>' +
      '        <th>Bidder / buyer</th>' +
      '        <th data-sort="date">Listed</th>' +
      '        <th data-sort="expiry">Ends</th>' +
      '        <th data-sort="status">Status</th>' +
      '        <th></th>' +
      '      </tr></thead>' +
      '      <tbody data-role="tbody"><tr><td colspan="12" class="mir-muted">Loading…</td></tr></tbody>' +
      '    </table>' +
      '  </div>' +
      '  <div class="mkt-pager">' +
      '    <button type="button" class="mir-btn" data-role="prev">&lsaquo; Prev</button>' +
      '    <span class="mir-muted" data-role="status"></span>' +
      '    <button type="button" class="mir-btn" data-role="next">Next &rsaquo;</button>' +
      '  </div>' +
      '</div>';

    q('[data-role="refresh"]').addEventListener('click', function (e) { OpsUI.withBusy(e.currentTarget, function () { return load(true); }); });
    q('[data-role="csv"]').addEventListener('click', exportCsv);
    q('[data-role="prev"]').addEventListener('click', function () { if (state.page > 1) { state.page--; load(true); } });
    q('[data-role="next"]').addEventListener('click', function () { if (state.page < state.pages) { state.page++; load(true); } });

    var search = q('[data-role="search"]');
    search.addEventListener('input', function () {
      clearTimeout(searchTimer);
      searchTimer = setTimeout(applySearch, 300);
    });
    search.addEventListener('keydown', function (e) {
      if (e.key !== 'Enter') return;
      e.preventDefault();
      clearTimeout(searchTimer);
      applySearch();
    });
    q('[data-role="by"]').addEventListener('change', function () {
      state.by = this.value;
      state.page = 1;
      global.portalSetHashParams({ by: state.by === 'any' ? null : state.by }, true);
      load(true);
    });

    Array.prototype.forEach.call(root.querySelectorAll('[data-status]'), function (b) {
      b.addEventListener('click', function () {
        state.status = b.getAttribute('data-status');
        state.page = 1;
        global.portalSetHashParams({ status: state.status === 'active' ? null : state.status }, true);
        syncChips();
        load(true);
      });
    });

    Array.prototype.forEach.call(root.querySelectorAll('th[data-sort]'), function (th) {
      th.addEventListener('click', function () {
        var key = th.getAttribute('data-sort');
        if (state.sort === key) state.dir = state.dir === 'asc' ? 'desc' : 'asc';
        else { state.sort = key; state.dir = (key === 'item' || key === 'seller' || key === 'expiry') ? 'asc' : 'desc'; }
        state.page = 1;
        load(true);
      });
    });

    q('[data-role="tbody"]').addEventListener('click', function (e) {
      var b = e.target && e.target.closest ? e.target.closest('[data-act]') : null;
      if (!b) return;
      var row = rowsById[b.getAttribute('data-id')];
      if (!row) return;
      if (b.getAttribute('data-act') === 'expire') expireListing(row);
      else if (b.getAttribute('data-act') === 'remove') removeListing(row);
    });

    syncChips();
  }

  function applySearch() {
    var search = q('[data-role="search"]');
    state.q = search ? search.value.trim() : '';
    state.page = 1;
    global.portalSetHashParams({ q: state.q || null }, true);
    load(true);
  }

  function syncChips() {
    Array.prototype.forEach.call(root.querySelectorAll('[data-status]'), function (b) {
      var on = b.getAttribute('data-status') === state.status;
      b.classList.toggle('primary', on);
      b.setAttribute('aria-selected', String(on));
    });
  }

  function syncSort() {
    Array.prototype.forEach.call(root.querySelectorAll('th[data-sort]'), function (th) {
      var on = th.getAttribute('data-sort') === state.sort;
      th.setAttribute('aria-sort', on ? (state.dir === 'desc' ? 'descending' : 'ascending') : 'none');
    });
  }

  async function load(force) {
    if (!root) return;
    if (!force && state.busy) return; // a dialog is open: do not shuffle rows under it
    var token = ++state.seq;
    var url = '/api/admin/market?page=' + state.page + '&pageSize=' + PAGE_SIZE +
      '&status=' + encodeURIComponent(state.status) + '&by=' + encodeURIComponent(state.by) +
      (state.sort ? '&sort=' + encodeURIComponent(state.sort) + '&dir=' + state.dir : '') +
      (state.q ? '&q=' + encodeURIComponent(state.q) : '');
    var st = q('[data-role="status"]');
    var j;
    try {
      j = OpsUI.unwrap(await OpsUI.fetchJson(url));
    } catch (e) {
      if (token !== state.seq) return;
      if (st) st.textContent = 'Error: ' + (e && e.message || e);
      if (force) portalToast('Could not load the market: ' + (e && e.message || e), 'error');
      return;
    }
    if (token !== state.seq || !j) return;

    meta.listingDays = j.listingDays || meta.listingDays;
    meta.commission = j.commission != null ? j.commission : meta.commission;
    meta.world = j.world || 'running';
    state.page = Number(j.page) || 1;
    state.pages = Number(j.pages) || 1;

    var counts = j.counts || {};
    ['active', 'expired', 'sold', 'all'].forEach(function (k) {
      var s = root.querySelector('[data-count="' + k + '"]');
      if (s) s.textContent = '(' + num(counts[k]) + ')';
    });
    var hint = q('[data-role="hint"]');
    if (hint) hint.textContent = 'Listings run ' + meta.listingDays + ' days · ' + Math.round(meta.commission * 100) + '% commission on sales';
    var note = q('[data-role="world-note"]');
    if (note) note.classList.toggle('hidden', meta.world === 'running');

    var rows = j.rows || [];
    var sig = JSON.stringify(rows) + '|' + meta.world;
    if (force || sig !== state.signature) {
      state.signature = sig;
      lastRows = rows;
      rowsById = {};
      rows.forEach(function (r) { rowsById[String(r.auctionId)] = r; });
      renderRows(rows);
    }

    q('[data-role="prev"]').disabled = state.page <= 1;
    q('[data-role="next"]').disabled = state.page >= state.pages;
    if (st) st.textContent = num(j.total) + ' listing(s) · page ' + state.page + ' / ' + state.pages;
    syncSort();
  }

  function renderRows(rows) {
    var tbody = q('[data-role="tbody"]');
    if (!tbody) return;
    if (!rows.length) {
      tbody.innerHTML = '<tr><td colspan="12" class="mir-muted">No ' + (state.status === 'all' ? '' : esc(state.status) + ' ') + 'listings' +
        (state.q ? ' match “' + esc(state.q) + '”' : '') + '.</td></tr>';
      return;
    }
    var live = meta.world === 'running';
    var dis = live ? '' : ' disabled title="The world is not running"';
    tbody.innerHTML = rows.map(function (r) {
      var id = esc(r.auctionId);
      var sellerOpts = { account: r.sellerAccount, accountIndex: r.sellerAccountIndex, characterIndex: r.sellerIndex, online: !!r.sellerOnline };
      var buyerOpts = { characterIndex: r.buyerIndex };
      var bid = Number(r.itemType) === 1 ? (r.buyer ? num(r.currentBid) : '<span class="mir-muted">no bid</span>') : '<span class="mir-muted">—</span>';
      var ends = OpsUI.toDate(r.expiry);
      var actions = '';
      if (r.status === 'active') actions += '<button type="button" class="mir-btn ops-row-btn" data-act="expire" data-id="' + id + '"' + dis + '>Expire…</button> ';
      actions += '<button type="button" class="mir-btn danger ops-row-btn" data-act="remove" data-id="' + id + '"' + dis + '>Remove…</button>';
      return '<tr data-row="' + id + '">' +
        '<td class="mono">' + id + '</td>' +
        '<td>' + esc(r.itemName) + ' <span class="mir-muted">#' + esc(r.itemIndex) + '</span></td>' +
        '<td>' + num(r.count) + '</td>' +
        '<td>' + esc(r.itemTypeName || TYPE_NAMES[r.itemType] || r.itemType) + '</td>' +
        '<td>' + num(r.price) + '</td>' +
        '<td>' + bid + '</td>' +
        '<td>' + chip(r.seller, sellerOpts) + '</td>' +
        '<td>' + (r.buyer ? chip(r.buyer, buyerOpts) : '<span class="mir-muted">—</span>') + '</td>' +
        '<td><span title="' + esc(OpsUI.fmtDate(r.consignmentDate)) + '">' + esc(OpsUI.ago(r.consignmentDate)) + '</span></td>' +
        '<td>' + (ends ? '<span title="' + esc(ends.toLocaleString()) + '">' + esc(OpsUI.ago(ends)) + '</span>' : '—') + '</td>' +
        '<td>' + statusPill(r) + '</td>' +
        '<td class="users-actions">' + actions + '</td>' +
        '</tr>';
    }).join('');
  }

  function describe(r) {
    return '#' + r.auctionId + ' — ' + r.itemName + (r.seller ? ' (seller ' + r.seller + ')' : '');
  }

  async function expireListing(r) {
    state.busy = true;
    try {
      var bidNote = Number(r.itemType) === 1 && r.buyer ? ' The current bid of ' + num(r.currentBid) + ' gold by ' + r.buyer + ' is refunded when the seller collects the item.' : '';
      var v = await portalDialog({
        title: 'Expire listing',
        message: 'Ends ' + describe(r) + ' now. The item goes back to the seller through the normal market collection.' + bidNote,
        confirmLabel: 'Expire listing',
        danger: true,
        fields: [{ name: 'reason', label: 'Reason (shown to the seller if online, audit log)', type: 'text', placeholder: 'Mispriced listing' }]
      });
      if (!v) return;
      var j = await OpsUI.post('/api/admin/market/' + encodeURIComponent(r.auctionId) + '/expire', { reason: v.reason || '' });
      if (j.__ok || j.__status === 404 || j.__status === 409) await load(true);
    } finally {
      state.busy = false;
    }
  }

  async function removeListing(r) {
    state.busy = true;
    try {
      var sold = r.status === 'sold';
      var returnLabel = sold
        ? 'Mail the seller their earnings (sale price minus ' + Math.round(meta.commission * 100) + '% commission)'
        : 'Mail the item back to the seller';
      var notes = [];
      if (sold) notes.push('This listing is already sold: the buyer keeps the item.');
      if (!sold && Number(r.itemType) === 1 && r.buyer) notes.push('The bid of ' + num(r.currentBid) + ' gold by ' + r.buyer + ' is refunded by mail.');
      notes.push('Unticking the box destroys the ' + (sold ? 'earnings' : 'item') + ' (cannot be undone).');
      var v = await portalDialog({
        title: 'Remove listing',
        message: 'Takes ' + describe(r) + ' off the market. ' + notes.join(' '),
        confirmLabel: 'Remove listing',
        danger: true,
        fields: [
          { name: 'reason', label: 'Reason (mailed / shown to the seller, audit log)', type: 'text', required: true, placeholder: 'Rule violation: price manipulation' },
          { name: 'returnItem', label: returnLabel, type: 'checkbox', value: true }
        ]
      });
      if (!v) return;
      var j = await OpsUI.post('/api/admin/market/' + encodeURIComponent(r.auctionId) + '/remove', { reason: v.reason, returnItem: !!v.returnItem });
      if (j.__ok || j.__status === 404) await load(true);
    } finally {
      state.busy = false;
    }
  }

  function exportCsv() {
    if (!lastRows.length) { portalToast('Nothing to export.', 'warn'); return; }
    var header = ['AuctionId', 'Item', 'ItemIndex', 'Count', 'Type', 'Price', 'CurrentBid', 'Seller', 'SellerAccount', 'Buyer', 'Listed', 'Ends', 'Status'];
    var rows = lastRows.map(function (r) {
      return [r.auctionId, r.itemName, r.itemIndex, r.count, r.itemTypeName, r.price, r.currentBid, r.seller || '', r.sellerAccount || '', r.buyer || '',
        r.consignmentDate, r.expiry, r.status];
    });
    OpsUI.download('market-' + state.status + '-page' + state.page + '.csv', OpsUI.toCsv(header, rows), 'text/csv');
  }

  function applyParams(params, reload) {
    if (!root || !params) return false;
    var changed = false;
    if (params.q != null && String(params.q) !== state.q) { state.q = String(params.q); changed = true; }
    if (params.by && ['any', 'item', 'seller', 'buyer'].indexOf(params.by) >= 0 && params.by !== state.by) { state.by = params.by; changed = true; }
    if (params.status && ['active', 'expired', 'sold', 'all'].indexOf(params.status) >= 0 && params.status !== state.status) { state.status = params.status; changed = true; }
    if (changed) {
      state.page = 1;
      var search = q('[data-role="search"]');
      if (search) search.value = state.q;
      var by = q('[data-role="by"]');
      if (by) by.value = state.by;
      syncChips();
      if (reload) load(true);
    }
    return changed;
  }

  global.portalRegisterDeepLink(SECTION, function (params) { applyParams(params, true); });

  global.AdminSections.register(SECTION, {
    load: function (el) {
      root = el;
      render(el);
      var hp = global.portalHashParams();
      if (hp.section === SECTION) applyParams(hp, false);
    },
    show: function () {
      if (!stopPoll) stopPoll = global.AdminSections.poll(SECTION, function () { return load(false); }, POLL_MS);
      return load(true);
    },
    hide: function () {
      if (stopPoll) { stopPoll(); stopPoll = null; }
    }
  });
})(window);
