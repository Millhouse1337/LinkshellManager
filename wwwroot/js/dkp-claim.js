// "Claim your DKP" banner for the web app. When a DKP import created an unclaimed
// placeholder whose character name matches the signed-in user, this lets them
// inherit that imported DKP. Mirrors the Discord Activity claim prompt; uses the
// same /api/activity/claim endpoints (cookie-authed on the web).
(function () {
    'use strict';

    var mount = document.getElementById('dkp-claim-mount');
    if (!mount) { return; }

    function fmt(n) {
        var v = Number(n) || 0;
        return (Math.round(v * 100) / 100).toString();
    }

    async function getAntiforgery() {
        try {
            var res = await fetch('/api/activity/antiforgery', { cache: 'no-store', credentials: 'include' });
            if (!res.ok) { return null; }
            return await res.json();
        } catch (e) {
            return null;
        }
    }

    async function claim(candidate, rowEl, btn) {
        btn.disabled = true;
        btn.textContent = 'Claiming…';
        var headers = { 'Content-Type': 'application/json' };
        var token = await getAntiforgery();
        if (token && token.headerName && token.requestToken) {
            headers[token.headerName] = token.requestToken;
        }
        try {
            var res = await fetch('/api/activity/claim', {
                method: 'POST',
                headers: headers,
                cache: 'no-store',
                credentials: 'include',
                body: JSON.stringify({
                    placeholderAppUserId: candidate.placeholderAppUserId,
                    linkshellId: candidate.linkshellId
                })
            });
            if (!res.ok) {
                btn.disabled = false;
                btn.textContent = 'Claim';
                return;
            }
            // Reload so DKP / roster reflect the inherited balance everywhere.
            window.location.reload();
        } catch (e) {
            btn.disabled = false;
            btn.textContent = 'Claim';
        }
    }

    function render(candidates) {
        if (!candidates || candidates.length === 0) { return; }

        var banner = document.createElement('div');
        banner.className = 'card';
        banner.style.cssText = 'margin:0 0 16px;border-color:color-mix(in srgb, var(--green-main) 40%, var(--border-soft));background:color-mix(in srgb, var(--green-main) 8%, var(--bg-card))';

        var head = document.createElement('div');
        head.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:10px;padding:12px 16px 4px';
        var title = document.createElement('strong');
        title.textContent = 'We found imported DKP that looks like you.';
        var dismiss = document.createElement('button');
        dismiss.type = 'button';
        dismiss.className = 'btn ghost sm';
        dismiss.textContent = 'Dismiss';
        dismiss.addEventListener('click', function () { banner.remove(); });
        head.appendChild(title);
        head.appendChild(dismiss);
        banner.appendChild(head);

        var list = document.createElement('div');
        list.style.cssText = 'display:flex;flex-direction:column;gap:8px;padding:8px 16px 16px';
        candidates.forEach(function (c) {
            var row = document.createElement('div');
            row.style.cssText = 'display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;font-size:13px';
            var text = document.createElement('span');
            text.innerHTML = '<strong></strong> in <strong></strong>';
            text.children[0].textContent = c.characterName;
            text.children[1].textContent = c.linkshellName;
            text.appendChild(document.createTextNode(' · ' + fmt(c.currentDkp) + ' DKP'));
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'btn primary sm';
            btn.textContent = 'Claim';
            btn.addEventListener('click', function () { claim(c, row, btn); });
            row.appendChild(text);
            row.appendChild(btn);
            list.appendChild(row);
        });
        banner.appendChild(list);
        mount.appendChild(banner);
    }

    (async function () {
        try {
            var res = await fetch('/api/activity/claim/candidates', { cache: 'no-store', credentials: 'include' });
            if (!res.ok) { return; }
            var data = await res.json();
            render(data && data.candidates);
        } catch (e) {
            // Non-fatal — no banner.
        }
    })();
})();
