(function () {
    'use strict';

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');
    }

    // A slot's <select> options: a blank placeholder then one option per
    // value (value == label). The server derives the requirement from what's
    // filled (main job > role > any).
    function selectOptions(values, placeholder) {
        var html = '<option value="">' + escapeHtml(placeholder) + '</option>';
        (values || []).forEach(function (v) {
            html += '<option value="' + escapeHtml(v) + '">' + escapeHtml(v) + '</option>';
        });
        return html;
    }

    // Role <select>: a "Select role" placeholder (role must be picked first),
    // then an explicit "Any Role" (= open slot, no job constraint), then the
    // concrete roles (Tank/Heal/Support/DPS).
    function roleSelectOptions(values) {
        var html = '<option value="">Select role</option>'
                 + '<option value="Any Role">Any Role</option>';
        (values || []).forEach(function (v) {
            html += '<option value="' + escapeHtml(v) + '">' + escapeHtml(v) + '</option>';
        });
        return html;
    }

    function init() {
        var form = document.getElementById('party-setup-form');
        if (!form) { return; }

        var container = document.getElementById('alliances-container');
        if (!container) { return; }

        var options = { roles: [], mainJobs: [], subJobs: [] };
        try {
            var parsed = JSON.parse(form.dataset.options || '{}');
            options.roles = parsed.roles || [];
            options.mainJobs = parsed.mainJobs || [];
            options.subJobs = parsed.subJobs || [];
        } catch (e) { /* keep defaults */ }

        var MAX_SLOTS = 6;

        // One global pass over the flat slot list: every .ps-slot gets a
        // contiguous Slots[flat] name plus its alliance/party/slot index and
        // the current alliance/party name values, so the default model binder
        // rebuilds the tree server-side. Mirrors tod-manager.reindexLootRows.
        function reindexAll() {
            var flat = 0;
            container.querySelectorAll(':scope > .ps-alliance').forEach(function (allianceEl, aIdx) {
                allianceEl.setAttribute('data-alliance', aIdx);
                var allianceNameEl = allianceEl.querySelector('.ps-alliance-name');
                var allianceName = allianceNameEl ? allianceNameEl.value : '';
                allianceEl.querySelectorAll(':scope > .ps-parties > .ps-party').forEach(function (partyEl, pIdx) {
                    partyEl.setAttribute('data-party', pIdx);
                    var partyNameEl = partyEl.querySelector('.ps-party-name');
                    var partyName = partyNameEl ? partyNameEl.value : '';
                    partyEl.querySelectorAll(':scope > .ps-slots > .ps-slot').forEach(function (slotEl, sIdx) {
                        slotEl.setAttribute('data-slot', sIdx);
                        setHidden(slotEl, 'AllianceIndex', aIdx, flat);
                        setHidden(slotEl, 'PartyIndex', pIdx, flat);
                        setHidden(slotEl, 'SlotIndex', sIdx, flat);
                        setHidden(slotEl, 'AllianceName', allianceName, flat);
                        setHidden(slotEl, 'PartyName', partyName, flat);
                        renameField(slotEl, 'Role', flat);
                        renameField(slotEl, 'MainJob', flat);
                        renameField(slotEl, 'SubJob', flat);
                        renameField(slotEl, 'IsPartyLeader', flat);
                        applyRoleState(slotEl);
                        flat++;
                    });
                });
            });
            updatePartyControls();
        }

        function setHidden(slotEl, field, value, flat) {
            var el = slotEl.querySelector('[data-field="' + field + '"]');
            if (!el) return;
            el.value = value;
            el.setAttribute('name', 'Slots[' + flat + '].' + field);
        }

        function renameField(slotEl, field, flat) {
            var el = slotEl.querySelector('[data-field="' + field + '"]');
            if (el) el.setAttribute('name', 'Slots[' + flat + '].' + field);
        }

        // Reflect a slot's leader state into the hidden field + crown/row
        // styling. Exactly one leader per party is enforced by the click
        // handler clearing siblings before setting the new one.
        function setLeaderVisual(slotEl, on) {
            var hidden = slotEl.querySelector('[data-field="IsPartyLeader"]');
            if (hidden) { hidden.value = on ? 'true' : 'false'; }
            slotEl.classList.toggle('is-leader', on);
            var btn = slotEl.querySelector('.ps-leader-btn');
            if (btn) {
                btn.classList.toggle('active', on);
                btn.setAttribute('aria-pressed', on ? 'true' : 'false');
            }
        }

        // Drive a slot's job UI from its Role pick:
        //   • specific role (Tank/Heal/Support/DPS) -> show Main/Sub selects,
        //     and relabel their blank option to "Any <Role>".
        //   • "Any Role" -> hide + clear the Main/Sub selects, show the single
        //     "— Anything —" indicator (server stores an open slot).
        //   • nothing picked -> hide the Main/Sub selects, show "Select a role
        //     first" (role must be chosen before a job). Values are preserved
        //     here so loading + saving a legacy job-only row isn't destructive.
        function applyRoleState(slotEl) {
            var roleSel = slotEl.querySelector('[data-field="Role"]');
            var mainSel = slotEl.querySelector('[data-field="MainJob"]');
            var subSel = slotEl.querySelector('[data-field="SubJob"]');
            var anyEl = slotEl.querySelector('.ps-slot__any');
            if (!roleSel || !mainSel || !subSel || !anyEl) { return; }

            var role = roleSel.value;
            var specific = role !== '' && role !== 'Any Role';
            if (specific) {
                mainSel.classList.remove('d-none');
                subSel.classList.remove('d-none');
                anyEl.classList.add('d-none');
                if (mainSel.options.length) { mainSel.options[0].textContent = 'Any ' + role; }
                if (subSel.options.length) { subSel.options[0].textContent = 'Any ' + role; }
            } else {
                if (role === 'Any Role') {
                    // Explicit "anything" — drop any stale job picks so the
                    // server persists this as an open (Any) slot.
                    mainSel.value = '';
                    subSel.value = '';
                }
                mainSel.classList.add('d-none');
                subSel.classList.add('d-none');
                anyEl.classList.remove('d-none');
                anyEl.textContent = (role === '') ? 'Select a role first' : '— Anything —';
            }
        }

        function createSlot() {
            var wrapper = document.createElement('div');
            wrapper.className = 'ps-slot';
            wrapper.setAttribute('data-slot', '0');
            wrapper.innerHTML =
                '<input type="hidden" data-field="AllianceIndex" value="0" />'
                + '<input type="hidden" data-field="PartyIndex" value="0" />'
                + '<input type="hidden" data-field="SlotIndex" value="0" />'
                + '<input type="hidden" data-field="AllianceName" value="" />'
                + '<input type="hidden" data-field="PartyName" value="" />'
                + '<input type="hidden" data-field="IsPartyLeader" value="false" />'
                + '<button type="button" class="ps-leader-btn" title="Set as party leader"'
                + ' aria-label="Set as party leader" aria-pressed="false">'
                + '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2.5 7.5l4.6 3.2L12 3.5l4.9 7.2 4.6-3.2-1.9 11.5H4.4L2.5 7.5z"/></svg>'
                + '</button>'
                + '<select data-field="Role" class="form-select ps-slot__sel ps-slot__role">'
                + roleSelectOptions(options.roles) + '</select>'
                + '<select data-field="MainJob" class="form-select ps-slot__sel ps-slot__job">'
                + selectOptions(options.mainJobs, 'Select main job') + '</select>'
                + '<select data-field="SubJob" class="form-select ps-slot__sel ps-slot__job">'
                + selectOptions(options.subJobs, 'Select sub job') + '</select>'
                + '<span class="ps-slot__any d-none" data-role="any" aria-hidden="true">— Anything —</span>'
                + '<button type="button" class="btn sm ghost ps-duplicate-slot ps-slot__dup"'
                + ' title="Duplicate this slot below" aria-label="Duplicate this slot below">'
                + '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true">'
                + '<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/></svg>'
                + '</button>'
                + '<button type="button" class="btn sm ghost ps-remove-slot ps-slot__remove" title="Remove slot" aria-label="Remove slot">&times;</button>';
            return wrapper;
        }

        // A party starts EMPTY (no slots) — the user clicks "+ Add slot" in
        // the body row to add them, up to MAX_SLOTS.
        function createParty(partyName) {
            var party = document.createElement('div');
            party.className = 'ps-party create-form__item';
            party.setAttribute('data-party', '0');
            party.innerHTML =
                '<div class="ps-party-head">'
                + '<input type="text" class="input ps-party-name" value="' + escapeHtml(partyName) + '" placeholder="Party name" maxlength="64" />'
                + '<div class="ps-party-head-actions">'
                + '<button type="button" class="btn sm danger-outline ps-remove-party">Remove</button>'
                + '</div>'
                + '</div>'
                + '<div class="ps-slots"></div>'
                + '<div class="ps-add-slot-row">'
                + '<button type="button" class="btn sm ghost ps-add-slot">+ Add slot</button>'
                + '</div>';
            return party;
        }

        function createAlliance(allianceName) {
            var alliance = document.createElement('section');
            alliance.className = 'ps-alliance create-form__section';
            alliance.setAttribute('data-alliance', '0');
            alliance.innerHTML =
                '<div class="create-form__section-head ps-alliance-head">'
                + '<label class="ps-name-field"><span>Alliance</span>'
                + '<input type="text" class="input ps-alliance-name" value="' + escapeHtml(allianceName) + '" placeholder="Alliance name" maxlength="64" /></label>'
                + '<div class="ps-head-actions">'
                + '<button type="button" class="btn sm ghost ps-add-party">+ Add party</button>'
                + '<button type="button" class="btn sm danger-outline ps-remove-alliance">Remove alliance</button>'
                + '</div>'
                + '</div>'
                + '<div class="ps-parties"></div>';
            alliance.querySelector('.ps-parties').appendChild(createParty('Party 1'));
            return alliance;
        }

        // Hide an alliance's "+ Add party" once it holds the FFXI maximum of
        // 3 parties; show it again if a party is removed. Runs on every
        // reindex (i.e. after every add/remove and on init).
        function updatePartyControls() {
            container.querySelectorAll(':scope > .ps-alliance').forEach(function (allianceEl) {
                var count = allianceEl.querySelectorAll(':scope > .ps-parties > .ps-party').length;
                var addBtn = allianceEl.querySelector('.ps-add-party');
                if (addBtn) { addBtn.classList.toggle('d-none', count >= 3); }
            });
            // Hide a party's "+ Add slot" row once it's full (6 slots), and
            // disable the per-row Duplicate buttons so they can't push past
            // the cap either.
            container.querySelectorAll('.ps-party').forEach(function (partyEl) {
                var slotCount = partyEl.querySelectorAll(':scope > .ps-slots > .ps-slot').length;
                var addRow = partyEl.querySelector(':scope > .ps-add-slot-row');
                if (addRow) { addRow.classList.toggle('d-none', slotCount >= MAX_SLOTS); }
                var full = slotCount >= MAX_SLOTS;
                partyEl.querySelectorAll(':scope > .ps-slots > .ps-slot .ps-duplicate-slot')
                    .forEach(function (b) { b.disabled = full; });
            });
        }

        // --- Delegated handlers ---
        var addAllianceBtn = document.getElementById('ps-add-alliance');
        if (addAllianceBtn) {
            addAllianceBtn.addEventListener('click', function () {
                var count = container.querySelectorAll(':scope > .ps-alliance').length;
                container.appendChild(createAlliance('Alliance ' + (count + 1)));
                reindexAll();
            });
        }

        container.addEventListener('click', function (evt) {
            var leaderBtn = evt.target.closest('.ps-leader-btn');
            if (leaderBtn) {
                var leaderSlot = leaderBtn.closest('.ps-slot');
                var slotsHost = leaderSlot.closest('.ps-slots');
                var wasLeader = leaderBtn.classList.contains('active');
                // Radio-like within the party: clear every slot, then set the
                // clicked one — unless it was already the leader (toggle off,
                // leaving the party with no designated leader).
                slotsHost.querySelectorAll(':scope > .ps-slot').forEach(function (s) {
                    setLeaderVisual(s, false);
                });
                if (!wasLeader) { setLeaderVisual(leaderSlot, true); }
                return;
            }

            var addParty = evt.target.closest('.ps-add-party');
            if (addParty) {
                var alliance = addParty.closest('.ps-alliance');
                var parties = alliance.querySelector('.ps-parties');
                var n = parties.querySelectorAll(':scope > .ps-party').length;
                // FFXI alliance maximum is 3 parties.
                if (n >= 3) { return; }
                parties.appendChild(createParty('Party ' + (n + 1)));
                reindexAll();
                return;
            }

            var removeParty = evt.target.closest('.ps-remove-party');
            if (removeParty) {
                var allianceForParty = removeParty.closest('.ps-alliance');
                var partyEls = allianceForParty.querySelectorAll(':scope > .ps-parties > .ps-party');
                if (partyEls.length > 1) {
                    removeParty.closest('.ps-party').remove();
                    reindexAll();
                }
                return;
            }

            var addSlot = evt.target.closest('.ps-add-slot');
            if (addSlot) {
                var slotsHost = addSlot.closest('.ps-party').querySelector('.ps-slots');
                var existing = slotsHost.querySelectorAll(':scope > .ps-slot').length;
                // FFXI party maximum is 6 slots.
                if (existing >= MAX_SLOTS) { return; }
                var newSlot = createSlot();
                slotsHost.appendChild(newSlot);
                // The first slot added to a party becomes its leader by
                // default (so every non-empty party has a crown); the user
                // can move it. Extra slots are added unflagged.
                if (existing === 0) { setLeaderVisual(newSlot, true); }
                reindexAll();
                return;
            }

            var dupSlot = evt.target.closest('.ps-duplicate-slot');
            if (dupSlot) {
                var srcSlot = dupSlot.closest('.ps-slot');
                var dupHost = srcSlot.closest('.ps-slots');
                var dupCount = dupHost.querySelectorAll(':scope > .ps-slot').length;
                // Respect the FFXI 6-slot party cap.
                if (dupCount >= MAX_SLOTS) { return; }
                var clone = createSlot();
                // Copy only the picks — not the leader crown, since a party
                // has at most one leader and the original keeps it.
                ['Role', 'MainJob', 'SubJob'].forEach(function (f) {
                    var from = srcSlot.querySelector('[data-field="' + f + '"]');
                    var to = clone.querySelector('[data-field="' + f + '"]');
                    if (from && to) { to.value = from.value; }
                });
                srcSlot.insertAdjacentElement('afterend', clone);
                reindexAll();
                return;
            }

            var removeSlot = evt.target.closest('.ps-remove-slot');
            if (removeSlot) {
                // Slots can be removed all the way down to zero (an empty
                // party simply isn't persisted).
                removeSlot.closest('.ps-slot').remove();
                reindexAll();
                return;
            }

            var removeAlliance = evt.target.closest('.ps-remove-alliance');
            if (removeAlliance) {
                var allianceEls = container.querySelectorAll(':scope > .ps-alliance');
                if (allianceEls.length > 1) {
                    removeAlliance.closest('.ps-alliance').remove();
                    reindexAll();
                }
                return;
            }
        });

        // Changing a slot's Role immediately re-flows that row's job UI
        // (show/hide + relabel + "anything" indicator).
        container.addEventListener('change', function (evt) {
            var roleSel = evt.target.closest('[data-field="Role"]');
            if (roleSel) {
                var slotEl = roleSel.closest('.ps-slot');
                if (slotEl) { applyRoleState(slotEl); }
            }
        });

        // Alliance/party name edits ride along on every child slot's hidden
        // field; resync before the form posts so the binder sees them.
        form.addEventListener('submit', reindexAll);

        // Normalize the server-rendered rows (contiguous Slots[] names), live.
        reindexAll();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
