import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { formatActionError } from '../../discord/discord-activity.helpers';
import { DiscordActivityService } from '../../discord/discord-activity.service';
import { WindowEventService } from '../../discord/window-event.service';
import type {
  ActivityEvent,
  ActivityWindowCombinedMember,
  ActivityWindowEvent,
  ActivityWindowEventCaptureDkpInput,
  ActivityWindowEventMemberDkpInput,
  ActivityWindowSnapshot,
  ActivityWindowSnapshotEntry
} from '../../discord/discord-activity.types';

// Baseline DKP per member when neither a saved override nor an event default
// exists yet.
const FALLBACK_DKP = 1.5;
// Mirrors AttendanceSnapshotSlotKinds on the server, for the roster's credit chips. Filing no
// longer picks one: a filed capture is always Misc (SnapshotFilingService).
const SLOT_WINDOW = "Window";
const SLOT_MISC = "Misc";

interface MemberDkpDraft {
  value: number | null;
  // True while the row mirrors the event's Default DKP. Goes false the moment
  // the officer types into the per-row input.
  followsDefault: boolean;
}

// Attendance snapshots used to be their own tab. They now render as two slices of the Event System
// tab — open events sit in "Current Field Activity" beside the live camps that produced them, and
// the review leftovers (unlinked snapshots, closed events) sit below the Upcoming Events queue.
// One component serves both because the per-member DKP draft below is read by BOTH the card's
// combined-roster column and the per-snapshot DKP input; splitting card from snapshot would force
// that state into a service for no gain. An event is either open or closed, so the two mounted
// instances never share one and their drafts can't diverge.
@Component({
  selector: 'app-attendance-sections',
  imports: [CommonModule, FormsModule],
  templateUrl: './attendance-sections.component.html',
  styleUrl: './attendance-sections.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AttendanceSectionsComponent {
  // Which slice of the attendance data to render. The component is mounted three times on the
  // Events tab, once per value:
  //   'live'     — the open attendance event cards, beside the camps that produced them
  //   'unlinked' — captures with no event yet, the triage queue (its own section, not the archive)
  //   'archive'  — the searchable closed-event history
  //
  // Required on purpose: an unset key would render nothing, which reads exactly like "this
  // linkshell has no attendance data".
  readonly section = input.required<'live' | 'unlinked' | 'archive'>();

  protected readonly activity = inject(DiscordActivityService);
  protected readonly windows = inject(WindowEventService);
  protected readonly attachNames: Record<number, string> = {};
  // Snapshot id -> the event picked in its Event dropdown. Kept here rather than read off the
  // <select>, so picking one re-checks the Attach button.
  protected readonly attachTargets: Record<number, string> = {};
  // Snapshot id -> the DKP box's value, and the value last seeded or saved (see attachDkp).
  protected readonly attachDkps: Record<number, number | null> = {};
  private readonly attachDkpSaved: Record<number, number | null> = {};
  // Snapshot id -> why its last Attach / Make New Event was refused ("That camp has ended...").
  // Shown on the row itself: the page's error banner sits at the top, out of sight
  // from the Unlinked Snapshots list.
  protected readonly snapshotErrors = signal<Record<number, string>>({});
  // Per-event, per-character DKP draft. Reset whenever the underlying combined
  // roster changes (handled lazily on read so newly-added/removed people
  // pick up sane defaults).
  protected readonly memberDkpDrafts: Record<number, Record<string, MemberDkpDraft>> = {};
  // Per-event, per-ENTRY DKP draft, for cards whose captures carry the money. Keyed by snapshot
  // entry id rather than by character on purpose: the same person is worth the open in one window
  // and the regular rate in the next, so a per-character draft could only hold one of them — which
  // is exactly how every capture came to show the same number.
  protected readonly captureDkpDrafts: Record<number, Record<number, number | null>> = {};
  // Snapshot id -> "add a character by name" input value.
  protected readonly addPersonDrafts: Record<number, string> = {};
  // Two-click confirm state for destructive actions — the Discord iframe
  // blocks native `confirm()` so we surface a "Confirm" button instead.
  protected readonly confirmDeleteEventId = signal<number | null>(null);
  protected readonly confirmDeleteSnapshotId = signal<number | null>(null);
  protected readonly confirmRemoveEntryId = signal<number | null>(null);
  protected readonly confirmRejectSnapshotId = signal<number | null>(null);

  // Alliance numbers an officer can reassign a capture to. Mirrors
  // AttendanceSnapshotAlliances.MaxAllianceNumber on the server, which clamps anything higher —
  // six alliances is 108 people, past any turnout this has to render.
  protected readonly allianceOptions = [1, 2, 3, 4, 5, 6];

  // No load effect here: EventsTabComponent owns the fetch, so mounting this component twice
  // doesn't fire two requests.
  protected primaryLinkshellId(): number {
    return this.activity.overview()?.primaryLinkshell?.id ?? this.activity.overview()?.appUser?.primaryLinkshellId ?? 0;
  }

  protected data() {
    return this.windows.data();
  }

  protected rosterNames(): string[] {
    return this.data()?.rosterCharacterNames ?? [];
  }

  // Unlinked captures still waiting on an officer. Drives the section header's warning tag, which
  // is the only place the count is visible without expanding each snapshot in turn.
  protected pendingUnlinkedCount(model: { unlinkedSnapshots: ActivityWindowSnapshot[] }): number {
    return model.unlinkedSnapshots.filter(snapshot => snapshot.isPending).length;
  }

  // Triage's two parts: what members sent without permission (waiting on an officer's Confirm),
  // and officers' own or already confirmed captures that just aren't filed against an event yet.
  protected unlinkedAwaiting(model: { unlinkedSnapshots: ActivityWindowSnapshot[] }): ActivityWindowSnapshot[] {
    return model.unlinkedSnapshots.filter(snapshot => snapshot.isPending);
  }

  protected unlinkedUnfiled(model: { unlinkedSnapshots: ActivityWindowSnapshot[] }): ActivityWindowSnapshot[] {
    return model.unlinkedSnapshots.filter(snapshot => !snapshot.isPending);
  }

  // "Window" / "Misc" are the STORAGE names (AttendanceSnapshotSlotKinds), and they told an
  // officer nothing about where a row came from. They map exactly onto the two ways attendance is
  // captured, so the chip says that instead.
  protected creditSourceLabel(source: string | null | undefined): string {
    if (source === 'Misc') return '/lsm now';
    if (source === 'Both') return 'Addon UI + /lsm now';
    return 'Addon UI';
  }

  protected creditSourceHint(source: string | null | undefined): string {
    if (source === 'Misc') return 'Captured with the /lsm now command, outside any window.';
    if (source === 'Both') return 'Posted against a window from the addon UI, and also captured with /lsm now.';
    return "Posted against a window from the addon's Attendance panel.";
  }

  // ----- Attendance Archive search + paging -----
  //
  // The applied query and page live on WindowEventService because the server does the searching
  // (see the note there). What's local is the DRAFT the officer is typing: nothing refetches until
  // they hit Search, since this payload carries every listed event's whole snapshot tree and the
  // tab polls it.
  //
  // Re-seeded whenever the applied query changes underneath the draft — a Clear, or a linkshell
  // switch resetting the archive — so the box can never disagree with the cards below it. Same
  // lazy-sync idiom as attachName() below.
  private archiveSearchSyncedFrom: string | null = null;
  private archiveSearchDraft = '';

  protected archiveSearchText(): string {
    const applied = this.windows.closedQuery();
    if (this.archiveSearchSyncedFrom !== applied) {
      this.archiveSearchSyncedFrom = applied;
      this.archiveSearchDraft = applied;
    }
    return this.archiveSearchDraft;
  }

  protected setArchiveSearchText(value: string): void {
    this.archiveSearchDraft = value ?? '';
  }

  // The query the CURRENT page was actually built from, straight off the payload — not the draft.
  // "No closed events match X" has to name what was searched, not what has since been typed.
  protected appliedArchiveQuery(): string {
    return this.data()?.closedQuery ?? '';
  }

  protected async submitArchiveSearch(): Promise<void> {
    const id = this.primaryLinkshellId();
    if (id) await this.windows.searchClosed(id, this.archiveSearchDraft);
  }

  protected async clearArchiveSearch(): Promise<void> {
    this.archiveSearchDraft = '';
    const id = this.primaryLinkshellId();
    if (id) await this.windows.searchClosed(id, '');
  }

  protected async goToArchivePage(page: number): Promise<void> {
    const id = this.primaryLinkshellId();
    if (id) await this.windows.goToClosedPage(id, page);
  }

  protected archiveTotalPages(): number {
    const model = this.data();
    if (!model) return 1;
    return Math.max(1, Math.ceil(model.closedTotalCount / Math.max(1, model.closedPageSize)));
  }

  // Bounds of the "Showing 1-10 of 34" tally. Zero when the archive is empty, so the template
  // shows "No results" instead of a 0-0 range.
  protected archiveRangeStart(): number {
    const model = this.data();
    if (!model || model.closedTotalCount === 0) return 0;
    return ((model.closedPage - 1) * model.closedPageSize) + 1;
  }

  protected archiveRangeEnd(): number {
    const model = this.data();
    if (!model) return 0;
    return Math.min(model.closedPage * model.closedPageSize, model.closedTotalCount);
  }

  protected formatDate(value?: string | null): string {
    if (!value) return '-';
    return new Intl.DateTimeFormat([], {
      month: 'short',
      day: 'numeric',
      year: 'numeric',
      hour: 'numeric',
      minute: '2-digit'
    }).format(new Date(value));
  }

  // The card header's "Attendance | <zone>" line: the first capture that names a zone.
  protected eventZone(event: ActivityWindowEvent): string | null {
    const zone = event.snapshots.find(s => !!s.primaryZone)?.primaryZone;
    return zone ? zone.split('_').join(' ') : null;
  }

  // The small label over the card header's centre readout: where the event came from.
  protected originLabel(event: ActivityWindowEvent): string {
    return event.isCamp || event.sourceEventId ? 'Camp generated Event' : 'LSM Now generated Event';
  }

  // The big centre readout. An LSM Now event counts its posts, Misc included -- most of what lands
  // on one is Misc, so a window count read 0. A camp-made event says how many windows have a
  // capture, worded like the live camp card's, with a window two alliances both posted counted
  // once. Either way an ignored capture doesn't count and a pending one does (it was captured, it
  // just isn't confirmed yet). Ungridded captures have no window number, so each counts on its own.
  protected readoutLabel(event: ActivityWindowEvent): string {
    const counts = (s: ActivityWindowSnapshot) => s.snapshotStatus === 'Active' || s.isPending;
    if (!(event.isCamp || event.sourceEventId)) {
      const posts = event.snapshots.filter(counts).length;
      return `${posts} ${posts === 1 ? 'Post' : 'Posts'}`;
    }
    const windows = this.windowSnapshots(event).filter(counts);
    const n = new Set(windows.map(s => s.windowNumber ?? `capture-${s.id}`)).size;
    return `${n} ${n === 1 ? 'Window' : 'Windows'} Captured`;
  }

  protected jobs(row: ActivityWindowSnapshotEntry | ActivityWindowCombinedMember): string {
    const isReal = (job?: string | null): boolean =>
      !!job && job.toUpperCase() !== 'NONE';
    const mainReal = isReal(row.mainJob);
    const subReal = isReal(row.subJob);
    if (!mainReal && !subReal) return 'Anon';
    const main = mainReal ? `${row.mainJob} ${row.mainJobLevel ?? ''}`.trim() : '-';
    const sub = subReal ? `${row.subJob} ${row.subJobLevel ?? ''}`.trim() : '-';
    return `${main}/${sub}`;
  }

  protected formatDkp(value: number | null | undefined): string {
    if (value === null || value === undefined) return '-';
    return Number.isInteger(value) ? value.toString() : value.toString();
  }

  // ----- Edit event dialog -----
  // The name is the only editable field left (Default DKP moved to per-snapshot), so it lives
  // in a dialog rather than inline next to Close / Delete, where a stray click was costly.
  protected readonly editEventId = signal<number | null>(null);
  protected editEventName = '';

  protected openEditEvent(event: ActivityWindowEvent): void {
    this.editEventId.set(event.id);
    this.editEventName = event.name ?? '';
  }

  protected closeEditEvent(): void {
    this.editEventId.set(null);
    this.editEventName = '';
  }

  protected async saveEditEvent(): Promise<void> {
    const eventId = this.editEventId();
    const linkshellId = this.primaryLinkshellId();
    const name = this.editEventName.trim();
    if (eventId === null || !linkshellId || !name) return;
    await this.windows.rename(eventId, name, linkshellId);
    this.closeEditEvent();
  }

  protected attachName(snapshot: ActivityWindowSnapshot): string {
    this.attachNames[snapshot.id] ??= snapshot.name ?? '';
    return this.attachNames[snapshot.id];
  }

  protected setAttachName(snapshotId: number, value: string): void {
    this.attachNames[snapshotId] = value;
  }

  // The DKP box: what this post pays each person on it. Starts at the post's own price, else the
  // linkshell's Misc post rate. `attachDkpSaved` is the value last seeded or saved, so only a real
  // change is sent -- an untouched box leaves the post on the rate rather than pinning it.
  protected attachDkp(snapshot: ActivityWindowSnapshot): number | null {
    if (!(snapshot.id in this.attachDkpSaved)) {
      const seed = snapshot.dkpOverride ?? this.data()?.miscPostRate ?? null;
      this.attachDkpSaved[snapshot.id] = seed;
      this.attachDkps[snapshot.id] = seed;
    }
    return this.attachDkps[snapshot.id] ?? null;
  }

  protected setAttachDkp(snapshotId: number, value: number | null): void {
    this.attachDkps[snapshotId] = value;
    this.setSnapshotError(snapshotId, null);
  }

  // Blur / Enter on the DKP box. A refusal lands on the row like Attach's.
  protected async commitAttachDkp(snapshot: ActivityWindowSnapshot): Promise<void> {
    await this.fileSnapshotRow(snapshot, () => this.saveAttachDkp(snapshot));
  }

  // Saves the box as the post's own price when it changed. Blank clears it, putting the post back
  // on the Misc post rate. Throws on failure so a filing action stops before it files.
  private async saveAttachDkp(snapshot: ActivityWindowSnapshot): Promise<void> {
    const value = this.attachDkp(snapshot);
    if (value === this.attachDkpSaved[snapshot.id]) return;
    if (value !== null && (!Number.isFinite(value) || value < 0)) {
      throw new Error('DKP must be 0 or more.');
    }
    if (!(await this.activity.setMiscPostDkp(snapshot.id, value))) {
      throw new Error(this.activity.actionError() ?? "Setting this post's DKP failed.");
    }
    this.attachDkpSaved[snapshot.id] = value;
  }

  // The Event dropdown's pick ("camp:<id>" / "we:<id>", '' = none yet).
  protected attachTarget(snapshotId: number): string {
    return this.attachTargets[snapshotId] ?? '';
  }

  protected setAttachTarget(snapshotId: number, value: string): void {
    this.attachTargets[snapshotId] = value;
    this.setSnapshotError(snapshotId, null);
  }

  private setSnapshotError(snapshotId: number, message: string | null): void {
    this.snapshotErrors.update(errors => {
      const next = { ...errors };
      if (message) next[snapshotId] = message;
      else delete next[snapshotId];
      return next;
    });
  }

  // Runs a filing action for one row and puts a refusal on that row, taking it off the banner so
  // it isn't shown twice.
  private async fileSnapshotRow(snapshot: ActivityWindowSnapshot, action: () => Promise<void>): Promise<void> {
    this.setSnapshotError(snapshot.id, null);
    try {
      await action();
    } catch (error) {
      this.activity.clearActionState();
      this.setSnapshotError(snapshot.id, formatActionError(error, 'Filing this snapshot failed.'));
    }
  }

  protected readonly slotWindow = SLOT_WINDOW;
  protected readonly slotMisc = SLOT_MISC;

  // The two groups a card renders its captures in. Split server-side and mirrored here.
  protected windowSnapshots(event: ActivityWindowEvent): ActivityWindowSnapshot[] {
    return event.snapshots.filter(s => !s.isMisc);
  }

  protected miscSnapshots(event: ActivityWindowEvent): ActivityWindowSnapshot[] {
    return event.snapshots.filter(s => s.isMisc);
  }

  // An ungridded camp (Sky gods, farm NMs) has no window numbers to show, so calling its captures
  // "Windows" would promise a distinction it does not have.
  protected windowGroupTitle(event: ActivityWindowEvent): string {
    return event.hasWindowGrid ? 'Windows' : 'Captures';
  }

  protected async close(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    if (id) await this.windows.close(event.id, id);
  }

  protected async reopen(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    if (id) await this.windows.reopen(event.id, id);
  }

  protected async deleteEvent(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id) return;
    await this.windows.deleteEvent(event.id, id);
    this.confirmDeleteEventId.set(null);
  }

  // "Create New Event": always mints a fresh attendance event under the typed name.
  //
  // It used to fold into an open event of the same name when one existed within 24h. That
  // reuse belongs to the dropdown now — this button says Create, so it creates. On a repeat
  // camp the same monster name comes round often enough that the old behaviour quietly
  // merged two separate runs into one payroll.
  protected async createNewEvent(snapshot: ActivityWindowSnapshot): Promise<void> {
    const id = this.primaryLinkshellId();
    const name = this.attachName(snapshot).trim();
    if (!id || !name) return;
    await this.fileSnapshotRow(snapshot, async () => {
      // The server keeps a capture's existing name, so a retyped one is saved first or the chip
      // and the new event would disagree.
      await this.saveAttachName(snapshot);
      // Its price becomes the new event's Misc DKP (server-side), so it is saved before filing.
      await this.saveAttachDkp(snapshot);
      // The post lands on the new event as a misc post, like every filed capture.
      await this.windows.attachSnapshot(snapshot.id, id, { name, createNew: true });
    });
  }

  // Persists the Message box onto the SNAPSHOT without creating or attaching anything, when it
  // differs from what is saved. There is no Save button: the box saves when it loses focus or on
  // Enter, and Attach / Make New Event call this first. Blank is allowed and clears it — an
  // officer who mislabelled a capture should be able to undo that without inventing a
  // placeholder name.
  protected async saveAttachName(snapshot: ActivityWindowSnapshot): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id) return;
    const name = this.attachName(snapshot).trim();
    if (name === (snapshot.name ?? '').trim()) return;
    await this.windows.renameSnapshot(snapshot.id, id, name.length > 0 ? name : null);
  }

  protected async setSnapshotAlliance(snapshot: ActivityWindowSnapshot, allianceNumber: number): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id || !allianceNumber || allianceNumber === snapshot.allianceNumber) return;
    await this.windows.setSnapshotAlliance(snapshot.id, id, allianceNumber);
  }

  // Confirm (true) puts the capture's members into the combined roster and therefore into the
  // payout; Reject (false) takes it out of every roster for good.
  protected async verifySnapshot(snapshot: ActivityWindowSnapshot, verified: boolean): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id) return;
    await this.windows.verifySnapshot(snapshot.id, id, verified);
    this.confirmRejectSnapshotId.set(null);
  }

  // Live HNM camps, offered as attach targets alongside the open attendance
  // events. Read straight off the overview rather than added to the window-events
  // payload: both are already loaded on this tab, and the camps are exactly what
  // the user sees in Current Field Activity above.
  //
  // A camp is live once it has commenced. Ended camps are excluded by the server
  // (ending an event deletes the row), so "has commencementStartTime" is the
  // whole test — the same one the events tab uses for its live list.
  protected liveHnmCamps(): ActivityEvent[] {
    return (this.activity.overview()?.activeEvents ?? [])
      .filter(event => (event.type ?? '').trim().toUpperCase() === 'HNM')
      .filter(event => Boolean(event.commencementStartTime));
  }

  // Whether there is anything at all to pick from, so the template can hide the
  // whole row rather than render an empty select.
  protected hasAttachTargets(model: { openEvents: ActivityWindowEvent[] }): boolean {
    return model.openEvents.length > 0 || this.liveHnmCamps().length > 0;
  }

  // One dropdown, two kinds of target, so the option value carries which kind:
  //   "we:<id>"   an open attendance event -> attach to it directly
  //   "camp:<id>" a live camp -> file it ON THE CAMP, with no attendance event
  //
  // The camp case mints nothing. It used to go through the name path, which created
  // an attendance event named after the camp -- a second card under the live board --
  // and End Camp, which builds the camp's real review card from its posted windows,
  // never picked that second card up, so a misc post filed this way never reached
  // Events Pending DKP Post. The server now parks the capture on the camp itself
  // (it shows on the camp's card in Current Field Activity) and End Camp carries it
  // onto the review card as a misc post.
  protected async attachToSelection(snapshot: ActivityWindowSnapshot, selection: string): Promise<void> {
    await this.fileSnapshotRow(snapshot, () => this.fileToSelection(snapshot, selection));
  }

  private async fileToSelection(snapshot: ActivityWindowSnapshot, selection: string): Promise<void> {
    const linkshellId = this.primaryLinkshellId();
    if (!linkshellId || !selection) return;
    await this.saveAttachName(snapshot);
    await this.saveAttachDkp(snapshot);

    if (selection.startsWith('we:')) {
      const windowEventId = Number(selection.slice(3));
      if (!windowEventId) return;
      await this.windows.attachSnapshot(snapshot.id, linkshellId, { windowEventId });
      return;
    }

    if (selection.startsWith('camp:')) {
      const campId = Number(selection.slice(5));
      if (!campId) return;
      await this.windows.attachSnapshot(snapshot.id, linkshellId, { linkedEventId: campId });
      // The camp card reads from the overview, not the window-events payload, so it
      // needs its own refresh or the snapshot won't appear until the next poll.
      await this.activity.refreshOverview();
    }
  }

  protected async setSnapshotStatus(snapshot: ActivityWindowSnapshot, status: string): Promise<void> {
    const id = this.primaryLinkshellId();
    if (id) await this.windows.setSnapshotStatus(snapshot.id, id, status);
  }

  protected async deleteSnapshot(snapshot: ActivityWindowSnapshot): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id) return;
    await this.windows.deleteSnapshot(snapshot.id, id);
    this.confirmDeleteSnapshotId.set(null);
  }

  protected addPersonDraft(snapshot: ActivityWindowSnapshot): string {
    this.addPersonDrafts[snapshot.id] ??= '';
    return this.addPersonDrafts[snapshot.id];
  }

  protected setAddPersonDraft(snapshotId: number, value: string): void {
    this.addPersonDrafts[snapshotId] = value;
  }

  // Custom roster typeahead state. Replaces the native <datalist>, which the
  // browser renders as an uncontrollable full-height popup (no way to cap its
  // height or scroll it). Holds the snapshot id whose dropdown is open.
  protected readonly openAddTypeahead = signal<number | null>(null);
  // Cap the rendered suggestions so a 195-member roster doesn't build a huge
  // DOM list; the input filters it down quickly anyway.
  private static readonly MAX_TYPEAHEAD_RESULTS = 50;

  // Where the open menu is drawn, in VIEWPORT coordinates.
  //
  // The input sits in the last row of a snapshot table, and that table lives in a horizontal
  // scroll box (.we-table__scroll, overflow-x: auto) so it can stay 520px wide inside a narrow
  // Discord panel. A scroll box clips on both axes -- CSS cannot scroll one and not clip the
  // other -- so the menu, which opens UPWARD over the rows, had its top sliced off by the table's
  // own edge. Positioning it against the viewport takes it out of that box entirely.
  //
  // Measured once, on open. The menu closes on blur, so there is no window in which the page
  // scrolls under an open menu and leaves it behind.
  protected readonly typeaheadAnchor = signal<{ left: number; top: number; width: number } | null>(null);

  protected openTypeahead(snapshotId: number, input?: HTMLElement): void {
    this.openAddTypeahead.set(snapshotId);
    const rect = input?.getBoundingClientRect();
    this.typeaheadAnchor.set(rect ? { left: rect.left, top: rect.top, width: rect.width } : null);
  }

  protected closeTypeahead(): void {
    this.openAddTypeahead.set(null);
  }

  // Roster names matching the current draft (case-insensitive substring),
  // capped. Empty query shows the head of the roster so focusing the field
  // still reveals the list.
  protected filteredRoster(snapshot: ActivityWindowSnapshot): string[] {
    const query = (this.addPersonDrafts[snapshot.id] ?? '').trim().toLowerCase();
    const names = this.rosterNames();
    const matches = query
      ? names.filter(name => name.toLowerCase().includes(query))
      : names;
    return matches.slice(0, AttendanceSectionsComponent.MAX_TYPEAHEAD_RESULTS);
  }

  // Click a suggestion -> fill the draft and add immediately (fewer clicks than
  // select-then-confirm). Free-typed names not in the roster still work via the
  // "+ Add person" button / Enter key.
  protected chooseRosterName(snapshot: ActivityWindowSnapshot, name: string): void {
    this.addPersonDrafts[snapshot.id] = name;
    this.openAddTypeahead.set(null);
    void this.addPerson(snapshot);
  }

  protected async addPerson(snapshot: ActivityWindowSnapshot): Promise<void> {
    this.openAddTypeahead.set(null);
    const id = this.primaryLinkshellId();
    const name = (this.addPersonDrafts[snapshot.id] ?? '').trim();
    if (!id || !name) return;
    await this.windows.addSnapshotEntry(snapshot.id, id, { characterName: name });
    this.addPersonDrafts[snapshot.id] = '';
  }

  protected async removePerson(snapshot: ActivityWindowSnapshot, entry: ActivityWindowSnapshotEntry): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id) return;
    await this.windows.deleteSnapshotEntry(snapshot.id, entry.id, id);
    this.confirmRemoveEntryId.set(null);
  }

  // ----- DKP posting (event defaults + per-character overrides) -----

  // entryType is no longer an input — it's auto-tagged from the monster when the event is
  // created. It stays on the draft only so the stored tag is echoed back on save; the server
  // resolves it (and heals a legacy null) either way. See WindowEventEntryTypes.Resolve.
  protected readonly dkpDrafts: Record<number, { amount: number | null; entryType: string; misc: number | null }> = {};

  protected isPosted(event: ActivityWindowEvent): boolean {
    return !!event.postedToSheetUtc;
  }

  protected dkpDraft(event: ActivityWindowEvent): { amount: number | null; entryType: string; misc: number | null } {
    this.dkpDrafts[event.id] ??= {
      amount: event.dkpAmount ?? null,
      entryType: event.entryType ?? '',
      // The server fills a blank one in with the linkshell's Misc post rate, so this starts at the
      // rate the event pays. Blanking the input goes back to that rate.
      misc: event.miscDkpAmount ?? null
    };
    return this.dkpDrafts[event.id];
  }

  private currentDefault(event: ActivityWindowEvent): number {
    const draft = this.dkpDrafts[event.id];
    if (draft && draft.amount !== null) return draft.amount;
    return event.dkpAmount ?? FALLBACK_DKP;
  }

  protected memberDkp(event: ActivityWindowEvent, member: ActivityWindowCombinedMember): MemberDkpDraft {
    const bucket = (this.memberDkpDrafts[event.id] ??= {});
    const key = member.characterName.trim();
    if (!(key in bucket)) {
      const hasOverride = member.dkpAmountOverride !== null && member.dkpAmountOverride !== undefined;
      bucket[key] = hasOverride
        ? { value: member.dkpAmountOverride ?? null, followsDefault: false }
        : { value: member.effectiveDkpAmount ?? this.currentDefault(event), followsDefault: true };
    }
    return bucket[key];
  }

  protected setMemberDkp(event: ActivityWindowEvent, member: ActivityWindowCombinedMember, value: number | null): void {
    const draft = this.memberDkp(event, member);
    draft.value = value;
    draft.followsDefault = false;
  }

  // ----------------------------------------------------------------- per-capture amounts ---
  //
  // On a camp handed off from a Standard board the money belongs to the CAPTURES: window 1 pays
  // the open, the middle windows pay the regular rate, the marked close pays the close, and the
  // Post Kill roster pays nothing as a window because the kill bonus covers it. Drafts are keyed
  // by ENTRY id rather than by character, which is the difference that matters — one person is
  // worth three different amounts on a three-window camp.

  protected captureDkp(event: ActivityWindowEvent, entry: ActivityWindowSnapshotEntry): number | null {
    const bucket = (this.captureDkpDrafts[event.id] ??= {});
    if (!(entry.id in bucket)) {
      bucket[entry.id] = entry.dkpAmount ?? null;
    }
    return bucket[entry.id];
  }

  protected setCaptureDkp(
    event: ActivityWindowEvent, entry: ActivityWindowSnapshotEntry, value: number | null): void {
    (this.captureDkpDrafts[event.id] ??= {})[entry.id] = value;
  }

  // The DKP a post pays, for its COLLAPSED row: one amount when everyone on it gets the same, a
  // "low–high" range when not. Reads the very drafts the boxes inside are bound to, so it moves as
  // an officer types. An unlinked post (no event yet) shows its own price. On an event, a post that
  // is pending or ignored pays nobody, and an unregistered name is paid nothing, so those add no
  // amount; null when nothing is left to show.
  protected postDkpLabel(snapshot: ActivityWindowSnapshot, event: ActivityWindowEvent | null): string | null {
    const values: number[] = [];
    if (!event) {
      const own = this.attachDkp(snapshot);
      if (own !== null && Number.isFinite(Number(own))) values.push(Number(own));
    } else {
      if (snapshot.snapshotStatus !== 'Active' || snapshot.isPending) return null;
      for (const entry of snapshot.entries ?? []) {
        let value: number | null = null;
        if (event.perCaptureDkp) {
          value = this.captureDkp(event, entry);
        } else {
          const name = entry.characterName.trim().toLowerCase();
          const member = event.combinedMembers.find(m => m.characterName.trim().toLowerCase() === name);
          if (!member || member.isRegistered === false) continue;
          value = this.memberDkp(event, member).value;
        }
        if (value !== null && Number.isFinite(Number(value))) values.push(Number(value));
      }
    }
    if (values.length === 0) return null;
    const low = Math.min(...values);
    const high = Math.max(...values);
    return low === high
      ? `${this.formatDkp(low)} DKP`
      : `${this.formatDkp(low)}–${this.formatDkp(high)} DKP`;
  }

  // The Combined DKP column shows the live draft so officers see edits
  // (including default-driven cascades) reflected immediately.
  //
  // On a priced camp it is a SUM of the capture drafts rather than one member figure, so
  // re-pricing a single window moves the total on screen the moment it is typed.
  protected combinedDkpDisplay(event: ActivityWindowEvent, member: ActivityWindowCombinedMember): string {
    if (event.perCaptureDkp) {
      const name = member.characterName.trim().toLowerCase();
      let total = 0;
      for (const snapshot of event.snapshots ?? []) {
        if (snapshot.snapshotStatus !== "Active") continue;
        for (const entry of snapshot.entries ?? []) {
          if (entry.characterName.trim().toLowerCase() !== name) continue;
          total += this.captureDkp(event, entry) ?? 0;
        }
      }
      return this.formatDkp(total);
    }

    const draft = this.memberDkp(event, member);
    return this.formatDkp(draft.value);
  }

  // The two payloads are mutually exclusive: a priced camp sends its capture amounts and NO member
  // rows (the server writes none for it, and sending some would only add rows nothing pays), while
  // every other card sends member rows and no captures.
  private buildCaptureDkpPayload(event: ActivityWindowEvent): ActivityWindowEventCaptureDkpInput[] | undefined {
    if (!event.perCaptureDkp) return undefined;
    const bucket = this.captureDkpDrafts[event.id];
    if (!bucket) return [];
    return Object.entries(bucket).map(([entryId, dkpAmount]) => ({
      entryId: Number(entryId),
      dkpAmount
    }));
  }

  private buildMemberDkpPayload(event: ActivityWindowEvent): ActivityWindowEventMemberDkpInput[] {
    if (event.perCaptureDkp) return [];
    const bucket = this.memberDkpDrafts[event.id];
    if (!bucket) return [];
    // Send every known character so the server can reconcile (rows that
    // match the default get their override row removed; differing rows are
    // upserted).
    return Object.entries(bucket).map(([characterName, row]) => ({
      characterName,
      dkpAmount: row.value
    }));
  }

  private dkpDraftValid(event: ActivityWindowEvent): boolean {
    const draft = this.dkpDraft(event);
    // No entryType check: with the picker gone there is no way for an officer to fill a blank
    // one, so requiring it here would permanently disable Post/Save on any event whose tag is
    // missing — exactly the rows that most need posting. The server resolves it instead.
    //
    // Same for a blank amount: the Default DKP box is gone too (DKP is set per capture), so an
    // event created with no amount (an /lsm now event) could never be saved or sent. The server
    // keeps the stored amount or falls back to the default (WindowEventDkp.Resolve). Only a
    // negative number is refused.
    return draft.amount === null || draft.amount >= 0;
  }

  protected dkpActionsDisabled(event: ActivityWindowEvent): boolean {
    return this.windows.busy() || !this.dkpDraftValid(event);
  }

  protected async saveDkp(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const draft = this.dkpDraft(event);
    if (!id || !this.dkpDraftValid(event)) return;
    await this.windows.saveDkp(event.id, id, draft.amount, draft.entryType, this.buildMemberDkpPayload(event), draft.misc, this.buildCaptureDkpPayload(event));
  }

  // A live event made from /lsm now isn't posted from Live Events: Send to Review moves it to
  // Events Pending DKP Post, and it is posted from there. Camp-made cards only ever reach that
  // list by End Camp.
  protected sendsToReview(event: ActivityWindowEvent): boolean {
    return this.section() === 'live' && !(event.isCamp || event.sourceEventId);
  }

  protected async sendToReview(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const draft = this.dkpDraft(event);
    if (!id || !this.dkpDraftValid(event)) return;
    await this.windows.sendToReview(event.id, id, draft.amount, draft.entryType, this.buildMemberDkpPayload(event), draft.misc, this.buildCaptureDkpPayload(event));
  }

  // No native confirm — the explicit "Post to sheet" / "Update sheet" button
  // click IS the confirmation. Discord's iframe blocks window.confirm()
  // which silently returned false and dropped the action.
  protected async postToSheet(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const draft = this.dkpDraft(event);
    if (!id || !this.dkpDraftValid(event)) return;
    await this.windows.postToSheet(event.id, id, draft.amount, draft.entryType, this.buildMemberDkpPayload(event), draft.misc, this.buildCaptureDkpPayload(event));
  }

  protected async editPosted(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const draft = this.dkpDraft(event);
    if (!id || !this.dkpDraftValid(event)) return;
    await this.windows.editPosted(event.id, id, draft.amount, draft.entryType, this.buildMemberDkpPayload(event), draft.misc, this.buildCaptureDkpPayload(event));
  }
}
