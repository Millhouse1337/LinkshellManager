import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityCreateLinkshellInput,
  ActivityLinkshellRole,
  DiscordActivityService
} from '../../discord/discord-activity.service';

@Component({
  selector: 'app-roster-panel',
  imports: [CommonModule, FormsModule],
  templateUrl: './roster-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RosterPanelComponent {
  protected readonly activity = inject(DiscordActivityService);

  @Input({ required: true }) selectedLinkshellId!: number;
  @Input({ required: true }) selectLinkshell!: (linkshellId: number) => Promise<void> | void;
  @Input({ required: true }) onPrimaryLinkshellChanged!: () => void;

  protected editingLinkshellId: number | null = null;
  protected readonly createLinkshellModel: ActivityCreateLinkshellInput = {
    name: '',
    details: ''
  };
  protected isCreateLinkshellOpen = false;
  protected isSubmittingLinkshell = false;
  protected memberSearchTerm = '';
  protected memberRoleFilter: 'all' | 'leader' | 'officer' | 'member' = 'all';
  protected selectedJoinLinkshellId = 0;
  protected readonly rolesByLinkshell = signal<Record<number, ActivityLinkshellRole[]>>({});

  public constructor() {
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

  protected canManageLinkshell(linkshellId: number): boolean {
    const membership = this.linkshellMemberships().find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
  }

  protected canManageMembers(): boolean {
    if (!this.selectedLinkshellId) {
      return false;
    }

    const currentMembership = this.linkshellMemberships().find(link => link.id === this.selectedLinkshellId);
    return (currentMembership?.rank ?? '').toLowerCase() === 'leader';
  }

  protected canRequestLinkshellAccess(): boolean {
    return this.linkshellMemberships().length === 0;
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

  protected primaryLinkshellActiveEventCount(): number {
    if (!this.selectedLinkshellId) {
      return 0;
    }

    return (this.activity.overview()?.activeEvents ?? []).filter(event => event.linkshellId === this.selectedLinkshellId).length;
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

  protected memberInitials(name?: string | null): string {
    const trimmed = (name ?? '').trim();
    if (!trimmed) {
      return '?';
    }

    const parts = trimmed.split(/\s+/).filter(Boolean);
    if (parts.length === 1) {
      return parts[0].substring(0, 2).toUpperCase();
    }

    return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
  }

  protected memberAvatarClass(name?: string | null): string {
    const trimmed = (name ?? '').trim();
    if (!trimmed) {
      return 'a';
    }

    let hash = 0;
    for (let i = 0; i < trimmed.length; i += 1) {
      hash = (hash * 31 + trimmed.charCodeAt(i)) >>> 0;
    }

    return ['a', 'b', 'c', 'd', 'e'][hash % 5];
  }

  protected memberStatusClass(status?: string | null): string {
    const normalized = (status ?? 'Active').toLowerCase();
    if (normalized === 'active') {
      return 'success';
    }
    if (normalized === 'pending') {
      return 'warning';
    }
    return 'default';
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
      this.onPrimaryLinkshellChanged();
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

  protected selectedJoinLinkshell() {
    return this.activity.linkshellSearchResults().find(linkshell => linkshell.id === this.selectedJoinLinkshellId) ?? null;
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

  protected async ensureRolesLoaded(linkshellId: number): Promise<void> {
    if (this.rolesByLinkshell()[linkshellId]) return;
    const data = await this.activity.loadLinkshellRoles(linkshellId);
    if (data) {
      this.rolesByLinkshell.update(map => ({ ...map, [linkshellId]: data.roles }));
    }
  }

  protected availableRolesForLinkshell(linkshellId: number): ActivityLinkshellRole[] {
    void this.ensureRolesLoaded(linkshellId);
    return this.rolesByLinkshell()[linkshellId] ?? [];
  }

  protected async changeMemberRole(linkshellId: number, memberId: number, characterName: string, newRole: string): Promise<void> {
    const trimmed = newRole?.trim();
    if (!trimmed) return;
    const promoteToLeader = trimmed.toLowerCase() === 'leader';
    const confirmation = promoteToLeader
      ? `Transfer linkshell leadership to ${characterName}? You will become an officer.`
      : `Change ${characterName}'s role to ${trimmed}?`;
    if (!window.confirm(confirmation)) return;
    await this.activity.updateLinkshellMemberRole(linkshellId, memberId, trimmed, characterName);
  }
}
