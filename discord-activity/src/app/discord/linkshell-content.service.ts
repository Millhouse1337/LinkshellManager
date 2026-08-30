import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type { ActivityItemInput, ActivityRevenueInput } from './discord-activity.types';
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
  readonly busyRevenueSave = signal(false);
  readonly busyRevenueId = signal<number | null>(null);

  // ----- Rules -----
  async createRule(linkshellId: number, title: string, details: string, category: string | null): Promise<void> {
    this.busyRuleSave.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/rules`, {
        title,
        details,
        category
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Rule added.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Creating the rule failed.'));
      throw error;
    } finally {
      this.busyRuleSave.set(false);
    }
  }

  async updateRule(ruleId: number, title: string, details: string, category: string | null): Promise<void> {
    this.busyRuleSave.set(true);
    this.busyRuleId.set(ruleId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/rules/${ruleId}/update`, {
        title,
        details,
        category
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Rule updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the rule failed.'));
      throw error;
    } finally {
      this.busyRuleSave.set(false);
      this.busyRuleId.set(null);
    }
  }

  async deleteRule(ruleId: number): Promise<void> {
    this.busyRuleId.set(ruleId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/rules/${ruleId}/delete`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Rule deleted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the rule failed.'));
      throw error;
    } finally {
      this.busyRuleId.set(null);
    }
  }

  // ----- Announcements -----
  async createAnnouncement(linkshellId: number, title: string, details: string, category: string | null): Promise<void> {
    this.busyAnnouncementSave.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/announcements`, {
        title,
        details,
        category
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Announcement posted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Creating the announcement failed.'));
      throw error;
    } finally {
      this.busyAnnouncementSave.set(false);
    }
  }

  async updateAnnouncement(announcementId: number, title: string, details: string, category: string | null): Promise<void> {
    this.busyAnnouncementSave.set(true);
    this.busyAnnouncementId.set(announcementId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/announcements/${announcementId}/update`, {
        title,
        details,
        category
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Announcement updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the announcement failed.'));
      throw error;
    } finally {
      this.busyAnnouncementSave.set(false);
      this.busyAnnouncementId.set(null);
    }
  }

  async deleteAnnouncement(announcementId: number): Promise<void> {
    this.busyAnnouncementId.set(announcementId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/announcements/${announcementId}/delete`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Announcement deleted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the announcement failed.'));
      throw error;
    } finally {
      this.busyAnnouncementId.set(null);
    }
  }

  // ----- Items -----
  async createItem(linkshellId: number, input: ActivityItemInput): Promise<void> {
    this.busyItemSave.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/items`, {
        itemName: input.itemName,
        itemType: input.itemType ?? null,
        quantity: input.quantity,
        notes: input.notes ?? null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Item added to inventory.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Adding the item failed.'));
      throw error;
    } finally {
      this.busyItemSave.set(false);
    }
  }

  async updateItem(itemId: number, input: ActivityItemInput): Promise<void> {
    this.busyItemId.set(itemId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/items/${itemId}/update`, {
        itemName: input.itemName,
        itemType: input.itemType ?? null,
        quantity: input.quantity,
        notes: input.notes ?? null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Item updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the item failed.'));
      throw error;
    } finally {
      this.busyItemId.set(null);
    }
  }

  async deleteItem(itemId: number): Promise<void> {
    this.busyItemId.set(itemId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/items/${itemId}/delete`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Item deleted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the item failed.'));
      throw error;
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
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(
        `/api/activity/items/${itemId}/mark-sold`, { salePrice, soldByCharacterName });
      // The overview carries the item; the gil lives on the treasury's own paged endpoint. Refresh
      // both, or the stash reads "sold" while the transactions list right below it shows nothing —
      // for up to a full background tick, which is exactly long enough to look broken.
      // The gil reload is best-effort: a hiccup loading the transactions list must never report a
      // sale that DID happen as a failure.
      await Promise.all([this.auth.refreshOverview(), this.treasury.reload().catch(() => undefined)]);
      this.auth.setActionMessage('Item sold — recorded in the transactions below.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Selling the item failed.'));
      throw error;
    } finally {
      this.busyItemId.set(null);
    }
  }

  async unsellItem(itemId: number): Promise<void> {
    this.busyItemId.set(itemId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/items/${itemId}/unsell`);
      // Same pair as the sale: undoing it reverses the gil entry, and that reversal is a row in the
      // transactions list too.
      // The gil reload is best-effort: a hiccup loading the transactions list must never report a
      // sale that DID happen as a failure.
      await Promise.all([this.auth.refreshOverview(), this.treasury.reload().catch(() => undefined)]);
      this.auth.setActionMessage('Sale undone — the gil entry was reversed.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Undoing the sale failed.'));
      throw error;
    } finally {
      this.busyItemId.set(null);
    }
  }

  // ----- Revenue -----
  async createRevenueEntry(linkshellId: number, input: ActivityRevenueInput): Promise<void> {
    this.busyRevenueSave.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/revenue`, {
        entryType: input.entryType,
        category: input.category ?? null,
        value: input.value,
        details: input.details ?? null,
        occurredAt: input.occurredAt ?? null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Revenue entry saved.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Saving the revenue entry failed.'));
      throw error;
    } finally {
      this.busyRevenueSave.set(false);
    }
  }

  async updateRevenueEntry(entryId: number, input: ActivityRevenueInput): Promise<void> {
    this.busyRevenueId.set(entryId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/revenue/${entryId}/update`, {
        entryType: input.entryType,
        category: input.category ?? null,
        value: input.value,
        details: input.details ?? null,
        occurredAt: input.occurredAt ?? null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Revenue entry updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the revenue entry failed.'));
      throw error;
    } finally {
      this.busyRevenueId.set(null);
    }
  }

  async deleteRevenueEntry(entryId: number): Promise<void> {
    this.busyRevenueId.set(entryId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/revenue/${entryId}/delete`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Revenue entry deleted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the revenue entry failed.'));
      throw error;
    } finally {
      this.busyRevenueId.set(null);
    }
  }
}
