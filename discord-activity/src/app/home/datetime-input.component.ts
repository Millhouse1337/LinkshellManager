import { booleanAttribute, ChangeDetectionStrategy, Component, ElementRef, forwardRef, inject, input, signal } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';

import { DateTimeParts, joinLocalDateTime, needsSecondsBoxes, splitLocalDateTime } from './datetime-input.helpers';

type TimePart = 'hour' | 'minute' | 'second';

// A date + time input that keeps the SECONDS on every device. An HNM pop time is second-precise,
// and a tablet's own date-time picker (iPad, Android) shows no seconds at all, whatever `step`
// says -- so an officer on a tablet could neither see nor set them.
//
// A PC gets the browser's picker exactly as before. A tablet or phone gets the browser's date
// picker plus Hour : Min : Sec boxes (number keypad) and an AM/PM switch. Either way the value is
// the same datetime-local string ("2026-11-05T07:28:29"), so callers bind it with ngModel as they
// did the plain input. `inputClass` is the form's own input class, so the boxes match its fields.
@Component({
  selector: 'app-datetime-input',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => DateTimeInputComponent), multi: true }],
  template: `
    @if (!boxes) {
      <input type="datetime-local" step="1" [class]="inputClass()" [value]="value()" [required]="required()"
        [disabled]="disabled()" (input)="onNativeInput($any($event.target).value)" (blur)="onTouched()" />
    } @else {
      <div class="dti" (focusout)="onFocusOut($event)">
        <input type="date" class="dti__date" [class]="inputClass()" aria-label="Date" [value]="parts().date"
          [required]="required()" [disabled]="disabled()" (input)="onDate($any($event.target).value)" />
        <span class="dti__time">
          @for (part of timeParts; track part.key; let last = $last) {
            <input type="text" inputmode="numeric" pattern="[0-9]*" maxlength="2" autocomplete="off"
              class="dti__part" [class]="inputClass()" [attr.data-part]="part.key" [attr.aria-label]="part.label"
              [placeholder]="part.placeholder" [value]="parts()[part.key]" [disabled]="disabled()"
              (focus)="$any($event.target).select()" (input)="onPart(part.key, $any($event.target))" />
            @if (!last) { <span class="dti__sep">:</span> }
          }
          <span class="dti__ampm" role="group" aria-label="AM or PM">
            <button type="button" [class.dti__on]="!parts().pm" [disabled]="disabled()" (click)="setPm(false)">AM</button>
            <button type="button" [class.dti__on]="parts().pm" [disabled]="disabled()" (click)="setPm(true)">PM</button>
          </span>
        </span>
      </div>
    }
  `,
  styles: `
    :host { display: block; min-width: 0; }
    :host .dti { display: flex; flex-wrap: wrap; align-items: stretch; gap: 6px; }
    :host .dti__date { flex: 1 1 9.5em; width: auto; min-width: 0; }
    :host .dti__time { display: flex; align-items: stretch; gap: 4px; }
    :host .dti__part { width: 2.9em; min-width: 0; padding: 0 4px; text-align: center; }
    :host .dti__sep { align-self: center; color: var(--fg-3); }
    :host .dti__ampm { display: flex; margin-left: 4px; border: 1px solid var(--border-2); border-radius: 6px; overflow: hidden; }
    :host .dti__ampm button {
      min-width: 2.8em; padding: 0 8px; border: 0; background: var(--surface); color: var(--fg-3);
      font: inherit; font-weight: 600; cursor: pointer;
    }
    :host .dti__ampm button.dti__on { background: var(--accent); color: #fff; }
    :host .dti__ampm button:disabled { cursor: not-allowed; opacity: 0.6; }
  `,
})
export class DateTimeInputComponent implements ControlValueAccessor {
  readonly inputClass = input('');
  readonly required = input(false, { transform: booleanAttribute });

  protected readonly boxes = needsSecondsBoxes();
  protected readonly timeParts: readonly { key: TimePart; label: string; placeholder: string }[] = [
    { key: 'hour', label: 'Hour', placeholder: 'hh' },
    { key: 'minute', label: 'Minute', placeholder: 'mm' },
    { key: 'second', label: 'Second', placeholder: 'ss' },
  ];

  protected readonly value = signal('');
  protected readonly parts = signal<DateTimeParts>(splitLocalDateTime(''));
  protected readonly disabled = signal(false);

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private onChange: (value: string) => void = () => undefined;
  protected onTouched: () => void = () => undefined;

  writeValue(value: string | null): void {
    this.value.set(value ?? '');
    this.parts.set(splitLocalDateTime(value));
  }

  registerOnChange(fn: (value: string) => void): void { this.onChange = fn; }
  registerOnTouched(fn: () => void): void { this.onTouched = fn; }
  setDisabledState(disabled: boolean): void { this.disabled.set(disabled); }

  protected onNativeInput(value: string): void {
    this.value.set(value);
    this.onChange(value);
  }

  protected onDate(date: string): void {
    this.update({ ...this.parts(), date });
  }

  protected setPm(pm: boolean): void {
    this.update({ ...this.parts(), pm });
  }

  // Digits only, two at most; two digits move on to the next box, the way a time is read out.
  protected onPart(part: TimePart, box: HTMLInputElement): void {
    const digits = box.value.replace(/\D/g, '').slice(0, 2);
    if (box.value !== digits) {
      box.value = digits;
    }
    this.update({ ...this.parts(), [part]: digits });
    if (digits.length === 2 && part !== 'second') {
      const next = part === 'hour' ? 'minute' : 'second';
      this.host.nativeElement.querySelector<HTMLInputElement>(`[data-part="${next}"]`)?.focus();
    }
  }

  // Leaving the whole control tidies the boxes to the saved value: "7" reads "07", and a time left
  // half-typed (no hour) goes back to the last good one rather than sitting there unsaved. A field
  // with nothing saved yet keeps what was typed.
  protected onFocusOut(event: FocusEvent): void {
    const into = event.relatedTarget as Node | null;
    if (into && this.host.nativeElement.contains(into)) {
      return;
    }
    if (this.value()) {
      this.parts.set(splitLocalDateTime(this.value()));
    }
    this.onTouched();
  }

  private update(parts: DateTimeParts): void {
    this.parts.set(parts);
    const joined = joinLocalDateTime(parts);
    if (joined === null || joined === this.value()) {
      return; // still typing, or nothing changed
    }
    this.value.set(joined);
    this.onChange(joined);
  }
}
