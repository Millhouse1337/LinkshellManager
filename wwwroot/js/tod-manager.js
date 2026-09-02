(function () {
    'use strict';

    // Countdown ticker runs on both the Create and Index pages: the Index
    // table has rows with `.countdown-timer[data-end-utc=...]` and needs the
    // 1-second tick; the Create form's "Time until repop" preview uses the
    // same class. Defined at module scope so the form-only `init()` below
    // can early-return without killing the timer.
    function startCountdownTicker() {
        const countdownElements = Array.from(document.querySelectorAll('.countdown-timer'));
        if (countdownElements.length === 0) return;

        function pad2(v) { return String(v).padStart(2, '0'); }
        function formatCountdown(ms) {
            const total = Math.max(0, Math.floor(ms / 1000));
            const days = Math.floor(total / 86400);
            const h = Math.floor((total % 86400) / 3600);
            const m = Math.floor((total % 3600) / 60);
            const s = total % 60;
            return days + 'd ' + pad2(h) + 'h ' + pad2(m) + 'm ' + pad2(s) + 's';
        }
        function tick() {
            const now = Date.now();
            countdownElements.forEach((el) => {
                const endTime = Date.parse(el.dataset.endUtc);
                // No repop to count down to = the ToD was never entered. Clear is-ready too, so a
                // missing repop can never render as the green "Ready" pill.
                if (Number.isNaN(endTime)) {
                    el.textContent = 'Not entered';
                    el.classList.remove('is-ready');
                    return;
                }
                const remaining = endTime - now;
                const isReady = remaining <= 0;
                el.textContent = isReady ? 'Ready' : formatCountdown(remaining);
                // Lets the ToD Tracker pill flip from neutral/blue (counting)
                // to green only once the mob is poppable.
                el.classList.toggle('is-ready', isReady);
            });
        }
        tick();
        window.setInterval(tick, 1000);
    }

    // Helpers used by the Index page handlers below.
    function escapeHtmlForIndex(value) {
        return String(value == null ? '' : value)
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');
    }

    // Wire up the History toggle and "Loot" buttons on the ToD Index page.
    // These need to run regardless of whether `#tod-form` exists, since the
    // Index page has no form but does have these buttons. The form-only
    // `init()` early-returns when no form is present, which is why these
    // handlers used to be dead on the Index page.
    function wireIndexPageHandlers() {
        document.querySelectorAll('.tod-history-toggle').forEach((button) => {
            button.addEventListener('click', () => {
                const target = button.getAttribute('data-target');
                if (!target) return;
                const rows = document.querySelectorAll('.tod-history-row[data-group="' + target + '"]');
                let nowHidden = true;
                rows.forEach((row) => {
                    row.classList.toggle('d-none');
                    if (!row.classList.contains('d-none')) nowHidden = false;
                });
                button.textContent = nowHidden ? ('History (' + rows.length + ')') : 'Hide history';
            });
        });

        // Inline Party Setup panel: expand/collapse in place (no page nav).
        // data-target is the panel row's element id (tod-setup-<id>).
        function toggleSetupRow(id, forceOpen) {
            const row = document.getElementById(id);
            if (!row) return false;
            if (forceOpen) { row.classList.remove('d-none'); }
            else { row.classList.toggle('d-none'); }
            return !row.classList.contains('d-none');
        }
        document.querySelectorAll('.tod-setup-toggle').forEach((button) => {
            button.addEventListener('click', () => {
                const target = button.getAttribute('data-target');
                if (target) toggleSetupRow(target, false);
            });
        });
        // After a sign-up / withdraw round-trip the controller redirects back
        // with #tod-setup-<id> so the panel the member acted on re-opens.
        if (window.location.hash && window.location.hash.indexOf('#tod-setup-') === 0) {
            const id = window.location.hash.slice(1);
            if (toggleSetupRow(id, true)) {
                const el = document.getElementById(id);
                if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center' });
            }
        }

        const lootDetailsTableBody = document.getElementById('loot-details-table-body');
        document.querySelectorAll('.view-loot-btn').forEach((button) => {
            button.addEventListener('click', async () => {
                const todId = button.getAttribute('data-id');
                if (!todId) return;
                const response = await fetch('/Tod/GetLootDetails/' + encodeURIComponent(todId));
                if (!response.ok) return;
                const lootDetails = await response.json();
                if (!lootDetailsTableBody) return;
                lootDetailsTableBody.innerHTML = '';
                if (!lootDetails.length) {
                    lootDetailsTableBody.innerHTML = '<tr><td colspan="3" class="empty">No loot details found.</td></tr>';
                } else {
                    lootDetails.forEach((detail) => {
                        const row = document.createElement('tr');
                        row.innerHTML =
                            '<td>' + escapeHtmlForIndex(detail.itemName || '') + '</td>'
                            + '<td>' + escapeHtmlForIndex(detail.itemWinner || '') + '</td>'
                            + '<td class="num" style="text-align:right">' + escapeHtmlForIndex(detail.winningDkpSpent || '') + '</td>';
                        lootDetailsTableBody.appendChild(row);
                    });
                }
                const modalElement = document.getElementById('viewLootModal');
                if (modalElement && window.bootstrap) {
                    const modal = new window.bootstrap.Modal(modalElement);
                    modal.show();
                }
            });
        });
    }

    function init() {
        // Countdown ticker + Index-page button handlers run first, since the
        // Index page has no `#tod-form` and the form-only block below would
        // skip them otherwise.
        startCountdownTicker();
        wireIndexPageHandlers();

        if (window.jQuery && window.jQuery.validator && window.jQuery.validator.methods) {
            window.jQuery.validator.methods.step = function () { return true; };
        }

        const todForm = document.getElementById('tod-form');
        if (!todForm) { return; }

        const qs = (sel) => todForm.querySelector(sel);
        const todTimeInput = qs('[name="Tod.Time"]');
        const monsterSelect = qs('[name="Tod.MonsterName"]');
        const cooldownValueInput = qs('[name="CooldownValue"]');
        const cooldownUnitSelect = qs('[name="CooldownUnit"]');
        const repopTimeInput = qs('[name="Tod.RepopTime"]');
        const intervalValueInput = qs('[name="IntervalValue"]');
        const intervalUnitSelect = qs('[name="IntervalUnit"]');
        const additionalSecondsInput = qs('[name="AdditionalSeconds"]');
        const repopSummary = document.getElementById('repop-summary');
        // The two fields only SOME monsters can answer: Day (a pop cycle, which only the three
        // NQ/HQ families have) and Popped on window (a monster with a spawn grid).
        const dayNumberWrap = document.getElementById('day-number-wrap');
        const popWindowWrap = document.getElementById('pop-window-wrap');

        // Per-monster cooldown / cadence for THIS linkshell, in canonical minutes, stamped on the
        // form by the server. Replaced two hardcoded monster sets that were a copy of the old global
        // defaults and knew nothing about what the linkshell had actually configured.
        let monsterTimings = {};
        try {
            monsterTimings = JSON.parse(todForm.dataset.monsterTimings || '{}') || {};
        } catch (err) {
            monsterTimings = {};
        }
        const timingFor = (name) => {
            if (!name) { return null; }
            const wanted = String(name).trim().toLowerCase();
            const key = Object.keys(monsterTimings).find(k => k.trim().toLowerCase() === wanted);
            return key ? monsterTimings[key] : null;
        };
        // Mirrors TodDurationFormat.Split: whole hours read as hours, everything else as minutes.
        const applyDuration = (valueInput, unitSelect, minutes) => {
            if (!valueInput || !unitSelect) { return; }
            if (minutes === null || minutes === undefined || !(minutes > 0)) {
                valueInput.value = '';
                unitSelect.value = 'mins';
                return;
            }
            const whole = minutes % 60 === 0;
            valueInput.value = String(whole ? minutes / 60 : minutes);
            unitSelect.value = whole ? 'hours' : 'mins';
        };

        function toDateTimeLocalValue(date) {
            const pad = (v) => String(v).padStart(2, '0');
            return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate())
                + 'T' + pad(date.getHours()) + ':' + pad(date.getMinutes()) + ':' + pad(date.getSeconds());
        }

        function getCooldownHours() {
            const amount = parseFloat(cooldownValueInput && cooldownValueInput.value);
            if (!isFinite(amount) || amount <= 0) return 0;
            return (cooldownUnitSelect && cooldownUnitSelect.value === 'hours') ? amount : amount / 60;
        }

        function getAdditionalSeconds() {
            const amount = parseInt(additionalSecondsInput && additionalSecondsInput.value, 10);
            return isFinite(amount) && amount > 0 ? Math.floor(amount) : 0;
        }

        // Pre-fill from what this linkshell configured for the picked monster. Unknown monsters
        // (the free-text "Other" option) keep whatever is already in the fields.
        function applyMonsterDefaults() {
            if (!monsterSelect || !monsterSelect.value) return;
            const timing = timingFor(monsterSelect.value);
            if (!timing) return;
            applyDuration(cooldownValueInput, cooldownUnitSelect, timing.cooldownMinutes);
            applyDuration(intervalValueInput, intervalUnitSelect, timing.cadenceMinutes);
        }

        // Show Day / Popped on window only for a monster that can answer them, the same rule the
        // Activity's form applies. An unknown monster ("Other", or one with no configured setup)
        // answers neither.
        function applyMonsterFieldVisibility() {
            const timing = monsterSelect ? timingFor(monsterSelect.value) : null;
            // A hidden input still posts, so the value goes with the field — otherwise switching
            // from Behemoth to Tiamat would quietly file a day number against a monster that has
            // no pop cycle to count.
            const setVisible = (wrap, visible) => {
                if (!wrap) { return; }
                wrap.classList.toggle('d-none', !visible);
                if (!visible) {
                    const input = wrap.querySelector('input');
                    if (input) input.value = '';
                }
            };
            setVisible(dayNumberWrap, !!(timing && timing.hasHqVariant));
            setVisible(popWindowWrap, !!(timing && timing.hasSpawnGrid));
        }

        function updateRepopTime() {
            if (!todTimeInput || !repopTimeInput) return;
            const rawValue = (todTimeInput.value || '').trim();
            const clear = (message) => {
                repopTimeInput.value = '';
                if (repopSummary) repopSummary.textContent = message;
            };
            if (!rawValue) { clear('Pick a date and time to calculate the next repop window.'); return; }
            const normalised = rawValue.length === 16 ? rawValue + ':00' : rawValue;
            const todTime = new Date(normalised);
            if (Number.isNaN(todTime.getTime())) { clear('Pick a date and time to calculate the next repop window.'); return; }
            const cooldownHours = getCooldownHours();
            if (cooldownHours <= 0) { clear('Enter a positive cooldown to calculate the next repop window.'); return; }
            // Cooldown, then the officer's fine "Additional seconds" offset — the same sum the
            // server stores (TodController.ResolveRepopTime) and the Activity previews.
            const repopTime = new Date(
                todTime.getTime() + (cooldownHours * 60 * 60 * 1000) + (getAdditionalSeconds() * 1000));
            repopTimeInput.value = toDateTimeLocalValue(repopTime);
            if (repopSummary) {
                repopSummary.textContent = repopTime.toLocaleString(undefined, {
                    year: 'numeric', month: 'numeric', day: 'numeric',
                    hour: 'numeric', minute: '2-digit', second: '2-digit'
                });
            }
        }

        if (monsterSelect) {
            monsterSelect.addEventListener('change', () => {
                applyMonsterDefaults();
                applyMonsterFieldVisibility();
                updateRepopTime();
            });
        }
        if (todTimeInput) {
            todTimeInput.addEventListener('change', updateRepopTime);
            todTimeInput.addEventListener('input', updateRepopTime);
            todTimeInput.addEventListener('blur', updateRepopTime);
        }
        if (cooldownValueInput) {
            cooldownValueInput.addEventListener('change', updateRepopTime);
            cooldownValueInput.addEventListener('input', updateRepopTime);
        }
        if (cooldownUnitSelect) cooldownUnitSelect.addEventListener('change', updateRepopTime);
        if (additionalSecondsInput) {
            additionalSecondsInput.addEventListener('change', updateRepopTime);
            additionalSecondsInput.addEventListener('input', updateRepopTime);
        }

        // Deliberately NOT applyMonsterDefaults() on load: the server already pre-filled the
        // durations for the drafted monster, and on the Edit form the fields hold the values that
        // were SAVED — re-applying the monster's configured defaults here would quietly overwrite
        // a cooldown an officer had adjusted for that particular pop.
        updateRepopTime();
        // Loot is recorded in the dedicated Loot section now, so the loot-row
        // / claim-toggle wiring that used to live here was removed.
        // Countdown ticker is started by startCountdownTicker() above,
        // outside this form-only init block, so the Index page also gets
        // live countdowns when there's no `#tod-form` on the page.
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
