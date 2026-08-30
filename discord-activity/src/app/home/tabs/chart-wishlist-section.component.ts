import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { ChartBoardService } from '../../discord/chart-board.service';
import type {
  ActivityChartBoard,
  ActivityChartWishlistRequest
} from '../../discord/discord-activity.types';

/**
 * Item requests: what members WANT off this board, as distinct from what the linkshell is holding.
 *
 * The one part of Charts a member without CanManageCharts may write, which makes the gates here
 * unlike every other Charts section:
 *
 *   submit    anybody in the linkshell
 *   withdraw  request.canWithdraw - the SERVER's per-viewer answer, never re-derived here
 *   fulfil    canManage
 *   reorder   canManage
 *
 * canWithdraw is read off the row rather than worked out from a membership id on purpose. Deriving
 * it would make this component a second copy of the ownership rule, and two copies are how one
 * surface ends up more permissive than the other.
 */
@Component({
  selector: 'app-chart-wishlist-section',
  imports: [CommonModule, FormsModule],
  templateUrl: './chart-wishlist-section.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ChartWishlistSectionComponent {
  protected readonly charts = inject(ChartBoardService);

  private readonly boardSignal = signal<ActivityChartBoard | null>(null);
  private readonly canManageSignal = signal(false);

  @Input({ required: true }) set board(value: ActivityChartBoard) {
    this.boardSignal.set(value);
  }

  @Input() set canManage(value: boolean) {
    this.canManageSignal.set(value);
  }

  protected readonly requests = computed(() => this.boardSignal()?.wishlist.requests ?? []);
  protected readonly pendingCount = computed(() => this.boardSignal()?.wishlist.pendingCount ?? 0);
  protected readonly bosses = computed(() => this.boardSignal()?.bosses ?? []);
  protected readonly canManageBoard = computed(() => this.canManageSignal());

  /** Only the pending queue is ordered. A fulfilled request is settled and has no place in one. */
  protected readonly pending = computed(() =>
    this.requests().filter(request => request.status === 'Pending'));

  // ---- the submit form ----
  //
  // Opens on "anywhere" because both readings are real: somebody wants a specific drop out of one
  // zone, somebody else just wants the item wherever it turns up. An empty string is what the server
  // turns into a null boss.

  protected readonly formOpen = signal(false);
  protected readonly formBoss = signal('');
  protected readonly formItemName = signal('');
  protected formQuantity = 1;
  protected formNotes = '';

  protected toggleForm(): void {
    const opening = !this.formOpen();
    this.resetForm();
    this.formOpen.set(opening);
  }

  private resetForm(): void {
    this.formBoss.set('');
    this.formItemName.set('');
    this.formQuantity = 1;
    this.formNotes = '';
  }

  protected themeKeyFor(boss: string | null | undefined): string {
    if (!boss) {
      return '';
    }
    const card = this.bosses().find(
      candidate => candidate.boss.toLowerCase() === boss.trim().toLowerCase());
    return card ? `is-${card.themeKey}` : '';
  }

  protected async submitForm(): Promise<void> {
    const board = this.boardSignal();
    if (!board || !this.formItemName().trim()) {
      return;
    }

    try {
      await this.charts.addWishlistRequest(board.linkshellId, board.board, {
        boss: this.formBoss() || null,
        itemName: this.formItemName().trim(),
        quantity: Number.isFinite(this.formQuantity) ? Math.max(1, this.formQuantity) : 1,
        notes: this.formNotes.trim() || null
      });
      this.resetForm();
      this.formOpen.set(false);
    } catch {
      // Surfaced by the service through the shared action banner.
    }
  }

  // ---- acting on a row ----

  protected async withdraw(request: ActivityChartWishlistRequest): Promise<void> {
    try {
      await this.charts.withdrawWishlistRequest(request.id);
    } catch {
      // Surfaced by the service.
    }
  }

  protected async toggleFulfilled(request: ActivityChartWishlistRequest): Promise<void> {
    try {
      await this.charts.setWishlistStatus(
        request.id, request.status === 'Fulfilled' ? 'Pending' : 'Fulfilled');
    } catch {
      // Surfaced by the service.
    }
  }

  /**
   * Moves one request a single place up or down.
   *
   * Sends the COMPLETE ordered id list with the pair swapped, because the endpoint is set-wise: it
   * rewrites a board's queue rather than nudging one row, so two officers reordering at once cannot
   * interleave into an order neither of them chose.
   */
  protected async move(request: ActivityChartWishlistRequest, step: -1 | 1): Promise<void> {
    const board = this.boardSignal();
    const queue = this.pending();
    const at = queue.findIndex(row => row.id === request.id);
    const swapWith = at + step;
    if (!board || at < 0 || swapWith < 0 || swapWith >= queue.length) {
      return;
    }

    const orderedIds = queue.map(row => row.id);
    [orderedIds[at], orderedIds[swapWith]] = [orderedIds[swapWith], orderedIds[at]];

    try {
      await this.charts.reorderWishlist(board.linkshellId, board.board, orderedIds);
    } catch {
      // Surfaced by the service.
    }
  }

  protected canMove(request: ActivityChartWishlistRequest, step: -1 | 1): boolean {
    const queue = this.pending();
    const at = queue.findIndex(row => row.id === request.id);
    return at >= 0 && at + step >= 0 && at + step < queue.length;
  }
}
