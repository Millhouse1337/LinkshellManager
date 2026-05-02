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

    function setMode(mode) {
        var adjust = document.getElementById('auditAdjustFields');
        var label = document.getElementById('auditAmountLabel');
        if (mode === 'Misc') {
            adjust.style.display = 'none';
            label.textContent = 'Amount (DKP, positive or negative)';
        } else {
            adjust.style.display = '';
            label.textContent = 'Corrected amount (DKP)';
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
        var entry = loadedEntries.find(function (e) { return e.id === id; });
        if (entry) {
            hint.textContent = 'Original amount: ' + entry.amount + ' DKP';
            amount.value = entry.amount;
        } else {
            hint.textContent = '';
        }
    });

    function showError(msg) {
        var err = document.getElementById('auditError');
        err.textContent = msg;
        err.style.display = msg ? 'block' : 'none';
    }

    function loadEntries(linkshellId, appUserId) {
        var sel = document.getElementById('auditEntrySelect');
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
                var label = when + ' · ' + (entry.entryType || 'Entry') + ' · ' + entry.amount + ' DKP' +
                    (entry.eventName ? ' · ' + entry.eventName : '') +
                    (entry.itemName ? ' (' + entry.itemName + ')' : '');
                opts.push('<option value="' + entry.id + '">' + label.replace(/</g, '&lt;') + '</option>');
            });
            sel.innerHTML = opts.join('');
        }).catch(function () {
            sel.innerHTML = '<option value="">Could not load entries</option>';
        });
    }

    document.querySelectorAll('.js-dkp-audit').forEach(function (btn) {
        btn.addEventListener('click', function () {
            currentLinkshellId = parseInt(btn.dataset.linkshellid, 10);
            currentAppUserId = btn.dataset.appuserid || '';
            document.getElementById('auditMemberName').textContent = btn.dataset.name || '';
            document.getElementById('auditMemberBalance').textContent = 'Current balance: ' + (btn.dataset.dkp || '0') + ' DKP';
            document.getElementById('auditAmount').value = '';
            document.getElementById('auditReason').value = '';
            document.getElementById('auditEntryHint').textContent = '';
            showError('');
            var adjustRadio = document.querySelector('input[name="auditMode"][value="Adjust"]');
            if (adjustRadio) adjustRadio.checked = true;
            setMode('Adjust');
            if (currentLinkshellId && currentAppUserId) {
                loadEntries(currentLinkshellId, currentAppUserId);
            }
            openModal('dkpAuditModal');
        });
    });

    document.getElementById('auditSaveBtn').addEventListener('click', function () {
        showError('');
        var modeEl = document.querySelector('input[name="auditMode"]:checked');
        var mode = modeEl ? modeEl.value : 'Adjust';
        var amountStr = document.getElementById('auditAmount').value;
        var amount = parseFloat(amountStr);
        var reason = document.getElementById('auditReason').value.trim();
        var relatedId = null;

        if (!currentLinkshellId || !currentAppUserId) { showError('Missing member context.'); return; }
        if (isNaN(amount)) { showError('Enter a numeric amount.'); return; }
        if (!reason) { showError('A reason is required.'); return; }
        if (mode === 'Adjust') {
            var sel = document.getElementById('auditEntrySelect');
            var pid = parseInt(sel.value, 10);
            if (!pid) { showError('Select the previous entry you want to correct.'); return; }
            relatedId = pid;
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
