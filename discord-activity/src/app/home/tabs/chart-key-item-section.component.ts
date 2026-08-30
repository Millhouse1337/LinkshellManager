import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, computed, inject, signal } from '@angular/core';

import { ChartBoardService } from '../../discord/chart-board.service';
import type {
  ActivityChartBoard,
  ActivityChartKeyItemRow
} from '../../discord/discord-activity.types';

/**
 * Who holds which key item.
 *
 * Presence is the fact - a ticked cell is a stored row and an empty one is no row - so every count
 * here reads as a fraction of the LIVE roster rather than as a stored total, and "still needs it" is
 * the inverse of what is stored.
 *
 * BOTH self-serve and officer override: a member ticks their own row, an officer ticks anybody's,
 * and the two write an identical row distinguishable only by its audit columns. The rule is decided
 * server-side on every write; canSet below only decides what is drawn enabled.
 *
 * Columns come off the payload in catalog order, so a key item nobody holds still gets a column
 * reading "0 of 14" - which is the whole point of the grid.
 */
@Component({
  selector: 'app-chart-key-item-section',
  imports: [CommonModule],
  templateUrl: './chart-key-item-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ChartKeyItemSectionComponent {
  protected readonly charts = inject(ChartBoardService);

  private readonly boardSignal = signal<ActivityChartBoard | null>(null);
  private readonly canManageSignal = signal(false);

  @Input({ required: true }) set board(value: ActivityChartBoard) {
    this.boardSignal.set(value);
  }

  @Input() set canManage(value: boolean) {
    this.canManageSignal.set(value);
  }

  protected readonly columns = computed(() => this.boardSignal()?.keyItems.columns ?? []);
  protected readonly rows = computed(() => this.boardSignal()?.keyItems.rows ?? []);
  protected readonly viewerMembershipId = computed(() => this.boardSignal()?.viewerMembershipId ?? null);

  protected readonly completeCount = computed(() =>
    this.rows().filter(row => row.totalColumns > 0 && row.haveCount === row.totalColumns).length);

  /**
   * Twin of ChartKeyItemService.CanSetKeyItemFor. A viewer with no membership never matches, and
   * this decides ENABLEMENT only - the server re-runs the same rule on every post.
   */
  protected canSet(membershipId: number): boolean {
    const viewer = this.viewerMembershipId();
    return this.canManageSignal() || (viewer !== null && viewer === membershipId);
  }

  protected isYou(row: ActivityChartKeyItemRow): boolean {
    return this.viewerMembershipId() === row.membershipId;
  }

  /** One cell, one post: the fact is per cell, and batching would need a diff nobody asked for. */
  protected async toggle(row: ActivityChartKeyItemRow, columnIndex: number): Promise<void> {
    const board = this.boardSignal();
    const column = this.columns()[columnIndex];
    if (!board || !column || !this.canSet(row.membershipId)) {
      return;
    }

    try {
      await this.charts.setKeyItem(board.linkshellId, board.board, {
        keyItemName: column.name,
        membershipId: row.membershipId,
        has: !row.has[columnIndex]
      });
    } catch {
      // Surfaced by the service through the shared action banner.
    }
  }
}
