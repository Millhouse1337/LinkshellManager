import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';

import { formatError } from '../../discord/discord-activity.helpers';
import { DataAdminService } from '../../discord/data-admin.service';
import type { DataAdminList, DataAdminListQuery } from '../../discord/data-admin.types';

// One table's rows: search, sort by any column, filter chips, paging, and the per-row actions the
// table's policy allows. The query is remembered per table (DataAdminService), so a row's page
// and back lands on the same search, sort and page.
@Component({
  selector: 'app-data-admin-list',
  imports: [DecimalPipe],
  templateUrl: './data-admin-list.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DataAdminListComponent {
  readonly slug = input.required<string>();
  readonly navigate = output<string[]>();

  protected readonly dataAdmin = inject(DataAdminService);
  protected readonly list = signal<DataAdminList | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly search = signal('');
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private loadId = 0;

  constructor() {
    effect(() => {
      const slug = this.slug();
      untracked(() => {
        this.list.set(null);
        this.search.set(this.dataAdmin.listQuery(slug).search ?? '');
        void this.load(this.dataAdmin.listQuery(slug));
      });
    });
    inject(DestroyRef).onDestroy(() => {
      if (this.searchTimer) clearTimeout(this.searchTimer);
    });
  }

  protected onSearch(value: string): void {
    this.search.set(value);
    if (this.searchTimer) clearTimeout(this.searchTimer);
    // Typing waits for a pause, so every keystroke isn't a query.
    this.searchTimer = setTimeout(() => void this.apply({ search: value.trim() || null, page: 1 }), 300);
  }

  protected sortBy(column: string): void {
    const list = this.list();
    const descending = list?.sortColumn === column ? !list.descending : false;
    void this.apply({ sort: column, descending, page: 1 });
  }

  protected goToPage(page: number): void {
    void this.apply({ page });
  }

  protected removeFilter(column: string): void {
    const filters = { ...this.current().filters };
    delete filters[column];
    void this.apply({ filters, page: 1 });
  }

  private current(): DataAdminListQuery {
    return this.dataAdmin.listQuery(this.slug());
  }

  private apply(change: Partial<DataAdminListQuery>): Promise<void> {
    const query = { ...this.current(), ...change };
    this.dataAdmin.setListQuery(this.slug(), query);
    return this.load(query);
  }

  // Only the newest request lands, so a slow page can't overwrite a newer search.
  private async load(query: DataAdminListQuery): Promise<void> {
    const id = ++this.loadId;
    this.loading.set(true);
    try {
      const list = await this.dataAdmin.list(this.slug(), query);
      if (id === this.loadId) {
        this.list.set(list);
        this.error.set(null);
      }
    } catch (error) {
      if (id === this.loadId) this.error.set(formatError(error));
    } finally {
      if (id === this.loadId) this.loading.set(false);
    }
  }
}
