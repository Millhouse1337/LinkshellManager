import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityChartBoard,
  ActivityChartCreditInput,
  ActivityChartKeyItemInput,
  ActivityChartPopItemInput,
  ActivityChartWishlistInput,
  ChartWishlistStatus
} from './discord-activity.types';

/**
 * Charts: the pop items a linkshell is sitting on, and who farmed each one.
 *
 * One service for every board — Sky, Sea, and later Dynamis / Limbus — because they are the same
 * feature with a different cast. The board is a parameter, never a branch.
 *
 * The payload carries the boss cards AND the Farming Credit Ledger together. They are two views of
 * the same rows — the cards show a per-item farmer count, the ledger shows a per-boss fraction — so
 * fetching them separately would let the two halves of one screen disagree.
 */
@Injectable({ providedIn: 'root' })
export class ChartBoardService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  /**
   * Boards held per key, so switching Sky ⇄ Sea shows the board you already loaded instead of
   * blanking while it refetches — and so one board's reload never clears the other.
   */
  readonly boards = signal<Record<string, ActivityChartBoard>>({});

  readonly busyLoad = signal(false);
  readonly busySave = signal(false);
  readonly busyItemId = signal<number | null>(null);

  /**
   * Separate from busyItemId, deliberately. A wishlist request and a pop item are rows in different
   * tables and can share a numeric id, so reusing one signal would grey out an unrelated holdings
   * row every time somebody withdrew a request.
   */
  readonly busyRequestId = signal<number | null>(null);

  /** What the last load was for, so any action can reload the same board. */
  private lastQuery: { linkshellId: number; board: string } | null = null;

  board(board: string): ActivityChartBoard | null {
    return this.boards()[board] ?? null;
  }

  async load(linkshellId: number, board: string): Promise<void> {
    this.busyLoad.set(true);
    this.lastQuery = { linkshellId, board };

    try {
      const payload = await this.http.fetchActivityJson<ActivityChartBoard>(
        `/api/activity/linkshells/${linkshellId}/charts/${board}`
      );
      this.boards.update(current => ({ ...current, [board]: payload }));
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the chart failed.'));
      throw error;
    } finally {
      this.busyLoad.set(false);
    }
  }

  async reload(): Promise<void> {
    if (!this.lastQuery) {
      return;
    }
    const { linkshellId, board } = this.lastQuery;
    await this.load(linkshellId, board);
  }

  async addItem(linkshellId: number, board: string, input: ActivityChartPopItemInput): Promise<void> {
    const drop = input.kind === 'Drop';
    await this.write(
      () => this.http.postActivityAction(
        `/api/activity/linkshells/${linkshellId}/charts/${board}/items`, input),
      drop ? 'Drop item added.' : 'Pop item added.',
      drop ? 'Adding the drop item failed.' : 'Adding the pop item failed.'
    );
  }

  async updateItem(itemId: number, input: ActivityChartPopItemInput): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(`/api/activity/charts/items/${itemId}/update`, input),
      'Pop item saved.',
      'Saving the pop item failed.',
      itemId
    );
  }

  async deleteItem(itemId: number): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(`/api/activity/charts/items/${itemId}/delete`),
      'Pop item removed.',
      'Removing the pop item failed.',
      itemId
    );
  }

  /**
   * Replaces the COMPLETE farmer list for one item — a name left out is a removal. Sending the whole
   * set rather than add/remove deltas is what makes duplicates impossible.
   */
  async setCredits(itemId: number, credits: ActivityChartCreditInput[]): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(`/api/activity/charts/items/${itemId}/credits`, { credits }),
      'Farming credit saved.',
      'Saving the farming credit failed.',
      itemId
    );
  }

  // ---- the wishlist ---------------------------------------------------------
  //
  // The one part of Charts a member without CanManageCharts may write. Nothing here decides who may
  // do what: the server stamps canWithdraw on every request row, and these just call.

  async addWishlistRequest(
    linkshellId: number, board: string, input: ActivityChartWishlistInput): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(
        `/api/activity/linkshells/${linkshellId}/charts/${board}/wishlist`, input),
      'Item request submitted.',
      'Submitting the item request failed.'
    );
  }

  /** Withdrawing DELETES the request. An officer removing somebody else's is the same call. */
  async withdrawWishlistRequest(requestId: number): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(`/api/activity/charts/wishlist/${requestId}/withdraw`),
      'Item request removed.',
      'Removing the item request failed.',
      undefined,
      requestId
    );
  }

  async setWishlistStatus(requestId: number, status: ChartWishlistStatus): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(
        `/api/activity/charts/wishlist/${requestId}/status`, { status }),
      status === 'Fulfilled' ? 'Marked fulfilled.' : 'Put back on the list.',
      'Saving the request failed.',
      undefined,
      requestId
    );
  }

  /**
   * Set-wise: the COMPLETE ordered id list for the board. An id from elsewhere refuses the whole
   * thing rather than reordering the rest, so two officers reordering at once cannot interleave into
   * an order neither of them chose.
   */
  async reorderWishlist(linkshellId: number, board: string, orderedIds: number[]): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(
        `/api/activity/linkshells/${linkshellId}/charts/${board}/wishlist/order`, { orderedIds }),
      'Request order saved.',
      'Saving the request order failed.'
    );
  }

  // ---- key items ------------------------------------------------------------

  /** `has: false` deletes the row - presence is the fact. Idempotent either way. */
  async setKeyItem(
    linkshellId: number, board: string, input: ActivityChartKeyItemInput): Promise<void> {
    await this.write(
      () => this.http.postActivityAction(
        `/api/activity/linkshells/${linkshellId}/charts/${board}/keyitems`, input),
      input.has ? 'Key item ticked off.' : 'Key item cleared.',
      'Saving the key item failed.'
    );
  }

  // Every write shares the same shape: flag busy, clear the banners, act, reload, report.
  //
  // Reloading rather than patching in place keeps every derived number honest — the per-boss totals,
  // the ledger's cell states and the "49 / 63  78%" row total are all computed server-side, and a
  // client that guessed at them would be a second implementation waiting to disagree.
  //
  // Deliberately no auth.refreshOverview() here, unlike TreasuryService: nothing about the Charts
  // boards feeds the overview payload, so that call would be a wasted round trip on every edit.
  private async write(
    action: () => Promise<void>,
    success: string,
    failure: string,
    itemId?: number,
    requestId?: number
  ): Promise<void> {
    this.busySave.set(true);
    if (itemId !== undefined) {
      this.busyItemId.set(itemId);
    }
    if (requestId !== undefined) {
      this.busyRequestId.set(requestId);
    }
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await action();
      await this.reload();
      this.auth.setActionMessage(success);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, failure));
      throw error;
    } finally {
      this.busySave.set(false);
      this.busyItemId.set(null);
      this.busyRequestId.set(null);
    }
  }
}
