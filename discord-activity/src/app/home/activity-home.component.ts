import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityCreateTodInput,
  ActivityQuickJoinInput,
  ActivityLootInput,
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
    'Simurgh'
  ] as const;

  private static readonly todCooldownOptions = ['22 Hour', '72 Hour'] as const;
  private static readonly todIntervalOptions = ['10 Min', '1 Hour'] as const;
  private static readonly longWindowTodMonsters = new Set(['Tiamat', 'Jormungand', 'Vrtra']);

  protected readonly activity = inject(DiscordActivityService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());
  protected readonly activeTab = signal<'dashboard' | 'linkshell' | 'events' | 'tods' | 'auctions' | 'dkp' | 'other'>('dashboard');

  protected setActiveTab(tab: 'dashboard' | 'linkshell' | 'events' | 'tods' | 'auctions' | 'dkp' | 'other'): void {
    this.activeTab.set(tab);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  protected initials(value: string | null | undefined): string {
    const name = (value ?? '').trim();
    if (!name) return '??';
    const parts = name.split(/\s+/).filter(Boolean);
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[1][0]).toUpperCase();
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
  protected todDateValue = '';
  protected todTimeValue = '';
  protected todRepopLocalValue = '';
  protected todRepopDateValue = '';
  protected todRepopTimeValue = '';
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

  protected pendingReturnLedgerEntries(participant: { statusLedger: Array<{ actionType: string; requiresVerification: boolean; verifiedAt?: string | null }> }) {
    return participant.statusLedger.filter(entry =>
      entry.actionType === 'BreakReturn' &&
      entry.requiresVerification &&
      !entry.verifiedAt);
  }

  protected hasPendingReturnVerification(participant: { statusLedger: Array<{ actionType: string; requiresVerification: boolean; verifiedAt?: string | null }> }): boolean {
    return this.pendingReturnLedgerEntries(participant).length > 0;
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
    const usesLongWindow = ActivityHomeComponent.longWindowTodMonsters.has(monsterName.trim());
    this.todDraft.cooldown = usesLongWindow ? '72 Hour' : '22 Hour';
    this.todDraft.interval = usesLongWindow ? '1 Hour' : '10 Min';
    this.updateTodRepopTime();
  }

  protected onTodClaimChange(claim: boolean): void {
    this.todDraft.claim = claim;
    if (!claim) {
      this.todDraft.noLoot = true;
      this.todDraft.lootDetails = [this.createEmptyTodLootRow()];
    }
  }

  protected onTodDateChange(value: string): void {
    this.todDateValue = value;
    this.updateTodRepopTime();
  }

  protected onTodTimeChange(value: string): void {
    this.todTimeValue = value;
    this.updateTodRepopTime();
  }

  protected onTodNoLootChange(noLoot: boolean): void {
    this.todDraft.noLoot = noLoot;
    if (noLoot) {
      this.todDraft.lootDetails = [this.createEmptyTodLootRow()];
    }
  }

  protected setTodToNow(): void {
    const now = new Date();
    this.todDateValue = this.toDateInputValue(now);
    this.todTimeValue = this.toTimeInputValue(now);
    this.updateTodRepopTime();
  }

  protected updateTodRepopTime(): void {
    this.todDraft.timeLocal = this.combineLocalDateTime(this.todDateValue, this.todTimeValue);
    if (!this.todDraft.timeLocal) {
      this.todRepopLocalValue = '';
      this.todRepopDateValue = '';
      this.todRepopTimeValue = '';
      return;
    }

    const todLocalTime = this.parseLocalDateTime(this.todDraft.timeLocal);
    if (!todLocalTime) {
      this.todRepopLocalValue = '';
      this.todRepopDateValue = '';
      this.todRepopTimeValue = '';
      return;
    }

    const cooldownHours = this.todDraft.cooldown === '72 Hour' ? 72 : 22;
    todLocalTime.setHours(todLocalTime.getHours() + cooldownHours);
    this.todRepopLocalValue = this.toDateTimeLocalValue(todLocalTime);
    this.todRepopDateValue = this.toDateInputValue(todLocalTime);
    this.todRepopTimeValue = this.toTimeInputValue(todLocalTime);
  }

  protected todRepopSummary(): string {
    if (!this.todRepopDateValue || !this.todRepopTimeValue) {
      return 'Pick a date and time to calculate the next repop window.';
    }

    const repopValue = this.combineLocalDateTime(this.todRepopDateValue, this.todRepopTimeValue);
    return this.activity.formatDateTime(repopValue) ?? `${this.todRepopDateValue} ${this.todRepopTimeValue}`;
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

    if (!this.todDateValue || !this.todTimeValue || !this.todDraft.timeLocal.trim()) {
      this.activity.actionError.set('Time of Death is required.');
      this.activity.actionMessage.set(null);
      return;
    }

    try {
      await this.activity.createTod({
        linkshellId,
        monsterName: this.todDraft.monsterName,
        dayNumber: this.todDraft.dayNumber,
        claim: this.todDraft.claim,
        timeLocal: this.todDraft.timeLocal,
        cooldown: this.todDraft.cooldown,
        interval: this.todDraft.interval,
        noLoot: this.todDraft.noLoot,
        lootDetails: this.todDraft.lootDetails.map(detail => ({
          itemName: detail.itemName?.trim() || null,
          itemWinner: detail.itemWinner?.trim() || null,
          winningDkpSpent: detail.winningDkpSpent ?? null
        }))
      });
      this.resetTodDraft(linkshellId);
    } catch {
      // Service already exposes the action error state.
    }
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
    this.todDateValue = '';
    this.todTimeValue = '';
    this.todRepopLocalValue = '';
    this.todRepopDateValue = '';
    this.todRepopTimeValue = '';
  }

  private combineLocalDateTime(dateValue?: string | null, timeValue?: string | null): string {
    if (!dateValue || !timeValue) {
      return '';
    }

    const normalizedTime = timeValue.length === 5 ? `${timeValue}:00` : timeValue;
    return `${dateValue}T${normalizedTime}`;
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

  private toDateInputValue(date: Date): string {
    const pad = (value: number) => value.toString().padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
  }

  private toTimeInputValue(date: Date): string {
    const pad = (value: number) => value.toString().padStart(2, '0');
    return `${pad(date.getHours())}:${pad(date.getMinutes())}`;
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
