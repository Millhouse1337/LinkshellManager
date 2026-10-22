import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';

import { AuthService } from '../../discord/auth.service';
import { formatActionError, formatError } from '../../discord/discord-activity.helpers';
import { DataAdminService } from '../../discord/data-admin.service';
import type { DataAdminDeletePreview } from '../../discord/data-admin.types';
import { DataAdminImpactNodeComponent } from './data-admin-impact-node.component';

// The delete confirmation: what else goes with the row, what is only unlinked, and what blocks the
// delete (then the button stays off). The server recomputes all of this when the delete is sent and
// refuses one the database would refuse, so a preview left open can't slip past it.
@Component({
  selector: 'app-data-admin-delete',
  imports: [DecimalPipe, DataAdminImpactNodeComponent],
  template: `
    <div class="section-head">
      <div>
        <div class="section-label">
          <a href="" (click)="$event.preventDefault(); navigate.emit([])">Data Admin</a> ›
          <a href="" (click)="$event.preventDefault(); navigate.emit([slug()])">{{ preview()?.displayName ?? slug() }}</a>
        </div>
        <div class="section-title">Delete {{ preview()?.label ?? '#' + id() }}?</div>
      </div>
    </div>

    @if (error(); as message) {
      <div class="card"><div class="card-body muted">{{ message }}</div></div>
    } @else if (preview(); as p) {
      <div class="card" style="max-width:860px;border-color:var(--danger)">
        <div class="card-body" style="display:flex;flex-direction:column;gap:14px">
          <p style="margin:0">{{ summary() }}</p>
          @if (p.discordSideEffect) {
            <p style="margin:0;color:var(--danger)">{{ p.discordSideEffect }}</p>
          }
          @if (p.children.length > 0) {
            <div>
              <div style="font-weight:500;margin-bottom:6px">What happens to the rows that point here</div>
              <ul style="margin:0;padding-left:18px">
                @for (child of p.children; track $index) {
                  <app-data-admin-impact-node [node]="child" />
                }
              </ul>
              @if (p.isTruncated) {
                <p class="muted" style="margin:6px 0 0;font-size:11px">
                  Some branches were not fully expanded (too many rows or too deep). The database still deletes everything
                  beneath them, and if a blocking row hides there it refuses the whole delete, which is reported after you confirm.
                </p>
              }
            </div>
          }
          @if (p.totals.length > 0) {
            <p class="muted" style="margin:0;font-size:12px">
              Rows deleted per table:
              @for (total of p.totals; track total.displayName; let last = $last) {
                {{ total.displayName }} {{ total.rows | number }}{{ last ? '' : '; ' }}
              }
            </p>
          }
          <div style="display:flex;gap:8px">
            <button type="button" class="btn danger-outline sm" [disabled]="p.isBlocked || deleting()" (click)="confirmDelete(p)">
              {{ deleting() ? 'Deleting…' : 'Delete ' + p.displayName }}
            </button>
            <button type="button" class="btn ghost sm" [disabled]="deleting()" (click)="navigate.emit([slug(), id()])">Cancel</button>
          </div>
        </div>
      </div>
    } @else {
      <div class="card"><div class="card-body muted">Working out what this delete would take with it…</div></div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DataAdminDeleteComponent {
  readonly slug = input.required<string>();
  readonly id = input.required<string>();
  readonly navigate = output<string[]>();

  private readonly dataAdmin = inject(DataAdminService);
  private readonly auth = inject(AuthService);
  protected readonly preview = signal<DataAdminDeletePreview | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly deleting = signal(false);

  protected readonly summary = computed(() => {
    const p = this.preview();
    if (!p) return '';
    if (p.isBlocked) {
      return 'Other rows still point at this row, and the database will not delete it while they do. '
        + 'The blocked rows are marked below; delete or re-point them first.';
    }
    if (p.deletedRows > 0) {
      const tables = p.totals.length;
      return `Deleting ${p.label} also deletes ${p.deletedRows.toLocaleString()} row${p.deletedRows === 1 ? '' : 's'} `
        + `in ${tables} other table${tables === 1 ? '' : 's'}. This cannot be undone.`;
    }
    return `This removes ${p.label}. No other table loses rows. This cannot be undone.`;
  });

  constructor() {
    effect(() => {
      const slug = this.slug();
      const id = this.id();
      untracked(() => {
        this.preview.set(null);
        this.error.set(null);
        this.dataAdmin.deletePreview(slug, id)
          .then(preview => this.preview.set(preview))
          .catch(error => this.error.set(formatError(error)));
      });
    });
  }

  // A second, browser-native confirmation like the Razor page's, on top of this page: it's a
  // website-only tool, so window.confirm is fine here (the Discord iframe would block it).
  protected async confirmDelete(preview: DataAdminDeletePreview): Promise<void> {
    if (!window.confirm(`Delete ${preview.label}? ${this.summary()}`)) return;
    this.deleting.set(true);
    try {
      await this.dataAdmin.delete(this.slug(), this.id());
      this.navigate.emit([this.slug()]);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting failed.'));
    } finally {
      this.deleting.set(false);
    }
  }
}
