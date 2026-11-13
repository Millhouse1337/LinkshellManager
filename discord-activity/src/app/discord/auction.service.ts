import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { datetimeLocalToUtcIso, formatActionError } from './discord-activity.helpers';
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
    } catch (error) {
      // A failed OLD request must not blank a list a newer request already filled.
      if (this.auctionsOrder.accept(ticket, key)) this.auctions.set([]);
      this.auth.setActionError(formatActionError(error, 'Loading auctions failed.'));
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
    } catch (error) {
      this.auctionHistory.set([]);
      this.auth.setActionError(formatActionError(error, 'Loading auction history failed.'));
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
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading auction bids failed.'));
    }
  }

  clearAuctionState(): void {
    this.auctions.set([]);
    this.auctionHistory.set([]);
    this.auctionBids.set({});
  }

  async createAuction(input: ActivityCreateAuctionInput): Promise<void> {
    this.busyAuctionId.set(input.linkshellId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction('/api/activity/auctions', {
        linkshellId: input.linkshellId,
        title: input.title,
        startTimeLocal: datetimeLocalToUtcIso(input.startTimeLocal),
        endTimeLocal: datetimeLocalToUtcIso(input.endTimeLocal),
        items: input.items,
        // Null = the linkshell's default pool.
        dkpPoolId: input.dkpPoolId ?? null
      });
      await this.auth.refreshOverview();
      await this.loadAuctions(input.linkshellId);
      await this.loadAuctionHistory(input.linkshellId);
      this.auth.setActionMessage('Auction created.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Creating the auction failed.'));
      throw error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async updateAuction(auctionId: number, input: ActivityCreateAuctionInput): Promise<void> {
    this.busyAuctionId.set(auctionId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/auctions/${auctionId}/update`, {
        linkshellId: input.linkshellId,
        title: input.title,
        startTimeLocal: datetimeLocalToUtcIso(input.startTimeLocal),
        endTimeLocal: datetimeLocalToUtcIso(input.endTimeLocal),
        items: input.items,
        // The form fills this with the auction's own pool, so an edit never moves the auction to
        // another pool unless the officer picked one. (The server also reads a missing pool as
        // "keep the current one".)
        dkpPoolId: input.dkpPoolId ?? null
      });
      await this.auth.refreshOverview();
      await this.loadAuctions(input.linkshellId);
      this.auth.setActionMessage('Auction updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the auction failed.'));
      throw error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async startAuction(auctionId: number, linkshellId: number): Promise<void> {
    this.busyAuctionId.set(auctionId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/auctions/${auctionId}/start`);
      await this.loadAuctions(linkshellId);
      this.auth.setActionMessage('Auction started.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Starting the auction failed.'));
      throw error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async placeAuctionBid(itemId: number, bidAmount: number, linkshellId: number): Promise<void> {
    this.busyAuctionItemId.set(itemId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/auction-items/${itemId}/bid`, { bidAmount });
      await this.loadAuctions(linkshellId);
      await this.loadAuctionItemBids(itemId);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Bid submitted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Submitting the bid failed.'));
      throw error;
    } finally {
      this.busyAuctionItemId.set(null);
    }
  }

  // Stops bidding now without archiving the auction. The auction stays on the
  // active board with status Ended until the creator runs closeAuction(),
  // which is where the delivery confirmation + inventory drawdown live.
  async endAuction(auctionId: number, linkshellId: number): Promise<void> {
    this.busyAuctionId.set(auctionId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/auctions/${auctionId}/end`);
      await this.auth.refreshOverview();
      await this.loadAuctions(linkshellId);
      this.auth.setActionMessage('Auction ended. Close it to archive and deliver items.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Ending the auction failed.'));
      throw error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  // Leadership freezes/unfreezes bidding for the whole linkshell (anti-collusion).
  async setAuctionsLock(linkshellId: number, locked: boolean): Promise<void> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/auctions-lock`, { locked });
      await this.loadAuctions(linkshellId);
      // Refresh the overview so the linkshell-level lock flag updates even when
      // the board has zero auctions (the per-auction flag has nothing to ride).
      await this.auth.refreshOverview();
      this.auth.setActionMessage(locked ? 'Bidding locked.' : 'Bidding unlocked.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the auction lock failed.'));
    }
  }

  async closeAuction(
    auctionId: number,
    linkshellId: number,
    deliveredItemIds: number[] = []
  ): Promise<void> {
    this.busyAuctionId.set(auctionId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/auctions/${auctionId}/close`, {
        deliveredItemIds
      });
      await this.auth.refreshOverview();
      await this.loadAuctions(linkshellId);
      await this.loadAuctionHistory(linkshellId);
      this.auth.setActionMessage('Auction closed and archived.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Closing the auction failed.'));
      throw error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  // Throws the auction away: no history, no DKP charged, no inventory drawn down.
  async deleteAuction(auctionId: number, linkshellId: number): Promise<void> {
    this.busyAuctionId.set(auctionId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/auctions/${auctionId}/delete`);
      await this.auth.refreshOverview();
      await this.loadAuctions(linkshellId);
      this.auth.setActionMessage('Auction deleted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the auction failed.'));
      throw error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async markAuctionHistoryItemReceived(itemId: number, linkshellId: number): Promise<void> {
    this.busyAuctionItemId.set(itemId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/auction-history/items/${itemId}/received`);
      await this.loadAuctionHistory(linkshellId);
      this.auth.setActionMessage('Auction history item marked received.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating auction history failed.'));
      throw error;
    } finally {
      this.busyAuctionItemId.set(null);
    }
  }

  async undoAuctionHistoryItem(itemId: number, linkshellId: number): Promise<void> {
    this.busyAuctionItemId.set(itemId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/auction-history/items/${itemId}/undo`);
      await this.loadAuctionHistory(linkshellId);
      this.auth.setActionMessage('Auction history status updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating auction history failed.'));
      throw error;
    } finally {
      this.busyAuctionItemId.set(null);
    }
  }
}
