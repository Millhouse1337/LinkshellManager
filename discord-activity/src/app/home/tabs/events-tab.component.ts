import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityAttendanceWindow,
  ActivityEventParticipant,
  ActivityLinkshellSettings,
  ActivityLootInput,
  ActivityLootStructure,
  ActivityQuickJoinInput,
  ActivityStatusLedgerEntry,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import { ActivityEvent } from '../../discord/discord-activity.types';
import { ActivityQueuePanelComponent } from '../activity-queue-panel.component';
import { ActivitySidebarPanelComponent } from '../activity-sidebar-panel.component';
import {
  EVENT_JOB_TYPE_OPTIONS,
  EVENT_MAIN_JOB_OPTIONS,
  EVENT_SUB_JOB_OPTIONS
} from '../event-job-options';
import {
  breakSessionInfo,
  formatBreakDuration,
  formatDkp,
  formatElapsed,
  parseDate
} from '../activity-home.helpers';

@Component({
  selector: 'app-events-tab',
  imports: [CommonModule, FormsModule, ActivityQueuePanelComponent, ActivitySidebarPanelComponent],
  templateUrl: './events-tab.component.html',
  styleUrl: './events-tab.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class EventsTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());
  private readonly queuePanel = viewChild.required(ActivityQueuePanelComponent);

  protected readonly mainJobOptions = [...EVENT_MAIN_JOB_OPTIONS];
  protected readonly subJobOptions = [...EVENT_SUB_JOB_OPTIONS];
  protected readonly jobTypeOptions = [...EVENT_JOB_TYPE_OPTIONS];

  protected readonly lootDrafts: Record<number, ActivityLootInput> = {};
  protected readonly quickJoinDrafts: Record<number, ActivityQuickJoinInput> = {};

  // Tracks which window's tab is currently active per event (key = event id).
  protected readonly activeWindowByEvent = signal<Record<number, number>>({});

  // Live-event collapse state (session-only).
  protected readonly expandedLiveEventIds = signal<Set<number>>(new Set());

  public constructor() {
    const intervalId = window.setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => window.clearInterval(intervalId));
  }

  // ----- Settings / loot helpers (read-only mirrors) -----

  private linkshellSettingsFor(linkshellId: number): ActivityLinkshellSettings | null {
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

  protected canManageLinkshell(linkshellId: number): boolean {
    const membership = (this.activity.overview()?.linkshells ?? []).find(link => link.id === linkshellId);
    const rank = (membership?.rank ?? '').toLowerCase();
    return rank === 'leader' || rank === 'officer';
  }

  // ----- Event lists -----

  protected liveEvents() {
    return (this.activity.overview()?.activeEvents ?? []).filter(event => Boolean(event.commencementStartTime));
  }

  protected queuedEvents() {
    return (this.activity.overview()?.activeEvents ?? []).filter(event => !event.commencementStartTime);
  }

  protected isLiveEventCollapsed(eventId: number): boolean {
    return !this.expandedLiveEventIds().has(eventId);
  }

  protected toggleLiveEventCollapsed(eventId: number): void {
    const next = new Set(this.expandedLiveEventIds());
    if (next.has(eventId)) next.delete(eventId); else next.add(eventId);
    this.expandedLiveEventIds.set(next);
  }

  protected openEditLiveEventForm(event: ActivityEvent): void {
    this.queuePanel().openEditEventForm(event);
  }

  // ----- Participant grouping -----

  protected attendanceParticipants(event: { participants: ActivityEventParticipant[] }): ActivityEventParticipant[] {
    return this.sortCurrentUserFirst(
      event.participants.filter(participant => !participant.isOnBreak && participant.isVerified !== true)
    );
  }

  protected activeRoomParticipants(event: { participants: ActivityEventParticipant[] }): ActivityEventParticipant[] {
    return this.sortCurrentUserFirst(
      event.participants.filter(participant => !participant.isOnBreak && participant.isVerified === true)
    );
  }

  protected onBreakParticipants(event: { participants: ActivityEventParticipant[] }): ActivityEventParticipant[] {
    return this.sortCurrentUserFirst(
      event.participants.filter(participant => !!participant.isOnBreak)
    );
  }

  private sortCurrentUserFirst(participants: ActivityEventParticipant[]): ActivityEventParticipant[] {
    const currentUserId = this.activity.overview()?.appUser?.id ?? null;
    if (!currentUserId) return participants;
    return [...participants].sort((a, b) => {
      const aIsMe = a.appUserId === currentUserId ? 0 : 1;
      const bIsMe = b.appUserId === currentUserId ? 0 : 1;
      return aIsMe - bIsMe;
    });
  }

  // ----- Attendance windows -----

  protected hasAttendanceWindows(event: { windowCount?: number; attendanceWindows?: ActivityAttendanceWindow[] }): boolean {
    return (event.windowCount ?? 1) > 1 && (event.attendanceWindows?.length ?? 0) > 0;
  }

  protected attendanceWindowsFor(event: { attendanceWindows?: ActivityAttendanceWindow[] }): ActivityAttendanceWindow[] {
    return event.attendanceWindows ?? [];
  }

  protected attendanceWindowLabel(window: ActivityAttendanceWindow): string {
    return window.label && window.label.trim().length > 0 ? window.label : `Window ${window.sequenceNumber}`;
  }

  protected activeAttendanceWindow(event: { id: number; attendanceWindows?: ActivityAttendanceWindow[] }): ActivityAttendanceWindow | null {
    const windows = this.attendanceWindowsFor(event);
    if (windows.length === 0) return null;
    const desiredSeq = this.activeWindowByEvent()[event.id];
    if (desiredSeq != null) {
      const match = windows.find(w => w.sequenceNumber === desiredSeq);
      if (match) return match;
    }
    return windows[windows.length - 1];
  }

  protected isActiveAttendanceWindow(event: { id: number; attendanceWindows?: ActivityAttendanceWindow[] }, window: ActivityAttendanceWindow): boolean {
    return this.activeAttendanceWindow(event)?.id === window.id;
  }

  protected setActiveAttendanceWindow(eventId: number, sequenceNumber: number): void {
    this.activeWindowByEvent.update(map => ({ ...map, [eventId]: sequenceNumber }));
  }

  // Two-stage inline confirmation for removing a verified attendee.
  // window.confirm() is suppressed in the Discord Activity iframe (no
  // `allow-modals`), so a first click flags the attendee and the
  // template swaps the Remove button out for a Confirm/Keep pair.
  // Second click on Confirm calls the API.
  protected readonly pendingRemoveAttendeeId = signal<number | null>(null);

  protected requestRemoveAttendee(attendeeId: number): void {
    this.pendingRemoveAttendeeId.set(attendeeId);
  }

  protected abortRemoveAttendee(): void {
    this.pendingRemoveAttendeeId.set(null);
  }

  protected async confirmRemoveAttendee(attendeeId: number): Promise<void> {
    this.pendingRemoveAttendeeId.set(null);
    const ok = await this.activity.removeAttendanceWindowAttendee(attendeeId);
    if (ok) {
      await this.activity.refreshOverview();
    }
  }

  // ----- Live participant timers / progress -----

  protected liveEventElapsedMs(event: { commencementStartTime?: string | null; startTime?: string | null }): number {
    return this.elapsedMs(event.commencementStartTime || event.startTime);
  }

  protected liveEventTimerLabel(event: { commencementStartTime?: string | null; startTime?: string | null }): string {
    return formatElapsed(this.liveEventElapsedMs(event));
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
    return formatElapsed(this.participantElapsedMs(participant, event));
  }

  protected participantCurrentDkp(
    participant: { startTime?: string | null; resumeTime?: string | null; duration?: number | null; isOnBreak?: boolean | null },
    event: { commencementStartTime?: string | null; startTime?: string | null; dkpPerHour?: number | null }
  ): string {
    return formatDkp(this.participantElapsedMs(participant, event), event.dkpPerHour);
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
    const info = breakSessionInfo(participant, entry.id);
    if (!info) {
      return 'Break Session';
    }
    return `Break Session #${info.sessionNumber} · ${formatBreakDuration(info.durationMs)}`;
  }

  // ----- Loot + quick-join drafts -----

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

  private elapsedMs(startValue?: string | null): number {
    const startTime = parseDate(startValue);
    if (!startTime) {
      return 0;
    }

    return Math.max(0, this.now() - startTime);
  }
}
