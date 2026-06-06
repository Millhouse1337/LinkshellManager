window.escapeHtml = function (s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
    });
};

(function () {
    var clockEl = document.getElementById('topbar-clock');
    var timeEl = document.querySelector('[data-clock-time]');
    var zoneEl = document.querySelector('[data-clock-zone]');
    if (!clockEl || !timeEl || !zoneEl) return;

    // Prefer the user's configured IANA zone from their profile so the header
    // matches the rest of the app (event times, etc.). Fall back to the browser
    // zone when the profile hasn't been set yet.
    var configuredTz = (clockEl.getAttribute('data-clock-tz') || '').trim();

    // If the saved profile zone is still on the server default ("" or "UTC"),
    // detect the browser's IANA zone and post it to the auto-detect endpoint
    // so subsequent requests (and other clients) see the right value.
    function maybeAutoDetectAndSave() {
        var browserTz = '';
        try { browserTz = (Intl.DateTimeFormat().resolvedOptions().timeZone || '').trim(); } catch (e) {}
        if (!browserTz) return;

        var isUnset = configuredTz === '' || configuredTz.toUpperCase() === 'UTC';
        if (!isUnset || browserTz === configuredTz) return;

        // Antiforgery is required for cookie-authed POSTs. Fetch the token, then
        // post the detected zone; if accepted, swap the in-page formatters so
        // the header reflects the new zone without a reload.
        fetch('/api/activity/antiforgery', { credentials: 'same-origin' })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(function (af) {
                if (!af || !af.headerName || !af.requestToken) return null;
                var headers = { 'Content-Type': 'application/json' };
                headers[af.headerName] = af.requestToken;
                return fetch('/api/activity/profile/detect-time-zone', {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: headers,
                    body: JSON.stringify({ timeZone: browserTz })
                });
            })
            .then(function (res) { return res && res.ok ? res.json() : null; })
            .then(function (body) {
                if (body && body.applied && body.timeZone) {
                    configuredTz = body.timeZone;
                    clockEl.setAttribute('data-clock-tz', body.timeZone);
                    rebuildFormatters();
                    tick();
                }
            })
            .catch(function () { /* best-effort; header will retry next pageview */ });
    }

    var timeFmt;
    var zoneFmt;

    function rebuildFormatters() {
        var options = { hour: 'numeric', minute: '2-digit' };
        var zoneOptions = { timeZoneName: 'short' };
        if (configuredTz) {
            options.timeZone = configuredTz;
            zoneOptions.timeZone = configuredTz;
        }
        try {
            timeFmt = new Intl.DateTimeFormat([], options);
            zoneFmt = new Intl.DateTimeFormat([], zoneOptions);
        } catch (e) {
            // An invalid stored zone shouldn't break the header — retry with the
            // browser default so the user at least sees a clock.
            timeFmt = new Intl.DateTimeFormat([], { hour: 'numeric', minute: '2-digit' });
            zoneFmt = new Intl.DateTimeFormat([], { timeZoneName: 'short' });
        }
    }

    function extractZone(date) {
        try {
            var parts = zoneFmt.formatToParts(date);
            for (var i = 0; i < parts.length; i++) {
                if (parts[i].type === 'timeZoneName') return parts[i].value;
            }
        } catch (e) { /* fall through */ }
        return '';
    }

    function tick() {
        var now = new Date();
        timeEl.textContent = timeFmt.format(now);
        zoneEl.textContent = extractZone(now);
    }

    rebuildFormatters();
    tick();
    var msToNextMinute = 60000 - (Date.now() % 60000);
    setTimeout(function () {
        tick();
        setInterval(tick, 60000);
    }, msToNextMinute);

    maybeAutoDetectAndSave();
})();

// Thousands separators for whole-number inputs marked with `data-thousands`.
// Shows "1,000,000" while typing and strips the commas right before the owning
// form submits, so the server still receives a plain integer (model binders
// reject the grouped value). Non-negative integers only.
(function () {
    function group(digits) {
        return digits.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
    }
    function format(raw) {
        var digits = String(raw).replace(/[^\d]/g, '').replace(/^0+(?=\d)/, '');
        return digits === '' ? '' : group(digits);
    }
    function digitsBeforeCaret(str, pos) {
        var n = 0;
        for (var i = 0; i < pos && i < str.length; i++) {
            var c = str.charCodeAt(i);
            if (c >= 48 && c <= 57) n++;
        }
        return n;
    }
    function caretAfterNthDigit(str, count) {
        if (count <= 0) return 0;
        var n = 0;
        for (var i = 0; i < str.length; i++) {
            var c = str.charCodeAt(i);
            if (c >= 48 && c <= 57) {
                n++;
                if (n === count) return i + 1;
            }
        }
        return str.length;
    }
    function onInput(e) {
        var el = e.target;
        var before = el.value;
        var caret = el.selectionStart || 0;
        var wanted = digitsBeforeCaret(before, caret);
        var formatted = format(before);
        if (formatted !== before) {
            el.value = formatted;
            var pos = caretAfterNthDigit(formatted, wanted);
            try { el.setSelectionRange(pos, pos); } catch (err) { /* unsupported on some inputs */ }
        }
    }
    function init() {
        var inputs = document.querySelectorAll('input[data-thousands]');
        if (!inputs.length) return;
        // jQuery validate (loaded after site.js) is present by DOMContentLoaded.
        // Teach its numeric checks to ignore the grouping commas so ASP.NET
        // unobtrusive number/range validation passes on data-thousands fields.
        if (window.jQuery && window.jQuery.validator) {
            ['number', 'range'].forEach(function (m) {
                var orig = window.jQuery.validator.methods[m];
                if (!orig) return;
                window.jQuery.validator.methods[m] = function (value, element, param) {
                    if (element && element.getAttribute && element.getAttribute('data-thousands') !== null) {
                        value = String(value).replace(/,/g, '');
                    }
                    return orig.call(this, value, element, param);
                };
            });
        }
        inputs.forEach(function (el) {
            el.value = format(el.value);
            el.addEventListener('input', onInput);
            var form = el.form;
            if (form && !form.dataset.thousandsHooked) {
                form.dataset.thousandsHooked = '1';
                form.addEventListener('submit', function () {
                    form.querySelectorAll('input[data-thousands]').forEach(function (i) {
                        i.value = i.value.replace(/,/g, '');
                    });
                });
            }
        });
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();
