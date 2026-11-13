import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { datetimeLocalToUtcIso } from './discord-activity.helpers';
import { mutateThenRefresh } from './mutate-then-refresh';
import type {
  ActivityAuction,
  ActivityAuctionBid,
  ActivityAuctionHistory,
  ActivityCreateAuctionInput
} from './discord-activity.types';
import { ResponseOrder } from './refresh-order';

@Injectable({ providedIn: 'root' })
export class AuctionService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly auctions = signal<ActivityAuction[]>([]);
  readonly auctionsBusy = signal(false);
  readonly auctionHistory = signal<ActivityAuctionHistory[]>([]);
  readonly auctionHistoryBusy = signal(false);
  readonly auctionBids = signal<Record<number, ActivityAuctionBid[]>>({});
  readonly busyAuctionId = signal<number | null>(null);
  readonly busyAuctionItemId = signal<number | null>(null);

  // The auction list is loaded by several panels, the refresh timer and every write; only the
  // newest reply for the current linkshell is shown (see refresh-order.ts).
  private readonly auctionsOrder = new ResponseOrder();

  async loadAuctions(linkshellId?: number | null): Promise<void> {
    this.auctionsBusy.set(true);
    const key = linkshellId ?? 0;
    const ticket = this.auctionsOrder.begin(key);

    try {
      const accessToken = this.auth.currentAccessToken();
      const query = new URLSearchParams();
      if (linkshellId) {
        query.set('linkshellId', String(linkshellId));
      }

      const path =
        query.size > 0 ? `/api/activity/auctions?${query.toString()}` : '/api/activity/auctions';
      const auctions = await this.http.fetchActivityJson<ActivityAuction[]>(path, accessToken);
      if (this.auctionsOrder.accept(ticket, key)) this.auctions.set(auctions);
      this.auth.clearLoadError('auctions');
    } catch (error) {
      // A failed OLD request must not blank a list a newer request already filled.
      if (this.auctionsOrder.accept(ticket, key)) this.auctions.set([]);
      // Polled by the refresh timer: its own banner, never the action one (AuthService.loadError).
      this.auth.reportLoadError('auctions', error, 'Loading auctions failed.');
    } finally {
      this.auctionsBusy.set(false);
    }
  }

  async loadAuctionHistory(linkshellId?: number | null): Promise<void> {
    this.auctionHistoryBusy.set(true);

    try {
      const accessToken = this.auth.currentAccessToken();
      const query = new URLSearchParams();
      if (linkshellId) {
        query.set('linkshellId', String(linkshellId));
      }

      const path =
        query.size > 0
          ? `/api/activity/auction-history?${query.toString()}`
          : '/api/activity/auction-history';
      this.auctionHistory.set(
        await this.http.fetchActivityJson<ActivityAuctionHistory[]>(path, accessToken)
      );
      this.auth.clearLoadError('auction-history');
    } catch (error) {
      this.auctionHistory.set([]);
      this.auth.reportLoadError('auction-history', error, 'Loading auction history failed.');
    } finally {
      this.auctionHistoryBusy.set(false);
    }
  }

  async loadAuctionItemBids(itemId: number): Promise<void> {
    if (itemId <= 0) {
      return;
    }

    try {
      const accessToken = this.auth.currentAccessToken();
      const bids = await this.http.fetchActivityJson<ActivityAuctionBid[]>(
        `/api/activity/auction-items/${itemId}/bids`,
        accessToken
      );
      this.auctionBids.update(current => ({ ...current, [itemId]: bids }));
      this.auth.clearLoadError('auction-bids');
    } catch (error) {
      this.auth.reportLoadError('auction-bids', error, 'Loading auction bids failed.');
    }
  }

  clearAuctionState(): void {
    this.auctions.set([]);
    this.auctionHistory.set([]);
    this.auctionBids.set({});
  }

  async createAuction(input: ActivityCreateAuctionInput): Promise<void> {
    this.busyAuctionId.set(input.linkshellId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction('/api/activity/auctions', {
          linkshellId: input.linkshellId,
          title: input.title,
          startTimeLocal: datetimeLocalToUtcIso(input.startTimeLocal),
          endTimeLocal: datetimeLocalToUtcIso(input.endTimeLocal),
          items: input.items,
          // Null = the linkshell's default pool.
          dkpPoolId: input.dkpPoolId ?? null
        }),
        refresh: async () => {
          await this.auth.refreshOverview();
          await this.loadAuctions(input.linkshellId);
          await this.loadAuctionHistory(input.linkshellId);
        },
        success: 'Auction created.',
        failure: 'Creating the auction failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async updateAuction(auctionId: number, input: ActivityCreateAuctionInput): Promise<void> {
    this.busyAuctionId.set(auctionId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/auctions/${auctionId}/update`, {
          linkshellId: input.linkshellId,
          title: input.title,
          startTimeLocal: datetimeLocalToUtcIso(input.startTimeLocal),
          endTimeLocal: datetimeLocalToUtcIso(input.endTimeLocal),
          items: input.items,
          // The form fills this with the auction's own pool, so an edit never moves the auction to
          // another pool unless the officer picked one. (The server also reads a missing pool as
          // "keep the current one".)
          dkpPoolId: input.dkpPoolId ?? null
        }),
        refresh: async () => {
          await this.auth.refreshOverview();
          await this.loadAuctions(input.linkshellId);
        },
        success: 'Auction updated.',
        failure: 'Updating the auction failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async startAuction(auctionId: number, linkshellId: number): Promise<void> {
    this.busyAuctionId.set(auctionId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/auctions/${auctionId}/start`),
        refresh: () => this.loadAuctions(linkshellId),
        success: 'Auction started.',
        failure: 'Starting the auction failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async placeAuctionBid(itemId: number, bidAmount: number, linkshellId: number): Promise<void> {
    this.busyAuctionItemId.set(itemId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/auction-items/${itemId}/bid`, { bidAmount }),
        refresh: async () => {
          await this.loadAuctions(linkshellId);
          await this.loadAuctionItemBids(itemId);
          await this.auth.refreshOverview();
        },
        success: 'Bid submitted.',
        failure: 'Submitting the bid failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyAuctionItemId.set(null);
    }
  }

  // Stops bidding now without archiving the auction. The auction stays on the
  // active board with status Ended until the creator runs closeAuction(),
  // which is where the delivery confirmation + inventory drawdown live.
  async endAuction(auctionId: number, linkshellId: number): Promise<void> {
    this.busyAuctionId.set(auctionId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/auctions/${auctionId}/end`),
        refresh: async () => {
          await this.auth.refreshOverview();
          await this.loadAuctions(linkshellId);
        },
        success: 'Auction ended. Close it to archive and deliver items.',
        failure: 'Ending the auction failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  // Leadership freezes/unfreezes bidding for the whole linkshell (anti-collusion).
  async setAuctionsLock(linkshellId: number, locked: boolean): Promise<void> {
    await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/auctions-lock`, { locked }),
      refresh: async () => {
        await this.loadAuctions(linkshellId);
        // Refresh the overview so the linkshell-level lock flag updates even when
        // the board has zero auctions (the per-auction flag has nothing to ride).
        await this.auth.refreshOverview();
      },
      success: locked ? 'Bidding locked.' : 'Bidding unlocked.',
      failure: 'Updating the auction lock failed.'
    });
  }

  async closeAuction(
    auctionId: number,
    linkshellId: number,
    deliveredItemIds: number[] = []
  ): Promise<void> {
    this.busyAuctionId.set(auctionId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/auctions/${auctionId}/close`, {
          deliveredItemIds
        }),
        refresh: async () => {
          await this.auth.refreshOverview();
          await this.loadAuctions(linkshellId);
          await this.loadAuctionHistory(linkshellId);
        },
        success: 'Auction closed and archived.',
        failure: 'Closing the auction failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  // Throws the auction away: no history, no DKP charged, no inventory drawn down.
  async deleteAuction(auctionId: number, linkshellId: number): Promise<void> {
    this.busyAuctionId.set(auctionId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/auctions/${auctionId}/delete`),
        refresh: async () => {
          await this.auth.refreshOverview();
          await this.loadAuctions(linkshellId);
        },
        success: 'Auction deleted.',
        failure: 'Deleting the auction failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async markAuctionHistoryItemReceived(itemId: number, linkshellId: number): Promise<void> {
    this.busyAuctionItemId.set(itemId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/auction-history/items/${itemId}/received`),
        refresh: () => this.loadAuctionHistory(linkshellId),
        success: 'Auction history item marked received.',
        failure: 'Updating auction history failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyAuctionItemId.set(null);
    }
  }

  async undoAuctionHistoryItem(itemId: number, linkshellId: number): Promise<void> {
    this.busyAuctionItemId.set(itemId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/auction-history/items/${itemId}/undo`),
        refresh: () => this.loadAuctionHistory(linkshellId),
        success: 'Auction history status updated.',
        failure: 'Updating auction history failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyAuctionItemId.set(null);
    }
  }
}
