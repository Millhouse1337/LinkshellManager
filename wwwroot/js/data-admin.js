// Data Admin page behaviour. External file because the CSP is nonce-only (no inline handlers).
(function () {
    'use strict';

    // ---- Choose tables: "Tick group" and "Tick all / Clear all" ---------------------------------
    // Rows carry data-da-path ("Linkshell/Event/AppUserEvent"); a group is a row plus every row
    // whose path starts with its path followed by a slash.
    function rowsInGroup(path) {
        var rows = document.querySelectorAll('[data-da-path]');
        var matched = [];
        for (var i = 0; i < rows.length; i++) {
            var p = rows[i].getAttribute('data-da-path');
            if (p === path || (p && p.indexOf(path + '/') === 0)) {
                matched.push(rows[i]);
            }
        }
        return matched;
    }

    function setChecked(rows, on) {
        for (var i = 0; i < rows.length; i++) {
            var box = rows[i].querySelector('input[type="checkbox"][name="show"]');
            if (box) { box.checked = on; }
        }
    }

    document.addEventListener('click', function (e) {
        var target = e.target;
        if (!target || !target.getAttribute) { return; }

        var groupPath = target.getAttribute('data-da-group');
        if (groupPath) {
            e.preventDefault();
            var rows = rowsInGroup(groupPath);
            var allOn = rows.length > 0;
            for (var i = 0; i < rows.length; i++) {
                var box = rows[i].querySelector('input[type="checkbox"][name="show"]');
                if (box && !box.checked) { allOn = false; break; }
            }
            setChecked(rows, !allOn);
            return;
        }

        var all = target.getAttribute('data-da-all');
        if (all) {
            e.preventDefault();
            setChecked(document.querySelectorAll('[data-da-path]'), all === 'on');
        }
    });

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
