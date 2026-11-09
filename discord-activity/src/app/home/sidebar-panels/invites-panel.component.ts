import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, effect, inject, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import type { ActivityInviteRosterState } from '../../discord/discord-activity.types';
import { canManageLinkshellIn } from '../activity-home.helpers';

/** Where the invite card looks: the dropdown beside its search box. */
type InviteSource = 'all' | 'linkshell' | 'connected' | 'discord';

/** One row of the invite card's results, whichever source it came from. */
interface InviteResultRow {
  key: string;
  name: string;
  detail: string | null;
  avatarUrl: string | null;
  /** In a linkshell locked to a Discord server, someone who isn't in that server. They can
   *  still be invited, but must join the server before the lock lets them in. */
  outsideServer: boolean;
  /** What Invite does. Null when the row can't be invited (see `status`). */
  invite: { appUserId: string } | { discordUserId: string } | null;
  /** Shown in place of the Invite button: Member, Invited, You, ... */
  status: string | null;
}

/** The label a roster row shows in place of Invite. */
const ROSTER_STATUS: Record<ActivityInviteRosterState, string | null> = {
  invite: null,
  member: 'Member',
  invited: 'Invited',
  requested: 'Requested to join',
  you: 'You',
  noAccount: 'No LSM account'
};

@Component({
  selector: 'app-invites-panel',
  imports: [CommonModule, FormsModule],
  templateUrl: './invites-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class InvitesPanelComponent {
  protected readonly activity = inject(DiscordActivityService);

  // Bumping `linkshellPrimaryResetTick` from the parent (e.g. after a
  // brand-new linkshell is created) rebases this panel's invite-target
  // selection to the freshly-resolved primary linkshell.
  @Input() linkshellPrimaryResetTick = 0;

  protected inviteLinkshellId = 0;
  private participantInviteSeed = '';

  // The invite card: one search box, the dropdown saying where to look, and one list.
  // "Entire LSM Server" searches the server (Enter / Search) a page at a time; the other
  // sources are already loaded, so the box filters them as you type.
  protected searchTerm = '';
  protected inviteSource: InviteSource = 'all';
  /** "In a Linkshell": the linkshell whose roster is showing. */
  protected rosterLinkshellId: number | null = null;
  protected page = 1;
  protected readonly pageSize = 10;
  private browseLoadedFor = 0;
  private sourcesLoadedFor = 0;
  private rosterResetFor = 0;

  // Tracks which (locked) linkshell the "From My Discord Server" roster has
  // been loaded for, so the effect doesn't refetch on every overview refresh.
  private discordRosterLoadedFor = 0;

  public constructor() {
    effect(() => {
      const tick = this.linkshellPrimaryResetTick;
      if (tick === 0) return;
      untracked(() => {
        this.inviteLinkshellId =
          this.activity.overview()?.primaryLinkshell?.id ??
          this.activity.overview()?.linkshells?.[0]?.id ??
          0;
        // Force a browse reload for the newly-resolved primary linkshell.
        this.browseLoadedFor = 0;
      });
    });

    // Connected Now: match current Discord participants to linked app users so
    // they can be one-click invited.
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

    // Load the Entire LSM Server list once per target linkshell (page 1). User
    // actions (search / paging / invite) reload it explicitly.
    effect(() => {
      const linkshellId = this.inviteTargetLinkshellId();
      const canManage = linkshellId > 0 && this.canManageLinkshell(linkshellId);
      if (!canManage) return;
      if (this.browseLoadedFor === linkshellId) return;
      this.browseLoadedFor = linkshellId;
      untracked(() => {
        if (this.inviteSource === 'all') this.page = 1;
        this.loadBrowse(1);
      });
    });

    // In a Linkshell: a picked roster belongs to the target it was marked for (Member,
    // Invited...), so a new target drops the pick and the list of linkshells to pick from.
    effect(() => {
      const linkshellId = this.inviteTargetLinkshellId();
      if (linkshellId === this.rosterResetFor) return;
      this.rosterResetFor = linkshellId;
      untracked(() => {
        this.rosterLinkshellId = null;
        this.sourcesLoadedFor = 0;
        this.activity.clearInviteRoster();
        if (this.inviteSource === 'linkshell') this.loadSources();
      });
    });

    // Load the "From My Discord Server" roster once per target linkshell, but
    // only when that linkshell is locked to a Discord server (otherwise there's
    // no roster to pull). Clears when switching to an unlocked linkshell.
    effect(() => {
      const linkshellId = this.inviteTargetLinkshellId();
      const canManage = linkshellId > 0 && this.canManageLinkshell(linkshellId);
      const locked = this.targetLinkshellLocked();

      if (!canManage || !locked) {
        if (this.discordRosterLoadedFor !== 0) {
          this.discordRosterLoadedFor = 0;
          untracked(() => this.activity.clearDiscordRoster());
        }
        return;
      }

      if (this.discordRosterLoadedFor === linkshellId) return;
      this.discordRosterLoadedFor = linkshellId;
      untracked(() => void this.activity.loadDiscordRoster(linkshellId));
    });
  }

  protected targetLinkshellLocked(): boolean {
    const id = this.inviteTargetLinkshellId();
    const link = this.linkshellMemberships().find(membership => membership.id === id);
    return !!link?.settings?.discordGuildId;
  }

  /** The dropdown's choices. Connected Now needs the Discord Activity (the website has no
   *  participants); From My Discord Server needs a linkshell locked to a server. */
  protected sourceOptions(): { key: InviteSource; label: string }[] {
    return [
      { key: 'all', label: 'Entire LSM Server' },
      { key: 'linkshell', label: 'In a Linkshell' },
      ...(this.activity.isWebsite ? [] : [{ key: 'connected' as const, label: 'Connected Now' }]),
      ...(this.targetLinkshellLocked() ? [{ key: 'discord' as const, label: 'From My Discord Server' }] : [])
    ];
  }

  /** The picked source, or Entire LSM Server when the pick is no longer offered (the
   *  linkshell stopped being locked, say). */
  protected source(): InviteSource {
    return this.sourceOptions().some(option => option.key === this.inviteSource) ? this.inviteSource : 'all';
  }

  protected onSourceChange(source: InviteSource): void {
    this.inviteSource = source;
    this.page = 1;
    if (source === 'all') {
      this.loadBrowse(1);
    } else if (source === 'linkshell') {
      this.loadSources();
    }
  }

  protected onRosterLinkshellChange(sourceLinkshellId: number | null): void {
    this.rosterLinkshellId = sourceLinkshellId;
    this.page = 1;
    this.loadRoster();
  }

  protected onSearchTermChange(term: string): void {
    this.searchTerm = term;
    // Entire LSM Server waits for Enter / Search; the others filter as you type.
    if (this.source() !== 'all') this.page = 1;
  }

  protected onSearch(): void {
    this.page = 1;
    if (this.source() === 'all') this.loadBrowse(1);
  }

  protected linkshellMemberships() {
    return this.activity.overview()?.linkshells ?? [];
  }

  protected isManagerMode(): boolean {
    return this.linkshellMemberships().some(link => this.canManageLinkshell(link.id));
  }

  protected isMemberMode(): boolean {
    return !this.isManagerMode();
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    return canManageLinkshellIn(this.activity.overview(), linkshellId);
  }

  protected inviteTargetLinkshellId(): number {
    return (
      this.inviteLinkshellId ||
      this.activity.overview()?.primaryLinkshell?.id ||
      this.activity.overview()?.linkshells?.[0]?.id ||
      0
    );
  }

  // Sent invites for the linkshell currently being managed only — never invites
  // belonging to OTHER linkshells the caller happens to be a member of. Keeps the
  // "Sent" list consistent with the rest of this card, which is all scoped to the
  // selected invite-target linkshell.
  protected sentInvitesForTarget() {
    const targetId = this.inviteTargetLinkshellId();
    return (this.activity.overview()?.sentInvites ?? [])
      .filter(invite => invite.linkshellId === targetId);
  }

  /** Every row the picked source has, already narrowed by the search box (except Entire
   *  LSM Server, which the server searched, and which is only the current page). */
  protected rows(): InviteResultRow[] {
    const term = this.searchTerm.trim().toLowerCase();
    const matches = (name: string) => !term || name.toLowerCase().includes(term);

    switch (this.source()) {
      case 'all':
        return this.activity.inviteBrowseResults().map(result => ({
          key: `user:${result.id}`,
          name: result.displayName,
          detail: result.primaryLinkshellName ? `Primary: ${result.primaryLinkshellName}` : null,
          avatarUrl: null,
          outsideServer: result.inDiscordServer === false,
          invite: { appUserId: result.id },
          status: null
        }));

      case 'linkshell':
        return this.activity.inviteRoster()
          .filter(row => matches(row.displayName))
          .map((row, index) => ({
            key: row.appUserId ? `user:${row.appUserId}` : `row:${index}`,
            name: row.displayName,
            detail: null,
            avatarUrl: null,
            outsideServer: row.inDiscordServer === false,
            invite: row.state === 'invite' && row.appUserId ? { appUserId: row.appUserId } : null,
            status: ROSTER_STATUS[row.state] ?? null
          }));

      case 'connected': {
        // Someone connected on two devices comes back twice; list them once.
        const byUser = new Map(this.activity.participantInviteCandidates()
          .map(candidate => [candidate.appUserId, candidate] as const));
        return [...byUser.values()]
          .filter(candidate => matches(candidate.displayName))
          .map(candidate => ({
            key: `user:${candidate.appUserId}`,
            name: candidate.displayName,
            detail: candidate.primaryLinkshellName ? `Primary: ${candidate.primaryLinkshellName}` : null,
            avatarUrl: null,
            outsideServer: false,
            invite: { appUserId: candidate.appUserId },
            status: null
          }));
      }

      case 'discord':
        return this.activity.discordRosterCandidates()
          .filter(candidate => matches(candidate.displayName))
          .map(candidate => ({
            key: `discord:${candidate.discordUserId}`,
            name: candidate.displayName,
            detail: candidate.hasLsmAccount ? null : 'New to LSM — auto-joins on first sign-in',
            avatarUrl: candidate.avatarUrl || null,
            outsideServer: false,
            invite: { discordUserId: candidate.discordUserId },
            status: null
          }));
    }
  }

  /** The rows on the current page. */
  protected pageRows(): InviteResultRow[] {
    const rows = this.rows();
    if (this.source() === 'all') return rows;
    const start = (this.currentPage() - 1) * this.pageSize;
    return rows.slice(start, start + this.pageSize);
  }

  protected totalCount(): number {
    return this.source() === 'all' ? this.activity.inviteBrowseTotal() : this.rows().length;
  }

  protected totalPages(): number {
    return Math.max(1, Math.ceil(this.totalCount() / this.pageSize));
  }

  protected currentPage(): number {
    return Math.min(Math.max(1, this.page), this.totalPages());
  }

  protected prevPage(): void {
    this.goToPage(this.currentPage() - 1);
  }

  protected nextPage(): void {
    this.goToPage(this.currentPage() + 1);
  }

  private goToPage(page: number): void {
    if (page < 1 || page > this.totalPages()) return;
    if (this.source() === 'all') {
      this.loadBrowse(page);
    } else {
      this.page = page;
    }
  }

  /** Null when the picked source isn't loading. */
  protected busyMessage(): string | null {
    switch (this.source()) {
      case 'all': return this.activity.inviteBrowseBusy() ? 'Loading members...' : null;
      case 'linkshell': return this.activity.inviteRosterBusy() ? 'Loading that roster...' : null;
      case 'connected': return this.activity.participantInviteBusy() ? 'Matching who is connected to app users...' : null;
      case 'discord': return this.activity.discordRosterBusy() ? 'Loading your Discord server roster...' : null;
    }
  }

  protected emptyMessage(): string {
    const searching = this.searchTerm.trim().length > 0;
    switch (this.source()) {
      case 'all':
        return searching ? 'No one matched your search.' : 'No eligible members found.';
      case 'linkshell':
        if (this.rosterLinkshellId === null) return 'Pick a linkshell to see its roster.';
        return searching ? 'No one on that roster matched your search.' : 'That linkshell has no members.';
      case 'connected':
        return searching
          ? 'No one connected matched your search.'
          : 'No one connected right now can be invited.';
      case 'discord':
        if (searching) return 'No Discord server members matched your search.';
        return "No one left to add from your Discord server, or the bot can't read it yet.";
    }
  }

  protected loadBrowse(page: number): void {
    const linkshellId = this.inviteTargetLinkshellId();
    if (!linkshellId || !this.canManageLinkshell(linkshellId)) return;
    if (this.source() === 'all') this.page = page;
    void this.activity.browsePlayers(linkshellId, {
      query: this.searchTerm,
      page,
      pageSize: this.pageSize
    });
  }

  private loadSources(): void {
    const linkshellId = this.inviteTargetLinkshellId();
    if (!linkshellId || this.sourcesLoadedFor === linkshellId) return;
    this.sourcesLoadedFor = linkshellId;
    void this.activity.loadInviteSources(linkshellId);
  }

  private loadRoster(): void {
    const linkshellId = this.inviteTargetLinkshellId();
    if (!linkshellId || this.rosterLinkshellId === null) {
      this.activity.clearInviteRoster();
      return;
    }
    void this.activity.loadInviteRoster(linkshellId, this.rosterLinkshellId);
  }

  protected async invite(row: InviteResultRow): Promise<void> {
    const linkshellId = this.inviteTargetLinkshellId();
    if (!linkshellId || !row.invite) {
      this.activity.actionError.set('Select a linkshell before sending invites.');
      this.activity.actionMessage.set(null);
      return;
    }

    if ('discordUserId' in row.invite) {
      // The service drops the added person from the Discord list itself.
      await this.activity.inviteDiscordUser(linkshellId, row.invite.discordUserId);
      return;
    }

    await this.activity.sendInvite(linkshellId, row.invite.appUserId);
    // Refresh whatever is showing, so the added person drops off (or reads Member), and
    // the Connected Now list.
    if (this.source() === 'all') this.loadBrowse(this.currentPage());
    if (this.source() === 'linkshell') this.loadRoster();
    await this.activity.loadParticipantInviteCandidates(
      linkshellId,
      this.activity.participants().map(participant => participant.id)
    );
  }

  protected isInviting(row: InviteResultRow): boolean {
    return !!row.invite && 'discordUserId' in row.invite
      && this.activity.busyDiscordUserId() === row.invite.discordUserId;
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
}
