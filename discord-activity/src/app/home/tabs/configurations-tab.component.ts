import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal } from '@angular/core';
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
import { type TabName } from '../activity-home.types';
import type {
  ActivityMonsterTiming,
  ActivityMonsterTimingInput,
  ActivityMonsterTimingsResponse
} from '../../discord/discord-activity.types';
import { ADMIN_BADGE, canManageLinkshellIn } from '../activity-home.helpers';

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
  // ---- Monster setups ----
  //
  // Its own card with its own endpoint, and deliberately NOT part of customizeDraft: the settings
  // form re-sends every field on any save, so a child collection riding along on it could be wiped
  // by an unrelated edit. Plain mutable array + dirty flag, the same shape channelRoutes uses.
  protected monsterTimingRows: ActivityMonsterTiming[] = [];
  protected monsterTimingCategories: string[] = [];
  protected monsterTimingMaxWindows = 25;
  protected monsterTimingsDirty = false;
  private monsterTimingsLoadedFor: number | null = null;
  protected readonly monsterDurationUnits = ['hours', 'mins'] as const;

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
    void this.loadMonsterTimings();

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
      void this.loadMonsterTimings();
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
    return canManageLinkshellIn(this.activity.overview(), linkshellId);
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
    // Palette key for the rendered event-board image (one of EVENT_BOARD_THEMES).
    eventBoardTheme: string;
    // Allow account-less Discord members to sign up / Check In from a board, every event type.
    outsidePartySignupEnabled: boolean;
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
    eventBoardTheme: 'Crystal',
    outsidePartySignupEnabled: false,
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
    hnmAutoSnapshotDelaySeconds: 20
  };

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

  // ---- Monster setups ----

  // Loading is what SEEDS a linkshell's catalog server-side, so this is deliberately only called
  // for someone who can actually edit it — a member opening the tab shouldn't write rows.
  protected async loadMonsterTimings(force = false): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id || !this.canCustomizeSelectedLinkshell()) {
      this.monsterTimingRows = [];
      this.monsterTimingsLoadedFor = null;
      return;
    }
    if (!force && this.monsterTimingsLoadedFor === id) return;

    const data = await this.activity.loadMonsterTimings(id);
    if (!data) return;
    this.applyMonsterTimings(data);
    this.monsterTimingsLoadedFor = id;
  }

  private applyMonsterTimings(data: ActivityMonsterTimingsResponse): void {
    // Copied rather than referenced: the rows are edited in place by ngModel, and the response
    // object is what Discard restores from.
    this.monsterTimingRows = data.rows.map(row => ({ ...row }));
    this.monsterTimingCategories = data.categories;
    this.monsterTimingMaxWindows = data.maxWindows;
    this.monsterTimingsDirty = false;
  }

  // Rows grouped under their heading, in the server's order within each group. An unknown category
  // folds into the last group so a stale row is always visible — and therefore deletable.
  protected monsterTimingGroups(): readonly { label: string; rows: ActivityMonsterTiming[] }[] {
    const labels = this.monsterTimingCategories.length ? this.monsterTimingCategories : ['Other NMs'];
    const fallback = labels[labels.length - 1];
    return labels.map(label => ({
      label,
      rows: this.monsterTimingRows.filter(row => (labels.includes(row.category) ? row.category : fallback) === label)
    }));
  }

  protected markMonsterTimingsDirty(): void {
    this.monsterTimingsDirty = true;
  }

  protected addCustomMonsterRow(category: string): void {
    this.monsterTimingRows.push({
      // id 0 tells the server this row is new; it comes back with a real id after the save.
      id: 0,
      monsterName: '',
      windows: null,
      // Every duration starts BLANK (placeholder 0), not on a borrowed 10 mins / 22 hours. Those
      // were the kings' numbers on a row for a monster nobody has named yet, and a prefilled field
      // reads as an answer — so a plain NM with no interval only got one if someone noticed to
      // clear it. Blank cadence saves as no interval; blank cooldown falls back to the built-in
      // band for whatever name is typed.
      cadenceValue: null,
      cadenceUnit: 'mins',
      cooldownValue: null,
      cooldownUnit: 'hours',
      category,
      isCustom: true,
      defaultWindows: null,
      defaultCadenceMinutes: null,
      defaultCooldownMinutes: 22 * 60,
      // On by default, matching the column's server-side default: someone adding a monster is
      // adding one they camp.
      claimShieldEnabled: true
    });
    this.markMonsterTimingsDirty();
  }

  protected removeMonsterRow(row: ActivityMonsterTiming): void {
    if (!row.isCustom) return;
    this.monsterTimingRows = this.monsterTimingRows.filter(candidate => candidate !== row);
    this.markMonsterTimingsDirty();
  }

  // Back to the built-in numbers for this monster. The escape hatch for a linkshell that has
  // edited a window grid into something the camp doesn't actually run.
  protected resetMonsterRow(row: ActivityMonsterTiming): void {
    row.windows = row.defaultWindows;
    const cadence = row.defaultCadenceMinutes;
    row.cadenceValue = cadence === null ? null : this.durationValue(cadence);
    row.cadenceUnit = cadence === null ? null : this.durationUnit(cadence);
    row.cooldownValue = this.durationValue(row.defaultCooldownMinutes);
    row.cooldownUnit = this.durationUnit(row.defaultCooldownMinutes);
    this.markMonsterTimingsDirty();
  }

  protected canResetMonsterRow(row: ActivityMonsterTiming): boolean {
    if (row.isCustom) return false;
    const cadence = row.defaultCadenceMinutes;
    return row.windows !== row.defaultWindows
      || row.cadenceValue !== (cadence === null ? null : this.durationValue(cadence))
      || row.cooldownValue !== this.durationValue(row.defaultCooldownMinutes)
      || row.cooldownUnit !== this.durationUnit(row.defaultCooldownMinutes);
  }

  // Mirrors the server's TodDurationFormat.Split: whole hours read as hours, everything else as
  // minutes, so a value never echoes back in a different unit than it was saved in.
  private durationValue(minutes: number): number {
    return minutes > 0 && minutes % 60 === 0 ? minutes / 60 : minutes;
  }

  private durationUnit(minutes: number): string {
    return minutes > 0 && minutes % 60 === 0 ? 'hours' : 'mins';
  }

  protected monsterWindowsPlaceholder(row: ActivityMonsterTiming): string {
    return row.defaultWindows === null ? 'none' : String(row.defaultWindows);
  }

  protected async saveMonsterTimings(): Promise<void> {
    const id = this.customizeTargetLinkshellId();
    if (!id) return;

    const rows: ActivityMonsterTimingInput[] = this.monsterTimingRows
      .filter(row => row.monsterName.trim().length > 0)
      .map(row => ({
        id: row.id > 0 ? row.id : null,
        monsterName: row.monsterName.trim(),
        windows: row.windows === null || Number(row.windows) <= 0 ? null : Math.floor(Number(row.windows)),
        cadenceValue: row.cadenceValue === null || Number(row.cadenceValue) <= 0 ? null : Number(row.cadenceValue),
        cadenceUnit: row.cadenceUnit,
        cooldownValue: Number(row.cooldownValue) > 0 ? Number(row.cooldownValue) : null,
        cooldownUnit: row.cooldownUnit,
        category: row.category,
        // Sent explicitly. The server treats a missing value as "leave it alone", which is what
        // keeps an older client from switching every monster off on a full-replace save.
        claimShieldEnabled: row.claimShieldEnabled !== false
      }));

    const saved = await this.activity.saveMonsterTimings(id, rows);
    if (saved) {
      this.applyMonsterTimings(saved);
      this.monsterTimingsLoadedFor = id;
    }
  }

  protected discardMonsterTimings(): void {
    void this.loadMonsterTimings(true);
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
    this.customizeDraft.eventBoardTheme = settings.eventBoardTheme || 'Crystal';
    this.customizeDraft.outsidePartySignupEnabled = settings.outsidePartySignupEnabled ?? false;
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
    this.customizeDirty = false;
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
        eventBoardTheme: this.customizeDraft.eventBoardTheme,
        outsidePartySignupEnabled: this.customizeDraft.outsidePartySignupEnabled,
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
  // Server-wide Claim Shield kill-switch, owned by a super admin on the web Settings page. Read
  // off the polled overview, so flipping it there reaches an open Activity within a poll.
  protected claimShieldGloballyDisabled(): boolean {
    return this.activity.overview()?.claimShieldGloballyDisabled === true;
  }

  protected addonGloballyDisabled(): boolean {
    return this.activity.overview()?.addonGloballyDisabled === true;
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
