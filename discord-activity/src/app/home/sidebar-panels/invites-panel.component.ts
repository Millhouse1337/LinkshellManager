import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, effect, inject, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import { canManageLinkshellIn } from '../activity-home.helpers';

/** Where the invite card looks: the dropdown beside its search box. */
type InviteSource = 'all' | 'discord';

/** One row of the invite card's results, whichever source it came from. */
interface InviteResultRow {
  key: string;
  name: string;
  detail: string | null;
  avatarUrl: string | null;
  /** Someone who isn't in the linkshell's Discord server (set in Configurations). */
  outsideServer: boolean;
  invite: { appUserId: string } | { discordUserId: string };
}

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

  // The invite card: one search box, the dropdown saying where to look, and one list.
  // "Entire LSM Server" searches the server (Enter / Search) a page at a time; "From My
  // Discord Server" is loaded whole, so the box filters it as you type.
  protected searchTerm = '';
  protected inviteSource: InviteSource = 'all';
  protected page = 1;
  protected readonly pageSize = 10;
  private browseLoadedFor = 0;
  // The Discord list is per target linkshell (it leaves out who's already in it), so it
  // loads the first time it's picked for each one.
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

    // A new target linkshell: the Discord list was for the old one, so drop it, and
    // reload it if it's the one showing.
    effect(() => {
      const linkshellId = this.inviteTargetLinkshellId();
      untracked(() => {
        if (this.discordRosterLoadedFor === 0 || this.discordRosterLoadedFor === linkshellId) return;
        this.discordRosterLoadedFor = 0;
        this.activity.clearDiscordRoster();
        if (this.source() === 'discord') this.loadDiscordRoster();
      });
    });
  }

  /** The dropdown's choices. From My Discord Server needs a Discord server set for the
   *  linkshell in Configurations. */
  protected sourceOptions(): { key: InviteSource; label: string }[] {
    return [
      { key: 'all', label: 'Entire LSM Server' },
      ...(this.targetDiscordServer() ? [{ key: 'discord' as const, label: 'From My Discord Server' }] : [])
    ];
  }

  /** The Discord server set for the target linkshell in Configurations, or null. */
  protected targetDiscordServer(): { id: string; name: string | null } | null {
    const id = this.inviteTargetLinkshellId();
    const settings = this.linkshellMemberships().find(membership => membership.id === id)?.settings;
    return settings?.discordGuildId
      ? { id: settings.discordGuildId, name: settings.discordGuildName ?? null }
      : null;
  }

  /** The picked source, or Entire LSM Server when the pick isn't offered here. */
  protected source(): InviteSource {
    return this.sourceOptions().some(option => option.key === this.inviteSource) ? this.inviteSource : 'all';
  }

  protected onSourceChange(source: InviteSource): void {
    this.inviteSource = source;
    this.page = 1;
    if (source === 'all') {
      this.loadBrowse(1);
    } else {
      this.loadDiscordRoster();
    }
  }

  protected onSearchTermChange(term: string): void {
    this.searchTerm = term;
    // Entire LSM Server waits for Enter / Search; the Discord list filters as you type.
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

  /** Every row the picked source has: for Entire LSM Server the server's current page; for
   *  the Discord list, all of it narrowed by the search box. */
  protected rows(): InviteResultRow[] {
    if (this.source() === 'all') {
      return this.activity.inviteBrowseResults().map(result => ({
        key: `user:${result.id}`,
        name: result.displayName,
        detail: result.primaryLinkshellName ? `Primary: ${result.primaryLinkshellName}` : null,
        avatarUrl: null,
        outsideServer: result.inDiscordServer === false,
        invite: { appUserId: result.id }
      }));
    }

    const term = this.searchTerm.trim().toLowerCase();
    return this.activity.discordRosterCandidates()
      .filter(candidate => !term || candidate.displayName.toLowerCase().includes(term))
      .map(candidate => ({
        key: `discord:${candidate.discordUserId}`,
        name: candidate.displayName,
        detail: candidate.hasLsmAccount ? null : 'New to LSM — auto-joins on first sign-in',
        avatarUrl: candidate.avatarUrl || null,
        outsideServer: false,
        invite: { discordUserId: candidate.discordUserId }
      }));
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
    if (this.source() === 'all') {
      return this.activity.inviteBrowseBusy() ? 'Loading members...' : null;
    }
    return this.activity.discordRosterBusy() ? 'Loading your Discord server roster...' : null;
  }

  protected emptyMessage(): string {
    const searching = this.searchTerm.trim().length > 0;
    if (this.source() === 'all') {
      return searching ? 'No one matched your search.' : 'No eligible members found.';
    }
    if (searching) return 'No Discord server members matched your search.';
    return "No one left to add from this Discord server, or the LSM bot can't read it yet.";
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

  private loadDiscordRoster(): void {
    const linkshellId = this.inviteTargetLinkshellId();
    if (!linkshellId || this.discordRosterLoadedFor === linkshellId) return;
    this.discordRosterLoadedFor = linkshellId;
    void this.activity.loadDiscordRoster(linkshellId);
  }

  protected async invite(row: InviteResultRow): Promise<void> {
    const linkshellId = this.inviteTargetLinkshellId();
    if (!linkshellId) {
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
    // Refresh the page so the added person drops off it.
    this.loadBrowse(this.currentPage());
  }

  protected isInviting(row: InviteResultRow): boolean {
    return 'discordUserId' in row.invite && this.activity.busyDiscordUserId() === row.invite.discordUserId;
  }
}
