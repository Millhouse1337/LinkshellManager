// The Record and Fix forms in Management > Treasury > Gil.
//
// Both views render the same fields, so both load this. It does four things:
//   - turns the grouped "what happened" select into a row of ACTION buttons plus a short reason
//     list, so the everyday path is two clicks instead of one read of every option
//   - shows the help text and plain-words preview for whichever reason is picked
//   - swaps the single Member box for the who-gets-a-share list when the reason splits an amount
//   - works out each person's share, the same way TreasurySplit.Allocate does on the server
//
// That last one matters: if the preview rounded differently than the server, the form would promise
// a split that is not the one recorded.
//
// The action row is rendered hidden and un-hidden here, so a browser with no script gets the plain
// grouped select — every reason, under its action heading — and no buttons that do nothing.
(function () {
    'use strict';

    var kind = document.getElementById('TransactionKind');
    if (!kind) { return; }

    var actionRow = document.getElementById('kindActions');
    var kindField = kind.closest('.field');

    var amount = document.getElementById('Amount');
    var help = document.getElementById('kindHelp');
    var preview = document.getElementById('preview');
    var memberField = document.getElementById('memberField');
    var memberInput = document.getElementById('Member');
    var memberOwed = document.getElementById('memberOwed');
    var memberOptions = document.getElementById('memberOptions');
    var holderField = document.getElementById('holderField');
    var holderLabel = document.getElementById('holderLabel');
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

        // One member is a normal answer here, not a split of one. "1 member at 250,000 gil each"
        // is arithmetic nobody asked for when there is nothing to divide.
        if (names.length === 1) {
            return ' All of it goes to ' + names[0] + '.';
        }

        var smallest = shares[shares.length - 1];
        var extra = shares.filter(function (share) { return share > smallest; }).length;
        var sentence = ' That is ' + names.length + ' members at ' + smallest.toLocaleString()
            + ' gil each';
        return extra === 0
            ? sentence + '.'
            : sentence + ', and the first ' + extra + (extra === 1 ? ' gets' : ' get') + ' 1 extra.';
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

    // What the single name box is called, and whether it offers the roster at all.
    //
    // Only the option that settles a debt the linkshell already owes wants the roster: the app knows
    // the amount there, and the list carries members who have LEFT but are still owed. Every other
    // option that names somebody names a party who is usually NOT on the roster — whoever owes the
    // linkshell gil — so the box takes a typed name and drops the datalist rather than offering a
    // menu of the wrong people.
    function applyMemberLabel(option) {
        if (!memberInput) { return; }

        var label = memberField ? memberField.querySelector('label') : null;
        if (label) { label.textContent = option.getAttribute('data-memberlabel') || 'Member'; }

        var settles = option.getAttribute('data-settles') === '1';
        if (settles) {
            memberInput.setAttribute('list', 'memberOptions');
            memberInput.setAttribute('placeholder', 'Search members…');
        } else {
            memberInput.removeAttribute('list');
            memberInput.setAttribute('placeholder', 'Type a name');
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
        applyMemberLabel(option);

        // Whose mule. Shown for exactly the options that move gil on hand — a linkshell has no bank,
        // and gil that arrives on nobody's character cannot be found again. It stays on screen for a
        // split too, unlike the single-member box above: a split that moves gil on hand moves it off
        // ONE mule however many people share the proceeds.
        if (holderField) {
            var needsHolder = option.getAttribute('data-holder') === '1';
            holderField.style.display = needsHolder ? '' : 'none';
            // The label flips with the direction — naming who ends up with the gil is a different
            // question from naming whose stack it came out of.
            if (needsHolder && holderLabel) {
                holderLabel.textContent =
                    option.getAttribute('data-holderlabel') || "Who's holding this gil";
            }
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

    // ---- the action row ---------------------------------------------------------------------
    //
    // Every option is snapshotted up front, because narrowing the select means REBUILDING it: an
    // <option> hidden with CSS still shows in Safari's native picker, and `disabled` still shows it
    // greyed. Rebuilding is the only approach that actually narrows the menu everywhere.
    var allOptions = Array.prototype.slice.call(kind.querySelectorAll('option'));
    var actionButtons = actionRow
        ? Array.prototype.slice.call(actionRow.querySelectorAll('[data-action-pick]'))
        : [];

    function optionsForAction(actionKey) {
        return allOptions.filter(function (option) {
            return option.getAttribute('data-action') === actionKey;
        });
    }

    function showAction(actionKey) {
        var options = optionsForAction(actionKey);
        if (options.length === 0) { return; }

        // Read before the rebuild wipes it.
        var wanted = kind.value;
        kind.innerHTML = '';
        options.forEach(function (option) { kind.appendChild(option); });

        // Keep the current reason when it belongs to this action, otherwise take the first — which
        // is the everyday one, since the fallbacks are all ordered last. Keeping it is what makes
        // re-clicking the action you are already on do nothing, rather than quietly resetting a
        // reason you had already chosen.
        var stillThere = options.some(function (option) { return option.value === wanted; });
        kind.value = stillThere ? wanted : options[0].value;

        // An action with a single reason has nothing to ask. The select still holds the value and
        // still posts it; only the control is out of the way.
        if (kindField) {
            var label = kindField.querySelector('label');
            var only = options.length === 1;
            kind.style.display = only ? 'none' : '';
            if (label) { label.style.display = only ? 'none' : ''; }
        }

        actionButtons.forEach(function (button) {
            var on = button.getAttribute('data-action-pick') === actionKey;
            button.classList.toggle('primary', on);
            button.classList.toggle('ghost', !on);
            button.setAttribute('aria-pressed', on ? 'true' : 'false');
        });

        apply();
        applyMemberOwed(false);
    }

    if (actionRow && actionButtons.length > 0) {
        actionRow.hidden = false;
        actionButtons.forEach(function (button) {
            button.addEventListener('click', function () {
                showAction(button.getAttribute('data-action-pick'));
            });
        });

        // Open on the action the current value belongs to, so Fix and Edit-draft land on the entry's
        // own action rather than resetting it to the first one.
        var current = selected();
        var startAction = current && current.getAttribute('data-action');
        showAction(startAction || actionButtons[0].getAttribute('data-action-pick'));
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
