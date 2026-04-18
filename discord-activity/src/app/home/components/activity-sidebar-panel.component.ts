import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivityCreateLinkshellInput, ActivityLootInput, DiscordActivityService } from '../../discord/discord-activity.service';

@Component({
  selector: 'app-activity-sidebar-panel',
  imports: [CommonModule, FormsModule],
  templateUrl: './activity-sidebar-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ActivitySidebarPanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected editingLinkshellId: number | null = null;
  protected readonly createLinkshellModel: ActivityCreateLinkshellInput = {
    name: '',
    details: ''
  };
  protected inviteSearchTerm = '';
  protected inviteLinkshellId = 0;
  protected memberSearchTerm = '';
  protected memberRoleFilter: 'all' | 'leader' | 'officer' | 'member' = 'all';
  protected isCreateLinkshellOpen = false;
  protected isSubmittingLinkshell = false;

  protected primaryLinkshell() {
    return this.activity.overview()?.primaryLinkshell ?? null;
  }

  protected linkshellMemberships() {
    return this.activity.overview()?.linkshells ?? [];
  }

  protected appUserId(): string | null {
    return this.activity.overview()?.appUser?.id ?? null;
  }

  protected primaryLinkshellId(): number | null {
    return this.activity.overview()?.appUser?.primaryLinkshellId ?? this.primaryLinkshell()?.id ?? null;
  }

  protected canManageMembers(): boolean {
    const primaryLinkshellId = this.primaryLinkshellId();
    if (!primaryLinkshellId) {
      return false;
    }

    const currentMembership = this.linkshellMemberships().find(link => link.id === primaryLinkshellId);
    return (currentMembership?.rank ?? '').toLowerCase() === 'leader';
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    const membership = this.linkshellMemberships().find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
  }

  protected filteredPrimaryLinkshellMembers() {
    const primary = this.primaryLinkshell();
    if (!primary) {
      return [];
    }

    const normalizedSearch = this.memberSearchTerm.trim().toLowerCase();
    return primary.members.filter(member => {
      const matchesRole =
        this.memberRoleFilter === 'all' ||
        (member.rank ?? 'Member').toLowerCase() === this.memberRoleFilter;

      const matchesSearch =
        !normalizedSearch ||
        member.characterName.toLowerCase().includes(normalizedSearch);

      return matchesRole && matchesSearch;
    });
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

  protected openCreateLinkshellForm(): void {
    this.activity.clearActionState();
    this.isCreateLinkshellOpen = true;
    this.editingLinkshellId = null;
    this.createLinkshellModel.name = '';
    this.createLinkshellModel.details = '';
  }

  protected openEditLinkshellForm(): void {
    const primary = this.primaryLinkshell();
    if (!primary) {
      return;
    }

    this.activity.clearActionState();
    this.isCreateLinkshellOpen = true;
    this.editingLinkshellId = primary.id;
    this.createLinkshellModel.name = primary.name;
    this.createLinkshellModel.details = primary.details ?? '';
  }

  protected closeCreateLinkshellForm(): void {
    this.isCreateLinkshellOpen = false;
    this.editingLinkshellId = null;
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

  protected onInviteLinkshellChange(value: number): void {
    this.inviteLinkshellId = value;
    if (this.inviteSearchTerm.trim().length >= 2) {
      void this.activity.searchPlayers(this.inviteSearchTerm, this.inviteLinkshellId);
    }
  }

  protected async runInviteSearch(): Promise<void> {
    const linkshellId =
      this.inviteLinkshellId ||
      this.activity.overview()?.primaryLinkshell?.id ||
      this.activity.overview()?.linkshells?.[0]?.id ||
      0;

    this.inviteLinkshellId = linkshellId;
    await this.activity.searchPlayers(this.inviteSearchTerm, linkshellId);
  }

  protected async sendInvite(appUserId: string): Promise<void> {
    const linkshellId =
      this.inviteLinkshellId ||
      this.activity.overview()?.primaryLinkshell?.id ||
      this.activity.overview()?.linkshells?.[0]?.id ||
      0;

    if (!linkshellId) {
      this.activity.actionError.set('Select a linkshell before sending invites.');
      this.activity.actionMessage.set(null);
      return;
    }

    await this.activity.sendInvite(linkshellId, appUserId);
    await this.activity.searchPlayers(this.inviteSearchTerm, linkshellId);
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
}
