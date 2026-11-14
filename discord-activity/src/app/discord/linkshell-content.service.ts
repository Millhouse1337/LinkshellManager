import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import type { ActivityItemInput } from './discord-activity.types';
import { mutateThenRefresh } from './mutate-then-refresh';
import { TreasuryService } from './treasury.service';

/**
 * Small CRUD groups that all hang off a linkshell:
 * - Rules
 * - Announcements
 * - Items (inventory)
 * - Revenue entries
 *
 * Bundled into one service rather than four micro-services because each
 * group is just create/update/delete with the same shape.
 */
@Injectable({ providedIn: 'root' })
export class LinkshellContentService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);
  // Selling an item is BOTH halves of the Treasury tab at once — the server records the item and the
  // gil in ONE database transaction, so the screen has to refresh both or the two disagree until the
  // next background tick. TreasuryService depends on nothing in here, so the arrow only points one way.
  private readonly treasury = inject(TreasuryService);

  readonly busyRuleSave = signal(false);
  readonly busyRuleId = signal<number | null>(null);
  readonly busyAnnouncementSave = signal(false);
  readonly busyAnnouncementId = signal<number | null>(null);
  readonly busyItemSave = signal(false);
  readonly busyItemId = signal<number | null>(null);

  // Every save here sends one change, reloads, and throws only when the SAVE failed (see
  // mutate-then-refresh.ts): a reload that fails after a save that worked never reports it as failed.
  private async save(
    mutate: () => Promise<unknown>,
    success: string,
    failure: string,
    refresh: () => Promise<unknown> = () => this.auth.refreshOverview()
  ): Promise<void> {
    const outcome = await mutateThenRefresh(this.auth, { mutate, refresh, success, failure });
    if (!outcome.ok) throw outcome.error;
  }

  // ----- Rules -----
  async createRule(linkshellId: number, title: string, details: string, category: string | null): Promise<void> {
    this.busyRuleSave.set(true);
    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/rules`, {
          title,
          details,
          category
        }),
        'Rule added.',
        'Creating the rule failed.');
    } finally {
      this.busyRuleSave.set(false);
    }
  }

  async updateRule(ruleId: number, title: string, details: string, category: string | null): Promise<void> {
    this.busyRuleSave.set(true);
    this.busyRuleId.set(ruleId);
    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/rules/${ruleId}/update`, {
          title,
          details,
          category
        }),
        'Rule updated.',
        'Updating the rule failed.');
    } finally {
      this.busyRuleSave.set(false);
      this.busyRuleId.set(null);
    }
  }

  async deleteRule(ruleId: number): Promise<void> {
    this.busyRuleId.set(ruleId);
    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/rules/${ruleId}/delete`),
        'Rule deleted.',
        'Deleting the rule failed.');
    } finally {
      this.busyRuleId.set(null);
    }
  }

  // ----- Announcements -----
  async createAnnouncement(linkshellId: number, title: string, details: string, category: string | null): Promise<void> {
    this.busyAnnouncementSave.set(true);
    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/announcements`, {
          title,
          details,
          category
        }),
        'Announcement posted.',
        'Creating the announcement failed.');
    } finally {
      this.busyAnnouncementSave.set(false);
    }
  }

  async updateAnnouncement(announcementId: number, title: string, details: string, category: string | null): Promise<void> {
    this.busyAnnouncementSave.set(true);
    this.busyAnnouncementId.set(announcementId);
    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/announcements/${announcementId}/update`, {
          title,
          details,
          category
        }),
        'Announcement updated.',
        'Updating the announcement failed.');
    } finally {
      this.busyAnnouncementSave.set(false);
      this.busyAnnouncementId.set(null);
    }
  }

  async deleteAnnouncement(announcementId: number): Promise<void> {
    this.busyAnnouncementId.set(announcementId);
    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/announcements/${announcementId}/delete`),
        'Announcement deleted.',
        'Deleting the announcement failed.');
    } finally {
      this.busyAnnouncementId.set(null);
    }
  }

  // ----- Items -----
  async createItem(linkshellId: number, input: ActivityItemInput): Promise<void> {
    this.busyItemSave.set(true);
    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/items`, {
          itemName: input.itemName,
          itemType: input.itemType ?? null,
          quantity: input.quantity,
          notes: input.notes ?? null
        }),
        'Item added to inventory.',
        'Adding the item failed.');
    } finally {
      this.busyItemSave.set(false);
    }
  }

  async updateItem(itemId: number, input: ActivityItemInput): Promise<void> {
    this.busyItemId.set(itemId);
    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/items/${itemId}/update`, {
          itemName: input.itemName,
          itemType: input.itemType ?? null,
          quantity: input.quantity,
          notes: input.notes ?? null
        }),
        'Item updated.',
        'Updating the item failed.');
    } finally {
      this.busyItemId.set(null);
    }
  }

  async deleteItem(itemId: number): Promise<void> {
    this.busyItemId.set(itemId);
    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/items/${itemId}/delete`),
        'Item deleted.',
        'Deleting the item failed.');
    } finally {
      this.busyItemId.set(null);
    }
  }

  // Mark an item sold for a price → records the income in Finances (server-side).
  //
  // soldByCharacterName is WHO SOLD IT, which is regularly not whoever is clicking: an officer
  // records sales other members made. They are also the one left holding the gil, so the same answer
  // becomes the treasury entry's holder and the item's seller. The server refuses the sale without it.
  async markItemSold(itemId: number, salePrice: number, soldByCharacterName: string): Promise<void> {
    this.busyItemId.set(itemId);
    try {
      await this.save(
        () => this.http.postActivityAction(
          `/api/activity/items/${itemId}/mark-sold`, { salePrice, soldByCharacterName }),
        'Item sold — recorded in the transactions below.',
        'Selling the item failed.',
        // The overview carries the item; the gil lives on the treasury's own paged endpoint. Refresh
        // both, or the stash reads "sold" while the transactions list right below it shows nothing —
        // for up to a full background tick, which is exactly long enough to look broken.
        () => this.refreshItemsAndGil());
    } finally {
      this.busyItemId.set(null);
    }
  }

  async unsellItem(itemId: number): Promise<void> {
    this.busyItemId.set(itemId);
    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/items/${itemId}/unsell`),
        'Sale undone — the gil entry was reversed.',
        'Undoing the sale failed.',
        // Same pair as the sale: undoing it reverses the gil entry, and that reversal is a row in the
        // transactions list too.
        () => this.refreshItemsAndGil());
    } finally {
      this.busyItemId.set(null);
    }
  }

  private async refreshItemsAndGil(): Promise<void> {
    // The gil reload is best-effort here; only the overview reload decides "press Refresh".
    await Promise.all([this.auth.refreshOverview(), this.treasury.reload().catch(() => undefined)]);
  }
}
