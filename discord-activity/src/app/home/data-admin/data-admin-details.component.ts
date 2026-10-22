import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, untracked } from '@angular/core';

import { formatError } from '../../discord/discord-activity.helpers';
import { DataAdminService } from '../../discord/data-admin.service';
import type { DataAdminDetails, DataAdminRelated } from '../../discord/data-admin.types';

// One row: every visible column (a foreign key links to the row it points at), then every table
// with rows pointing here, counted, each linking to its list narrowed to this row.
@Component({
  selector: 'app-data-admin-details',
  imports: [DecimalPipe],
  template: `
    <div class="section-head">
      <div>
        <div class="section-label">
          <a href="" (click)="$event.preventDefault(); navigate.emit([])">Data Admin</a> ›
          <a href="" (click)="$event.preventDefault(); navigate.emit([slug()])">{{ row()?.displayName ?? slug() }}</a>
        </div>
        <div class="section-title">{{ row()?.label ?? '#' + id() }}</div>
        @if (row(); as r) {
          <div class="section-sub">{{ r.displayName }} #{{ r.key }} · <span class="muted">{{ r.tableName }}</span></div>
        }
      </div>
      <div class="section-head-right">
        @if (row()?.canEdit) {
          <button type="button" class="btn primary sm" (click)="navigate.emit([slug(), id(), 'edit'])">Edit</button>
        }
        @if (row()?.canDelete) {
          <button type="button" class="btn danger-outline sm" (click)="navigate.emit([slug(), id(), 'delete'])">Delete</button>
        }
        <button type="button" class="btn ghost sm" (click)="navigate.emit([slug()])">Back to list</button>
      </div>
    </div>

    @if (error(); as message) {
      <div class="card"><div class="card-body muted">{{ message }}</div></div>
    } @else if (row(); as r) {
      <div style="display:grid;gap:14px">
        <div class="card">
          <div class="card-head"><div class="card-title">Fields</div></div>
          <div class="table-wrap">
            <table class="data-table">
              <tbody>
                @for (field of r.fields; track field.name) {
                  <tr>
                    <td style="width:240px;white-space:nowrap;vertical-align:top;font-weight:500">{{ field.displayName }}</td>
                    <td style="vertical-align:top" [style.white-space]="field.isLongText ? 'pre-wrap' : null">
                      @if (field.linkSlug && field.linkKey) {
                        <a href="" (click)="$event.preventDefault(); navigate.emit([field.linkSlug, field.linkKey])">{{ field.text }}</a>
                      } @else if (field.isEmpty) {
                        <span class="muted">{{ field.text }}</span>
                      } @else {
                        {{ field.text }}
                      }
                      @if (field.note) {
                        <span class="muted" style="font-size:11px;margin-left:8px">{{ field.note }}</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </div>

        @if (r.related.length > 0) {
          <div class="card">
            <div class="card-head">
              <div class="card-title">Related rows</div>
              <span class="tag">{{ r.related.length }} table{{ r.related.length === 1 ? '' : 's' }}</span>
            </div>
            <div class="table-wrap">
              <table class="data-table">
                <thead>
                  <tr><th>Table</th><th>Via</th><th class="num">Rows</th><th>When this row is deleted</th></tr>
                </thead>
                <tbody>
                  @for (related of r.related; track related.slug + related.foreignKeyColumn) {
                    <tr>
                      <td style="font-weight:500">
                        @if (!related.isHidden && related.count > 0) {
                          <a href="" (click)="$event.preventDefault(); openRelated(related)">{{ related.displayName }}</a>
                        } @else {
                          {{ related.displayName }}
                        }
                      </td>
                      <td class="muted">{{ related.foreignKeyDisplayName }}</td>
                      <td class="num">{{ related.count | number }}</td>
                      <td class="muted">{{ related.onDelete }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </div>
        }
      </div>
    } @else {
      <div class="card"><div class="card-body muted">Loading…</div></div>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DataAdminDetailsComponent {
  readonly slug = input.required<string>();
  readonly id = input.required<string>();
  readonly navigate = output<string[]>();

  private readonly dataAdmin = inject(DataAdminService);
  protected readonly row = signal<DataAdminDetails | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const slug = this.slug();
      const id = this.id();
      untracked(() => {
        this.row.set(null);
        this.error.set(null);
        this.dataAdmin.details(slug, id)
          .then(row => this.row.set(row))
          .catch(error => this.error.set(formatError(error)));
      });
    });
  }

  protected openRelated(related: DataAdminRelated): void {
    this.dataAdmin.openFiltered(related.slug, related.filterRoute);
    this.navigate.emit([related.slug]);
  }
}
