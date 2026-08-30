import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityLootEditInput,
  ActivityLootEventOptions,
  ActivityLootHistoryList
} from "./discord-activity.types";

// Client wrapper around /api/activity/loot-history endpoints. Mirrors the
// shape of DkpService so the home component can wire it in identically.
// The list is a signal so the panel can re-render reactively after an edit
// without re-fetching from scratch.
@Injectable({ providedIn: 'root' })
export class LootHistoryService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly lootHistory = signal<ActivityLootHistoryList | null>(null);
  readonly lootHistoryBusy = signal(false);
  readonly busyLootEdit = signal(false);
  readonly busyLootAdd = signal(false);

  async addLoot(input: {
    // Which KIND of event this came from; picks which id below the server reads.
    sourceKind: "none" | "live" | "past";
    eventId: number | null;
    eventHistoryId: number | null;
    itemName: string;
    itemWinner: string;
    winningDkpSpent: number;
  }): Promise<boolean> {
    this.busyLootAdd.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityJson('/api/activity/loot-history', input);
      this.auth.setActionMessage('Loot added.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Adding loot failed.'));
      return false;
    } finally {
      this.busyLootAdd.set(false);
    }
  }

  // Live events plus the recent past ones for the Add loot pickers. `query` widens the past list;
  // without it only the most recent are offered.
  async loadLootEventOptions(query?: string | null): Promise<ActivityLootEventOptions | null> {
    try {
      const accessToken = this.auth.currentAccessToken();
      const search = new URLSearchParams();
      if (query && query.trim()) search.set("q", query.trim());
      const qs = search.toString();
      return await this.http.fetchActivityJson<ActivityLootEventOptions>(
        `/api/activity/loot-history/event-options-e`,
        accessToken
      );
    } catch {
      // A failed lookup must not block recording the loot -- the officer can still file it as
      // "No event" and re-file it later.
      return null;
    }
  }

  async loadLootHistory(
    page = 1,
    pageSize = 20
  ): Promise<ActivityLootHistoryList | null> {
    this.lootHistoryBusy.set(true);
    try {
      const accessToken = this.auth.currentAccessToken();
      const query = new URLSearchParams({
        page: String(page),
        pageSize: String(pageSize)
      });
      const result = await this.http.fetchActivityJson<ActivityLootHistoryList>(
        `/api/activity/loot-history?${query.toString()}`,
        accessToken
      );
      this.lootHistory.set(result);
      return result;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading Loot History failed.'));
      this.lootHistory.set(null);
      return null;
    } finally {
      this.lootHistoryBusy.set(false);
    }
  }

  async editTodLoot(lootDetailId: number, input: ActivityLootEditInput): Promise<boolean> {
    return this.editLoot(`/api/activity/loot-history/tod/${lootDetailId}/edit`, input);
  }

  async editEventLoot(lootDetailId: number, input: ActivityLootEditInput): Promise<boolean> {
    return this.editLoot(`/api/activity/loot-history/event/${lootDetailId}/edit`, input);
  }

  private async editLoot(path: string, input: ActivityLootEditInput): Promise<boolean> {
    this.busyLootEdit.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityJson(path, input);
      this.auth.setActionMessage('Loot record updated.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Saving the loot edit failed.'));
      return false;
    } finally {
      this.busyLootEdit.set(false);
    }
  }
}
