import { Component, ElementRef, model, input, viewChild } from '@angular/core';

import { FormatId, TextEdit, formatEdit } from '../markdown-format.helpers';

interface ToolbarAction {
  readonly id: FormatId;
  readonly glyph: string;
  // Tooltip. Spells out the syntax so the raw markdown in the box stays legible
  // to whoever edits the event next.
  readonly title: string;
  // Optional glyph styling class (renders the B bold, the I italic, etc.).
  readonly glyphClass?: string;
  // Starts a new visual group in the toolbar.
  readonly startsGroup?: boolean;
}

// Rich-text-style toolbar over a plain <textarea>, emitting Discord-flavoured
// markdown rather than HTML.
//
// It is deliberately NOT a WYSIWYG/contenteditable editor: this value's primary
// consumer is a Discord embed description (DiscordEventMessageBuilder
// BuildEmbed/BuildBoardEmbed), which accepts only Discord markdown — HTML would
// have to be converted back on the server and would still lose anything Discord
// cannot express. Same reason there is no font-family/size/colour control:
// Discord embeds have no such formatting, so the button would silently do
// nothing. Headings are the closest available "bigger text".
//
// The text math lives in markdown-format.helpers; this component only applies
// the resulting edit to the DOM. Edits go through execCommand('insertText')
// where the browser allows it so the textarea's native undo stack (Ctrl+Z)
// survives toolbar use, falling back to setRangeText otherwise.
@Component({
  selector: 'app-markdown-textarea',
  templateUrl: './markdown-textarea.component.html',
  styleUrl: './markdown-textarea.component.scss'
})
export class MarkdownTextareaComponent {
  readonly value = model<string | null | undefined>('');
  readonly placeholder = input('');
  readonly ariaLabel = input('Notes');
  // Matches the 1500-char cap the Discord embed description is truncated to in
  // DiscordEventMessageBuilder, and keeps the value clear of the 2048 limit on
  // HnmRecurringBoard.Details that a recurring board copies it into.
  readonly maxLength = input(1500);

  private readonly area = viewChild<ElementRef<HTMLTextAreaElement>>('area');

  protected readonly actions: readonly ToolbarAction[] = [
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

  protected get length(): number {
    return (this.value() ?? '').length;
  }

  protected onInput(event: Event): void {
    this.value.set((event.target as HTMLTextAreaElement).value);
  }

  // Ctrl/Cmd shortcuts for the formats that conventionally have them.
  protected onKeydown(event: KeyboardEvent): void {
    if (!event.ctrlKey && !event.metaKey) {
      return;
    }
    const id: FormatId | null =
      event.key === 'b' || event.key === 'B' ? 'bold' :
      event.key === 'i' || event.key === 'I' ? 'italic' :
      event.key === 'u' || event.key === 'U' ? 'underline' :
      event.key === 'k' || event.key === 'K' ? 'link' :
      null;
    if (!id) {
      return;
    }
    event.preventDefault();
    this.apply(id);
  }

  protected apply(id: FormatId): void {
    const el = this.area()?.nativeElement;
    if (!el) {
      return;
    }
    this.applyEdit(el, formatEdit(id, el.value, el.selectionStart, el.selectionEnd));
  }

  private applyEdit(el: HTMLTextAreaElement, edit: TextEdit): void {
    el.focus();
    el.setSelectionRange(edit.start, edit.end);

    let inserted = false;
    if (edit.text.length > 0) {
      try {
        inserted = document.execCommand('insertText', false, edit.text);
      } catch {
        inserted = false;
      }
    }
    if (!inserted) {
      el.setRangeText(edit.text, edit.start, edit.end, 'end');
    }

    el.setSelectionRange(edit.selectStart, edit.selectEnd);
    this.value.set(el.value);
  }
}
