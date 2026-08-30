import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input, effect, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityCreateLinkshellInput,
  ActivityLinkshellRole,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import {
  ADMIN_BADGE,
  canManageLinkshellIn,
  formatAlts,
  isLeaderTierIn,
  memberAvatarClass,
  memberInitials,
  memberStatusClass,
  rankIcon
} from '../activity-home.helpers';

@Component({
  selector: 'app-roster-panel',
  imports: [CommonModule, FormsModule],
  templateUrl: './roster-panel.component.html',
  styleUrl: './roster-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RosterPanelComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly formatAlts = formatAlts;
  // Shared roster helpers (avatar initials/color, status tag) — single source in
  // activity-home.helpers so every roster renders members identically.
  protected readonly memberInitials = memberInitials;
  protected readonly memberAvatarClass = memberAvatarClass;
  protected readonly memberStatusClass = memberStatusClass;

  readonly selectedLinkshellId = input.required<number>();
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

  // Discord Activities run in a sandboxed iframe without `allow-modals`, so
  // window.confirm() returns false silently and destructive actions never
  // run. Drive confirmations through this signal + an in-app modal instead.
  protected readonly pendingConfirm = signal<{
    title: string;
    message: string;
    confirmLabel: string;
    danger: boolean;
    confirm: () => Promise<void> | void;
  } | null>(null);

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

    // Eagerly load the role list for the currently-selected linkshell when
    // the user can manage members. Doing this in an effect (instead of from
    // a template-render-time getter) avoids kicking off fetches during change
    // detection and the duplicate-fetch races that pattern produces.
    effect(() => {
      const selectedId = this.selectedLinkshellId();
      if (!selectedId || !this.canManageMembers()) {
        return;
      }
      void this.ensureRolesLoaded(selectedId);
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
    return canManageLinkshellIn(this.activity.overview(), linkshellId);
  }

  protected canManageMembers(): boolean {
    const selectedId = this.selectedLinkshellId();
    if (!selectedId) {
      return false;
    }

    return isLeaderTierIn(this.activity.overview(), selectedId, this.linkshellMemberships());
  }

  protected canRequestLinkshellAccess(): boolean {
    return this.linkshellMemberships().length === 0;
  }

  protected selectedLinkshell() {
    const selectedId = this.selectedLinkshellId();
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
    const selectedId = this.selectedLinkshellId();
    if (!selectedId) {
      return 0;
    }

    return (this.activity.overview()?.activeEvents ?? []).filter(event => event.linkshellId === selectedId).length;
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

  protected confirmDeleteLinkshell(linkshellId: number, linkshellName: string): void {
    this.pendingConfirm.set({
      title: `Delete ${linkshellName}?`,
      message: `This removes ${linkshellName}'s events, history, invites, and memberships.`,
      confirmLabel: 'Delete',
      danger: true,
      confirm: () => this.activity.deleteLinkshell(linkshellId)
    });
  }

  protected confirmLeaveLinkshell(linkshellId: number, linkshellName: string): void {
    this.pendingConfirm.set({
      title: `Leave ${linkshellName}?`,
      message: `You'll lose access to this linkshell and need a new invite to rejoin.`,
      confirmLabel: 'Leave',
      danger: true,
      confirm: () => this.activity.leaveLinkshell(linkshellId)
    });
  }

  protected confirmRemoveMember(linkshellId: number, memberId: number, characterName: string): void {
    this.pendingConfirm.set({
      title: `Remove ${characterName}?`,
      message: `Remove ${characterName} from the linkshell? They'll lose all access until re-invited.`,
      confirmLabel: 'Remove',
      danger: true,
      confirm: () => this.activity.removeLinkshellMember(linkshellId, memberId)
    });
  }

  protected cancelPendingConfirm(): void {
    this.pendingConfirm.set(null);
  }

  protected async runPendingConfirm(): Promise<void> {
    const pending = this.pendingConfirm();
    if (!pending) return;
    this.pendingConfirm.set(null);
    try {
      await pending.confirm();
    } catch {
      // Action errors are surfaced via activity.actionError.
    }
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
    return this.rolesByLinkshell()[linkshellId] ?? [];
  }

  protected changeMemberRole(linkshellId: number, memberId: number, characterName: string, newRole: string): void {
    const trimmed = newRole.trim();
    if (!trimmed) return;
    const promoteToLeader = trimmed.toLowerCase() === 'leader';
    this.pendingConfirm.set({
      title: promoteToLeader ? `Transfer leadership?` : `Change role?`,
      message: promoteToLeader
        ? `Transfer linkshell leadership to ${characterName}? You will become an officer.`
        : `Change ${characterName}'s role to ${trimmed}?`,
      confirmLabel: promoteToLeader ? 'Transfer' : 'Change',
      danger: promoteToLeader,
      confirm: () => this.activity.updateLinkshellMemberRole(linkshellId, memberId, trimmed, characterName)
    });
  }

  protected readonly rankIcon = rankIcon;
  protected readonly ADMIN_BADGE = ADMIN_BADGE;
  protected readonly statusOptions = ['Active', 'Pending', 'Inactive'] as const;

  protected changeMemberStatus(linkshellId: number, memberId: number, characterName: string, newStatus: string): void {
    const trimmed = newStatus.trim();
    if (!trimmed) return;
    this.pendingConfirm.set({
      title: 'Change status?',
      message: `Set ${characterName}'s status to ${trimmed}?`,
      confirmLabel: 'Change',
      danger: false,
      confirm: () => this.activity.updateLinkshellMemberStatus(linkshellId, memberId, trimmed, characterName)
    });
  }
}
