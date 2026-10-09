// Data Admin page behaviour. External file because the CSP is nonce-only (no inline handlers).
(function () {
    'use strict';

    // ---- List page: live search --------------------------------------------------------------
    // Rebuild the query string and navigate: a normal GET keeps server-side paging correct, the
    // sort and every f.* filter survive, and dropping `page` returns to page 1 when the term
    // changes. (No <form> wrapper, so row-level forms can never be nested inside it.)
    var q = document.getElementById('q');
    if (q) {
        var timer = null;
        var apply = function () {
            var params = new URLSearchParams(window.location.search);
            var value = q.value.trim();
            if (value) { params.set('q', value); } else { params.delete('q'); }
            params.delete('page');
            var qs = params.toString();
            window.location.href = window.location.pathname + (qs ? '?' + qs : '');
        };
        q.addEventListener('input', function () {
            if (timer) { clearTimeout(timer); }
            timer = setTimeout(apply, 400);
        });
        q.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                if (timer) { clearTimeout(timer); timer = null; }
                apply();
            }
        });
        // Keep typing smooth across the reload: re-focus with the caret at the end.
        if (q.value) {
            q.focus();
            var v = q.value; q.value = ''; q.value = v;
        }
    }

    // ---- Delete confirmation ------------------------------------------------------------------
    // Capture phase so it runs before the form submits; inline onsubmit is blocked by the CSP.
    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (form && form.matches && form.matches('form[data-confirm]')) {
            if (!window.confirm(form.getAttribute('data-confirm'))) {
                e.preventDefault();
            }
        }
    }, true);
})();
