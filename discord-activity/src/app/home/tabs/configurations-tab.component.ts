import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityAddonToken,
  ActivityDkpRoundingIncrement,
  ActivityLinkshellRole,
  ActivityLinkshellRolePermissionsInput,
  ActivityLootStructure,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import { TOD_BUILT_IN_MONSTER_GROUPS } from '../activity-home.types';

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

    // Re-sync customize draft + reload roles when the active (primary)
    // linkshell changes — both cards now follow the dashboard selection so
    // there's no per-card picker to invalidate.
    effect(() => {
      const id = this.selectedDashboardLinkshellId();
      if (!id) return;
      this.editingRoleId = null;
      this.showNewRoleForm = false;
      this.pendingDeleteRoleId = null;
      this.addonModalLoadedFor = null;
      this.syncCustomizeDraft();
      void this.loadRolesForSelectedLinkshell();
      void this.loadAddonTokensForCurrent();
    });

    this.destroyRef.onDestroy(() => {
      if (this.addonCountdownTimer) {
        clearInterval(this.addonCountdownTimer);
        this.addonCountdownTimer = null;
      }
    });
  }

  protected activeLinkshellName = computed(() => {
    const id = this.selectedDashboardLinkshellId();
    return this.dashboardLinkshells().find(l => l.id === id)?.name ?? null;
  });

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

  // ----- Edit / delete linkshell (leaders only) -----

  protected editingLinkshellId: number | null = null;
  protected linkshellEditDraft: { name: string; details: string } = { name: '', details: '' };
  protected pendingDeleteLinkshellId: number | null = null;

  protected isLinkshellLeader(rank: string | null | undefined): boolean {
    return (rank ?? '').toLowerCase() === 'leader';
  }

  protected beginEditLinkshell(link: { id: number; name: string; details?: string | null }): void {
    this.editingLinkshellId = link.id;
    this.linkshellEditDraft = {
      name: link.name ?? '',
      details: link.details ?? ''
    };
    this.pendingDeleteLinkshellId = null;
  }

  protected cancelEditLinkshell(): void {
    this.editingLinkshellId = null;
    this.linkshellEditDraft = { name: '', details: '' };
  }

  protected async saveEditLinkshell(): Promise<void> {
    if (this.editingLinkshellId === null) return;
    const linkshellId = this.editingLinkshellId;
    const link = this.dashboardLinkshells().find(l => l.id === linkshellId);
    if (!link) return;
    const name = this.linkshellEditDraft.name.trim();
    if (!name) return;

    try {
      // Pass through existing settings so the rename doesn't reset feature flags.
      const settings = link.settings;
      await this.activity.updateLinkshell(linkshellId, {
        name,
        details: this.linkshellEditDraft.details.trim() || null,
        lootStructure: settings?.lootStructure ?? null,
        enableHnmSection: settings?.enableHnmSection ?? null,
        enableMissions: settings?.enableMissions ?? null,
        enableAuctions: settings?.enableAuctions ?? null,
        enableToDs: settings?.enableToDs ?? null,
        enableEndgame: settings?.enableEndgame ?? null,
        enableEvents: settings?.enableEvents ?? null,
        enableDkp: settings?.enableDkp ?? null,
        enableItems: settings?.enableItems ?? null,
        enableRevenue: settings?.enableRevenue ?? null,
        dkpRoundingIncrement: settings?.dkpRoundingIncrement ?? null,
        hiddenTodMonsters: settings?.hiddenTodMonsters ?? null
      });
      this.cancelEditLinkshell();
    } catch {
      // surfaced by service
    }
  }

  protected requestDeleteLinkshell(link: { id: number }): void {
    this.pendingDeleteLinkshellId = link.id;
    if (this.editingLinkshellId === link.id) {
      this.cancelEditLinkshell();
    }
  }

  protected cancelDeleteLinkshell(): void {
    this.pendingDeleteLinkshellId = null;
  }

  protected async confirmDeleteLinkshell(link: { id: number }): Promise<void> {
    try {
      await this.activity.deleteLinkshell(link.id);
      this.pendingDeleteLinkshellId = null;
    } catch {
      // surfaced by service
    }
  }

  // ----- Switch primary linkshell -----

  protected switchTargetLinkshellId = signal<number | null>(null);

  protected effectiveSwitchLinkshellId(): number {
    const explicit = this.switchTargetLinkshellId();
    if (explicit && this.dashboardLinkshells().some(l => l.id === explicit)) {
      return explicit;
    }
    return this.selectedDashboardLinkshellId();
  }

  protected isSwitchTargetCurrent(): boolean {
    return this.effectiveSwitchLinkshellId() === this.selectedDashboardLinkshellId();
  }

  protected onSwitchLinkshellChange(linkshellId: number): void {
    this.switchTargetLinkshellId.set(linkshellId);
  }

  protected async switchPrimaryLinkshell(): Promise<void> {
    const id = this.effectiveSwitchLinkshellId();
    if (!id || id === this.selectedDashboardLinkshellId()) return;
    await this.activity.setPrimaryLinkshell(id);
    this.switchTargetLinkshellId.set(null);
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
  protected editingRoleId: number | null = null;
  protected readonly roleDraft: {
    name: string;
    permissions: Record<string, boolean>;
  } = { name: '', permissions: {} };
  protected showNewRoleForm = false;

  protected permissionsTargetLinkshellId(): number {
    return this.selectedDashboardLinkshellId();
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
    // Lower-cased names of monsters the linkshell wants hidden from the
    // ToD Tracker. Lower-case for comparison stability — re-cased to the
    // canonical built-in label on save.
    hiddenTodMonsters: Set<string>;
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
    dkpRoundingIncrement: 'Quarter',
    hiddenTodMonsters: new Set<string>()
  };

  protected readonly todMonsterGroups = TOD_BUILT_IN_MONSTER_GROUPS;

  protected customizeDirty = false;

  protected customizeTargetLinkshellId(): number {
    return this.selectedDashboardLinkshellId();
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
    // Rebuild the hidden-monsters Set from the persisted list. Lower-cased
    // for compare stability — restored to canonical case on save.
    this.customizeDraft.hiddenTodMonsters = new Set(
      (settings.hiddenTodMonsters ?? []).map(name => name.trim().toLowerCase())
    );
    this.customizeDirty = false;
  }

  // Per-monster toggle state. Bound through ngModel via these helpers so
  // the template can stay declarative without leaking Set internals.
  protected isMonsterHidden(name: string): boolean {
    return this.customizeDraft.hiddenTodMonsters.has(name.trim().toLowerCase());
  }

  protected onMonsterHiddenChange(name: string, hidden: boolean): void {
    const key = name.trim().toLowerCase();
    if (hidden) {
      this.customizeDraft.hiddenTodMonsters.add(key);
    } else {
      this.customizeDraft.hiddenTodMonsters.delete(key);
    }
    this.onCustomizeFieldChange();
  }

  // Re-emits the hidden Set as canonical-case names for the wire DTO. Walks
  // TOD_BUILT_IN_MONSTER_GROUPS so the recasing matches whatever the addon
  // sends as the canonical label.
  private buildHiddenTodMonstersPayload(): string[] {
    const out: string[] = [];
    for (const group of TOD_BUILT_IN_MONSTER_GROUPS) {
      for (const name of group.names) {
        if (this.customizeDraft.hiddenTodMonsters.has(name.trim().toLowerCase())) {
          out.push(name);
        }
      }
    }
    return out;
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
        dkpRoundingIncrement: this.customizeDraft.dkpRoundingIncrement,
        hiddenTodMonsters: this.buildHiddenTodMonstersPayload()
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
