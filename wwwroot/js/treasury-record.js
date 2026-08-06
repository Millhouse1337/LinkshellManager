// The Record and Fix forms in Management > Treasury > Gil.
//
// Both views render the same fields, so both load this. It does three things:
//   - shows the help text and plain-words preview for whichever "what happened" option is picked
//   - swaps the single Member box for the who-gets-a-share list when the option splits an amount
//   - works out each person's share, the same way TreasurySplit.Allocate does on the server
//
// That last one matters: if the preview rounded differently than the server, the form would promise
// a split that is not the one recorded.
(function () {
    'use strict';

    var kind = document.getElementById('TransactionKind');
    if (!kind) { return; }

    var amount = document.getElementById('Amount');
    var help = document.getElementById('kindHelp');
    var preview = document.getElementById('preview');
    var memberField = document.getElementById('memberField');
    var memberInput = document.getElementById('Member');
    var memberOwed = document.getElementById('memberOwed');
    var memberOptions = document.getElementById('memberOptions');
    var recipientsField = document.getElementById('recipientsField');
    var recipientList = document.getElementById('recipientList');
    var recipientFilter = document.getElementById('recipientFilter');
    var splitPreview = document.getElementById('splitPreview');

    function selected() { return kind.options[kind.selectedIndex]; }

    function typedAmount() {
        var digits = (amount && amount.value ? amount.value : '').replace(/[^0-9]/g, '');
        return digits ? Number(digits) : 0;
    }

    function pickedRows() {
        if (!recipientList) { return []; }
        return Array.prototype.slice
            .call(recipientList.querySelectorAll('input[type="checkbox"]:checked'))
            .map(function (box) {
                var name = box.parentNode.querySelector('.lsm-recipient__name');
                return name ? name.textContent.trim() : '';
            })
            // Sorted by name, because that is the order the server allocates the leftover gil in.
            .sort(function (a, b) { return a.toLowerCase().localeCompare(b.toLowerCase()); });
    }

    // Whole gil only, and the shares always add back up to the total: the first (total % count)
    // people get one extra. Mirrors Services/TreasurySplit.cs.
    function allocate(total, count) {
        var base = Math.floor(total / count);
        var leftover = total % count;
        var shares = [];
        for (var i = 0; i < count; i++) {
            shares.push(base + (i < leftover ? 1 : 0));
        }
        return shares;
    }

    function renderSplit() {
        if (!splitPreview) { return ''; }

        var names = pickedRows();
        if (names.length === 0) {
            splitPreview.textContent = '';
            return '';
        }

        var total = typedAmount();
        var shares = allocate(total, names.length);
        splitPreview.textContent = names
            .map(function (name, index) { return name + ' ' + shares[index].toLocaleString(); })
            .join(' · ');

        var smallest = shares[shares.length - 1];
        var extra = shares.filter(function (share) { return share > smallest; }).length;
        var sentence = ' That is ' + names.length + (names.length === 1 ? ' member' : ' members')
            + ' at ' + smallest.toLocaleString() + ' gil each';
        return extra === 0 ? sentence + '.' : sentence + ', and the first ' + extra + ' get 1 extra.';
    }

    // What the named member is still owed, or null when they are not on the list.
    function owedToTypedMember() {
        if (!memberInput || !memberOptions) { return null; }
        var typed = memberInput.value.trim().toLowerCase();
        if (!typed) { return null; }
        var match = Array.prototype.slice.call(memberOptions.options).filter(function (option) {
            return (option.value || '').trim().toLowerCase() === typed;
        })[0];
        return match ? Number(match.getAttribute('data-owed') || 0) : null;
    }

    // Settling hands over gil that was already promised, so the amount is known. Fill it in on pick
    // and say what it is — a part payment is legitimate, so the figure stays visible either way.
    function applyMemberOwed(fillAmount) {
        var option = selected();
        var settles = option && option.getAttribute('data-settles') === '1';
        if (!memberOwed) { return; }

        var owed = settles ? owedToTypedMember() : null;
        if (!settles || owed === null || owed === 0) {
            memberOwed.textContent = '';
            return;
        }

        memberOwed.textContent = memberInput.value.trim() + ' is owed ' + owed.toLocaleString() + ' gil.';
        if (fillAmount && amount) {
            amount.value = String(owed);
        }
    }

    function apply() {
        var option = selected();
        if (!option) { return; }

        var splits = option.getAttribute('data-split') === '1';
        if (help) { help.textContent = option.getAttribute('data-help') || ''; }

        // Only ask who was involved when the option actually concerns a member, and only ask for a
        // list when it shares the amount out.
        if (memberField) {
            memberField.style.display =
                option.getAttribute('data-member') === '1' && !splits ? '' : 'none';
        }
        if (recipientsField) {
            recipientsField.style.display = splits ? '' : 'none';
        }

        var extra = splits ? renderSplit() : '';
        if (splitPreview && !splits) { splitPreview.textContent = ''; }

        if (preview) {
            var template = option.getAttribute('data-preview') || '';
            // Split on the placeholder rather than String.replace, which only swaps the first
            // occurrence — two templates mention the amount twice ("takes {0} … clears {0}").
            preview.textContent = template.split('{0}').join(typedAmount().toLocaleString()) + extra;
        }
    }

    function filterRoster() {
        if (!recipientList || !recipientFilter) { return; }
        var term = recipientFilter.value.trim().toLowerCase();
        Array.prototype.forEach.call(recipientList.children, function (row) {
            var name = row.getAttribute('data-name') || '';
            // A picked member stays visible even when filtered out, so nobody is accidentally
            // dropped by typing in the search box.
            var picked = row.querySelector('input[type="checkbox"]').checked;
            row.style.display = !term || picked || name.indexOf(term) !== -1 ? '' : 'none';
        });
    }

    // The server refuses to record while a departed member is still named on the entry being fixed.
    // Dropping them is how an officer says that is deliberate — it clears the hidden fields the
    // server checks, so the refusal is acknowledged rather than merely dismissed on screen.
    var dropUnresolved = document.getElementById('dropUnresolved');
    if (dropUnresolved) {
        dropUnresolved.addEventListener('click', function () {
            var warn = document.getElementById('unresolvedRecipients');
            if (warn) { warn.parentNode.removeChild(warn); }
        });
    }

    kind.addEventListener('change', function () { apply(); applyMemberOwed(false); });
    if (amount) { amount.addEventListener('input', apply); }
    if (recipientList) { recipientList.addEventListener('change', apply); }
    if (recipientFilter) { recipientFilter.addEventListener('input', filterRoster); }
    if (memberInput) {
        // Fill the amount in only when the typed name actually matches someone on the list, so
        // half-typed text does not keep overwriting a figure the officer is editing.
        memberInput.addEventListener('input', function () { applyMemberOwed(true); apply(); });
    }

    apply();
    applyMemberOwed(false);
    filterRoster();
})();
