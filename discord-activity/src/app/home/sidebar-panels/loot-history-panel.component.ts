import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import type {
  ActivityLootEventOption,
  ActivityLootHistoryItem,
  ActivityLootHistoryList
} from '../../discord/discord-activity.types';


interface LootEditFormModel {
  itemName: string;
  itemWinner: string;
  winningDkpSpent: number | null;
  reason: string;
}

interface LootAddFormModel {
  context: string;
  itemName: string;
  itemWinner: string;
  winningDkpSpent: number | null;
  noDkp: boolean;
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

  protected readonly pageIndex = signal(0); // 0-based for UI; server uses 1-based
  protected readonly pageSize = 20;
  protected readonly winnerFilter = signal('');
  protected readonly itemFilter = signal('');

  protected readonly editingItem = signal<ActivityLootHistoryItem | null>(null);
  protected editFormModel: LootEditFormModel = this.emptyForm();
  protected editError = signal<string | null>(null);

  protected readonly addingLoot = signal(false);
  protected addFormModel: LootAddFormModel = this.emptyAddForm();
  protected addError = signal<string | null>(null);
  // Which kind of event the loot came from, and the id within that kind. Loot used to carry a
  // free-text label that the server turned into a throwaway ToD; it points at a real Event or
  // EventHistory now, or at nothing.
  protected addSourceKind: "none" | "live" | "past" = "none";
  protected addEventId: number | null = null;
  protected addEventHistoryId: number | null = null;

  // Fetched rather than read off the overview: the overview only carries LIVE events, and past
  // ones are the whole point of the second picker.
  protected readonly liveEventOptions = signal<ActivityLootEventOption[]>([]);
  protected readonly pastEventOptions = signal<ActivityLootEventOption[]>([]);
  protected readonly pastEventQuery = signal("");
  private pastEventSearchTimer: ReturnType<typeof setTimeout> | null = null;

  // Debounced: each keystroke would otherwise fire a request, and this runs inside the Discord
  // iframe where that is worth avoiding.
  protected onPastEventQueryChange(value: string): void {
    this.pastEventQuery.set(value);
    if (this.pastEventSearchTimer) clearTimeout(this.pastEventSearchTimer);
    this.pastEventSearchTimer = setTimeout(() => void this.loadEventOptions(), 350);
  }

  private async loadEventOptions(): Promise<void> {
    const options = await this.activity.loadLootEventOptions(this.pastEventQuery());
    if (!options) return;
    this.liveEventOptions.set(options.liveEvents ?? []);
    this.pastEventOptions.set(options.pastEvents ?? []);
  }

  // Whether the current user may add loot to the selected linkshell (drives the
  // "+ Add loot" button). The server re-checks CanAddLoot regardless.
  protected canAddLoot(): boolean {
    const link = this.activity.overview()?.linkshells?.find(l => l.id === this.selectedLinkshellId);
    return !!link?.permissions?.canAddLoot;
  }

  protected openAdd(): void {
    this.addFormModel = this.emptyAddForm();
    this.addSourceKind = "none";
    this.addEventId = null;
    this.addEventHistoryId = null;
    this.pastEventQuery.set("");
    this.addError.set(null);
    this.addingLoot.set(true);
    void this.loadEventOptions();
  }

  protected closeAdd(): void {
    this.addingLoot.set(false);
    this.addFormModel = this.emptyAddForm();
    this.addSourceKind = "none";
    this.addEventId = null;
    this.addEventHistoryId = null;
    this.addError.set(null);
  }

  protected onNoDkpChange(checked: boolean): void {
    this.addFormModel.noDkp = checked;
    if (checked) this.addFormModel.winningDkpSpent = 0;
  }

  protected async submitAdd(): Promise<void> {
    const itemName = (this.addFormModel.itemName ?? '').trim();
    const itemWinner = (this.addFormModel.itemWinner ?? '').trim();
    if (!itemName) {
      this.addError.set('Item name is required.');
      return;
    }
    if (!itemWinner) {
      this.addError.set('A winner is required.');
      return;
    }
    const dkp = this.addFormModel.noDkp ? 0 : (this.addFormModel.winningDkpSpent ?? 0);
    if (dkp < 0) {
      this.addError.set('DKP spent must be 0 or greater.');
      return;
    }

    // An event has to actually be CHOSEN once its kind is picked, or the loot would silently
    // fall back to "No event" and the officer would think they had filed it.
    if (this.addSourceKind === "live" && !this.addEventId) {
      this.addError.set("Choose a live event, or switch to No event.");
      return;
    }
    if (this.addSourceKind === "past" && !this.addEventHistoryId) {
      this.addError.set("Choose a past event, or switch to No event.");
      return;
    }

    this.addError.set(null);
    const ok = await this.activity.addManualLoot({
      sourceKind: this.addSourceKind,
      eventId: this.addSourceKind === "live" ? this.addEventId : null,
      eventHistoryId: this.addSourceKind === "past" ? this.addEventHistoryId : null,
      itemName,
      itemWinner,
      winningDkpSpent: dkp
    });

    if (ok) {
      this.closeAdd();
      this.pageIndex.set(0);
      await this.refresh();
    } else {
      this.addError.set(this.activity.actionError() ?? 'Adding loot failed.');
    }
  }

  ngOnInit(): void {
    void this.refresh();
  }

  protected async refresh(): Promise<void> {
    await this.activity.loadLootHistory(this.pageIndex() + 1, this.pageSize);
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

  private emptyAddForm(): LootAddFormModel {
    return { context: '', itemName: '', itemWinner: '', winningDkpSpent: 0, noDkp: false };
  }
}
