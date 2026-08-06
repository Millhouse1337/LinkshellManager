import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, Output, EventEmitter, computed, inject, signal } from '@angular/core';

import { ChartBoardService } from '../../discord/chart-board.service';
import type { ActivityChartBoss, ActivityChartPopItem } from '../../discord/discord-activity.types';

/**
 * One line of the holdings table: ONE pop item, in ONE person's hands, with the farmers credited
 * for that copy of it.
 *
 * The same item appears as many times as the linkshell holds copies of it — three Gems of the North
 * held by three people are three rows, which is the question this table exists to answer ("who is
 * sitting on what"). A holder is a plain name, so "alt of Millhouse" is a perfectly good one. The
 * boss cards above fold these back into one line per item; this is the grain the data is stored at.
 */
interface ChartHoldingRow {
  item: ActivityChartPopItem;
  boss: string;
  themeKey: string;
  heldBy: string | null;
  credits: string[];
  /** True when the row above is the same item on the same boss, so its name cell is left blank. */
  continues: boolean;
}

/**
 * Every pop item on the board, by item and by who holds it — and the ONLY place a row is edited.
 *
 * Replaces the member × boss credit ledger that used to sit here. That grid answered "how much of
 * the board has each member farmed"; this answers "where is each item, who earned it, and let me fix
 * it" — the question the cards above are actually filled in to answer.
 *
 * The row buttons live here rather than on the cards because a card is one of five in a row with no
 * width to spare, and because a card's lines are CONSOLIDATED: "Gem of the South ×3" is not a thing
 * you can edit, credit or remove — the three rows behind it are, and they are here.
 *
 * Built from the items already in the payload, not a second server-side projection: the cards and
 * this table are the same rows read two ways, so they cannot disagree.
 */
@Component({
  selector: 'app-chart-holdings-section',
  imports: [CommonModule],
  templateUrl: './chart-holdings-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ChartHoldingsSectionComponent {
  protected readonly charts = inject(ChartBoardService);

  @Input({ required: true }) set bosses(value: ActivityChartBoss[]) {
    this.source.set(value ?? []);
  }

  @Input() boardLabel = '';

  /** The server's own answer on whether this member may edit. Never re-derived from permissions. */
  @Input() canManage = false;

  /**
   * Editing opens the add/edit form at the top of the board, which the parent owns — so Edit is an
   * event, while Remove is done here.
   */
  @Output() readonly edit = new EventEmitter<ActivityChartPopItem>();

  protected readonly source = signal<ActivityChartBoss[]>([]);

  /**
   * Rows in board order, then by item name, then by holder.
   *
   * Sorted by name WITHIN a boss rather than left in row order so copies of one item land together
   * and read as one block — which is what `continues` then blanks the repeated name cell for.
   */
  protected readonly rows = computed<ChartHoldingRow[]>(() => {
    const rows: ChartHoldingRow[] = [];

    for (const boss of this.source()) {
      const sorted = [...boss.items].sort(
        (left, right) =>
          left.itemName.localeCompare(right.itemName) ||
          (left.heldByCharacterName ?? '').localeCompare(right.heldByCharacterName ?? '')
      );

      for (const item of sorted) {
        const previous = rows[rows.length - 1];
        rows.push({
          item,
          boss: boss.boss,
          themeKey: boss.themeKey,
          heldBy: item.heldByCharacterName?.trim() || null,
          credits: item.credits.map(credit => credit.characterName),
          continues:
            previous?.boss === boss.boss &&
            previous?.item.itemName.toLowerCase() === item.itemName.toLowerCase()
        });
      }
    }

    return rows;
  });

  protected readonly isEmpty = computed(() => this.rows().length === 0);

  /** How many copies of anything the board holds — the headline number for the section. */
  protected readonly totalHeld = computed(() =>
    this.rows().reduce((sum, row) => sum + row.item.quantity, 0)
  );

  protected isBusy(itemId: number): boolean {
    return this.charts.busySave() && this.charts.busyItemId() === itemId;
  }

  // ---- who is credited ----
  //
  // READ-ONLY here. Expanding "3 credited" shows the farmer list as a row of names with nothing to
  // click by accident; changing it is the Edit form's farming credit picker, which opens preloaded
  // with exactly this list. One place a row is written.

  /** Rows whose farmer list is open, by item id. Several at once: they are read side by side. */
  protected readonly expanded = signal<Set<number>>(new Set());

  protected isExpanded(itemId: number): boolean {
    return this.expanded().has(itemId);
  }

  protected toggleExpanded(itemId: number): void {
    this.expanded.update(current => {
      const next = new Set(current);
      if (!next.delete(itemId)) {
        next.add(itemId);
      }
      return next;
    });
  }

  protected async removeItem(item: ActivityChartPopItem): Promise<void> {
    try {
      await this.charts.deleteItem(item.id);
    } catch {
      // surfaced
    }
  }

  protected themeClass(themeKey: string): string {
    return 'is-' + themeKey;
  }
}
