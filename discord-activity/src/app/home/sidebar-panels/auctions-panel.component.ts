import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, Input, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityAuctionItemInput,
  ActivityCreateAuctionInput,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import { formatElapsed, parseDate } from '../sidebar-panel.helpers';

@Component({
  selector: 'app-auctions-panel',
  imports: [CommonModule, FormsModule],
  templateUrl: './auctions-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AuctionsPanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());

  @Input({ required: true }) selectedLinkshellId!: number;

  protected readonly auctionFormModel: ActivityCreateAuctionInput = {
    linkshellId: 0,
    title: '',
    startTimeLocal: '',
    endTimeLocal: '',
    items: [{ id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '', sourceItemId: null }]
  };
  protected auctionItemFromInventory: boolean[] = [true];
  protected readonly auctionBidDrafts: Record<number, number | null> = {};
  protected readonly expandedAuctionBidItems: Record<number, boolean> = {};
  protected isAuctionFormOpen = false;
  protected isSubmittingAuction = false;
  protected editingAuctionId: number | null = null;
  protected readonly auctionsView = signal<'active' | 'history'>('active');

  // Auction history is rendered as a flat table — one row per item across
  // all closed auctions, paginated. Page is a signal so OnPush picks up
  // Prev/Next clicks. Page size chosen to roughly fill the panel without
  // an inner scrollbar; tweak if the layout changes.
  protected readonly auctionHistoryPage = signal(0);
  protected readonly auctionHistoryPageSize = 20;

  // Active-auctions table pagination. Page is by *auction* (not item row)
  // so a multi-item auction never gets split across pages — keeps the
  // grouped header rendering consistent.
  protected readonly auctionsActivePage = signal(0);
  protected readonly auctionsActivePageSize = 15;

  protected closingAuctionId: number | null = null;
  protected auctionCloseDelivered: Record<number, boolean> = {};

  public constructor() {
    const intervalId = window.setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => window.clearInterval(intervalId));
  }

  protected selectedLinkshell() {
    const selectedId = this.selectedLinkshellId;
    const primary = this.activity.overview()?.primaryLinkshell;

    if (primary && primary.id === selectedId) {
      return {
        id: primary.id,
        name: primary.name,
        memberCount: primary.memberCount,
        details: primary.details,
        status: 'Active',
        members: primary.members
      };
    }

    return this.activity.linkshellDetail();
  }

  protected primaryLinkshellId(): number | null {
    return this.activity.overview()?.appUser?.primaryLinkshellId ?? this.activity.overview()?.primaryLinkshell?.id ?? null;
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    const memberships = this.activity.overview()?.linkshells ?? [];
    const membership = memberships.find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
  }

  protected openCreateAuctionForm(): void {
    const linkshellId = this.selectedLinkshellId || this.primaryLinkshellId() || 0;
    if (!linkshellId) {
      return;
    }

    this.activity.clearActionState();
    this.isAuctionFormOpen = true;
    this.editingAuctionId = null;
    this.auctionFormModel.linkshellId = linkshellId;
    this.auctionFormModel.title = '';
    this.auctionFormModel.startTimeLocal = '';
    this.auctionFormModel.endTimeLocal = '';
    this.auctionFormModel.items = [{ id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '', sourceItemId: null }];
    this.auctionItemFromInventory = [true];
  }

  protected openEditAuctionForm(auction: {
    id: number;
    linkshellId: number;
    title?: string | null;
    startTime?: string | null;
    endTime?: string | null;
    items: Array<{
      id: number;
      itemName?: string | null;
      itemType?: string | null;
      startingBidDkp?: number | null;
      notes?: string | null;
      sourceItemId?: number | null;
    }>;
  }): void {
    this.activity.clearActionState();
    this.isAuctionFormOpen = true;
    this.editingAuctionId = auction.id;
    this.auctionFormModel.linkshellId = auction.linkshellId;
    this.auctionFormModel.title = auction.title ?? '';
    this.auctionFormModel.startTimeLocal = this.activity.toViewerLocalInputValue(auction.startTime);
    this.auctionFormModel.endTimeLocal = this.activity.toViewerLocalInputValue(auction.endTime);
    this.auctionFormModel.items = auction.items.length > 0
      ? auction.items.map(item => ({
          id: item.id,
          itemName: item.itemName ?? '',
          itemType: item.itemType ?? '',
          startingBidDkp: item.startingBidDkp ?? 0,
          notes: item.notes ?? '',
          sourceItemId: item.sourceItemId ?? null
        }))
      : [{ id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '', sourceItemId: null }];
    this.auctionItemFromInventory = this.auctionFormModel.items.map(item => item.sourceItemId != null);
  }

  protected closeAuctionForm(): void {
    this.isAuctionFormOpen = false;
    this.editingAuctionId = null;
  }

  protected addAuctionFormItem(): void {
    this.auctionFormModel.items = [
      ...this.auctionFormModel.items,
      { id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '', sourceItemId: null }
    ];
    this.auctionItemFromInventory = [...this.auctionItemFromInventory, true];
  }

  protected inventoryItemsForAuctionForm(): Array<{ id: number; itemName: string; itemType?: string | null; quantity: number }> {
    const linkshellId = this.auctionFormModel.linkshellId;
    const primary = this.activity.overview()?.primaryLinkshell;
    if (primary && primary.id === linkshellId) {
      return primary.items ?? [];
    }
    return [];
  }

  protected onAuctionItemSourceModeChange(index: number, mode: 'inventory' | 'external'): void {
    this.auctionItemFromInventory[index] = mode === 'inventory';
    const item = this.auctionFormModel.items[index];
    if (!item) return;
    item.sourceItemId = null;
    item.itemName = '';
    item.itemType = '';
  }

  protected onAuctionItemInventoryPick(index: number, inventoryItemId: number | null): void {
    const item = this.auctionFormModel.items[index];
    if (!item) return;
    if (inventoryItemId == null) {
      item.sourceItemId = null;
      item.itemName = '';
      item.itemType = '';
      return;
    }
    const match = this.inventoryItemsForAuctionForm().find(inv => inv.id === inventoryItemId);
    item.sourceItemId = inventoryItemId;
    item.itemName = match?.itemName ?? '';
    item.itemType = match?.itemType ?? '';
  }

  protected removeAuctionFormItem(index: number): void {
    if (this.auctionFormModel.items.length <= 1) {
      return;
    }

    this.auctionFormModel.items = this.auctionFormModel.items.filter((_, itemIndex) => itemIndex !== index);
    this.auctionItemFromInventory = this.auctionItemFromInventory.filter((_, itemIndex) => itemIndex !== index);
  }

  protected getAuctionBidDraft(itemId: number): number | null {
    return this.auctionBidDrafts[itemId] ?? null;
  }

  protected toggleAuctionBids(itemId: number): void {
    const nextState = !this.expandedAuctionBidItems[itemId];
    this.expandedAuctionBidItems[itemId] = nextState;

    if (nextState) {
      void this.activity.loadAuctionItemBids(itemId);
    }
  }

  protected async submitAuctionForm(): Promise<void> {
    if (!this.auctionFormModel.linkshellId) {
      return;
    }

    this.isSubmittingAuction = true;

    try {
      const payload: ActivityCreateAuctionInput = {
        linkshellId: this.auctionFormModel.linkshellId,
        title: this.auctionFormModel.title.trim(),
        startTimeLocal: this.auctionFormModel.startTimeLocal?.trim() || null,
        endTimeLocal: this.auctionFormModel.endTimeLocal?.trim() || null,
        items: this.auctionFormModel.items.map<ActivityAuctionItemInput>(item => ({
          id: item.id,
          itemName: item.itemName.trim(),
          itemType: item.itemType?.trim() || null,
          startingBidDkp: item.startingBidDkp ?? 0,
          notes: item.notes?.trim() || null
        }))
      };

      if (this.editingAuctionId) {
        await this.activity.updateAuction(this.editingAuctionId, payload);
      } else {
        await this.activity.createAuction(payload);
      }

      this.closeAuctionForm();
    } finally {
      this.isSubmittingAuction = false;
    }
  }

  protected auctions() {
    return this.activity.auctions();
  }

  protected auctionHistory() {
    return this.activity.auctionHistory();
  }

  protected pagedActiveAuctions() {
    const all = this.auctions();
    const size = this.auctionsActivePageSize;
    const totalPages = Math.max(1, Math.ceil(all.length / size));
    if (this.auctionsActivePage() >= totalPages) {
      this.auctionsActivePage.set(totalPages - 1);
    }
    const start = this.auctionsActivePage() * size;
    return all.slice(start, start + size);
  }

  protected auctionsActivePageCount(): number {
    return Math.max(1, Math.ceil(this.auctions().length / this.auctionsActivePageSize));
  }

  protected auctionsActiveNextPage(): void {
    const next = this.auctionsActivePage() + 1;
    if (next < this.auctionsActivePageCount()) this.auctionsActivePage.set(next);
  }

  protected auctionsActivePrevPage(): void {
    const current = this.auctionsActivePage();
    if (current > 0) this.auctionsActivePage.set(current - 1);
  }

  protected auctionHistoryRows(): {
    auctionId: number;
    auctionTitle: string;
    closedAt: string;
    windowLabel: string;
    item: any;
  }[] {
    const rows: { auctionId: number; auctionTitle: string; closedAt: string; windowLabel: string; item: any }[] = [];
    for (const h of this.auctionHistory()) {
      const title = h.title || 'Auction history';
      const windowLabel = this.auctionTimeWindowLabel(h);
      for (const item of h.items) {
        rows.push({
          auctionId: h.id,
          auctionTitle: title,
          closedAt: h.closedAt,
          windowLabel,
          item
        });
      }
    }
    return rows;
  }

  protected pagedAuctionHistoryRows(): ReturnType<AuctionsPanelComponent['auctionHistoryRows']> {
    const all = this.auctionHistoryRows();
    const size = this.auctionHistoryPageSize;
    const totalPages = Math.max(1, Math.ceil(all.length / size));
    // Clamp current page so deletes that shrink the dataset can't strand
    // the user on a now-empty page.
    if (this.auctionHistoryPage() >= totalPages) {
      this.auctionHistoryPage.set(totalPages - 1);
    }
    const start = this.auctionHistoryPage() * size;
    return all.slice(start, start + size);
  }

  protected auctionHistoryPageCount(): number {
    return Math.max(1, Math.ceil(this.auctionHistoryRows().length / this.auctionHistoryPageSize));
  }

  protected auctionHistoryNextPage(): void {
    const next = this.auctionHistoryPage() + 1;
    if (next < this.auctionHistoryPageCount()) this.auctionHistoryPage.set(next);
  }

  protected auctionHistoryPrevPage(): void {
    const current = this.auctionHistoryPage();
    if (current > 0) this.auctionHistoryPage.set(current - 1);
  }

  protected auctionBids(itemId: number) {
    return this.activity.auctionBids()[itemId] ?? [];
  }

  protected auctionTimeWindowLabel(auction: { startedAt?: string | null; startTime?: string | null; endTime?: string | null }): string {
    const actualStart = this.activity.formatDateTime(auction.startedAt);
    const scheduledStart = this.activity.formatDateTime(auction.startTime);
    const endTime = this.activity.formatDateTime(auction.endTime);

    if (actualStart) {
      return endTime ? `Started ${actualStart} • Ends ${endTime}` : `Started ${actualStart}`;
    }

    if (scheduledStart && endTime) {
      return `Scheduled ${scheduledStart} • Ends ${endTime}`;
    }

    return scheduledStart || endTime || 'Timer unavailable';
  }

  protected setAuctionsView(view: 'active' | 'history'): void {
    this.auctionsView.set(view);
  }

  protected auctionState(auction: { startedAt?: string | null; startTime?: string | null; endTime?: string | null; status: string }): 'live' | 'ending' | 'ended' | 'scheduled' {
    const normalized = (auction.status || '').toLowerCase();
    if (normalized === 'closed' || normalized === 'archived' || normalized === 'ended') {
      return 'ended';
    }
    const endMs = parseDate(auction.endTime);
    const startMs = parseDate(auction.startedAt || auction.startTime);
    const now = this.now();
    if (endMs && endMs <= now) {
      return 'ended';
    }
    if (startMs && now < startMs) {
      return 'scheduled';
    }
    if (endMs && endMs - now < 60 * 60 * 1000) {
      return 'ending';
    }
    return 'live';
  }

  protected auctionStatusLabel(auction: { status: string; startedAt?: string | null; startTime?: string | null; endTime?: string | null }): string {
    switch (this.auctionState(auction)) {
      case 'ending': return 'Ending soon';
      case 'live': return 'Live';
      case 'ended': return 'Ended';
      case 'scheduled': return 'Scheduled';
    }
  }

  protected auctionStatusTagClass(auction: { status: string; startedAt?: string | null; startTime?: string | null; endTime?: string | null }): string {
    switch (this.auctionState(auction)) {
      case 'ending': return 'warning';
      case 'live': return 'success';
      case 'ended': return '';
      case 'scheduled': return 'warning';
    }
  }

  protected auctionRemainingLabel(auction: { startedAt?: string | null; startTime?: string | null; endTime?: string | null; status: string }): string {
    const state = this.auctionState(auction);
    if (state === 'ended') return 'Ended';
    if (state === 'scheduled') return 'Starts in';
    return 'Remaining';
  }

  protected auctionTimerValue(auction: { startedAt?: string | null; startTime?: string | null; endTime?: string | null; status: string }): string {
    const state = this.auctionState(auction);
    if (state === 'ended') return 'Ended';
    const startMs = parseDate(auction.startedAt || auction.startTime);
    const endMs = parseDate(auction.endTime);
    const now = this.now();
    if (state === 'scheduled' && startMs) {
      return formatElapsed(Math.max(0, startMs - now));
    }
    if (endMs) {
      return formatElapsed(Math.max(0, endMs - now));
    }
    return '—';
  }

  protected itemDotClass(itemType?: string | null): 'crafting' | 'loot' | 'drop' | 'other' {
    const normalized = (itemType || '').trim().toLowerCase();
    if (normalized === 'crafting') return 'crafting';
    if (normalized === 'loot') return 'loot';
    if (normalized === 'drop') return 'drop';
    return 'other';
  }

  protected itemMinBid(item: { startingBidDkp?: number | null; currentHighestBid?: number | null }): number {
    const highest = item.currentHighestBid ?? 0;
    if (highest > 0) return highest + 1;
    return item.startingBidDkp ?? 0;
  }

  protected isCurrentUserWinning(item: { currentHighestBidderAppUserId?: string | null; currentHighestBid?: number | null }): boolean {
    const me = this.activity.overview()?.appUser?.id;
    return Boolean(me && item.currentHighestBidderAppUserId && item.currentHighestBidderAppUserId === me);
  }

  protected auctionLiveCount(): number {
    return this.auctions().filter(auction => {
      const state = this.auctionState(auction);
      return state === 'live' || state === 'ending';
    }).length;
  }

  protected auctionTotalItems(): number {
    return this.auctions().reduce((sum, auction) => sum + auction.items.length, 0);
  }

  protected auctionTotalBids(): number {
    return this.auctions().reduce((sum, auction) => sum + auction.items.reduce((s, item) => s + (item.bidCount ?? 0), 0), 0);
  }

  protected bidderAvatarVariant(name?: string | null): 'a' | 'b' | 'c' | 'd' {
    const raw = (name ?? '').trim();
    if (!raw) return 'a';
    let hash = 0;
    for (let i = 0; i < raw.length; i++) {
      hash = (hash * 31 + raw.charCodeAt(i)) & 0xffffffff;
    }
    const variants: Array<'a' | 'b' | 'c' | 'd'> = ['a', 'b', 'c', 'd'];
    return variants[Math.abs(hash) % variants.length];
  }

  protected bidderInitials(name?: string | null): string {
    const raw = (name ?? '').trim();
    if (!raw) return '??';
    const parts = raw.split(/\s+/).filter(Boolean);
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[1][0]).toUpperCase();
  }

  protected timeAgo(value?: string | null): string {
    const ms = parseDate(value ?? null);
    if (!ms) return '';
    const diff = Math.max(0, this.now() - ms);
    const seconds = Math.floor(diff / 1000);
    if (seconds < 60) return `${seconds}s ago`;
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    return `${days}d ago`;
  }

  protected async startAuction(auctionId: number): Promise<void> {
    if (!this.selectedLinkshellId) {
      return;
    }

    await this.activity.startAuction(auctionId, this.selectedLinkshellId);
  }

  protected async endAuction(auctionId: number): Promise<void> {
    if (!this.selectedLinkshellId) return;
    if (!window.confirm('End this auction now? Bidding will stop immediately. You will then be able to close it and deliver the won items.')) {
      return;
    }
    try {
      await this.activity.endAuction(auctionId, this.selectedLinkshellId);
    } catch {
      // message set by service
    }
  }

  protected beginCloseAuction(auctionId: number): void {
    const auction = this.auctions().find(a => a.id === auctionId);
    if (!auction) return;
    this.closingAuctionId = auctionId;
    this.auctionCloseDelivered = {};
    for (const item of auction.items) {
      if (item.currentHighestBidderAppUserId) {
        this.auctionCloseDelivered[item.id] = true;
      }
    }
  }

  protected cancelCloseAuction(): void {
    this.closingAuctionId = null;
    this.auctionCloseDelivered = {};
  }

  protected toggleAuctionItemDelivered(itemId: number, value: boolean): void {
    this.auctionCloseDelivered = { ...this.auctionCloseDelivered, [itemId]: value };
  }

  protected async confirmCloseAuction(auctionId: number): Promise<void> {
    if (!this.selectedLinkshellId) return;
    const deliveredItemIds = Object.entries(this.auctionCloseDelivered)
      .filter(([, delivered]) => delivered)
      .map(([id]) => Number(id));
    try {
      await this.activity.closeAuction(auctionId, this.selectedLinkshellId, deliveredItemIds);
      this.closingAuctionId = null;
      this.auctionCloseDelivered = {};
    } catch {
      // message set by service
    }
  }

  protected async submitAuctionBid(itemId: number): Promise<void> {
    if (!this.selectedLinkshellId) {
      return;
    }

    const bidAmount = this.auctionBidDrafts[itemId];
    if (!bidAmount || bidAmount <= 0) {
      this.activity.actionError.set('Enter a bid greater than 0.');
      this.activity.actionMessage.set(null);
      return;
    }

    await this.activity.placeAuctionBid(itemId, bidAmount, this.selectedLinkshellId);
    this.auctionBidDrafts[itemId] = null;
  }

  protected async markAuctionHistoryItemReceived(itemId: number): Promise<void> {
    if (!this.selectedLinkshellId) {
      return;
    }

    await this.activity.markAuctionHistoryItemReceived(itemId, this.selectedLinkshellId);
  }

  protected async undoAuctionHistoryItem(itemId: number): Promise<void> {
    if (!this.selectedLinkshellId) {
      return;
    }

    await this.activity.undoAuctionHistoryItem(itemId, this.selectedLinkshellId);
  }
}
