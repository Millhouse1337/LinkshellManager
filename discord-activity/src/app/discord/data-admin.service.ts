import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient, ActivityRequestError } from './activity-http.client';
import { AuthService } from './auth.service';
import type {
  DataAdminDeletePreview,
  DataAdminDetails,
  DataAdminForm,
  DataAdminList,
  DataAdminListQuery,
  DataAdminSaved,
  DataAdminSchema
} from './data-admin.types';

const EMPTY_QUERY: DataAdminListQuery = { search: null, sort: null, descending: false, page: 1, filters: {} };

// Data Admin (website only, super admins) over /api/data-admin (DataAdminApiController). Holds no
// rows itself: each view asks for what it shows. It remembers each table's list query (search,
// sort, page, filters), so going from a list into a row and back lands on the same page of it.
@Injectable({ providedIn: 'root' })
export class DataAdminService {
  private readonly http = inject(ActivityHttpClient);
  private readonly auth = inject(AuthService);
  private readonly listQueries = new Map<string, DataAdminListQuery>();

  // The diagram never changes while the app runs (it is the EF model), so it is fetched once.
  readonly schema = signal<DataAdminSchema | null>(null);

  async loadSchema(): Promise<void> {
    if (!this.schema()) {
      this.schema.set(await this.http.fetchActivityJson<DataAdminSchema>('/api/data-admin/schema'));
    }
  }

  listQuery(slug: string): DataAdminListQuery {
    return this.listQueries.get(slug) ?? EMPTY_QUERY;
  }

  setListQuery(slug: string, query: DataAdminListQuery): void {
    this.listQueries.set(slug, query);
  }

  // A related-rows link: the dependent's list narrowed to this row (filterRoute is { "f.Col": key }).
  openFiltered(slug: string, filterRoute: Record<string, string>): void {
    const filters: Record<string, string> = {};
    for (const [key, value] of Object.entries(filterRoute)) {
      if (key.startsWith('f.')) {
        filters[key.slice(2)] = value;
      }
    }
    this.listQueries.set(slug, { ...EMPTY_QUERY, filters });
  }

  list(slug: string, query: DataAdminListQuery): Promise<DataAdminList> {
    const params = new URLSearchParams();
    if (query.search) params.set('q', query.search);
    if (query.sort) params.set('sort', query.sort);
    if (query.descending) params.set('desc', 'true');
    if (query.page > 1) params.set('page', String(query.page));
    for (const [column, value] of Object.entries(query.filters)) {
      params.set(`f.${column}`, value);
    }
    const qs = params.toString();
    return this.http.fetchActivityJson<DataAdminList>(`${this.tableUrl(slug)}${qs ? `?${qs}` : ''}`);
  }

  details(slug: string, id: string): Promise<DataAdminDetails> {
    return this.http.fetchActivityJson<DataAdminDetails>(this.rowUrl(slug, id));
  }

  // id null = the empty create form.
  form(slug: string, id: string | null): Promise<DataAdminForm> {
    return this.http.fetchActivityJson<DataAdminForm>(id === null ? `${this.tableUrl(slug)}/form` : `${this.rowUrl(slug, id)}/form`);
  }

  // Saved, or refused with the form rebuilt around the values and each field's error (422).
  async save(slug: string, id: string | null, values: Record<string, string>):
      Promise<{ saved: DataAdminSaved } | { refused: DataAdminForm }> {
    this.auth.clearActionState();
    try {
      const saved = await this.http.postActivityJson<DataAdminSaved>(id === null ? this.tableUrl(slug) : this.rowUrl(slug, id), { values });
      this.auth.setActionMessage(saved.message);
      return { saved };
    } catch (error) {
      if (error instanceof ActivityRequestError && error.status === 422) {
        this.auth.setActionError(error.message);
        return { refused: (error.body as { form: DataAdminForm }).form };
      }
      throw error;
    }
  }

  deletePreview(slug: string, id: string): Promise<DataAdminDeletePreview> {
    return this.http.fetchActivityJson<DataAdminDeletePreview>(`${this.rowUrl(slug, id)}/delete-preview`);
  }

  async delete(slug: string, id: string): Promise<void> {
    this.auth.clearActionState();
    const result = await this.http.postActivityJson<DataAdminSaved>(`${this.rowUrl(slug, id)}/delete`);
    this.auth.setActionMessage(result.message);
  }

  private tableUrl(slug: string): string {
    return `/api/data-admin/tables/${encodeURIComponent(slug)}`;
  }

  private rowUrl(slug: string, id: string): string {
    return `${this.tableUrl(slug)}/${encodeURIComponent(id)}`;
  }
}
