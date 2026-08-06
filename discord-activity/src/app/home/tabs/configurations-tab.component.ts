import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityAddonToken,
  ActivityDkpRoundingIncrement,
  ActivityGuildOption,
  ActivityLinkshellRole,
  ActivityLinkshellRolePermissionsInput,
  ActivityLootStructure,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import { defaultTodMonsterTiming, POP_ONLY_TOD_MONSTERS, TOD_BUILT_IN_MONSTER_GROUPS, type TabName } from '../activity-home.types';
import type { ActivityTodMonsterTiming, HnmWindowSetup } from '../../discord/discord-activity.types';

// (PoolDraft moved to tabs/dkp-grouping-tab.component.ts with the editor it belongs to.)

// One editable Discord channel route row. id is null for an unsaved route.
interface RouteDraft {
  id: number | null;
  name: string;
  channelId: string;
  postEvents: boolean;
  postLoot: boolean;
  postAuctions: boolean;
  postAttendance: boolean;
  postTodBoard: boolean;
  postDkpSheet: boolean;
  eventTypeFilter: string[];
  // Per-monster narrowing for an HNM route (only used when eventTypeFilter has HNM).
  hnmMonsterFilter: string[];
  // false once persisted + unchanged (drives the green "saved" state); true for a
  // new row or any edited field until the next successful save.
  dirty: boolean;
  // A saved route collapses to just its channel name; expanded reveals the editor.
  // New/unsaved rows always render expanded.
  expanded: boolean;
}

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
  private activeConfigurationLinkshellId: number | null = null;
  protected todTimingsOpen = false;
  private readonly newTodMonsterDialog = viewChild<ElementRef<HTMLDialogElement>>('newTodMonsterDialog');
  protected readonly newTodMonsterCategories = TOD_BUILT_IN_MONSTER_GROUPS.map(group => group.label);
  protected newTodMonsterCategory = TOD_BUILT_IN_MONSTER_GROUPS[0].label;
  protected newTodMonsterName = '';
  protected newTodMonsterCooldownHours = 22;
  protected newTodMonsterCooldownMinutes = 0;
  protected newTodMonsterIntervalHours = 0;
  protected newTodMonsterIntervalMinutes = 10;
  protected newTodMonsterError = '';

  // Which section this instance renders. The parent mounts ONE component for the
  // Configurations / Permissions / Game Addon tabs and swaps this input, so state is
  // shared and switching between the three tabs doesn't reload anything.
  readonly view = input<TabName>('configurations');
  protected readonly viewTitle = computed(() =>
    this.view() === 'permissions' ? 'Permissions'
      : this.view() === 'addon' ? 'Game Addon'
        : 'Configurations');

  public constructor() {
    // Match the parent's behavior on tab activation: prefetch roles and seed
    // the customize draft. The parent triggered these when switching to this
    // tab; with a child component we run them on construction (the @if in the
    // parent only mounts us when active).
    void this.loadRolesForSelectedLinkshell();
    this.syncCustomizeDraft();
    void this.loadDiscordChannels();
    void this.loadEligibleGuilds();

    // Re-sync customize draft + reload roles when the active (primary)
    // linkshell changes — both cards now follow the dashboard selection so
    // there's no per-card picker to invalidate.
    effect(() => {
      const id = this.selectedDashboardLinkshellId();
      if (!id || id === this.activeConfigurationLinkshellId) return;
      this.activeConfigurationLinkshellId = id;
      this.editingRoleId = null;
      this.showNewRoleForm = false;
      this.pendingDeleteRoleId = null;
      this.addonModalLoadedFor = null;
      this.syncCustomizeDraft();
      void this.loadRolesForSelectedLinkshell();
      void this.loadAddonTokensForCurrent();
      void this.loadDiscordChannels();
      void this.loadEligibleGuilds();
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
    if (!(await this.confirmChange())) return;

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
        enableActivityTracking: settings?.enableActivityTracking ?? null,
        inactiveAfterAbsences: settings?.inactiveAfterAbsences ?? null,
        activeAfterAttendances: settings?.activeAfterAttendances ?? null,
        hiddenTodMonsters: settings?.hiddenTodMonsters ?? null,
        linkshellType: settings?.linkshellType ?? null
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
    // Editing the Charts boards — pop items, holders and farming credit on EVERY board, not just
    // Sky. Reads stay open to every member. Adding a key here is only half the job — saveRoleDraft
    // below has to send it too, or the checkbox renders and its value is dropped on every save.
    { key: 'canManageCharts', label: 'Manage Charts (pop items & credits)' },
    // The gate for recording anything in the treasury — on both the website and here. It used to be
    // the coarse Leader/Officer rank on the web, so an officer without this could record gil there
    // that the Activity refused.
    { key: 'canManageTreasury', label: 'Record treasury entries' },
    { key: 'canManageRules', label: 'Manage rules' },
    { key: 'canManageAnnouncements', label: 'Manage announcements' },
    { key: 'canManageTods', label: 'Manage ToDs' },
    { key: 'canAuditDkp', label: 'Audit DKP' },
    { key: 'canManageAuctions', label: 'Manage auctions' },
    { key: 'canLockAuctions', label: 'Lock auctions (bid freeze)' },
    { key: 'canCustomizeLinkshell', label: 'Customize linkshell settings' },
    { key: 'canManageParties', label: 'Manage party setups' },
    { key: 'canManageInvites', label: 'Manage invites' },
    { key: 'canBid', label: 'Place bids on auctions' }
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
      // Bidding is on by default (the norm); every other permission starts off.
      this.roleDraft.permissions[perm.key] = perm.key === 'canBid';
    }
  }

  protected async saveRoleDraft(): Promise<void> {
    const linkshellId = this.permissionsTargetLinkshellId();
    if (!linkshellId) return;
    if (!(await this.confirmChange())) return;

    const input: ActivityLinkshellRolePermissionsInput = {
      name: this.roleDraft.name?.trim() || null,
      canManageRoles: !!this.roleDraft.permissions['canManageRoles'],
      canManageMembers: !!this.roleDraft.permissions['canManageMembers'],
      canManageEvents: !!this.roleDraft.permissions['canManageEvents'],
      canModerateLiveEvent: !!this.roleDraft.permissions['canModerateLiveEvent'],
      canAddLoot: !!this.roleDraft.permissions['canAddLoot'],
      canManageInventory: !!this.roleDraft.permissions['canManageInventory'],
      canManageCharts: !!this.roleDraft.permissions['canManageCharts'],
      canManageTreasury: !!this.roleDraft.permissions['canManageTreasury'],
      canManageRules: !!this.roleDraft.permissions['canManageRules'],
      canManageAnnouncements: !!this.roleDraft.permissions['canManageAnnouncements'],
      canManageTods: !!this.roleDraft.permissions['canManageTods'],
      canAuditDkp: !!this.roleDraft.permissions['canAuditDkp'],
      canManageAuctions: !!this.roleDraft.permissions['canManageAuctions'],
      canLockAuctions: !!this.roleDraft.permissions['canLockAuctions'],
      canCustomizeLinkshell: !!this.roleDraft.permissions['canCustomizeLinkshell'],
      canManageParties: !!this.roleDraft.permissions['canManageParties'],
      canManageInvites: !!this.roleDraft.permissions['canManageInvites'],
      canBid: !!this.roleDraft.permissions['canBid']
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

  // ----- Per-save confirm gate -----
  // Discord Activities run in an iframe without `allow-modals`, so native
  // confirm()/alert() are suppressed — this in-DOM modal is the only way to gate
  // saves. It's a promise-based gate every save action awaits, naming the linkshell
  // being changed. There's no upfront "which linkshell" prompt — switch which one
  // you're editing via the Switch Linkshells card or the top-bar switcher.
  protected readonly confirmModalOpen = signal(false);
  private confirmResolver: ((ok: boolean) => void) | null = null;

  // Returns a promise that resolves true (Yes) / false (No). Every save action
  // awaits this so the user re-confirms which linkshell they're changing.
  protected confirmChange(): Promise<boolean> {
    return new Promise<boolean>(resolve => {
      this.confirmResolver = resolve;
      this.confirmModalOpen.set(true);
    });
  }

  protected resolveConfirm(ok: boolean): void {
    this.confirmModalOpen.set(false);
    const resolve = this.confirmResolver;
    this.confirmResolver = null;
    if (resolve) resolve(ok);
  }

  // --- Dashboard banner (uploaded as base64 JSON — the iframe can't multipart) ---
  protected readonly bannerBusy = signal(false);
  protected readonly bannerFileName = signal('');
  protected readonly bannerError = signal<string | null>(null);
  // The picked image as a data URL (or null). Sent verbatim; the server strips
  // the data: prefix and validates the bytes.
  private bannerData: string | null = null;

  // The selected linkshell's current banner URL (already cache-busted), or null.
  protected activeBannerUrl(): string | null {
    const id = this.selectedDashboardLinkshellId();
    return (this.activity.overview()?.linkshells ?? []).find(link => link.id === id)?.bannerUrl ?? null;
  }

  protected onBannerFileChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files && input.files.length ? input.files[0] : null;
    this.bannerError.set(null);
    if (!file) {
      this.bannerData = null;
      this.bannerFileName.set('');
      return;
    }
    if (file.size > 5_000_000) {
      this.bannerError.set('Image must be 5 MB or smaller.');
      input.value = '';
      this.bannerData = null;
      this.bannerFileName.set('');
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      this.bannerData = typeof reader.result === 'string' ? reader.result : null;
      this.bannerFileName.set(file.name);
    };
    reader.readAsDataURL(file);
  }

  protected async uploadBanner(): Promise<void> {
    const id = this.selectedDashboardLinkshellId();
    if (!id || !this.bannerData) { return; }
    if (!(await this.confirmChange())) { return; }
    this.bannerBusy.set(true);
    try {
      const ok = await this.activity.uploadLinkshellBanner(id, this.bannerData);
      if (ok) {
        this.bannerData = null;
        this.bannerFileName.set('');
      }
    } finally {
      this.bannerBusy.set(false);
    }
  }

  protected async removeBanner(): Promise<void> {
    const id = this.selectedDashboardLinkshellId();
    if (!id) { return; }
    if (!(await this.confirmChange())) { return; }
    this.bannerBusy.set(true);
    try {
      await this.activity.removeLinkshellBanner(id);
    } finally {
      this.bannerBusy.set(false);
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
    // Member activity tracking (opt-in Active/Inactive badge from attendance).
    enableActivityTracking: boolean;
    inactiveAfterAbsences: number;
    activeAfterAttendances: number;
    // SkySeaDynamis | HnmOnly | Both — which content this linkshell runs.
    linkshellType: string;
    // Palette key for the rendered event-board image (one of EVENT_BOARD_THEMES).
    eventBoardTheme: string;
    // Allow account-less Discord members to sign up from the party board (non-HNM).
    outsidePartySignupEnabled: boolean;
    // "Fill earlier alliances first" signup nudge.
    fillAlliancesInOrder: boolean;
    // Gate HNM: event type in the create dropdown + account-less HNM-board signups.
    hnmOutsideSignupEnabled: boolean;
    // Experimental: post event boards as Components V2 (wide media-gallery card).
    useComponentsV2Boards: boolean;
    // Manual Check In HNM attendance: mode ('Standard' | 'Wd') + scoring. Only the mode + rates
    // matter when Wd; window counts/cadence are built in per monster (HnmConfig), not configurable.
    hnmAttendanceMode: string;
    wdDkpPerWindow: number;
    wdClaimBonus: number;
    wdKillBonus: number;
    // Manual Check In open / close bonuses — paid once on top of the per-window rate, gated on the
    // member.s own check-in range (open = in from window 1, close = still in at the last window).
    wdOpenBonus: number;
    wdCloseBonus: number;
    // Standard-mode HNM bonuses — only meaningful when hnmAttendanceMode === 'Standard'.
    hnmStandardOpenBonus: number;
    hnmStandardCloseBonus: number;
    hnmStandardClaimBonus: number;
    hnmStandardKillBonus: number;
    // What a REGULAR (in-between) window pays each attendee — the base the open / close ride on.
    hnmStandardWindowBonus: number;
    // Automatic per-window snapshots — applies to BOTH attendance modes.
    hnmAutoSnapshotEnabled: boolean;
    hnmAutoSnapshotDelaySeconds: number;
    todMonsterTimings: Record<string, ActivityTodMonsterTiming>;
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
    enableActivityTracking: false,
    inactiveAfterAbsences: 3,
    activeAfterAttendances: 2,
    linkshellType: 'Both',
    eventBoardTheme: 'Crystal',
    outsidePartySignupEnabled: false,
    fillAlliancesInOrder: true,
    hnmOutsideSignupEnabled: false,
    useComponentsV2Boards: false,
    hnmAttendanceMode: 'Standard',
    wdDkpPerWindow: 0.25,
    wdClaimBonus: 0,
    wdKillBonus: 0,
    wdOpenBonus: 0,
    wdCloseBonus: 0,
    hnmStandardOpenBonus: 0,
    hnmStandardCloseBonus: 0,
    hnmStandardClaimBonus: 0,
    hnmStandardKillBonus: 0,
    hnmStandardWindowBonus: 0,
    hnmAutoSnapshotEnabled: false,
    hnmAutoSnapshotDelaySeconds: 20,
    todMonsterTimings: {}
  };

  protected todMonsterGroups(): ReadonlyArray<{ label: string; names: string[] }> {
    const builtInNames = new Set(TOD_BUILT_IN_MONSTER_GROUPS.flatMap(group => group.names).map(name => name.toLocaleLowerCase()));
    // A custom monster carries the category it was added under, which may name a group that
    // no longer exists (the retired "Sea NMs" / "HENMs" headers). Fall those back to the
    // catch-all so a stored monster is always visible — and therefore deletable — somewhere.
    const knownLabels = new Set(TOD_BUILT_IN_MONSTER_GROUPS.map(group => group.label));
    const groupLabelFor = (category?: string | null): string =>
      category && knownLabels.has(category) ? category : 'Other NMs';
    return TOD_BUILT_IN_MONSTER_GROUPS.map(group => ({
      label: group.label,
      names: [
        ...group.names,
        ...Object.values(this.customizeDraft.todMonsterTimings)
          .filter(timing => !builtInNames.has(timing.monsterName.toLocaleLowerCase())
            && groupLabelFor(timing.category) === group.label)
          .map(timing => timing.monsterName)
          .sort((left, right) => left.localeCompare(right))
      ]
    }));
  }

  protected isCustomTodMonster(monsterName: string): boolean {
    return !TOD_BUILT_IN_MONSTER_GROUPS
      .flatMap(group => group.names)
      .some(builtInName => builtInName.localeCompare(monsterName, undefined, { sensitivity: 'accent' }) === 0);
  }

  protected deleteCustomTodMonster(monsterName: string): void {
    if (!this.isCustomTodMonster(monsterName)) return;
    delete this.customizeDraft.todMonsterTimings[monsterName];
    this.onCustomizeFieldChange();
  }

  // Event-board image palettes (mirrors the server-side EventBoardThemes). The
  // swatch colours are shown in the picker; the key is what gets persisted.
  protected readonly eventBoardThemes: ReadonlyArray<{ key: string; label: string; bg: string; accent: string }> = [
    { key: 'Crystal', label: 'Crystal', bg: '#0b0e1a', accent: '#d8b86a' },
    { key: 'Abyss', label: 'Abyss', bg: '#041c24', accent: '#3fd9e6' },
    { key: 'Ember', label: 'Ember', bg: '#170a05', accent: '#f0883e' },
    { key: 'Verdant', label: 'Verdant', bg: '#07150f', accent: '#cda86a' },
    { key: 'Royal', label: 'Royal', bg: '#150a24', accent: '#c9a8f0' },
    { key: 'Tome', label: 'Tome', bg: '#e8d6ad', accent: '#8a3522' }
  ];


  protected customizeDirty = false;

  protected customizeTargetLinkshellId(): number {
    return this.selectedDashboardLinkshellId();
  }

  protected canCustomizeSelectedLinkshell(): boolean {
    const id = this.customizeTargetLinkshellId();
    const link = this.dashboardLinkshells().find(l => l.id === id);
    return !!link?.permissions?.canCustomizeLinkshell;
  }

  // ----- Discord server (associate) + separate access lock -----
  // Draft for the "server name" the user types when setting via the current
  // server (fallback when no eligible-guild dropdown is available).
  protected guildLockNameDraft = '';

  // Servers the caller can pick from (bot's servers they're also in) + the one
  // picked in the dropdown.
  protected readonly eligibleGuilds = signal<ActivityGuildOption[]>([]);
  protected guildLockSelection = '';

  protected async loadEligibleGuilds(): Promise<void> {
    if (!this.canCustomizeSelectedLinkshell()) {
      this.eligibleGuilds.set([]);
      return;
    }
    const guilds = await this.activity.loadEligibleGuilds();
    this.eligibleGuilds.set(guilds);
    // Default the dropdown to the already-set server (if it's in the list),
    // else the first option.
    const current = this.setGuildId();
    this.guildLockSelection =
      (current && guilds.some(g => g.id === current) ? current : guilds[0]?.id) ?? '';
  }

  // The guild id the Activity is currently launched in (null on the website).
  protected currentGuildId(): string | null {
    return this.activity.currentGuildId();
  }

  // The Discord server this linkshell is associated with (set), or null.
  protected setGuildId(): string | null {
    const id = this.customizeTargetLinkshellId();
    return this.dashboardLinkshells().find(l => l.id === id)?.settings?.discordGuildId ?? null;
  }

  protected setGuildName(): string | null {
    const id = this.customizeTargetLinkshellId();
    return this.dashboardLinkshells().find(l => l.id === id)?.settings?.discordGuildName ?? null;
  }

  // Whether the optional access lock is on for the selected linkshell.
  protected isGuildLocked(): boolean {
    const id = this.customizeTargetLinkshellId();
    return this.dashboardLinkshells().find(l => l.id === id)?.settings?.lockToDiscordGuild === true;
  }

  protected isSetToCurrentGuild(): boolean {
    const set = this.setGuildId();
    return !!set && set === this.currentGuildId();
  }

  // Changing an *unlocked* server is allowed from anywhere; changing a *locked*
  // one requires being in that server (or on the website, no guild context).
  protected canChangeGuildHere(): boolean {
    if (!this.isGuildLocked()) return true;
    return this.isSetToCurrentGuild() || this.currentGuildId() === null;
  }

  // Toggling the lock requires a server set and being in it (server-side rejects
  // locking to a server you're not in). The website (no guild context) may lock.
  protected canToggleGuildLockHere(): boolean {
    if (!this.setGuildId()) return false;
    return this.isSetToCurrentGuild() || this.currentGuildId() === null;
  }

  // Set the server chosen in the dropdown (the common path).
  protected async setSelectedGuild(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    const guildId = this.guildLockSelection;
    if (!id || !guildId) return;
    const name = this.eligibleGuilds().find(g => g.id === guildId)?.name ?? null;
    await this.activity.setLinkshellGuild(id, guildId, name);
  }

  // Fallback when the dropdown can't be built (bot can't list servers): set to
  // the server the Activity is launched in, with a typed display name.
  protected async setToCurrentGuild(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id || !this.currentGuildId()) return;
    const ok = await this.activity.setLinkshellGuild(id, null, this.guildLockNameDraft.trim() || null);
    if (ok) this.guildLockNameDraft = '';
  }

  protected async clearGuild(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id) return;
    await this.activity.clearLinkshellGuild(id);
  }

  protected async toggleGuildLock(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id) return;
    await this.activity.setLinkshellGuildLock(id, !this.isGuildLocked());
  }

  // ----- Discord channel routes (bot posts each kind of content to a channel) -----
  protected readonly discordChannelsAvailable = signal<{ id: string; name: string }[]>([]);
  protected readonly discordGuildConfigured = signal(false);
  protected readonly channelPostTypes = signal<{ key: string; label: string }[]>([]);
  protected readonly channelEventTypes = signal<string[]>([]);
  protected readonly channelMonsterOptions = signal<string[]>([]);
  // Plain mutable drafts so the template can two-way bind checkboxes; mutated
  // in place + structural add/remove happen in zone-run click handlers.
  protected channelRoutes: RouteDraft[] = [];

  // Post-event discussion mirror channel ('' = none / in-app only). Seeded from
  // the linkshell's settings when channels load; saved via its own Save button.
  protected discussionChannelDraft = '';
  protected discussionChannelId(): string {
    const id = this.customizeTargetLinkshellId();
    const link = this.dashboardLinkshells().find(l => l.id === id);
    return link?.settings?.discussionChannelId ?? '';
  }
  protected async saveDiscussionChannel(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id) { return; }
    const value = this.discussionChannelDraft.trim();
    await this.activity.setDiscussionChannel(id, value.length > 0 ? value : null);
  }

  // refresh=true force-pulls the live Discord channel list (bypassing the bot's
  // cache) so a just-created channel appears now, and ONLY updates the pick-list —
  // in-progress (unsaved) route drafts are kept.
  protected async loadDiscordChannels(refresh = false): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id || !this.canCustomizeSelectedLinkshell()) {
      if (refresh) { return; }
      this.channelRoutes = [];
      this.discordChannelsAvailable.set([]);
      this.discordGuildConfigured.set(false);
      this.channelPostTypes.set([]);
      this.channelEventTypes.set([]);
      this.channelMonsterOptions.set([]);
      return;
    }
    if (!refresh) { this.discussionChannelDraft = this.discussionChannelId(); }
    const data = await this.activity.loadDiscordChannels(id, refresh);
    if (!data) { return; }
    this.discordChannelsAvailable.set(data.availableChannels);
    if (refresh) { return; }
    this.discordGuildConfigured.set(data.guildConfigured);
    this.channelPostTypes.set(data.postTypes);
    this.channelEventTypes.set(data.eventTypes);
    this.channelMonsterOptions.set(data.monsterOptions ?? []);
    this.channelRoutes = data.routes.map(route => ({
      id: route.id,
      name: route.name ?? '',
      channelId: route.channelId,
      postEvents: route.postEvents,
      postLoot: route.postLoot,
      postAuctions: route.postAuctions,
      postAttendance: route.postAttendance,
      postTodBoard: route.postTodBoard,
      postDkpSheet: route.postDkpSheet,
      eventTypeFilter: [...route.eventTypeFilter],
      hnmMonsterFilter: [...(route.hnmMonsterFilter ?? [])],
      dirty: false,
      expanded: false
    }));
  }

  protected addRoute(): void {
    this.channelRoutes = [
      ...this.channelRoutes,
      {
        id: null, name: '', channelId: '',
        postEvents: false, postLoot: false, postAuctions: false,
        postAttendance: false, postTodBoard: false, postDkpSheet: false, eventTypeFilter: [],
        hnmMonsterFilter: [],
        dirty: true,
        expanded: true
      }
    ];
  }

  protected removeRoute(route: RouteDraft): void {
    this.channelRoutes = this.channelRoutes.filter(r => r !== route);
  }

  // Flags a route as having unsaved edits (clears its green "saved" state).
  protected markRouteDirty(route: RouteDraft): void {
    route.dirty = true;
  }

  // A route is "saved" (green) when it's persisted and has no pending edits.
  protected isRouteSaved(route: RouteDraft): boolean {
    return route.id !== null && !route.dirty;
  }

  // A saved route renders collapsed (just its channel name, read-only) until expanded.
  protected isCollapsed(route: RouteDraft): boolean {
    return this.isRouteSaved(route) && !route.expanded;
  }

  // The "#channel" label shown on a collapsed route, resolved from its channel id.
  protected routeChannelLabel(route: RouteDraft): string {
    const ch = this.discordChannelsAvailable().find(c => String(c.id) === String(route.channelId));
    return ch ? `#${ch.name}` : (route.channelId || 'No channel selected');
  }

  protected isEventTypeOn(route: RouteDraft, type: string): boolean {
    return route.eventTypeFilter.includes(type);
  }

  protected toggleEventType(route: RouteDraft, type: string, on: boolean): void {
    route.eventTypeFilter = on
      ? [...route.eventTypeFilter, type]
      : route.eventTypeFilter.filter(t => t !== type);
    route.dirty = true;
  }

  // The per-monster HNM narrowing UI only shows when a route catches HNM events.
  protected routeCatchesHnm(route: RouteDraft): boolean {
    return route.postEvents && route.eventTypeFilter.includes('HNM');
  }

  protected isMonsterOn(route: RouteDraft, monster: string): boolean {
    return route.hnmMonsterFilter.includes(monster);
  }

  protected toggleMonster(route: RouteDraft, monster: string, on: boolean): void {
    route.hnmMonsterFilter = on
      ? [...route.hnmMonsterFilter, monster]
      : route.hnmMonsterFilter.filter(m => m !== monster);
    route.dirty = true;
  }


  // Mirrors the server's "one route per non-event post type" rule so a bad save
  // is caught before the round-trip.
  protected channelRoutesError(): string | null {
    const count = (pick: (r: RouteDraft) => boolean) =>
      this.channelRoutes.filter(r => r.channelId && pick(r)).length;
    if (count(r => r.postLoot) > 1) return 'Only one route can post Loot.';
    if (count(r => r.postAuctions) > 1) return 'Only one route can post Auctions.';
    if (count(r => r.postAttendance) > 1) return 'Only one route can post Attendance.';
    if (count(r => r.postTodBoard) > 1) return 'Only one route can post the ToD board.';
    if (count(r => r.postDkpSheet) > 1) return 'Only one route can post the DKP sheet.';
    return null;
  }

  protected async saveDiscordChannels(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id) return;
    if (this.channelRoutesError()) return;
    if (!(await this.confirmChange())) return;
    const routes = this.channelRoutes
      .filter(r => r.channelId)
      .map(r => ({
        id: r.id,
        name: r.name ? r.name : null,
        channelId: r.channelId,
        postEvents: r.postEvents,
        postLoot: r.postLoot,
        postAuctions: r.postAuctions,
        postAttendance: r.postAttendance,
        postTodBoard: r.postTodBoard,
        postDkpSheet: r.postDkpSheet,
        eventTypeFilter: r.eventTypeFilter,
        hnmMonsterFilter: r.hnmMonsterFilter
      }));
    const ok = await this.activity.saveDiscordChannels(id, routes);
    if (ok) {
      await this.loadDiscordChannels();
    }
  }

  protected syncCustomizeDraft(): void {
    const id = this.customizeTargetLinkshellId();
    const link = this.dashboardLinkshells().find(l => l.id === id);
    const settings = link?.settings;
    if (!settings) return;
    this.activeConfigurationLinkshellId = id;
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
    this.customizeDraft.enableActivityTracking = settings.enableActivityTracking ?? false;
    this.customizeDraft.inactiveAfterAbsences = settings.inactiveAfterAbsences || 3;
    this.customizeDraft.activeAfterAttendances = settings.activeAfterAttendances || 2;
    this.customizeDraft.linkshellType = settings.linkshellType || 'Both';
    this.customizeDraft.eventBoardTheme = settings.eventBoardTheme || 'Crystal';
    this.customizeDraft.outsidePartySignupEnabled = settings.outsidePartySignupEnabled ?? false;
    this.customizeDraft.fillAlliancesInOrder = settings.fillAlliancesInOrder ?? true;
    this.customizeDraft.hnmOutsideSignupEnabled = settings.hnmOutsideSignupEnabled ?? false;
    this.customizeDraft.useComponentsV2Boards = settings.useComponentsV2Boards ?? false;
    this.customizeDraft.hnmAttendanceMode = settings.hnmAttendanceMode || 'Standard';
    this.customizeDraft.wdDkpPerWindow = settings.wdDkpPerWindow ?? 0.25;
    this.customizeDraft.wdClaimBonus = settings.wdClaimBonus ?? 0;
    this.customizeDraft.wdKillBonus = settings.wdKillBonus ?? 0;
    this.customizeDraft.wdOpenBonus = settings.wdOpenBonus ?? 0;
    this.customizeDraft.wdCloseBonus = settings.wdCloseBonus ?? 0;
    this.customizeDraft.hnmStandardOpenBonus = settings.hnmStandardOpenBonus ?? 0;
    this.customizeDraft.hnmStandardCloseBonus = settings.hnmStandardCloseBonus ?? 0;
    this.customizeDraft.hnmStandardClaimBonus = settings.hnmStandardClaimBonus ?? 0;
    this.customizeDraft.hnmStandardKillBonus = settings.hnmStandardKillBonus ?? 0;
    this.customizeDraft.hnmStandardWindowBonus = settings.hnmStandardWindowBonus ?? 0;
    this.customizeDraft.hnmAutoSnapshotEnabled = settings.hnmAutoSnapshotEnabled ?? false;
    this.customizeDraft.hnmAutoSnapshotDelaySeconds = settings.hnmAutoSnapshotDelaySeconds ?? 20;
    this.customizeDraft.todMonsterTimings = this.buildTodMonsterTimings(settings.todMonsterTimings ?? []);
    this.customizeDirty = false;
  }

  private buildTodMonsterTimings(saved: readonly ActivityTodMonsterTiming[]): Record<string, ActivityTodMonsterTiming> {
    const savedByName = new Map(saved.map(timing => [timing.monsterName.toLocaleLowerCase(), timing]));
    const timings: Record<string, ActivityTodMonsterTiming> = {};
    for (const group of TOD_BUILT_IN_MONSTER_GROUPS) {
      for (const monsterName of group.names) {
        const existing = savedByName.get(monsterName.toLocaleLowerCase());
        const fallback = defaultTodMonsterTiming(monsterName);
        const cooldownMinutes = this.timingLabelToMinutes(fallback.cooldown);
        const intervalMinutes = this.timingLabelToMinutes(fallback.interval);
        timings[monsterName] = existing ?? {
          monsterName,
          cooldownHours: cooldownMinutes / 60,
          intervalHours: Math.floor(intervalMinutes / 60),
          intervalMinutes: intervalMinutes % 60,
          category: group.label
        };
      }
    }
    for (const timing of saved) {
      const monsterName = timing.monsterName.trim();
      if (monsterName && !timings[monsterName]) {
        timings[monsterName] = { ...timing, monsterName, category: timing.category || 'Other NMs' };
      }
    }
    return timings;
  }

  private timingLabelToMinutes(value: string): number {
    const match = /^(\d+)\s+(Min|Hour)$/i.exec(value.trim());
    if (!match) return 0;
    return Number(match[1]) * (match[2].toLowerCase() === 'hour' ? 60 : 1);
  }

  protected todMonsterTiming(monsterName: string): ActivityTodMonsterTiming {
    const existing = this.customizeDraft.todMonsterTimings[monsterName];
    if (existing) return existing;

    const fallback = defaultTodMonsterTiming(monsterName);
    const cooldownMinutes = this.timingLabelToMinutes(fallback.cooldown);
    const intervalMinutes = this.timingLabelToMinutes(fallback.interval);
    return this.customizeDraft.todMonsterTimings[monsterName] = {
      monsterName,
      cooldownHours: cooldownMinutes / 60,
      intervalHours: Math.floor(intervalMinutes / 60),
      intervalMinutes: intervalMinutes % 60,
      category: 'Other NMs'
    };
  }

  protected todCooldownHours(monsterName: string): number {
    return Math.floor(Math.max(0, Number(this.todMonsterTiming(monsterName).cooldownHours) || 0));
  }

  protected todCooldownMinutes(monsterName: string): number {
    const cooldownHours = Math.max(0, Number(this.todMonsterTiming(monsterName).cooldownHours) || 0);
    return Math.round((cooldownHours - Math.floor(cooldownHours)) * 60);
  }

  protected updateTodCooldownHours(monsterName: string, value: number): void {
    this.updateTodCooldown(monsterName, value, this.todCooldownMinutes(monsterName));
  }

  protected updateTodCooldownMinutes(monsterName: string, value: number): void {
    this.updateTodCooldown(monsterName, this.todCooldownHours(monsterName), value);
  }

  private updateTodCooldown(monsterName: string, hours: number, minutes: number): void {
    this.todMonsterTiming(monsterName).cooldownHours = this.cooldownHoursFromParts(hours, minutes);
    this.onCustomizeFieldChange();
  }

  private cooldownHoursFromParts(hours: number, minutes: number): number {
    const normalizedHours = Math.max(0, Math.floor(Number(hours) || 0));
    const normalizedMinutes = Math.min(59, Math.max(0, Math.floor(Number(minutes) || 0)));
    return Math.max(1 / 60, normalizedHours + normalizedMinutes / 60);
  }

  protected openNewTodMonsterDialog(): void {
    this.newTodMonsterError = '';
    this.newTodMonsterName = '';
    this.newTodMonsterCategory = TOD_BUILT_IN_MONSTER_GROUPS[0].label;
    this.newTodMonsterCooldownHours = 22;
    this.newTodMonsterCooldownMinutes = 0;
    this.newTodMonsterIntervalHours = 0;
    this.newTodMonsterIntervalMinutes = 10;
    this.newTodMonsterDialog()?.nativeElement.showModal();
  }

  protected closeNewTodMonsterDialog(): void {
    this.newTodMonsterDialog()?.nativeElement.close();
  }

  protected addNewTodMonster(): void {
    const monsterName = this.newTodMonsterName.trim();
    if (!monsterName) {
      this.newTodMonsterError = 'Enter a monster name.';
      return;
    }
    if (Object.values(this.customizeDraft.todMonsterTimings)
      .some(timing => timing.monsterName.localeCompare(monsterName, undefined, { sensitivity: 'accent' }) === 0)) {
      this.newTodMonsterError = 'That monster already exists.';
      return;
    }
    // Pop-only mobs are kept out of the catalog on purpose, and the server drops any timing
    // stored for one — so say no here rather than accept a row that silently vanishes.
    if (POP_ONLY_TOD_MONSTERS.has(monsterName)) {
      this.newTodMonsterError = `${monsterName} pops from an item instead of a repop timer, so it has no ToD cooldown to set.`;
      return;
    }
    this.customizeDraft.todMonsterTimings[monsterName] = {
      monsterName,
      category: this.newTodMonsterCategory,
      cooldownHours: this.cooldownHoursFromParts(this.newTodMonsterCooldownHours, this.newTodMonsterCooldownMinutes),
      intervalHours: Math.max(0, Math.floor(Number(this.newTodMonsterIntervalHours) || 0)),
      intervalMinutes: Math.min(59, Math.max(0, Math.floor(Number(this.newTodMonsterIntervalMinutes) || 0)))
    };
    this.onCustomizeFieldChange();
    this.closeNewTodMonsterDialog();
  }

  protected todMonsterTimingsPayload(): ActivityTodMonsterTiming[] {
    return Object.values(this.customizeDraft.todMonsterTimings).map(timing => ({
      ...timing,
      cooldownHours: Math.max(1 / 60, Number(timing.cooldownHours) || 1 / 60),
      intervalHours: Math.max(0, Math.floor(Number(timing.intervalHours) || 0)),
      intervalMinutes: Math.min(59, Math.max(0, Math.floor(Number(timing.intervalMinutes) || 0)))
    }));
  }

  protected onCustomizeFieldChange(): void {
    this.customizeDirty = true;
  }

  protected async saveCustomizeDraft(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id) return;
    const link = this.dashboardLinkshells().find(l => l.id === id);
    if (!link) return;
    if (!(await this.confirmChange())) return;

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
        enableActivityTracking: this.customizeDraft.enableActivityTracking,
        inactiveAfterAbsences: this.customizeDraft.inactiveAfterAbsences,
        activeAfterAttendances: this.customizeDraft.activeAfterAttendances,
        hiddenTodMonsters: [],
        todMonsterTimings: this.todMonsterTimingsPayload(),
        linkshellType: this.customizeDraft.linkshellType,
        eventBoardTheme: this.customizeDraft.eventBoardTheme,
        outsidePartySignupEnabled: this.customizeDraft.outsidePartySignupEnabled,
        fillAlliancesInOrder: this.customizeDraft.fillAlliancesInOrder,
        hnmOutsideSignupEnabled: this.customizeDraft.hnmOutsideSignupEnabled,
        useComponentsV2Boards: this.customizeDraft.useComponentsV2Boards,
        hnmAttendanceMode: this.customizeDraft.hnmAttendanceMode,
        wdDkpPerWindow: this.customizeDraft.wdDkpPerWindow,
        wdClaimBonus: this.customizeDraft.wdClaimBonus,
        wdKillBonus: this.customizeDraft.wdKillBonus,
        wdOpenBonus: this.customizeDraft.wdOpenBonus,
        wdCloseBonus: this.customizeDraft.wdCloseBonus,
        hnmStandardOpenBonus: this.customizeDraft.hnmStandardOpenBonus,
        hnmStandardCloseBonus: this.customizeDraft.hnmStandardCloseBonus,
        hnmStandardClaimBonus: this.customizeDraft.hnmStandardClaimBonus,
        hnmStandardKillBonus: this.customizeDraft.hnmStandardKillBonus,
        hnmStandardWindowBonus: this.customizeDraft.hnmStandardWindowBonus,
        hnmAutoSnapshotEnabled: this.customizeDraft.hnmAutoSnapshotEnabled,
        hnmAutoSnapshotDelaySeconds: this.customizeDraft.hnmAutoSnapshotDelaySeconds
        // The Discord server is set via the dedicated "Discord server" card
        // (setLinkshellGuild / clearLinkshellGuild), not the main save.
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
  protected addonGeneratedCode: string | null = null;
  protected addonCountdownLabel = '';
  protected addonModalError: string | null = null;
  protected addonModalLoadedFor: number | null = null;
  private addonCountdownTimer: ReturnType<typeof setInterval> | null = null;

  protected canManageAddonTokens(): boolean {
    return this.canCustomizeSelectedLinkshell();
  }

  // True when a super admin has globally disabled the addon — the whole Game
  // Addon card is hidden (pairing endpoints reject requests anyway).
  protected addonGloballyDisabled(): boolean {
    return this.activity.overview()?.addonGloballyDisabled === true;
  }

  // Built-in per-monster window setups for the HNM Settings card's read-only list. Served
  // from HnmConfig so the numbers can't drift from what the boards actually run.
  protected hnmWindowSetups(): HnmWindowSetup[] {
    return this.activity.overview()?.hnmWindowSetups ?? [];
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
    if (!(await this.confirmChange())) return;
    this.addonModalError = null;
    const result = await this.activity.createAddonPairingCode(id);
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

  // Two-stage inline confirmation for revoking an addon token.
  //
  // We can't use `window.confirm()` here: Discord Activities run in an
  // iframe without `allow-modals`, so any native confirm/alert/prompt
  // dialog is suppressed by the browser and the call returns `false`
  // immediately. Result: the user clicks Revoke and nothing happens.
  //
  // Instead, the first click on Revoke flags the row via
  // `pendingRevokeTokenId`; the template swaps the button out for a
  // "Confirm" + "Cancel" pair, and the second click on Confirm calls
  // the API. Light-weight alternative to a full modal component.
  protected readonly pendingRevokeTokenId = signal<number | null>(null);

  protected requestRevokeAddonToken(tokenId: number): void {
    this.pendingRevokeTokenId.set(tokenId);
  }

  protected cancelRevokeAddonToken(): void {
    this.pendingRevokeTokenId.set(null);
  }

  protected async confirmRevokeAddonToken(tokenId: number): Promise<void> {
    const ok = await this.activity.revokeAddonToken(tokenId);
    this.pendingRevokeTokenId.set(null);
    if (ok) {
      this.addonModalLoadedFor = null;
      this.loadAddonTokensForCurrent();
    }
  }

  protected formatAddonTokenLinkshells(token: ActivityAddonToken): string {
    return token.linkshells?.length ? token.linkshells.join(', ') : '—';
  }

  protected formatAddonTokenDate(value?: string | null): string {
    if (!value) return '—';
    const d = new Date(value);
    return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString();
  }
}
