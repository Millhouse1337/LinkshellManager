import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, inject, model, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityJobsRoster,
  ActivityJobsRosterMember,
  ActivityLinkshellRole,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import type { ActivityJobRating, ActivityJobRatingOverall, ActivityJobRatingsResponse } from '../../discord/discord-activity.types';
import { ActivitySidebarPanelComponent } from '../activity-sidebar-panel.component';
import { ClaimRequestsCardComponent } from '../sidebar-panels/claim-requests-card.component';
import { JobsRosterStore, type RosterCharacterJobs, type RosterJobPill } from '../jobs-roster.store';
import {
  ADMIN_BADGE,
  canManageLinkshellIn,
  formatAlts,
  isLeaderRankName,
  isLeaderTierIn,
  memberAvatarClass,
  memberInitials,
  memberStatusClass,
  rankIcon
} from '../activity-home.helpers';
import type { ManagementView } from '../activity-home.types';

// HorizonXI is classic-75: the profile's level bars fill against it, and a 75 bar turns gold.
// Kept in sync with ProfileJobLevels.MaxLevel (C#).
const MAX_JOB_LEVEL = 75;

// One row of the View Profile job table: a leveled job (or one only rated, level 0) with the
// ratings left on it for that character, when there are any.
interface ProfileJobRow extends RosterJobPill {
  rating: ActivityJobRating | null;
}

