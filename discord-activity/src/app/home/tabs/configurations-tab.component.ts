import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityAddonToken,
  ActivityDkpRoundingIncrement,
  ActivityLinkshellRole,
  ActivityLinkshellRolePermissionsInput,
  ActivityLootStructure,
  DiscordActivityService
} from '../../discord/discord-activity.service';

@Component({
  selector: 'app-configurations-tab',
  imports: [CommonModule, FormsModule],
  templateUrl: './configurations-tab.component.html',
  styleUrl: './configurations-tab.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ConfigurationsTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  private readonly destroyRef = inject(DestroyRef);

  public constructor() {
    // Match the parent's behavior on tab activation: prefetch roles and seed
    // the customize draft. The parent triggered these when switching to this
    // tab; with a child component we run them on construction (the @if in the
    // parent only mounts us when active).
    void this.loadRolesForSelectedLinkshell();
    this.syncCustomizeDraft();

    this.destroyRef.onDestroy(() => {
      if (this.addonCountdownTimer) {
        clearInterval(this.addonCountdownTimer);
        this.addonCountdownTimer = null;
      }
    });
  }

  // ----- Re-implemented small reads -----

  protected primaryLinkshell() {
    return this.activity.overview()?.primaryLinkshell ?? null;
  }

  protected dashboardLinkshells() {
    return this.activity.overview()?.linkshells ?? [];
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
      this.dashboardLinkshells()[0]?.id ??
      0
    );
  }

  // ----- Create linkshell -----

  protected showCreateLinkshellForm = signal(false);
  protected newLinkshellName = '';
  protected newLinkshellDetails = '';

  protected toggleCreateLinkshellForm(): void {
    this.showCreateLinkshellForm.update(value => !value);
    if (!this.showCreateLinkshellForm()) {
      this.newLinkshellName = '';
      this.newLinkshellDetails = '';
    }
  }

  protected async submitCreateLinkshell(): Promise<void> {
    const name = this.newLinkshellName.trim();
    if (!name) return;
    try {
      await this.activity.createLinkshell({
        name,
        details: this.newLinkshellDetails.trim() || null
      });
      this.newLinkshellName = '';
      this.newLinkshellDetails = '';
      this.showCreateLinkshellForm.set(false);
    } catch {
      // surfaced
    }
  }

  // --- Permissions / roles admin state ---
  protected readonly permissionKeys = [
    { key: 'canManageRoles', label: 'Manage roles & permissions' },
    { key: 'canManageMembers', label: 'Manage members (invite, rank, status)' },
    { key: 'canManageEvents', label: 'Manage events (create, edit, start, end, cancel)' },
    { key: 'canModerateLiveEvent', label: 'Moderate live events (verify attendance, break room)' },
    { key: 'canAddLoot', label: 'Add event loot entries' },
    { key: 'canManageInventory', label: 'Manage inventory (items)' },
    { key: 'canManageTreasury', label: 'Manage treasury (revenue)' },
    { key: 'canManageRules', label: 'Manage rules' },
    { key: 'canManageAnnouncements', label: 'Manage announcements' },
    { key: 'canManageTods', label: 'Manage ToDs' },
    { key: 'canAuditDkp', label: 'Audit DKP' },
    { key: 'canManageAuctions', label: 'Manage auctions' },
    { key: 'canCustomizeLinkshell', label: 'Customize linkshell settings' }
  ] as const;

  protected readonly rolesByLinkshell = signal<Record<number, ActivityLinkshellRole[]>>({});
  protected rolesLinkshellId: number | null = null;
  protected editingRoleId: number | null = null;
  protected readonly roleDraft: {
    name: string;
    permissions: Record<string, boolean>;
  } = { name: '', permissions: {} };
  protected showNewRoleForm = false;

  protected permissionsTargetLinkshellId(): number {
    return this.rolesLinkshellId ?? this.selectedDashboardLinkshellId();
  }

  protected currentLinkshellRoles(): ActivityLinkshellRole[] {
    const id = this.permissionsTargetLinkshellId();
    return this.rolesByLinkshell()[id] ?? [];
  }

  protected canManageRolesForSelectedLinkshell(): boolean {
    const id = this.permissionsTargetLinkshellId();
    const linkshell = this.dashboardLinkshells().find(l => l.id === id);
    return !!linkshell?.permissions?.canManageRoles;
  }

  protected async loadRolesForSelectedLinkshell(): Promise<void> {
    const id = this.permissionsTargetLinkshellId();
    if (!id) return;
    const data = await this.activity.loadLinkshellRoles(id);
    if (data) {
      this.rolesByLinkshell.update(map => ({ ...map, [id]: data.roles }));
    }
  }

  protected onPermissionsLinkshellChange(linkshellId: number): void {
    this.rolesLinkshellId = linkshellId;
    this.editingRoleId = null;
    this.showNewRoleForm = false;
    void this.loadRolesForSelectedLinkshell();
  }

  protected beginEditRole(role: ActivityLinkshellRole): void {
    this.showNewRoleForm = false;
    this.editingRoleId = role.id;
    this.roleDraft.name = role.name;
    this.roleDraft.permissions = {};
    for (const perm of this.permissionKeys) {
      this.roleDraft.permissions[perm.key] = (role as any)[perm.key] === true;
    }
  }

  protected cancelRoleEdit(): void {
    this.editingRoleId = null;
    this.showNewRoleForm = false;
    this.roleDraft.name = '';
    this.roleDraft.permissions = {};
  }

  protected beginCreateRole(): void {
    this.editingRoleId = null;
    this.showNewRoleForm = true;
    this.roleDraft.name = '';
    this.roleDraft.permissions = {};
    for (const perm of this.permissionKeys) {
      this.roleDraft.permissions[perm.key] = false;
    }
  }

  protected async saveRoleDraft(): Promise<void> {
    const linkshellId = this.permissionsTargetLinkshellId();
    if (!linkshellId) return;

    const input: ActivityLinkshellRolePermissionsInput = {
      name: this.roleDraft.name?.trim() || null,
      canManageRoles: !!this.roleDraft.permissions['canManageRoles'],
      canManageMembers: !!this.roleDraft.permissions['canManageMembers'],
      canManageEvents: !!this.roleDraft.permissions['canManageEvents'],
      canModerateLiveEvent: !!this.roleDraft.permissions['canModerateLiveEvent'],
      canAddLoot: !!this.roleDraft.permissions['canAddLoot'],
      canManageInventory: !!this.roleDraft.permissions['canManageInventory'],
      canManageTreasury: !!this.roleDraft.permissions['canManageTreasury'],
      canManageRules: !!this.roleDraft.permissions['canManageRules'],
      canManageAnnouncements: !!this.roleDraft.permissions['canManageAnnouncements'],
      canManageTods: !!this.roleDraft.permissions['canManageTods'],
      canAuditDkp: !!this.roleDraft.permissions['canAuditDkp'],
      canManageAuctions: !!this.roleDraft.permissions['canManageAuctions'],
      canCustomizeLinkshell: !!this.roleDraft.permissions['canCustomizeLinkshell']
    };

    const ok = this.editingRoleId !== null
      ? await this.activity.updateLinkshellRole(linkshellId, this.editingRoleId, input)
      : await this.activity.createLinkshellRole(linkshellId, input);

    if (ok) {
      this.editingRoleId = null;
      this.showNewRoleForm = false;
      await this.loadRolesForSelectedLinkshell();
    }
  }

  protected isEditingSystemRole(): boolean {
    if (this.editingRoleId === null) return false;
    const role = this.currentLinkshellRoles().find(r => r.id === this.editingRoleId);
    return !!role?.isSystem;
  }

  protected pendingDeleteRoleId: number | null = null;

  protected requestDeleteRole(role: ActivityLinkshellRole): void {
    if (role.isSystem) return;
    this.pendingDeleteRoleId = role.id;
  }

  protected cancelDeleteRole(): void {
    this.pendingDeleteRoleId = null;
  }

  protected async confirmDeleteRole(role: ActivityLinkshellRole): Promise<void> {
    if (role.isSystem) return;
    const linkshellId = this.permissionsTargetLinkshellId();
    if (!linkshellId) return;
    const ok = await this.activity.deleteLinkshellRole(linkshellId, role.id);
    this.pendingDeleteRoleId = null;
    if (ok) {
      await this.loadRolesForSelectedLinkshell();
    }
  }

  // --- Customize Linkshell state ---
  protected readonly customizeDraft: {
    lootStructure: ActivityLootStructure;
    enableHnmSection: boolean;
    enableMissions: boolean;
    enableAuctions: boolean;
    enableToDs: boolean;
    enableEndgame: boolean;
    enableEvents: boolean;
    enableDkp: boolean;
    enableItems: boolean;
    enableRevenue: boolean;
    dkpRoundingIncrement: ActivityDkpRoundingIncrement;
  } = {
    lootStructure: 'Dkp',
    enableHnmSection: true,
    enableMissions: true,
    enableAuctions: true,
    enableToDs: true,
    enableEndgame: true,
    enableEvents: true,
    enableDkp: true,
    enableItems: true,
    enableRevenue: true,
    dkpRoundingIncrement: 'Quarter'
  };

  protected customizeLinkshellId: number | null = null;
  protected customizeDirty = false;

  protected customizeTargetLinkshellId(): number {
    return this.customizeLinkshellId ?? this.selectedDashboardLinkshellId();
  }

  protected canCustomizeSelectedLinkshell(): boolean {
    const id = this.customizeTargetLinkshellId();
    const link = this.dashboardLinkshells().find(l => l.id === id);
    return !!link?.permissions?.canCustomizeLinkshell;
  }

  protected syncCustomizeDraft(): void {
    const id = this.customizeTargetLinkshellId();
    const link = this.dashboardLinkshells().find(l => l.id === id);
    const settings = link?.settings;
    if (!settings) return;
    this.customizeDraft.lootStructure = settings.lootStructure;
    this.customizeDraft.enableHnmSection = settings.enableHnmSection;
    this.customizeDraft.enableMissions = settings.enableMissions;
    this.customizeDraft.enableAuctions = settings.enableAuctions;
    this.customizeDraft.enableToDs = settings.enableToDs;
    this.customizeDraft.enableEndgame = settings.enableEndgame;
    this.customizeDraft.enableEvents = settings.enableEvents;
    this.customizeDraft.enableDkp = settings.enableDkp;
    this.customizeDraft.enableItems = settings.enableItems;
    this.customizeDraft.enableRevenue = settings.enableRevenue;
    this.customizeDraft.dkpRoundingIncrement = settings.dkpRoundingIncrement || 'Quarter';
    this.customizeDirty = false;
  }

  protected onCustomizeLinkshellChange(linkshellId: number): void {
    this.customizeLinkshellId = linkshellId;
    this.syncCustomizeDraft();
  }

  protected onCustomizeFieldChange(): void {
    this.customizeDirty = true;
  }

  protected async saveCustomizeDraft(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id) return;
    const link = this.dashboardLinkshells().find(l => l.id === id);
    if (!link) return;

    try {
      await this.activity.updateLinkshell(id, {
        name: link.name,
        details: link.details ?? null,
        lootStructure: this.customizeDraft.lootStructure,
        enableHnmSection: this.customizeDraft.enableHnmSection,
        enableMissions: this.customizeDraft.enableMissions,
        enableAuctions: this.customizeDraft.enableAuctions,
        enableToDs: this.customizeDraft.enableToDs,
        enableEndgame: this.customizeDraft.enableEndgame,
        enableEvents: this.customizeDraft.enableEvents,
        enableDkp: this.customizeDraft.enableDkp,
        enableItems: this.customizeDraft.enableItems,
        enableRevenue: this.customizeDraft.enableRevenue,
        dkpRoundingIncrement: this.customizeDraft.dkpRoundingIncrement
      });
      this.customizeDirty = false;
      this.syncCustomizeDraft();
    } catch {
      // surfaced by service
    }
  }

  // ----- Game Addon (att) pairing -----
  protected readonly addonTokens = signal<ActivityAddonToken[]>([]);
  protected addonModalOpen = false;
  protected addonModalLabel = '';
  protected addonGeneratedCode: string | null = null;
  protected addonCountdownLabel = '';
  protected addonModalError: string | null = null;
  protected addonModalLoadedFor: number | null = null;
  private addonCountdownTimer: ReturnType<typeof setInterval> | null = null;

  protected canManageAddonTokens(): boolean {
    return this.canCustomizeSelectedLinkshell();
  }

  protected async loadAddonTokensForCurrent(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id || !this.canManageAddonTokens()) {
      this.addonTokens.set([]);
      return;
    }
    if (this.addonModalLoadedFor === id) return;
    const result = await this.activity.loadAddonTokens(id);
    if (result) {
      this.addonTokens.set(result.tokens);
      this.addonModalLoadedFor = id;
    }
  }

  protected openAddonPairingModal(): void {
    this.addonModalLabel = '';
    this.addonGeneratedCode = null;
    this.addonCountdownLabel = '';
    this.addonModalError = null;
    this.addonModalOpen = true;
  }

  protected closeAddonPairingModal(): void {
    this.addonModalOpen = false;
    if (this.addonCountdownTimer) {
      clearInterval(this.addonCountdownTimer);
      this.addonCountdownTimer = null;
    }
    this.addonModalLoadedFor = null;
    this.loadAddonTokensForCurrent();
  }

  protected async submitAddonPairingCode(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id) return;
    this.addonModalError = null;
    const result = await this.activity.createAddonPairingCode(
      id, this.addonModalLabel.trim() || null);
    if (!result) {
      this.addonModalError = this.activity.actionError() ?? 'Could not generate pairing code.';
      return;
    }
    this.addonGeneratedCode = result.code;
    this.startAddonCountdown((result.expiresInMinutes || 10) * 60);
  }

  private startAddonCountdown(totalSeconds: number): void {
    if (this.addonCountdownTimer) clearInterval(this.addonCountdownTimer);
    let remaining = totalSeconds;
    const tick = () => {
      if (remaining <= 0) {
        this.addonCountdownLabel = 'expired';
        if (this.addonCountdownTimer) {
          clearInterval(this.addonCountdownTimer);
          this.addonCountdownTimer = null;
        }
        return;
      }
      const m = Math.floor(remaining / 60);
      const s = remaining % 60;
      this.addonCountdownLabel = `${m}:${s.toString().padStart(2, '0')}`;
      remaining--;
    };
    tick();
    this.addonCountdownTimer = setInterval(tick, 1000);
  }

  protected async revokeAddonToken(tokenId: number): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id) return;
    if (!window.confirm('Revoke this addon token? The addon will lose access immediately.')) return;
    const ok = await this.activity.revokeAddonToken(tokenId, id);
    if (ok) {
      this.addonModalLoadedFor = null;
      this.loadAddonTokensForCurrent();
    }
  }

  protected formatAddonTokenDate(value?: string | null): string {
    if (!value) return '—';
    const d = new Date(value);
    return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString();
  }
}
