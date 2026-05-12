import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DiscordActivityService } from '../discord/discord-activity.service';
import { resolveBrowserTimeZone, resolveTimeZoneOptions } from './sidebar-panel.helpers';
import { AuctionsPanelComponent } from './sidebar-panels/auctions-panel.component';
import { InvitesPanelComponent } from './sidebar-panels/invites-panel.component';
import { RosterPanelComponent } from './sidebar-panels/roster-panel.component';

@Component({
  selector: 'app-activity-sidebar-panel',
  imports: [CommonModule, FormsModule, AuctionsPanelComponent, InvitesPanelComponent, RosterPanelComponent],
  templateUrl: './activity-sidebar-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ActivitySidebarPanelComponent {
  @Input() visibleSections?: readonly string[];
  protected readonly activity = inject(DiscordActivityService);

  protected showSection(name: string): boolean {
    return !this.visibleSections || this.visibleSections.includes(name);
  }

  protected readonly profileModel = {
    characterName: '',
    timeZone: '',
    altCharacterName1: '',
    altCharacterName2: ''
  };

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
  protected readonly browserTimeZone = resolveBrowserTimeZone();
  protected readonly timeZoneOptions = resolveTimeZoneOptions(this.activity.overview()?.appUser?.timeZone, this.browserTimeZone);
  private profileSeed = '';
  private historySeed = '';

  // Bumped after a brand-new linkshell is created in the roster panel so
  // the invites panel re-anchors its target to the freshly-resolved
  // primary linkshell (matches pre-refactor behavior).
  protected readonly invitePrimaryResetTick = signal(0);

  // Latest-write-wins guard. Each linkshell-selection change bumps this
  // counter; in-flight loads from a previous selection capture the older
  // value and bail before mutating state, so a slow detail/auction load
  // can't overwrite data for the user's current selection.
  private selectionGen = 0;

  // Stable arrow bindings for child component callback @Inputs — wrapping
  // the methods avoids `this` binding issues when the references are passed
  // into `[selectLinkshell]` etc.
  protected readonly selectLinkshellFn = (linkshellId: number) => this.selectLinkshell(linkshellId);
  protected readonly onPrimaryLinkshellChangedFn = () => this.invitePrimaryResetTick.update(tick => tick + 1);

  public constructor()
  {
    effect(() => {
      const appUser = this.activity.overview()?.appUser;
      if (!appUser) {
        return;
      }

      const nextCharacterName = appUser.characterName ?? '';
      const nextTimeZone = appUser.timeZone ?? this.browserTimeZone;
      const nextAlt1 = appUser.altCharacterName1 ?? '';
      const nextAlt2 = appUser.altCharacterName2 ?? '';
      const nextSeed = `${appUser.id}|${nextCharacterName}|${nextTimeZone}|${nextAlt1}|${nextAlt2}`;

      if (nextSeed === this.profileSeed) {
        return;
      }

      this.profileSeed = nextSeed;
      this.profileModel.characterName = nextCharacterName;
      this.profileModel.timeZone = nextTimeZone;
      this.profileModel.altCharacterName1 = nextAlt1;
      this.profileModel.altCharacterName2 = nextAlt2;
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

      const gen = ++this.selectionGen;
      const isStale = (): boolean => gen !== this.selectionGen;

      void (async () => {
        await this.activity.loadLinkshellDetail(selectedLinkshellId);
        if (isStale()) return;
        await this.reloadDkpHistory();
        if (isStale()) return;
        await this.activity.loadAuctions(selectedLinkshellId);
        if (isStale()) return;
        await this.activity.loadAuctionHistory(selectedLinkshellId);
      })();
    });

    effect(() => {
      const overview = this.activity.overview();
      if (!overview) {
        this.historySeed = '';
        this.activity.clearHistoryList();
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

  protected primaryLinkshellId(): number | null {
    return this.activity.overview()?.appUser?.primaryLinkshellId ?? this.activity.overview()?.primaryLinkshell?.id ?? null;
  }

  protected isManagerMode(): boolean {
    return this.linkshellMemberships().some(link => this.canManageLinkshell(link.id));
  }

  protected isMemberMode(): boolean {
    return !this.isManagerMode();
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    const membership = this.linkshellMemberships().find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
  }

  protected needsProfileSetup(): boolean {
    const appUser = this.activity.overview()?.appUser;
    return !appUser?.characterName?.trim() || !appUser?.timeZone?.trim();
  }

  protected async submitProfile(): Promise<void> {
    await this.activity.updateProfile({
      characterName: this.profileModel.characterName.trim(),
      timeZone: this.profileModel.timeZone.trim() || null,
      altCharacterName1: this.profileModel.altCharacterName1.trim() || null,
      altCharacterName2: this.profileModel.altCharacterName2.trim() || null
    });
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
    const currentPage = this.dkpPage();
    const clamped = currentPage >= totalPages ? totalPages - 1 : currentPage;
    if (clamped !== currentPage) {
      // Defer corrective set so we don't synchronously mutate during change
      // detection — same pattern as historyPageItems above.
      queueMicrotask(() => this.dkpPage.set(clamped));
    }
    const start = clamped * size;
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
      case 'LootRefund':
        return 'Loot Refund';
      case 'LootEditRefund':
        return 'Loot Edit · Refund';
      case 'LootEditSpent':
        return 'Loot Edit · Spent';
      case 'EventEarned':
        return 'Event Earned';
      case 'AuctionSpent':
        return 'Auction Spent';
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

  protected async openHistoryDetail(historyId: number): Promise<void> {
    await this.activity.loadHistoryDetail(historyId);
  }

  protected closeHistoryDetail(): void {
    this.activity.clearHistoryDetail();
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
}
