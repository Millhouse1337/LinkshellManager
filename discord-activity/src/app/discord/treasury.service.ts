import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { mutateThenRefresh } from './mutate-then-refresh';
import type {
  ActivitySettleOwedResult,
  ActivityTreasuryEntryInput,
  ActivityTreasuryFilter,
  ActivityTreasuryFixInput,
  ActivityTreasuryPage,
  ActivityTreasurySettlePick
} from './discord-activity.types';
import { ResponseOrder } from './refresh-order';

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
  private readonly order = new ResponseOrder();

  async load(
    linkshellId: number,
    page = 0,
    search = '',
    filter: ActivityTreasuryFilter = 'all'
  ): Promise<void> {
    this.busyLoad.set(true);
    this.lastQuery = { linkshellId, page, search, filter };
    // Only the reply to the newest query is shown, so a slow refresh of the old page or search
    // cannot land on top of the one the officer just asked for (see refresh-order.ts).
    const key = JSON.stringify(this.lastQuery);
    const ticket = this.order.begin(key);

    try {
      const query = new URLSearchParams({ page: String(page) });
      if (search.trim()) {
        query.set('search', search.trim());
      }
      if (filter !== 'all') {
        query.set('filter', filter);
      }
      const result = await this.http.fetchActivityJson<ActivityTreasuryPage>(
        `/api/activity/linkshells/${linkshellId}/treasury?${query.toString()}`
      );
      if (this.order.accept(ticket, key)) this.page.set(result);
      this.auth.clearLoadError('treasury');
    } catch (error) {
      // Polled by the refresh timer: its own banner, never the action one (AuthService.loadError).
      this.auth.reportLoadError('treasury', error, 'Loading the treasury failed.');
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

  /**
   * Pays the ticked members off the "who we owe" list, in full, in one save.
   *
   * Each one becomes an ordinary "We paid a member what we owed" transaction — the same movement the
   * form's settle option records, reached without retyping the names and figures.
   *
   * The banner wording comes from the server rather than from a string here, because only the server
   * knows which ticks became payments: a row whose figure moved while the panel was open is skipped
   * rather than paid, and the officer has to be told which one and why.
   */
  async settleOwed(
    linkshellId: number,
    picks: ActivityTreasurySettlePick[],
    holderCharacterName: string
  ): Promise<void> {
    await this.settle(linkshellId, picks, holderCharacterName, 'settle');
  }

  /**
   * The mirror on the other half of the sheet: ticking whoever has now paid the LINKSHELL.
   *
   * Same endpoint shape, same result shape, same skip rules — one method with the path swapped
   * rather than a second copy, because the two are the same operation with the direction flipped and
   * a copy is how they would drift.
   */
  async settleOwedToUs(
    linkshellId: number,
    picks: ActivityTreasurySettlePick[],
    holderCharacterName: string
  ): Promise<void> {
    await this.settle(linkshellId, picks, holderCharacterName, 'settle-owed-to-us');
  }

  private async settle(
    linkshellId: number,
    picks: ActivityTreasurySettlePick[],
    // Whose mule the gil leaves from, or lands on. ONE for the whole run: a payout is one person
    // sitting at one mule handing gil out, so asking per ticked name would ask eight times. The
    // server refuses the batch without it.
    holderCharacterName: string,
    path: 'settle' | 'settle-owed-to-us'
  ): Promise<void> {
    let outcome: ActivitySettleOwedResult | null = null;
    await this.write(
      async () => {
        outcome = await this.http.postActivityJson<ActivitySettleOwedResult>(
          `/api/activity/linkshells/${linkshellId}/treasury/${path}`,
          { picks, holderCharacterName }
        );
      },
      'Payment recorded.',
      'Recording the payment failed.'
    );

    // After write(), so this replaces the placeholder above rather than being overwritten by it.
    const result = outcome as ActivitySettleOwedResult | null;
    if (!result) {
      return;
    }
    if (result.success) {
      this.auth.setActionMessage(result.message);
    } else {
      // Every tick was skipped. Nothing failed, but nothing was recorded either, so it does not
      // belong in the banner that means "done".
      this.auth.setActionMessage(null);
      this.auth.setActionError(result.message);
    }
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

    try {
      // Saved is saved: a reload that fails afterwards must not read as a failed payment, or a
      // second press records the gil twice (mutate-then-refresh.ts).
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: action,
        refresh: async () => {
          await this.reload();
          // The dashboard tile reads gil on hand out of the overview payload.
          await this.auth.refreshOverview();
        },
        success,
        failure
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busySave.set(false);
      this.busyEntryId.set(null);
    }
  }
}
