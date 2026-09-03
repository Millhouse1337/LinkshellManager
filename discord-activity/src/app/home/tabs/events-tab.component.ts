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
import {
  ActivityClaimShieldCapture,
  ActivityEvent,
  ActivityLinkedSnapshot,
  ActivityPartySetupSlot,
  ActivityWindowEvent,
} from '../../discord/discord-activity.types';
import { ActivityQueuePanelComponent } from '../activity-queue-panel.component';
import { EventHistoryPanelComponent } from '../sidebar-panels/event-history-panel.component';
import { AttendanceSectionsComponent } from './attendance-sections.component';
import { PartySetupPanelComponent } from './party-setup-panel.component';
import { TodFormComponent } from './tod-form.component';
import { PartySetupService } from '../../discord/party-setup.service';
import { WindowEventService } from '../../discord/window-event.service';
import {
  EVENT_JOB_TYPE_OPTIONS,
  EVENT_MAIN_JOB_OPTIONS,
  EVENT_SUB_JOB_OPTIONS
} from '../event-job-options';
import {
  breakSessionInfo,
  canManageLinkshellIn,
  formatBreakDuration,
  formatDkp,
  formatElapsed,
  parseDate
} from '../activity-home.helpers';

// One entry in the Attendance Windows strip. `window` is null for a window the camp REACHED but
// never posted a roster for — those still get a tab, because the gap is the thing an officer needs
// to see. See attendanceWindowTabs().
interface AttendanceWindowTab {
  sequenceNumber: number;
  label: string;
  window: ActivityAttendanceWindow | null;
  attendeeCount: number;
}

