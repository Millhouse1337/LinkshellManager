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
                if (Number.isNaN(endTime)) { el.textContent = '—'; return; }
                const remaining = endTime - now;
                el.textContent = remaining <= 0 ? 'Ready' : formatCountdown(remaining);
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

        const longWindowMonsters = new Set(['Tiamat', 'Jormungand', 'Vrtra']);
        const todForm = document.getElementById('tod-form');
        if (!todForm) { return; }

        let characterNames = [];
        try { characterNames = JSON.parse(todForm.dataset.characterNames || '[]'); } catch (e) { characterNames = []; }

        const qs = (sel) => todForm.querySelector(sel);
        const todTimeInput = qs('[name="Tod.Time"]');
        const monsterSelect = qs('[name="Tod.MonsterName"]');
        const cooldownSelect = qs('[name="Tod.Cooldown"]');
        const repopTimeInput = qs('[name="Tod.RepopTime"]');
        const intervalSelect = qs('[name="Tod.Interval"]');
        const claimSelect = qs('[name="Tod.Claim"]');
        const lootSection = document.getElementById('loot-section');
        const lootRows = document.getElementById('loot-rows');
        const lootControls = document.getElementById('loot-controls');
        const submitOnlyRow = document.getElementById('submit-only-row');
        const noLootInput = document.getElementById('no-loot');
        const addLootRowButton = document.getElementById('add-loot-row');
        const removeLootRowButton = document.getElementById('remove-loot-row');
        const noLootButton = document.getElementById('no-loot-button');
        const lootDetailsTableBody = document.getElementById('loot-details-table-body');

        function escapeHtml(value) {
            return String(value == null ? '' : value)
                .replaceAll('&', '&amp;')
                .replaceAll('<', '&lt;')
                .replaceAll('>', '&gt;')
                .replaceAll('"', '&quot;')
                .replaceAll("'", '&#39;');
        }

        function toDateTimeLocalValue(date) {
            const pad = (v) => String(v).padStart(2, '0');
            return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate())
                + 'T' + pad(date.getHours()) + ':' + pad(date.getMinutes()) + ':' + pad(date.getSeconds());
        }

        function getCooldownHours() {
            return cooldownSelect && cooldownSelect.value === '72 Hour' ? 72 : 22;
        }

        function applyMonsterDefaults() {
            if (!monsterSelect || !monsterSelect.value) return;
            if (longWindowMonsters.has(monsterSelect.value)) {
                if (cooldownSelect) cooldownSelect.value = '72 Hour';
                if (intervalSelect) intervalSelect.value = '1 Hour';
            } else {
                if (cooldownSelect) cooldownSelect.value = '22 Hour';
                if (intervalSelect) intervalSelect.value = '10 Min';
            }
        }

        function updateRepopTime() {
            if (!todTimeInput || !repopTimeInput) return;
            const rawValue = (todTimeInput.value || '').trim();
            if (!rawValue) { repopTimeInput.value = ''; return; }
            const normalised = rawValue.length === 16 ? rawValue + ':00' : rawValue;
            const todTime = new Date(normalised);
            if (Number.isNaN(todTime.getTime())) { repopTimeInput.value = ''; return; }
            const repopTime = new Date(todTime.getTime() + (getCooldownHours() * 60 * 60 * 1000));
            repopTimeInput.value = toDateTimeLocalValue(repopTime);
        }

        function buildWinnerOptions(selectedValue) {
            selectedValue = selectedValue || '';
            const options = ['<option value="">Select winner</option>'];
            characterNames.forEach((name) => {
                const selected = name === selectedValue ? ' selected' : '';
                options.push('<option value="' + escapeHtml(name) + '"' + selected + '>' + escapeHtml(name) + '</option>');
            });
            return options.join('');
        }

        function createLootRow(itemName, itemWinner, winningDkpSpent) {
            itemName = itemName || '';
            itemWinner = itemWinner || '';
            winningDkpSpent = winningDkpSpent || '';
            const wrapper = document.createElement('div');
            wrapper.className = 'loot-detail-row';
            wrapper.style.cssText = 'padding:12px;border:1px solid var(--border);border-radius:var(--r-md);background:var(--surface)';
            wrapper.innerHTML =
                '<div class="field-row" style="margin-bottom:0">'
                + '<div class="field"><label class="field-label">Item name</label>'
                + '<input type="text" class="form-control" data-field="ItemName" value="' + escapeHtml(itemName) + '" /></div>'
                + '<div class="field"><label class="field-label">Item winner</label>'
                + '<select class="form-select" data-field="ItemWinner">' + buildWinnerOptions(itemWinner) + '</select></div>'
                + '<div class="field"><label class="field-label">DKP spent</label>'
                + '<input type="number" class="form-control" data-field="WinningDkpSpent" min="1" value="' + escapeHtml(winningDkpSpent) + '" /></div>'
                + '</div>';
            return wrapper;
        }

        function reindexLootRows() {
            if (!lootRows) return;
            lootRows.querySelectorAll('.loot-detail-row').forEach((row, index) => {
                row.querySelector('[data-field="ItemName"]').setAttribute('name', 'TodLootDetails[' + index + '].ItemName');
                row.querySelector('[data-field="ItemWinner"]').setAttribute('name', 'TodLootDetails[' + index + '].ItemWinner');
                row.querySelector('[data-field="WinningDkpSpent"]').setAttribute('name', 'TodLootDetails[' + index + '].WinningDkpSpent');
            });
        }

        function ensureLootRow() {
            if (!lootRows) return;
            if (!lootRows.querySelector('.loot-detail-row')) {
                lootRows.appendChild(createLootRow());
            }
            reindexLootRows();
        }

        function resetLootRows() {
            if (!lootRows) return;
            lootRows.innerHTML = '';
            ensureLootRow();
        }

        function toggleLootUi() {
            if (!claimSelect) return;
            const claimIsYes = claimSelect.value === 'true';
            if (lootControls) lootControls.classList.toggle('d-none', !claimIsYes);
            if (submitOnlyRow) submitOnlyRow.classList.toggle('d-none', claimIsYes);
            if (!claimIsYes) {
                if (noLootInput) noLootInput.value = 'false';
                if (lootSection) lootSection.classList.add('d-none');
                resetLootRows();
                return;
            }
            if (noLootInput && noLootInput.value === 'true') {
                if (lootSection) lootSection.classList.add('d-none');
                return;
            }
            ensureLootRow();
            if (lootSection) lootSection.classList.remove('d-none');
        }

        if (monsterSelect) monsterSelect.addEventListener('change', () => { applyMonsterDefaults(); updateRepopTime(); });
        if (todTimeInput) {
            todTimeInput.addEventListener('change', updateRepopTime);
            todTimeInput.addEventListener('input', updateRepopTime);
            todTimeInput.addEventListener('blur', updateRepopTime);
        }
        if (cooldownSelect) cooldownSelect.addEventListener('change', updateRepopTime);
        if (claimSelect) claimSelect.addEventListener('change', toggleLootUi);

        if (addLootRowButton) addLootRowButton.addEventListener('click', () => {
            if (noLootInput) noLootInput.value = 'false';
            if (lootSection) lootSection.classList.remove('d-none');
            if (lootRows) lootRows.appendChild(createLootRow());
            reindexLootRows();
        });

        if (removeLootRowButton) removeLootRowButton.addEventListener('click', () => {
            if (!lootRows) return;
            const rows = lootRows.querySelectorAll('.loot-detail-row');
            if (rows.length > 1) rows[rows.length - 1].remove();
            else if (rows.length === 1) {
                rows[0].querySelectorAll('input, select').forEach((input) => { input.value = ''; });
            }
            reindexLootRows();
        });

        if (noLootButton) noLootButton.addEventListener('click', () => {
            if (noLootInput) noLootInput.value = 'true';
            if (lootSection) lootSection.classList.add('d-none');
            resetLootRows();
        });

        ensureLootRow();
        applyMonsterDefaults();
        updateRepopTime();
        toggleLootUi();
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
