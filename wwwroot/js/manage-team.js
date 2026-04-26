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
        fetch('/api/activity/dkp-audit', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                linkshellId: currentLinkshellId,
                targetAppUserId: currentAppUserId,
                mode: mode,
                relatedLedgerEntryId: relatedId,
                amount: amount,
                reason: reason
            })
        }).then(function (res) {
            if (!res.ok) {
                return res.text().then(function (text) {
                    var errMsg = 'Audit failed.';
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

    // ----- Game Addon (att) pairing -----
    var addonCard = document.getElementById('addonTokenCard');
    if (addonCard) {
        var addonLinkshellId = parseInt(addonCard.dataset.linkshellid, 10);
        var addonGenerateBtn = document.getElementById('addonGenerateBtn');
        var addonPairCreateBtn = document.getElementById('addonPairCreateBtn');
        var addonPairCodeWrapper = document.getElementById('addonPairCodeWrapper');
        var addonPairCode = document.getElementById('addonPairCode');
        var addonPairCountdown = document.getElementById('addonPairCountdown');
        var addonPairError = document.getElementById('addonPairError');
        var addonTokensTable = document.getElementById('addonTokensTable');
        var addonTokensEmpty = document.getElementById('addonTokensEmpty');
        var countdownTimer = null;

        function setAddonPairError(msg) {
            addonPairError.textContent = msg || '';
            addonPairError.style.display = msg ? 'block' : 'none';
        }

        function renderTokens(tokens) {
            var tbody = addonTokensTable.querySelector('tbody');
            tbody.innerHTML = '';
            if (!tokens || tokens.length === 0) {
                addonTokensTable.style.display = 'none';
                addonTokensEmpty.style.display = 'block';
                return;
            }
            addonTokensEmpty.style.display = 'none';
            addonTokensTable.style.display = '';
            tokens.forEach(function (t) {
                var tr = document.createElement('tr');
                var lastUsed = t.lastUsedAt ? new Date(t.lastUsedAt).toLocaleString() : '—';
                var created = new Date(t.createdAt).toLocaleString();
                tr.innerHTML =
                    '<td><code>' + escapeHtml(t.prefix) + '…</code></td>' +
                    '<td>' + escapeHtml(t.label || '—') + '</td>' +
                    '<td style="font-size:12px;color:var(--fg-3)">' + escapeHtml(created) + '</td>' +
                    '<td style="font-size:12px;color:var(--fg-3)">' + escapeHtml(lastUsed) + '</td>' +
                    '<td style="text-align:right"><button type="button" class="btn warn sm" data-revoke="' + t.id + '">Revoke</button></td>';
                tbody.appendChild(tr);
            });
            tbody.querySelectorAll('button[data-revoke]').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    var id = parseInt(btn.dataset.revoke, 10);
                    if (!id || !window.confirm('Revoke this addon token? The addon will lose access immediately.')) return;
                    btn.disabled = true;
                    fetch('/api/addon/management/tokens/' + id + '/revoke?linkshellId=' + addonLinkshellId, {
                        method: 'POST',
                        credentials: 'same-origin'
                    }).then(function (res) {
                        if (!res.ok) { btn.disabled = false; return; }
                        loadTokens();
                    }).catch(function () { btn.disabled = false; });
                });
            });
        }

        function escapeHtml(s) {
            return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
                return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
            });
        }

        function loadTokens() {
            fetch('/api/addon/management/tokens?linkshellId=' + addonLinkshellId, {
                credentials: 'same-origin'
            }).then(function (res) {
                if (!res.ok) return null;
                return res.json();
            }).then(function (data) {
                if (!data) return;
                renderTokens(data.tokens || []);
            });
        }

        function startCountdown(totalSeconds) {
            if (countdownTimer) clearInterval(countdownTimer);
            var remaining = totalSeconds;
            function tick() {
                if (remaining <= 0) {
                    addonPairCountdown.textContent = 'expired';
                    clearInterval(countdownTimer);
                    return;
                }
                var m = Math.floor(remaining / 60);
                var s = remaining % 60;
                addonPairCountdown.textContent = m + ':' + (s < 10 ? '0' : '') + s;
                remaining--;
            }
            tick();
            countdownTimer = setInterval(tick, 1000);
        }

        addonGenerateBtn.addEventListener('click', function () {
            setAddonPairError('');
            addonPairCodeWrapper.style.display = 'none';
            addonPairCode.textContent = '';
            document.getElementById('addonPairLabel').value = '';
            openModal('addonPairModal');
        });

        addonPairCreateBtn.addEventListener('click', function () {
            setAddonPairError('');
            addonPairCreateBtn.disabled = true;
            var label = document.getElementById('addonPairLabel').value.trim();
            fetch('/api/addon/management/pairing-code', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ linkshellId: addonLinkshellId, label: label || null })
            }).then(function (res) {
                if (!res.ok) {
                    return res.text().then(function (txt) {
                        var msg = 'Could not generate pairing code.';
                        try { var j = JSON.parse(txt); if (j && j.error) msg = j.error; } catch (e) {}
                        setAddonPairError(msg);
                        return null;
                    });
                }
                return res.json();
            }).then(function (data) {
                if (!data) return;
                addonPairCode.textContent = data.code;
                addonPairCodeWrapper.style.display = 'block';
                startCountdown((data.expiresInMinutes || 10) * 60);
            }).catch(function () {
                setAddonPairError('Network error generating pairing code.');
            }).finally(function () {
                addonPairCreateBtn.disabled = false;
            });
        });

        document.getElementById('addonPairModal').addEventListener('click', function (e) {
            if (e.target.dataset && e.target.dataset.mtClose === '1') {
                if (countdownTimer) clearInterval(countdownTimer);
                loadTokens();
            }
        });

        loadTokens();
    }
})();
