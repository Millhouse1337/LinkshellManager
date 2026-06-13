import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityItem,
  ActivityItemInput,
  ActivityJobsRoster,
  ActivityJobsRosterMember,
  ActivityLinkshellRole,
  ActivityRevenueEntry,
  ActivityRevenueInput,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import type { ActivityJobRatingCommentSummary, ActivityJobRatingsResponse } from '../../discord/discord-activity.types';
import { ActivitySidebarPanelComponent } from '../activity-sidebar-panel.component';
import { formatAlts, rankIcon } from '../activity-home.helpers';
import { StarRatingComponent } from '../sidebar-panels/star-rating.component';

@Component({
  selector: 'app-linkshell-tab',
  imports: [CommonModule, FormsModule, ActivitySidebarPanelComponent, StarRatingComponent],
  templateUrl: './linkshell-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class LinkshellTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly formatAlts = formatAlts;

  // Persists across the dashboard <-> linkshell hop, since both tabs render
  // a roster search that we want to feel like the same control. Parent owns
  // the model, child binds via these getters.
  @Input({ required: true }) rosterSearchValue!: string;
  @Input({ required: true }) rosterSearchChange!: (value: string) => void;

  protected get rosterSearch(): string { return this.rosterSearchValue; }
  protected set rosterSearch(value: string) { this.rosterSearchChange(value); this.rosterPage.set(1); }

  // App Sync filter: limits the Linkshell Roster (Manage Team) list to members
  // who are app-linked (appUserId set). Status stays visible but is never part
  // of the filter. Mirrors the Dashboard tab's identical toggle.
  protected readonly appSyncOnly = signal(false);
  protected toggleAppSync(value: boolean): void {
    this.appSyncOnly.set(value);
    this.rosterPage.set(1);
  }

  // ----- Re-implemented small reads via this.activity -----

  protected initials(value: string | null | undefined): string {
    const name = (value ?? '').trim();
    if (!name) return '??';
    const parts = name.split(/\s+/).filter(Boolean);
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[1][0]).toUpperCase();
  }

  protected memberAvatarClass(name?: string | null): string {
    const trimmed = (name ?? '').trim();
    if (!trimmed) return 'a';
    let hash = 0;
    for (let i = 0; i < trimmed.length; i += 1) {
      hash = (hash * 31 + trimmed.charCodeAt(i)) >>> 0;
    }
    return ['a', 'b', 'c', 'd', 'e'][hash % 5];
  }

  protected memberStatusClass(status?: string | null): string {
    const normalized = (status ?? 'Active').toLowerCase();
    if (normalized === 'active') return 'success';
    if (normalized === 'pending') return 'warning';
    return 'default';
  }

  protected primaryLinkshell() {
    return this.activity.overview()?.primaryLinkshell ?? null;
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    const membership = (this.activity.overview()?.linkshells ?? []).find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
  }

  protected selectedDashboardLinkshellId(): number {
    return (
      this.activity.overview()?.appUser?.primaryLinkshellId ??
      this.primaryLinkshell()?.id ??
      (this.activity.overview()?.linkshells ?? [])[0]?.id ??
      0
    );
  }

  protected selectedDashboardLinkshell() {
    const selectedId = this.selectedDashboardLinkshellId();
    return (this.activity.overview()?.linkshells ?? []).find(linkshell => linkshell.id === selectedId) ?? null;
  }

  protected selectedDashboardMembers() {
    const selectedId = this.selectedDashboardLinkshellId();
    if (this.primaryLinkshell()?.id !== selectedId) {
      return [];
    }

    return [...(this.primaryLinkshell()?.members ?? [])].sort((left, right) =>
      left.characterName.localeCompare(right.characterName)
    );
  }

  protected filteredDashboardMembers() {
    const term = this.rosterSearch.trim().toLowerCase();
    const appSyncOnly = this.appSyncOnly();
    const members = this.selectedDashboardMembers();
    return members.filter(member => {
      if (appSyncOnly) {
        if (!member.appUserId) return false;
      }
      if (term) {
        const nameMatch = (member.characterName ?? '').toLowerCase().includes(term);
        const rankMatch = (member.rank ?? '').toLowerCase().includes(term);
        if (!nameMatch && !rankMatch) return false;
      }
      return true;
    });
  }

  // Roster pagination: 10 per page. rosterPage is 1-based; every read clamps
  // to the valid range so removing members or narrowing the search can't
  // strand the view on an empty out-of-range page (the search setter also
  // resets to page 1). No signal writes happen during render — clamping is
  // pure; only the Prev/Next handlers mutate the signal.
  protected readonly rosterPageSize = 10;
  protected readonly rosterPage = signal(1);

  protected rosterTotalPages(): number {
    return Math.max(1, Math.ceil(this.filteredDashboardMembers().length / this.rosterPageSize));
  }

  protected rosterCurrentPage(): number {
    return Math.min(Math.max(1, this.rosterPage()), this.rosterTotalPages());
  }

  protected pagedDashboardMembers() {
    const start = (this.rosterCurrentPage() - 1) * this.rosterPageSize;
    return this.filteredDashboardMembers().slice(start, start + this.rosterPageSize);
  }

  protected rosterPrev(): void {
    this.rosterPage.set(Math.max(1, this.rosterCurrentPage() - 1));
  }

  protected rosterNext(): void {
    this.rosterPage.set(Math.min(this.rosterTotalPages(), this.rosterCurrentPage() + 1));
  }

  protected canManageSelectedDashboard(): boolean {
    return this.canManageLinkshell(this.selectedDashboardLinkshellId());
  }

  // ----- Jobs Roster (every member's leveled jobs) -----
  // Lazy + cached per linkshell; collapsed by default so opening the Management
  // tab doesn't fire an extra request until the user asks for it.
  protected readonly jobsRoster = signal<ActivityJobsRoster | null>(null);
  protected readonly jobsRosterExpanded = signal(false);
  protected readonly jobsRosterBusy = signal(false);
  private readonly jobsRosterLoadedFor = signal<number | null>(null);
  protected readonly jobsRosterPageSize = 3;
  protected readonly jobsRosterPage = signal(1);
  protected jobsRosterSearchTerm = '';

  protected get jobsRosterSearch(): string {
    return this.jobsRosterSearchTerm;
  }

  protected set jobsRosterSearch(value: string) {
    this.jobsRosterSearchTerm = value;
    this.jobsRosterPage.set(1);
  }

  // The cached roster, but only if it belongs to the currently-selected
  // linkshell — guards against showing one linkshell's jobs after a switch.
  protected jobsRosterForCurrent(): ActivityJobsRoster | null {
    return this.jobsRosterLoadedFor() === this.selectedDashboardLinkshellId() ? this.jobsRoster() : null;
  }

  protected async toggleJobsRoster(): Promise<void> {
    const next = !this.jobsRosterExpanded();
    this.jobsRosterExpanded.set(next);
    if (next) await this.ensureJobsRoster();
  }

  protected async ensureJobsRoster(): Promise<void> {
    const id = this.selectedDashboardLinkshellId();
    if (id <= 0) return;
    if (this.jobsRosterLoadedFor() === id && this.jobsRoster()) return;

    this.jobsRosterBusy.set(true);
    try {
      const data = await this.activity.loadJobsRoster(id);
      if (data) {
        this.jobsRoster.set(data);
        this.jobsRosterLoadedFor.set(id);
        this.jobsRosterPage.set(1);
      }
    } finally {
      this.jobsRosterBusy.set(false);
    }
  }

  protected filteredJobsRosterMembers(): ActivityJobsRosterMember[] {
    const members = this.jobsRosterForCurrent()?.members ?? [];
    const term = this.jobsRosterSearch.trim().toLowerCase();
    if (!term) {
      return members;
    }

    return members.filter(member => this.matchesJobsRosterSearch(member, term));
  }

  protected jobsRosterTotalPages(): number {
    return Math.max(1, Math.ceil(this.filteredJobsRosterMembers().length / this.jobsRosterPageSize));
  }

  protected jobsRosterCurrentPage(): number {
    return Math.min(Math.max(1, this.jobsRosterPage()), this.jobsRosterTotalPages());
  }

  protected pagedJobsRosterMembers(): ActivityJobsRosterMember[] {
    const start = (this.jobsRosterCurrentPage() - 1) * this.jobsRosterPageSize;
    return this.filteredJobsRosterMembers().slice(start, start + this.jobsRosterPageSize);
  }

  protected jobsRosterPrev(): void {
    this.jobsRosterPage.set(Math.max(1, this.jobsRosterCurrentPage() - 1));
  }

  protected jobsRosterNext(): void {
    this.jobsRosterPage.set(Math.min(this.jobsRosterTotalPages(), this.jobsRosterCurrentPage() + 1));
  }

  private matchesJobsRosterSearch(member: ActivityJobsRosterMember, term: string): boolean {
    const namesAndRank = [
      member.characterName,
      member.rank,
      member.alt1Name,
      member.alt2Name
    ];

    if (namesAndRank.some(value => (value ?? '').toLowerCase().includes(term))) {
      return true;
    }

    return this.rosterCharacters(member).some(character =>
      this.leveledJobs(character.levels, character.strong)
        .some(job => `${job.name} ${job.level}`.toLowerCase().includes(term))
    );
  }

  // Main + named alts for one roster member, as labeled characters to render.
  protected rosterCharacters(member: ActivityJobsRosterMember): { label: string; isAlt: boolean; levels: number[]; strong: boolean[]; relic: boolean[]; merit: string[]; relicName: string[] }[] {
    const list = [{ label: member.characterName, isAlt: false, levels: member.jobLevels ?? [], strong: member.strongJobs ?? [], relic: member.relicFlags ?? [], merit: member.meritJobs ?? [], relicName: member.relicNames ?? [] }];
    if (member.alt1Name) { list.push({ label: member.alt1Name, isAlt: true, levels: member.alt1JobLevels ?? [], strong: member.alt1StrongJobs ?? [], relic: member.alt1RelicFlags ?? [], merit: member.alt1MeritJobs ?? [], relicName: member.alt1RelicNames ?? [] }); }
    if (member.alt2Name) { list.push({ label: member.alt2Name, isAlt: true, levels: member.alt2JobLevels ?? [], strong: member.alt2StrongJobs ?? [], relic: member.alt2RelicFlags ?? [], merit: member.alt2MeritJobs ?? [], relicName: member.alt2RelicNames ?? [] }); }
    return list;
  }

  // The leveled jobs (level > 0) for one character, highest level first; each
  // carries its "strong" (merited) flag + relic flag/weapon + merit note for the pills.
  protected leveledJobs(levels: number[] | null | undefined, strong?: boolean[] | null, relic?: boolean[] | null, merit?: string[] | null, relicName?: string[] | null): { name: string; level: number; strong: boolean; relic: boolean; merit: string; relicName: string }[] {
    const catalog = this.jobsRosterForCurrent()?.jobCatalog ?? [];
    const arr = levels ?? [];
    const flags = strong ?? [];
    const relicFlags = relic ?? [];
    const meritNotes = merit ?? [];
    const relicNames = relicName ?? [];
    return catalog
      .map((name, i) => ({ name, level: arr[i] ?? 0, strong: flags[i] ?? false, relic: relicFlags[i] ?? false, merit: meritNotes[i] ?? '', relicName: relicNames[i] ?? '' }))
      .filter(entry => entry.level > 0)
      .sort((a, b) => b.level - a.level);
  }

  // Hover tooltip for a job pill: relic (weapon name if known) + merit info.
  protected pillTitle(job: { strong: boolean; relic: boolean; merit: string; relicName: string }): string | null {
    const parts: string[] = [];
    if (job.relic) { parts.push(job.relicName ? 'Relic: ' + job.relicName : 'Relic weapon'); }
    if (job.strong && job.merit) { parts.push('Merits: ' + job.merit); }
    else if (job.strong) { parts.push('Merited'); }
    return parts.length ? parts.join(' · ') : null;
  }

  // ----- Per-member "View Profile" modal -----
  // Reuses the jobs-roster data (lazy-loaded) to show one member's jobs in a
  // popup, opened from the roster row. Also loads what the linkshell thinks of
  // them (peer gear/skill averages per job + an anonymous comment summary, main
  // character / slot 0).
  protected readonly viewingProfileMember = signal<ActivityJobsRosterMember | null>(null);
  protected readonly viewingProfileBusy = signal(false);
  // Peer feedback is loaded PER CHARACTER (main = slot 0, alts = slots 1/2) so a
  // teammate's rating/comment on an alt is shown under that alt's name, not mixed
  // into the main character's block.
  protected readonly viewingProfileRatingBlocks = signal<{
    slot: number;
    name: string;
    isAlt: boolean;
    ratings: ActivityJobRatingsResponse | null;
    summary: ActivityJobRatingCommentSummary | null;
  }[]>([]);

  protected async openMemberProfile(memberId: number): Promise<void> {
    this.viewingProfileBusy.set(true);
    this.viewingProfileMember.set(null);
    this.viewingProfileRatingBlocks.set([]);
    try {
      await this.ensureJobsRoster();
      const found = this.jobsRosterForCurrent()?.members.find(m => m.id === memberId) ?? null;
      this.viewingProfileMember.set(found);

      // Resolve the AppUser id (the roster member only carries the membership id)
      // from the linkshell's members, then pull peer ratings + summary for the
      // main character and each alt the member has.
      const linkshellId = this.selectedDashboardLinkshellId();
      const appUserId = this.selectedDashboardMembers().find(m => m.id === memberId)?.appUserId ?? null;
      if (found && linkshellId && appUserId) {
        const slots: { slot: number; name: string; isAlt: boolean }[] = [
          { slot: 0, name: found.characterName, isAlt: false },
        ];
        if (found.alt1Name) { slots.push({ slot: 1, name: found.alt1Name, isAlt: true }); }
        if (found.alt2Name) { slots.push({ slot: 2, name: found.alt2Name, isAlt: true }); }

        const blocks: {
          slot: number; name: string; isAlt: boolean;
          ratings: ActivityJobRatingsResponse | null;
          summary: ActivityJobRatingCommentSummary | null;
        }[] = [];
        for (const s of slots) {
          const ratings = await this.activity.loadJobRatings(linkshellId, appUserId, s.slot);
          let summary: ActivityJobRatingCommentSummary | null = null;
          if ((ratings?.peerCommentCount ?? 0) > 0) {
            summary = await this.activity.loadJobRatingCommentSummary(linkshellId, appUserId, s.slot);
          }
          blocks.push({ ...s, ratings, summary });
        }
        this.viewingProfileRatingBlocks.set(blocks);
      }
    } finally {
      this.viewingProfileBusy.set(false);
    }
  }

  protected closeMemberProfile(): void {
    this.viewingProfileMember.set(null);
    this.viewingProfileRatingBlocks.set([]);
  }

  // Jobs a teammate rated for one character block (peerCount > 0).
  protected peerJobsFor(ratings: ActivityJobRatingsResponse | null) {
    return (ratings?.jobs ?? []).filter(job => job.peerCount > 0);
  }

  // True when ANY character (main or alt) has peer ratings or comments.
  protected viewingProfileHasFeedback(): boolean {
    return this.viewingProfileRatingBlocks().some(
      b => (b.ratings?.peerRaterCount ?? 0) > 0 || (b.ratings?.peerCommentCount ?? 0) > 0);
  }

  // Catalog job name for a rating's jobIndex (uses the loaded jobs-roster catalog).
  protected ratingJobName(jobIndex: number): string {
    return this.jobsRosterForCurrent()?.jobCatalog?.[jobIndex] ?? `Job ${jobIndex + 1}`;
  }

  // ----- Rank editing UI (only shown in this tab) -----

  protected editingRankMemberId = signal<number | null>(null);
  protected editingRankValue = '';
  protected editingStatusValue = '';
  // Roster "Count" edit (only while modifying a row): which streak the number
  // targets (active credit = green / absence = red) and its value.
  protected editingStreakType: 'credit' | 'absent' = 'credit';
  protected editingStreakValue = 0;
  protected readonly rankIcon = rankIcon;
  protected readonly statusOptions = ['Active', 'Pending', 'Inactive'] as const;

  // Roles are linkshell-specific (custom roles are persisted server-side via
  // createLinkshellRole). We load on demand and cache per-linkshell so the
  // dropdown reflects the server's current set instead of a hardcoded list.
  private readonly rolesByLinkshell = signal<Record<number, ActivityLinkshellRole[]>>({});
  private readonly fallbackRoleNames = ['Leader', 'Officer', 'Member'] as const;

  // Returns the rank options for the dropdown as { id, name } pairs. While the
  // server is loading we surface the system defaults so the inline edit is
  // never empty.
  protected rankOptions(): { id: number; name: string }[] {
    const id = this.selectedDashboardLinkshellId();
    const loaded = this.rolesByLinkshell()[id];
    if (loaded && loaded.length > 0) {
      return loaded.map(role => ({ id: role.id, name: role.name }));
    }
    return this.fallbackRoleNames.map((name, index) => ({ id: -(index + 1), name }));
  }

  protected async beginEditRank(memberId: number, currentRank: string | null | undefined): Promise<void> {
    this.editingRankMemberId.set(memberId);
    this.editingRankValue = currentRank || 'Member';
    const editing = this.selectedDashboardMembers().find(m => m.id === memberId);
    this.editingStatusValue = editing?.status || 'Active';
    // Default the Count question to whichever streak the member is currently on:
    // an absence run shows red, otherwise we edit the (green) active-credit run.
    if ((editing?.absentStreak ?? 0) > 0) {
      this.editingStreakType = 'absent';
      this.editingStreakValue = editing?.absentStreak ?? 0;
    } else {
      this.editingStreakType = 'credit';
      this.editingStreakValue = editing?.activeCreditStreak ?? 0;
    }
    const id = this.selectedDashboardLinkshellId();
    if (id && !this.rolesByLinkshell()[id]) {
      const data = await this.activity.loadLinkshellRoles(id);
      if (data) {
        this.rolesByLinkshell.update(map => ({ ...map, [id]: data.roles }));
      }
    }
  }

  protected cancelEditRank(): void {
    this.editingRankMemberId.set(null);
    this.editingRankValue = '';
    this.editingStatusValue = '';
    this.editingStreakType = 'credit';
    this.editingStreakValue = 0;
  }

  protected async saveEditRank(linkshellId: number, memberId: number): Promise<void> {
    const member = this.selectedDashboardMembers().find(m => m.id === memberId);
    const characterName = member?.characterName ?? null;
    const newRank = this.editingRankValue;
    const newStatus = this.editingStatusValue;
    const rankChanged = !!newRank && newRank !== (member?.rank || 'Member');
    const statusChanged = !!newStatus && newStatus !== (member?.status || 'Active');

    // The roster "Count" override: compare the typed (type, value) against the
    // member's current displayed streak (absence wins the color when present).
    const curType: 'credit' | 'absent' = (member?.absentStreak ?? 0) > 0 ? 'absent' : 'credit';
    const curVal = curType === 'absent' ? (member?.absentStreak ?? 0) : (member?.activeCreditStreak ?? 0);
    const streakValue = Math.max(0, Math.trunc(Number(this.editingStreakValue) || 0));
    const streakChanged = this.editingStreakType !== curType || streakValue !== curVal;

    if (rankChanged) {
      await this.activity.updateLinkshellMemberRole(linkshellId, memberId, newRank, characterName);
    }
    // Apply the streak override before an explicit status change so a manually
    // chosen status (e.g. Pending) still wins as the final word.
    if (streakChanged) {
      await this.activity.setMemberActiveCreditCount(
        linkshellId, memberId, streakValue, characterName, this.editingStreakType);
    }
    if (statusChanged) {
      await this.activity.updateLinkshellMemberStatus(linkshellId, memberId, newStatus, characterName);
    }
    this.editingRankMemberId.set(null);
    this.editingRankValue = '';
    this.editingStatusValue = '';
  }

  protected canEditRosterRank(memberAppUserId: string | null | undefined): boolean {
    if (!this.canManageSelectedDashboard()) return false;
    if (!memberAppUserId) return false;
    return memberAppUserId !== this.activity.overview()?.appUser?.id;
  }

  // Status is editable for ANY member incl. self (a leader/officer can set their
  // own Active/Inactive); rank stays non-self via canEditRosterRank.
  protected canEditRosterStatus(memberAppUserId: string | null | undefined): boolean {
    return this.canManageSelectedDashboard() && !!memberAppUserId;
  }

  // ----- Leave / remove members -----

  protected pendingLeaveLinkshell = signal(false);
  protected pendingRemoveMemberId = signal<number | null>(null);

  protected currentUserAppUserId(): string | null {
    return this.activity.overview()?.appUser?.id ?? null;
  }

  protected isCurrentUser(memberAppUserId: string | null | undefined): boolean {
    const id = this.currentUserAppUserId();
    return !!id && id === memberAppUserId;
  }

  protected isCurrentUserLeaderOfSelected(): boolean {
    return (this.selectedDashboardLinkshell()?.rank ?? '').toLowerCase() === 'leader';
  }

  protected otherLeaderCountInSelected(): number {
    const myId = this.currentUserAppUserId();
    return this.selectedDashboardMembers()
      .filter(member => (member.rank ?? '').toLowerCase() === 'leader')
      .filter(member => member.appUserId !== myId)
      .length;
  }

  // True when the current user is the linkshell's last remaining leader and
  // there are other members. They must promote someone else to Leader before
  // they can leave (otherwise the linkshell would be left orphaned).
  protected mustHandoffBeforeLeaving(): boolean {
    if (!this.isCurrentUserLeaderOfSelected()) return false;
    if (this.otherLeaderCountInSelected() > 0) return false;
    return this.selectedDashboardMembers().length > 1;
  }

  protected canRemoveMember(memberAppUserId: string | null | undefined): boolean {
    if (!this.isCurrentUserLeaderOfSelected()) return false;
    if (this.isCurrentUser(memberAppUserId)) return false;
    return true;
  }

  protected requestLeaveLinkshell(): void {
    if (this.mustHandoffBeforeLeaving()) return;
    this.pendingRemoveMemberId.set(null);
    this.pendingLeaveLinkshell.set(true);
  }

  protected cancelLeaveLinkshell(): void {
    this.pendingLeaveLinkshell.set(false);
  }

  protected async confirmLeaveLinkshell(): Promise<void> {
    if (this.mustHandoffBeforeLeaving()) return;
    const id = this.selectedDashboardLinkshellId();
    if (!id) return;
    try {
      await this.activity.leaveLinkshell(id);
    } finally {
      this.pendingLeaveLinkshell.set(false);
    }
  }

  protected requestRemoveMember(memberId: number): void {
    this.pendingLeaveLinkshell.set(false);
    this.pendingRemoveMemberId.set(memberId);
  }

  protected cancelRemoveMember(): void {
    this.pendingRemoveMemberId.set(null);
  }

  protected async confirmRemoveMember(memberId: number): Promise<void> {
    const id = this.selectedDashboardLinkshellId();
    if (!id) return;
    try {
      await this.activity.removeLinkshellMember(id, memberId);
    } finally {
      this.pendingRemoveMemberId.set(null);
    }
  }

  // ----- Inventory & revenue (formerly the "configurations tab" leftovers
  // that actually appear under the Management tab) -----

  protected configLinkshellId = signal<number | null>(null);

  protected selectedConfigLinkshellId(): number | null {
    const explicit = this.configLinkshellId();
    if (explicit !== null) return explicit;
    return (
      this.activity.overview()?.appUser?.primaryLinkshellId ??
      this.primaryLinkshell()?.id ??
      this.activity.overview()?.linkshells?.[0]?.id ??
      null
    );
  }

  protected canManageConfigLinkshell(): boolean {
    const id = this.selectedConfigLinkshellId();
    return id !== null && this.canManageLinkshell(id);
  }

  protected configItems(): ActivityItem[] {
    const id = this.selectedConfigLinkshellId();
    if (this.primaryLinkshell()?.id !== id) return [];
    return this.primaryLinkshell()?.items ?? [];
  }

  protected configRevenue(): ActivityRevenueEntry[] {
    const id = this.selectedConfigLinkshellId();
    if (this.primaryLinkshell()?.id !== id) return [];
    return this.primaryLinkshell()?.revenueEntries ?? [];
  }

  protected configIncomeTotal(): number {
    return this.configRevenue()
      .filter(entry => entry.entryType === 'Income')
      .reduce((sum, entry) => sum + (entry.value ?? 0), 0);
  }

  protected configExpenseTotal(): number {
    return this.configRevenue()
      .filter(entry => entry.entryType === 'Expense')
      .reduce((sum, entry) => sum + (entry.value ?? 0), 0);
  }

  protected configNetTotal(): number {
    return this.configIncomeTotal() - this.configExpenseTotal();
  }

  protected configTotalItemQuantity(): number {
    return this.configItems().reduce((sum, item) => sum + (item.quantity ?? 0), 0);
  }

  protected showItemForm = signal(false);
  protected readonly itemTypeOptions = ['Crafted Item', 'Endgame Loot Drop', 'Other'] as const;
  protected itemName = '';
  protected itemType = '';
  protected itemTypeSelection = '';
  protected itemQuantity = 1;
  protected itemNotes = '';
  protected editingItemId = signal<number | null>(null);

  protected onItemTypeSelectionChange(value: string): void {
    this.itemTypeSelection = value;
    // Preset selections (e.g. "Crafted Item") flow straight through; "Other"
    // and the empty placeholder both clear the value so the freeform input
    // starts blank.
    this.itemType = value && value !== 'Other' ? value : '';
  }

  protected toggleItemForm(): void {
    this.showItemForm.update(value => !value);
    if (!this.showItemForm()) {
      this.resetItemForm();
    }
  }

  protected resetItemForm(): void {
    this.itemName = '';
    this.itemType = '';
    this.itemTypeSelection = '';
    this.itemQuantity = 1;
    this.itemNotes = '';
    this.editingItemId.set(null);
  }

  protected beginEditItem(item: ActivityItem): void {
    this.editingItemId.set(item.id);
    this.itemName = item.itemName;
    const existingType = item.itemType ?? '';
    // Legacy items can carry any string for itemType. Map a preset match to
    // the dropdown selection; anything else falls into the "Other" branch so
    // the freeform input shows the saved value.
    if ((this.itemTypeOptions as readonly string[]).includes(existingType)) {
      this.itemTypeSelection = existingType;
      this.itemType = existingType;
    } else if (existingType) {
      this.itemTypeSelection = 'Other';
      this.itemType = existingType;
    } else {
      this.itemTypeSelection = '';
      this.itemType = '';
    }
    this.itemQuantity = item.quantity;
    this.itemNotes = item.notes ?? '';
    this.showItemForm.set(true);
  }

  protected async submitItem(): Promise<void> {
    const linkshellId = this.selectedConfigLinkshellId();
    if (!linkshellId) return;
    const name = this.itemName.trim();
    if (!name) return;
    const input: ActivityItemInput = {
      itemName: name,
      itemType: this.itemType.trim() || null,
      quantity: Math.max(0, Math.floor(this.itemQuantity || 0)),
      notes: this.itemNotes.trim() || null
    };
    try {
      const editingId = this.editingItemId();
      if (editingId !== null) {
        await this.activity.updateItem(editingId, input);
      } else {
        await this.activity.createItem(linkshellId, input);
      }
      this.resetItemForm();
      this.showItemForm.set(false);
    } catch {
      // surfaced
    }
  }

  protected async deleteItem(itemId: number): Promise<void> {
    try { await this.activity.deleteItem(itemId); } catch { /* surfaced */ }
  }

  protected showRevenueForm = signal(false);
  // Set while editing an existing entry; null when the form is adding a new one.
  protected readonly editingRevenueId = signal<number | null>(null);
  protected revenueType: 'Income' | 'Expense' = 'Income';
  protected revenueCategory = '';
  protected revenueValue = 0;
  protected revenueDetails = '';

  protected toggleRevenueForm(): void {
    this.showRevenueForm.update(value => !value);
    if (!this.showRevenueForm()) {
      this.resetRevenueForm();
    }
  }

  protected resetRevenueForm(): void {
    this.editingRevenueId.set(null);
    this.revenueType = 'Income';
    this.revenueCategory = '';
    this.revenueValue = 0;
    this.revenueDetails = '';
  }

  // Loads an existing entry into the (shared) form for editing.
  protected beginEditRevenue(entry: ActivityRevenueEntry): void {
    this.editingRevenueId.set(entry.id);
    this.revenueType = entry.entryType === 'Expense' ? 'Expense' : 'Income';
    this.revenueCategory = entry.category ?? '';
    this.revenueValue = entry.value ?? 0;
    this.revenueDetails = entry.details ?? '';
    this.showRevenueForm.set(true);
  }

  protected async submitRevenue(): Promise<void> {
    const linkshellId = this.selectedConfigLinkshellId();
    if (!linkshellId) return;
    const value = Math.max(0, Math.floor(this.revenueValue || 0));
    if (value <= 0) return;
    const input: ActivityRevenueInput = {
      entryType: this.revenueType,
      category: this.revenueCategory.trim() || null,
      value,
      details: this.revenueDetails.trim() || null,
      occurredAt: null
    };
    try {
      const editingId = this.editingRevenueId();
      if (editingId !== null) {
        await this.activity.updateRevenueEntry(editingId, input);
      } else {
        await this.activity.createRevenueEntry(linkshellId, input);
      }
      this.resetRevenueForm();
      this.showRevenueForm.set(false);
    } catch {
      // surfaced
    }
  }

  protected async deleteRevenue(entryId: number): Promise<void> {
    try { await this.activity.deleteRevenueEntry(entryId); } catch { /* surfaced */ }
  }
}
