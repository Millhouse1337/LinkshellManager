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

  async loadAuctions(linkshellId?: number | null): Promise<void> {
    this.auctionsBusy.set(true);

    try {
      const accessToken = this.auth.currentAccessToken();
      const query = new URLSearchParams();
      if (linkshellId) {
        query.set('linkshellId', String(linkshellId));
      }

      const path =
        query.size > 0 ? `/api/activity/auctions?${query.toString()}` : '/api/activity/auctions';
      this.auctions.set(await this.http.fetchActivityJson<ActivityAuction[]>(path, accessToken));
    } catch (error) {
      this.auctions.set([]);
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
        items: input.items
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
        items: input.items
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

  // Undo the viewer's OWN currently-winning bid while the auction is live.
  // The next-highest remaining bid becomes the winner; the viewer is then
  // blocked from in-game loot wins for the linkshell's cooldown window.
  async undoAuctionBid(itemId: number, linkshellId: number): Promise<void> {
    this.busyAuctionItemId.set(itemId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/auction-items/${itemId}/undo-bid`);
      await this.loadAuctions(linkshellId);
      await this.loadAuctionItemBids(itemId);
      await this.auth.refreshOverview();
      this.auth.setActionMessage(
        'Bid undone. You are temporarily blocked from in-game loot wins.'
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Undoing the bid failed.'));
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
