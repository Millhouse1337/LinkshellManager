// Web profile gear/skill/relic ratings + rate-teammates + "what your linkshell
// thinks" — parity with the Discord Activity's job-ratings panel. Reuses the
// SAME Activity API (/api/activity/job-ratings*) via cookie-auth fetch, exactly
// like the DKP audit modal does. No server-rendered rating controls: this module
// renders the star/relic widgets and saves each change immediately.
(function () {
    'use strict';

    var cfgEl = document.getElementById('profile-ratings-config');
    if (!cfgEl) { return; }
    var CFG;
    try { CFG = JSON.parse(cfgEl.textContent || '{}'); } catch (e) { return; }
    if (!CFG.linkshellId || !CFG.myAppUserId) { return; }

    var JOBS = CFG.jobs || [];
    var RELICS = CFG.relicOptions || [];
    var MEMBERS = (CFG.members || []).filter(function (m) { return m.appUserId !== CFG.myAppUserId; });
    var STAR_ON = '#8d92ff', STAR_OFF = '#41434c';

    function headers(json) {
        var h = {};
        if (json) { h['Content-Type'] = 'application/json'; }
        if (window.LSM_CSRF_HEADER && window.LSM_CSRF_TOKEN) { h[window.LSM_CSRF_HEADER] = window.LSM_CSRF_TOKEN; }
        return h;
    }
    function apiGet(path) {
        return fetch(path, { credentials: 'same-origin', cache: 'no-store' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });
    }
    function apiPost(path, body) {
        return fetch(path, { method: 'POST', credentials: 'same-origin', headers: headers(true), body: JSON.stringify(body) })
            .then(function (r) { return r.ok; })
            .catch(function () { return false; });
    }

    // --- Star control (0..5). onChange(value) when editable; null = read-only. ---
    function makeStars(value, onChange, showValue) {
        var wrap = document.createElement('span');
        wrap.style.display = 'inline-flex';
        wrap.style.alignItems = 'center';
        wrap.style.gap = '2px';
        var stars = [];
        function paint(v) {
            for (var i = 0; i < 5; i++) { stars[i].style.color = i < v ? STAR_ON : STAR_OFF; }
            if (showValue) { valEl.textContent = v > 0 ? Number(v).toFixed(1) : '—'; }
        }
        for (var i = 0; i < 5; i++) {
            (function (idx) {
                var s = document.createElement('span');
                s.textContent = '★';
                s.style.fontSize = '15px';
                s.style.lineHeight = '1';
                if (onChange) {
                    s.style.cursor = 'pointer';
                    s.addEventListener('click', function () {
                        // Click the lit star again to clear back to 0.
                        var current = wrap._value || 0;
                        var next = (idx + 1 === current) ? idx : idx + 1;
                        wrap._value = next; paint(next); onChange(next);
                    });
                }
                stars.push(s); wrap.appendChild(s);
            })(i);
        }
        var valEl = null;
        if (showValue) {
            valEl = document.createElement('span');
            valEl.style.fontSize = '12px';
            valEl.style.opacity = '.7';
            valEl.style.marginLeft = '4px';
            wrap.appendChild(valEl);
        }
        wrap._value = value || 0;
        paint(wrap._value);
        return wrap;
    }

    function labeledRow(label, control) {
        var row = document.createElement('div');
        row.style.display = 'flex';
        row.style.alignItems = 'center';
        row.style.gap = '8px';
        row.style.marginTop = '6px';
        var l = document.createElement('span');
        l.textContent = label;
        l.style.fontSize = '10px';
        l.style.textTransform = 'uppercase';
        l.style.letterSpacing = '.04em';
        l.style.color = 'var(--fg-3)';
        l.style.width = '38px';
        row.appendChild(l);
        row.appendChild(control);
        return row;
    }

    // ===================== SELF RATINGS (gear/skill/relic) =====================
    // Cache the GET response per slot so the per-job containers + the peer block
    // share one round-trip.
    var selfBySlot = {};   // slot -> { jobs: {jobIndex: {gear,skill,relicNames}}, raw }
    var selfLoaded = {};

    function loadSelfSlot(slot) {
        return apiGet('/api/activity/job-ratings/' + encodeURIComponent(CFG.myAppUserId) +
            '?linkshellId=' + CFG.linkshellId + '&slot=' + slot).then(function (data) {
            var map = {};
            if (data && data.jobs) {
                data.jobs.forEach(function (j) {
                    map[j.jobIndex] = { gear: j.selfGear || 0, skill: j.selfSkill || 0, relicNames: (j.selfRelicNames || []).slice() };
                });
            }
            selfBySlot[slot] = { jobs: map, raw: data };
            selfLoaded[slot] = true;
            return selfBySlot[slot];
        });
    }

    function saveSelfJob(slot, jobIndex) {
        var e = (selfBySlot[slot] && selfBySlot[slot].jobs[jobIndex]) || { gear: 0, skill: 0, relicNames: [] };
        return apiPost('/api/activity/job-ratings', {
            linkshellId: CFG.linkshellId,
            targetAppUserId: CFG.myAppUserId,
            jobIndex: jobIndex,
            gear: e.gear, skill: e.skill,
            hasRelic: e.relicNames.length > 0,
            characterSlot: slot,
            relicNames: e.relicNames
        });
    }

    function renderSelfContainer(container) {
        var slot = parseInt(container.getAttribute('data-slot'), 10) || 0;
        var jobIndex = parseInt(container.getAttribute('data-job'), 10) || 0;
        var entry = (selfBySlot[slot] && selfBySlot[slot].jobs[jobIndex]) || { gear: 0, skill: 0, relicNames: [] };
        selfBySlot[slot] = selfBySlot[slot] || { jobs: {} };
        selfBySlot[slot].jobs[jobIndex] = entry;
        container.innerHTML = '';

        container.appendChild(labeledRow('Gear', makeStars(entry.gear, function (v) { entry.gear = v; saveSelfJob(slot, jobIndex); })));
        container.appendChild(labeledRow('Skill', makeStars(entry.skill, function (v) { entry.skill = v; saveSelfJob(slot, jobIndex); })));

        // Relic add-multiple: chips + an "add relic" dropdown of unused options.
        var relicWrap = document.createElement('div');
        relicWrap.style.marginTop = '6px';
        function renderRelics() {
            relicWrap.innerHTML = '';
            var lbl = document.createElement('div');
            lbl.textContent = 'RELIC';
            lbl.style.fontSize = '10px';
            lbl.style.textTransform = 'uppercase';
            lbl.style.letterSpacing = '.04em';
            lbl.style.color = 'var(--fg-3)';
            relicWrap.appendChild(lbl);
            var chips = document.createElement('div');
            chips.style.display = 'flex';
            chips.style.flexWrap = 'wrap';
            chips.style.gap = '4px';
            chips.style.marginTop = '4px';
            entry.relicNames.forEach(function (name) {
                var chip = document.createElement('span');
                chip.className = 'tag relic-flame';
                chip.style.display = 'inline-flex';
                chip.style.alignItems = 'center';
                chip.style.gap = '4px';
                chip.textContent = name;
                var x = document.createElement('button');
                x.type = 'button';
                x.textContent = '×';
                x.style.cssText = 'background:none;border:0;color:inherit;cursor:pointer;font-size:13px;line-height:1;padding:0';
                x.addEventListener('click', function () {
                    entry.relicNames = entry.relicNames.filter(function (r) { return r !== name; });
                    saveSelfJob(slot, jobIndex); renderRelics();
                });
                chip.appendChild(x);
                chips.appendChild(chip);
            });
            relicWrap.appendChild(chips);
            var avail = (RELICS[jobIndex] || []).filter(function (r) { return entry.relicNames.indexOf(r) < 0; });
            if (avail.length > 0) {
                var sel = document.createElement('select');
                sel.className = 'form-select';
                sel.style.cssText = 'margin-top:4px;font-size:11px;max-width:160px';
                var opt0 = document.createElement('option');
                opt0.value = ''; opt0.textContent = '+ Add relic';
                sel.appendChild(opt0);
                avail.forEach(function (r) {
                    var o = document.createElement('option'); o.value = r; o.textContent = r; sel.appendChild(o);
                });
                sel.addEventListener('change', function () {
                    if (!sel.value) { return; }
                    entry.relicNames = entry.relicNames.concat([sel.value]);
                    saveSelfJob(slot, jobIndex); renderRelics();
                });
                relicWrap.appendChild(sel);
            }
        }
        renderRelics();
        container.appendChild(relicWrap);
    }

    // ===================== PEER FEEDBACK (what they think) =====================
    function renderPeerFeedback(container, slot) {
        var data = selfBySlot[slot] && selfBySlot[slot].raw;
        container.innerHTML = '';
        if (!data) { return; }
        var box = document.createElement('div');
        box.style.cssText = 'padding:12px 14px;border:1px solid var(--border);border-radius:10px';
        var title = document.createElement('div');
        title.textContent = 'WHAT YOUR LINKSHELL THINKS';
        title.style.cssText = 'font-weight:600;letter-spacing:.04em;font-size:12px';
        box.appendChild(title);

        if ((data.peerRaterCount || 0) > 0) {
            var sub = document.createElement('div');
            sub.textContent = 'Based on feedback from ' + data.peerRaterCount + ' teammate' + (data.peerRaterCount === 1 ? '' : 's') + '.';
            sub.style.cssText = 'font-size:11px;opacity:.65;margin:4px 0 10px';
            box.appendChild(sub);
            var rated = (data.jobs || []).filter(function (j) { return j.peerCount > 0; });
            rated.forEach(function (j) {
                var row = document.createElement('div');
                row.style.cssText = 'display:grid;grid-template-columns:54px 1fr 1fr auto;gap:10px;align-items:center;padding:4px 0;border-top:1px solid var(--border);font-size:12px';
                var name = document.createElement('span'); name.textContent = JOBS[j.jobIndex] || ('Job ' + j.jobIndex); name.style.fontWeight = '600';
                var rated2 = document.createElement('span'); rated2.textContent = j.peerCount; rated2.style.opacity = '.6'; rated2.style.textAlign = 'right';
                row.appendChild(name);
                row.appendChild(makeStars(j.peerAvgGear, null, true));
                row.appendChild(makeStars(j.peerAvgSkill, null, true));
                row.appendChild(rated2);
                box.appendChild(row);
            });
        } else {
            var none = document.createElement('div');
            none.textContent = 'No teammates have rated your jobs yet.';
            none.style.cssText = 'font-size:12px;opacity:.7;margin-top:8px';
            box.appendChild(none);
        }

        if ((data.peerCommentCount || 0) > 0) {
            var sumWrap = document.createElement('div');
            sumWrap.style.marginTop = '12px';
            var sumTitle = document.createElement('div');
            sumTitle.textContent = 'Summary';
            sumTitle.style.cssText = 'font-size:12px;font-weight:600';
            sumWrap.appendChild(sumTitle);
            var sumP = document.createElement('p');
            sumP.style.cssText = 'font-size:11px;opacity:.7;margin:8px 0 0';
            sumP.textContent = 'Summarizing ' + data.peerCommentCount + ' comment' + (data.peerCommentCount === 1 ? '' : 's') + '…';
            sumWrap.appendChild(sumP);
            box.appendChild(sumWrap);
            apiGet('/api/activity/job-ratings/' + encodeURIComponent(CFG.myAppUserId) + '/comment-summary?linkshellId=' + CFG.linkshellId + '&slot=' + slot)
                .then(function (s) { if (s && s.summary) { sumP.style.opacity = '.95'; sumP.style.fontSize = '12px'; sumP.textContent = s.summary; } else { sumP.style.display = 'none'; } });
            (data.peerComments || []).forEach(function (c) {
                var bq = document.createElement('blockquote');
                bq.textContent = c;
                bq.style.cssText = 'margin:8px 0 0;padding:6px 10px;border-left:2px solid var(--border);font-size:12px;opacity:.9';
                box.appendChild(bq);
            });
        }
        container.appendChild(box);
    }

    // Render every self container + peer block for a slot once its data is loaded.
    function applySlot(slot) {
        document.querySelectorAll('.job-ratings[data-slot="' + slot + '"]').forEach(renderSelfContainer);
        document.querySelectorAll('.peer-feedback[data-slot="' + slot + '"]').forEach(function (c) { renderPeerFeedback(c, slot); });
    }
    function ensureSlot(slot) {
        if (selfLoaded[slot]) { applySlot(slot); return; }
        loadSelfSlot(slot).then(function () { applySlot(slot); });
    }

    var TAB_TO_SLOT = { main: 0, alt1: 1, alt2: 2 };
    function onTabShown(tabKey) { ensureSlot(TAB_TO_SLOT[tabKey] != null ? TAB_TO_SLOT[tabKey] : 0); }

    // ===================== RATE TEAMMATES =====================
    function targetCharacters(member) {
        var list = [{ slot: 0, name: member.name }];
        if (member.alt1) { list.push({ slot: 1, name: member.alt1 }); }
        if (member.alt2) { list.push({ slot: 2, name: member.alt2 }); }
        return list;
    }

    function initRateTeammates() {
        var card = document.getElementById('rateTeammatesCard');
        if (!card || MEMBERS.length === 0) { if (card) { card.style.display = 'none'; } return; }
        var search = document.getElementById('rateMemberSearch');
        var memberSel = document.getElementById('rateMemberSelect');
        var charField = document.getElementById('rateCharacterField');
        var charSel = document.getElementById('rateCharacterSelect');
        var grid = document.getElementById('rateGrid');
        var commentWrap = document.getElementById('rateComment');
        var commentBox = document.getElementById('rateCommentBox');
        var commentSave = document.getElementById('rateCommentSave');
        var commentStatus = document.getElementById('rateCommentStatus');

        function fillMembers(filter) {
            var f = (filter || '').trim().toLowerCase();
            memberSel.innerHTML = '';
            var first = document.createElement('option');
            first.value = ''; first.textContent = 'Select a member…';
            memberSel.appendChild(first);
            MEMBERS.filter(function (m) { return !f || m.name.toLowerCase().indexOf(f) >= 0; }).forEach(function (m) {
                var o = document.createElement('option'); o.value = m.appUserId; o.textContent = m.name; memberSel.appendChild(o);
            });
        }
        fillMembers('');
        search.addEventListener('input', function () { fillMembers(search.value); });

        function currentMember() { return MEMBERS.find(function (m) { return m.appUserId === memberSel.value; }) || null; }

        function loadAndRender() {
            var member = currentMember();
            if (!member) { grid.innerHTML = ''; charField.style.display = 'none'; commentWrap.style.display = 'none'; return; }
            // Character (main/alt) picker.
            var chars = targetCharacters(member);
            charField.style.display = chars.length > 1 ? '' : 'none';
            charSel.innerHTML = '';
            chars.forEach(function (c) { var o = document.createElement('option'); o.value = c.slot; o.textContent = c.name + (c.slot === 0 ? ' (main)' : ' (alt)'); charSel.appendChild(o); });
            renderGridForSlot(member, parseInt(charSel.value, 10) || 0);
        }

        function renderGridForSlot(member, slot) {
            grid.innerHTML = 'Loading…';
            apiGet('/api/activity/job-ratings/' + encodeURIComponent(member.appUserId) + '?linkshellId=' + CFG.linkshellId + '&slot=' + slot).then(function (data) {
                grid.innerHTML = '';
                var mine = {}, lvl = {};
                if (data && data.jobs) { data.jobs.forEach(function (j) { mine[j.jobIndex] = { gear: j.myGear || 0, skill: j.mySkill || 0 }; lvl[j.jobIndex] = j.level || 0; }); }
                var wrap = document.createElement('div');
                wrap.style.cssText = 'display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:10px';
                JOBS.forEach(function (jobName, idx) {
                    var e = mine[idx] || { gear: 0, skill: 0 };
                    var lv = lvl[idx] || 0;
                    var cell = document.createElement('div');
                    cell.style.cssText = 'border:1px solid var(--border);border-radius:8px;padding:8px 10px';
                    if (lv === 0) { cell.style.opacity = '.6'; }
                    // Job name + the teammate's level for it, so the rater knows what they're scoring.
                    var h = document.createElement('div'); h.style.cssText = 'font-size:12px;font-weight:600;display:flex;align-items:baseline;gap:6px';
                    var hn = document.createElement('span'); hn.textContent = jobName; h.appendChild(hn);
                    var hl = document.createElement('span'); hl.textContent = lv > 0 ? ('Lv ' + lv) : '—';
                    hl.style.cssText = 'font-size:11px;font-weight:600;color:' + (lv > 0 ? 'var(--accent)' : 'var(--fg-3)');
                    h.appendChild(hl);
                    cell.appendChild(h);
                    cell.appendChild(labeledRow('Gear', makeStars(e.gear, function (v) { e.gear = v; saveTeammate(member, slot, idx, e); })));
                    cell.appendChild(labeledRow('Skill', makeStars(e.skill, function (v) { e.skill = v; saveTeammate(member, slot, idx, e); })));
                    wrap.appendChild(cell);
                });
                grid.appendChild(wrap);

                // Comment box (per member+slot): seed with my existing comment.
                commentWrap.style.display = '';
                commentBox.value = (data && data.myComment) || '';
                commentStatus.textContent = '';
                commentSave.onclick = function () {
                    commentSave.disabled = true;
                    apiPost('/api/activity/job-ratings/comment', {
                        linkshellId: CFG.linkshellId, targetAppUserId: member.appUserId,
                        comment: commentBox.value, characterSlot: slot
                    }).then(function (ok) {
                        commentStatus.textContent = ok ? 'Saved.' : 'Save failed.';
                        commentSave.disabled = false;
                    });
                };
            });
        }

        function saveTeammate(member, slot, jobIndex, e) {
            apiPost('/api/activity/job-ratings', {
                linkshellId: CFG.linkshellId, targetAppUserId: member.appUserId,
                jobIndex: jobIndex, gear: e.gear, skill: e.skill, hasRelic: false, characterSlot: slot, relicNames: []
            });
        }

        memberSel.addEventListener('change', loadAndRender);
        charSel.addEventListener('change', function () { var m = currentMember(); if (m) { renderGridForSlot(m, parseInt(charSel.value, 10) || 0); } });
    }

    // ===================== INIT =====================
    window.LSMProfileRatings = { onTabShown: onTabShown };
    ensureSlot(0);          // main tab is shown first
    initRateTeammates();
})();
