import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import type { ActivityItem, ActivityItemInput } from '../../discord/discord-activity.types';

/**
 * Treasury → Items: the linkshell's stash.
 *
 * Lifted out of LinkshellTabComponent alongside the gil section, so the Management tab is people again
 * — roster, ranks, invites — and the two things a linkshell owns live together under Treasury.
 *
 * Items and gil belong side by side because one turns into the other: marking an item sold records the
 * gil against the treasury in the same transaction.
 */
@Component({
  selector: 'app-items-section',
  imports: [CommonModule, FormsModule],
  templateUrl: './items-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ItemsSectionComponent {
  protected readonly activity = inject(DiscordActivityService);

  @Input({ required: true }) set linkshellId(value: number | null) {
    this.selectedLinkshellId.set(value);
  }

  /** Whether this member can add, edit, sell or remove items (CanManageInventory). */
  @Input() canManage = false;

  protected readonly selectedLinkshellId = signal<number | null>(null);

  protected readonly items = computed<ActivityItem[]>(() => {
    const id = this.selectedLinkshellId();
    const primary = this.activity.overview()?.primaryLinkshell;
    if (!primary || primary.id !== id) {
      return [];
    }
    return primary.items ?? [];
  });

  // Stockpile is what the linkshell still holds; Sold is the archive.
  //
  // Sold items are kept rather than deleted — the gil entry points at them, and "what did that
  // Osode go for" is a question officers actually ask — but a stash you can still sell from should
  // not be padded out with things already gone.
  protected readonly view = signal<'stockpile' | 'sold'>('stockpile');

  protected readonly stockpile = computed(() => this.items().filter(item => !item.isSold));

  /**
   * Names to suggest for "who sold it", read off the same overview payload the items come from.
   *
   * A datalist rather than a select: the seller is usually on the roster but not always — gil lands
   * on mules, and a mule is not a member — so the list is a shortcut, never a gate.
   */
  protected readonly roster = computed(() => {
    const primary = this.activity.overview()?.primaryLinkshell;
    if (!primary || primary.id !== this.selectedLinkshellId()) {
      return [] as string[];
    }
    return (primary.members ?? [])
      .map(member => member.characterName)
      .filter((name): name is string => !!name && name.trim().length > 0)
      .sort((a, b) => a.localeCompare(b));
  });

  /** Most recently sold first: the archive only grows, and the newest sale is the interesting one. */
  protected readonly soldItems = computed(() =>
    this.items()
      .filter(item => item.isSold)
      .slice()
      .sort((a, b) => (b.updatedAt ?? '').localeCompare(a.updatedAt ?? ''))
  );

  protected readonly visibleItems = computed(() =>
    this.view() === 'sold' ? this.soldItems() : this.stockpile()
  );

  protected readonly totalQuantity = computed(() =>
    this.stockpile().reduce((sum, item) => sum + (item.quantity ?? 0), 0)
  );

  /** What the archive has brought in, so the Sold tab answers its own obvious question. */
  protected readonly soldTotal = computed(() =>
    this.soldItems().reduce((sum, item) => sum + (item.soldPrice ?? 0), 0)
  );

  protected setView(view: 'stockpile' | 'sold'): void {
    this.view.set(view);
  }

  // --- the add/edit form ---
  protected showForm = signal(false);
  protected readonly itemTypeOptions = ['Crafted Item', 'Endgame Loot Drop', 'Other'] as const;
  protected itemName = '';
  protected itemType = '';
  protected itemTypeSelection = '';
  protected itemQuantity = 1;
  protected itemNotes = '';
  protected editingItemId = signal<number | null>(null);

  protected onItemTypeSelectionChange(value: string): void {
    this.itemTypeSelection = value;
    // Preset selections (e.g. "Crafted Item") flow straight through; "Other" and the empty placeholder
    // both clear the value so the freeform input starts blank.
    this.itemType = value && value !== 'Other' ? value : '';
  }

  protected toggleForm(): void {
    this.showForm.update(value => !value);
    if (!this.showForm()) {
      this.resetForm();
      return;
    }
    // A new item is never sold, so it would land in the other tab and look like nothing happened.
    this.view.set('stockpile');
  }

  protected resetForm(): void {
    this.itemName = '';
    this.itemType = '';
    this.itemTypeSelection = '';
    this.itemQuantity = 1;
    this.itemNotes = '';
    this.editingItemId.set(null);
  }

  protected beginEdit(item: ActivityItem): void {
    this.editingItemId.set(item.id);
    this.itemName = item.itemName;
    const existingType = item.itemType ?? '';
    // Older items can carry any string for itemType. A preset match selects the dropdown; anything else
    // falls into the "Other" branch so the freeform input shows what was saved.
    if ((this.itemTypeOptions as readonly string[]).includes(existingType)) {
      this.itemTypeSelection = existingType;
      this.itemType = existingType;
    } else if (existingType) {
      this.itemTypeSelection = 'Other';
      this.itemType = existingType;
    } else {
      this.itemTypeSelection = '';
      this.itemType = '';
    }
    this.itemQuantity = item.quantity;
    this.itemNotes = item.notes ?? '';
    this.showForm.set(true);
  }

  protected async submit(): Promise<void> {
    const linkshellId = this.selectedLinkshellId();
    if (!linkshellId) {
      return;
    }
    const name = this.itemName.trim();
    if (!name) {
      return;
    }
    const input: ActivityItemInput = {
      itemName: name,
      itemType: this.itemType.trim() || null,
      quantity: Math.max(0, Math.floor(this.itemQuantity || 0)),
      notes: this.itemNotes.trim() || null
    };
    try {
      const editingId = this.editingItemId();
      if (editingId !== null) {
        await this.activity.updateItem(editingId, input);
      } else {
        await this.activity.createItem(linkshellId, input);
      }
      this.resetForm();
      this.showForm.set(false);
    } catch {
      // surfaced by the service
    }
  }

  protected async deleteItem(itemId: number): Promise<void> {
    try {
      await this.activity.deleteItem(itemId);
    } catch {
      // surfaced
    }
  }

  // --- Selling: the point where an item becomes gil ---
  // Marking an item sold records the gil against the treasury in the same transaction, so the two
  // halves of the Treasury tab stay consistent without anyone re-typing a number.
  protected readonly sellingItemId = signal<number | null>(null);
  protected sellPrice: number | null = null;

  /**
   * Who sold it — NOT whoever is clicking. An officer records sales other members made all the time,
   * and the app used to file the clicker as the seller.
   *
   * It is also the answer to where the gil went: whoever sold it is holding it until they hand it
   * on, so this one box fills both the item's seller and the treasury entry's holder, and the
   * Treasury tab's who's-holding-the-gil list gets a name instead of another anonymous arrival.
   */
  protected soldBy = '';

  protected beginSell(itemId: number): void {
    this.sellPrice = null;
    this.soldBy = '';
    this.sellingItemId.set(itemId);
  }

  /**
   * Blank is not zero. An empty box is someone who has not said a price yet, and recording that as a
   * 0-gil sale writes a transaction into the list claiming the item went for nothing. Typing an
   * explicit 0 still works — an item can genuinely be given away.
   */
  protected get sellPriceMissing(): boolean {
    return this.sellPrice === null || this.sellPrice === undefined || String(this.sellPrice).trim() === '';
  }

  /** Both boxes have to be answered before a sale can be recorded — see sellPriceMissing. */
  protected get sellerMissing(): boolean {
    return this.soldBy.trim().length === 0;
  }

  protected cancelSell(): void {
    this.sellingItemId.set(null);
    this.sellPrice = null;
    this.soldBy = '';
  }

  protected async confirmSell(itemId: number): Promise<void> {
    const price = Math.max(0, Math.floor(Number(this.sellPrice) || 0));
    const seller = this.soldBy.trim();
    if (seller.length === 0) {
      return;
    }
    try {
      await this.activity.markItemSold(itemId, price, seller);
      this.sellingItemId.set(null);
      this.sellPrice = null;
      this.soldBy = '';
    } catch {
      // surfaced
    }
  }

  /** Undoing a sale reverses the gil entry rather than deleting it — the sale still happened. */
  protected async unsellItem(itemId: number): Promise<void> {
    try {
      await this.activity.unsellItem(itemId);
    } catch {
      // surfaced
    }
  }
}
