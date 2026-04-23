import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityCreateTodInput,
  ActivityEventParticipant,
  ActivityLinkshellSettings,
  ActivityLootStructure,
  ActivityStatusLedgerEntry,
  ActivityItem,
  ActivityItemInput,
  ActivityLinkshellRole,
  ActivityLinkshellRolePermissionsInput,
  ActivityQuickJoinInput,
  ActivityLootInput,
  ActivityRevenueEntry,
  ActivityRevenueInput,
  ActivityTodLootInput,
  DiscordActivityService
} from '../discord/discord-activity.service';
import { ActivityQueuePanelComponent } from './activity-queue-panel.component';
import { ActivitySidebarPanelComponent } from './activity-sidebar-panel.component';
import {
  EVENT_JOB_TYPE_OPTIONS,
  EVENT_MAIN_JOB_OPTIONS,
  EVENT_SUB_JOB_OPTIONS
} from './event-job-options';

@Component({
  selector: 'app-activity-home',
  imports: [CommonModule, FormsModule, ActivityQueuePanelComponent, ActivitySidebarPanelComponent],
  templateUrl: './activity-home.component.html',
  styleUrl: './activity-home.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ActivityHomeComponent {
  private static readonly todMonsterOptions = [
    'Fafnir',
    'Nidhogg',
    'Behemoth',
    'King Behemoth',
    'Adamantoise',
    'Aspidochelone',
    'Tiamat',
    'Jormungand',
    'Vrtra',
    'King Arthro',
    'Simurgh',
    'Other'
  ] as const;

  private static readonly todCooldownOptions = ['22 Hour', '72 Hour', 'Other'] as const;
  private static readonly todIntervalOptions = ['10 Min', '1 Hour', 'Not specified'] as const;
  private static readonly longWindowTodMonsters = new Set(['Tiamat', 'Jormungand', 'Vrtra']);

  protected readonly activity = inject(DiscordActivityService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());
  protected readonly activeTab = signal<'dashboard' | 'profile' | 'linkshell' | 'events' | 'tods' | 'auctions' | 'dkp' | 'endgame' | 'hnm' | 'missions' | 'messages' | 'configurations'>('dashboard');

  protected setActiveTab(tab: 'dashboard' | 'profile' | 'linkshell' | 'events' | 'tods' | 'auctions' | 'dkp' | 'endgame' | 'hnm' | 'missions' | 'messages' | 'configurations'): void {
    this.activeTab.set(tab);
    window.scrollTo({ top: 0, behavior: 'smooth' });
    if (tab === 'configurations') {
      void this.loadRolesForSelectedLinkshell();
      this.syncCustomizeDraft();
    }
  }

  protected initials(value: string | null | undefined): string {
    const name = (value ?? '').trim();
    if (!name) return '??';
    const parts = name.split(/\s+/).filter(Boolean);
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[1][0]).toUpperCase();
  }

  protected memberAvatarClass(name?: string | null): string {
    const trimmed = (name ?? '').trim();
    if (!trimmed) return 'a';
    let hash = 0;
    for (let i = 0; i < trimmed.length; i += 1) {
      hash = (hash * 31 + trimmed.charCodeAt(i)) >>> 0;
    }
    return ['a', 'b', 'c', 'd', 'e'][hash % 5];
  }

  protected memberStatusClass(status?: string | null): string {
    const normalized = (status ?? 'Active').toLowerCase();
    if (normalized === 'active') return 'success';
    if (normalized === 'pending') return 'warning';
    return 'default';
  }

  protected appUserRoleLabel(): string {
    const linkshells = this.activity.overview()?.linkshells ?? [];
    if (linkshells.length === 0) return 'Member';
    const primaryId = this.activity.overview()?.appUser?.primaryLinkshellId;
    const primary = linkshells.find(l => l.id === primaryId) ?? linkshells[0];
    const rank = (primary?.rank ?? 'Member').toString();
    return rank.charAt(0).toUpperCase() + rank.slice(1).toLowerCase();
  }

  protected primaryLinkshellName(): string {
    return this.primaryLinkshell()?.name || this.activity.overview()?.appUser?.primaryLinkshellName || 'No linkshell';
  }

  protected primaryMemberCount(): number {
    return this.primaryLinkshell()?.memberCount ?? 0;
  }

  protected openEventsCount(): number {
    return this.liveEvents().length + this.queuedEvents().length;
  }

  protected openTodCount(): number {
    return (this.activity.overview()?.recentTods ?? []).filter(tod => {
      const repop = tod.repopTime ? new Date(tod.repopTime).getTime() : 0;
      return repop > 0 && repop <= Date.now();
    }).length;
  }

  protected liveAuctionCount(): number {
    const auctions = (this.activity.overview() as any)?.auctions ?? [];
    return auctions.filter((a: any) => a?.status === 'Live' || a?.status === 'live').length;
  }
  protected readonly lootDrafts: Record<number, ActivityLootInput> = {};
  protected readonly quickJoinDrafts: Record<number, ActivityQuickJoinInput> = {};
  protected readonly todMonsterOptions = [...ActivityHomeComponent.todMonsterOptions];
  protected readonly todCooldownOptions = [...ActivityHomeComponent.todCooldownOptions];
  protected readonly todIntervalOptions = [...ActivityHomeComponent.todIntervalOptions];
  protected readonly todDraft: ActivityCreateTodInput = {
    linkshellId: 0,
    monsterName: ActivityHomeComponent.todMonsterOptions[0],
    dayNumber: null,
    claim: true,
    timeLocal: '',
    cooldown: '22 Hour',
    interval: '10 Min',
    noLoot: true,
    lootDetails: [{ itemName: '', itemWinner: '', winningDkpSpent: null }]
  };
  protected todTimeLocalValue = '';
  protected todRepopLocalValue = '';
  protected todCustomMonsterName = '';
  protected todClaimChoice: 'Yes' | 'No' | 'NotSpecified' = 'Yes';
  protected todDayNumberNotSpecified = false;
  protected todCustomCooldownHours: number | null = null;
  protected todImagePath: string | null = null;
  protected isUploadingTodImage = false;
  protected editingTodId: number | null = null;
  protected readonly mainJobOptions = [...EVENT_MAIN_JOB_OPTIONS];
  protected readonly subJobOptions = [...EVENT_SUB_JOB_OPTIONS];
  protected readonly jobTypeOptions = [...EVENT_JOB_TYPE_OPTIONS];

  public constructor() {
    const intervalId = window.setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => window.clearInterval(intervalId));
    this.resetTodDraft();
  }

  protected appDisplayName(): string {
    const overviewUser = this.activity.overview()?.appUser;
    const localUser = this.activity.localUser();
    const sessionUser = this.activity.session()?.user;

    return (
      overviewUser?.characterName ||
      localUser?.appUser?.characterName ||
      localUser?.globalName ||
      sessionUser?.global_name ||
      overviewUser?.userName ||
      localUser?.username ||
      sessionUser?.username ||
      'Linkshell member'
    );
  }

  protected primaryLinkshell() {
    return this.activity.overview()?.primaryLinkshell ?? null;
  }

  protected primaryLinkshellSettings(): ActivityLinkshellSettings | null {
    const primaryId = this.activity.overview()?.appUser?.primaryLinkshellId;
    if (primaryId == null) return null;
    const link = this.activity.overview()?.linkshells?.find(l => l.id === primaryId);
    return link?.settings ?? null;
  }

  protected primaryLootStructure(): ActivityLootStructure {
    return this.primaryLinkshellSettings()?.lootStructure ?? 'Dkp';
  }

  protected isFeatureEnabled(key: keyof ActivityLinkshellSettings): boolean {
    const settings = this.primaryLinkshellSettings();
    if (!settings) return true;
    const value = settings[key];
    return value !== false;
  }

  protected isDkpModeEnabled(): boolean {
    return this.primaryLootStructure() !== 'LootCouncil';
  }

  protected linkshellSettingsFor(linkshellId: number): ActivityLinkshellSettings | null {
    const link = this.activity.overview()?.linkshells?.find(l => l.id === linkshellId);
    return link?.settings ?? null;
  }

  protected lootStructureFor(linkshellId: number): ActivityLootStructure {
    return this.linkshellSettingsFor(linkshellId)?.lootStructure ?? 'Dkp';
  }

  protected lootInputPlaceholder(linkshellId: number): string {
    return this.lootStructureFor(linkshellId) === 'Hybrid' ? 'Deduction %' : 'DKP spent';
  }

  protected lootInputMax(linkshellId: number): number | null {
    return this.lootStructureFor(linkshellId) === 'Hybrid' ? 100 : null;
  }

  protected formatLootCost(
    value: number | null | undefined,
    linkshellId: number | undefined | null
  ): string {
    const amount = value ?? 0;
    if (linkshellId != null && this.lootStructureFor(linkshellId) === 'Hybrid') {
      return `${amount}%`;
    }
    return `${amount} DKP`;
  }

  protected isManagerMode(): boolean {
    return (this.activity.overview()?.linkshells ?? []).some(link => this.canManageLinkshell(link.id));
  }

  protected isMemberMode(): boolean {
    return !this.isManagerMode();
  }

  protected canManageLinkshell(linkshellId: number): boolean {
    const membership = (this.activity.overview()?.linkshells ?? []).find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
  }

  protected liveEvents() {
    return (this.activity.overview()?.activeEvents ?? []).filter(event => Boolean(event.commencementStartTime));
  }

  protected attendanceParticipants(event: { participants: ActivityEventParticipant[] }): ActivityEventParticipant[] {
    return event.participants.filter(participant => !participant.isOnBreak && participant.isVerified !== true);
  }

  protected activeRoomParticipants(event: { participants: ActivityEventParticipant[] }): ActivityEventParticipant[] {
    return event.participants.filter(participant => !participant.isOnBreak && participant.isVerified === true);
  }

  protected onBreakParticipants(event: { participants: ActivityEventParticipant[] }): ActivityEventParticipant[] {
    return event.participants.filter(participant => !!participant.isOnBreak);
  }

  protected queuedEvents() {
    return (this.activity.overview()?.activeEvents ?? []).filter(event => !event.commencementStartTime);
  }

  protected dashboardLinkshells() {
    return this.activity.overview()?.linkshells ?? [];
  }

  protected selectedDashboardLinkshellId(): number {
    return (
      this.activity.overview()?.appUser?.primaryLinkshellId ??
      this.primaryLinkshell()?.id ??
      this.dashboardLinkshells()[0]?.id ??
      0
    );
  }

  protected selectedDashboardLinkshell() {
    const selectedId = this.selectedDashboardLinkshellId();
    return this.dashboardLinkshells().find(linkshell => linkshell.id === selectedId) ?? null;
  }

  protected selectedDashboardMembers() {
    const selectedId = this.selectedDashboardLinkshellId();
    if (this.primaryLinkshell()?.id !== selectedId) {
      return [];
    }

    return [...(this.primaryLinkshell()?.members ?? [])].sort((left, right) =>
      left.characterName.localeCompare(right.characterName)
    );
  }

  protected dashboardRosterSearch = '';

  protected showRuleForm = signal(false);
  protected ruleTitle = '';
  protected ruleDetails = '';
  protected editingRuleId = signal<number | null>(null);
  protected showAnnouncementForm = signal(false);
  protected announcementTitle = '';
  protected announcementDetails = '';
  protected editingAnnouncementId = signal<number | null>(null);

  protected selectedDashboardRules() {
    const selectedId = this.selectedDashboardLinkshellId();
    if (this.primaryLinkshell()?.id !== selectedId) return [];
    return this.primaryLinkshell()?.rules ?? [];
  }

  protected selectedDashboardAnnouncements() {
    const selectedId = this.selectedDashboardLinkshellId();
    if (this.primaryLinkshell()?.id !== selectedId) return [];
    return this.primaryLinkshell()?.announcements ?? [];
  }

  protected canManageSelectedDashboard(): boolean {
    return this.canManageLinkshell(this.selectedDashboardLinkshellId());
  }

  protected toggleRuleForm(): void {
    this.showRuleForm.update(value => !value);
    this.editingRuleId.set(null);
    if (!this.showRuleForm()) {
      this.ruleTitle = '';
      this.ruleDetails = '';
    }
  }

  protected toggleAnnouncementForm(): void {
    this.showAnnouncementForm.update(value => !value);
    this.editingAnnouncementId.set(null);
    if (!this.showAnnouncementForm()) {
      this.announcementTitle = '';
      this.announcementDetails = '';
    }
  }

  protected startEditRule(rule: { id: number; title: string; details: string }): void {
    this.editingRuleId.set(rule.id);
    this.ruleTitle = rule.title;
    this.ruleDetails = rule.details;
    this.showRuleForm.set(true);
  }

  protected cancelEditRule(): void {
    this.editingRuleId.set(null);
    this.ruleTitle = '';
    this.ruleDetails = '';
    this.showRuleForm.set(false);
  }

  protected startEditAnnouncement(announcement: { id: number; title: string; details: string }): void {
    this.editingAnnouncementId.set(announcement.id);
    this.announcementTitle = announcement.title;
    this.announcementDetails = announcement.details;
    this.showAnnouncementForm.set(true);
  }

  protected cancelEditAnnouncement(): void {
    this.editingAnnouncementId.set(null);
    this.announcementTitle = '';
    this.announcementDetails = '';
    this.showAnnouncementForm.set(false);
  }

  protected async submitRule(): Promise<void> {
    const linkshellId = this.selectedDashboardLinkshellId();
    if (!linkshellId) return;
    const title = this.ruleTitle.trim();
    const details = this.ruleDetails.trim();
    if (!title || !details) return;
    const editingId = this.editingRuleId();
    try {
      if (editingId !== null) {
        await this.activity.updateRule(editingId, title, details);
      } else {
        await this.activity.createRule(linkshellId, title, details);
      }
      this.ruleTitle = '';
      this.ruleDetails = '';
      this.showRuleForm.set(false);
      this.editingRuleId.set(null);
    } catch {
      // error is surfaced via activity.actionError
    }
  }

  protected async submitAnnouncement(): Promise<void> {
    const linkshellId = this.selectedDashboardLinkshellId();
    if (!linkshellId) return;
    const title = this.announcementTitle.trim();
    const details = this.announcementDetails.trim();
    if (!title || !details) return;
    const editingId = this.editingAnnouncementId();
    try {
      if (editingId !== null) {
        await this.activity.updateAnnouncement(editingId, title, details);
      } else {
        await this.activity.createAnnouncement(linkshellId, title, details);
      }
      this.announcementTitle = '';
      this.announcementDetails = '';
      this.showAnnouncementForm.set(false);
      this.editingAnnouncementId.set(null);
    } catch {
      // error is surfaced via activity.actionError
    }
  }

  protected async deleteRule(ruleId: number): Promise<void> {
    try { await this.activity.deleteRule(ruleId); } catch { /* surfaced */ }
  }

  protected async deleteAnnouncement(announcementId: number): Promise<void> {
    try { await this.activity.deleteAnnouncement(announcementId); } catch { /* surfaced */ }
  }

  protected configLinkshellId = signal<number | null>(null);

  protected selectedConfigLinkshellId(): number | null {
    const explicit = this.configLinkshellId();
    if (explicit !== null) return explicit;
    return (
      this.activity.overview()?.appUser?.primaryLinkshellId ??
      this.primaryLinkshell()?.id ??
      this.activity.overview()?.linkshells?.[0]?.id ??
      null
    );
  }

  protected selectConfigLinkshell(linkshellId: number): void {
    this.configLinkshellId.set(linkshellId);
  }

  protected canManageConfigLinkshell(): boolean {
    const id = this.selectedConfigLinkshellId();
    return id !== null && this.canManageLinkshell(id);
  }

  protected configItems(): ActivityItem[] {
    const id = this.selectedConfigLinkshellId();
    if (this.primaryLinkshell()?.id !== id) return [];
    return this.primaryLinkshell()?.items ?? [];
  }

  protected configRevenue(): ActivityRevenueEntry[] {
    const id = this.selectedConfigLinkshellId();
    if (this.primaryLinkshell()?.id !== id) return [];
    return this.primaryLinkshell()?.revenueEntries ?? [];
  }

  protected configIncomeTotal(): number {
    return this.configRevenue()
      .filter(entry => entry.entryType === 'Income')
      .reduce((sum, entry) => sum + (entry.value ?? 0), 0);
  }

  protected configExpenseTotal(): number {
    return this.configRevenue()
      .filter(entry => entry.entryType === 'Expense')
      .reduce((sum, entry) => sum + (entry.value ?? 0), 0);
  }

  protected configNetTotal(): number {
    return this.configIncomeTotal() - this.configExpenseTotal();
  }

  protected configTotalItemQuantity(): number {
    return this.configItems().reduce((sum, item) => sum + (item.quantity ?? 0), 0);
  }

  protected showItemForm = signal(false);
  protected itemName = '';
  protected itemType = '';
  protected itemQuantity = 1;
  protected itemNotes = '';
  protected editingItemId = signal<number | null>(null);

  protected toggleItemForm(): void {
    this.showItemForm.update(value => !value);
    if (!this.showItemForm()) {
      this.resetItemForm();
    }
  }

  protected resetItemForm(): void {
    this.itemName = '';
    this.itemType = '';
    this.itemQuantity = 1;
    this.itemNotes = '';
    this.editingItemId.set(null);
  }

  protected beginEditItem(item: ActivityItem): void {
    this.editingItemId.set(item.id);
    this.itemName = item.itemName;
    this.itemType = item.itemType ?? '';
    this.itemQuantity = item.quantity;
    this.itemNotes = item.notes ?? '';
    this.showItemForm.set(true);
  }

  protected async submitItem(): Promise<void> {
    const linkshellId = this.selectedConfigLinkshellId();
    if (!linkshellId) return;
    const name = this.itemName.trim();
    if (!name) return;
    const input: ActivityItemInput = {
      itemName: name,
      itemType: this.itemType.trim() || null,
      quantity: Math.max(0, Math.floor(this.itemQuantity || 0)),
      notes: this.itemNotes.trim() || null
    };
    try {
      const editingId = this.editingItemId();
      if (editingId !== null) {
        await this.activity.updateItem(editingId, input);
      } else {
        await this.activity.createItem(linkshellId, input);
      }
      this.resetItemForm();
      this.showItemForm.set(false);
    } catch {
      // surfaced
    }
  }

  protected async deleteItem(itemId: number): Promise<void> {
    try { await this.activity.deleteItem(itemId); } catch { /* surfaced */ }
  }

  protected showRevenueForm = signal(false);
  protected revenueType: 'Income' | 'Expense' = 'Income';
  protected revenueCategory = '';
  protected revenueValue = 0;
  protected revenueDetails = '';

  protected toggleRevenueForm(): void {
    this.showRevenueForm.update(value => !value);
    if (!this.showRevenueForm()) {
      this.resetRevenueForm();
    }
  }

  protected resetRevenueForm(): void {
    this.revenueType = 'Income';
    this.revenueCategory = '';
    this.revenueValue = 0;
    this.revenueDetails = '';
  }

  protected async submitRevenue(): Promise<void> {
    const linkshellId = this.selectedConfigLinkshellId();
    if (!linkshellId) return;
    const value = Math.max(0, Math.floor(this.revenueValue || 0));
    if (value <= 0) return;
    const input: ActivityRevenueInput = {
      entryType: this.revenueType,
      category: this.revenueCategory.trim() || null,
      value,
      details: this.revenueDetails.trim() || null,
      occurredAt: null
    };
    try {
      await this.activity.createRevenueEntry(linkshellId, input);
      this.resetRevenueForm();
      this.showRevenueForm.set(false);
    } catch {
      // surfaced
    }
  }

  protected async deleteRevenue(entryId: number): Promise<void> {
    try { await this.activity.deleteRevenueEntry(entryId); } catch { /* surfaced */ }
  }

  protected showCreateLinkshellForm = signal(false);
  protected newLinkshellName = '';
  protected newLinkshellDetails = '';

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
    hybridDkpPercentage: number | null;
    enableHnmSection: boolean;
    enableMissions: boolean;
    enableAuctions: boolean;
    enableToDs: boolean;
    enableEndgame: boolean;
  } = {
    lootStructure: 'Dkp',
    hybridDkpPercentage: 50,
    enableHnmSection: true,
    enableMissions: true,
    enableAuctions: true,
    enableToDs: true,
    enableEndgame: true
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
    this.customizeDraft.hybridDkpPercentage = settings.hybridDkpPercentage ?? 50;
    this.customizeDraft.enableHnmSection = settings.enableHnmSection;
    this.customizeDraft.enableMissions = settings.enableMissions;
    this.customizeDraft.enableAuctions = settings.enableAuctions;
    this.customizeDraft.enableToDs = settings.enableToDs;
    this.customizeDraft.enableEndgame = settings.enableEndgame;
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
        hybridDkpPercentage: this.customizeDraft.lootStructure === 'Hybrid'
          ? this.customizeDraft.hybridDkpPercentage
          : null,
        enableHnmSection: this.customizeDraft.enableHnmSection,
        enableMissions: this.customizeDraft.enableMissions,
        enableAuctions: this.customizeDraft.enableAuctions,
        enableToDs: this.customizeDraft.enableToDs,
        enableEndgame: this.customizeDraft.enableEndgame
      });
      this.customizeDirty = false;
      this.syncCustomizeDraft();
    } catch {
      // surfaced by service
    }
  }

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

  protected dashboardUpcomingEvents() {
    return this.selectedDashboardEvents()
      .filter(event => !event.commencementStartTime)
      .slice(0, 4);
  }

  protected eventRelativeLabel(value?: string | null): string {
    const target = this.parseDate(value);
    if (!target) return '';
    const deltaMs = target - this.now();
    if (deltaMs <= 0) return 'Now';
    const minutes = Math.floor(deltaMs / 60000);
    if (minutes < 60) return `in ${minutes}m`;
    const hours = Math.floor(minutes / 60);
    const remainderMinutes = minutes % 60;
    if (hours < 24) {
      return remainderMinutes > 0 ? `in ${hours}h ${remainderMinutes}m` : `in ${hours}h`;
    }
    const days = Math.floor(hours / 24);
    return `in ${days} day${days === 1 ? '' : 's'}`;
  }

  protected eventClockLabel(value?: string | null): string {
    const target = this.parseDate(value);
    if (!target) return '—';
    const date = new Date(target);
    const now = new Date(this.now());
    const sameDay = date.toDateString() === now.toDateString();
    if (sameDay) {
      return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
    }
    const weekday = date.toLocaleDateString([], { weekday: 'short' });
    const time = date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false });
    return `${weekday} ${time}`;
  }

  protected dashboardHnmWindow: '7d' | '30d' | 'all' = '30d';

  protected dashboardHnmClaims(): { monsterName: string; count: number; percent: number; colorClass: string }[] {
    const tods = (this.activity.overview()?.recentTods ?? [])
      .filter(tod => tod.linkshellId === this.selectedDashboardLinkshellId() && tod.claim);

    const cutoffMs = this.dashboardHnmWindow === 'all'
      ? 0
      : this.dashboardHnmWindow === '7d' ? 7 * 86400000 : 30 * 86400000;

    const nowMs = this.now();
    const filtered = cutoffMs === 0 ? tods : tods.filter(tod => {
      const timeMs = this.parseDate(tod.time) ?? 0;
      return timeMs > 0 && (nowMs - timeMs) <= cutoffMs;
    });

    const counts = new Map<string, number>();
    for (const tod of filtered) {
      const name = (tod.monsterName ?? 'Unknown').trim();
      counts.set(name, (counts.get(name) ?? 0) + 1);
    }

    const total = filtered.length;
    const palette = ['a', 'b', 'c', 'd', 'e', 'f'];
    return Array.from(counts.entries())
      .sort((left, right) => right[1] - left[1])
      .slice(0, 6)
      .map((entry, index) => ({
        monsterName: entry[0],
        count: entry[1],
        percent: total === 0 ? 0 : (entry[1] / total) * 100,
        colorClass: palette[index % palette.length]
      }));
  }

  protected dashboardHnmClaimsTotal(): number {
    return this.dashboardHnmClaims().reduce((sum, entry) => sum + entry.count, 0);
  }

  protected donutOffset(index: number): number {
    const circumference = 251.2;
    const claims = this.dashboardHnmClaims();
    const total = claims.reduce((sum, entry) => sum + entry.count, 0);
    if (total === 0) return 0;
    let offset = 0;
    for (let i = 0; i < index; i += 1) {
      offset += (claims[i].count / total) * circumference;
    }
    return -offset;
  }

  protected donutSegmentLength(count: number): number {
    const circumference = 251.2;
    const total = this.dashboardHnmClaims().reduce((sum, entry) => sum + entry.count, 0);
    if (total === 0) return 0;
    return (count / total) * circumference;
  }

  protected dashboardNewsUpdates(): {
    title: string;
    subtitle: string;
    dkp: number | null;
    relative: string;
    colorClass: string;
    when: number;
  }[] {
    const selectedId = this.selectedDashboardLinkshellId();
    const palette = ['a', 'b', 'c', 'd', 'e', 'f'];
    const items: ReturnType<typeof this.dashboardNewsUpdates> = [];

    const tods = (this.activity.overview()?.recentTods ?? []).filter(tod => tod.linkshellId === selectedId);
    for (const tod of tods) {
      if (!tod.claim || !tod.lootDetails?.length) continue;
      const when = this.parseDate(tod.time) ?? 0;
      for (const loot of tod.lootDetails) {
        if (!loot.itemName) continue;
        const winner = (loot.itemWinner ?? '').trim();
        items.push({
          title: loot.itemName,
          subtitle: `${tod.monsterName} defeated${winner ? ` · ${winner}` : ''}`,
          dkp: loot.winningDkpSpent ?? null,
          relative: this.shortPastRelative(when),
          colorClass: palette[(tod.monsterName?.length ?? 0) % palette.length],
          when
        });
      }
    }

    return items
      .filter(item => item.when > 0)
      .sort((left, right) => right.when - left.when)
      .slice(0, 8);
  }

  protected activityFilter: 'all' | 'kills' | 'claims' | 'events' | 'loot' = 'all';

  protected dashboardRecentActivity(): {
    kind: 'loot' | 'no-claim' | 'claim' | 'event' | 'announcement' | 'rule';
    name: string;
    action: string;
    detail: string;
    dkp: number | null;
    categoryLabel: string;
    relative: string;
    when: number;
  }[] {
    const selectedId = this.selectedDashboardLinkshellId();
    const items: ReturnType<typeof this.dashboardRecentActivity> = [];

    const tods = (this.activity.overview()?.recentTods ?? []).filter(tod => tod.linkshellId === selectedId);
    for (const tod of tods) {
      const when = this.parseDate(tod.time) ?? 0;
      if (tod.claim && tod.lootDetails?.length) {
        for (const loot of tod.lootDetails) {
          const winner = (loot.itemWinner ?? '').trim();
          const dkp = loot.winningDkpSpent ?? null;
          const detail = `${loot.itemName || 'Loot'}${winner ? ` → ${winner}` : ''}`;
          items.push({
            kind: 'loot',
            name: tod.monsterName,
            action: 'defeated',
            detail,
            dkp,
            categoryLabel: 'Loot',
            relative: this.longPastRelative(when),
            when
          });
        }
      } else if (tod.claim) {
        const linkshell = this.activity.overview()?.linkshells?.find(ls => ls.id === tod.linkshellId);
        items.push({
          kind: 'claim',
          name: tod.monsterName,
          action: 'claimed',
          detail: linkshell?.name ? `by ${linkshell.name}` : '',
          dkp: null,
          categoryLabel: 'Claimed',
          relative: this.longPastRelative(when),
          when
        });
      } else {
        items.push({
          kind: 'no-claim',
          name: tod.monsterName,
          action: 'defeated',
          detail: 'No claim',
          dkp: null,
          categoryLabel: 'No claim',
          relative: this.longPastRelative(when),
          when
        });
      }
    }

    for (const history of this.selectedDashboardHistory()) {
      const when = this.parseDate(history.endTime) ?? 0;
      const parts: string[] = [`${history.participantCount} participants`];
      if (history.type) parts.push(history.type);
      if (history.location) parts.push(history.location);
      items.push({
        kind: 'event',
        name: history.name || 'Event',
        action: 'completed',
        detail: parts.join(' · '),
        dkp: null,
        categoryLabel: 'Event',
        relative: this.longPastRelative(when),
        when
      });
    }

    const primary = this.activity.overview()?.primaryLinkshell;
    if (primary && primary.id === selectedId) {
      for (const rule of primary.rules ?? []) {
        const when = this.parseDate(rule.createdAt) ?? 0;
        items.push({
          kind: 'rule',
          name: rule.title,
          action: 'rule added',
          detail: rule.createdByCharacterName ? `by ${rule.createdByCharacterName}` : '',
          dkp: null,
          categoryLabel: 'Rule',
          relative: this.longPastRelative(when),
          when
        });
      }
      for (const announcement of primary.announcements ?? []) {
        const when = this.parseDate(announcement.createdAt) ?? 0;
        items.push({
          kind: 'announcement',
          name: announcement.title,
          action: 'announcement posted',
          detail: announcement.createdByCharacterName ? `by ${announcement.createdByCharacterName}` : '',
          dkp: null,
          categoryLabel: 'Announcement',
          relative: this.longPastRelative(when),
          when
        });
      }
    }

    const filter = this.activityFilter;
    const filtered = items.filter(item => {
      if (filter === 'all') return true;
      if (filter === 'kills') return item.kind === 'loot' || item.kind === 'no-claim' || item.kind === 'claim';
      if (filter === 'claims') return item.kind === 'claim';
      if (filter === 'events') return item.kind === 'event' || item.kind === 'announcement' || item.kind === 'rule';
      if (filter === 'loot') return item.kind === 'loot';
      return true;
    });

    return filtered
      .filter(item => item.when > 0)
      .sort((left, right) => right.when - left.when)
      .slice(0, 10);
  }

  private shortPastRelative(whenMs: number): string {
    if (!whenMs) return '';
    const deltaSeconds = Math.max(0, Math.floor((this.now() - whenMs) / 1000));
    const minutes = Math.floor(deltaSeconds / 60);
    if (minutes < 60) return minutes <= 1 ? 'just now' : `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days === 1) return 'yesterday';
    if (days < 7) {
      const date = new Date(whenMs);
      return date.toLocaleDateString([], { weekday: 'short' });
    }
    return `${days}d ago`;
  }

  private longPastRelative(whenMs: number): string {
    if (!whenMs) return '';
    const deltaSeconds = Math.max(0, Math.floor((this.now() - whenMs) / 1000));
    const days = Math.floor(deltaSeconds / 86400);
    const hours = Math.floor((deltaSeconds % 86400) / 3600);
    const minutes = Math.floor((deltaSeconds % 3600) / 60);
    return `${days}d ${hours}h ${minutes}m`;
  }

  protected filteredDashboardMembers() {
    const term = this.dashboardRosterSearch.trim().toLowerCase();
    const members = this.selectedDashboardMembers();
    if (!term) return members;
    return members.filter(member =>
      (member.characterName ?? '').toLowerCase().includes(term) ||
      (member.rank ?? '').toLowerCase().includes(term)
    );
  }

  protected selectedDashboardEvents() {
    const selectedId = this.selectedDashboardLinkshellId();
    return [...(this.activity.overview()?.activeEvents ?? [])]
      .filter(event => event.linkshellId === selectedId)
      .sort((left, right) => {
        const leftTime = left.startTime ? new Date(left.startTime).getTime() : 0;
        const rightTime = right.startTime ? new Date(right.startTime).getTime() : 0;
        return leftTime - rightTime;
      });
  }

  protected selectedDashboardHistory() {
    const selectedId = this.selectedDashboardLinkshellId();
    return this.activity.historyList().filter(history => history.linkshellId === selectedId);
  }

  protected selectedDashboardTods() {
    const selectedId = this.selectedDashboardLinkshellId();
    return [...(this.activity.overview()?.recentTods ?? [])]
      .filter(tod => tod.linkshellId === selectedId)
      .sort((left, right) => {
        const leftTime = left.time ? new Date(left.time).getTime() : 0;
        const rightTime = right.time ? new Date(right.time).getTime() : 0;
        return rightTime - leftTime;
      });
  }

  protected readonly expandedTodGroups = signal<Set<string>>(new Set());

  protected groupedDashboardTods(): { key: string; latest: any; history: any[] }[] {
    const groups = new Map<string, any[]>();
    for (const tod of this.selectedDashboardTods()) {
      const key = (tod.monsterName ?? '').trim().toLowerCase() || `__${tod.id}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(tod);
    }
    return Array.from(groups.entries()).map(([key, entries]) => ({
      key,
      latest: entries[0],
      history: entries.slice(1, 10)
    }));
  }

  protected isTodGroupExpanded(key: string): boolean {
    return this.expandedTodGroups().has(key);
  }

  protected toggleTodGroup(key: string): void {
    const next = new Set(this.expandedTodGroups());
    if (next.has(key)) next.delete(key); else next.add(key);
    this.expandedTodGroups.set(next);
  }

  protected todCharacterNames() {
    return [...new Set(this.selectedDashboardMembers().map(member => member.characterName).filter(name => name.trim().length > 0))]
      .sort((left, right) => left.localeCompare(right));
  }

  protected liveEventElapsedMs(event: { commencementStartTime?: string | null; startTime?: string | null }): number {
    return this.elapsedMs(event.commencementStartTime || event.startTime);
  }

  protected liveEventTimerLabel(event: { commencementStartTime?: string | null; startTime?: string | null }): string {
    return this.formatElapsed(this.liveEventElapsedMs(event));
  }

  protected participantElapsedMs(
    participant: { startTime?: string | null; resumeTime?: string | null; duration?: number | null; isOnBreak?: boolean | null },
    event: { commencementStartTime?: string | null; startTime?: string | null }
  ): number {
    const accumulatedMs = Math.max(0, participant.duration ?? 0) * 3600000;
    if (participant.isOnBreak) {
      return accumulatedMs;
    }

    return accumulatedMs + this.elapsedMs(participant.resumeTime || participant.startTime || event.commencementStartTime || event.startTime);
  }

  protected participantTimerLabel(
    participant: { startTime?: string | null; resumeTime?: string | null; duration?: number | null; isOnBreak?: boolean | null },
    event: { commencementStartTime?: string | null; startTime?: string | null }
  ): string {
    return this.formatElapsed(this.participantElapsedMs(participant, event));
  }

  protected participantCurrentDkp(
    participant: { startTime?: string | null; resumeTime?: string | null; duration?: number | null; isOnBreak?: boolean | null },
    event: { commencementStartTime?: string | null; startTime?: string | null; dkpPerHour?: number | null }
  ): string {
    return this.formatDkp(this.participantElapsedMs(participant, event), event.dkpPerHour);
  }

  protected participantProgressPercent(
    participant: { startTime?: string | null; resumeTime?: string | null; duration?: number | null; isOnBreak?: boolean | null },
    event: { commencementStartTime?: string | null; startTime?: string | null; duration?: number | null }
  ): number {
    const plannedHours = event.duration ?? 0;
    if (plannedHours <= 0) {
      return 0;
    }

    return Math.min(100, (this.participantElapsedMs(participant, event) / (plannedHours * 3600000)) * 100);
  }

  protected isCurrentParticipant(
    participant: { id: number },
    event: { currentParticipation?: { id: number } | null }
  ): boolean {
    return event.currentParticipation?.id === participant.id;
  }

  protected attendanceBadgeLabel(participant: { isOnBreak?: boolean | null; isVerified?: boolean | null }): string {
    if (participant.isOnBreak) {
      return 'On break';
    }

    if (participant.isVerified === true) {
      return 'Verified attendance';
    }

    if (participant.isVerified === false) {
      return 'Attendance denied';
    }

    return 'Pending attendance';
  }

  protected pendingReturnLedgerEntries(participant: ActivityEventParticipant): ActivityStatusLedgerEntry[] {
    return participant.statusLedger
      .filter(entry =>
        entry.actionType === 'BreakReturn' &&
        entry.requiresVerification &&
        !entry.verifiedAt &&
        !entry.deniedAt)
      .slice()
      .sort((a, b) => new Date(a.occurredAt).getTime() - new Date(b.occurredAt).getTime());
  }

  protected hasPendingReturnVerification(participant: ActivityEventParticipant): boolean {
    return this.pendingReturnLedgerEntries(participant).length > 0;
  }

  protected pendingReturnLedgerLabel(
    participant: ActivityEventParticipant,
    entry: ActivityStatusLedgerEntry
  ): string {
    const info = this.breakSessionInfo(participant, entry.id);
    if (!info) {
      return 'Break Session';
    }
    return `Break Session #${info.sessionNumber} · ${this.formatBreakDuration(info.durationMs)}`;
  }

  private breakSessionInfo(
    participant: ActivityEventParticipant,
    breakReturnId: number
  ): { sessionNumber: number; durationMs: number } | null {
    const sorted = [...participant.statusLedger].sort(
      (a, b) => new Date(a.occurredAt).getTime() - new Date(b.occurredAt).getTime()
    );
    let currentStart: ActivityStatusLedgerEntry | null = null;
    let sessionNumber = 0;
    for (const entry of sorted) {
      if (entry.actionType === 'BreakStart') {
        currentStart = entry;
        sessionNumber += 1;
      } else if (entry.actionType === 'BreakReturn' && currentStart) {
        if (entry.id === breakReturnId) {
          const durationMs = Math.max(
            0,
            new Date(entry.occurredAt).getTime() - new Date(currentStart.occurredAt).getTime()
          );
          return { sessionNumber, durationMs };
        }
        currentStart = null;
      }
    }
    return null;
  }

  private formatBreakDuration(ms: number): string {
    if (!Number.isFinite(ms) || ms <= 0) {
      return '0s';
    }
    const totalSeconds = Math.floor(ms / 1000);
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;
    if (hours > 0) {
      return `${hours}h ${minutes}m`;
    }
    if (minutes > 0) {
      return `${minutes}m ${seconds}s`;
    }
    return `${seconds}s`;
  }

  protected async onDashboardLinkshellChange(linkshellId: number): Promise<void> {
    if (!linkshellId || linkshellId === this.selectedDashboardLinkshellId()) {
      return;
    }

    await this.activity.setPrimaryLinkshell(linkshellId);
    this.resetTodDraft(linkshellId);
  }

  protected onTodMonsterChange(monsterName: string): void {
    this.todDraft.monsterName = monsterName;
    if (monsterName !== 'Other') {
      this.todCustomMonsterName = '';
      const usesLongWindow = ActivityHomeComponent.longWindowTodMonsters.has(monsterName.trim());
      this.todDraft.cooldown = usesLongWindow ? '72 Hour' : '22 Hour';
      this.todDraft.interval = usesLongWindow ? '1 Hour' : '10 Min';
    }
    this.updateTodRepopTime();
  }

  protected isTodMonsterOther(): boolean {
    return this.todDraft.monsterName === 'Other';
  }

  protected onTodClaimChange(claim: boolean): void {
    this.todDraft.claim = claim;
    if (!claim) {
      this.todDraft.noLoot = true;
      this.todDraft.lootDetails = [this.createEmptyTodLootRow()];
    }
  }

  protected onTodClaimChoiceChange(value: 'Yes' | 'No' | 'NotSpecified'): void {
    this.todClaimChoice = value;
    this.onTodClaimChange(value === 'Yes');
  }

  protected onTodDayNumberNotSpecifiedChange(enabled: boolean): void {
    this.todDayNumberNotSpecified = enabled;
    if (enabled) {
      this.todDraft.dayNumber = null;
    }
  }

  protected isTodCooldownOther(): boolean {
    return this.todDraft.cooldown === 'Other';
  }

  protected onTodCustomCooldownChange(hours: number | null): void {
    this.todCustomCooldownHours = hours;
    this.updateTodRepopTime();
  }

  protected todCooldownLabelForSummary(): string {
    const cooldown = this.todDraft.cooldown;
    if (cooldown === 'Other') {
      const hours = this.todCustomCooldownHours;
      return hours && hours > 0 ? `${hours} Hour` : 'Custom';
    }
    return cooldown || '22 Hour';
  }

  private resolveCooldownHours(): number {
    if (this.todDraft.cooldown === '72 Hour') {
      return 72;
    }
    if (this.todDraft.cooldown === 'Other') {
      return Math.max(0, this.todCustomCooldownHours ?? 0);
    }
    return 22;
  }

  protected onTodTimeLocalChange(value: string): void {
    this.todTimeLocalValue = value;
    this.updateTodRepopTime();
  }

  protected onTodNoLootChange(noLoot: boolean): void {
    this.todDraft.noLoot = noLoot;
    if (noLoot) {
      this.todDraft.lootDetails = [this.createEmptyTodLootRow()];
    }
  }

  protected updateTodRepopTime(): void {
    this.todDraft.timeLocal = this.todTimeLocalValue;
    if (!this.todDraft.timeLocal) {
      this.todRepopLocalValue = '';
      return;
    }

    const todLocalTime = this.parseLocalDateTime(this.todDraft.timeLocal);
    if (!todLocalTime) {
      this.todRepopLocalValue = '';
      return;
    }

    const cooldownHours = this.resolveCooldownHours();
    if (cooldownHours <= 0) {
      this.todRepopLocalValue = '';
      return;
    }
    todLocalTime.setHours(todLocalTime.getHours() + cooldownHours);
    this.todRepopLocalValue = this.toDateTimeLocalValue(todLocalTime);
  }

  protected todRepopSummary(): string {
    if (!this.todRepopLocalValue) {
      return 'Pick a date and time to calculate the next repop window.';
    }

    return this.activity.formatDateTime(this.todRepopLocalValue) ?? this.todRepopLocalValue;
  }

  protected addTodLootRow(): void {
    this.todDraft.lootDetails = [...this.todDraft.lootDetails, this.createEmptyTodLootRow()];
  }

  protected removeTodLootRow(index: number): void {
    if (this.todDraft.lootDetails.length === 1) {
      this.todDraft.lootDetails = [this.createEmptyTodLootRow()];
      return;
    }

    this.todDraft.lootDetails = this.todDraft.lootDetails.filter((_, lootIndex) => lootIndex !== index);
  }

  protected todCountdownLabel(tod: { repopTime?: string | null }): string {
    const remainingMilliseconds = this.remainingMs(tod.repopTime);
    return remainingMilliseconds <= 0 ? 'Ready' : this.formatElapsed(remainingMilliseconds);
  }

  protected isTodReady(tod: { repopTime?: string | null }): boolean {
    return this.remainingMs(tod.repopTime) <= 0;
  }

  protected async submitTod(): Promise<void> {
    const linkshellId = this.selectedDashboardLinkshellId();
    if (!linkshellId) {
      this.activity.actionError.set('Create or join a linkshell before logging ToD entries.');
      this.activity.actionMessage.set(null);
      return;
    }

    if (!this.todTimeLocalValue || !this.todDraft.timeLocal.trim()) {
      this.activity.actionError.set('Time of Death is required.');
      this.activity.actionMessage.set(null);
      return;
    }

    let monsterName = this.todDraft.monsterName;
    if (monsterName === 'Other') {
      const custom = this.todCustomMonsterName.trim();
      if (!custom) {
        this.activity.actionError.set('Enter the custom monster name.');
        this.activity.actionMessage.set(null);
        return;
      }
      monsterName = custom;
    }

    let cooldown = this.todDraft.cooldown;
    if (cooldown === 'Other') {
      const hours = this.todCustomCooldownHours;
      if (!hours || hours <= 0) {
        this.activity.actionError.set('Enter a positive cooldown in hours.');
        this.activity.actionMessage.set(null);
        return;
      }
      cooldown = `${hours} Hour`;
    }

    const dayNumber = this.todDayNumberNotSpecified ? null : this.todDraft.dayNumber;
    const interval = this.todDraft.interval === 'Not specified' ? null : this.todDraft.interval;
    const lootStructure = this.lootStructureFor(linkshellId);
    const shouldIncludeLoot = this.todDraft.claim && !this.todDraft.noLoot && lootStructure !== 'LootCouncil';
    const lootDetails = shouldIncludeLoot
      ? this.todDraft.lootDetails.map(detail => ({
          itemName: detail.itemName?.trim() || null,
          itemWinner: detail.itemWinner?.trim() || null,
          winningDkpSpent: detail.winningDkpSpent ?? null
        }))
      : [];
    if (shouldIncludeLoot && lootStructure === 'Hybrid') {
      for (const detail of lootDetails) {
        const pct = Number(detail.winningDkpSpent ?? 0);
        if (detail.itemName && (!Number.isFinite(pct) || pct < 0 || pct > 100)) {
          this.activity.actionError.set('Deduction % must be between 0 and 100 on every loot row.');
          this.activity.actionMessage.set(null);
          return;
        }
      }
    }

    try {
      if (this.editingTodId !== null) {
        await this.activity.updateTod({
          todId: this.editingTodId,
          monsterName,
          dayNumber,
          claim: this.todDraft.claim,
          timeLocal: this.todDraft.timeLocal,
          cooldown,
          interval,
          noLoot: this.todDraft.noLoot,
          lootDetails,
          imagePath: this.todImagePath
        });
      } else {
        await this.activity.createTod({
          linkshellId,
          monsterName,
          dayNumber,
          claim: this.todDraft.claim,
          timeLocal: this.todDraft.timeLocal,
          cooldown,
          interval,
          noLoot: this.todDraft.noLoot,
          lootDetails,
          imagePath: this.todImagePath
        });
      }
      this.editingTodId = null;
      this.resetTodDraft(linkshellId);
    } catch {
      // Service already exposes the action error state.
    }
  }

  protected async onTodImageFileChange(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }

    this.isUploadingTodImage = true;
    try {
      const path = await this.activity.uploadTodImage(file);
      if (path) {
        this.todImagePath = path;
      }
    } finally {
      this.isUploadingTodImage = false;
      input.value = '';
    }
  }

  protected removeTodImage(): void {
    this.todImagePath = null;
  }

  protected beginEditTod(tod: any): void {
    const linkshellId = tod.linkshellId ?? this.selectedDashboardLinkshellId();
    this.editingTodId = tod.id;
    this.todDraft.linkshellId = linkshellId;
    this.todDraft.dayNumber = tod.dayNumber ?? null;
    this.todDraft.claim = !!tod.claim;
    this.todClaimChoice = tod.claim ? 'Yes' : 'No';
    this.todDayNumberNotSpecified = tod.dayNumber == null;

    const monsterName: string = tod.monsterName ?? '';
    const presets = ActivityHomeComponent.todMonsterOptions as readonly string[];
    if (presets.includes(monsterName)) {
      this.todDraft.monsterName = monsterName;
      this.todCustomMonsterName = '';
    } else {
      this.todDraft.monsterName = 'Other';
      this.todCustomMonsterName = monsterName;
    }

    const cooldown: string = tod.cooldown ?? '22 Hour';
    const cooldownPresets = ActivityHomeComponent.todCooldownOptions as readonly string[];
    if (cooldownPresets.includes(cooldown)) {
      this.todDraft.cooldown = cooldown;
      this.todCustomCooldownHours = null;
    } else {
      const match = /^\s*(\d+(?:\.\d+)?)\s*(?:hours?|hr|h)?\s*$/i.exec(cooldown);
      this.todDraft.cooldown = 'Other';
      this.todCustomCooldownHours = match ? parseFloat(match[1]) : null;
    }

    this.todDraft.interval = tod.interval || 'Not specified';
    this.todDraft.noLoot = !(tod.lootDetails && tod.lootDetails.length > 0);
    this.todDraft.lootDetails = (tod.lootDetails && tod.lootDetails.length > 0
      ? tod.lootDetails.map((loot: any) => ({
          itemName: loot.itemName ?? '',
          itemWinner: loot.itemWinner ?? '',
          winningDkpSpent: loot.winningDkpSpent ?? null
        }))
      : [this.createEmptyTodLootRow()]);

    if (tod.time) {
      const d = new Date(tod.time);
      if (!Number.isNaN(d.getTime())) {
        this.todTimeLocalValue = this.toDateTimeLocalValue(d);
      } else {
        this.todTimeLocalValue = '';
      }
    } else {
      this.todTimeLocalValue = '';
    }
    this.todDraft.timeLocal = this.todTimeLocalValue;

    this.todImagePath = tod.imagePath ?? null;
    this.updateTodRepopTime();

    window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' });
  }

  protected cancelTodEdit(): void {
    this.editingTodId = null;
    this.resetTodDraft();
  }

  protected async deleteTod(todId: number, monsterName: string): Promise<void> {
    if (!window.confirm(`Delete ${monsterName}? This also reverses any attached ToD loot DKP.`)) {
      return;
    }

    try {
      await this.activity.deleteTod(todId);
    } catch {
      // Service already exposes the action error state.
    }
  }

  protected getLootDraft(eventId: number): ActivityLootInput {
    this.lootDrafts[eventId] ??= {
      itemName: '',
      itemWinner: '',
      winningDkpSpent: 0
    };

    return this.lootDrafts[eventId];
  }

  protected getQuickJoinDraft(eventId: number): ActivityQuickJoinInput {
    this.quickJoinDrafts[eventId] ??= {
      jobName: '',
      subJobName: '',
      jobType: ''
    };

    return this.quickJoinDrafts[eventId];
  }

  protected async submitLoot(eventId: number): Promise<void> {
    const draft = this.getLootDraft(eventId);
    if (!draft.itemName.trim()) {
      this.activity.actionError.set('Loot item name is required.');
      this.activity.actionMessage.set(null);
      return;
    }

    const event = (this.activity.overview()?.activeEvents ?? []).find(item => item.id === eventId);
    const structure = event ? this.lootStructureFor(event.linkshellId) : 'Dkp';
    if (structure === 'LootCouncil') {
      draft.winningDkpSpent = 0;
    } else if (structure === 'Hybrid') {
      const pct = Number(draft.winningDkpSpent ?? 0);
      if (!Number.isFinite(pct) || pct < 0 || pct > 100) {
        this.activity.actionError.set('Deduction % must be between 0 and 100.');
        this.activity.actionMessage.set(null);
        return;
      }
    }

    try {
      await this.activity.addLoot(eventId, draft);
      this.lootDrafts[eventId] = {
        itemName: '',
        itemWinner: '',
        winningDkpSpent: 0
      };
    } catch {
      // Service already exposes the action error state.
    }
  }

  protected async submitQuickJoin(eventId: number): Promise<void> {
    const draft = this.getQuickJoinDraft(eventId);
    if (!draft.jobName || !draft.subJobName || !draft.jobType) {
      this.activity.actionError.set('Role, main job, and sub job are required for late join.');
      this.activity.actionMessage.set(null);
      return;
    }

    try {
      await this.activity.quickJoinLiveEvent(eventId, draft);
      this.quickJoinDrafts[eventId] = {
        jobName: '',
        subJobName: '',
        jobType: ''
      };
    } catch {
      // Service already exposes the action error state.
    }
  }

  private createEmptyTodLootRow(): ActivityTodLootInput {
    return {
      itemName: '',
      itemWinner: '',
      winningDkpSpent: null
    };
  }

  private resetTodDraft(selectedLinkshellId = this.selectedDashboardLinkshellId()): void {
    this.todDraft.linkshellId = selectedLinkshellId;
    this.todDraft.monsterName = ActivityHomeComponent.todMonsterOptions[0];
    this.todDraft.dayNumber = null;
    this.todDraft.claim = true;
    this.todDraft.timeLocal = '';
    this.todDraft.cooldown = '22 Hour';
    this.todDraft.interval = '10 Min';
    this.todDraft.noLoot = true;
    this.todDraft.lootDetails = [this.createEmptyTodLootRow()];
    this.todTimeLocalValue = '';
    this.todRepopLocalValue = '';
    this.todCustomMonsterName = '';
    this.todClaimChoice = 'Yes';
    this.todDayNumberNotSpecified = false;
    this.todCustomCooldownHours = null;
    this.todImagePath = null;
    this.isUploadingTodImage = false;
  }

  private remainingMs(targetValue?: string | null): number {
    const targetTime = this.parseDate(targetValue);
    if (!targetTime) {
      return 0;
    }

    return Math.max(0, targetTime - this.now());
  }

  private toDateTimeLocalValue(date: Date): string {
    const pad = (value: number) => value.toString().padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
  }

  private elapsedMs(startValue?: string | null): number {
    const startTime = this.parseDate(startValue);
    if (!startTime) {
      return 0;
    }

    return Math.max(0, this.now() - startTime);
  }

  private parseDate(value?: string | null): number | null {
    if (!value) {
      return null;
    }

    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? null : parsed.getTime();
  }

  private parseLocalDateTime(value?: string | null): Date | null {
    if (!value) {
      return null;
    }

    const parsed = new Date(value);
    return Number.isNaN(parsed.getTime()) ? null : parsed;
  }

  private formatElapsed(totalMilliseconds: number): string {
    const totalSeconds = Math.max(0, Math.floor(totalMilliseconds / 1000));
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;

    return [hours, minutes, seconds].map(value => value.toString().padStart(2, '0')).join(':');
  }

  private formatDkp(totalMilliseconds: number, dkpPerHour?: number | null): string {
    const rate = dkpPerHour ?? 0;
    return ((totalMilliseconds / 3600000) * rate).toFixed(2);
  }
}
