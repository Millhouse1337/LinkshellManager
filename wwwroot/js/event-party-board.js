// Officer party-board editing on the web event page. Mirrors the Discord Activity's
// party-setup-panel: an "Edit layout" toggle reveals drag-to-rearrange slots, inline
// job edits, member moves, and add/remove/rename. Each action fetch()-POSTs to the
// EventController.PartyBoardEdit endpoints (antiforgery via the configured header), then
// re-fetches PartyBoardPartial to refresh ONLY the board (the page's live timers stay
// untouched). Edit affordances are server-rendered with the `ps-edit-only` class and
// shown by toggling `.ps-editing` on the board root.
(function () {
  'use strict';

  function csrfHeaders() {
    const h = { 'Content-Type': 'application/json' };
    if (window.LSM_CSRF_HEADER && window.LSM_CSRF_TOKEN) {
      h[window.LSM_CSRF_HEADER] = window.LSM_CSRF_TOKEN;
    }
    return h;
  }

  async function postEdit(action, body) {
    let res;
    try {
      res = await fetch('/Event/' + action, { method: 'POST', headers: csrfHeaders(), body: JSON.stringify(body) });
    } catch {
      alert('Network error — the board change was not saved.');
      return false;
    }
    let data = {};
    try { data = await res.json(); } catch { /* non-JSON */ }
    if (!res.ok || data.success === false) {
      alert((data && data.error) || 'That board change could not be saved.');
      return false;
    }
    return true;
  }

  // The slot index where a dragged slot should land, given the pointer Y.
  function dragAfterElement(list, y) {
    const items = Array.prototype.slice.call(list.querySelectorAll('.event-ps-slot:not(.ps-dragging)'));
    let closest = { offset: -Infinity, el: null };
    for (const child of items) {
      const box = child.getBoundingClientRect();
      const offset = y - box.top - box.height / 2;
      if (offset < 0 && offset > closest.offset) { closest = { offset, el: child }; }
    }
    return closest.el;
  }

  function init(root, startEditing) {
    if (!root || root.__epbInit) { return; }
    root.__epbInit = true;
    if (root.getAttribute('data-can-manage') !== '1') { return; }

    const eventId = Number(root.getAttribute('data-event-id'));
    let editing = false;
    let dragSlot = null;

    const toggleBtn = root.querySelector('[data-board-edit-toggle]');

    function setEditing(on) {
      editing = on;
      root.classList.toggle('ps-editing', on);
      if (toggleBtn) { toggleBtn.textContent = on ? 'Done editing' : 'Edit layout'; toggleBtn.classList.toggle('primary', on); }
      root.querySelectorAll('.event-ps-slot').forEach(s => { s.draggable = on; });
      if (on) { populateMoveTargets(); }
    }

    if (toggleBtn) { toggleBtn.addEventListener('click', () => setEditing(!editing)); }

    // Every OPEN slot (no occupant chip), labelled, as a move/seat target.
    function openSlotTargets() {
      const out = [];
      root.querySelectorAll('.event-ps-alliance').forEach((al, ai) => {
        al.querySelectorAll('.event-ps-party').forEach(pt => {
          const pname = ((pt.querySelector('[data-rename-party]') && pt.querySelector('[data-rename-party]').value) ||
            (pt.querySelector('.event-ps-party__head .ps-view-only') && pt.querySelector('.event-ps-party__head .ps-view-only').textContent) || 'Party').trim();
          pt.querySelectorAll('.event-ps-slot').forEach(sl => {
            if (!sl.querySelector('.event-ps-chip')) {
              const reqEl = sl.querySelector('.event-ps-slot__label > span:not(.event-ps-leader-tag):not(.ps-drag-handle)');
              const label = (reqEl ? reqEl.textContent : 'slot').trim();
              out.push({ slotId: Number(sl.getAttribute('data-slot-id')), label: 'A' + (ai + 1) + ' ' + pname + ': ' + label });
            }
          });
        });
      });
      return out;
    }

    function populateMoveTargets() {
      const targets = openSlotTargets();
      root.querySelectorAll('[data-move-member], [data-seat-member]').forEach(sel => {
        sel.querySelectorAll('option[data-target]').forEach(o => o.remove());
        targets.forEach(t => {
          const o = document.createElement('option');
          o.value = String(t.slotId);
          o.textContent = '→ ' + t.label;
          o.setAttribute('data-target', '');
          sel.appendChild(o);
        });
      });
    }

    async function refresh() {
      let res;
      try { res = await fetch('/Event/PartyBoardPartial?eventId=' + eventId, { headers: { 'X-Requested-With': 'XMLHttpRequest' } }); }
      catch { return; }
      if (!res.ok) { return; }
      const html = await res.text();
      const tmp = document.createElement('div');
      tmp.innerHTML = html.trim();
      const fresh = tmp.querySelector('[data-event-party-board]');
      if (!fresh) { return; }
      const wasEditing = editing;
      root.replaceWith(fresh);
      init(fresh, wasEditing);
    }

    // ----- native drag-drop: reorder slots within a party / move between parties -----
    root.addEventListener('dragstart', e => {
      if (!editing) { return; }
      const slot = e.target.closest('.event-ps-slot');
      if (!slot || !root.contains(slot)) { return; }
      dragSlot = slot;
      slot.classList.add('ps-dragging');
      e.dataTransfer.effectAllowed = 'move';
      try { e.dataTransfer.setData('text/plain', slot.getAttribute('data-slot-id')); } catch { /* IE */ }
    });

    root.addEventListener('dragend', () => {
      if (dragSlot) { dragSlot.classList.remove('ps-dragging'); }
      dragSlot = null;
    });

    root.addEventListener('dragover', e => {
      if (!editing || !dragSlot) { return; }
      const list = e.target.closest('[data-slot-list]');
      if (!list) { return; }
      e.preventDefault();
      const after = dragAfterElement(list, e.clientY);
      if (after == null) { list.appendChild(dragSlot); }
      else { list.insertBefore(dragSlot, after); }
    });

    root.addEventListener('drop', async e => {
      if (!editing || !dragSlot) { return; }
      const list = e.target.closest('[data-slot-list]');
      if (!list) { return; }
      e.preventDefault();
      const party = list.closest('[data-party-id]');
      const slotId = Number(dragSlot.getAttribute('data-slot-id'));
      const targetPartyId = Number(party.getAttribute('data-party-id'));
      const targetIndex = Array.prototype.slice.call(list.querySelectorAll('.event-ps-slot')).indexOf(dragSlot);
      dragSlot.classList.remove('ps-dragging');
      dragSlot = null;
      if (await postEdit('MoveSlot', { eventId, slotId, targetPartyId, targetIndex })) { await refresh(); }
    });

    // ----- click actions (job edit toggle/save, delete slot, add slot/party, remove party) -----
    root.addEventListener('click', async e => {
      if (!editing) { return; }
      const slotEl = e.target.closest('.event-ps-slot');

      if (e.target.closest('[data-edit-slot]') && slotEl) {
        const je = slotEl.querySelector('[data-job-edit]');
        if (je) { je.hidden = !je.hidden; }
        return;
      }
      if (e.target.closest('[data-job-save]') && slotEl) {
        const je = slotEl.querySelector('[data-job-edit]');
        const body = {
          eventId,
          slotId: Number(slotEl.getAttribute('data-slot-id')),
          role: je.querySelector('[data-job-role]').value || null,
          mainJob: je.querySelector('[data-job-main]').value || null,
          subJob: je.querySelector('[data-job-sub]').value || null
        };
        if (await postEdit('EditSlotRequirement', body)) { await refresh(); }
        return;
      }
      if (e.target.closest('[data-job-cancel]') && slotEl) {
        // Back out without saving: revert each select to its server-rendered value
        // (defaultSelected reflects the markup regardless of user changes), then hide.
        const je = slotEl.querySelector('[data-job-edit]');
        if (je) {
          je.querySelectorAll('select').forEach(sel => {
            const def = Array.prototype.find.call(sel.options, o => o.defaultSelected);
            sel.value = def ? def.value : '';
          });
          je.hidden = true;
        }
        return;
      }
      if (e.target.closest('[data-delete-slot]') && slotEl) {
        if (await postEdit('DeleteBoardSlot', { eventId, slotId: Number(slotEl.getAttribute('data-slot-id')) })) { await refresh(); }
        return;
      }
      if (e.target.closest('[data-add-slot]')) {
        const party = e.target.closest('[data-party-id]');
        if (await postEdit('AddBoardSlot', { eventId, partyId: Number(party.getAttribute('data-party-id')) })) { await refresh(); }
        return;
      }
      if (e.target.closest('[data-add-party]')) {
        const al = e.target.closest('[data-alliance-id]');
        if (await postEdit('AddBoardParty', { eventId, allianceId: Number(al.getAttribute('data-alliance-id')) })) { await refresh(); }
        return;
      }
      if (e.target.closest('[data-remove-party]')) {
        const party = e.target.closest('[data-party-id]');
        if (await postEdit('RemoveBoardParty', { eventId, partyId: Number(party.getAttribute('data-party-id')) })) { await refresh(); }
        return;
      }
    });

    // ----- change actions (rename, move member, seat attendee) -----
    root.addEventListener('change', async e => {
      const t = e.target;

      // Seat an Also-Attending member into an open slot. Available to officers
      // WITHOUT edit mode (the assign dropdown isn't ps-edit-only), so it's handled
      // BEFORE the edit-mode guard below. init() already gated on data-can-manage.
      if (t.matches('[data-seat-member]')) {
        const chip = t.closest('[data-also-app-user]');
        const val = t.value;
        if (!val) { return; }
        const body = { eventId, fromSlotId: null, toSlotId: Number(val), appUserId: (chip && chip.getAttribute('data-also-app-user')) || null, discordUserId: null };
        if (await postEdit('MoveMember', body)) { await refresh(); }
        return;
      }

      if (!editing) { return; }

      if (t.matches('[data-rename-alliance]')) {
        const al = t.closest('[data-alliance-id]');
        if (await postEdit('RenameBoard', { eventId, allianceId: Number(al.getAttribute('data-alliance-id')), partyId: null, name: t.value })) { await refresh(); }
        return;
      }
      if (t.matches('[data-rename-party]')) {
        const party = t.closest('[data-party-id]');
        if (await postEdit('RenameBoard', { eventId, allianceId: null, partyId: Number(party.getAttribute('data-party-id')), name: t.value })) { await refresh(); }
        return;
      }
      if (t.matches('[data-move-member]')) {
        const slot = t.closest('.event-ps-slot');
        const val = t.value;
        if (!val) { return; }
        const body = { eventId, fromSlotId: Number(slot.getAttribute('data-slot-id')), toSlotId: val === 'also' ? null : Number(val), appUserId: null, discordUserId: null };
        if (await postEdit('MoveMember', body)) { await refresh(); }
        return;
      }
    });

    // Fill the Also-Attending "Assign to slot" dropdowns up front so officers can
    // seat members WITHOUT entering edit mode (those selects aren't ps-edit-only).
    populateMoveTargets();

    if (startEditing) { setEditing(true); }
  }

  // ----- live updates for ALL viewers (not just officers) -----
  // Long-poll the shared change feed (cookie-authed — the SAME endpoint the Discord
  // Activity uses) and swap in a fresh board whenever someone else signs up, withdraws,
  // or an officer edits the layout, so passive web viewers stay live without reloading.
  // This runs independently of init()'s officer-only edit wiring.
  let liveStarted = false;

  function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

  async function refreshCurrentBoard() {
    const board = document.querySelector('[data-event-party-board]');
    if (!board) { return; }
    // Don't clobber an officer who's mid-edit (drag / job form open). Their own edits
    // refresh the board directly; they'll pick up others' changes when they exit.
    if (board.classList.contains('ps-editing')) { return; }
    const eventId = Number(board.getAttribute('data-event-id'));
    let res;
    try { res = await fetch('/Event/PartyBoardPartial?eventId=' + eventId, { headers: { 'X-Requested-With': 'XMLHttpRequest' } }); }
    catch { return; }
    if (!res.ok) { return; }
    const tmp = document.createElement('div');
    tmp.innerHTML = (await res.text()).trim();
    const fresh = tmp.querySelector('[data-event-party-board]');
    if (!fresh) { return; }
    board.replaceWith(fresh);
    init(fresh, false);
  }

  async function startLiveUpdates() {
    if (liveStarted) { return; }
    const board = document.querySelector('[data-event-party-board]');
    if (!board) { return; }
    const linkshellId = Number(board.getAttribute('data-linkshell-id'));
    if (!linkshellId) { return; }
    liveStarted = true;
    let since = -1;
    for (;;) {
      let data;
      try {
        const res = await fetch('/api/activity/changes?linkshellId=' + linkshellId + '&since=' + since, { credentials: 'same-origin' });
        if (!res.ok) { await sleep(5000); continue; }
        data = await res.json();
      } catch { await sleep(5000); continue; }
      const version = typeof data.version === 'number' ? data.version : 0;
      if (since < 0) { since = version; continue; } // first response just primes the baseline
      const advanced = version !== since;
      since = version;
      const areas = Array.isArray(data.areas) ? data.areas : [];
      if (advanced && areas.some(function (a) { return a === 'events' || a === 'parties'; })) {
        await refreshCurrentBoard();
      }
    }
  }

  // Submit the board's member sign-up / no-slot / withdraw forms (class ps-view-only) via
  // fetch so the page DOESN'T reload — then swap in the fresh board. Delegated at the
  // document level so it works for EVERY viewer (init()'s edit wiring is officer-only).
  let signupAjaxWired = false;
  function wireBoardSignupAjax() {
    if (signupAjaxWired) { return; }
    signupAjaxWired = true;
    document.addEventListener('submit', async function (e) {
      const form = e.target;
      if (!(form instanceof HTMLFormElement) || !form.classList.contains('ps-view-only')) { return; }
      if (!form.closest('[data-event-party-board]')) { return; }
      e.preventDefault();
      const btn = form.querySelector('button[type="submit"]');
      if (btn) { btn.disabled = true; }
      const fd = new FormData(form);
      // FormData omits the clicked submit button — re-add it so e.g. asLeader=true/false posts.
      if (e.submitter && e.submitter.name) { fd.append(e.submitter.name, e.submitter.value); }
      try {
        await fetch(form.action, { method: 'POST', body: fd, headers: { 'X-Requested-With': 'XMLHttpRequest' }, credentials: 'same-origin' });
      } catch {
        // Network error — fall back to a normal submit so the user isn't stuck.
        if (btn) { btn.disabled = false; }
        form.submit();
        return;
      }
      await refreshCurrentBoard();
    });
  }

  function boot() {
    document.querySelectorAll('[data-event-party-board]').forEach(el => init(el, false));
    startLiveUpdates();
    wireBoardSignupAjax();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
