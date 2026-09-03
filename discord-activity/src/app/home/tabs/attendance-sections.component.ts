import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { WindowEventService } from '../../discord/window-event.service';
import type {
  ActivityEvent,
  ActivityWindowCombinedMember,
  ActivityWindowEvent,
  ActivityWindowEventMemberDkpInput,
  ActivityWindowSnapshot,
  ActivityWindowSnapshotEntry
} from '../../discord/discord-activity.types';

// Baseline DKP per member when neither a saved override nor an event default
// exists yet. Mirrors the web view (`memberDkpDefault = Model.DkpAmount ?? 1.5`).
const FALLBACK_DKP = 1.5;
// Mirrors AttendanceSnapshotSlotKinds on the server. Window is the fail-closed default: it is what
// every capture means until an officer says otherwise.
const SLOT_WINDOW = "Window";
const SLOT_MISC = "Misc";

interface MemberDkpDraft {
  value: number | null;
  // True while the row mirrors the event's Default DKP. Goes false the moment
  // the officer types into the per-row input (matches the web's
  // data-follows-default attribute + inline sync script).
  followsDefault: boolean;
}

// Attendance snapshots used to be their own tab. They now render as two slices of the Event System
// tab — open events sit in "Current Field Activity" beside the live camps that produced them, and
// the review leftovers (unlinked snapshots, closed events) sit below the Pending Events queue.
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
  // Snapshot id -> the slot an officer picked while filing it. Ingest classifies nothing now, so
  // this choice is the whole filing decision.
  protected readonly attachSlots: Record<number, { kind: string; window: number | null }> = {};
  // Per-event, per-character DKP draft. Reset whenever the underlying combined
  // roster changes (handled lazily on read so newly-added/removed people
  // pick up sane defaults).
  protected readonly memberDkpDrafts: Record<number, Record<string, MemberDkpDraft>> = {};
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

  // "Window" / "Misc" are the STORAGE names (AttendanceSnapshotSlotKinds), and they told an
  // officer nothing about where a row came from. They map exactly onto the two ways attendance is
  // captured, so the chip says that instead. Mirrors Views/WindowEvents/_WindowEventCard.cshtml --
  // the two must move together.
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

  protected attachSlot(snapshot: ActivityWindowSnapshot): { kind: string; window: number | null } {
    this.attachSlots[snapshot.id] ??= { kind: SLOT_WINDOW, window: null };
    return this.attachSlots[snapshot.id];
  }

  protected setAttachSlotKind(snapshotId: number, kind: string): void {
    const slot = (this.attachSlots[snapshotId] ??= { kind: SLOT_WINDOW, window: null });
    slot.kind = kind;
  }

  protected setAttachSlotWindow(snapshotId: number, value: number | null): void {
    const slot = (this.attachSlots[snapshotId] ??= { kind: SLOT_WINDOW, window: null });
    slot.window = value;
  }

  protected readonly slotWindow = SLOT_WINDOW;
  protected readonly slotMisc = SLOT_MISC;

  // The two groups a card renders its captures in. Split server-side and mirrored here so the
  // Activity and the web cannot disagree about what counts as Misc.
  protected windowSnapshots(event: ActivityWindowEvent): ActivityWindowSnapshot[] {
    return event.snapshots.filter(s => !s.isMisc);
  }

  protected miscSnapshots(event: ActivityWindowEvent): ActivityWindowSnapshot[] {
    return event.snapshots.filter(s => s.isMisc);
  }

  // 1..N for the window picker. Built from the camp own grid rather than HnmConfig.MaxWindow so a
  // linkshell that configured a shorter cadence cannot be offered a window it never runs.
  protected windowNumbers(event: ActivityWindowEvent): number[] {
    const count = Math.max(1, event.windowCount || 1);
    return Array.from({ length: count }, (_, i) => i + 1);
  }

  // An ungridded camp (Sky gods, farm NMs) has no window numbers to show, so calling its captures
  // "Windows" would promise a distinction it does not have.
  protected windowGroupTitle(event: ActivityWindowEvent): string {
    return event.hasWindowGrid ? 'Windows' : 'Captures';
  }

  // Moves an already-filed capture between a numbered window and Misc, in place. The server
  // refuses it on a posted event: that would move members between the window and misc rates
  // after the DKP was already paid.
  protected async setSnapshotSlot(
    snapshot: ActivityWindowSnapshot, kind: string, windowNumber: number | null,
  ): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id) return;
    await this.windows.setSnapshotSlot(snapshot.id, id, kind, windowNumber);
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
    // A brand-new event starts as an ordinary window capture; an officer who meant Misc moves it
    // with the slot control on the card.
    await this.windows.attachSnapshot(snapshot.id, id, { name, createNew: true, slotKind: SLOT_WINDOW });
  }

  protected async attachExisting(snapshot: ActivityWindowSnapshot, windowEventId: number): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id || !windowEventId) return;
    const slot = this.attachSlot(snapshot);
    await this.windows.attachSnapshot(
      snapshot.id, id, { windowEventId, slotKind: slot.kind, windowNumber: slot.window });
  }

  // Persists the typed name onto the SNAPSHOT without creating or attaching anything. Blank is
  // allowed and clears it — an officer who mislabelled a capture should be able to undo that
  // without inventing a placeholder name.
  protected async renameSnapshot(snapshot: ActivityWindowSnapshot): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id) return;
    const name = this.attachName(snapshot).trim();
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
  //   "camp:<id>" a live camp -> attach BY THAT CAMP'S NAME
  //
  // The camp case goes through the name path on purpose. A snapshot belongs to a
  // WindowEvent, and the server groups snapshots into one by name within 24h --
  // which is how the addon's own posts for that camp are already filed. Attaching
  // by name therefore drops this snapshot into the SAME bucket its siblings went
  // to, whereas minting a fresh WindowEvent would split one camp's payroll in two.
  // It also means picking the camp is exactly equivalent to typing its name, just
  // without the chance of a typo.
  protected async attachToSelection(snapshot: ActivityWindowSnapshot, selection: string): Promise<void> {
    const linkshellId = this.primaryLinkshellId();
    if (!linkshellId || !selection) return;

    if (selection.startsWith('we:')) {
      const windowEventId = Number(selection.slice(3));
      if (!windowEventId) return;
      const slot = this.attachSlot(snapshot);
      await this.windows.attachSnapshot(
        snapshot.id, linkshellId, { windowEventId, slotKind: slot.kind, windowNumber: slot.window });
      return;
    }

    if (selection.startsWith('camp:')) {
      const campId = Number(selection.slice(5));
      const camp = this.liveHnmCamps().find(event => event.id === campId);
      const name = camp?.name?.trim();
      if (!name) return;
      // Both at once: the name files it for payroll the way the addon's own posts
      // for this camp are filed, and linkedEventId makes it show on the camp's card.
      const slot = this.attachSlot(snapshot);
      await this.windows.attachSnapshot(snapshot.id, linkshellId,
        { name, linkedEventId: campId, slotKind: slot.kind, windowNumber: slot.window });
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

  protected openTypeahead(snapshotId: number): void {
    this.openAddTypeahead.set(snapshotId);
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
      // Null is MEANINGFUL here, unlike amount: it means "misc pays what a window pays", which is
      // the default. Blanking the input is how an officer goes back to that.
      misc: event.miscDkpAmount ?? null
    };
    return this.dkpDrafts[event.id];
  }

  protected setMiscDkp(event: ActivityWindowEvent, value: number | null): void {
    this.dkpDraft(event).misc = value;
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

  // The Combined DKP column shows the live draft so officers see edits
  // (including default-driven cascades) reflected immediately.
  protected combinedDkpDisplay(event: ActivityWindowEvent, member: ActivityWindowCombinedMember): string {
    const draft = this.memberDkp(event, member);
    return this.formatDkp(draft.value);
  }

  private buildMemberDkpPayload(event: ActivityWindowEvent): ActivityWindowEventMemberDkpInput[] {
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
    return draft.amount !== null && draft.amount >= 0;
  }

  protected dkpActionsDisabled(event: ActivityWindowEvent): boolean {
    return this.windows.busy() || !this.dkpDraftValid(event);
  }

  protected async saveDkp(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const draft = this.dkpDraft(event);
    if (!id || !this.dkpDraftValid(event)) return;
    await this.windows.saveDkp(event.id, id, draft.amount!, draft.entryType, this.buildMemberDkpPayload(event), draft.misc);
  }

  // No native confirm — the explicit "Post to sheet" / "Update sheet" button
  // click IS the confirmation. Discord's iframe blocks window.confirm()
  // which silently returned false and dropped the action.
  protected async postToSheet(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const draft = this.dkpDraft(event);
    if (!id || !this.dkpDraftValid(event)) return;
    await this.windows.postToSheet(event.id, id, draft.amount!, draft.entryType, this.buildMemberDkpPayload(event), draft.misc);
  }

  protected async editPosted(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const draft = this.dkpDraft(event);
    if (!id || !this.dkpDraftValid(event)) return;
    await this.windows.editPosted(event.id, id, draft.amount!, draft.entryType, this.buildMemberDkpPayload(event), draft.misc);
  }
}
