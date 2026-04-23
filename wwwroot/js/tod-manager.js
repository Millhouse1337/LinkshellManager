(function () {
    'use strict';

    function init() {
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
        const linkshellFilter = document.getElementById('linkshell-filter');
        const lootDetailsTableBody = document.getElementById('loot-details-table-body');
        const countdownElements = Array.from(document.querySelectorAll('.countdown-timer'));

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

        function formatCountdown(milliseconds) {
            const totalSeconds = Math.max(0, Math.floor(milliseconds / 1000));
            const days = Math.floor(totalSeconds / 86400);
            const hours = Math.floor((totalSeconds % 86400) / 3600);
            const minutes = Math.floor((totalSeconds % 3600) / 60);
            const seconds = totalSeconds % 60;
            return days + 'd ' + String(hours).padStart(2, '0') + 'h ' + String(minutes).padStart(2, '0') + 'm ' + String(seconds).padStart(2, '0') + 's';
        }

        function updateCountdowns() {
            const now = Date.now();
            countdownElements.forEach((el) => {
                const endUtc = el.dataset.endUtc;
                const endTime = Date.parse(endUtc);
                if (Number.isNaN(endTime)) { el.textContent = '—'; return; }
                const remaining = endTime - now;
                el.textContent = remaining <= 0 ? 'Ready' : formatCountdown(remaining);
            });
        }

        if (monsterSelect) monsterSelect.addEventListener('change', () => { applyMonsterDefaults(); updateRepopTime(); });
        if (todTimeInput) {
            todTimeInput.addEventListener('change', updateRepopTime);
            todTimeInput.addEventListener('input', updateRepopTime);
            todTimeInput.addEventListener('blur', updateRepopTime);
        }
        if (cooldownSelect) cooldownSelect.addEventListener('change', updateRepopTime);
        if (claimSelect) claimSelect.addEventListener('change', toggleLootUi);

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

        if (linkshellFilter) linkshellFilter.addEventListener('change', () => {
            const url = new URL(window.location.href);
            url.searchParams.set('linkshellId', linkshellFilter.value);
            window.location.assign(url.toString());
        });

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
                            '<td>' + escapeHtml(detail.itemName || '') + '</td>'
                            + '<td>' + escapeHtml(detail.itemWinner || '') + '</td>'
                            + '<td class="num" style="text-align:right">' + escapeHtml(detail.winningDkpSpent || '') + '</td>';
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

        ensureLootRow();
        applyMonsterDefaults();
        updateRepopTime();
        toggleLootUi();
        updateCountdowns();
        window.setInterval(updateCountdowns, 1000);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
