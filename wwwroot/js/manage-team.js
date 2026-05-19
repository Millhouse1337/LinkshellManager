(function () {
    'use strict';

    function openModal(id) {
        var el = document.getElementById(id);
        if (el) { el.classList.add('mt-modal--open'); document.body.style.overflow = 'hidden'; }
    }
    function closeModal(id) {
        var el = document.getElementById(id);
        if (el) { el.classList.remove('mt-modal--open'); document.body.style.overflow = ''; }
    }

    // Treat 401 (signed out) as a hard redirect: the cookie has expired so any
    // further interaction will fail. 403 surfaces as an authorization message
    // because re-login won't help. Returns true if the response was handled.
    function handleAuthFailure(res) {
        if (res.status === 401) {
            var returnUrl = encodeURIComponent(window.location.pathname + window.location.search);
            window.location.href = '/Identity/Account/Login?returnUrl=' + returnUrl;
            return true;
        }
        return false;
    }

    document.querySelectorAll('[data-mt-close]').forEach(function (el) {
        el.addEventListener('click', function () {
            var modal = el.closest('.mt-modal');
            if (modal) closeModal(modal.id);
        });
    });

    document.querySelectorAll('.js-modify-rank').forEach(function (btn) {
        btn.addEventListener('click', function () {
            document.getElementById('modifyId').value = btn.dataset.id || '';
            document.getElementById('modifyRank').value = btn.dataset.rank || 'Member';
            document.getElementById('modifyStatus').value = btn.dataset.status || 'Active';
            document.getElementById('modifyName').textContent = btn.dataset.name || '';
            openModal('modifyRankModal');
        });
    });

    var auditModalEl = document.getElementById('dkpAuditModal');
    if (!auditModalEl) return;

    var currentLinkshellId = null;
    var currentAppUserId = null;
    var loadedEntries = [];
    var loadedAddEntries = [];

    function setMode(mode) {
        var adjust = document.getElementById('auditAdjustFields');
        var entryLabel = document.getElementById('auditEntryLabel');
        var amountField = document.getElementById('auditAmountField');
        var label = document.getElementById('auditAmountLabel');
        if (mode === 'Misc') {
            adjust.style.display = 'none';
            amountField.style.display = '';
            label.textContent = 'Amount (DKP, positive or negative)';
        } else if (mode === 'Add') {
            adjust.style.display = '';
            amountField.style.display = 'none';
            entryLabel.textContent = 'Entry to add member to';
            loadAddCandidates(currentLinkshellId, currentAppUserId);
        } else {
            adjust.style.display = '';
            amountField.style.display = '';
            entryLabel.textContent = 'Entry to correct';
            label.textContent = 'Corrected amount (DKP)';
            loadEntries(currentLinkshellId, currentAppUserId);
        }
    }

    document.querySelectorAll('input[name="auditMode"]').forEach(function (r) {
        r.addEventListener('change', function () { setMode(r.value); });
    });

    document.getElementById('auditEntrySelect').addEventListener('change', function () {
        var sel = document.getElementById('auditEntrySelect');
        var id = parseInt(sel.value, 10);
        var hint = document.getElementById('auditEntryHint');
        var amount = document.getElementById('auditAmount');
        var modeEl = document.querySelector('input[name="auditMode"]:checked');
        var mode = modeEl ? modeEl.value : 'Adjust';
        var entry = loadedEntries.find(function (e) { return e.id === id; });
        if (mode === 'Add') {
            entry = loadedAddEntries.find(function (e) { return e.windowEventId === id; });
        }
        if (entry) {
            hint.textContent = mode === 'Add'
                ? 'Will add this member for ' + entry.amount + ' DKP' + (entry.primaryZone ? ' · ' + entry.primaryZone : '')
                : 'Original amount: ' + entry.amount + ' DKP';
            if (mode !== 'Add') amount.value = entry.amount;
        } else {
            hint.textContent = '';
        }
    });

    function showError(msg) {
        var err = document.getElementById('auditError');
        err.textContent = msg;
        err.style.display = msg ? 'block' : 'none';
    }

    function entryTypeLabel(entryType) {
        switch (entryType) {
            case 'EventEarned': return 'Event Earned';
            case 'SnapshotEarned': return 'Snapshot Earned';
            case 'LootSpent': return 'Loot Spent';
            case 'LootRefund': return 'Loot Refund';
            case 'LootEditRefund': return 'Loot Edit Refund';
            case 'LootEditSpent': return 'Loot Edit Spent';
            case 'LootDeleteRefund': return 'Loot Delete Refund';
            case 'AuctionSpent': return 'Auction Spent';
            case 'AuditAdjustment': return 'Audit Adjustment';
            case 'AuditMisc': return 'Audit Misc';
            default: return entryType || 'Entry';
        }
    }

    function loadEntries(linkshellId, appUserId) {
        var sel = document.getElementById('auditEntrySelect');
        if (!linkshellId || !appUserId) return;
        sel.innerHTML = '<option value="">Loading entries…</option>';
        loadedEntries = [];
        fetch('/api/activity/dkp-history?linkshellId=' + encodeURIComponent(linkshellId) + '&appUserId=' + encodeURIComponent(appUserId), {
            credentials: 'same-origin'
        }).then(function (res) {
            if (handleAuthFailure(res)) { return null; }
            if (res.status === 403) { sel.innerHTML = '<option value="">Not authorized to view entries</option>'; return null; }
            if (!res.ok) { sel.innerHTML = '<option value="">Could not load entries</option>'; return null; }
            return res.json();
        }).then(function (data) {
            if (!data) return;
            loadedEntries = (data.entries || []).slice().reverse();
            if (loadedEntries.length === 0) {
                sel.innerHTML = '<option value="">No prior entries</option>';
                return;
            }
            var opts = ['<option value="">Select a previous entry</option>'];
            loadedEntries.forEach(function (entry) {
                var when = new Date(entry.occurredAt).toLocaleDateString();
                var label = when + ' · ' + entryTypeLabel(entry.entryType) + ' · ' + entry.amount + ' DKP' +
                    (entry.eventName ? ' · ' + entry.eventName : '') +
                    (entry.itemName ? ' (' + entry.itemName + ')' : '');
                opts.push('<option value="' + entry.id + '">' + label.replace(/</g, '&lt;') + '</option>');
            });
            sel.innerHTML = opts.join('');
        }).catch(function () {
            sel.innerHTML = '<option value="">Could not load entries</option>';
        });
    }

    function loadAddCandidates(linkshellId, appUserId) {
        var sel = document.getElementById('auditEntrySelect');
        if (!linkshellId || !appUserId) return;
        sel.innerHTML = '<option value="">Loading entries…</option>';
        loadedAddEntries = [];
        fetch('/api/activity/dkp-audit/add-candidates?linkshellId=' + encodeURIComponent(linkshellId) + '&targetAppUserId=' + encodeURIComponent(appUserId), {
            credentials: 'same-origin'
        }).then(function (res) {
            if (handleAuthFailure(res)) { return null; }
            if (res.status === 403) { sel.innerHTML = '<option value="">Not authorized to view entries</option>'; return null; }
            if (!res.ok) { sel.innerHTML = '<option value="">Could not load entries</option>'; return null; }
            return res.json();
        }).then(function (data) {
            if (!data) return;
            loadedAddEntries = data.entries || [];
            if (loadedAddEntries.length === 0) {
                sel.innerHTML = '<option value="">No posted snapshot entries available</option>';
                return;
            }
            var opts = ['<option value="">Select a previous entry</option>'];
            loadedAddEntries.forEach(function (entry) {
                var when = new Date(entry.occurredAt).toLocaleDateString();
                var label = when + ' · ' + (entry.eventName || 'Window Event') + ' · ' + entry.amount + ' DKP' +
                    (entry.entryType ? ' · ' + entry.entryType : '') +
                    (entry.primaryZone ? ' · ' + entry.primaryZone : '');
                opts.push('<option value="' + entry.windowEventId + '">' + label.replace(/</g, '&lt;') + '</option>');
            });
            sel.innerHTML = opts.join('');
        }).catch(function () {
            sel.innerHTML = '<option value="">Could not load entries</option>';
        });
    }

    function updateSelectedMember(appUserId) {
        var memberSelect = document.getElementById('auditMemberSelect');
        if (appUserId) memberSelect.value = appUserId;
        var selected = memberSelect.options[memberSelect.selectedIndex];
        currentAppUserId = memberSelect.value || '';
        document.getElementById('auditMemberBalance').textContent = 'Current balance: ' + ((selected && selected.dataset.dkp) || '0') + ' DKP';
        document.getElementById('auditEntryHint').textContent = '';
        document.getElementById('auditAmount').value = '';
        var modeEl = document.querySelector('input[name="auditMode"]:checked');
        setMode(modeEl ? modeEl.value : 'Adjust');
    }

    document.getElementById('auditMemberSelect').addEventListener('change', function () {
        updateSelectedMember();
    });

    document.querySelectorAll('.js-dkp-audit').forEach(function (btn) {
        btn.addEventListener('click', function () {
            currentLinkshellId = parseInt(btn.dataset.linkshellid, 10);
            document.getElementById('auditAmount').value = '';
            document.getElementById('auditReason').value = '';
            document.getElementById('auditEntryHint').textContent = '';
            showError('');
            var adjustRadio = document.querySelector('input[name="auditMode"][value="Adjust"]');
            if (adjustRadio) adjustRadio.checked = true;
            updateSelectedMember(btn.dataset.appuserid || '');
            openModal('dkpAuditModal');
        });
    });

    document.getElementById('auditSaveBtn').addEventListener('click', function () {
        showError('');
        var modeEl = document.querySelector('input[name="auditMode"]:checked');
        var mode = modeEl ? modeEl.value : 'Adjust';
        var amountStr = document.getElementById('auditAmount').value;
        var amount = mode === 'Add' ? 0 : parseFloat(amountStr);
        var reason = document.getElementById('auditReason').value.trim();
        var relatedId = null;
        var sourceWindowEventId = null;

        if (!currentLinkshellId || !currentAppUserId) { showError('Missing member context.'); return; }
        if (mode !== 'Add' && isNaN(amount)) { showError('Enter a numeric amount.'); return; }
        if (!reason) { showError('A reason is required.'); return; }
        if (mode === 'Adjust') {
            var sel = document.getElementById('auditEntrySelect');
            var pid = parseInt(sel.value, 10);
            if (!pid) { showError('Select the previous entry you want to correct.'); return; }
            relatedId = pid;
        } else if (mode === 'Add') {
            var addSel = document.getElementById('auditEntrySelect');
            var wid = parseInt(addSel.value, 10);
            if (!wid) { showError('Select the previous entry to add this member to.'); return; }
            sourceWindowEventId = wid;
        }

        var btn = document.getElementById('auditSaveBtn');
        btn.disabled = true;
        var headers = { 'Content-Type': 'application/json' };
        if (window.LSM_CSRF_HEADER && window.LSM_CSRF_TOKEN) {
            headers[window.LSM_CSRF_HEADER] = window.LSM_CSRF_TOKEN;
        }
        fetch('/api/activity/dkp-audit', {
            method: 'POST',
            credentials: 'same-origin',
            headers: headers,
            body: JSON.stringify({
                linkshellId: currentLinkshellId,
                targetAppUserId: currentAppUserId,
                mode: mode,
                relatedLedgerEntryId: relatedId,
                sourceWindowEventId: sourceWindowEventId,
                amount: amount,
                reason: reason
            })
        }).then(function (res) {
            if (handleAuthFailure(res)) { return null; }
            if (!res.ok) {
                return res.text().then(function (text) {
                    var errMsg = res.status === 403 ? 'Not authorized to perform this audit.' : 'Audit failed.';
                    try { var j = JSON.parse(text); if (j && j.error) errMsg = j.error; } catch (e) {}
                    showError(errMsg);
                    return null;
                });
            }
            closeModal('dkpAuditModal');
            window.location.reload();
            return null;
        }).catch(function () {
            showError('Network error saving audit.');
        }).finally(function () {
            btn.disabled = false;
        });
    });

    // Game Addon (att) pairing UX moved to /Linkshell/Customize; its inline
    // <script> on that page owns the modal + token list now.
})();
