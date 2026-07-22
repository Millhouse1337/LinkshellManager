// "Ratings" for the web View Profile page — the member's own self-assessment +
// peer gear/skill ratings + an Overall rollup (average ratings + AI comment summary).
// Reuses the SAME Activity API the Account profile page uses (/api/activity/job-ratings*)
// via cookie-auth GET, so the web mirrors the Discord Activity modal without any
// server-side aggregation. Read-only. Config + #memberPeer host come from
// MemberProfile.cshtml.
(function () {
    'use strict';

    var cfgEl = document.getElementById('member-peer-config');
    var host = document.getElementById('memberPeer');
    if (!cfgEl || !host) { return; }
    var CFG;
    try { CFG = JSON.parse(cfgEl.textContent || '{}'); } catch (e) { return; }
    if (!CFG.linkshellId || !CFG.targetAppUserId) { host.textContent = ''; return; }

    var JOBS = CFG.jobs || [];
    var SLOTS = CFG.slots || [];
    var STAR_ON = '#8d92ff', STAR_OFF = '#41434c';

    function apiGet(path) {
        return fetch(path, { credentials: 'same-origin', cache: 'no-store' })
            .then(function (r) { return r.ok ? r.json() : null; })
            .catch(function () { return null; });
    }

    // Read-only star row (0..5) + the numeric average (matches the Activity widget).
    function stars(value) {
        var wrap = document.createElement('span');
        wrap.style.cssText = 'display:inline-flex;align-items:center;gap:2px';
        var v = value || 0;
        for (var i = 0; i < 5; i++) {
            var s = document.createElement('span');
            s.textContent = '★';
            s.style.cssText = 'font-size:15px;line-height:1;color:' + (i < v ? STAR_ON : STAR_OFF);
            wrap.appendChild(s);
        }
        var val = document.createElement('span');
        val.style.cssText = 'font-size:12px;opacity:.7;margin-left:4px';
        val.textContent = v > 0 ? Number(v).toFixed(1) : '—';
        wrap.appendChild(val);
        return wrap;
    }

    function label(text, css) {
        var d = document.createElement('div');
        d.textContent = text;
        d.style.cssText = css;
        return d;
    }

    function jobUrl(slot) {
        return '/api/activity/job-ratings/' + encodeURIComponent(CFG.targetAppUserId) +
            '?linkshellId=' + CFG.linkshellId + '&slot=' + slot;
    }
    var overallUrl = '/api/activity/job-ratings/' + encodeURIComponent(CFG.targetAppUserId) +
        '/overall?linkshellId=' + CFG.linkshellId;

    var SELF_GRID = 'display:grid;grid-template-columns:54px 1fr 1fr;gap:10px;align-items:center;';
    var PEER_GRID = 'display:grid;grid-template-columns:54px 1fr 1fr auto;gap:10px;align-items:center;';
    var HEAD_CSS = 'font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:var(--fg-3);padding-bottom:4px;';
    var ROW_CSS = 'padding:4px 0;border-top:1px solid var(--border);font-size:12px;';
    var SUBLBL = 'font-size:11px;text-transform:uppercase;letter-spacing:.04em;color:var(--fg-3);';

    function gridHead(css, cols) {
        var head = document.createElement('div');
        head.style.cssText = css + HEAD_CSS;
        cols.forEach(function (h) { var c = document.createElement('span'); c.textContent = h; head.appendChild(c); });
        return head;
    }

    // One character block: the member's self-assessment + what the linkshell thinks
    // (per-job peer averages). Comments live in the Overall section, not here.
    function renderBlock(slotInfo, data) {
        var block = document.createElement('div');
        block.style.cssText = 'margin-top:12px';
        block.appendChild(label(slotInfo.name + (slotInfo.isAlt ? ' · alt' : ''),
            'font-size:12px;font-weight:600;color:var(--fg-2);margin-bottom:6px'));

        var selfJobs = (data.jobs || []).filter(function (j) { return (j.selfGear || 0) > 0 || (j.selfSkill || 0) > 0; });
        if (selfJobs.length > 0) {
            block.appendChild(label('Self-rated', SUBLBL + 'margin-bottom:4px'));
            block.appendChild(gridHead(SELF_GRID, ['Job', 'Gear', 'Skill']));
            selfJobs.forEach(function (j) {
                var row = document.createElement('div');
                row.style.cssText = SELF_GRID + ROW_CSS;
                var jn = document.createElement('span'); jn.style.fontWeight = '600'; jn.textContent = JOBS[j.jobIndex] || ('Job ' + (j.jobIndex + 1));
                row.appendChild(jn);
                row.appendChild(stars(j.selfGear));
                row.appendChild(stars(j.selfSkill));
                block.appendChild(row);
            });
        }

        if ((data.peerRaterCount || 0) > 0) {
            block.appendChild(label('What the linkshell thinks', SUBLBL + 'margin:12px 0 4px'));
            block.appendChild(label('Based on feedback from ' + data.peerRaterCount + ' teammate' + (data.peerRaterCount === 1 ? '' : 's') + '.',
                'font-size:11px;color:var(--fg-3);margin-bottom:8px'));
            block.appendChild(gridHead(PEER_GRID, ['Job', 'Gear', 'Skill', '# Rated']));
            (data.jobs || []).filter(function (j) { return j.peerCount > 0; }).forEach(function (j) {
                var row = document.createElement('div');
                row.style.cssText = PEER_GRID + ROW_CSS;
                var jn = document.createElement('span'); jn.style.fontWeight = '600'; jn.textContent = JOBS[j.jobIndex] || ('Job ' + (j.jobIndex + 1));
                var pc = document.createElement('span'); pc.style.opacity = '.6'; pc.textContent = j.peerCount;
                row.appendChild(jn);
                row.appendChild(stars(j.peerAvgGear));
                row.appendChild(stars(j.peerAvgSkill));
                row.appendChild(pc);
                block.appendChild(row);
            });
        }

        return block;
    }

    // The Overall rollup: average self + linkshell ratings across all the member's
    // characters, plus the AI summary of every peer comment they've received.
    function renderOverall(ov) {
        var box = document.createElement('div');
        box.style.cssText = 'margin-top:16px;border-top:1px solid var(--border);padding-top:12px';
        box.appendChild(label('Overall', 'font-weight:600;color:var(--fg);font-size:13px;margin-bottom:8px'));

        var OV_GRID = 'display:grid;grid-template-columns:80px 1fr 1fr;gap:10px;align-items:center;';
        if (ov.selfCount > 0 || ov.peerRaterCount > 0) {
            var head = document.createElement('div');
            head.style.cssText = OV_GRID + HEAD_CSS;
            ['', 'Gear', 'Skill'].forEach(function (h) { var c = document.createElement('span'); c.textContent = h; head.appendChild(c); });
            box.appendChild(head);
        }
        function avgRow(name, gear, skill) {
            var row = document.createElement('div');
            row.style.cssText = OV_GRID + ROW_CSS;
            var n = document.createElement('span'); n.style.fontWeight = '600'; n.textContent = name;
            row.appendChild(n); row.appendChild(stars(gear)); row.appendChild(stars(skill));
            return row;
        }
        if (ov.selfCount > 0) { box.appendChild(avgRow('Self', ov.selfAvgGear, ov.selfAvgSkill)); }
        if (ov.peerRaterCount > 0) {
            box.appendChild(avgRow('Linkshell', ov.peerAvgGear, ov.peerAvgSkill));
            box.appendChild(label('From ' + ov.peerRaterCount + ' teammate' + (ov.peerRaterCount === 1 ? '' : 's') + '.',
                'font-size:11px;color:var(--fg-3);margin-top:4px'));
        }

        if ((ov.commentCount || 0) > 0) {
            var sumWrap = document.createElement('div'); sumWrap.style.cssText = 'margin-top:12px';
            sumWrap.appendChild(label('Comments summary', 'font-size:12px;font-weight:600;color:var(--fg)'));
            if (ov.summary) {
                sumWrap.appendChild(label(ov.summary, 'font-size:12px;margin:6px 0 0;line-height:1.45;color:var(--fg-2)'));
            }
            (ov.comments || []).forEach(function (c) {
                var bq = document.createElement('blockquote');
                bq.style.cssText = 'margin:8px 0 0;padding:6px 10px;border-left:2px solid var(--border);font-size:12px;color:var(--fg-2)';
                bq.textContent = c;
                sumWrap.appendChild(bq);
            });
            box.appendChild(sumWrap);
        }
        return box;
    }

    // A character block is worth showing if it has self-ratings or peer ratings.
    function blockHasContent(data) {
        if (!data) { return false; }
        var self = (data.jobs || []).some(function (j) { return (j.selfGear || 0) > 0 || (j.selfSkill || 0) > 0; });
        return self || (data.peerRaterCount || 0) > 0;
    }
    function overallHasContent(ov) {
        return !!ov && ((ov.selfCount || 0) > 0 || (ov.peerRaterCount || 0) > 0 || (ov.commentCount || 0) > 0);
    }

    Promise.all([
        Promise.all(SLOTS.map(function (s) {
            return apiGet(jobUrl(s.slot)).then(function (data) { return { slot: s, data: data }; });
        })),
        apiGet(overallUrl)
    ]).then(function (out) {
        var results = out[0], overall = out[1];
        host.innerHTML = '';

        var blocks = results.filter(function (r) { return blockHasContent(r.data); });
        if (blocks.length === 0 && !overallHasContent(overall)) {
            host.appendChild(label('No ratings yet for this member.', 'font-size:12px;color:var(--fg-3)'));
            return;
        }
        blocks.forEach(function (r) { host.appendChild(renderBlock(r.slot, r.data)); });
        if (overallHasContent(overall)) { host.appendChild(renderOverall(overall)); }
    });
})();
