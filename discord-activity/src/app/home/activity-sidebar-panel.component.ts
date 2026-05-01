import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, Input, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityAuctionItemInput,
  ActivityCreateAuctionInput,
  ActivityCreateLinkshellInput,
  ActivityLinkshellRole,
  DiscordActivityService
} from '../discord/discord-activity.service';

@Component({
  selector: 'app-activity-sidebar-panel',
  imports: [CommonModule, FormsModule],
  templateUrl: './activity-sidebar-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ActivitySidebarPanelComponent {
  private static readonly curatedTimeZones = [
    'UTC',
    'America/New_York',
    'America/Chicago',
    'America/Denver',
    'America/Los_Angeles',
    'America/Phoenix',
    'America/Anchorage',
    'Pacific/Honolulu',
    'America/Toronto',
    'America/Vancouver',
    'America/Mexico_City',
    'America/Sao_Paulo',
    'America/Argentina/Buenos_Aires',
    'Europe/London',
    'Europe/Dublin',
    'Europe/Paris',
    'Europe/Berlin',
    'Europe/Madrid',
    'Europe/Rome',
    'Europe/Warsaw',
    'Europe/Helsinki',
    'Europe/Athens',
    'Europe/Istanbul',
    'Europe/Kyiv',
    'Africa/Johannesburg',
    'Asia/Dubai',
    'Asia/Kolkata',
    'Asia/Dhaka',
    'Asia/Bangkok',
    'Asia/Singapore',
    'Asia/Manila',
    'Asia/Hong_Kong',
    'Asia/Taipei',
    'Asia/Seoul',
    'Asia/Tokyo',
    'Australia/Perth',
    'Australia/Adelaide',
    'Australia/Sydney',
    'Pacific/Auckland'
  ] as const;

  @Input() visibleSections?: readonly string[];
  protected readonly activity = inject(DiscordActivityService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());

  protected showSection(name: string): boolean {
    return !this.visibleSections || this.visibleSections.includes(name);
  }
  protected readonly profileModel = {
    characterName: '',
    timeZone: ''
  };
  protected editingLinkshellId: number | null = null;
  protected readonly createLinkshellModel: ActivityCreateLinkshellInput = {
    name: '',
    details: ''
  };
  protected readonly auctionFormModel: ActivityCreateAuctionInput = {
    linkshellId: 0,
    title: '',
    startTimeLocal: '',
    endTimeLocal: '',
    items: [{ id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '', sourceItemId: null }]
  };
  protected auctionItemFromInventory: boolean[] = [true];

  protected inviteSearchTerm = '';
  protected inviteLinkshellId = 0;
  protected selectedJoinLinkshellId = 0;
  protected selectedLinkshellId = 0;
  protected selectedDkpHistoryAppUserId = '';
  protected dkpSearchTerm = '';
  protected dkpMemberSearchTerm = '';

  // Pagination for the DKP ledger table. Page is a signal so OnPush picks
  // up clicks on the Prev/Next buttons; size is a small constant chosen so
  // a typical screen shows ~one viewport with no scroll inside the table.
  protected readonly dkpPage = signal(0);
  protected readonly dkpPageSize = 15;
  protected isDkpAuditOpen = false;
  protected readonly dkpAuditModel: {
    mode: 'Adjust' | 'Misc';
    targetAppUserId: string;
    relatedLedgerEntryId: number | null;
    amount: number | null;
    reason: string;
  } = {
    mode: 'Misc',
    targetAppUserId: '',
    relatedLedgerEntryId: null,
    amount: null,
    reason: ''
  };
  protected readonly auctionBidDrafts: Record<number, number | null> = {};
  protected readonly expandedAuctionBidItems: Record<number, boolean> = {};
  protected memberSearchTerm = '';
  protected memberRoleFilter: 'all' | 'leader' | 'officer' | 'member' = 'all';
  protected isCreateLinkshellOpen = false;
  protected isSubmittingLinkshell = false;
  protected isAuctionFormOpen = false;
  protected isSubmittingAuction = false;
  protected editingAuctionId: number | null = null;
  protected readonly auctionsView = signal<'active' | 'history'>('active');
  protected readonly browserTimeZone = this.resolveBrowserTimeZone();
  protected readonly timeZoneOptions = this.resolveTimeZoneOptions();
  private profileSeed = '';
  private participantInviteSeed = '';
  private historySeed = '';

  public constructor()
  {
    const intervalId = window.setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => window.clearInterval(intervalId));

    effect(() => {
      const appUser = this.activity.overview()?.appUser;
      if (!appUser) {
        return;
      }

      const nextCharacterName = appUser.characterName ?? '';
      const nextTimeZone = appUser.timeZone ?? this.browserTimeZone;
      const nextSeed = `${appUser.id}|${nextCharacterName}|${nextTimeZone}`;

      if (nextSeed === this.profileSeed) {
        return;
      }

      this.profileSeed = nextSeed;
      this.profileModel.characterName = nextCharacterName;
      this.profileModel.timeZone = nextTimeZone;
    });

    effect(() => {
      const linkshellId = this.inviteTargetLinkshellId();
      const participantIds = this.activity.participants().map(participant => participant.id).sort();
      const eligibilitySeed = this.inviteEligibilitySeed(linkshellId);
      const canUseShortcutInvites = linkshellId > 0 && this.canManageLinkshell(linkshellId);

      if (!canUseShortcutInvites || participantIds.length === 0) {
        this.participantInviteSeed = '';
        this.activity.clearParticipantInviteCandidates();
        return;
      }

      const nextSeed = `${linkshellId}|${participantIds.join(',')}|${eligibilitySeed}`;
      if (nextSeed === this.participantInviteSeed) {
        return;
      }

      this.participantInviteSeed = nextSeed;
      void this.activity.loadParticipantInviteCandidates(linkshellId, participantIds);
    });

    effect(() => {
      const linkshellId = this.inviteTargetLinkshellId();
      const searchTerm = this.inviteSearchTerm.trim();
      const eligibilitySeed = this.inviteEligibilitySeed(linkshellId);
      const canSearchInvites = linkshellId > 0 && this.canManageLinkshell(linkshellId);

      if (!canSearchInvites || searchTerm.length < 2) {
        return;
      }

      void eligibilitySeed;
      void this.activity.searchPlayers(searchTerm, linkshellId);
    });

    effect(() => {
      const canRequestAccess = this.canRequestLinkshellAccess();
      const overviewLoaded = this.activity.overview();

      if (!overviewLoaded || !canRequestAccess) {
        this.selectedJoinLinkshellId = 0;
        this.activity.clearLinkshellSearch();
        return;
      }

      void this.activity.searchLinkshells('');
    });

    effect(() => {
      const availableLinkshells = this.activity.linkshellSearchResults();
      if (availableLinkshells.length === 0) {
        this.selectedJoinLinkshellId = 0;
        return;
      }

      if (!availableLinkshells.some(linkshell => linkshell.id === this.selectedJoinLinkshellId)) {
        this.selectedJoinLinkshellId = availableLinkshells[0].id;
      }
    });

    effect(() => {
      const memberships = this.linkshellMemberships();
      if (memberships.length === 0) {
        this.selectedLinkshellId = 0;
        this.activity.clearLinkshellDetail();
        return;
      }

      const preferredId =
        this.selectedLinkshellId ||
        this.primaryLinkshellId() ||
        memberships[0]?.id ||
        0;

      if (!memberships.some(linkshell => linkshell.id === preferredId)) {
        this.selectedLinkshellId = memberships[0].id;
      } else if (this.selectedLinkshellId === 0) {
        this.selectedLinkshellId = preferredId;
      }
    });

    effect(() => {
      const selectedLinkshellId = this.selectedLinkshellId;
      const memberships = this.linkshellMemberships();

      if (!selectedLinkshellId || !memberships.some(linkshell => linkshell.id === selectedLinkshellId)) {
        this.activity.clearLinkshellDetail();
        this.selectedDkpHistoryAppUserId = '';
        this.activity.clearDkpHistory();
        this.activity.clearAuctionState();
        return;
      }

      void this.activity.loadLinkshellDetail(selectedLinkshellId);
      void this.reloadDkpHistory();
      void this.activity.loadAuctions(selectedLinkshellId);
      void this.activity.loadAuctionHistory(selectedLinkshellId);
    });

    effect(() => {
      const overview = this.activity.overview();
      if (!overview) {
        this.historySeed = '';
        this.activity.historyList.set([]);
        this.activity.clearHistoryDetail();
        return;
      }

      const primaryLinkshellId = overview.appUser?.primaryLinkshellId ?? overview.primaryLinkshell?.id ?? 0;
      const recentHistoryIds = (overview.recentHistory ?? []).map(history => history.id).sort((left, right) => left - right);
      const nextSeed = `${primaryLinkshellId}|${recentHistoryIds.join(',')}`;

      if (nextSeed === this.historySeed) {
        return;
      }

      this.historySeed = nextSeed;
      this.activity.clearHistoryDetail();
      void this.activity.loadHistoryList();
    });
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

  protected linkshellMemberships() {
    return this.activity.overview()?.linkshells ?? [];
  }

  protected appUserId(): string | null {
    return this.activity.overview()?.appUser?.id ?? null;
  }

  protected primaryLinkshellId(): number | null {
    return this.activity.overview()?.appUser?.primaryLinkshellId ?? this.activity.overview()?.primaryLinkshell?.id ?? null;
  }

  protected isManagerMode(): boolean {
    return this.linkshellMemberships().some(link => this.canManageLinkshell(link.id));
  }

  protected isMemberMode(): boolean {
    return !this.isManagerMode();
  }

  protected canCreateLinkshell(): boolean {
    return this.linkshellMemberships().length === 0 || this.isManagerMode();
  }

  protected inviteTargetLinkshellId(): number {
    return (
      this.inviteLinkshellId ||
      this.activity.overview()?.primaryLinkshell?.id ||
      this.activity.overview()?.linkshells?.[0]?.id ||
      0
    );
  }

  protected connectedInviteCandidates() {
    const seen = new Set<string>();
    return this.activity.participantInviteCandidates().filter(candidate => {
      if (seen.has(candidate.appUserId)) return false;
      seen.add(candidate.appUserId);
      return true;
    });
  }

  protected filteredInviteSearchResults() {
    const connectedIds = new Set(
      this.connectedInviteCandidates().map(candidate => candidate.appUserId)
    );
    return this.activity.inviteSearchResults().filter(result => !connectedIds.has(result.id));
  }

  protected canRequestLinkshellAccess(): boolean {
    return this.linkshellMemberships().length === 0;
  }

  protected filteredSelectedLinkshellMembers() {
    const linkshell = this.selectedLinkshell();
    if (!linkshell) {
      return [];
    }

    const normalizedSearch = this.memberSearchTerm.trim().toLowerCase();
    return linkshell.members.filter(member => {
      const matchesRole =
        this.memberRoleFilter === 'all' ||
        (member.rank ?? 'Member').toLowerCase() === this.memberRoleFilter;

      const matchesSearch =
        !normalizedSearch ||
        member.characterName.toLowerCase().includes(normalizedSearch);

      return matchesRole && matchesSearch;
    });
  }

  protected primaryLinkshellActiveEventCount(): number {
    if (!this.selectedLinkshellId) {
      return 0;
    }

    return (this.activity.overview()?.activeEvents ?? []).filter(event => event.linkshellId === this.selectedLinkshellId).length;
  }

  protected canManageMembers(): boolean {
    if (!this.selectedLinkshellId) {
      return false;
    }

    const currentMembership = this.linkshellMemberships().find(link => link.id === this.selectedLinkshellId);
    return (currentMembership?.rank ?? '').toLowerCase() === 'leader';
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    const membership = this.linkshellMemberships().find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
  }

  protected canDeletePrimaryLinkshell(): boolean {
    const linkshell = this.selectedLinkshell();
    if (!linkshell) {
      return false;
    }

    return this.canManageMembers() && linkshell.memberCount <= 1 && this.primaryLinkshellActiveEventCount() === 0;
  }

  protected deletePrimaryLinkshellHint(): string {
    const linkshell = this.selectedLinkshell();
    if (!linkshell) {
      return 'Select a linkshell first.';
    }

    if (!this.canManageMembers()) {
      return 'Only the leader can delete a linkshell.';
    }

    if (linkshell.memberCount > 1) {
      return 'Remove the remaining members before deleting this linkshell.';
    }

    if (this.primaryLinkshellActiveEventCount() > 0) {
      return 'End or cancel all queued/live events before deleting this linkshell.';
    }

    return 'Delete this linkshell and its history.';
  }

  protected roleBadgeClass(rank?: string | null): string {
    switch ((rank ?? 'Member').toLowerCase()) {
      case 'leader':
        return 'role-pill role-pill--leader';
      case 'officer':
        return 'role-pill role-pill--officer';
      default:
        return 'role-pill role-pill--member';
    }
  }

  protected memberInitials(name?: string | null): string {
    const trimmed = (name ?? '').trim();
    if (!trimmed) {
      return '?';
    }

    const parts = trimmed.split(/\s+/).filter(Boolean);
    if (parts.length === 1) {
      return parts[0].substring(0, 2).toUpperCase();
    }

    return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
  }

  protected memberAvatarClass(name?: string | null): string {
    const trimmed = (name ?? '').trim();
    if (!trimmed) {
      return 'a';
    }

    let hash = 0;
    for (let i = 0; i < trimmed.length; i += 1) {
      hash = (hash * 31 + trimmed.charCodeAt(i)) >>> 0;
    }

    return ['a', 'b', 'c', 'd', 'e'][hash % 5];
  }

  protected memberStatusClass(status?: string | null): string {
    const normalized = (status ?? 'Active').toLowerCase();
    if (normalized === 'active') {
      return 'success';
    }
    if (normalized === 'pending') {
      return 'warning';
    }
    return 'default';
  }

  protected needsProfileSetup(): boolean {
    const appUser = this.activity.overview()?.appUser;
    return !appUser?.characterName?.trim() || !appUser?.timeZone?.trim();
  }

  protected async submitProfile(): Promise<void> {
    await this.activity.updateProfile({
      characterName: this.profileModel.characterName.trim(),
      timeZone: this.profileModel.timeZone.trim() || null
    });
  }

  protected openCreateLinkshellForm(): void {
    if (!this.canCreateLinkshell()) {
      return;
    }

    this.activity.clearActionState();
    this.isCreateLinkshellOpen = true;
    this.editingLinkshellId = null;
    this.createLinkshellModel.name = '';
    this.createLinkshellModel.details = '';
  }

  protected openEditLinkshellForm(): void {
    const linkshell = this.selectedLinkshell();
    if (!linkshell) {
      return;
    }

    this.activity.clearActionState();
    this.isCreateLinkshellOpen = true;
    this.editingLinkshellId = linkshell.id;
    this.createLinkshellModel.name = linkshell.name;
    this.createLinkshellModel.details = linkshell.details ?? '';
  }

  protected closeCreateLinkshellForm(): void {
    this.isCreateLinkshellOpen = false;
    this.editingLinkshellId = null;
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

  protected onInviteLinkshellChange(value: number): void {
    this.inviteLinkshellId = value;
    this.participantInviteSeed = '';
    if (this.inviteSearchTerm.trim().length >= 2) {
      void this.activity.searchPlayers(this.inviteSearchTerm, this.inviteLinkshellId);
    }
  }

  protected async submitCreateLinkshellForm(): Promise<void> {
    this.isSubmittingLinkshell = true;

    try {
      if (this.editingLinkshellId) {
        await this.activity.updateLinkshell(this.editingLinkshellId, this.createLinkshellModel);
      } else {
        await this.activity.createLinkshell(this.createLinkshellModel);
      }
      this.createLinkshellModel.name = '';
      this.createLinkshellModel.details = '';
      this.isCreateLinkshellOpen = false;
      this.editingLinkshellId = null;
      this.inviteLinkshellId =
        this.activity.overview()?.primaryLinkshell?.id ??
        this.activity.overview()?.linkshells?.[0]?.id ??
        0;
    } finally {
      this.isSubmittingLinkshell = false;
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

  protected async confirmDeleteLinkshell(linkshellId: number, linkshellName: string): Promise<void> {
    if (!window.confirm(`Delete ${linkshellName}? This removes its events, history, invites, and memberships.`)) {
      return;
    }

    await this.activity.deleteLinkshell(linkshellId);
  }

  protected async confirmLeaveLinkshell(linkshellId: number, linkshellName: string): Promise<void> {
    if (!window.confirm(`Leave ${linkshellName}?`)) {
      return;
    }

    await this.activity.leaveLinkshell(linkshellId);
  }

  protected async runInviteSearch(): Promise<void> {
    const linkshellId = this.inviteTargetLinkshellId();

    this.inviteLinkshellId = linkshellId;
    await this.activity.searchPlayers(this.inviteSearchTerm, linkshellId);
  }

  protected selectedJoinLinkshell() {
    return this.activity.linkshellSearchResults().find(linkshell => linkshell.id === this.selectedJoinLinkshellId) ?? null;
  }

  protected historyList() {
    return this.activity.historyList();
  }

  // ---------- Event History pagination (10 per page) ----------
  protected readonly historyPageSize = 10;
  protected readonly historyPage = signal(1);

  protected historyTotalPages(): number {
    return Math.max(1, Math.ceil(this.historyList().length / this.historyPageSize));
  }

  protected historyPageItems() {
    const all = this.historyList();
    // Clamp the page so deletes/refreshes that shrink the list can never
    // leave us pointing at a non-existent page.
    const page = Math.min(Math.max(1, this.historyPage()), this.historyTotalPages());
    if (page !== this.historyPage()) {
      // Defer the corrective set so we don't synchronously mutate during
      // change detection — a microtask keeps Angular happy.
      queueMicrotask(() => this.historyPage.set(page));
    }
    const start = (page - 1) * this.historyPageSize;
    return all.slice(start, start + this.historyPageSize);
  }

  protected historyPageGoto(page: number): void {
    const clamped = Math.min(Math.max(1, page), this.historyTotalPages());
    this.historyPage.set(clamped);
  }

  protected historyPageRangeLabel(): string {
    const total = this.historyList().length;
    if (total === 0) return '0';
    const page = Math.min(Math.max(1, this.historyPage()), this.historyTotalPages());
    const start = (page - 1) * this.historyPageSize + 1;
    const end = Math.min(start + this.historyPageSize - 1, total);
    return `${start}–${end} of ${total}`;
  }

  protected historyDetail() {
    return this.activity.historyDetail();
  }

  protected formatDurationForLinkshell(duration: number | null | undefined, linkshellId: number): string {
    const hours = duration ?? 0;
    const linkshell = (this.activity.overview()?.linkshells ?? []).find(l => l.id === linkshellId);
    const step = linkshell?.settings?.dkpRoundingIncrement === 'Half' ? 0.5 : 0.25;
    const rounded = Math.round(hours / step) * step;
    return rounded.toFixed(2).replace(/\.?0+$/, '');
  }

  protected dkpHistory() {
    return this.activity.dkpHistory();
  }

  protected auctions() {
    return this.activity.auctions();
  }

  protected auctionHistory() {
    return this.activity.auctionHistory();
  }

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

  protected auctionRowSpan(auction: { items: { id: number }[] }): number {
    return Math.max(1, auction.items.length);
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

  protected pagedAuctionHistoryRows(): ReturnType<ActivitySidebarPanelComponent['auctionHistoryRows']> {
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

  protected auctionTimerLabel(auction: { startedAt?: string | null; startTime?: string | null; endTime?: string | null }): string {
    const startMs = this.parseDate(auction.startedAt || auction.startTime);
    const endMs = this.parseDate(auction.endTime);
    if (!startMs || !endMs) {
      return 'No timer';
    }

    const now = this.now();
    if (now < startMs) {
      return `Starts in ${this.formatElapsed(startMs - now)}`;
    }

    const remaining = endMs - now;
    if (remaining <= 0) {
      return 'Auction ended';
    }

    return this.formatElapsed(remaining);
  }

  protected setAuctionsView(view: 'active' | 'history'): void {
    this.auctionsView.set(view);
  }

  protected auctionState(auction: { startedAt?: string | null; startTime?: string | null; endTime?: string | null; status: string }): 'live' | 'ending' | 'ended' | 'scheduled' {
    const normalized = (auction.status || '').toLowerCase();
    if (normalized === 'closed' || normalized === 'archived' || normalized === 'ended') {
      return 'ended';
    }
    const endMs = this.parseDate(auction.endTime);
    const startMs = this.parseDate(auction.startedAt || auction.startTime);
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
    const startMs = this.parseDate(auction.startedAt || auction.startTime);
    const endMs = this.parseDate(auction.endTime);
    const now = this.now();
    if (state === 'scheduled' && startMs) {
      return this.formatElapsed(Math.max(0, startMs - now));
    }
    if (endMs) {
      return this.formatElapsed(Math.max(0, endMs - now));
    }
    return '—';
  }

  protected auctionProgressPercent(auction: { startedAt?: string | null; startTime?: string | null; endTime?: string | null; status: string }): number {
    const state = this.auctionState(auction);
    if (state === 'ended') return 100;
    const startMs = this.parseDate(auction.startedAt || auction.startTime);
    const endMs = this.parseDate(auction.endTime);
    if (!startMs || !endMs || endMs <= startMs) return 0;
    const now = this.now();
    const pct = ((now - startMs) / (endMs - startMs)) * 100;
    return Math.max(0, Math.min(100, pct));
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
    const ms = this.parseDate(value ?? null);
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

  protected async selectLinkshell(linkshellId: number): Promise<void> {
    if (!linkshellId) {
      return;
    }

    this.selectedLinkshellId = linkshellId;
    this.selectedDkpHistoryAppUserId = '';
    await this.activity.loadLinkshellDetail(linkshellId);
    await this.reloadDkpHistory();
    await this.activity.loadAuctions(linkshellId);
    await this.activity.loadAuctionHistory(linkshellId);
  }

  protected async onDkpHistoryMemberChange(appUserId: string): Promise<void> {
    this.selectedDkpHistoryAppUserId = appUserId;
    await this.reloadDkpHistory();
  }

  protected filteredDkpMembers() {
    const members = this.dkpHistory()?.members ?? [];
    const term = this.dkpMemberSearchTerm.trim().toLowerCase();
    if (!term) {
      return members;
    }
    return members.filter(member =>
      (member.characterName ?? '').toLowerCase().includes(term)
    );
  }

  protected filteredDkpEntries() {
    const entries = this.dkpHistory()?.entries ?? [];
    const term = this.dkpSearchTerm.trim().toLowerCase();
    if (!term) {
      return entries;
    }
    return entries.filter(entry => {
      const haystacks = [
        this.dkpEntryTypeLabel(entry.entryType),
        entry.entryType,
        entry.eventName,
        entry.eventType,
        entry.eventLocation,
        entry.itemName,
        entry.details
      ];
      return haystacks.some(field =>
        typeof field === 'string' && field.toLowerCase().includes(term)
      );
    });
  }

  protected pagedDkpEntries() {
    const all = this.filteredDkpEntries();
    const size = this.dkpPageSize;
    const totalPages = Math.max(1, Math.ceil(all.length / size));
    if (this.dkpPage() >= totalPages) {
      this.dkpPage.set(totalPages - 1);
    }
    const start = this.dkpPage() * size;
    return all.slice(start, start + size);
  }

  protected dkpPageCount(): number {
    return Math.max(1, Math.ceil(this.filteredDkpEntries().length / this.dkpPageSize));
  }

  protected dkpNextPage(): void {
    const next = this.dkpPage() + 1;
    if (next < this.dkpPageCount()) this.dkpPage.set(next);
  }

  protected dkpPrevPage(): void {
    const current = this.dkpPage();
    if (current > 0) this.dkpPage.set(current - 1);
  }

  protected dkpEntryTypeLabel(entryType: string): string {
    switch (entryType) {
      case 'LootSpent':
        return 'Loot Spent';
      case 'EventEarned':
        return 'Event Earned';
      case 'AuditAdjustment':
        return 'Audit · Adjustment';
      case 'AuditMisc':
        return 'Audit · Misc';
      default:
        return entryType || 'Entry';
    }
  }

  // Canonical event-type ordering. Anything not in this list (e.g. blank
  // entries that get bucketed as "Unspecified", or future custom types)
  // sorts to the bottom in alphabetical order.
  private static readonly DKP_EVENT_TYPE_ORDER: readonly string[] = [
    'Sky', 'Sea', 'Dynamis', 'Limbus',
    'HNM', 'HENM', 'NM', 'BCNM', 'KSNM',
    'Other'
  ];

  protected dkpEarnedByEventType(): { eventType: string; amount: number }[] {
    const entries = this.dkpHistory()?.entries ?? [];
    const totals = new Map<string, number>();
    for (const entry of entries) {
      if (entry.entryType !== 'EventEarned') continue;
      const key = (entry.eventType ?? '').trim() || 'Unspecified';
      totals.set(key, (totals.get(key) ?? 0) + entry.amount);
    }
    const orderIndex = (eventType: string): number => {
      const idx = ActivitySidebarPanelComponent.DKP_EVENT_TYPE_ORDER.indexOf(eventType);
      return idx === -1 ? Number.MAX_SAFE_INTEGER : idx;
    };
    return [...totals.entries()]
      .map(([eventType, amount]) => ({ eventType, amount: Math.round(amount * 100) / 100 }))
      .sort((a, b) => {
        const ai = orderIndex(a.eventType);
        const bi = orderIndex(b.eventType);
        if (ai !== bi) return ai - bi;
        return a.eventType.localeCompare(b.eventType);
      });
  }

  protected canAuditSelectedLinkshell(): boolean {
    return !!this.selectedLinkshellId && this.canManageLinkshell(this.selectedLinkshellId);
  }

  protected openDkpAudit(): void {
    const history = this.dkpHistory();
    this.dkpAuditModel.mode = 'Misc';
    this.dkpAuditModel.targetAppUserId =
      this.selectedDkpHistoryAppUserId || history?.members[0]?.appUserId || '';
    this.dkpAuditModel.relatedLedgerEntryId = null;
    this.dkpAuditModel.amount = null;
    this.dkpAuditModel.reason = '';
    this.isDkpAuditOpen = true;
  }

  protected closeDkpAudit(): void {
    this.isDkpAuditOpen = false;
  }

  protected onDkpAuditModeChange(mode: 'Adjust' | 'Misc'): void {
    this.dkpAuditModel.mode = mode;
    if (mode === 'Misc') {
      this.dkpAuditModel.relatedLedgerEntryId = null;
    }
  }

  protected dkpAuditCandidateEntries() {
    const entries = this.dkpHistory()?.entries ?? [];
    return entries.filter(entry =>
      entry.entryType === 'EventEarned' ||
      entry.entryType === 'LootSpent' ||
      entry.entryType === 'AuditAdjustment' ||
      entry.entryType === 'AuditMisc'
    );
  }

  protected async submitDkpAudit(): Promise<void> {
    if (!this.selectedLinkshellId) {
      this.activity.actionError.set('Select a linkshell before submitting an audit.');
      return;
    }

    if (!this.dkpAuditModel.targetAppUserId) {
      this.activity.actionError.set('Select the member this audit applies to.');
      return;
    }

    const reason = this.dkpAuditModel.reason?.trim() ?? '';
    if (!reason) {
      this.activity.actionError.set('Enter a reason for the audit.');
      return;
    }

    const amount = this.dkpAuditModel.amount;
    if (amount === null || Number.isNaN(amount)) {
      this.activity.actionError.set('Enter a numeric amount for the audit.');
      return;
    }

    if (this.dkpAuditModel.mode === 'Adjust' && !this.dkpAuditModel.relatedLedgerEntryId) {
      this.activity.actionError.set('Pick the previous entry you want to correct.');
      return;
    }

    const ok = await this.activity.submitDkpAudit({
      linkshellId: this.selectedLinkshellId,
      targetAppUserId: this.dkpAuditModel.targetAppUserId,
      mode: this.dkpAuditModel.mode,
      relatedLedgerEntryId:
        this.dkpAuditModel.mode === 'Adjust' ? this.dkpAuditModel.relatedLedgerEntryId : null,
      amount,
      reason
    });

    if (ok) {
      this.selectedDkpHistoryAppUserId = this.dkpAuditModel.targetAppUserId;
      this.isDkpAuditOpen = false;
    }
  }

  protected async sendInvite(appUserId: string): Promise<void> {
    const linkshellId = this.inviteTargetLinkshellId();

    if (!linkshellId) {
      this.activity.actionError.set('Select a linkshell before sending invites.');
      this.activity.actionMessage.set(null);
      return;
    }

    await this.activity.sendInvite(linkshellId, appUserId);
    await this.activity.searchPlayers(this.inviteSearchTerm, linkshellId);
    await this.activity.loadParticipantInviteCandidates(
      linkshellId,
      this.activity.participants().map(participant => participant.id)
    );
  }

  protected async requestJoinSelectedLinkshell(): Promise<void> {
    if (!this.selectedJoinLinkshellId) {
      this.activity.actionError.set('Select a linkshell before sending a join request.');
      this.activity.actionMessage.set(null);
      return;
    }

    await this.activity.requestJoinLinkshell(this.selectedJoinLinkshellId);
    await this.activity.searchLinkshells('');
  }

  protected async startAuction(auctionId: number): Promise<void> {
    if (!this.selectedLinkshellId) {
      return;
    }

    await this.activity.startAuction(auctionId, this.selectedLinkshellId);
  }

  protected closingAuctionId: number | null = null;
  protected auctionCloseDelivered: Record<number, boolean> = {};

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

  protected async openHistoryDetail(historyId: number): Promise<void> {
    await this.activity.loadHistoryDetail(historyId);
  }

  protected closeHistoryDetail(): void {
    this.activity.clearHistoryDetail();
  }

  protected async promoteMemberToOfficer(linkshellId: number, memberId: number, characterName: string): Promise<void> {
    if (!window.confirm(`Promote ${characterName} to officer?`)) {
      return;
    }

    await this.activity.updateLinkshellMemberRole(linkshellId, memberId, 'Officer', characterName);
  }

  protected async demoteMemberToMember(linkshellId: number, memberId: number, characterName: string): Promise<void> {
    if (!window.confirm(`Demote ${characterName} to member?`)) {
      return;
    }

    await this.activity.updateLinkshellMemberRole(linkshellId, memberId, 'Member', characterName);
  }

  protected async transferLeadership(linkshellId: number, memberId: number, characterName: string): Promise<void> {
    if (!window.confirm(`Transfer linkshell leadership to ${characterName}? You will become an officer.`)) {
      return;
    }

    await this.activity.updateLinkshellMemberRole(linkshellId, memberId, 'Leader', characterName);
  }

  protected readonly rolesByLinkshell = signal<Record<number, ActivityLinkshellRole[]>>({});

  protected async ensureRolesLoaded(linkshellId: number): Promise<void> {
    if (this.rolesByLinkshell()[linkshellId]) return;
    const data = await this.activity.loadLinkshellRoles(linkshellId);
    if (data) {
      this.rolesByLinkshell.update(map => ({ ...map, [linkshellId]: data.roles }));
    }
  }

  protected availableRolesForLinkshell(linkshellId: number): ActivityLinkshellRole[] {
    void this.ensureRolesLoaded(linkshellId);
    return this.rolesByLinkshell()[linkshellId] ?? [];
  }

  protected async changeMemberRole(linkshellId: number, memberId: number, characterName: string, newRole: string): Promise<void> {
    const trimmed = newRole?.trim();
    if (!trimmed) return;
    const promoteToLeader = trimmed.toLowerCase() === 'leader';
    const confirmation = promoteToLeader
      ? `Transfer linkshell leadership to ${characterName}? You will become an officer.`
      : `Change ${characterName}'s role to ${trimmed}?`;
    if (!window.confirm(confirmation)) return;
    await this.activity.updateLinkshellMemberRole(linkshellId, memberId, trimmed, characterName);
  }

  private resolveBrowserTimeZone(): string {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    } catch {
      return 'UTC';
    }
  }

  private resolveTimeZoneOptions(): string[] {
    const intlWithSupportedValues = Intl as typeof Intl & {
      supportedValuesOf?: (key: string) => string[];
    };

    const currentProfileTimeZone = this.activity.overview()?.appUser?.timeZone;
    const seedValues = [
      currentProfileTimeZone,
      this.browserTimeZone,
      ...ActivitySidebarPanelComponent.curatedTimeZones
    ].filter((value): value is string => Boolean(value && value.trim().length > 0));

    if (typeof intlWithSupportedValues.supportedValuesOf === 'function') {
      return Array.from(
        new Set([
          ...seedValues,
          ...intlWithSupportedValues.supportedValuesOf('timeZone')
        ])
      );
    }

    return Array.from(new Set(seedValues));
  }

  private inviteEligibilitySeed(linkshellId: number): string {
    if (linkshellId <= 0) {
      return '';
    }

    const overview = this.activity.overview();
    if (!overview) {
      return '';
    }

    const pendingInviteIds = (overview.sentInvites ?? [])
      .filter(invite => invite.linkshellId === linkshellId)
      .map(invite => invite.appUserId)
      .sort();

    const pendingJoinRequestIds = (overview.incomingJoinRequests ?? [])
      .filter(invite => invite.linkshellId === linkshellId)
      .map(invite => invite.appUserId)
      .sort();

    const primaryMemberIds = overview.primaryLinkshell?.id === linkshellId
      ? (overview.primaryLinkshell.members ?? [])
          .map(member => member.appUserId)
          .sort()
      : [];

    return [
      pendingInviteIds.join(','),
      pendingJoinRequestIds.join(','),
      primaryMemberIds.join(',')
    ].join('|');
  }

  private async reloadDkpHistory(): Promise<void> {
    if (!this.selectedLinkshellId) {
      this.activity.clearDkpHistory();
      return;
    }

    const history = await this.activity.loadDkpHistory(
      this.selectedLinkshellId,
      this.selectedDkpHistoryAppUserId || null
    );

    if (!history) {
      return;
    }

    this.selectedDkpHistoryAppUserId =
      history.selectedAppUserId ||
      history.members[0]?.appUserId ||
      '';
  }

  private parseDate(value?: string | null): number | null {
    if (!value) {
      return null;
    }

    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? null : parsed.getTime();
  }

  private formatElapsed(totalMilliseconds: number): string {
    const totalSeconds = Math.max(0, Math.floor(totalMilliseconds / 1000));
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;

    return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
  }
}
