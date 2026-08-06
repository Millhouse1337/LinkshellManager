// Pure text math behind the notes toolbar (MarkdownTextareaComponent).
//
// Every operation is expressed as a TextEdit — "replace [start, end) with text,
// then leave the selection at [selectStart, selectEnd)" — so the component is
// left with nothing but the DOM call, and the caret arithmetic stays testable
// on plain strings.
//
// The output is Discord-flavoured markdown: the notes end up in a Discord embed
// description (DiscordEventMessageBuilder), which is why there is no
// font-family/size/colour operation — Discord embeds cannot express it.

export type FormatId =
  | 'bold'
  | 'italic'
  | 'underline'
  | 'strike'
  | 'heading'
  | 'bullet'
  | 'numbered'
  | 'quote'
  | 'code'
  | 'codeblock'
  | 'link'
  | 'spoiler';

export interface TextEdit {
  readonly start: number;
  readonly end: number;
  readonly text: string;
  readonly selectStart: number;
  readonly selectEnd: number;
}

// Any line marker this toolbar can produce. Stripped before a new one is
// applied so the line styles swap rather than stack ("1. - item").
const LINE_MARKER = /^(?:[-*] |\d+\. |> |#{1,3} )/;

export function formatEdit(id: FormatId, value: string, start: number, end: number): TextEdit {
  switch (id) {
    case 'bold': return wrap(value, start, end, '**', 'bold text');
    case 'italic': return wrap(value, start, end, '*', 'italic text');
    case 'underline': return wrap(value, start, end, '__', 'underlined text');
    case 'strike': return wrap(value, start, end, '~~', 'struck text');
    case 'code': return wrap(value, start, end, '`', 'code');
    case 'spoiler': return wrap(value, start, end, '||', 'spoiler');
    case 'heading': return linePrefix(value, start, end, () => '## ', /^#{1,3} /, 'Heading');
    case 'bullet': return linePrefix(value, start, end, () => '- ', /^[-*] /, 'List item');
    case 'numbered': return linePrefix(value, start, end, i => `${i + 1}. `, /^\d+\. /, 'List item');
    case 'quote': return linePrefix(value, start, end, () => '> ', /^> /, 'Quoted text');
    case 'codeblock': return codeBlock(value, start, end);
    case 'link': return link(value, start, end);
  }
}

// Wraps the selection in `marker`, or unwraps it when it is already wrapped
// (whether the markers sit inside or outside the selection), so every inline
// button toggles.
function wrap(value: string, start: number, end: number, marker: string, placeholder: string): TextEdit {
  const selected = value.slice(start, end);
  const width = marker.length;

  if (selected.length >= width * 2 && selected.startsWith(marker) && selected.endsWith(marker)) {
    const inner = selected.slice(width, -width);
    return { start, end, text: inner, selectStart: start, selectEnd: start + inner.length };
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

  const body = selected || placeholder;
  return {
    start,
    end,
    text: marker + body + marker,
    selectStart: start + width,
    selectEnd: start + width + body.length
  };
}

// Adds or removes a per-line marker across every line the selection touches.
function linePrefix(
  value: string,
  selStart: number,
  selEnd: number,
  build: (index: number) => string,
  match: RegExp,
  placeholder: string
): TextEdit {
  const start = selStart === 0 ? 0 : value.lastIndexOf('\n', selStart - 1) + 1;
  const lineEnd = value.indexOf('\n', selEnd);
  const end = lineEnd === -1 ? value.length : lineEnd;

  const lines = value.slice(start, end).split('\n');
  const filled = lines.filter(line => line.trim().length > 0);

  // Every marked line already? Then this click clears the marker.
  if (filled.length > 0 && filled.every(line => match.test(line))) {
    const cleared = lines.map(line => line.replace(match, '')).join('\n');
    return { start, end, text: cleared, selectStart: start, selectEnd: start + cleared.length };
  }

  // Empty single line: drop in a marked placeholder and select the text part.
  if (lines.length === 1 && lines[0].trim().length === 0) {
    const prefix = build(0);
    const text = prefix + placeholder;
    return { start, end, text, selectStart: start + prefix.length, selectEnd: start + text.length };
  }

  let index = 0;
  const marked = lines
    .map(line => (line.trim().length === 0 ? line : build(index++) + line.replace(LINE_MARKER, '')))
    .join('\n');
  return { start, end, text: marked, selectStart: start, selectEnd: start + marked.length };
}

// Fenced block, always on its own lines.
function codeBlock(value: string, start: number, end: number): TextEdit {
  const body = value.slice(start, end) || 'code';
  const before = start > 0 && value[start - 1] !== '\n' ? '\n' : '';
  const after = end < value.length && value[end] !== '\n' ? '\n' : '';
  const text = `${before}\`\`\`\n${body}\n\`\`\`${after}`;
  const bodyAt = start + before.length + 4;
  return { start, end, text, selectStart: bodyAt, selectEnd: bodyAt + body.length };
}

// A selection that looks like a URL becomes the target; anything else becomes
// the label. The half that was left a placeholder ends up selected — and with
// nothing selected that is the url, so Ctrl+K then paste yields a working link.
function link(value: string, start: number, end: number): TextEdit {
  const selected = value.slice(start, end).trim();
  const isUrl = /^(https?:\/\/|www\.)\S+$/i.test(selected);

  const label = isUrl ? 'text' : selected || 'text';
  const url = isUrl ? selected : 'url';
  const text = `[${label}](${url})`;
  const selectStart = isUrl ? start + 1 : start + label.length + 3;
  const selectEnd = selectStart + (isUrl ? label.length : url.length);
  return { start, end, text, selectStart, selectEnd };
}
