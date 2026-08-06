import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityTreasuryEntryInput,
  ActivityTreasuryFilter,
  ActivityTreasuryFixInput,
  ActivityTreasuryPage
} from './discord-activity.types';

/**
 * The linkshell's gil: what it has, what moved, and recording more of it.
 *
 * Its own domain service (matching tod.service / auction.service / dkp.service) rather than a corner of
 * the four-domain LinkshellContentService, because this now has a real read endpoint, a lifecycle, and
 * eight write actions of its own.
 *
 * Reads go through a dedicated GET rather than riding the whole-overview payload the way revenue used
 * to. That old arrangement meant an unbounded list of every entry a linkshell had ever recorded was
 * embedded in every overview refresh, and there was no way to page or filter it server-side.
 */
@Injectable({ providedIn: 'root' })
export class TreasuryService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly busySave = signal(false);
  readonly busyEntryId = signal<number | null>(null);
  readonly busyLoad = signal(false);

  readonly page = signal<ActivityTreasuryPage | null>(null);

  /** The query the current page was loaded with, so any action can reload the same view. */
  private lastQuery: { linkshellId: number; page: number; search: string; filter: ActivityTreasuryFilter } | null = null;

  async load(
    linkshellId: number,
    page = 0,
    search = '',
    filter: ActivityTreasuryFilter = 'all'
  ): Promise<void> {
    this.busyLoad.set(true);
    this.lastQuery = { linkshellId, page, search, filter };

    try {
      const query = new URLSearchParams({ page: String(page) });
      if (search.trim()) {
        query.set('search', search.trim());
      }
      if (filter !== 'all') {
        query.set('filter', filter);
      }
      this.page.set(
        await this.http.fetchActivityJson<ActivityTreasuryPage>(
          `/api/activity/linkshells/${linkshellId}/treasury?${query.toString()}`
        )
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the treasury failed.'));
      throw error;
    } finally {
      this.busyLoad.set(false);
    }
  }

  async reload(): Promise<void> {
    if (!this.lastQuery) {
      return;
    }
    const { linkshellId, page, search, filter } = this.lastQuery;
    await this.load(linkshellId, page, search, filter);
  }

  async record(linkshellId: number, input: ActivityTreasuryEntryInput): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/treasury/entries`, input),
      input.confirm ? 'Entry recorded.' : 'Draft saved.',
      'Recording the entry failed.'
    );
  }

  async updateDraft(entryId: number, input: ActivityTreasuryEntryInput): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(`/api/activity/treasury/entries/${entryId}/update`, input),
      input.confirm ? 'Entry recorded.' : 'Draft saved.',
      'Saving the draft failed.',
      entryId
    );
  }

  async confirm(entryId: number): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(`/api/activity/treasury/entries/${entryId}/confirm`),
      'Entry recorded.',
      'Recording the entry failed.',
      entryId
    );
  }

  async discardDraft(entryId: number): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(`/api/activity/treasury/entries/${entryId}/discard`),
      'Draft discarded.',
      'Discarding the draft failed.',
      entryId
    );
  }

  /** Cancels an entry by recording its opposite. The original stays in the list. */
  async reverse(entryId: number, reason: string): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(`/api/activity/treasury/entries/${entryId}/reverse`, { reason }),
      'Entry reversed.',
      'Reversing the entry failed.',
      entryId
    );
  }

  /** One action: reverses the wrong entry and records the corrected one. */
  async fix(entryId: number, input: ActivityTreasuryFixInput): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(`/api/activity/treasury/entries/${entryId}/fix`, input),
      'Entry fixed.',
      'Fixing the entry failed.',
      entryId
    );
  }

  /** Someone counted the mule. The server works out which way the difference goes. */
  async checkGil(linkshellId: number, countedAmount: number, memo?: string | null): Promise<void> {
    await this.write(
      () =>
        this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/treasury/check-gil`, {
          countedAmount,
          transactionDate: null,
          memo: memo ?? null
        }),
      'Gil on hand checked.',
      'Checking gil on hand failed.'
    );
  }

  async setLock(linkshellId: number, lockedThrough: string | null, reason?: string | null): Promise<void> {
    await this.write(
      () =>
        this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/treasury/lock`, {
          lockedThrough,
          reason: reason ?? null
        }),
      lockedThrough ? 'Older entries locked.' : 'Entries unlocked.',
      'Changing the lock failed.'
    );
  }

  // Every write shares the same shape: flag busy, clear the banners, act, reload, report. Reloading
  // rather than patching in place keeps the summary figures honest — they are derived server-side, so
  // guessing at them here is how the old panel ended up showing three different totals.
  private async write(
    action: () => Promise<void>,
    success: string,
    failure: string,
    entryId?: number
  ): Promise<void> {
    this.busySave.set(true);
    if (entryId !== undefined) {
      this.busyEntryId.set(entryId);
    }
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await action();
      await this.reload();
      // The dashboard tile reads gil on hand out of the overview payload.
      await this.auth.refreshOverview();
      this.auth.setActionMessage(success);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, failure));
      throw error;
    } finally {
      this.busySave.set(false);
      this.busyEntryId.set(null);
    }
  }
}
