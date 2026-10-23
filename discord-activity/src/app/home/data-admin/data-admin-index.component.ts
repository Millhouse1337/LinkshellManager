import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';

import { formatError } from '../../discord/discord-activity.helpers';
import { DataAdminService } from '../../discord/data-admin.service';
import { DataAdminDiagramComponent } from './data-admin-diagram.component';

// Data Admin's landing: the schema diagram, then a plain list of every table with a records page
// (quicker when you know the name, and there on narrow screens where the diagram's panel isn't).
@Component({
  selector: 'app-data-admin-index',
  imports: [DecimalPipe, DataAdminDiagramComponent],
  template: `
    <div class="section-head">
      <div>
        <div class="section-label">Website only · Super admin</div>
        <div class="section-title">Data Admin</div>
        @if (dataAdmin.schema(); as schema) {
          <div class="section-sub">
            <span class="tag">{{ schema.databaseName }}</span>
            {{ schema.tableCount | number }} tables · {{ schema.columnCount | number }} columns ·
            {{ schema.relationshipCount | number }} relationships
          </div>
        }
      </div>
    </div>

    @if (error(); as message) {
      <div class="card"><div class="card-body muted">{{ message }}</div></div>
    } @else if (dataAdmin.schema(); as schema) {
      <app-data-admin-diagram [schema]="schema" (navigate)="navigate.emit($event)" />

      <div class="card" style="margin-top:14px">
        <div class="card-head">
          <div class="card-title">Tables</div>
          <input type="search" placeholder="Find a table" style="max-width:260px"
                 [value]="filter()" (input)="filter.set($any($event.target).value)" />
        </div>
        <div class="card-body">
          <div class="table-list">
            @for (table of tables(); track table.slug) {
              <button type="button" class="table-item" [title]="table.label + ' (' + table.name + ')'"
                      (click)="navigate.emit([table.slug])">
                <span class="table-item__label">{{ table.label }}</span>
                <span class="table-item__name">{{ table.name }}</span>
              </button>
            } @empty {
              <span class="muted">No table matches.</span>
            }
          </div>
        </div>
      </div>
    } @else {
      <div class="card"><div class="card-body muted">Loading the schema…</div></div>
    }
  `,
  // The list scrolls inside its card rather than growing the page, and each entry keeps its
  // label and table name on two lines cut with "…": side by side on one line, long names ran
  // out of their button and over the next one.
  styles: [`
    .table-list {
      display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 6px;
      max-height: 340px; overflow-y: auto; padding-right: 4px;
    }
    .table-item {
      display: flex; flex-direction: column; align-items: flex-start; gap: 2px; min-width: 0;
      padding: 7px 10px; border: 1px solid var(--border); border-radius: var(--r-md);
      background: var(--surface); color: var(--fg); font-family: inherit; text-align: left; cursor: pointer;
    }
    .table-item:hover { border-color: var(--border-hot); background: var(--surface-2); }
    .table-item:focus-visible { outline: none; box-shadow: 0 0 0 3px var(--accent-glow); }
    .table-item span { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .table-item__label { font-size: 13px; font-weight: 600; }
    .table-item__name { font-size: 11px; color: var(--fg-3); font-family: var(--font-mono); }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DataAdminIndexComponent {
  readonly navigate = output<string[]>();

  protected readonly dataAdmin = inject(DataAdminService);
  protected readonly filter = signal('');
  protected readonly error = signal<string | null>(null);

  // Tables with a records page (the Identity tables have none), alphabetical, narrowed by the box.
  protected readonly tables = computed(() => {
    const term = this.filter().trim().toLowerCase();
    return (this.dataAdmin.schema()?.diagram.tables ?? [])
      .filter((table): table is typeof table & { slug: string } => !!table.slug)
      .filter(table => !term || table.label.toLowerCase().includes(term) || table.name.toLowerCase().includes(term))
      .sort((a, b) => a.label.localeCompare(b.label));
  });

  constructor() {
    this.dataAdmin.loadSchema().catch(error => this.error.set(formatError(error)));
  }
}
