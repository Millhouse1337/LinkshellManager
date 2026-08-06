import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Input, ViewChild, inject, signal } from '@angular/core';
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
  styleUrl: './auctions-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AuctionsPanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());
  private readonly numberFormatter = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 });

  @Input({ required: true }) selectedLinkshellId!: number;

  protected readonly auctionFormModel: ActivityCreateAuctionInput = {
    linkshellId: 0,
    title: '',
    startTimeLocal: '',
    endTimeLocal: '',
    items: [{ id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '', sourceItemId: null, gilAmount: null }]
  };
  // Per-item source mode driving the form UI: inventory pick, free-text
  // external, or a gil sale (treasury gil sold for DKP).
  protected auctionItemSourceMode: ('inventory' | 'external' | 'gil')[] = ['external'];
  protected auctionGilAmountDrafts: string[] = [''];
  protected readonly auctionBidDrafts: Record<number, number | null> = {};
  protected readonly expandedAuctionBidItems: Record<number, boolean> = {};
  protected isAuctionFormOpen = false;
  protected isSubmittingAuction = false;
  protected editingAuctionId: number | null = null;

  // Anchor element so we can scrollIntoView when the form opens on phone
  // (where it renders inline at the bottom of the panel rather than as
  // a floating modal). On desktop the modal is fixed-position and this
  // call is a harmless no-op visually.
  @ViewChild('auctionFormAnchor') private auctionFormAnchor?: ElementRef<HTMLElement>;
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

  // The CanLockAuctions permission for the selected linkshell (gates the toggle).
  protected canLockAuctions(): boolean {
    const link = this.activity.overview()?.linkshells?.find(l => l.id === this.selectedLinkshellId);
    return link?.permissions?.canLockAuctions === true;
  }

  // Leadership has frozen bidding. The canonical flag lives on the linkshell
  // (overview), so it's correct even with ZERO auctions on the board. Fall back
  // to the per-auction flag the list endpoint stamps when the overview is stale.
  protected auctionsLocked(): boolean {
    const link = this.activity.overview()?.linkshells?.find(l => l.id === this.selectedLinkshellId);
    if (link?.auctionsLocked !== undefined) {
      return link.auctionsLocked === true;
    }
    const auctions = this.auctions();
    return auctions.length > 0 && auctions[0].auctionsLocked === true;
  }

  protected async toggleAuctionsLock(): Promise<void> {
    if (!this.selectedLinkshellId) {
      return;
    }
    await this.activity.setAuctionsLock(this.selectedLinkshellId, !this.auctionsLocked());
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
    this.auctionFormModel.items = [{ id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '', sourceItemId: null, gilAmount: null }];
    this.auctionItemSourceMode = ['external'];
    this.auctionGilAmountDrafts = [''];
    this.scrollFormIntoView();
  }

  // Phone: after the form's `@if` block has rendered, scroll it into
  // view so the user lands on it instead of having to scroll down the
  // long auction board to find the inline form. requestAnimationFrame
  // waits one frame for Angular to render the new DOM nodes; without
  // it, ViewChild is still undefined.
  private scrollFormIntoView(): void {
    requestAnimationFrame(() => {
      this.auctionFormAnchor?.nativeElement?.scrollIntoView({
        behavior: 'smooth',
        block: 'start'
      });
    });
  }

  protected openEditAuctionForm(auction: {
    id: number;
    linkshellId: number;
    title?: string | null;
    startTime?: string | null;
    endTime?: string | null;
    items: {
      id: number;
      itemName?: string | null;
      itemType?: string | null;
      startingBidDkp?: number | null;
      notes?: string | null;
      sourceItemId?: number | null;
      gilAmount?: number | null;
    }[];
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
          sourceItemId: item.sourceItemId ?? null,
          gilAmount: item.gilAmount ?? null
        }))
      : [{ id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '', sourceItemId: null, gilAmount: null }];
    this.auctionItemSourceMode = this.auctionFormModel.items.map(item =>
      item.gilAmount != null ? 'gil' : item.sourceItemId != null ? 'inventory' : 'external');
    this.auctionGilAmountDrafts = this.auctionFormModel.items.map(item => this.formatGilAmount(item.gilAmount));
    this.scrollFormIntoView();
  }

  protected closeAuctionForm(): void {
    this.isAuctionFormOpen = false;
    this.editingAuctionId = null;
  }

  protected addAuctionFormItem(): void {
    this.auctionFormModel.items = [
      ...this.auctionFormModel.items,
      { id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '', sourceItemId: null, gilAmount: null }
    ];
    this.auctionItemSourceMode = [...this.auctionItemSourceMode, 'external'];
    this.auctionGilAmountDrafts = [...this.auctionGilAmountDrafts, ''];
  }

  protected onAuctionItemGilAmountInput(index: number, rawValue: string | number | null): void {
    const item = this.auctionFormModel.items[index];
    if (!item) return;

    const digits = String(rawValue ?? '').replace(/\D/g, '');
    if (!digits) {
      item.gilAmount = null;
      this.auctionGilAmountDrafts[index] = '';
      return;
    }

    const numericValue = Number(digits);
    item.gilAmount = Number.isFinite(numericValue) ? numericValue : null;
    this.auctionGilAmountDrafts[index] = this.formatGilAmount(item.gilAmount);
  }

  private formatGilAmount(value: number | null | undefined): string {
    if (value == null || !Number.isFinite(value)) {
      return '';
    }

    return this.numberFormatter.format(value);
  }

  protected inventoryItemsForAuctionForm(): { id: number; itemName: string; itemType?: string | null; quantity: number }[] {
    const linkshellId = this.auctionFormModel.linkshellId;
    const primary = this.activity.overview()?.primaryLinkshell;
    if (primary && primary.id === linkshellId) {
      return primary.items ?? [];
    }
    return [];
  }

  protected onAuctionItemSourceModeChange(index: number, mode: 'inventory' | 'external' | 'gil'): void {
    this.auctionItemSourceMode[index] = mode;
    const item = this.auctionFormModel.items[index];
    if (!item) return;
    // Reset cross-mode fields so a switched row never carries stale values.
    item.sourceItemId = null;
    item.itemName = '';
    item.itemType = mode === 'gil' ? 'Gil' : '';
    item.gilAmount = null;
    this.auctionGilAmountDrafts[index] = '';
  }

  protected isGilItem(index: number): boolean {
    return this.auctionItemSourceMode[index] === 'gil';
  }

  protected isInventoryItem(index: number): boolean {
    return this.auctionItemSourceMode[index] === 'inventory';
  }

  // Two-step Source / Source Type views over the single `auctionItemSourceMode`
  // source of truth: External = not from inventory; Internal = from the linkshell
  // (Item = inventory pick, Gil = treasury gil sold for DKP).
  protected auctionItemSource(index: number): 'external' | 'internal' {
    return this.auctionItemSourceMode[index] === 'external' ? 'external' : 'internal';
  }

  protected auctionItemSourceType(index: number): 'item' | 'gil' {
    return this.auctionItemSourceMode[index] === 'gil' ? 'gil' : 'item';
  }

  protected onAuctionItemSourceChange(index: number, source: 'external' | 'internal'): void {
    if (source === 'external') {
      this.onAuctionItemSourceModeChange(index, 'external');
    } else {
      // Internal defaults to Item (inventory); keep Gil if it was already gil.
      this.onAuctionItemSourceModeChange(index, this.auctionItemSourceMode[index] === 'gil' ? 'gil' : 'inventory');
    }
  }

  protected onAuctionItemSourceTypeChange(index: number, type: 'item' | 'gil'): void {
    this.onAuctionItemSourceModeChange(index, type === 'gil' ? 'gil' : 'inventory');
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
    this.auctionItemSourceMode = this.auctionItemSourceMode.filter((_, itemIndex) => itemIndex !== index);
    this.auctionGilAmountDrafts = this.auctionGilAmountDrafts.filter((_, itemIndex) => itemIndex !== index);
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
        items: this.auctionFormModel.items.map<ActivityAuctionItemInput>((item, index) => {
          const isGil = this.auctionItemSourceMode[index] === 'gil';
          return {
            id: item.id,
            itemName: item.itemName.trim(),
            itemType: isGil ? 'Gil' : (item.itemType?.trim() || null),
            startingBidDkp: item.startingBidDkp ?? 0,
            notes: item.notes?.trim() || null,
            sourceItemId: isGil ? null : (item.sourceItemId ?? null),
            gilAmount: isGil ? (item.gilAmount ?? null) : null
          };
        })
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
    // The starting bid is a hard floor; once there's a high bid the next one must
    // also beat it by at least 1.
    return Math.max(item.startingBidDkp ?? 0, highest + 1, 1);
  }

  protected isCurrentUserWinning(item: { currentHighestBidderAppUserId?: string | null; currentHighestBid?: number | null }): boolean {
    const me = this.activity.overview()?.appUser?.id;
    return Boolean(me && item.currentHighestBidderAppUserId && item.currentHighestBidderAppUserId === me);
  }

  // Viewer's available DKP in the selected linkshell. The list endpoint
  // stamps the same value on every returned auction, so the first one is
  // representative; null until auctions load.
  protected viewerAvailableDkp(): number | null {
    const auctions = this.auctions();
    for (const a of auctions) {
      if (a.availableDkp != null) {
        return a.availableDkp;
      }
    }
    return null;
  }

  protected viewerTotalDkp(): number | null {
    const id = this.selectedLinkshellId;
    const link = this.activity.overview()?.linkshells?.find(l => l.id === id);
    return link?.linkshellDkp ?? null;
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
    const variants: ('a' | 'b' | 'c' | 'd')[] = ['a', 'b', 'c', 'd'];
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

  // Two-stage inline confirmation for ending an auction early.
  // window.confirm() is suppressed in the Discord Activity iframe (no
  // `allow-modals`), so a first click flags the auction and the
  // template swaps the End button out for a Confirm/Keep pair. Second
  // click on Confirm calls the API.
  protected readonly pendingEndAuctionId = signal<number | null>(null);

  protected requestEndAuction(auctionId: number): void {
    this.pendingEndAuctionId.set(auctionId);
  }

  protected cancelEndAuction(): void {
    this.pendingEndAuctionId.set(null);
  }

  protected async endAuction(auctionId: number): Promise<void> {
    if (!this.selectedLinkshellId) {
      this.pendingEndAuctionId.set(null);
      return;
    }
    this.pendingEndAuctionId.set(null);
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

  protected async submitAuctionBid(item: {
    id: number;
    startingBidDkp?: number | null;
    currentHighestBid?: number | null;
  }): Promise<void> {
    if (!this.selectedLinkshellId) {
      return;
    }

    const bidAmount = this.auctionBidDrafts[item.id];
    const minimumBid = this.itemMinBid(item);
    if (!bidAmount || bidAmount < minimumBid) {
      this.activity.actionError.set(`Enter a bid of at least ${minimumBid} DKP.`);
      this.activity.actionMessage.set(null);
      return;
    }

    await this.activity.placeAuctionBid(item.id, bidAmount, this.selectedLinkshellId);
    this.auctionBidDrafts[item.id] = null;
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