@Component({
  selector: 'app-events-tab',
  imports: [CommonModule, FormsModule, ActivityQueuePanelComponent, PartySetupPanelComponent, EventHistoryPanelComponent, TodFormComponent, AttendanceSectionsComponent],
  templateUrl: './events-tab.component.html',
  styleUrl: './events-tab.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class EventsTabComponent {
  private static readonly MAX_ADD_MEMBER_RESULTS = 10;

  protected readonly activity = inject(DiscordActivityService);
  protected readonly partySetups = inject(PartySetupService);
  protected readonly windows = inject(WindowEventService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly now = signal(Date.now());
  private readonly queuePanel = viewChild.required(ActivityQueuePanelComponent);
  private readonly todForm = viewChild(TodFormComponent);

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

    // Attendance snapshots are NOT on the overview payload, so this tab has to fetch them itself.
    // The fetch lives here rather than in AttendanceSectionsComponent because that component is
    // mounted twice (live + archive slices) and would otherwise fire two requests. The service
    // dedupes against AutoRefreshService's own tick.
    effect(() => {
      const linkshellId = this.primaryLinkshellId();
      if (!linkshellId) return;
      queueMicrotask(() => void this.windows.ensureLoaded(linkshellId));
    });
  }

  // ----- Attendance sections (merged in from the old Attendance System tab) -----

  private primaryLinkshellId(): number {
    const overview = this.activity.overview();
    return overview?.primaryLinkshell?.id ?? overview?.appUser?.primaryLinkshellId ?? 0;
  }

  // Every OPEN attendance event is an unposted payout awaiting an officer, so all of them belong in
  // Current Field Activity — not just the ones traceable to a camp. Filtering on a camp link would
  // in fact show almost nothing: a camp-handoff row is only written at End Camp, by which point the
  // camp has already dropped out of liveEvents(), and the rows that DO arrive mid-camp (`/lsm now`)
  // carry no event link at all. Newest capture first, so a card still collecting snapshots sits
  // above one from three days ago.
  protected liveAttendanceEvents(): ActivityWindowEvent[] {
    return [...(this.windows.data()?.openEvents ?? [])].sort(
      (a, b) => new Date(b.lastCapturedAtUtc).getTime() - new Date(a.lastCapturedAtUtc).getTime()
    );
  }

  // The "no live events running" card must not sit above a list of attendance cards, and must not
  // flash before the first window-events fetch settles. A failed fetch still leaves data() null but
  // clears busy(), so the empty state does eventually appear rather than hanging on a spinner.
  protected showNoLiveActivity(): boolean {
    if (this.liveEvents().length > 0) return false;
    if (this.windows.data() === null && this.windows.busy()) return false;
    return this.liveAttendanceEvents().length === 0;
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

  // What the number beside each participant actually means. On a linkshell that has split its DKP
  // into pools, it's their spendable balance in the pool THIS event's type draws from — which is
  // the figure the officer needs when typing a loot cost, and is usually not their grand total.
  protected biddableTitle(event: ActivityEvent): string {
    const pool = event.dkpPoolName;
    return pool
      ? `Spendable ${pool} DKP now = their ${pool} balance − bids they're winning from it − DKP spent on loot this event`
      : "Biddable DKP now = balance − bids they're winning − DKP spent on loot this event";
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
    return canManageLinkshellIn(this.activity.overview(), linkshellId);
  }

  // ----- Event lists -----

  // HNM camps ARE shown here now (a started camp used to fall into a dead zone visible in no
  // section). isHnmEvent still switches the live card between the generic layout and the camp
  // variant (window N of M + next-window countdown + End Camp).
  protected isHnmEvent(event: { type?: string | null }): boolean {
    return (event.type ?? '').trim().toUpperCase() === 'HNM';
  }

  // Canonical "does the Break Room apply?" test — the ONLY thing the template branches on for
  // break / withdraw UI. Comes straight from the server (Services/EventBreakPolicy) so the app
  // can never show a control the endpoints would reject with a 400.
  //
  // A windowed (HNM) camp credits attendance per posted window, so there is no timer to pause and
  // the whole Lobby / Active Room / Break Room concept is meaningless for it. The local fallback
  // mirrors the server predicate (windowed OR HNM-typed) and only runs against a server that
  // predates the flag.
  protected supportsBreakRoom(event: ActivityEvent): boolean {
    return event.supportsBreakRoom ?? ((event.windowCount ?? 1) <= 1 && !this.isHnmEvent(event));
  }

  // Members left in a break state on a windowed camp — only possible from an older build, before
  // break-creating actions were refused there. Officers get a one-click clear; the strip erases
  // itself once the last one is resolved. This is the ONLY break-related control allowed to render
  // on a windowed event, and it is undo-only (it can never put someone back on break).
  protected stuckBreakParticipants(event: ActivityEvent): ActivityEventParticipant[] {
    if (this.supportsBreakRoom(event)) {
      return [];
    }
    return (event.participants ?? []).filter(participant => participant.isOnBreak);
  }

  // ----- Live HNM camp helpers -----

  // "Window N of M" for a live camp. Uses hnmFocusWindow (what the Discord board shows), NOT
  // hnmWindowNumber — the raw counter reads one lower and made the app disagree with the board.
  //
  // A 2-post camp reads "Open" / "Close" instead: those two are roster snapshots, not a numbered
  // sequence, and calling them Window 1 and Window 2 reads like a count of the kings'/dragons'
  // seven SPAWN windows, which they aren't. Mirrors HnmConfig.GetDefaultWindowLabel server-side
  // and constants.window_label in the addon — all three must agree or one camp gets three names.
  protected campWindowLabel(event: ActivityEvent): string {
    const focus = event.hnmFocusWindow ?? event.hnmWindowNumber ?? 1;
    if (event.windowCount === 2) {
      // Once the Close is in, the camp is done posting whatever the cadence counter says.
      // Without this a Close-only camp read "Open" — naming a window it will never have and
      // can no longer accept — because focus tracks the clock, not what was posted.
      return this.campClosePosted(event) || focus > 1 ? 'Close' : 'Open';
    }
    return `Window ${focus} of ${event.windowCount}`;
  }

  // How many roster reads this camp takes — 2 on a Standard king/dragon (Open + Close), against
  // the 7 SPAWN windows windowCount reports. Everything that NAMES or COUNTS a posted window goes
  // by this; only the "Window N of M" camp heading goes by windowCount.
  //
  // Falls back to windowCount for a payload from a server predating the split, which is exactly
  // what the code here read before the two were separated.
  protected postCount(event: ActivityEvent): number {
    return event.attendancePostCount ?? event.windowCount ?? 1;
  }

  // A two-post camp whose Close has landed. Both the addon and the server refuse further
  // windows at that point, so the card must stop implying one is outstanding.
  protected campClosePosted(event: ActivityEvent): boolean {
    return this.postCount(event) === 2
        && this.attendanceWindowsFor(event).some(w => w.sequenceNumber === 2);
  }

  // Manual Check In camp. Credit comes from Check In / Check Out window range only — the Manual
  // Check In roster never reads the posted window snapshots, so on these camps the Attendance
  // Windows card is a record, not the payout source.
  protected isWdCamp(event: ActivityEvent): boolean {
    return (event.attendanceMode ?? '').trim().toLowerCase() === 'wd';
  }

  // Countdown to the next window opening (mm:ss via the 1s `now` signal), or null when the camp
  // isn't on a timed cadence, is on its final window, or has already popped.
  protected campNextWindowCountdown(event: ActivityEvent): string | null {
    if (event.wdFinalizedAt) {
      return null;
    }
    const nextAt = parseDate(event.nextWindowAt);
    if (nextAt == null) {
      return null;
    }
    const remaining = nextAt - this.now();
    return remaining <= 0 ? 'due now' : formatElapsed(remaining);
  }

  // The line under the window label. THREE states, where the template used to render two:
  // anything without a countdown fell through to "final window", including a camp that has
  // no window schedule at all — so a run that had just started, with nothing posted yet,
  // announced itself as being on its last window.
  //
  // A camp on a real cadence only runs out of nextWindowAt on its final window. So a null
  // countdown BELOW the last window means there is no cadence to count, not that the camp
  // is finishing — and for those the useful thing to say is whether the current window's
  // snapshot has landed, since that's the only thing anyone is waiting on.
  protected campWindowStatus(event: ActivityEvent): string {
    // Checked BEFORE the countdown on purpose. A closed two-post camp can still be sitting on
    // a cadence that has a next window scheduled, and "next window 4:12" on a camp that
    // accepts no more posts is the same lie in a different sentence.
    if (this.campClosePosted(event)) {
      return 'posted';
    }
    const countdown = this.campNextWindowCountdown(event);
    if (countdown) {
      return `next window ${countdown}`;
    }
    const focus = event.hnmFocusWindow ?? event.hnmWindowNumber ?? 1;
    if (focus >= (event.windowCount ?? 1)) {
      return 'final window';
    }
    return this.attendanceWindowsFor(event).some(w => w.sequenceNumber === focus)
      ? 'posted'
      : 'not posted yet';
  }

  // Two-click confirm for deleting a live HNM camp outright (no ToD). The Discord iframe blocks the
  // native confirm(), so we surface an inline Confirm/Keep like the withdraw flow. Delete discards the
  // board entirely — no ToD, no history, no DKP — for cleaning up test/mistaken camps.
  protected readonly pendingDeleteEventId = signal<number | null>(null);
  protected requestDeleteCamp(eventId: number): void { this.pendingDeleteEventId.set(eventId); }
  protected abortDeleteCamp(): void { this.pendingDeleteEventId.set(null); }
  protected async confirmDeleteCamp(eventId: number): Promise<void> {
    this.pendingDeleteEventId.set(null);
    await this.activity.deleteEvent(eventId);
  }

  // Open the shared board ToD form in "End Camp" mode (pop window pre-filled to the current window).
  protected openEndCamp(event: ActivityEvent): void {
    const monster = event.assignedMonsterName ?? event.partySetupAssignedMonsterName ?? null;
    if (!monster) {
      this.activity.actionError.set('This HNM camp has no monster assigned, so it can’t be ended here.');
      return;
    }
    this.todForm()?.openForBoard(
      event.linkshellId, monster, event.id, event.dayNumber ?? null, event.hnmWindowNumber ?? 1,
      event.repeatOnTod ?? false, event.repeatLeadHours ?? null);
  }

  // An HNM camp that has popped is no longer "running": a Standard defeat un-commences the board
  // (so it's excluded by the commencement check), and a Manual Check In camp is stamped finalized
  // at End Camp. This keeps popped/defeated boards out of the live section while they wait in the
  // queue for re-post — their roster is meanwhile pending review in the attendance cards on this tab.
  private isEndedCamp(event: ActivityEvent): boolean {
    return this.isHnmEvent(event) && (Boolean(event.hnmAwaitingRepost) || Boolean(event.wdFinalizedAt));
  }

  protected liveEvents() {
    return (this.activity.overview()?.activeEvents ?? [])
      .filter(event => Boolean(event.commencementStartTime))
      .filter(event => !this.isEndedCamp(event));
  }

  // Mirrors the embedded queue panel's own filter so the "N queued" chip matches the rendered list.
  // A Standard defeated board is un-commenced (so it lands here for Edit ToD); a running/awaiting
  // camp keeps its commencement and stays in the live section.
  protected queuedEvents() {
    return (this.activity.overview()?.activeEvents ?? [])
      .filter(event => !event.commencementStartTime);
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

    const biddableByUser = this.rosterBiddableByUser(event.linkshellId);

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
        // Surface biddable for a board signup too (looked up from the roster by
        // account) so the Active Room shows it for everyone — not only members who
        // already have a materialized participation row carrying the value.
        biddableDkp: slot.signedUpAppUserId ? biddableByUser.get(slot.signedUpAppUserId) : undefined,
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

  // Roster biddable DKP keyed by app-user id, for enriching board-signup
  // participants. Biddable is computed for the primary linkshell only, so it's
  // returned empty for an event in another linkshell (rather than a wrong figure).
  private rosterBiddableByUser(linkshellId: number): Map<string, number> {
    const map = new Map<string, number>();
    const primary = this.activity.overview()?.primaryLinkshell;
    if (!primary || primary.id !== linkshellId) {
      return map;
    }
    for (const member of primary.members ?? []) {
      if (member.appUserId && member.biddableDkp != null) {
        map.set(member.appUserId, member.biddableDkp);
      }
    }
    return map;
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

  protected hasAttendanceWindows(event: ActivityEvent): boolean {
    return (event.windowCount ?? 1) > 1 && this.attendanceWindowTabs(event).length > 0;
  }

  protected attendanceWindowsFor(event: { attendanceWindows?: ActivityAttendanceWindow[] }): ActivityAttendanceWindow[] {
    return event.attendanceWindows ?? [];
  }

  // How many of the CAMP's windows have been posted — the number the card's "N of M" counts
  // against the post count. Kill rosters are excluded: they are not one of the camp's roster reads
  // and counting them made a king camp read "3 of 2" the moment Post Kill was pressed.
  protected postedWindowCount(event: ActivityEvent): number {
    return this.attendanceWindowsFor(event).filter(window => !window.isKillWindow).length;
  }

  protected hasKillWindow(event: ActivityEvent): boolean {
    return this.attendanceWindowsFor(event).some(window => window.isKillWindow);
  }

  protected attendanceWindowLabel(window: ActivityAttendanceWindow): string {
    return window.label && window.label.trim().length > 0 ? window.label : `Window ${window.sequenceNumber}`;
  }

  // Every window the camp has SAT THROUGH, not only the ones someone was around to post.
  //
  // The card used to render the posted rows and nothing else, so a camp where only window 6 landed
  // showed a single tab and read as a camp that had run one window. Windows 1–5 happened; nobody
  // recorded them. That gap is the thing an officer needs to see — it's what tells them to go back
  // and file one — and hiding it made a half-covered camp look complete.
  //
  // The bound is hnmWindowNumber (the server's OPENED counter, clamped to the spawn count), not the
  // post high-water mark. And these monsters show within seconds of a boundary, so a window that has
  // been reached is a window whose chance is already spent — every one of them is a past window with
  // a definite answer, which is exactly what makes it worth a tab.
  //
  // Numbered camps only. A 2-post king/dragon names its windows Open / Close while its counter walks
  // the seven SPAWN windows underneath, so synthesizing 1..opened there would invent five windows
  // that camp can never have — the same phantom-tab trap the addon's strip is written around.
  protected attendanceWindowTabs(event: ActivityEvent): AttendanceWindowTab[] {
    const posted = new Map(this.attendanceWindowsFor(event).map(w => [w.sequenceNumber, w]));
    const sequences = new Set<number>(posted.keys());

    if (this.postCount(event) > 2) {
      const reached = Math.min(
        Math.max(1, event.hnmWindowNumber ?? 1),
        Math.max(1, event.windowCount ?? 1));
      for (let seq = 1; seq <= reached; seq++) {
        sequences.add(seq);
      }
    }

    return [...sequences].sort((a, b) => a - b).map(seq => {
      const window = posted.get(seq) ?? null;
      return {
        sequenceNumber: seq,
        label: window ? this.attendanceWindowLabel(window) : `Window ${seq}`,
        window,
        attendeeCount: window?.attendees.length ?? 0,
      };
    });
  }

  // ----- Attendance windows: what the window pays -----
  //
  // "How much DKP did this window give?" has two different answers, because the two attendance
  // modes pay off different evidence:
  //
  //   Standard — the snapshots ARE the payroll. HnmStandardCampFinalizer pays an open bonus for
  //              being scanned in window 1 and a close bonus for being scanned in the close
  //              window; every window in between is worth nothing on its own.
  //   Manual Check In — the snapshots are a record only. WdCampFinalizer pays a flat per-window
  //              rate across each member's Check In..Check Out range, whether or not the addon
  //              happened to scan them that window.
  //
  // Claim and kill bonuses are excluded from both, matching wdDkpSoFar: neither is decided until
  // End Camp, so folding them in would show a number that changes for a reason nothing on screen
  // explains. The footer under the table says so.

  // The window a Standard camp closes out on: the one an officer TICKED.
  //
  // MIRROR of HnmStandardCampFinalizer.ResolveCloseWindow, fallback included. It used to be
  // "the highest sequence posted", which is what made every window in turn look like the close
  // while it was the newest one — and since the addon writes the server's quote back as the
  // window's explicit price, that moving guess got frozen into every window on the camp. Camps
  // where nobody ticked the box still fall back to the old derivation so they keep paying a close;
  // kill rosters are excluded from it, being filed after the close by design.
  private closeWindowSequence(event: ActivityEvent): number {
    const windows = this.attendanceWindowsFor(event).filter(window => !window.isKillWindow);
    const marked = windows.find(window => window.isClosingWindow);
    if (marked) return marked.sequenceNumber;
    return windows.length === 0 ? 0 : Math.max(...windows.map(window => window.sequenceNumber));
  }

  // What ONE window pays each attendee: the officer's explicit price when they set one, else what
  // the camp's model pays for that sequence. One place, so the cell, the note and the total cannot
  // disagree with each other.
  //
  // MIRROR of HnmStandardCampFinalizer.WindowValue — the same rule in two languages, including the
  // precedence: an explicit amount REPLACES the open / close bonus, it does not add to it. A
  // control labelled "DKP this window" that showed 5 and paid 5.5 would be lying about its name.
  // The two must move together.
  private windowValue(event: ActivityEvent, window: ActivityAttendanceWindow): number {
    if (window.dkpAmount != null) return Math.max(0, window.dkpAmount);
    // A kill roster is worth 0 as a window — being in it earns the kill bonus instead, which is
    // decided at End Camp and so is excluded here like every other outcome bonus.
    if (window.isKillWindow) return 0;
    // One amount per window, no exception. A window that is both the open and the close pays the
    // OPEN — the camp that opened and closed in one roster read is the officer's to settle by hand,
    // because the close falls back to "the latest window posted" and so the opening post of EVERY
    // camp is briefly both ends of it. Open is tested first for that reason: sequence 1 is the open
    // by definition. Same precedence as HnmStandardCampFinalizer.WindowValue.
    const closeWindow = this.closeWindowSequence(event);
    const isOpen = window.sequenceNumber === 1;
    const isClose = closeWindow > 0 && window.sequenceNumber === closeWindow;
    if (isOpen) return this.standardBonus(event, 'open');
    if (isClose) return this.standardBonus(event, 'close');
    return this.standardBonus(event, 'window');
  }

  // Per-camp override first, else the linkshell default — the precedence both finalizers apply.
  // 'window' shares Event.hnmPerWindowOverride with the Manual Check In rate: one column per
  // amount, and the camp's mode picks which linkshell setting it falls back to.
  private standardBonus(
    event: ActivityEvent,
    which: 'window' | 'open' | 'close' | 'claim' | 'kill',
  ): number {
    const settings = this.linkshellSettingsFor(event.linkshellId);
    let value: number | null | undefined;
    switch (which) {
      case 'window': value = event.hnmPerWindowOverride ?? settings?.hnmStandardWindowBonus; break;
      case 'open': value = event.hnmOpenBonusOverride ?? settings?.hnmStandardOpenBonus; break;
      case 'close': value = event.hnmCloseBonusOverride ?? settings?.hnmStandardCloseBonus; break;
      // The two OUTCOME bonuses. Read UNGATED here, mirroring HnmCampPricing.OutcomeBonuses: they
      // are being displayed on a live camp, whose outcome is not known yet, so applying the
      // claimed/killed gate would print 0 until the mob is dead -- which is the whole reason a
      // configured kill bonus appeared nowhere on this card.
      case 'claim': value = event.hnmClaimBonusOverride ?? settings?.hnmStandardClaimBonus; break;
      case 'kill': value = event.hnmKillBonusOverride ?? settings?.hnmStandardKillBonus; break;
    }
    return Math.max(0, value ?? 0);
  }

  // The two outcome bonuses as display strings, for the notes under the roster. Public because
  // the template names the amounts now -- saying a bonus exists without saying what it is worth
  // is what sent an officer to the linkshell settings to check a number this card already had.
  protected claimBonusLabel(event: ActivityEvent): string {
    return EventsTabComponent.trimDkp(this.standardBonus(event, 'claim'));
  }

  protected killBonusLabel(event: ActivityEvent): string {
    return EventsTabComponent.trimDkp(this.standardBonus(event, 'kill'));
  }

  // What one attendee earned from THIS window, or null when the window carries no credit for
  // them. Null renders as an em dash rather than 0 — "this window pays nothing here" and "this
  // window is priced at zero" read identically otherwise, and only one of them is a settings
  // problem an officer can fix. An explicitly-priced window is never null: pricing a window is
  // what makes it pay everyone on it, including a middle window that used to pay nobody.
  protected attendanceWindowDkp(
    event: ActivityEvent,
    window: ActivityAttendanceWindow,
    attendee: { characterName?: string | null },
  ): number | null {
    if (this.isWdCamp(event)) {
      // Credit follows the check-in range, not the scan. Someone the addon caught at camp who
      // never checked in is paid nothing for the window they're standing in — which is exactly
      // the discrepancy this card exists to make visible.
      const participant = (event.participants ?? []).find(p =>
        (p.characterName ?? '').trim().toLowerCase() === (attendee.characterName ?? '').trim().toLowerCase()
        && (attendee.characterName ?? '').trim().length > 0);
      if (!participant) return null;

      const arrival = Math.max(1, participant.wdArrivalWindow ?? 1);
      const departure = participant.wdDepartureWindow ?? Number.MAX_SAFE_INTEGER;
      const seq = window.sequenceNumber;
      return seq >= arrival && seq <= departure ? this.wdPerWindowRate(event) : null;
    }

    // An officer priced this window by hand, so it pays everyone scanned in it — that IS what
    // pricing a window means, and it's the one case where a middle window is worth something.
    if (window.dkpAmount != null) return this.windowValue(event, window);

    // A kill roster pays nothing AS A WINDOW — the kill bonus is what pays it, and that isn't
    // decided until End Camp. Dash rather than 0, same as any other window that carries no credit.
    if (window.isKillWindow) return null;

    // Standard: window 1 pays the open bonus, the ticked closing window pays the close bonus, and
    // everything else pays the camp's regular window rate. One amount per window — they don't add.
    //
    // A middle window on a camp with NO per-window rate still returns null so it renders as an em
    // dash rather than a 0 — "this window pays nobody" and "this window is priced at zero" read
    // identically otherwise, and only one of them is a settings problem an officer can fix.
    const closeWindow = this.closeWindowSequence(event);
    const isOpen = window.sequenceNumber === 1;
    const isClose = closeWindow > 0 && window.sequenceNumber === closeWindow;
    if (!isOpen && !isClose && this.standardBonus(event, 'window') <= 0) return null;
    return this.windowValue(event, window);
  }

  // Display form of the above. A separate helper because 0 is FALSY and 0 is a real answer here:
  // `@if (attendanceWindowDkp(...); as dkp)` in the template would quietly show "priced at zero"
  // as "pays nothing", which is the one distinction the null return exists to preserve.
  protected attendanceWindowDkpLabel(
    event: ActivityEvent,
    window: ActivityAttendanceWindow,
    attendee: { characterName?: string | null },
  ): string {
    const dkp = this.attendanceWindowDkp(event, window, attendee);
    return dkp == null ? '—' : EventsTabComponent.trimDkp(dkp);
  }

  // Two decimals at most, trailing zeros dropped — what `| number:'1.0-2'` renders, for the
  // places (the note below) that build a sentence in TS instead of going through the pipe.
  private static trimDkp(value: number): string {
    return Number(value.toFixed(2)).toString();
  }

  // Sum of the column, so the window reads as a single payout instead of a list of rows to add up.
  protected attendanceWindowDkpTotal(event: ActivityEvent, window: ActivityAttendanceWindow): number {
    return window.attendees.reduce((sum, attendee) => sum + (this.attendanceWindowDkp(event, window, attendee) ?? 0), 0);
  }

  // One line of "why is the column that number" under the table. Standard names the gates the
  // window pays on; Manual Check In names the rate. A middle window says outright that it pays
  // nobody, so a column of dashes reads as the rule rather than as missing data.
  protected attendanceWindowDkpNote(event: ActivityEvent, window: ActivityAttendanceWindow): string {
    // Said first and said plainly, because a priced window ignores every rule the sentences below
    // describe — reading "0 open + 0 close" under a window paying 5 would be worse than silence.
    if (window.dkpAmount != null) {
      return `${EventsTabComponent.trimDkp(window.dkpAmount)} DKP per attendee `
        + `(set for this window — replaces the open / close bonus)`;
    }

    if (this.isWdCamp(event)) {
      const rate = EventsTabComponent.trimDkp(this.wdPerWindowRate(event));
      return `${rate} DKP per window, credited from Check In to Check Out`;
    }

    if (window.isKillWindow) {
      // Names the AMOUNT. "earns the kill bonus" was true and useless: the figure is configured on
      // the linkshell, resolved server-side, and was printed on no surface at all -- so a camp with
      // a kill bonus set looked identical to one without.
      const kill = this.standardBonus(event, 'kill');
      return kill > 0
        ? `Post Kill roster — pays no window credit; being on it earns the `
          + `${EventsTabComponent.trimDkp(kill)} DKP kill bonus at End Camp`
        : 'Post Kill roster — pays no window credit, and this camp has no kill bonus configured';
    }

    // Named for what makes it that amount. Never a sum: one window pays one amount, so there is no
    // "+" to print. A window that both opened and closed the camp is named as the OPEN, and the
    // close on it is left to the officer — see windowValue.
    const closeWindow = this.closeWindowSequence(event);
    const isOpen = window.sequenceNumber === 1;
    const isClose = closeWindow > 0 && window.sequenceNumber === closeWindow;
    // Said out loud when it applies, because it MOVES: nobody ticked a closing window, so the close
    // falls back to "the latest window posted" and stops being true the moment the next one lands.
    const closeNote = this.attendanceWindowsFor(event).some(w => w.isClosingWindow)
      ? 'closing window'
      : 'no closing window marked — falling back to the latest window posted';

    // Said plainly when the camp opened and closed in one window, because the close bonus is
    // visibly configured and visibly not being paid here. It is the officer's to award by hand.
    if (isOpen && isClose) {
      return `${EventsTabComponent.trimDkp(this.standardBonus(event, 'open'))} DKP per attendee `
        + `(open) — this window is also the close, and a window only ever pays one amount; `
        + `award the close by hand if the camp earned it`;
    }
    if (isOpen) {
      return `${EventsTabComponent.trimDkp(this.standardBonus(event, 'open'))} DKP per attendee (open)`;
    }
    if (isClose) {
      return `${EventsTabComponent.trimDkp(this.standardBonus(event, 'close'))} DKP per attendee `
        + `(${closeNote})`;
    }
    const windowRate = this.standardBonus(event, 'window');
    return windowRate > 0
      ? `${EventsTabComponent.trimDkp(windowRate)} DKP per attendee (regular window)`
      : 'Middle windows carry no bonus — only the open and the close pay';
  }

  // ----- Manual Check In: late arrivals -----

  // Anyone who checked in AFTER the camp opened. Window 1 arrivals were there from the start,
  // so they aren't "late" and would bury the handful of rows this panel exists to show.
  // Sorted by arrival so the most recent joiner is at the bottom, next to the running total.
  protected wdLateArrivals(event: ActivityEvent): ActivityEventParticipant[] {
    if (!this.isWdCamp(event)) return [];
    return (event.participants ?? [])
      .filter(p => (p.wdArrivalWindow ?? 0) > 1)
      .sort((a, b) => (a.wdArrivalWindow ?? 0) - (b.wdArrivalWindow ?? 0));
  }

  // How many checked in at the open — shown as a count so the late list reads as a subset
  // rather than as the whole roster.
  protected wdOnTimeCount(event: ActivityEvent): number {
    if (!this.isWdCamp(event)) return 0;
    return (event.participants ?? []).filter(p => p.wdArrivalWindow === 1).length;
  }

  // The window credit is measured against RIGHT NOW: the one that has already opened.
  //
  // hnmWindowNumber, NOT hnmFocusWindow — End Camp treats the opened window as the pop window,
  // so this is the same number the payout will actually be computed from. Using the awaited
  // window would promise everyone one window more DKP than they are going to get.
  private wdCurrentWindow(event: ActivityEvent): number {
    return Math.max(1, event.hnmWindowNumber ?? 1);
  }

  // Windows this member is credited for so far: arrival..min(departure, current), inclusive.
  // Mirrors WdCampFinalizer.WindowsCredited, which is the authority at payout.
  protected wdWindowsCredited(event: ActivityEvent, participant: ActivityEventParticipant): number {
    const arrival = Math.max(1, participant.wdArrivalWindow ?? 1);
    const last = Math.min(participant.wdDepartureWindow ?? this.wdCurrentWindow(event), this.wdCurrentWindow(event));
    return arrival > last ? 0 : last - arrival + 1;
  }

  // Per-window rate: the camp's own override first, else the linkshell default — the same
  // precedence WdCampFinalizer applies (Event.HnmPerWindowOverride ?? Linkshell.WdDkpPerWindow).
  protected wdPerWindowRate(event: ActivityEvent): number {
    if (event.hnmPerWindowOverride != null) return event.hnmPerWindowOverride;
    return this.linkshellSettingsFor(event.linkshellId)?.wdDkpPerWindow ?? 0.25;
  }

  // What this member has earned SO FAR — rate × windows credited.
  //
  // The open / close / claim / kill bonuses are deliberately NOT included. Claim and kill aren't
  // known until End Camp; close depends on the camp's final window, which is still moving; and the
  // open would be the only one of the four that could appear here, which reads as an arbitrary
  // half-answer. Folding any of them in would show a number that changes for a reason nothing on
  // screen explains. The footer says they're still to come.
  protected wdDkpSoFar(event: ActivityEvent, participant: ActivityEventParticipant): number {
    return this.wdWindowsCredited(event, participant) * this.wdPerWindowRate(event);
  }

  // "Open" / "Close" / "Window 3" for an arrival, named the way the window tabs are so the
  // two read as one timeline. Falls back to the plain number when no window has been posted
  // under that sequence yet — a check-in can run ahead of the roster post.
  protected wdArrivalLabel(event: ActivityEvent, participant: ActivityEventParticipant): string {
    const seq = participant.wdArrivalWindow ?? 1;
    const window = this.attendanceWindowsFor(event).find(w => w.sequenceNumber === seq);
    return window ? this.attendanceWindowLabel(window) : `Window ${seq}`;
  }

  // Snapshots an officer attached to this camp from the Event System's unlinked list.
  // Read-only here — the link is presentational and the roster that pays out is still
  // edited on the attendance event.
  protected linkedSnapshotsFor(event: { linkedSnapshots?: ActivityLinkedSnapshot[] }): ActivityLinkedSnapshot[] {
    return event.linkedSnapshots ?? [];
  }

  // ----- Claim shield -----

  protected claimShieldsFor(event: { claimShieldCaptures?: ActivityClaimShieldCapture[] }): ActivityClaimShieldCapture[] {
    return event.claimShieldCaptures ?? [];
  }

  // Which posted window the lottery landed in, named the same way the tabs
  // above are ("Open" / "Close" / "Window 3") so the two read as one timeline.
  // The server picked the window; this only borrows its label.
  protected claimWindowLabel(
    event: { attendanceWindows?: ActivityAttendanceWindow[] },
    capture: ActivityClaimShieldCapture,
  ): string {
    const seq = capture.nearestWindowSequence;
    if (seq == null) return '';
    const window = this.attendanceWindowsFor(event).find(w => w.sequenceNumber === seq);
    return window ? this.attendanceWindowLabel(window) : `Window ${seq}`;
  }

  // Which tab the pane is showing. Defaults to the newest window that actually HOLDS a roster
  // rather than to the last tab: now that unposted windows get tabs too, opening the card on the
  // trailing one would greet an officer with "no roster was posted" on most live camps, hiding the
  // snapshot they came to look at. Only a camp with nothing posted at all falls back to the last.
  protected activeAttendanceWindowTab(event: ActivityEvent): AttendanceWindowTab | null {
    const tabs = this.attendanceWindowTabs(event);
    if (tabs.length === 0) return null;
    const desiredSeq = this.activeWindowByEvent()[event.id];
    if (desiredSeq != null) {
      const match = tabs.find(t => t.sequenceNumber === desiredSeq);
      if (match) return match;
    }
    const withRoster = tabs.filter(t => t.window != null);
    return withRoster.length > 0 ? withRoster[withRoster.length - 1] : tabs[tabs.length - 1];
  }

  protected activeAttendanceWindow(event: ActivityEvent): ActivityAttendanceWindow | null {
    return this.activeAttendanceWindowTab(event)?.window ?? null;
  }

  // Compared on SEQUENCE, not on window id: an unposted tab has no id to compare.
  protected isActiveAttendanceWindow(event: ActivityEvent, tab: AttendanceWindowTab): boolean {
    return this.activeAttendanceWindowTab(event)?.sequenceNumber === tab.sequenceNumber;
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

  // ----- Pricing one window by hand -----
  //
  // No Apply button and no Use default button: whatever is in the box IS this window's DKP. There
  // is nothing to "confirm" because nothing here is final — the amounts stay editable through
  // review, and the event being ended and posted to the sheet is what settles them.
  //
  // Commits on CHANGE, not on input. `change` on a number field fires on blur or Enter, which is
  // the difference between one write of 1.25 and three writes of 1, 12 and 125 on the way there.
  //
  // An empty box is the instruction the removed "Use default" button used to carry: no price of
  // its own, fall back to the camp's open / close / regular-window value.
  //
  // Drafts are keyed by window id and only exist for windows the officer has actually typed in —
  // an absent entry means "show what the server holds", so a refresh elsewhere isn't fought by a
  // stale draft.
  private readonly windowDkpDrafts = signal<Record<number, string>>({});
  protected readonly windowDkpSaving = signal(false);

  // The box is never blank. An empty field used to be how "this window has no price of its own"
  // showed, leaning on a `default` placeholder to say it — which left the one control on the card
  // that names a DKP amount as the only thing on the card not showing one, on exactly the windows
  // an officer opens it to read. So it always holds what this window actually pays: the officer's
  // explicit price when they set one, else what the camp's model pays for this sequence. It goes
  // through the same windowValue as the DKP column and the note beneath it, so the three cannot
  // disagree about what the window is worth.
  //
  // Showing the default does NOT write it — see commitWindowDkp. A window nobody priced stays
  // unpriced, so it keeps following the camp's open / close / window bonus when that changes.
  protected windowDkpDraft(event: ActivityEvent, window: ActivityAttendanceWindow): string {
    const draft = this.windowDkpDrafts()[window.id];
    if (draft !== undefined) return draft;
    // A kill roster shows the CAMP'S KILL BONUS, not windowValue -- which returns 0 for one, by
    // design, because the roster pays through that bonus rather than as a window. The box read
    // "0" on every camp with a kill bonus configured, which is the one number it could not have
    // meant. See killDkpLabel / commitWindowDkp for the write side.
    if (window.isKillWindow) {
      return EventsTabComponent.trimDkp(this.standardBonus(event, 'kill'));
    }
    return EventsTabComponent.trimDkp(this.windowValue(event, window));
  }

  // The Kill tab's box is not "DKP this window" -- it edits the camp's kill bonus, and naming it
  // for the window would promise a per-window payment that does not exist.
  protected windowDkpLabel(window: ActivityAttendanceWindow): string {
    return window.isKillWindow ? 'Kill DKP' : 'DKP this window';
  }

  // Hand-set and inherited now look identical, both being a number in the same box, so the tooltip
  // is what tells them apart — and it names the way back to the default, which an empty box used
  // to be the visible sign of.
  protected windowDkpHint(window: ActivityAttendanceWindow): string {
    if (window.isKillWindow) {
      return 'What everyone on the kill roster earns at End Camp. This is the camp\'s kill bonus, '
        + 'not a window price — clear the box to go back to the linkshell default.';
    }
    return window.dkpAmount == null
      ? 'Camp default for this window — type an amount to price it by hand.'
      : 'Priced by hand — clear the box to go back to the camp default.';
  }

  protected setWindowDkpDraft(window: ActivityAttendanceWindow, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.windowDkpDrafts.update(map => ({ ...map, [window.id]: value }));
  }

  protected async commitWindowDkp(event: ActivityEvent, window: ActivityAttendanceWindow): Promise<void> {
    const raw = this.windowDkpDraft(event, window).trim();
    const amount = raw === '' ? null : Number(raw);

    // The kill roster's box writes the CAMP'S kill bonus, not this window's price. Split off
    // before any of the window-specific comparisons below, which all read window.dkpAmount -- a
    // column a kill window never carries and the server refuses to set.
    if (window.isKillWindow) {
      if (amount !== null && (!Number.isFinite(amount) || amount < 0)) {
        this.dropWindowDkpDraft(window);
        return;
      }
      if (this.windowDkpSaving()) return;
      this.windowDkpSaving.set(true);
      try {
        if (await this.activity.setCampKillBonus(event.id, amount)) {
          this.dropWindowDkpDraft(window);
          await this.activity.refreshOverview();
        }
      } finally {
        this.windowDkpSaving.set(false);
      }
      return;
    }

    // Garbage in the box drops the draft rather than writing it, so the field snaps back to what
    // the server holds instead of sitting there looking saved. With no Apply button there is no
    // other moment at which a bad value would be rejected.
    if (amount !== null && (!Number.isFinite(amount) || amount < 0)) {
      this.dropWindowDkpDraft(window);
      return;
    }

    // Nothing actually changed — re-blurring an untouched field must not fire a write.
    const current = window.dkpAmount ?? null;
    if (amount === null ? current === null : current !== null && Math.abs(current - amount) < 0.0001) {
      this.dropWindowDkpDraft(window);
      return;
    }

    // The box shows the camp's own number on a window nobody has priced, so tabbing through the
    // field now arrives here as "write that number down explicitly". Don't: an explicit amount
    // FREEZES the window at it, and a later change to the camp's open / close / window bonus would
    // then stop reaching a window the officer never actually priced. Typing the number the default
    // already shows means the same thing as leaving it alone, so it is treated the same way.
    if (current === null && amount !== null
        && Math.abs(this.windowValue(event, window) - amount) < 0.0001) {
      this.dropWindowDkpDraft(window);
      return;
    }

    await this.writeWindowDkp(window, amount);
  }

  // Puts the field back on the server's value by forgetting what was typed.
  private dropWindowDkpDraft(window: ActivityAttendanceWindow): void {
    this.windowDkpDrafts.update(map => {
      const next = { ...map };
      delete next[window.id];
      return next;
    });
  }

  private async writeWindowDkp(window: ActivityAttendanceWindow, amount: number | null): Promise<void> {
    if (this.windowDkpSaving()) return;
    this.windowDkpSaving.set(true);
    try {
      const ok = await this.activity.setAttendanceWindowDkp(window.id, amount);
      if (ok) {
        // Drop the draft so the field re-reads the server's value — which may differ from what was
        // typed, since the server snaps to the linkshell's rounding grid on write.
        this.dropWindowDkpDraft(window);
        await this.activity.refreshOverview();
      }
    } finally {
      this.windowDkpSaving.set(false);
    }
  }

  // ----- Marking the closing window -----
  //
  // Shares windowDkpSaving with the price control on purpose: the two write the same row, and the
  // server clears DkpAmount when the box is ticked, so letting them fly concurrently would leave
  // the price field showing a value the server has just dropped.

  // A kill roster is filed after the close and pays through the kill bonus, so it can never BE the
  // close — the server refuses it too, and this hides the box rather than offering a click that
  // comes back as an error.
  protected canMarkClosingWindow(event: ActivityEvent, window: ActivityAttendanceWindow): boolean {
    return !this.isWdCamp(event) && !window.isKillWindow;
  }

  protected async toggleClosingWindow(window: ActivityAttendanceWindow): Promise<void> {
    if (this.windowDkpSaving()) return;
    this.windowDkpSaving.set(true);
    try {
      const ok = await this.activity.setAttendanceWindowClosing(window.id, !window.isClosingWindow);
      if (ok) {
        // Drop any draft price for this row: ticking the box clears the server's DkpAmount, and a
        // surviving draft would re-show the number the officer just superseded.
        this.windowDkpDrafts.update(map => {
          const next = { ...map };
          delete next[window.id];
          return next;
        });
        // Full refresh rather than a local flag flip — marking one window UNMARKS another, and the
        // whole DKP column repaints when the close moves.
        await this.activity.refreshOverview();
      }
    } finally {
      this.windowDkpSaving.set(false);
    }
  }

  // ----- Claim Shield: who the claim bonus goes to -----
  //
  // The tag list PAYS now. HnmStandardCampFinalizer and WdCampFinalizer both read
  // ClaimShieldCaptureMembers to decide who earns the claim bonus, replacing the old rule of
  // "everyone scanned in the close window". So this stopped being an audit curiosity and became
  // part of the payout, which is what the wording under the list says.

  // Everyone this camp will pay a claim bonus to, de-duplicated across captures. A contested pop
  // produces several lotteries and a member can tag in more than one; the bonus is paid ONCE, so a
  // count that added them up per capture would overstate what the camp owes.
  //
  // Matched only — an unmatched name resolved to no membership, so there is no balance to credit.
  // Those rows stay VISIBLE in the list (see the template) because an unmatched name is usually a
  // roster problem an officer can fix, and hiding it would hide the fix.
  protected claimShieldPaidNames(event: ActivityEvent): string[] {
    const seen = new Set<string>();
    for (const capture of this.claimShieldsFor(event)) {
      for (const member of capture.members ?? []) {
        if (!member.matched) continue;
        const name = (member.characterName ?? '').trim();
        if (name) seen.add(name);
      }
    }
    return [...seen].sort((a, b) => a.localeCompare(b));
  }

  // What one tagger earns, resolved the same way every other bonus on this card is: the camp's own
  // override first, else the linkshell default for the camp's mode.
  protected claimBonusAmount(event: ActivityEvent): number {
    const settings = this.linkshellSettingsFor(event.linkshellId);
    const value = event.hnmClaimBonusOverride
      ?? (this.isWdCamp(event) ? settings?.wdClaimBonus : settings?.hnmStandardClaimBonus);
    return Math.max(0, value ?? 0);
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

  // True for a board-only (Discord channel / outside) signup that has no real
  // AppUserEvent — `channelSignupParticipants` synthesizes these with a negative
  // id (`-slot.slotId`). They can't be moderated (verify / break / undo) because
  // those endpoints look up a participation row by id, so we hide those actions
  // for them instead of letting the call fail with "participant was not found".
  protected isChannelSignupOnly(participant: { id: number }): boolean {
    return participant.id < 0;
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
