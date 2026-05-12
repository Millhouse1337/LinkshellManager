import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import type {
  ActivityLootHistoryItem,
  ActivityLootHistoryList
} from '../../discord/discord-activity.types';

type SourceFilter = 'all' | 'tod' | 'event';

interface LootEditFormModel {
  itemName: string;
  itemWinner: string;
  winningDkpSpent: number | null;
  reason: string;
}

@Component({
  selector: 'app-loot-history-panel',
  imports: [CommonModule, FormsModule],
  templateUrl: './loot-history-panel.component.html',
  styleUrl: './loot-history-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class LootHistoryPanelComponent implements OnInit {
  protected readonly activity = inject(DiscordActivityService);

  @Input({ required: true }) selectedLinkshellId!: number;
  @Input() rosterCharacterNames: string[] = [];

  protected readonly sourceFilter = signal<SourceFilter>('all');
  protected readonly pageIndex = signal(0); // 0-based for UI; server uses 1-based
  protected readonly pageSize = 20;
  protected readonly winnerFilter = signal('');
  protected readonly itemFilter = signal('');

  protected readonly editingItem = signal<ActivityLootHistoryItem | null>(null);
  protected editFormModel: LootEditFormModel = this.emptyForm();
  protected editError = signal<string | null>(null);

  ngOnInit(): void {
    void this.refresh();
  }

  protected async refresh(): Promise<void> {
    await this.activity.loadLootHistory(this.sourceFilter(), this.pageIndex() + 1, this.pageSize);
  }

  protected setSource(value: SourceFilter): void {
    if (this.sourceFilter() === value) return;
    this.sourceFilter.set(value);
    this.pageIndex.set(0);
    void this.refresh();
  }

  protected goPrev(): void {
    if (this.pageIndex() === 0) return;
    this.pageIndex.update(p => p - 1);
    void this.refresh();
  }

  protected goNext(): void {
    const list = this.activity.lootHistory();
    if (!list) return;
    const totalPages = Math.max(1, Math.ceil(list.totalCount / list.pageSize));
    if (this.pageIndex() + 1 >= totalPages) return;
    this.pageIndex.update(p => p + 1);
    void this.refresh();
  }

  protected totalPages = computed(() => {
    const list = this.activity.lootHistory();
    if (!list || list.totalCount === 0) return 1;
    return Math.max(1, Math.ceil(list.totalCount / list.pageSize));
  });

  // Client-side filters layered on top of the server's paginated response.
  // The server doesn't expose substring filters today; keeping these
  // client-side avoids adding query params while still letting the user
  // narrow what they see in the current page.
  protected filteredItems = computed(() => {
    const list = this.activity.lootHistory();
    if (!list) return [] as ActivityLootHistoryItem[];
    const winnerNeedle = this.winnerFilter().trim().toLowerCase();
    const itemNeedle = this.itemFilter().trim().toLowerCase();
    return list.items.filter(row => {
      if (winnerNeedle && !(row.itemWinner ?? '').toLowerCase().includes(winnerNeedle)) {
        return false;
      }
      if (itemNeedle && !(row.itemName ?? '').toLowerCase().includes(itemNeedle)) {
        return false;
      }
      return true;
    });
  });

  protected openEdit(item: ActivityLootHistoryItem): void {
    this.editingItem.set(item);
    this.editFormModel = {
      itemName: item.itemName ?? '',
      itemWinner: item.itemWinner ?? '',
      winningDkpSpent: item.winningDkpSpent ?? 0,
      reason: ''
    };
    this.editError.set(null);
  }

  protected closeEdit(): void {
    this.editingItem.set(null);
    this.editFormModel = this.emptyForm();
    this.editError.set(null);
  }

  protected async submitEdit(): Promise<void> {
    const item = this.editingItem();
    if (!item) return;
    const reason = (this.editFormModel.reason ?? '').trim();
    if (!reason) {
      this.editError.set('An edit reason is required.');
      return;
    }
    const dkp = this.editFormModel.winningDkpSpent;
    if (dkp == null || dkp < 0) {
      this.editError.set('DKP amount must be 0 or greater.');
      return;
    }
    const itemName = (this.editFormModel.itemName ?? '').trim();
    const itemWinner = (this.editFormModel.itemWinner ?? '').trim();
    if (!itemName || !itemWinner) {
      this.editError.set('Item name and winner are required.');
      return;
    }

    this.editError.set(null);
    const ok = item.source === 'Tod'
      ? await this.activity.editTodLoot(item.lootDetailId, {
          itemName, itemWinner, winningDkpSpent: dkp, reason
        })
      : await this.activity.editEventLoot(item.lootDetailId, {
          itemName, itemWinner, winningDkpSpent: dkp, reason
        });

    if (ok) {
      this.closeEdit();
      await this.refresh();
    } else {
      this.editError.set(this.activity.actionError() ?? 'Saving the loot edit failed.');
    }
  }

  protected formatTimestamp(value?: string | null): string {
    return this.activity.formatDateTime(value) ?? '—';
  }

  protected list(): ActivityLootHistoryList | null {
    return this.activity.lootHistory();
  }

  private emptyForm(): LootEditFormModel {
    return { itemName: '', itemWinner: '', winningDkpSpent: 0, reason: '' };
  }
}
