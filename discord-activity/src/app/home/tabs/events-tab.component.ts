import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, model, signal, viewChild } from '@angular/core';
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
  ActivityCampShare,
  ActivityClaimShieldCapture,
  ActivityEvent,
  ActivityLinkedSnapshot,
  ActivityPartySetupSlot,
  ActivityTodEntry,
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
  canManageEventsIn,
  canManageLinkshellIn,
  formatBreakDuration,
  formatDkp,
  formatElapsed,
  mergedMonsterName,
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

// One stop on a live HNM camp's attendance strip: a window (posted, or reached with no roster), a
// Claim Shield lottery, a linked snapshot, the kill roster, or the windows still to come. All but
// the last open in the detail panel under the strip. See campSteps().
interface CampStep {
  key: string;
  kind: 'window' | 'missed' | 'kill' | 'claim' | 'snapshot' | 'upcoming';
  title: string;
  time: string | null;
  sub: string;
  badge: string | null;
  tone: 'neutral' | 'accent' | 'success' | 'danger' | 'warning' | 'kill' | 'snapshot';
  window: ActivityAttendanceWindow | null;
  capture: ActivityClaimShieldCapture | null;
  snapshot: ActivityLinkedSnapshot | null;
}

// One slice of the "Payout at end camp" panel.
interface CampPayoutPart {
  label: string;
  value: number;
  tone: 'windows' | 'kill' | 'misc' | 'tag';
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

  // The header's Current / Pending / Past switch. Current is what is running now (live camps and
  // open attendance events); Pending is officer to-do work (the queue, captures to triage, events
  // awaiting a DKP post, member submissions to review); Past is settled history. The queue shows in
  // both Current and Pending as ONE instance outside the view wrappers, because Current's Edit Live
  // Event opens the queue panel's own <dialog>, which can't open while that panel is hidden.
  // The template HIDES the other views rather than unmounting them, so a half-typed draft or
  // search survives a switch, and the queue panel (a required view child) is always there to find.
  // Two-way `view` binding: ActivityHomeComponent keeps it in the location (/events/pending).
  readonly eventsView = model<'current' | 'pending' | 'past'>('current', { alias: 'view' });

  // Shown on the Pending view's button so nobody misses it: the ended events still waiting for their
  // DKP to be posted, which is all that view holds. Only while the list isn't narrowed by a search,
  // since the total then counts the matches rather than everything waiting.
  protected readonly pendingCount = computed(() => {
    const data = this.windows.data();
    return data && !data.closedQuery ? data.closedTotalCount : 0;
  });

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