@Component({
  selector: 'app-linkshell-tab',
  imports: [CommonModule, FormsModule, ActivitySidebarPanelComponent, ClaimRequestsCardComponent],
  templateUrl: './linkshell-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styles: [`
    /* View Profile modal. Colours are tokens only (see discord-activity/CLAUDE.md). */
    .mp-scrim {
      position: fixed; inset: 0; z-index: 200; background: var(--scrim);
      display: flex; align-items: flex-start; justify-content: center; padding: 48px 16px; overflow: auto;
    }
    .mp {
      width: 620px; max-width: 100%; overflow: hidden;
      background: var(--bg-elev); border: 1px solid var(--border-2); border-radius: var(--r-xl);
      box-shadow: var(--shadow-pop);
    }
    .mp-head { display: flex; align-items: center; gap: 14px; padding: 20px 22px 0; }
    .mp-avatar {
      width: 44px; height: 44px; border-radius: 50%; flex-shrink: 0;
      display: grid; place-items: center; font-weight: 700; font-size: 16px; color: var(--fg);
      background: linear-gradient(135deg, var(--accent), var(--purple));
      box-shadow: 0 0 0 3px var(--bg-elev), 0 0 0 4px var(--gold);
    }
    .mp-id { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
    .mp-name { display: flex; gap: 8px; align-items: baseline; flex-wrap: wrap; }
    .mp-name strong { font-size: 19px; color: var(--fg); }
    .mp-rank { color: var(--gold); font-size: 12px; font-weight: 600; }
    .mp-overall { font-size: 12px; color: var(--fg-2); }
    .mp-overall b { font-family: var(--font-mono); color: var(--fg); }
    .mp-overall b.skill { color: var(--accent); }
    .mp-close {
      width: 32px; height: 32px; border-radius: var(--r-md); border: 0; flex-shrink: 0;
      background: transparent; color: var(--fg-2); font-size: 18px; cursor: pointer;
    }
    .mp-close:hover { background: var(--surface-3); color: var(--fg); }
    .mp-tabs { display: flex; flex-wrap: wrap; gap: 4px; padding: 18px 22px 0; border-bottom: 1px solid var(--border); }
    .mp-tab {
      display: flex; align-items: center; gap: 8px; padding: 9px 12px; margin-bottom: -1px; white-space: nowrap;
      cursor: pointer; background: transparent; border: 0; border-bottom: 2px solid transparent;
      color: var(--fg-2); font: 600 13px var(--font-sans);
    }
    .mp-tab:hover { color: var(--fg-1); }
    .mp-tab[aria-selected="true"] { color: var(--fg); border-bottom-color: var(--accent); }
    .mp-count {
      font: 600 10px var(--font-mono); background: var(--surface-3); color: var(--fg-2);
      padding: 1px 6px; border-radius: 10px;
    }
    .mp-main { font: 600 9px var(--font-mono); color: var(--fg-2); letter-spacing: 0.08em; }
    .mp-body { padding: 14px 22px 22px; display: flex; flex-direction: column; gap: 4px; min-height: 240px; }
    .mp-cols, .mp-row {
      display: grid; grid-template-columns: 64px minmax(0, 1fr) 92px 92px; gap: 16px; align-items: center;
    }
    .mp-cols { padding: 4px 10px 8px; font: 600 10px var(--font-mono); letter-spacing: 0.1em; color: var(--fg-3); }
    .mp-row { padding: 9px 10px; border-radius: var(--r-md); }
    .mp-row:nth-child(even) { background: var(--surface); }
    .mp-row:hover { background: var(--surface-2); }
    .mp-job { display: flex; gap: 5px; align-items: center; font: 700 13px var(--font-mono); color: var(--fg); }
    .mp-star { color: var(--gold); font-size: 11px; }
    .mp-relic { font: 600 9px var(--font-mono); color: var(--gold); letter-spacing: 0.06em; }
    .mp-lvl { display: flex; align-items: center; gap: 10px; }
    .mp-lvl > span { font: 700 13px var(--font-mono); width: 20px; text-align: right; color: var(--fg); }
    .mp-track { flex: 1; height: 4px; background: var(--surface-3); border-radius: 2px; overflow: hidden; }
    .mp-fill { height: 100%; background: var(--accent); border-radius: 2px; }
    .mp-fill.max { background: var(--gold); }
    .mp-dots { display: flex; gap: 4px; align-items: center; }
    .mp-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--surface-3); }
    .mp-dots.gear .on { background: var(--fg-1); }
    .mp-dots.skill .on { background: var(--accent); }
    .mp-dots em { font: normal 600 11px var(--font-mono); color: var(--fg-2); margin-left: 4px; }
    .mp-none { font-size: 12px; color: var(--fg-4); }
    .mp-muted { font-size: 12px; color: var(--fg-3); }
    .mp-empty {
      flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center;
      gap: 6px; padding: 40px 0; color: var(--fg-3); font-size: 12px; text-align: center;
    }
    .mp-empty strong { font-size: 14px; color: var(--fg-2); }
    .mp-section { margin-top: 14px; display: flex; flex-direction: column; gap: 4px; }
    .mp-section-head { padding: 0 10px 4px; font: 600 10px var(--font-mono); letter-spacing: 0.1em; color: var(--fg-3); }
    .mp-section-head span { font: 400 12px var(--font-sans); letter-spacing: 0; margin-left: 6px; }
    .mp-comments { padding: 0 22px 22px; }
    .mp-comments p { font-size: 12px; margin: 0 0 8px; line-height: 1.45; color: var(--fg-2); }
    .mp-comments blockquote {
      margin: 0 0 6px; padding: 6px 10px; border-left: 2px solid var(--border-2); font-size: 12px; color: var(--fg-2);
    }
    .mp-loading { padding: 24px 16px; text-align: center; color: var(--fg-3); font-size: 13px; }
    @media (max-width: 520px) {
      .mp-head { padding: 16px 14px 0; }
      .mp-tabs { padding: 14px 14px 0; }
      .mp-body { padding: 12px 10px 16px; }
      .mp-cols, .mp-row { grid-template-columns: 42px minmax(0, 1fr) 62px 62px; gap: 8px; }
      .mp-dots { gap: 2px; }
      .mp-dot { width: 6px; height: 6px; }
      .mp-dots em { display: none; }
      .mp-comments { padding: 0 14px 16px; }
    }
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

  // Roster or Invites, picked by the strip in the header. Two-way: ActivityHomeComponent keeps it
  // in the location (/management/invites). Invites used to sit under the roster.
  readonly view = model<ManagementView>('roster');

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
    return canManageLinkshellIn(this.activity.overview(), linkshellId);
  }

  // The app's one current linkshell, the same one the Dashboard and Configurations read.
  protected selectedDashboardLinkshellId(): number {
    return this.activity.currentLinkshellId();
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

  // The "Manage members" PERMISSION the claim-request endpoints check (or the Leader, whom the
  // server always lets through), not the rank: a custom role with Manage members must see the
  // card. (The card used to sit in an old roster panel that was never shown.)
  protected canApproveClaimRequests(linkshellId: number): boolean {
    const permissions = (this.activity.overview()?.linkshells ?? []).find(link => link.id === linkshellId)?.permissions;
    return permissions?.canManageMembers === true || isLeaderTierIn(this.activity.overview(), linkshellId);
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

  // Which character's tab is open in the modal: an index into rosterCharacters() (main first).
  protected readonly profileCharIndex = signal(0);

  protected async openMemberProfile(memberId: number): Promise<void> {
    this.viewingProfileBusy.set(true);
    this.viewingProfileMember.set(null);
    this.viewingProfileRatingBlocks.set([]);
    this.viewingProfileOverall.set(null);
    this.profileCharIndex.set(0);
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
    this.profileCharIndex.set(0);
  }

  // The jobs to render for a character: every leveled job, highest first (leveledJobs()).
  protected jobsToShow(ch: RosterCharacterJobs) {
    return this.leveledJobs(ch.levels, ch.strong, ch.relic, ch.merit, ch.relicName);
  }

  // The modal's job table for one character: every leveled job, highest first, each with the
  // ratings left on it. A job that is rated but has no level entered still gets a row (level 0,
  // at the end), so a rating never disappears because the level is blank.
  protected profileRows(ch: RosterCharacterJobs): ProfileJobRow[] {
    const rated = new Map((this.ratingsFor(ch)?.jobs ?? [])
      .filter(job => job.selfGear > 0 || job.selfSkill > 0 || job.peerCount > 0)
      .map(job => [this.ratingJobName(job.jobIndex), job] as const));
    const rows: ProfileJobRow[] = this.jobsToShow(ch).map(job => ({ ...job, rating: rated.get(job.name) ?? null }));
    for (const [name, rating] of rated) {
      if (!rows.some(row => row.name === name)) {
        rows.push({ name, level: 0, strong: false, relic: false, merit: '', relicName: '', rating });
      }
    }
    return rows;
  }

  // The ratings loaded for one character (blocks are per character, matched by name).
  protected ratingsFor(ch: RosterCharacterJobs): ActivityJobRatingsResponse | null {
    return this.viewingProfileRatingBlocks().find(block => block.name === ch.label)?.ratings ?? null;
  }

  protected hasSelfRating(rating: ActivityJobRating | null): boolean {
    return !!rating && (rating.selfGear > 0 || rating.selfSkill > 0);
  }

  // Level bar width: a sliver even at level 1 so the track never reads as empty.
  protected levelPercent(level: number): number {
    return level > 0 ? Math.max(3, (level / MAX_JOB_LEVEL) * 100) : 0;
  }

  protected isMaxLevel(level: number): boolean {
    return level >= MAX_JOB_LEVEL;
  }

  // Five rating dots, the first round(value) of them lit.
  protected ratingDots(value: number): boolean[] {
    const lit = Math.round(value);
    return [0, 1, 2, 3, 4].map(i => i < lit);
  }

  // Jobs a teammate rated for one character block (peerCount > 0).
  protected peerJobsFor(ratings: ActivityJobRatingsResponse | null) {
    return (ratings?.jobs ?? []).filter(job => job.peerCount > 0);
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
  protected readonly ADMIN_BADGE = ADMIN_BADGE;
  protected readonly statusOptions = ['Active', 'Pending', 'Inactive'] as const;

  // "Jun 7, 2026"-style joined date for the roster column.
  protected formatJoined(value?: string | null): string {
    return this.activity.formatDate(value) ?? '—';
  }

  // Roles are linkshell-specific (custom roles are persisted server-side via
  // createLinkshellRole). We load on demand and cache per-linkshell so the
  // dropdown reflects the server's current set instead of a hardcoded list.
  private readonly rolesByLinkshell = signal<Record<number, ActivityLinkshellRole[]>>({});
  private readonly fallbackRoleNames = ['Leader', 'Officer', 'Member'] as const;

  // Returns the rank options for the dropdown as { id, name } pairs. While the
  // server is loading we surface the system defaults so the inline edit is
  // never empty.
  //
  // Only the Leader (or the admin override) may hand out the Leader rank, so the
  // server answers 403 when an officer picks it. Leave it out of their list
  // rather than offer a choice that can only fail.
  protected rankOptions(): { id: number; name: string }[] {
    const id = this.selectedDashboardLinkshellId();
    const loaded = this.rolesByLinkshell()[id];
    const options = loaded && loaded.length > 0
      ? loaded.map(role => ({ id: role.id, name: role.name }))
      : this.fallbackRoleNames.map((name, index) => ({ id: -(index + 1), name }));
    if (isLeaderTierIn(this.activity.overview(), id)) return options;
    return options.filter(option => !isLeaderRankName(option.name));
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

  // A Leader's own rank is Leader-only too (the server refuses anyone else), so an
  // officer sees it as a plain tag instead of a dropdown that can only fail.
  protected canEditRosterRank(
    memberAppUserId: string | null | undefined,
    memberRank?: string | null
  ): boolean {
    if (!this.canManageSelectedDashboard()) return false;
    if (!memberAppUserId) return false;
    if (isLeaderRankName(memberRank) &&
        !isLeaderTierIn(this.activity.overview(), this.selectedDashboardLinkshellId())) {
      return false;
    }
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

  // Leader-tier permission, so the app-wide admin override applies. Deliberately NOT
  // isCurrentUserLeaderOfSelected(), which stays a literal rank check because
  // mustHandoffBeforeLeaving() above is an integrity rail, not a permission — an admin
  // who is only a Member must not be told to hand off leadership before leaving.
  protected canRemoveMember(memberAppUserId: string | null | undefined): boolean {
    if (!isLeaderTierIn(this.activity.overview(), this.selectedDashboardLinkshell()?.id)) return false;
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
