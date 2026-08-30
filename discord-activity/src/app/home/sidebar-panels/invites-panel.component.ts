import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, effect, inject, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DiscordActivityService } from '../../discord/discord-activity.service';import { ADMIN_BADGE, canManageLinkshellIn } from '../activity-home.helpers';

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

  // Browse-all-members state: a paginated, searchable, filterable list of every
  // LSM user eligible to invite (anyone who's used the app at least once). No
  // Discord bot needed — these come from our own database.
  protected browseTerm = '';
  protected browseFilter: 'all' | 'unaffiliated' | 'affiliated' = 'all';
  protected browsePage = 1;
  protected readonly browsePageSize = 10;
  private browseLoadedFor = 0;

  // Tracks which (locked) linkshell the "From your Discord server" roster has
  // been loaded for, so the effect doesn't refetch on every overview refresh.
  private discordRosterLoadedFor = 0;
  protected discordRosterSearchTerm = '';
  protected discordRosterPage = 1;
  protected readonly discordRosterPageSize = 9;

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

    // Connected-now quick invites: match current Discord participants to linked
    // app users so they can be one-click invited.
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

    // Load the browse list once per target linkshell (page 1). User actions
    // (search / filter / paging / invite) reload it explicitly.
    effect(() => {
      const linkshellId = this.inviteTargetLinkshellId();
      const canManage = linkshellId > 0 && this.canManageLinkshell(linkshellId);
      if (!canManage) return;
      if (this.browseLoadedFor === linkshellId) return;
      this.browseLoadedFor = linkshellId;
      untracked(() => {
        this.browsePage = 1;
        this.loadBrowse(1);
      });
    });

    // Load the "From your Discord server" roster once per target linkshell, but
    // only when that linkshell is locked to a Discord server (otherwise there's
    // no roster to pull). Clears when switching to an unlocked linkshell.
    effect(() => {
      const linkshellId = this.inviteTargetLinkshellId();
      const canManage = linkshellId > 0 && this.canManageLinkshell(linkshellId);
      const locked = this.targetLinkshellLocked();

      if (!canManage || !locked) {
        if (this.discordRosterLoadedFor !== 0) {
          this.discordRosterLoadedFor = 0;
          untracked(() => {
            this.discordRosterSearchTerm = '';
            this.discordRosterPage = 1;
            this.activity.clearDiscordRoster();
          });
        }
        return;
      }

      if (this.discordRosterLoadedFor === linkshellId) return;
      this.discordRosterLoadedFor = linkshellId;
      untracked(() => {
        this.discordRosterSearchTerm = '';
        this.discordRosterPage = 1;
        void this.activity.loadDiscordRoster(linkshellId);
      });
    });
  }

  protected targetLinkshellLocked(): boolean {
    const id = this.inviteTargetLinkshellId();
    const link = this.linkshellMemberships().find(membership => membership.id === id);
    return !!link?.settings?.discordGuildId;
  }

  protected async inviteFromDiscord(discordUserId: string): Promise<void> {
    const linkshellId = this.inviteTargetLinkshellId();
    if (!linkshellId) {
      this.activity.actionError.set('Select a linkshell before sending invites.');
      this.activity.actionMessage.set(null);
      return;
    }
    await this.activity.inviteDiscordUser(linkshellId, discordUserId);
  }

  protected get discordRosterSearch(): string {
    return this.discordRosterSearchTerm;
  }

  protected set discordRosterSearch(value: string) {
    this.discordRosterSearchTerm = value;
    this.discordRosterPage = 1;
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

  protected connectedInviteCandidates() {
    const seen = new Set<string>();
    return this.activity.participantInviteCandidates().filter(candidate => {
      if (seen.has(candidate.appUserId)) return false;
      seen.add(candidate.appUserId);
      return true;
    });
  }

  // Hide anyone already shown under "Connected now" so they don't appear twice.
  protected filteredBrowseResults() {
    const connectedIds = new Set(
      this.connectedInviteCandidates().map(candidate => candidate.appUserId)
    );
    return this.activity.inviteBrowseResults().filter(result => !connectedIds.has(result.id));
  }

  protected filteredDiscordRosterCandidates() {
    const term = this.discordRosterSearch.trim().toLowerCase();
    const candidates = this.activity.discordRosterCandidates();
    if (!term) {
      return candidates;
    }

    return candidates.filter(candidate => candidate.displayName.toLowerCase().includes(term));
  }

  protected discordRosterTotalPages(): number {
    return Math.max(1, Math.ceil(this.filteredDiscordRosterCandidates().length / this.discordRosterPageSize));
  }

  protected discordRosterCurrentPage(): number {
    return Math.min(Math.max(1, this.discordRosterPage), this.discordRosterTotalPages());
  }

  protected pagedDiscordRosterCandidates() {
    const start = (this.discordRosterCurrentPage() - 1) * this.discordRosterPageSize;
    return this.filteredDiscordRosterCandidates().slice(start, start + this.discordRosterPageSize);
  }

  protected discordRosterPrev(): void {
    this.discordRosterPage = Math.max(1, this.discordRosterCurrentPage() - 1);
  }

  protected discordRosterNext(): void {
    this.discordRosterPage = Math.min(this.discordRosterTotalPages(), this.discordRosterCurrentPage() + 1);
  }

  protected browseTotalPages(): number {
    return Math.max(1, Math.ceil(this.activity.inviteBrowseTotal() / this.browsePageSize));
  }

  protected loadBrowse(page: number): void {
    const linkshellId = this.inviteTargetLinkshellId();
    if (!linkshellId || !this.canManageLinkshell(linkshellId)) return;
    this.browsePage = page;
    void this.activity.browsePlayers(linkshellId, {
      query: this.browseTerm,
      filter: this.browseFilter,
      page,
      pageSize: this.browsePageSize
    });
  }

  protected onBrowseSearch(): void {
    this.loadBrowse(1);
  }

  protected onBrowseFilterChange(value: 'all' | 'unaffiliated' | 'affiliated'): void {
    this.browseFilter = value;
    this.loadBrowse(1);
  }

  protected browsePrev(): void {
    if (this.browsePage > 1) this.loadBrowse(this.browsePage - 1);
  }

  protected browseNext(): void {
    if (this.browsePage < this.browseTotalPages()) this.loadBrowse(this.browsePage + 1);
  }

  protected async sendInvite(appUserId: string): Promise<void> {
    const linkshellId = this.inviteTargetLinkshellId();

    if (!linkshellId) {
      this.activity.actionError.set('Select a linkshell before sending invites.');
      this.activity.actionMessage.set(null);
      return;
    }

    await this.activity.sendInvite(linkshellId, appUserId);
    // Refresh the current browse page (the invited user drops off) and the
    // connected-now shortcut list.
    this.loadBrowse(this.browsePage);
    await this.activity.loadParticipantInviteCandidates(
      linkshellId,
      this.activity.participants().map(participant => participant.id)
    );
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
