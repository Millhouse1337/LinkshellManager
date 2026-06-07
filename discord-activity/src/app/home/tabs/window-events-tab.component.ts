import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { WindowEventService } from '../../discord/window-event.service';
import type {
  ActivityWindowCombinedMember,
  ActivityWindowEvent,
  ActivityWindowEventMemberDkpInput,
  ActivityWindowSnapshot,
  ActivityWindowSnapshotEntry
} from '../../discord/discord-activity.types';

// Baseline DKP per member when neither a saved override nor an event default
// exists yet. Mirrors the web view (`memberDkpDefault = Model.DkpAmount ?? 1.5`).
const FALLBACK_DKP = 1.5;

interface MemberDkpDraft {
  value: number | null;
  // True while the row mirrors the event's Default DKP. Goes false the moment
  // the officer types into the per-row input (matches the web's
  // data-follows-default attribute + inline sync script).
  followsDefault: boolean;
}

@Component({
  selector: 'app-window-events-tab',
  imports: [CommonModule, FormsModule],
  templateUrl: './window-events-tab.component.html',
  styleUrl: './window-events-tab.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class WindowEventsTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly windows = inject(WindowEventService);
  protected readonly attachNames: Record<number, string> = {};
  protected readonly renameDrafts: Record<number, string> = {};
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

  constructor() {
    effect(() => {
      const id = this.primaryLinkshellId();
      if (id) queueMicrotask(() => void this.windows.load(id));
    });
  }

  protected primaryLinkshellId(): number {
    return this.activity.overview()?.primaryLinkshell?.id ?? this.activity.overview()?.appUser?.primaryLinkshellId ?? 0;
  }

  protected data() {
    return this.windows.data();
  }

  protected rosterNames(): string[] {
    return this.data()?.rosterCharacterNames ?? [];
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

  protected renameDraft(event: ActivityWindowEvent): string {
    this.renameDrafts[event.id] ??= event.name ?? '';
    return this.renameDrafts[event.id];
  }

  protected setRenameDraft(eventId: number, value: string): void {
    this.renameDrafts[eventId] = value;
  }

  protected attachName(snapshot: ActivityWindowSnapshot): string {
    this.attachNames[snapshot.id] ??= snapshot.name ?? '';
    return this.attachNames[snapshot.id];
  }

  protected setAttachName(snapshotId: number, value: string): void {
    this.attachNames[snapshotId] = value;
  }

  protected async rename(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const name = (this.renameDrafts[event.id] ?? event.name ?? '').trim();
    if (!id || !name) return;
    await this.windows.rename(event.id, name, id);
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

  protected async attachByName(snapshot: ActivityWindowSnapshot): Promise<void> {
    const id = this.primaryLinkshellId();
    const name = this.attachName(snapshot).trim();
    if (!id || !name) return;
    await this.windows.attachSnapshot(snapshot.id, id, { name });
  }

  protected async attachExisting(snapshot: ActivityWindowSnapshot, windowEventId: number): Promise<void> {
    const id = this.primaryLinkshellId();
    if (!id || !windowEventId) return;
    await this.windows.attachSnapshot(snapshot.id, id, { windowEventId });
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
    return matches.slice(0, WindowEventsTabComponent.MAX_TYPEAHEAD_RESULTS);
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

  protected readonly dkpDrafts: Record<number, { amount: number | null; entryType: string }> = {};

  protected entryTypeOptions(): string[] {
    return this.data()?.entryTypeOptions ?? [];
  }

  protected isPosted(event: ActivityWindowEvent): boolean {
    return !!event.postedToSheetUtc;
  }

  protected dkpDraft(event: ActivityWindowEvent): { amount: number | null; entryType: string } {
    this.dkpDrafts[event.id] ??= {
      amount: event.dkpAmount ?? null,
      entryType: event.entryType ?? ''
    };
    return this.dkpDrafts[event.id];
  }

  protected setDkpAmount(event: ActivityWindowEvent, value: number | null): void {
    const draft = (this.dkpDrafts[event.id] ??= { amount: null, entryType: '' });
    draft.amount = value;
    // Cascade the new default into every per-row override still set to
    // "follow default" (matches the web's inline sync script).
    const members = this.memberDkpDrafts[event.id];
    if (members) {
      for (const name of Object.keys(members)) {
        const row = members[name];
        if (row.followsDefault) row.value = value;
      }
    }
  }

  protected setDkpEntryType(eventId: number, value: string): void {
    (this.dkpDrafts[eventId] ??= { amount: null, entryType: '' }).entryType = value;
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
    return draft.amount !== null && draft.amount >= 0 && !!draft.entryType;
  }

  protected dkpActionsDisabled(event: ActivityWindowEvent): boolean {
    return this.windows.busy() || !this.dkpDraftValid(event);
  }

  protected async saveDkp(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const draft = this.dkpDraft(event);
    if (!id || !this.dkpDraftValid(event)) return;
    await this.windows.saveDkp(event.id, id, draft.amount!, draft.entryType, this.buildMemberDkpPayload(event));
  }

  // No native confirm — the explicit "Post to sheet" / "Update sheet" button
  // click IS the confirmation. Discord's iframe blocks window.confirm()
  // which silently returned false and dropped the action.
  protected async postToSheet(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const draft = this.dkpDraft(event);
    if (!id || !this.dkpDraftValid(event)) return;
    await this.windows.postToSheet(event.id, id, draft.amount!, draft.entryType, this.buildMemberDkpPayload(event));
  }

  protected async editPosted(event: ActivityWindowEvent): Promise<void> {
    const id = this.primaryLinkshellId();
    const draft = this.dkpDraft(event);
    if (!id || !this.dkpDraftValid(event)) return;
    await this.windows.editPosted(event.id, id, draft.amount!, draft.entryType, this.buildMemberDkpPayload(event));
  }
}