  // End Camp specifically, which the server gates on the Manage events PERMISSION
  // rather than the Leader/Officer rank — see canManageEventsIn(). Kept separate
  // from canManageLinkshell so the two never quietly drift back together.
  protected canEndCamp(linkshellId: number): boolean {
    return canManageEventsIn(this.activity.overview(), linkshellId);
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
    // An Open/Close camp with at most two spawn windows has no "Window N of M" worth printing —
    // that includes a custom NM with no window grid at all, which would otherwise read "of 1".
    // A king/dragon keeps its "Window N of 7" heading, matching the Discord board.
    if (this.postsOpenAndClose(event) && (event.windowCount ?? 1) <= 2) {
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

  // Whether the camp's posts are a named Open and Close. The server decides it from the camp's
  // Window Method; a payload from a server predating the field falls back to the old "two posts"
  // test, which is what this was before Window N could have exactly two windows.
  protected postsOpenAndClose(event: ActivityEvent): boolean {
    return event.postsOpenAndClose ?? this.postCount(event) === 2;
  }

  // An Open/Close camp whose Close has landed. Both the addon and the server refuse further
  // windows at that point, so the card must stop implying one is outstanding.
  protected campClosePosted(event: ActivityEvent): boolean {
    return this.postsOpenAndClose(event)
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
  // Every state here says ROSTER, because that is the only thing this line has ever been about:
  // whether the attendance snapshot for the window named directly above it has landed. It used to
  // read a bare "posted", sitting in a large slot under "WINDOW 4 OF 7" on a card whose main
  // button says "End Camp/Post ToD" -- so it was read as the ToD having been posted, which it
  // never means. The ToD is filed at End Camp and this camp is still live.
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
      return 'close roster posted';
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
      ? 'roster posted'
      : 'roster not posted';
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

  // End Camp on a camp with no closing window ticked asks first, because nobody would get the close
  // bonus. Inline Go back / End anyway rather than confirm(), which the Discord iframe blocks. (The
  // addon refuses outright; here the officer may know better, e.g. a camp that never got going.)
  protected readonly pendingEndCampWarningEventId = signal<number | null>(null);
  protected abortEndCamp(): void { this.pendingEndCampWarningEventId.set(null); }

  // Whether ending this camp now would pay nobody the close bonus for want of a tick. Numbered
  // Standard camps only, with at least one window posted and a close bonus to lose: a named camp's
  // Close post is its close without a tick, and the addon applies the same test before End Camp.
  protected missingClosingWindow(event: ActivityEvent): boolean {
    if (this.isWdCamp(event) || this.postsOpenAndClose(event)) return false;
    if (this.standardBonus(event, 'close') <= 0) return false;
    const windows = this.attendanceWindowsFor(event).filter(window => !window.isKillWindow);
    return windows.length > 0 && !windows.some(window => window.isClosingWindow);
  }

  // Open the shared board ToD form in "End Camp" mode (pop window pre-filled to the current window).
  protected openEndCamp(event: ActivityEvent): void {
    if (this.missingClosingWindow(event) && this.pendingEndCampWarningEventId() !== event.id) {
      this.pendingEndCampWarningEventId.set(event.id);
      return;
    }
    this.pendingEndCampWarningEventId.set(null);
    const monster = event.assignedMonsterName ?? event.partySetupAssignedMonsterName ?? null;
    if (!monster) {
      this.activity.actionError.set('This HNM camp has no monster assigned, so it can’t be ended here.');
      return;
    }
    this.todForm()?.openForBoard(
      event.linkshellId, monster, event.id, event.dayNumber ?? null, event.hnmWindowNumber ?? 1,
      event.repeatOnTod ?? false, event.repeatLeadHours ?? null, this.postedTodFor(event, monster));
  }

  // The ToD already logged for this camp's kill -- normally by the in-game ToD Tracker -- so End
  // Camp opens on it instead of asking for the time of death a second time.
  //
  // The SAME rule HnmCampPopService uses to adopt that row, and it has to be: if the form showed
  // one ToD and the server adopted another, the officer would confirm a time that was not the one
  // being written. Same linkshell, same spawn (merge-pair aware), not the ToD this board was
  // CREATED from (SourceTodId -- on a live camp that is the previous kill), and stamped since the
  // camp went live. Newest first.
  //
  // recentTods only covers the primary linkshell, so a camp elsewhere finds nothing here and opens
  // blank. The server keeps the posted time on a blank submit, so that case is still safe.
  private postedTodFor(event: ActivityEvent, monster: string): ActivityTodEntry | null {
    const liveSince = parseDate(event.commencementStartTime);
    if (liveSince == null) {
      return null;
    }
    const spawn = mergedMonsterName(monster).toLowerCase();
    const candidates = (this.activity.overview()?.recentTods ?? []).filter(tod =>
      tod.linkshellId === event.linkshellId
      && tod.id !== event.sourceTodId
      && mergedMonsterName(tod.monsterName).toLowerCase() === spawn
      && (parseDate(tod.timeStamp) ?? 0) >= liveSince);
    candidates.sort((a, b) =>
      ((parseDate(b.timeStamp) ?? 0) - (parseDate(a.timeStamp) ?? 0)) || (b.id - a.id));
    return candidates[0] ?? null;
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

  // The header's "Create Event": managing any linkshell, the same rule the queue panel used for
  // the button it used to carry (worked out here because the header draws before the panel does).
  protected canCreateEvent(): boolean {
    return (this.activity.overview()?.linkshells ?? []).some(link => this.canManageLinkshell(link.id));
  }

  protected openCreateEventForm(): void {
    this.queuePanel().openCreateForm();
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

  // ----- Add New Post -----
  //
  // Replaces "Add a Member" on a live Standard HNM camp, where signing someone up pays nothing:
  // camp DKP comes from its posts. An officer picks a Window post (credited into one of the camp's
  // windows — a new one, or added to one already posted) or a Misc post (paid the Misc rate once per
  // person), then the members on it. One panel open at a time.
  protected readonly newPostEventId = signal<number | null>(null);
  protected readonly newPostKind = signal<'window' | 'misc'>('window');
  protected readonly newPostWindow = signal(1);
  protected readonly newPostName = signal('');
  protected readonly newPostSearch = signal('');
  protected readonly newPostSelected = signal<ReadonlySet<string>>(new Set());
  protected readonly newPostSaving = signal(false);

  protected offersNewPost(event: ActivityEvent): boolean {
    return this.isHnmEvent(event) && !this.isWdCamp(event) && !!event.commencementStartTime;
  }

  protected async toggleNewPost(event: ActivityEvent): Promise<void> {
    if (this.newPostEventId() === event.id) {
      this.closeNewPost();
      return;
    }
    this.newPostEventId.set(event.id);
    this.newPostKind.set('window');
    this.newPostName.set('');
    this.newPostSearch.set('');
    this.newPostSelected.set(new Set());
    // The first window not posted yet, else the last one (adding to it).
    const options = this.newPostWindowOptions(event);
    this.newPostWindow.set((options.find(option => !option.posted) ?? options[options.length - 1])?.sequence ?? 1);
    if (!this.addMemberCandidatesByEvent()[event.id]) {
      await this.loadAddMemberCandidates(event.id);
    }
  }

  protected closeNewPost(): void {
    this.newPostEventId.set(null);
  }

  // Every window the camp can take, named like its tabs. A posted one is still offered: posting
  // into it adds the picked members to it.
  protected newPostWindowOptions(event: ActivityEvent): { sequence: number; label: string; posted: boolean }[] {
    const named = this.postsOpenAndClose(event);
    const posted = new Map(this.attendanceWindowsFor(event)
      .filter(window => !window.isKillWindow)
      .map(window => [window.sequenceNumber, window] as const));
    return Array.from({ length: this.postCount(event) }, (_, index) => {
      const sequence = index + 1;
      const existing = posted.get(sequence);
      const name = existing
        ? this.attendanceWindowLabel(existing)
        : named ? (sequence === 1 ? 'Open' : 'Close') : `Window ${sequence}`;
      return { sequence, label: existing ? `${name} (posted, adds to it)` : name, posted: !!existing };
    });
  }

  // The whole linkshell roster, not addMemberCandidates: that one drops anyone already signed up,
  // and being signed up has nothing to do with whether someone is on a post.
  protected newPostCandidates(eventId: number): ActivityEventAddMemberCandidate[] {
    const query = this.newPostSearch().trim().toLowerCase();
    const all = this.addMemberCandidatesByEvent()[eventId] ?? [];
    return query ? all.filter(candidate => candidate.characterName.toLowerCase().includes(query)) : all;
  }

  protected isNewPostSelected(appUserId: string): boolean {
    return this.newPostSelected().has(appUserId);
  }

  protected toggleNewPostMember(appUserId: string): void {
    const next = new Set(this.newPostSelected());
    if (next.has(appUserId)) next.delete(appUserId); else next.add(appUserId);
    this.newPostSelected.set(next);
  }

  protected async submitNewPost(event: ActivityEvent): Promise<void> {
    const ids = [...this.newPostSelected()];
    if (ids.length === 0 || this.newPostSaving()) return;
    this.newPostSaving.set(true);
    try {
      const ok = this.newPostKind() === 'window'
        ? await this.activity.addCampWindowPost(event.id, this.newPostWindow(), ids)
        : await this.activity.addCampMiscPost(event.id, this.newPostName().trim() || null, ids);
      if (ok) {
        this.closeNewPost();
        await this.activity.refreshOverview();
      }
    } finally {
      this.newPostSaving.set(false);
    }
  }

  // ----- Live HNM camp card -----
  //
  // A live HNM camp keeps the classic card on top (name, window status, the action stack), collapsed
  // or not. Opened, its attendance uses its own layout: a strip of everything the camp has recorded
  // (windows, claim, snapshots, kill), one detail panel for the picked step, and a payout + loot
  // column. Timed events keep the generic card throughout.

  // Which strip step the detail panel shows, per camp. Absent = the newest roster (selectedCampStep).
  protected readonly campStepByEvent = signal<Record<number, string>>({});

  // The top section's Add New Post / Add a Member. On an open card they toggle their panel, as they
  // always did. Both panels live in the opened card, so on a collapsed one they open the card as well
  // — and only ever open the panel, never close it out of sight.
  protected async toggleCampPanel(event: ActivityEvent, panel: 'post' | 'member'): Promise<void> {
    const wasCollapsed = this.isLiveEventCollapsed(event.id);
    if (wasCollapsed) this.toggleLiveEventCollapsed(event.id);
    if (panel === 'post') {
      if (wasCollapsed && this.newPostEventId() === event.id) return;
      await this.toggleNewPost(event);
    } else {
      if (wasCollapsed && this.isAddMemberExpanded(event.id)) return;
      await this.toggleAddMember(event.id);
    }
  }

  // Every step on the strip, in reading order: the camp's windows by number (a reached window with
  // no roster stays in as a gap to fill), then the Claim Shield lotteries and linked snapshots by
  // time, then the kill roster, then whatever windows are still to come.
  protected campSteps(event: ActivityEvent): CampStep[] {
    const blank = { window: null, capture: null, snapshot: null };
    const windows: CampStep[] = [];
    const kills: CampStep[] = [];

    for (const tab of this.attendanceWindowTabs(event)) {
      const window = tab.window;
      const key = `w:${tab.sequenceNumber}`;
      if (!window) {
        windows.push({ ...blank, key, kind: 'missed', title: tab.label, time: null,
          sub: 'No roster posted', badge: 'No roster', tone: 'warning' });
        continue;
      }
      const per = this.windowPerAttendee(event, window);
      const sub = EventsTabComponent.countLabel(window.attendees.length, 'member')
        + (per > 0 ? ` · +${EventsTabComponent.trimDkp(per)}` : '');
      if (window.isKillWindow) {
        kills.push({ ...blank, window, key, kind: 'kill', title: 'Kill', time: window.postedAt, sub,
          badge: 'Killed', tone: 'kill' });
        continue;
      }
      const closing = !this.isWdCamp(event) && this.isCloseWindow(event, window);
      windows.push({ ...blank, window, key, kind: 'window', title: tab.label, time: window.postedAt, sub,
        badge: closing ? 'Closing' : 'Posted', tone: closing ? 'accent' : 'neutral' });
    }

    const tag = this.claimBonusAmount(event);
    const claims: CampStep[] = this.claimShieldsFor(event).map(capture => {
      const tagged = capture.members.filter(member => member.matched).length;
      return {
        ...blank, capture, key: `c:${capture.id}`, kind: 'claim', title: 'Claim Shield',
        time: capture.capturedAtUtc,
        sub: `${tagged} tagged` + (tag > 0 ? ` · +${EventsTabComponent.trimDkp(tag)}` : ''),
        badge: capture.won ? 'Won' : 'Lost', tone: capture.won ? 'success' : 'danger',
      };
    });
    const snapshots: CampStep[] = this.linkedSnapshotTabs(event).map(snapshot => ({
      ...blank, snapshot, key: `s:${snapshot.id}`, kind: 'snapshot',
      title: this.linkedSnapshotTabLabel(snapshot), time: snapshot.capturedAtUtc,
      sub: EventsTabComponent.countLabel(snapshot.entries.length, 'member')
        + ((snapshot.dkpPerMember ?? 0) > 0 ? ` · +${EventsTabComponent.trimDkp(snapshot.dkpPerMember ?? 0)}` : ''),
      badge: snapshot.isMisc ? 'Misc' : snapshot.windowLabel?.trim() || 'Snapshot', tone: 'snapshot',
    }));
    const extras = [...claims, ...snapshots]
      .sort((a, b) => (parseDate(a.time) ?? 0) - (parseDate(b.time) ?? 0));

    const upcoming = this.campUpcomingStep(event, kills.length > 0);
    return [...windows, ...extras, ...kills, ...(upcoming ? [upcoming] : [])];
  }

  // The camp's windows not reached yet, as one greyed step: "Windows 4–7" (or "Close" on a named
  // camp). After a kill they are "Not needed"; before it, "Upcoming". Null once every window is in.
  private campUpcomingStep(event: ActivityEvent, killed: boolean): CampStep | null {
    const named = this.postsOpenAndClose(event);
    const reached = named ? 0 : Math.max(0, ...this.attendanceWindowTabs(event)
      .filter(tab => !tab.window?.isKillWindow)
      .map(tab => tab.sequenceNumber));
    const posted = new Set(this.attendanceWindowsFor(event)
      .filter(window => !window.isKillWindow)
      .map(window => window.sequenceNumber));
    const remaining: number[] = [];
    for (let seq = reached + 1; seq <= this.postCount(event); seq++) {
      if (!posted.has(seq)) remaining.push(seq);
    }
    if (remaining.length === 0) return null;

    const name = (seq: number) => named ? (seq === 1 ? 'Open' : 'Close') : `Window ${seq}`;
    const first = remaining[0];
    const last = remaining[remaining.length - 1];
    const title = remaining.length === 1 ? name(first)
      : named ? remaining.map(name).join(' & ') : `Windows ${first}–${last}`;
    return {
      key: 'upcoming', kind: 'upcoming', title, time: null, sub: killed ? 'Not needed' : 'Upcoming',
      badge: null, tone: 'neutral', window: null, capture: null, snapshot: null,
    };
  }

  // The step the detail panel shows: the one picked, else the newest roster (kill roster included),
  // which is what an officer opening the card has usually come to check.
  protected selectedCampStep(event: ActivityEvent): CampStep | null {
    const steps = this.campSteps(event).filter(step => step.kind !== 'upcoming');
    if (steps.length === 0) return null;
    const wanted = this.campStepByEvent()[event.id];
    const match = wanted ? steps.find(step => step.key === wanted) : undefined;
    if (match) return match;
    const rosters = steps.filter(step => step.kind === 'window' || step.kind === 'kill');
    return rosters.length > 0 ? rosters[rosters.length - 1] : steps[steps.length - 1];
  }

  protected selectCampStep(eventId: number, step: CampStep): void {
    if (step.kind === 'upcoming') return;
    this.campStepByEvent.update(map => ({ ...map, [eventId]: step.key }));
  }

  // What one window pays each attendee on it: the window's own amount plus its open and close
  // bonuses (windowValue), the kill bonus on a kill roster, or the flat rate on a Manual Check In
  // camp. The figure on the strip and in the detail panel's formula row.
  protected windowPerAttendee(event: ActivityEvent, window: ActivityAttendanceWindow): number {
    if (this.isWdCamp(event)) return this.wdPerWindowRate(event);
    if (window.isKillWindow) return this.standardBonus(event, 'kill');
    return this.windowValue(event, window);
  }

  // The window's own amount before either bonus — the formula row's first chip. Public twin of
  // baseWindowValue.
  protected windowBaseValue(event: ActivityEvent, window: ActivityAttendanceWindow): number {
    return this.baseWindowValue(event, window);
  }

  // One attendee's credit from this window: the DKP column plus the open and close bonuses. The
  // Subtotal column.
  protected attendeeWindowSubtotal(
    event: ActivityEvent,
    window: ActivityAttendanceWindow,
    attendee: { characterName?: string | null },
  ): number {
    const own = this.attendanceWindowDkp(event, window, attendee) ?? 0;
    if (this.isWdCamp(event) || window.isKillWindow) return own;
    return own + this.openWindowValue(event, window) + this.closeWindowValue(event, window);
  }

  // The kill roster, if one is posted — the newest, when an officer posted it twice.
  protected campKillWindow(event: ActivityEvent): ActivityAttendanceWindow | null {
    const kills = this.attendanceWindowsFor(event).filter(window => window.isKillWindow);
    return kills.length > 0 ? kills[kills.length - 1] : null;
  }

  // The payout panel's Mine / Event toggle, per camp. Absent = Mine: what a member opens the card
  // to check is their own DKP.
  protected readonly campPayoutViewByEvent = signal<Record<number, 'mine' | 'event'>>({});

  protected campPayoutView(event: ActivityEvent): 'mine' | 'event' {
    return this.campPayoutViewByEvent()[event.id] ?? 'mine';
  }

  protected setCampPayoutView(event: ActivityEvent, view: 'mine' | 'event'): void {
    this.campPayoutViewByEvent.update(views => ({ ...views, [event.id]: view }));
  }

  // The figures the panel shows: the member's own share, or the whole camp's. Mine is all zeros
  // when they are owed nothing yet.
  protected campPayoutFigures(event: ActivityEvent): ActivityCampShare | null {
    const payout = event.hnmPayout;
    if (!payout) return null;
    if (this.campPayoutView(event) === 'event') return payout;
    return payout.mine ?? { totalDkp: 0, killDkp: 0, miscDkp: 0, tagDkp: 0 };
  }

  // The payout panel's four slices. Windows is whatever of the total the three awards don't make
  // up, never below zero.
  protected campPayoutParts(event: ActivityEvent): CampPayoutPart[] {
    const figures = this.campPayoutFigures(event);
    if (!figures) return [];
    const windows = Math.max(0, figures.totalDkp - figures.killDkp - figures.miscDkp - figures.tagDkp);
    return [
      { label: 'Windows', value: windows, tone: 'windows' },
      { label: 'Kill', value: figures.killDkp, tone: 'kill' },
      { label: 'Misc', value: figures.miscDkp, tone: 'misc' },
      { label: 'Tag', value: figures.tagDkp, tone: 'tag' },
    ];
  }

  // One slice's share of the bar, as a width percentage.
  protected campPayoutShare(event: ActivityEvent, part: CampPayoutPart): number {
    const total = this.campPayoutFigures(event)?.totalDkp ?? 0;
    return total > 0 ? (part.value / total) * 100 : 0;
  }

  // Members on one lottery who resolved to a roster member — the ones its tag bonus can pay.
  protected claimTaggedCount(capture: ActivityClaimShieldCapture): number {
    return capture.members.filter(member => member.matched).length;
  }

  // "12 members", "1 member".
  protected countLabel(count: number, noun: string): string {
    return EventsTabComponent.countLabel(count, noun);
  }

  private static countLabel(count: number, noun: string): string {
    return `${count} ${noun}${count === 1 ? '' : 's'}`;
  }

  // The DKP the loot list has spent, for the Loot card's heading. Null where loot isn't priced in
  // DKP (Loot Council, or Hybrid's percentages).
  protected lootDkpSpent(event: ActivityEvent): number | null {
    if (this.lootStructureFor(event.linkshellId) !== 'Dkp') return null;
    return event.loot.reduce((sum, loot) => sum + (loot.winningDkpSpent ?? 0), 0);
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

    if (!this.postsOpenAndClose(event) && this.postCount(event) > 1) {
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

  // The window a Standard camp closes out on, or 0 for none: the one an officer TICKED, or on a
  // named Open/Close camp its latest post (pressing Post Close already said which one closes it).
  // A numbered camp nobody ticked has no close and pays no close bonus.
  //
  // MIRROR of HnmStandardCampFinalizer.ResolveCloseWindow. Kill rosters are excluded, being filed
  // after the close by design.
  private closeWindowSequence(event: ActivityEvent): number {
    const windows = this.attendanceWindowsFor(event).filter(window => !window.isKillWindow);
    const marked = windows.find(window => window.isClosingWindow);
    if (marked) return marked.sequenceNumber;
    if (!this.postsOpenAndClose(event) || windows.length === 0) return 0;
    return Math.max(...windows.map(window => window.sequenceNumber));
  }

  // Whether an officer TICKED a closing window, rather than the close falling back to the latest
  // post. Mirrors HnmStandardCampFinalizer.HasMarkedCloseWindow.
  private closeIsMarked(event: ActivityEvent): boolean {
    return this.attendanceWindowsFor(event).some(window => window.isClosingWindow && !window.isKillWindow);
  }

  // What ONE window pays each attendee: its own amount, plus the open bonus on window 1, plus the
  // close bonus on the close. One place, so the cells, the note and the total cannot disagree.
  //
  // MIRROR of HnmStandardCampFinalizer.WindowValue (BaseWindowValue + OpenWindowValue +
  // CloseWindowValue) — the same rule in two languages. The two must move together.
  private windowValue(event: ActivityEvent, window: ActivityAttendanceWindow): number {
    return this.baseWindowValue(event, window)
      + this.openWindowValue(event, window)
      + this.closeWindowValue(event, window);
  }

  // The window's OWN amount, before either bonus — the DKP column: the officer's hand-set price,
  // else the regular rate, on every window including window 1. A kill roster is worth 0 as a
  // window — being in it earns the kill bonus instead, which is decided at End Camp.
  private baseWindowValue(event: ActivityEvent, window: ActivityAttendanceWindow): number {
    if (window.dkpAmount != null) return Math.max(0, window.dkpAmount);
    if (window.isKillWindow) return 0;
    return this.standardBonus(event, 'window');
  }

  // The open bonus this window adds — the Open bonus column: the camp's open bonus on window 1,
  // 0 elsewhere.
  protected openWindowValue(event: ActivityEvent, window: ActivityAttendanceWindow): number {
    return this.isOpenWindow(window) ? this.standardBonus(event, 'open') : 0;
  }

  // Window 1 is the open, whatever else it turns out to be. Never the kill roster.
  protected isOpenWindow(window: ActivityAttendanceWindow): boolean {
    return !window.isKillWindow && window.sequenceNumber === 1;
  }

  // The close bonus this window adds — the Closing window column: the camp's close bonus on the
  // close, 0 elsewhere.
  protected closeWindowValue(event: ActivityEvent, window: ActivityAttendanceWindow): number {
    return this.isCloseWindow(event, window) ? this.standardBonus(event, 'close') : 0;
  }

  // Whether this window is the camp's close. A close that only fell back onto window 1 (a named
  // camp with just its Open posted) does not count — window 1 is the close only when TICKED.
  protected isCloseWindow(event: ActivityEvent, window: ActivityAttendanceWindow): boolean {
    if (window.isKillWindow) return false;
    const closeWindow = this.closeWindowSequence(event);
    if (closeWindow <= 0 || window.sequenceNumber !== closeWindow) return false;
    return window.sequenceNumber !== 1 || this.closeIsMarked(event);
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

  // The "+N DKP" beside the Closing window tick: what ticking a window adds for everyone on it.
  // Null when the camp pays no close bonus, so the tick isn't advertised as earning something.
  protected closeBonusLabel(event: ActivityEvent): string | null {
    const close = this.standardBonus(event, 'close');
    return close > 0 ? EventsTabComponent.trimDkp(close) : null;
  }

  // The DKP column: what one attendee earned from THIS window before either bonus (those are the
  // Open bonus and Closing window columns beside it), or null when the window carries no credit for
  // them. Null
  // renders as an em dash rather than 0 — "this window pays nothing here" and "this window is
  // priced at zero" read identically otherwise, and only one of them is a settings problem an
  // officer can fix. An explicitly-priced window is never null: pricing a window is what makes it
  // pay everyone on it, including a middle window that used to pay nobody.
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
    if (window.dkpAmount != null) return this.baseWindowValue(event, window);

    // The kill roster's column is the CAMP'S KILL BONUS -- the number its DKP cells edit, and what
    // everyone listed here actually earns for being on it.
    //
    // It used to return null and render as a dash, on the reasoning that a kill roster pays
    // nothing AS A WINDOW. True of the window, useless to the officer: the column sat empty on
    // the one roster whose whole purpose is the bonus, directly under a box showing what that
    // bonus is. The amount still lands at End Camp rather than as window credit -- the note under
    // the table says so -- but it is the member's, and the table is where members' amounts go.
    if (window.isKillWindow) return this.standardBonus(event, 'kill');

    // Standard: every window pays the camp's regular window rate (window 1's open bonus is its own
    // column). A camp with NO regular rate returns null, for the em dash above.
    if (this.standardBonus(event, 'window') <= 0) return null;
    return this.baseWindowValue(event, window);
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

  // Sum of the DKP, Open bonus and Closing window columns, so the window reads as a single payout
  // instead of a list of rows to add up.
  protected attendanceWindowDkpTotal(event: ActivityEvent, window: ActivityAttendanceWindow): number {
    const bonuses = this.isWdCamp(event)
      ? 0
      : this.openWindowValue(event, window) + this.closeWindowValue(event, window);
    return window.attendees.reduce(
      (sum, attendee) => sum + (this.attendanceWindowDkp(event, window, attendee) ?? 0) + bonuses, 0);
  }

  // One line of "why is the column that number" under the table. Standard names the gates the
  // window pays on; Manual Check In names the rate. A middle window says outright that it pays
  // nobody, so a column of dashes reads as the rule rather than as missing data.
  protected attendanceWindowDkpNote(event: ActivityEvent, window: ActivityAttendanceWindow): string {
    if (this.isWdCamp(event)) {
      const rate = EventsTabComponent.trimDkp(this.wdPerWindowRate(event));
      return `${rate} DKP per window, credited from Check In to Check Out`;
    }

    if (window.isKillWindow) {
      // No longer says "pays no window credit" -- the column beside it now shows the bonus, and a
      // note contradicting the number it sits under is worse than no note.
      const kill = this.standardBonus(event, 'kill');
      return kill > 0
        ? `Kill roster — ${EventsTabComponent.trimDkp(kill)} DKP each, `
          + `paid as the camp's kill bonus at End Camp`
        : 'Kill roster — this camp has no kill bonus configured, so being on it earns nothing';
    }

    // Named for what makes up the amount, in column order: the window's own amount (a hand-set
    // price or the regular rate), the open bonus on window 1, and the close bonus on the close.
    const base = this.baseWindowValue(event, window);
    const open = this.openWindowValue(event, window);
    const close = this.closeWindowValue(event, window);
    if (window.dkpAmount == null && base <= 0 && open <= 0 && close <= 0) {
      return 'Middle windows carry no bonus — only the open and the close pay';
    }
    const parts: string[] = [];
    if (window.dkpAmount != null || base > 0) {
      parts.push(`${EventsTabComponent.trimDkp(base)} ${window.dkpAmount != null ? 'set for this window' : 'regular'}`);
    }
    if (open > 0) parts.push(`${EventsTabComponent.trimDkp(open)} open`);
    if (close > 0) parts.push(`${EventsTabComponent.trimDkp(close)} close`);
    const note = `${EventsTabComponent.trimDkp(base + open + close)} DKP per attendee (${parts.join(' + ')})`;
    // Said here too, not only at End Camp: the officer should hear it while the windows are on screen.
    return this.missingClosingWindow(event)
      ? `${note} — no closing window ticked yet, so nobody gets the close bonus`
      : note;
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

  // Linked snapshots render as TABS, the same strip the Attendance Windows card uses above them.
  //
  // They used to stack, every roster table open at once, each captioned only by its slot chip
  // ("Misc") and a timestamp. Two misc posts on one camp were two identical-looking blocks told
  // apart by the minute they were taken, and the post's NAME -- the one thing an officer typed to
  // say what the read was for -- appeared nowhere. As tabs, the name IS the label, one roster is
  // open at a time, and the section reads like the windows card it sits under.
  //
  // Chronological left to right, like the window tabs. Opens on the NEWEST, which is the one an
  // officer who just took a read is coming here to check.
  protected readonly activeLinkedSnapshotByEvent = signal<Record<number, number>>({});

  protected linkedSnapshotTabs(event: { linkedSnapshots?: ActivityLinkedSnapshot[] }): ActivityLinkedSnapshot[] {
    return [...this.linkedSnapshotsFor(event)].sort((a, b) =>
      ((parseDate(a.capturedAtUtc) ?? 0) - (parseDate(b.capturedAtUtc) ?? 0)) || (a.id - b.id));
  }

  protected activeLinkedSnapshot(event: ActivityEvent): ActivityLinkedSnapshot | null {
    const tabs = this.linkedSnapshotTabs(event);
    if (tabs.length === 0) return null;
    const wanted = this.activeLinkedSnapshotByEvent()[event.id];
    return tabs.find(s => s.id === wanted) ?? tabs[tabs.length - 1];
  }

  protected setActiveLinkedSnapshot(eventId: number, snapshotId: number): void {
    this.activeLinkedSnapshotByEvent.update(map => ({ ...map, [eventId]: snapshotId }));
  }

  // The post's own name first -- "test", "test2" -- then the slot ("Open", "Window 3") for a
  // capture nobody named, then a neutral word rather than an empty tab.
  protected linkedSnapshotTabLabel(snapshot: ActivityLinkedSnapshot): string {
    return snapshot.name?.trim() || snapshot.windowLabel?.trim() || 'Snapshot';
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
  // The DKP column's cells. Every row of a window shows the same box, because the price belongs to
  // the window: typing in any row prices it for everyone on it.
  //
  // No Apply button and no Use default button: whatever is in the box IS this window's DKP. There
  // is nothing to "confirm" because nothing here is final — the amounts stay editable through
  // review, and the event being ended and posted to the sheet is what settles them.
  //
  // Commits on CHANGE, not on input. `change` on a number field fires on blur or Enter, which is
  // the difference between one write of 1.25 and three writes of 1, 12 and 125 on the way there.
  //
  // An empty box is the instruction the removed "Use default" button used to carry: no price of
  // its own, fall back to the camp's open / regular-window value.
  //
  // Drafts are keyed by window id and only exist for windows the officer has actually typed in —
  // an absent entry means "show what the server holds", so a refresh elsewhere isn't fought by a
  // stale draft.
  private readonly windowDkpDrafts = signal<Record<number, string>>({});
  protected readonly windowDkpSaving = signal(false);

  // The box is never blank. An empty field used to be how "this window has no price of its own"
  // showed, leaning on a `default` placeholder to say it — which left the one control on the card
  // that names a DKP amount as the only thing on the card not showing one, on exactly the windows
  // an officer opens it to read. So it always holds what this window pays before any close bonus:
  // the officer's explicit price when they set one, else the open or the regular rate. It goes
  // through the same baseWindowValue as the note beneath the table, so the two cannot disagree.
  //
  // Showing the default does NOT write it — see commitWindowDkp. A window nobody priced stays
  // unpriced, so it keeps following the camp's open / window bonus when that changes.
  protected windowDkpDraft(event: ActivityEvent, window: ActivityAttendanceWindow): string {
    const draft = this.windowDkpDrafts()[window.id];
    if (draft !== undefined) return draft;
    // A kill roster shows the CAMP'S KILL BONUS, not baseWindowValue -- which returns 0 for one,
    // by design, because the roster pays through that bonus rather than as a window. See
    // commitWindowDkp for the write side.
    if (window.isKillWindow) {
      return EventsTabComponent.trimDkp(this.standardBonus(event, 'kill'));
    }
    return EventsTabComponent.trimDkp(this.baseWindowValue(event, window));
  }

  // Hand-set and inherited look identical, both being a number in the same box, so the tooltip is
  // what tells them apart — and it names the way back to the default.
  protected windowDkpHint(window: ActivityAttendanceWindow): string {
    if (window.isKillWindow) {
      return 'What everyone on the kill roster earns at End Camp. This is the camp\'s kill bonus, '
        + 'not a window price — clear the box to go back to the linkshell default.';
    }
    return window.dkpAmount == null
      ? 'Camp default for this window — type an amount to price it by hand. '
        + 'Applies to everyone on this window.'
      : 'Priced by hand for everyone on this window — clear the box to go back to the camp default.';
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
    // FREEZES the window at it, and a later change to the camp's open / window bonus would then
    // stop reaching a window the officer never actually priced. Typing the number the default
    // already shows means the same thing as leaving it alone, so it is treated the same way.
    if (current === null && amount !== null
        && Math.abs(this.baseWindowValue(event, window) - amount) < 0.0001) {
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
  // Shares windowDkpSaving with the price control on purpose: the two write the same row, so they
  // never fly concurrently.

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
        // Full refresh rather than a local flag flip — marking one window UNMARKS another, and the
        // whole Closing window column repaints when the close moves.
        await this.activity.refreshOverview();
      }
    } finally {
      this.windowDkpSaving.set(false);
    }
  }

  // ----- The Closing window column -----
  //
  // On the close, the cell edits the CAMP'S close bonus (Event.HnmCloseBonusOverride): a camp has
  // one close, so its bonus belongs to the camp rather than to the window. Every other window shows
  // a plain 0. Drafts are keyed by event id for that reason, and commit on change like the DKP cells.
  private readonly closeBonusDrafts = signal<Record<number, string>>({});

  protected closeBonusDraft(event: ActivityEvent): string {
    const draft = this.closeBonusDrafts()[event.id];
    if (draft !== undefined) return draft;
    return EventsTabComponent.trimDkp(this.standardBonus(event, 'close'));
  }

  protected setCloseBonusDraft(event: ActivityEvent, input: Event): void {
    const value = (input.target as HTMLInputElement).value;
    this.closeBonusDrafts.update(map => ({ ...map, [event.id]: value }));
  }

  protected async commitCloseBonus(event: ActivityEvent): Promise<void> {
    const raw = this.closeBonusDraft(event).trim();
    const amount = raw === '' ? null : Number(raw);
    const unchanged = amount === null
      ? event.hnmCloseBonusOverride == null
      : Math.abs(this.standardBonus(event, 'close') - amount) < 0.0001;
    if (unchanged || (amount !== null && (!Number.isFinite(amount) || amount < 0))) {
      this.dropCloseBonusDraft(event);
      return;
    }

    if (this.windowDkpSaving()) return;
    this.windowDkpSaving.set(true);
    try {
      if (await this.activity.setCampCloseBonus(event.id, amount)) {
        this.dropCloseBonusDraft(event);
        await this.activity.refreshOverview();
      }
    } finally {
      this.windowDkpSaving.set(false);
    }
  }

  private dropCloseBonusDraft(event: ActivityEvent): void {
    this.closeBonusDrafts.update(map => {
      const next = { ...map };
      delete next[event.id];
      return next;
    });
  }

  // ----- The Open bonus column -----
  //
  // Twin of the Closing window column. On window 1 the cell edits the CAMP'S open bonus
  // (Event.HnmOpenBonusOverride): a camp has one open, so its bonus belongs to the camp. Every other
  // window shows a plain 0.
  private readonly openBonusDrafts = signal<Record<number, string>>({});

  protected openBonusDraft(event: ActivityEvent): string {
    const draft = this.openBonusDrafts()[event.id];
    if (draft !== undefined) return draft;
    return EventsTabComponent.trimDkp(this.standardBonus(event, 'open'));
  }

  protected setOpenBonusDraft(event: ActivityEvent, input: Event): void {
    const value = (input.target as HTMLInputElement).value;
    this.openBonusDrafts.update(map => ({ ...map, [event.id]: value }));
  }

  protected async commitOpenBonus(event: ActivityEvent): Promise<void> {
    const raw = this.openBonusDraft(event).trim();
    const amount = raw === '' ? null : Number(raw);
    const unchanged = amount === null
      ? event.hnmOpenBonusOverride == null
      : Math.abs(this.standardBonus(event, 'open') - amount) < 0.0001;
    if (unchanged || (amount !== null && (!Number.isFinite(amount) || amount < 0))) {
      this.dropOpenBonusDraft(event);
      return;
    }

    if (this.windowDkpSaving()) return;
    this.windowDkpSaving.set(true);
    try {
      if (await this.activity.setCampOpenBonus(event.id, amount)) {
        this.dropOpenBonusDraft(event);
        await this.activity.refreshOverview();
      }
    } finally {
      this.windowDkpSaving.set(false);
    }
  }

  private dropOpenBonusDraft(event: ActivityEvent): void {
    this.openBonusDrafts.update(map => {
      const next = { ...map };
      delete next[event.id];
      return next;
    });
  }

  // ----- Pricing a misc post by hand -----
  //
  // The Linked Snapshots table's DKP cells on a Misc post: what that post pays each person on it
  // (AttendanceSnapshot.DkpAmount), replacing the linkshell's Misc post rate. Same rules as the
  // window cells: commit on change, empty puts it back on the rate, and typing the number already
  // shown on an unpriced post writes nothing. Shares windowDkpSaving so only one price write is
  // ever in flight.
  private readonly miscDkpDrafts = signal<Record<number, string>>({});

  protected miscDkpDraft(snapshot: ActivityLinkedSnapshot): string {
    const draft = this.miscDkpDrafts()[snapshot.id];
    if (draft !== undefined) return draft;
    return EventsTabComponent.trimDkp(snapshot.dkpPerMember ?? 0);
  }

  protected setMiscDkpDraft(snapshot: ActivityLinkedSnapshot, input: Event): void {
    const value = (input.target as HTMLInputElement).value;
    this.miscDkpDrafts.update(map => ({ ...map, [snapshot.id]: value }));
  }

  protected async commitMiscDkp(snapshot: ActivityLinkedSnapshot): Promise<void> {
    const raw = this.miscDkpDraft(snapshot).trim();
    const amount = raw === '' ? null : Number(raw);
    const current = snapshot.dkpOverride ?? null;
    const unchanged = amount === null
      ? current === null
      : Math.abs((current ?? snapshot.dkpPerMember ?? 0) - amount) < 0.0001;
    if (unchanged || (amount !== null && (!Number.isFinite(amount) || amount < 0))) {
      this.dropMiscDkpDraft(snapshot);
      return;
    }

    if (this.windowDkpSaving()) return;
    this.windowDkpSaving.set(true);
    try {
      if (await this.activity.setMiscPostDkp(snapshot.id, amount)) {
        this.dropMiscDkpDraft(snapshot);
        await this.activity.refreshOverview();
      }
    } finally {
      this.windowDkpSaving.set(false);
    }
  }

  // Unlink: sends a linked snapshot back to Unlinked Snapshots. Two clicks, through an inline
  // confirm (the Discord iframe blocks confirm()), because it changes what the camp pays.
  protected readonly pendingUnlinkSnapshotId = signal<number | null>(null);
  protected readonly unlinkingSnapshotId = signal<number | null>(null);

  protected async confirmUnlinkSnapshot(snapshot: ActivityLinkedSnapshot): Promise<void> {
    if (this.unlinkingSnapshotId() !== null) return;
    this.unlinkingSnapshotId.set(snapshot.id);
    try {
      if (await this.activity.unlinkCampSnapshot(snapshot.id)) {
        this.pendingUnlinkSnapshotId.set(null);
        this.dropMiscDkpDraft(snapshot);
        await this.activity.refreshOverview();
      }
    } finally {
      this.unlinkingSnapshotId.set(null);
    }
  }

  private dropMiscDkpDraft(snapshot: ActivityLinkedSnapshot): void {
    this.miscDkpDrafts.update(map => {
      const next = { ...map };
      delete next[snapshot.id];
      return next;
    });
  }

  protected miscDkpHint(snapshot: ActivityLinkedSnapshot): string {
    return snapshot.dkpOverride == null
      ? 'The linkshell\'s Misc post rate — type an amount to price this post by hand. '
        + 'Applies to everyone on it.'
      : 'Priced by hand for everyone on this post — clear the box to go back to the Misc post rate.';
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
