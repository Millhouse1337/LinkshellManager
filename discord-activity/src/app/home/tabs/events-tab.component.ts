import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityAddEventMemberInput,
  ActivityAttendanceWindow,
  ActivityEventAddMemberCandidate,
  ActivityEventParticipant,
  ActivityLinkshellSettings,
  ActivityLootInput,
  ActivityLootStructure,
  ActivityQuickJoinInput,
  ActivityStatusLedgerEntry,
  DiscordActivityService
} from '../../discord/discord-activity.service';
import { ActivityEvent, ActivityPartySetupSlot } from '../../discord/discord-activity.types';
import { ActivityQueuePanelComponent } from '../activity-queue-panel.component';
import { EventHistoryPanelComponent } from '../sidebar-panels/event-history-panel.component';
import { PartySetupPanelComponent } from './party-setup-panel.component';
import { PartySetupService } from '../../discord/party-setup.service';
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
  imports: [CommonModule, FormsModule, ActivityQueuePanelComponent, PartySetupPanelComponent, EventHistoryPanelComponent],
  templateUrl: './events-tab.component.html',
  styleUrl: './events-tab.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class EventsTabComponent {
  private static readonly MAX_ADD_MEMBER_RESULTS = 10;

  protected readonly activity = inject(DiscordActivityService);
  protected readonly partySetups = inject(PartySetupService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());
  private readonly queuePanel = viewChild.required(ActivityQueuePanelComponent);

  protected readonly mainJobOptions = [...EVENT_MAIN_JOB_OPTIONS];
  protected readonly subJobOptions = [...EVENT_SUB_JOB_OPTIONS];
  protected readonly jobTypeOptions = [...EVENT_JOB_TYPE_OPTIONS];

  protected readonly addMemberDrafts: Record<number, ActivityAddEventMemberInput> = {};
  protected readonly addMemberSearchDrafts: Record<number, string> = {};
  protected readonly addMemberCandidatesByEvent = signal<Record<number, ActivityEventAddMemberCandidate[]>>({});
  protected readonly expandedAddMemberEventIds = signal<Set<number>>(new Set());
  protected readonly loadingAddMemberEventIds = signal<Set<number>>(new Set());
  protected readonly openAddMemberTypeaheadEventId = signal<number | null>(null);
  protected readonly lootDrafts: Record<number, ActivityLootInput> = {};
  protected readonly quickJoinDrafts: Record<number, ActivityQuickJoinInput> = {};

  // Tracks which window's tab is currently active per event (key = event id).
  protected readonly activeWindowByEvent = signal<Record<number, number>>({});

  // Live-event collapse state (session-only).
  protected readonly expandedLiveEventIds = signal<Set<number>>(new Set());

  // Live-event "View Party Setup" inline read-only panel: per-event toggle so
  // leaders can recap the planned roster mid-run without leaving the page.
  protected readonly expandedLiveEventPartySetupIds = signal<Set<number>>(new Set());

  protected toggleLiveEventPartySetupExpanded(eventId: number, partySetupId: number | null | undefined, linkshellId: number): void {
    const next = new Set(this.expandedLiveEventPartySetupIds());
    if (next.has(eventId)) {
      next.delete(eventId);
    } else {
      next.add(eventId);
      // The embedded panel reads role/main/sub options from the linkshell's
      // party-setup list cache. Read-only mode doesn't render the dropdowns,
      // but loading the list here is cheap and keeps parity with the queue
      // panel toggle behavior.
      if (linkshellId) void this.partySetups.loadList(linkshellId);
    }
    this.expandedLiveEventPartySetupIds.set(next);
  }

  protected isLiveEventPartySetupExpanded(eventId: number): boolean {
    return this.expandedLiveEventPartySetupIds().has(eventId);
  }

  public constructor() {
    const intervalId = window.setInterval(() => this.now.set(Date.now()), 1000);
    this.destroyRef.onDestroy(() => window.clearInterval(intervalId));

    effect(() => {
      const livePartyBoardEventIds = this.liveEvents()
        .filter(event => !!event.partySetupId)
        .map(event => event.id);

      for (const eventId of livePartyBoardEventIds) {
        void this.partySetups.loadEventBoard(eventId);
      }
    });
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

  // True HNM events (EventType="HNM") live in the dedicated HNM tab, so they're
  // dropped from the generic Events tab to mirror the server-side filter.
  private isHnmEvent(event: { type?: string | null }): boolean {
    return (event.type ?? '').trim().toUpperCase() === 'HNM';
  }

  protected liveEvents() {
    return (this.activity.overview()?.activeEvents ?? [])
      .filter(event => Boolean(event.commencementStartTime))
      .filter(event => !this.isHnmEvent(event));
  }

  protected queuedEvents() {
    return (this.activity.overview()?.activeEvents ?? [])
      .filter(event => !event.commencementStartTime)
      .filter(event => !this.isHnmEvent(event));
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

  // Whether the current member is attending this event (in a slot or the roster),
  // so the self-service "Withdraw From Event" button only shows when there's
  // something to withdraw from.
  protected isParticipant(event: ActivityEvent): boolean {
    const myId = this.activity.overview()?.appUser?.id;
    if (!myId) return false;
    return (event.participants ?? []).some(participant => participant.appUserId === myId);
  }

  // "Withdraw From Event" no longer deletes a live participant — the backend parks
  // them in the Break Room (timer paused; DKP / attendance / event history all kept)
  // so a return resumes exactly where they left off. Pre-start it still cancels the
  // signup. window.confirm() is suppressed in the Discord iframe, so we use the same
  // two-stage inline confirm as Remove Attendee.
  protected readonly pendingWithdrawEventId = signal<number | null>(null);

  protected requestWithdraw(eventId: number): void {
    this.pendingWithdrawEventId.set(eventId);
  }

  protected abortWithdraw(): void {
    this.pendingWithdrawEventId.set(null);
  }

  protected async withdrawFromEvent(event: ActivityEvent): Promise<void> {
    this.pendingWithdrawEventId.set(null);
    await this.activity.unsignFromEvent(event.id);
  }

  protected isAddMemberExpanded(eventId: number): boolean {
    return this.expandedAddMemberEventIds().has(eventId);
  }

  protected addMemberCandidates(eventId: number): ActivityEventAddMemberCandidate[] {
    const candidates = this.addMemberCandidatesByEvent()[eventId] ?? [];
    // Drop anyone already in the event (including the current user) so they
    // can't be "added" a second time and don't clutter the search.
    const event = (this.activity.overview()?.activeEvents ?? []).find(item => item.id === eventId);
    const taken = new Set(
      (event?.participants ?? [])
        .map(participant => participant.appUserId)
        .filter((id): id is string => !!id && id.trim().length > 0)
    );
    return taken.size === 0
      ? candidates
      : candidates.filter(candidate => !taken.has(candidate.appUserId));
  }

  protected addMemberLoading(eventId: number): boolean {
    return this.loadingAddMemberEventIds().has(eventId);
  }

  protected addMemberSearchDraft(eventId: number): string {
    return this.addMemberSearchDrafts[eventId] ?? '';
  }

  protected setAddMemberSearchDraft(eventId: number, value: string): void {
    this.addMemberSearchDrafts[eventId] = value;
    this.getAddMemberDraft(eventId).appUserId = '';
    this.openAddMemberTypeaheadEventId.set(eventId);
  }

  protected openAddMemberTypeahead(eventId: number): void {
    this.openAddMemberTypeaheadEventId.set(eventId);
  }

  protected closeAddMemberTypeahead(): void {
    this.openAddMemberTypeaheadEventId.set(null);
  }

  protected filteredAddMemberCandidates(eventId: number): ActivityEventAddMemberCandidate[] {
    const query = this.addMemberSearchDraft(eventId).trim().toLowerCase();
    const matches = query
      ? this.addMemberCandidates(eventId).filter(candidate => this.addMemberCandidateLabel(candidate).toLowerCase().includes(query))
      : this.addMemberCandidates(eventId);

    return matches.slice(0, EventsTabComponent.MAX_ADD_MEMBER_RESULTS);
  }

  protected chooseAddMemberCandidate(eventId: number, candidate: ActivityEventAddMemberCandidate): void {
    this.addMemberSearchDrafts[eventId] = this.addMemberCandidateLabel(candidate);
    this.getAddMemberDraft(eventId).appUserId = candidate.appUserId;
    this.closeAddMemberTypeahead();
  }

  protected getAddMemberDraft(eventId: number): ActivityAddEventMemberInput {
    this.addMemberDrafts[eventId] ??= {
      appUserId: '',
      jobName: '',
      subJobName: '',
      jobType: ''
    };

    return this.addMemberDrafts[eventId];
  }

  protected async toggleAddMember(eventId: number): Promise<void> {
    const next = new Set(this.expandedAddMemberEventIds());
    if (next.has(eventId)) {
      next.delete(eventId);
      this.expandedAddMemberEventIds.set(next);
      if (this.openAddMemberTypeaheadEventId() === eventId) {
        this.closeAddMemberTypeahead();
      }
      return;
    }

    next.add(eventId);
    this.expandedAddMemberEventIds.set(next);

    if (this.addMemberCandidates(eventId).length === 0) {
      await this.loadAddMemberCandidates(eventId);
    }
  }

  private async loadAddMemberCandidates(eventId: number): Promise<void> {
    this.loadingAddMemberEventIds.update(current => new Set(current).add(eventId));
    try {
      const candidates = await this.activity.loadAddMemberCandidates(eventId);
      this.addMemberCandidatesByEvent.update(current => ({ ...current, [eventId]: candidates }));
    } finally {
      this.loadingAddMemberEventIds.update(current => {
        const next = new Set(current);
        next.delete(eventId);
        return next;
      });
    }
  }

  protected addMemberCandidateLabel(candidate: ActivityEventAddMemberCandidate): string {
    return candidate.rank ? `${candidate.characterName} · ${candidate.rank}` : candidate.characterName;
  }

  // ----- Participant grouping -----

  protected attendanceParticipants(event: { participants: ActivityEventParticipant[] }): ActivityEventParticipant[] {
    return this.sortCurrentUserFirst(
      event.participants.filter(participant => !participant.isOnBreak && participant.isVerified !== true)
    );
  }

  // Members still awaiting an officer's confirmation (isVerified == null). The
  // event can't be ended until every one is confirmed present or removed.
  protected pendingAttendanceCount(event: { participants: ActivityEventParticipant[] }): number {
    return event.participants.filter(participant => !participant.isOnBreak && participant.isVerified == null).length;
  }

  protected activeRoomParticipants(event: { participants: ActivityEventParticipant[] }): ActivityEventParticipant[] {
    return this.sortCurrentUserFirst(
      this.mergeChannelSignupParticipants(event as ActivityEvent).filter(participant => !participant.isOnBreak && participant.isVerified === true)
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

  protected lootEligibleParticipants(event: ActivityEvent): ActivityEventParticipant[] {
    return this.sortCurrentUserFirst(this.mergeChannelSignupParticipants(event));
  }

  private mergeChannelSignupParticipants(event: ActivityEvent): ActivityEventParticipant[] {
    const merged = [...event.participants];
    const seen = new Set(merged.map(participant => this.participantKey(participant)).filter((key): key is string => !!key));

    for (const signup of this.channelSignupParticipants(event)) {
      const key = this.participantKey(signup);
      if (key && seen.has(key)) {
        continue;
      }

      if (key) {
        seen.add(key);
      }

      merged.push(signup);
    }

    return merged;
  }

  private channelSignupParticipants(event: ActivityEvent): ActivityEventParticipant[] {
    if (!event.partySetupId) {
      return [];
    }

    return this.eventBoardSlots(event.id)
      .filter(slot => !!slot.signedUpAppUserId || !!slot.signedUpCharacterName)
      .map(slot => ({
        id: -slot.slotId,
        appUserId: slot.signedUpAppUserId ?? null,
        characterName: slot.signedUpCharacterName ?? null,
        jobName: slot.signedUpMainJob ?? slot.mainJob ?? null,
        subJobName: slot.signedUpSubJob ?? slot.subJob ?? null,
        jobType: slot.signedUpRole ?? slot.role ?? null,
        isQuickJoin: false,
        isVerified: true,
        proctor: 'Discord channel signup',
        startTime: event.commencementStartTime ?? event.startTime ?? null,
        resumeTime: null,
        pauseTime: null,
        isOnBreak: false,
        duration: 0,
        eventDkp: 0,
        statusLedger: []
      } satisfies ActivityEventParticipant));
  }

  private eventBoardSlots(eventId: number): ActivityPartySetupSlot[] {
    const board = this.partySetups.eventBoardFor(eventId);
    if (!board) {
      return [];
    }

    return board.alliances.flatMap(alliance => alliance.parties.flatMap(party => party.slots));
  }

  private participantKey(participant: Pick<ActivityEventParticipant, 'appUserId' | 'characterName'>): string | null {
    if (participant.appUserId && participant.appUserId.trim().length > 0) {
      return `user:${participant.appUserId.trim()}`;
    }

    if (participant.characterName && participant.characterName.trim().length > 0) {
      return `name:${participant.characterName.trim().toLowerCase()}`;
    }

    return null;
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

  protected async submitAddMember(eventId: number): Promise<void> {
    const draft = this.getAddMemberDraft(eventId);
    if (!draft.appUserId || !draft.jobName || !draft.subJobName || !draft.jobType) {
      this.activity.actionError.set('Member, role, main job, and sub job are required.');
      this.activity.actionMessage.set(null);
      return;
    }

    try {
      await this.activity.addMemberToLiveEvent(eventId, draft);
      this.addMemberDrafts[eventId] = {
        appUserId: '',
        jobName: '',
        subJobName: '',
        jobType: ''
      };
      this.addMemberSearchDrafts[eventId] = '';
      this.closeAddMemberTypeahead();
      await this.loadAddMemberCandidates(eventId);
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
