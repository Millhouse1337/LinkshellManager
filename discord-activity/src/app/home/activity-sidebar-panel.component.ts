import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityAuctionItemInput,
  ActivityCreateAuctionInput,
  ActivityCreateLinkshellInput,
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

  protected readonly activity = inject(DiscordActivityService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());
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
    items: [{ id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '' }]
  };

  protected inviteSearchTerm = '';
  protected inviteLinkshellId = 0;
  protected selectedJoinLinkshellId = 0;
  protected selectedLinkshellId = 0;
  protected selectedDkpHistoryAppUserId = '';
  protected readonly auctionBidDrafts: Record<number, number | null> = {};
  protected readonly expandedAuctionBidItems: Record<number, boolean> = {};
  protected memberSearchTerm = '';
  protected memberRoleFilter: 'all' | 'leader' | 'officer' | 'member' = 'all';
  protected isCreateLinkshellOpen = false;
  protected isSubmittingLinkshell = false;
  protected isAuctionFormOpen = false;
  protected isSubmittingAuction = false;
  protected editingAuctionId: number | null = null;
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
    return this.activity.participantInviteCandidates();
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
    this.auctionFormModel.items = [{ id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '' }];
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
          notes: item.notes ?? ''
        }))
      : [{ id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '' }];
  }

  protected closeAuctionForm(): void {
    this.isAuctionFormOpen = false;
    this.editingAuctionId = null;
  }

  protected addAuctionFormItem(): void {
    this.auctionFormModel.items = [
      ...this.auctionFormModel.items,
      { id: 0, itemName: '', itemType: '', startingBidDkp: 0, notes: '' }
    ];
  }

  protected removeAuctionFormItem(index: number): void {
    if (this.auctionFormModel.items.length <= 1) {
      return;
    }

    this.auctionFormModel.items = this.auctionFormModel.items.filter((_, itemIndex) => itemIndex !== index);
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

  protected historyDetail() {
    return this.activity.historyDetail();
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

  protected async closeAuction(auctionId: number, title?: string | null): Promise<void> {
    if (!this.selectedLinkshellId) {
      return;
    }

    if (!window.confirm(`Close ${title || 'this auction'} and archive its outcome?`)) {
      return;
    }

    await this.activity.closeAuction(auctionId, this.selectedLinkshellId);
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

    await this.activity.updateLinkshellMemberRole(linkshellId, memberId, 'Officer');
  }

  protected async demoteMemberToMember(linkshellId: number, memberId: number, characterName: string): Promise<void> {
    if (!window.confirm(`Demote ${characterName} to member?`)) {
      return;
    }

    await this.activity.updateLinkshellMemberRole(linkshellId, memberId, 'Member');
  }

  protected async transferLeadership(linkshellId: number, memberId: number, characterName: string): Promise<void> {
    if (!window.confirm(`Transfer linkshell leadership to ${characterName}? You will become an officer.`)) {
      return;
    }

    await this.activity.updateLinkshellMemberRole(linkshellId, memberId, 'Leader');
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
