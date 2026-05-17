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

    function init() {
        var form = document.getElementById('party-setup-form');
        if (!form) { return; }

        var container = document.getElementById('alliances-container');
        if (!container) { return; }

        var options = { requirementTypes: ['Any', 'Role', 'Job'], roles: [], mainJobs: [], subJobs: [] };
        try {
            var parsed = JSON.parse(form.dataset.options || '{}');
            options.requirementTypes = parsed.requirementTypes || options.requirementTypes;
            options.roles = parsed.roles || [];
            options.mainJobs = parsed.mainJobs || [];
            options.subJobs = parsed.subJobs || [];
        } catch (e) { /* keep defaults */ }

        function buildOptions(values, selected, placeholder) {
            var html = placeholder != null
                ? '<option value="">' + escapeHtml(placeholder) + '</option>'
                : '';
            (values || []).forEach(function (value) {
                var sel = String(value) === String(selected || '') ? ' selected' : '';
                html += '<option value="' + escapeHtml(value) + '"' + sel + '>' + escapeHtml(value) + '</option>';
            });
            return html;
        }

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
                        renameField(slotEl, 'RequirementType', flat);
                        renameField(slotEl, 'Role', flat);
                        renameField(slotEl, 'MainJob', flat);
                        renameField(slotEl, 'SubJob', flat);
                        renameField(slotEl, 'Label', flat);
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

        function applyReqVisibility(slotEl) {
            var req = slotEl.querySelector('.ps-req');
            var value = req ? req.value : 'Any';
            var isRole = value === 'Role';
            var isJob = value === 'Job';
            slotEl.querySelectorAll('.ps-role-field').forEach(function (el) {
                el.classList.toggle('d-none', !isRole);
            });
            slotEl.querySelectorAll('.ps-job-field').forEach(function (el) {
                el.classList.toggle('d-none', !isJob);
            });
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
                + '<select data-field="RequirementType" class="form-select ps-req ps-slot__req">'
                + buildOptions(options.requirementTypes, 'Any', null) + '</select>'
                + '<select data-field="Role" class="form-select ps-role-field ps-slot__grow d-none">'
                + buildOptions(options.roles, '', 'Select role') + '</select>'
                + '<select data-field="MainJob" class="form-select ps-job-field ps-slot__job d-none">'
                + buildOptions(options.mainJobs, '', 'Job') + '</select>'
                + '<select data-field="SubJob" class="form-select ps-job-field ps-slot__job d-none">'
                + buildOptions(options.subJobs, '', '/ sub') + '</select>'
                + '<input type="text" data-field="Label" class="input ps-slot__label" value="" placeholder="Label (optional)" maxlength="64" />'
                + '<button type="button" class="btn sm ghost ps-remove-slot ps-slot__remove" title="Remove slot" aria-label="Remove slot">&times;</button>';
            return wrapper;
        }

        function createParty(partyName) {
            var party = document.createElement('div');
            party.className = 'ps-party create-form__item';
            party.setAttribute('data-party', '0');
            party.innerHTML =
                '<div class="ps-party-head">'
                + '<input type="text" class="input ps-party-name" value="' + escapeHtml(partyName) + '" placeholder="Party name" maxlength="64" />'
                + '<div class="ps-party-head-actions">'
                + '<button type="button" class="btn sm ghost ps-add-slot">+ Slot</button>'
                + '<button type="button" class="btn sm danger-outline ps-remove-party">Remove</button>'
                + '</div>'
                + '</div>'
                + '<div class="ps-slots"></div>';
            var slots = party.querySelector('.ps-slots');
            for (var i = 0; i < 6; i++) {
                slots.appendChild(createSlot());
            }
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
                slotsHost.appendChild(createSlot());
                reindexAll();
                return;
            }

            var removeSlot = evt.target.closest('.ps-remove-slot');
            if (removeSlot) {
                var party = removeSlot.closest('.ps-party');
                var slotEls = party.querySelectorAll(':scope > .ps-slots > .ps-slot');
                if (slotEls.length > 1) {
                    removeSlot.closest('.ps-slot').remove();
                    reindexAll();
                }
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

        container.addEventListener('change', function (evt) {
            var req = evt.target.closest('.ps-req');
            if (req) {
                applyReqVisibility(req.closest('.ps-slot'));
            }
        });

        // Alliance/party name edits ride along on every child slot's hidden
        // field; resync before the form posts so the binder sees them.
        form.addEventListener('submit', reindexAll);

        // Normalize the server-rendered rows and set initial Role/Job
        // visibility, then we're live.
        container.querySelectorAll('.ps-slot').forEach(applyReqVisibility);
        reindexAll();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
