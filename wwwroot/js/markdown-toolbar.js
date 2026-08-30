// Discord-flavoured markdown toolbar over a plain <textarea>.
//
// Port of the Discord Activity's notes editor — MarkdownTextareaComponent plus
// the pure text math in markdown-format.helpers.ts — so both front-ends offer
// the same formatting buttons and produce the same output.
//
// It is deliberately NOT a WYSIWYG/contenteditable editor: this value's primary
// consumer is a Discord embed description (DiscordEventMessageBuilder
// BuildEmbed/BuildBoardEmbed), which accepts only Discord markdown. Same reason
// there is no font-family/size/colour control: Discord embeds cannot express
// it, so the button would silently do nothing.
//
// Usage: wrap a textarea in `<div class="mdta" data-markdown-toolbar>` — this
// script builds the toolbar and wires the behaviour on DOM ready.
(function () {
    'use strict';

    // Any line marker this toolbar can produce. Stripped before a new one is
    // applied so the line styles swap rather than stack ("1. - item").
    var LINE_MARKER = /^(?:[-*] |\d+\. |> |#{1,3} )/;

    // Matches the 1500-char cap the Discord embed description is truncated to
    // in DiscordEventMessageBuilder, and keeps the value clear of the 2048
    // limit on HnmRecurringBoard.Details that a recurring board copies it into.
    var DEFAULT_MAX_LENGTH = 1500;

    var ACTIONS = [
        { id: 'bold', glyph: 'B', title: 'Bold  **text**  (Ctrl+B)', glyphClass: 'mdta__glyph--bold' },
        { id: 'italic', glyph: 'I', title: 'Italic  *text*  (Ctrl+I)', glyphClass: 'mdta__glyph--italic' },
        { id: 'underline', glyph: 'U', title: 'Underline  __text__  (Ctrl+U)', glyphClass: 'mdta__glyph--underline' },
        { id: 'strike', glyph: 'S', title: 'Strikethrough  ~~text~~', glyphClass: 'mdta__glyph--strike' },
        { id: 'heading', glyph: 'H', title: 'Heading  ## text', startsGroup: true },
        { id: 'bullet', glyph: '•', title: 'Bulleted list  - item' },
        { id: 'numbered', glyph: '1.', title: 'Numbered list  1. item', glyphClass: 'mdta__glyph--tight' },
        { id: 'quote', glyph: '❝', title: 'Quote  > text' },
        { id: 'code', glyph: '</>', title: 'Inline code  `text`', glyphClass: 'mdta__glyph--tight', startsGroup: true },
        { id: 'codeblock', glyph: '▤', title: 'Code block  ```text```' },
        { id: 'link', glyph: '🔗', title: 'Link  [text](url)  (Ctrl+K)' },
        { id: 'spoiler', glyph: '▮', title: 'Spoiler  ||text||' }
    ];

    // ===== Text math =====
    //
    // Every operation is expressed as an edit — "replace [start, end) with text,
    // then leave the selection at [selectStart, selectEnd)" — so applying it is
    // one DOM call and the caret arithmetic stays plain string work.

    function formatEdit(id, value, start, end) {
        switch (id) {
            case 'bold': return wrap(value, start, end, '**', 'bold text');
            case 'italic': return wrap(value, start, end, '*', 'italic text');
            case 'underline': return wrap(value, start, end, '__', 'underlined text');
            case 'strike': return wrap(value, start, end, '~~', 'struck text');
            case 'code': return wrap(value, start, end, '`', 'code');
            case 'spoiler': return wrap(value, start, end, '||', 'spoiler');
            case 'heading': return linePrefix(value, start, end, function () { return '## '; }, /^#{1,3} /, 'Heading');
            case 'bullet': return linePrefix(value, start, end, function () { return '- '; }, /^[-*] /, 'List item');
            case 'numbered': return linePrefix(value, start, end, function (i) { return (i + 1) + '. '; }, /^\d+\. /, 'List item');
            case 'quote': return linePrefix(value, start, end, function () { return '> '; }, /^> /, 'Quoted text');
            case 'codeblock': return codeBlock(value, start, end);
            case 'link': return link(value, start, end);
            default: return null;
        }
    }

    // Wraps the selection in `marker`, or unwraps it when it is already wrapped
    // (whether the markers sit inside or outside the selection), so every
    // inline button toggles.
    function wrap(value, start, end, marker, placeholder) {
        var selected = value.slice(start, end);
        var width = marker.length;

        if (selected.length >= width * 2 &&
            selected.slice(0, width) === marker &&
            selected.slice(-width) === marker) {
            var inner = selected.slice(width, -width);
            return { start: start, end: end, text: inner, selectStart: start, selectEnd: start + inner.length };
        }

        if (start >= width && value.slice(start - width, start) === marker && value.slice(end, end + width) === marker) {
            return {
                start: start - width,
                end: end + width,
                text: selected,
                selectStart: start - width,
                selectEnd: start - width + selected.length
            };
        }

        var body = selected || placeholder;
        return {
            start: start,
            end: end,
            text: marker + body + marker,
            selectStart: start + width,
            selectEnd: start + width + body.length
        };
    }

    // Adds or removes a per-line marker across every line the selection touches.
    function linePrefix(value, selStart, selEnd, build, match, placeholder) {
        var start = selStart === 0 ? 0 : value.lastIndexOf('\n', selStart - 1) + 1;
        var lineEnd = value.indexOf('\n', selEnd);
        var end = lineEnd === -1 ? value.length : lineEnd;

        var lines = value.slice(start, end).split('\n');
        var filled = lines.filter(function (line) { return line.trim().length > 0; });

        // Every marked line already? Then this click clears the marker.
        if (filled.length > 0 && filled.every(function (line) { return match.test(line); })) {
            var cleared = lines.map(function (line) { return line.replace(match, ''); }).join('\n');
            return { start: start, end: end, text: cleared, selectStart: start, selectEnd: start + cleared.length };
        }

        // Empty single line: drop in a marked placeholder and select the text part.
        if (lines.length === 1 && lines[0].trim().length === 0) {
            var prefix = build(0);
            var text = prefix + placeholder;
            return { start: start, end: end, text: text, selectStart: start + prefix.length, selectEnd: start + text.length };
        }

        var index = 0;
        var marked = lines
            .map(function (line) {
                return line.trim().length === 0 ? line : build(index++) + line.replace(LINE_MARKER, '');
            })
            .join('\n');
        return { start: start, end: end, text: marked, selectStart: start, selectEnd: start + marked.length };
    }

    // Fenced block, always on its own lines.
    function codeBlock(value, start, end) {
        var body = value.slice(start, end) || 'code';
        var before = start > 0 && value[start - 1] !== '\n' ? '\n' : '';
        var after = end < value.length && value[end] !== '\n' ? '\n' : '';
        var text = before + '```\n' + body + '\n```' + after;
        var bodyAt = start + before.length + 4;
        return { start: start, end: end, text: text, selectStart: bodyAt, selectEnd: bodyAt + body.length };
    }

    // A selection that looks like a URL becomes the target; anything else
    // becomes the label. The half left as a placeholder ends up selected — and
    // with nothing selected that is the url, so Ctrl+K then paste yields a
    // working link.
    function link(value, start, end) {
        var selected = value.slice(start, end).trim();
        var isUrl = /^(https?:\/\/|www\.)\S+$/i.test(selected);

        var label = isUrl ? 'text' : (selected || 'text');
        var url = isUrl ? selected : 'url';
        var text = '[' + label + '](' + url + ')';
        var selectStart = isUrl ? start + 1 : start + label.length + 3;
        var selectEnd = selectStart + (isUrl ? label.length : url.length);
        return { start: start, end: end, text: text, selectStart: selectStart, selectEnd: selectEnd };
    }

    // ===== Wiring =====

    function upgrade(root) {
        var area = root.querySelector('textarea');
        if (!area || root.dataset.mdtaReady === '1') {
            return;
        }
        root.dataset.mdtaReady = '1';

        var maxLength = parseInt(root.getAttribute('data-max-length'), 10);
        if (isNaN(maxLength) || maxLength <= 0) {
            maxLength = DEFAULT_MAX_LENGTH;
        }
        area.classList.add('mdta__area');
        area.setAttribute('maxlength', String(maxLength));

        var toolbar = document.createElement('div');
        toolbar.className = 'mdta__toolbar';
        toolbar.setAttribute('role', 'toolbar');
        toolbar.setAttribute('aria-label', 'Formatting');

        ACTIONS.forEach(function (action) {
            if (action.startsGroup) {
                var divider = document.createElement('span');
                divider.className = 'mdta__divider';
                divider.setAttribute('aria-hidden', 'true');
                toolbar.appendChild(divider);
            }
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'mdta__btn';
            button.title = action.title;
            button.setAttribute('aria-label', action.title);
            var glyph = document.createElement('span');
            glyph.className = 'mdta__glyph' + (action.glyphClass ? ' ' + action.glyphClass : '');
            glyph.textContent = action.glyph;
            button.appendChild(glyph);
            button.addEventListener('click', function () { apply(action.id); });
            toolbar.appendChild(button);
        });

        var count = document.createElement('span');
        count.className = 'mdta__count';
        toolbar.appendChild(count);

        root.insertBefore(toolbar, area);

        function syncCount() {
            var length = area.value.length;
            count.textContent = length + ' / ' + maxLength;
            count.classList.toggle('mdta__count--over', length > maxLength);
        }

        // Edits go through execCommand('insertText') where the browser allows
        // it, so the textarea's native undo stack (Ctrl+Z) survives toolbar use;
        // setRangeText is the fallback.
        function applyEdit(edit) {
            area.focus();
            area.setSelectionRange(edit.start, edit.end);

            var inserted = false;
            if (edit.text.length > 0) {
                try {
                    inserted = document.execCommand('insertText', false, edit.text);
                } catch (err) {
                    inserted = false;
                }
            }
            if (!inserted) {
                area.setRangeText(edit.text, edit.start, edit.end, 'end');
            }

            area.setSelectionRange(edit.selectStart, edit.selectEnd);
            syncCount();
        }

        function apply(id) {
            var edit = formatEdit(id, area.value, area.selectionStart, area.selectionEnd);
            if (edit) {
                applyEdit(edit);
            }
        }

        // Ctrl/Cmd shortcuts for the formats that conventionally have them.
        area.addEventListener('keydown', function (event) {
            if (!event.ctrlKey && !event.metaKey) {
                return;
            }
            var key = event.key.toLowerCase();
            var id = key === 'b' ? 'bold'
                : key === 'i' ? 'italic'
                : key === 'u' ? 'underline'
                : key === 'k' ? 'link'
                : null;
            if (!id) {
                return;
            }
            event.preventDefault();
            apply(id);
        });

        area.addEventListener('input', syncCount);
        syncCount();
    }

    function upgradeAll() {
        document.querySelectorAll('[data-markdown-toolbar]').forEach(upgrade);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', upgradeAll);
    } else {
        upgradeAll();
    }
})();
