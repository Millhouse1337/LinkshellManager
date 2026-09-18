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
})();
