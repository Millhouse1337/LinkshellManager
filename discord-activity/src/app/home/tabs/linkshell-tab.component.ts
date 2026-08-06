import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityJobsRoster,
  ActivityJobsRosterMember,
  ActivityLinkshellRole,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import type { ActivityJobRatingOverall, ActivityJobRatingsResponse } from '../../discord/discord-activity.types';
import { ActivitySidebarPanelComponent } from '../activity-sidebar-panel.component';
import { JobsRosterStore, type RosterCharacterJobs, type RosterJobPill } from '../jobs-roster.store';
import { formatAlts, memberAvatarClass, memberInitials, memberStatusClass, rankIcon } from '../activity-home.helpers';
import { StarRatingComponent } from '../sidebar-panels/star-rating.component';

// HorizonXI is classic-75. A job at 75 is "max level" (shown by default in the
// profile modal); a job at 37+ is sub-capable (listed in the hover popover). Kept
// in sync with ProfileJobLevels.MaxLevel / ProfileJobLevels.SubJobMinLevel (C#).
const MAX_JOB_LEVEL = 75;
const SUB_JOB_MIN_LEVEL = 37;

@Component({
  selector: 'app-linkshell-tab',
  imports: [CommonModule, FormsModule, ActivitySidebarPanelComponent, StarRatingComponent],
  templateUrl: './linkshell-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: [`
    .lsp-dialog {
      width: 100%; max-width: 880px; border-radius: 14px;
      background: linear-gradient(var(--surface), var(--surface)) padding-box,
                  linear-gradient(160deg, var(--border-hot), var(--border)) border-box;
      border: 1px solid transparent;
      box-shadow: 0 24px 64px rgba(0, 0, 0, 0.55);
    }
    .lsp-body { padding: 16px 18px; max-height: 78vh; overflow: auto; }
    .lsp-av {
      width: 44px; height: 44px; border-radius: 12px; flex-shrink: 0;
      display: grid; place-items: center; font-weight: 800; font-size: 17px;
      color: var(--bg); background: linear-gradient(135deg, var(--accent), var(--purple));
    }
    .lsp-grid { display: grid; grid-template-columns: 1fr; gap: 20px; }
    @media (min-width: 760px) { .lsp-grid { grid-template-columns: 1.15fr 0.85fr; } }
    .lsp-sub-popover {
      position: fixed; z-index: 210; max-width: 248px;
      padding: 8px 10px; border-radius: var(--r-md);
      background: var(--surface-2); border: 1px solid var(--border-hot);
      box-shadow: 0 12px 30px rgba(0, 0, 0, 0.5);
      display: flex; flex-wrap: wrap; gap: 5px;
    }
    .lsp-sub-popover__title {
      width: 100%; font-size: 10px; text-transform: uppercase; letter-spacing: 0.04em;
      color: var(--fg-3); margin-bottom: 1px;
    }
    .lsp-sub-popover__empty { font-size: 11px; color: var(--fg-3); font-style: italic; }
    .lsp-sub-chip {
      font-size: 11px; padding: 1px 6px; border-radius: var(--r-sm);
      background: var(--surface-3); border: 1px solid var(--border); color: var(--fg-1);
    }
    .lsp-sub-chip .lvl { color: var(--accent); font-weight: 700; margin-left: 3px; }
    /* The treasury's styles moved to src/styles/_finances.scss with the section itself. */
  `]
})
export class LinkshellTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  // Jobs pills (roster "Show Jobs" column + the profile modal) read through the
  // shared store, so the Dashboard roster's toggle and this one share one fetch.
  private readonly jobs = inject(JobsRosterStore);
  protected readonly formatAlts = formatAlts;
  // Shared roster helpers (avatar initials/color, status tag) — single source in
  // activity-home.helpers so every roster renders members identically. `initials`
  // keeps its template name and delegates to the shared memberInitials.
  protected readonly initials = memberInitials;
  protected readonly memberAvatarClass = memberAvatarClass;
  protected readonly memberStatusClass = memberStatusClass;

  // Persists across the dashboard <-> linkshell hop, since both tabs render
  // a roster search that we want to feel like the same control. Parent owns
  // the model, child binds via these getters.
  @Input({ required: true }) rosterSearchValue!: string;
  @Input({ required: true }) rosterSearchChange!: (value: string) => void;

  protected get rosterSearch(): string { return this.rosterSearchValue; }
  protected set rosterSearch(value: string) { this.rosterSearchChange(value); }

  // "Show Jobs" switches the Linkshell Roster table into a jobs view: every column
  // except Character is dropped and each row instead lists that member's leveled
  // jobs (main + alts), which is what the old standalone Jobs Roster card showed.
  // The jobs data is lazy-loaded the first time it's switched on.
  protected readonly showJobs = signal(false);
  protected async toggleShowJobs(value: boolean): Promise<void> {
    this.showJobs.set(value);
    // The Modify editors live in the columns the jobs view hides — close any
    // open row so a half-finished edit can't be stranded off-screen.
    this.editingRankMemberId.set(null);
    if (value) await this.ensureJobsRoster();
  }

  // ----- Re-implemented small reads via this.activity -----

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
    const members = this.selectedDashboardMembers();
    if (!term) {
      return members;
    }

    return members.filter(member => {
      const nameMatch = (member.characterName ?? '').toLowerCase().includes(term);
      const rankMatch = (member.rank ?? '').toLowerCase().includes(term);
      if (nameMatch || rankMatch) return true;
      // With the jobs view on, the search also reaches alt names and job
      // names/levels — what the standalone Jobs Roster search used to cover.
      return this.showJobs() && this.matchesJobsSearch(member.id, term);
    });
  }

  // The roster used to page 10 members at a time. It now renders every filtered
  // member and scrolls inside its own box (.ac-roster--with-actions caps the
  // height and pins the header), so the whole linkshell is one continuous list.

  protected canManageSelectedDashboard(): boolean {
    return this.canManageLinkshell(this.selectedDashboardLinkshellId());
  }

  // ----- Jobs data (every member's leveled jobs, rendered inline in the roster) -----
  // Lazy + cached in JobsRosterStore, shared with the Dashboard roster's own
  // "Show Jobs" toggle: nothing is fetched until one of them asks (or a member
  // profile is opened), and a tab hop doesn't re-fetch.
  protected readonly jobsRosterBusy = this.jobs.busy;

  protected jobsRosterForCurrent(): ActivityJobsRoster | null {
    return this.jobs.forLinkshell(this.selectedDashboardLinkshellId());
  }

  protected ensureJobsRoster(): Promise<void> {
    return this.jobs.ensure(this.selectedDashboardLinkshellId());
  }

  protected jobsRosterMember(memberId: number): ActivityJobsRosterMember | null {
    return this.jobs.memberFor(this.selectedDashboardLinkshellId(), memberId);
  }

  // Main + alts for a roster row's Jobs cell (empty when the member has no entry).
  protected jobsCharacters(memberId: number) {
    return this.jobs.charactersFor(this.selectedDashboardLinkshellId(), memberId);
  }

  private matchesJobsSearch(memberId: number, term: string): boolean {
    return this.jobs.matchesSearch(this.selectedDashboardLinkshellId(), memberId, term);
  }

  // Main + named alts for one roster member, as labeled characters to render.
  protected rosterCharacters(member: ActivityJobsRosterMember): RosterCharacterJobs[] {
    return this.jobs.characters(member);
  }

  // The leveled jobs (level > 0) for one character, highest level first; each
  // carries its "strong" (merited) flag + relic flag/weapon + merit note for the pills.
  protected leveledJobs(
    levels: number[] | null | undefined,
    strong?: boolean[] | null,
    relic?: boolean[] | null,
    merit?: string[] | null,
    relicName?: string[] | null
  ): RosterJobPill[] {
    return this.jobs.leveledJobs(levels, strong, relic, merit, relicName);
  }

  // Hover tooltip for a job pill: relic (weapon name if known) + merit info.
  protected pillTitle(job: RosterJobPill): string | null {
    return this.jobs.pillTitle(job);
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
  }[]>([]);

  // The member's overall ratings rollup (self + linkshell averages + an AI summary
  // over all their peer comments), shown in the "Overall" section below the
  // per-character blocks.
  protected readonly viewingProfileOverall = signal<ActivityJobRatingOverall | null>(null);

  // Jobs grid defaults to max-level (75) jobs only; this toggles to ALL leveled
  // jobs. One toggle for the whole modal (every character expands together).
  protected readonly profileShowAllJobs = signal(false);

  // The hover/tap popover listing a character's leveled subjobs (37+) for one
  // max-level pill. Positioned with `position:fixed` from the pill's screen rect so
  // it escapes the modal body's `overflow:auto` clipping. Null = hidden.
  protected readonly subPopover = signal<{
    label: string;
    jobs: { name: string; level: number }[];
    top: number;
    left: number;
  } | null>(null);

  protected async openMemberProfile(memberId: number): Promise<void> {
    this.viewingProfileBusy.set(true);
    this.viewingProfileMember.set(null);
    this.viewingProfileRatingBlocks.set([]);
    this.viewingProfileOverall.set(null);
    this.profileShowAllJobs.set(false);
    this.subPopover.set(null);
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
        }[] = [];
        for (const s of slots) {
          const ratings = await this.activity.loadJobRatings(linkshellId, appUserId, s.slot);
          blocks.push({ ...s, ratings });
        }
        this.viewingProfileRatingBlocks.set(blocks);
        // One rollup across all characters (averages + AI comment summary).
        this.viewingProfileOverall.set(await this.activity.loadJobRatingOverall(linkshellId, appUserId));
      }
    } finally {
      this.viewingProfileBusy.set(false);
    }
  }

  protected closeMemberProfile(): void {
    this.viewingProfileMember.set(null);
    this.viewingProfileRatingBlocks.set([]);
    this.viewingProfileOverall.set(null);
    this.profileShowAllJobs.set(false);
    this.subPopover.set(null);
  }

  // The jobs to render for a character: max-level (75) only by default, or every
  // leveled job when "Show all jobs" is on. Reuses leveledJobs() (sorted desc).
  protected jobsToShow(ch: RosterCharacterJobs) {
    const all = this.leveledJobs(ch.levels, ch.strong, ch.relic, ch.merit, ch.relicName);
    return this.profileShowAllJobs() ? all : all.filter(job => job.level >= MAX_JOB_LEVEL);
  }

  // A character's leveled SUBJOBS (37+), excluding the hovered job, for the popover.
  protected subJobsFor(levels: number[], excludeName: string): { name: string; level: number }[] {
    return this.leveledJobs(levels)
      .filter(job => job.level >= SUB_JOB_MIN_LEVEL && job.name !== excludeName)
      .map(job => ({ name: job.name, level: job.level }));
  }

  protected toggleProfileShowAll(): void {
    this.profileShowAllJobs.set(!this.profileShowAllJobs());
  }

  // Open the subjob popover for a max-level pill: anchor it below the pill, clamped
  // to the viewport so it never runs off the right edge.
  protected openSubPopover(ev: Event, levels: number[], jobName: string): void {
    const subs = this.subJobsFor(levels, jobName);
    const rect = (ev.currentTarget as HTMLElement).getBoundingClientRect();
    const width = 248;
    let left = rect.left;
    if (left + width > window.innerWidth - 8) { left = window.innerWidth - width - 8; }
    if (left < 8) { left = 8; }
    this.subPopover.set({ label: jobName, jobs: subs, top: rect.bottom + 6, left });
  }

  protected closeSubPopover(): void {
    this.subPopover.set(null);
  }

  // Jobs a teammate rated for one character block (peerCount > 0).
  protected peerJobsFor(ratings: ActivityJobRatingsResponse | null) {
    return (ratings?.jobs ?? []).filter(job => job.peerCount > 0);
  }

  // Jobs the member rated THEMSELVES (their own gear/skill assessment) for one block.
  protected selfJobsFor(ratings: ActivityJobRatingsResponse | null) {
    return (ratings?.jobs ?? []).filter(job => job.selfGear > 0 || job.selfSkill > 0);
  }

  // True when a character block has anything to show: self-ratings, peer ratings,
  // or peer comments.
  protected blockHasRatings(block: { ratings: ActivityJobRatingsResponse | null }): boolean {
    return this.selfJobsFor(block.ratings).length > 0
      || (block.ratings?.peerRaterCount ?? 0) > 0
      || (block.ratings?.peerCommentCount ?? 0) > 0;
  }

  // True when ANY character (main or alt) has self-ratings, peer ratings, or comments.
  protected viewingProfileHasRatings(): boolean {
    return this.viewingProfileRatingBlocks().some(b => this.blockHasRatings(b));
  }

  // Catalog job name for a rating's jobIndex (uses the loaded jobs-roster catalog).
  protected ratingJobName(jobIndex: number): string {
    return this.jobs.jobName(jobIndex);
  }

  // ----- Rank editing UI (only shown in this tab) -----

  protected editingRankMemberId = signal<number | null>(null);
  protected editingRankValue = '';
  protected editingStatusValue = '';
  // Roster streak overrides while modifying a row — the Active Credit and Absent
  // Streak columns each become editable. They're mutually exclusive: on Save we
  // apply whichever the officer actually changed.
  protected editingCreditValue = 0;
  protected editingAbsentValue = 0;
  protected readonly rankIcon = rankIcon;
  protected readonly statusOptions = ['Active', 'Pending', 'Inactive'] as const;

  // "Jun 7, 2026"-style joined date for the roster column.
  protected formatJoined(value?: string | null): string {
    if (!value) return '—';
    const d = new Date(value);
    if (isNaN(d.getTime())) return '—';
    return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
  }

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
    this.editingCreditValue = editing?.activeCreditStreak ?? 0;
    this.editingAbsentValue = editing?.absentStreak ?? 0;
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
    this.editingCreditValue = 0;
    this.editingAbsentValue = 0;
  }

  protected async saveEditRank(linkshellId: number, memberId: number): Promise<void> {
    const member = this.selectedDashboardMembers().find(m => m.id === memberId);
    const characterName = member?.characterName ?? null;
    const newRank = this.editingRankValue;
    const newStatus = this.editingStatusValue;
    const rankChanged = !!newRank && newRank !== (member?.rank || 'Member');
    const statusChanged = !!newStatus && newStatus !== (member?.status || 'Active');

    // The Active Credit / Absent Streak columns are editable overrides (mutually
    // exclusive). Apply whichever the officer actually changed: credit wins if
    // both differ.
    const curCredit = member?.activeCreditStreak ?? 0;
    const curAbsent = member?.absentStreak ?? 0;
    const newCredit = Math.max(0, Math.trunc(Number(this.editingCreditValue) || 0));
    const newAbsent = Math.max(0, Math.trunc(Number(this.editingAbsentValue) || 0));

    if (rankChanged) {
      await this.activity.updateLinkshellMemberRole(linkshellId, memberId, newRank, characterName);
    }
    // Apply the streak override before an explicit status change so a manually
    // chosen status (e.g. Pending) still wins as the final word.
    if (newCredit !== curCredit) {
      await this.activity.setMemberActiveCreditCount(linkshellId, memberId, newCredit, characterName, 'credit');
    } else if (newAbsent !== curAbsent) {
      await this.activity.setMemberActiveCreditCount(linkshellId, memberId, newAbsent, characterName, 'absent');
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

  // Inventory and gil used to live here too, under the tab about the linkshell's PEOPLE. Both moved
  // to their own top-level Treasury tab: ItemsSectionComponent and FinancesSectionComponent.
}
