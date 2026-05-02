import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, effect, inject, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DiscordActivityService } from '../../discord/discord-activity.service';

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
  // selection to the freshly-resolved primary linkshell — preserves the
  // pre-refactor behavior where creating a linkshell auto-pointed the
  // invite UI at it.
  @Input() linkshellPrimaryResetTick = 0;

  protected inviteSearchTerm = '';
  protected inviteLinkshellId = 0;
  private participantInviteSeed = '';

  public constructor() {
    effect(() => {
      const tick = this.linkshellPrimaryResetTick;
      // Skip the initial firing (tick === 0) — preserves the pre-refactor
      // initial state of `inviteLinkshellId = 0`. The parent only bumps
      // the tick after a brand-new linkshell is created, at which point
      // we re-anchor the invite UI to the new primary.
      if (tick === 0) return;
      // Read overview untracked so this effect doesn't re-fire on every
      // overview refresh — only when the parent explicitly bumps the tick.
      untracked(() => {
        this.inviteLinkshellId =
          this.activity.overview()?.primaryLinkshell?.id ??
          this.activity.overview()?.linkshells?.[0]?.id ??
          0;
      });
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
    const membership = this.linkshellMemberships().find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
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

  protected onInviteLinkshellChange(value: number): void {
    this.inviteLinkshellId = value;
    this.participantInviteSeed = '';
    if (this.inviteSearchTerm.trim().length >= 2) {
      void this.activity.searchPlayers(this.inviteSearchTerm, this.inviteLinkshellId);
    }
  }

  protected async runInviteSearch(): Promise<void> {
    const linkshellId = this.inviteTargetLinkshellId();

    this.inviteLinkshellId = linkshellId;
    await this.activity.searchPlayers(this.inviteSearchTerm, linkshellId);
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
