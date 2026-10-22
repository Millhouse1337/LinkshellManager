import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, output, signal } from '@angular/core';

import { formatError } from '../../discord/discord-activity.helpers';
import { DataAdminService } from '../../discord/data-admin.service';

// Data Admin's landing: the database at a glance and every table that has a records page.
@Component({
  selector: 'app-data-admin-index',
  imports: [DecimalPipe],
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
    } @else if (dataAdmin.schema()) {
      <div class="card">
        <div class="card-head">
          <div class="card-title">Tables</div>
          <input type="search" placeholder="Find a table" style="max-width:260px"
                 [value]="filter()" (input)="filter.set($any($event.target).value)" />
        </div>
        <div class="card-body" style="display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:6px">
          @for (table of tables(); track table.slug) {
            <button type="button" class="btn ghost sm" style="justify-content:flex-start;text-align:left"
                    (click)="navigate.emit([table.slug])">
              {{ table.label }} <span class="muted" style="margin-left:6px">{{ table.name }}</span>
            </button>
          } @empty {
            <span class="muted">No table matches.</span>
          }
        </div>
      </div>
    } @else {
      <div class="card"><div class="card-body muted">Loading the schema…</div></div>
    }
  `,
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
