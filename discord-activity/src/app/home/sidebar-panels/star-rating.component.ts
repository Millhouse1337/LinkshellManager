import { Component, EventEmitter, Input, Output } from '@angular/core';

// A compact 0–5 star control. Editable by default (click a star to set, click the
// same star again to clear to 0); pass [readonly]="true" to render an average as
// filled stars plus its numeric value. Used by the profile job grid (self gear/
// skill) and the rate-teammates panel.
@Component({
  selector: 'app-star-rating',
  standalone: true,
  template: `
    <span class="stars" [class.readonly]="readonly">
      @for (s of starList; track s) {
        <button
          type="button"
          class="star"
          [class.on]="s <= rounded"
          [disabled]="readonly"
          (click)="pick(s)"
          [attr.aria-label]="s + ' of ' + max"
          [title]="s + ' / ' + max">★</button>
      }
      @if (readonly && showValue) {
        <span class="val">{{ value > 0 ? value.toFixed(1) : '—' }}</span>
      }
    </span>
  `,
  styles: [`
    .stars { display: inline-flex; align-items: center; gap: 1px; }
    .star {
      background: none; border: 0; padding: 0 1px; cursor: pointer; line-height: 1;
      font-size: 15px; color: #41434c; transition: color .08s ease;
    }
    .star.on { color: var(--accent); }
    .stars:not(.readonly) .star:hover { color: var(--accent); filter: brightness(1.15); }
    .stars.readonly .star { cursor: default; }
    .val { margin-left: 5px; font-size: 11px; color: var(--fg-3); }
  `]
})
export class StarRatingComponent {
  @Input() value = 0;
  @Input() max = 5;
  @Input() readonly = false;
  @Input() showValue = false;
  @Output() valueChange = new EventEmitter<number>();

  protected get starList(): number[] {
    return Array.from({ length: this.max }, (_, i) => i + 1);
  }

  protected get rounded(): number {
    return Math.round(this.value);
  }

  protected pick(star: number): void {
    if (this.readonly) { return; }
    const next = this.value === star ? 0 : star;
    this.value = next;
    this.valueChange.emit(next);
  }
}
