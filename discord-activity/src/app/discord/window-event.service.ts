import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityAddSnapshotEntryInput,
  ActivityWindowEventMemberDkpInput,
  ActivityWindowEventsResponse
} from './discord-activity.types';

@Injectable({ providedIn: 'root' })
export class WindowEventService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly data = signal<ActivityWindowEventsResponse | null>(null);
  readonly busy = signal(false);

  // Linkshell whose payload is currently in `data` (0 = none yet). Lets ensureLoaded/loadIfStale
  // tell "already have it" from "different linkshell" after a switch.
  private loadedFor = 0;
  private loadedAt = 0;
  private inFlight: Promise<void> | null = null;

  // ----- Attendance Archive search + paging -----
  //
  // Server-side, and held HERE rather than in the component, because the archive is paged by the
  // endpoint: a client-side filter could only ever search the one page it was handed, so a hit on
  // an older event would silently read as "no results". Keeping it on the service also means the
  // 5s poll and every write's refetch re-issue the officer's current query instead of resetting it.
  readonly closedQuery = signal('');
  readonly closedPage = signal(1);

  // Force a refetch. Used after every write and by the manual Refresh button.
  async load(linkshellId: number): Promise<void> {
    if (!linkshellId) {
      this.data.set(null);
      this.loadedFor = 0;
      this.resetClosedPaging();
      return;
    }
    // A different linkshell's archive is a different list, so neither the query nor the page
    // carries over — page 3 of the old one is very likely past the end of the new one.
    if (this.loadedFor && this.loadedFor !== linkshellId) this.resetClosedPaging();
    // Since attendance moved into the Event System tab there are three callers that can land in the
    // same frame — the tab's first-paint effect, the refresh timer, and a write's refetch. Share one
    // request rather than firing three.
    this.inFlight ??= this.loadCore(linkshellId);
    await this.inFlight;
  }

  // First paint. No-op when this linkshell's payload is already loaded or in flight.
  async ensureLoaded(linkshellId: number): Promise<void> {
    if (!linkshellId || this.loadedFor === linkshellId || this.inFlight) return;
    await this.load(linkshellId);
  }

  // Background polling. This payload is fat — every open AND closed event with all their snapshots,
  // entries and combined rosters — and the Event System tab ticks at 5s while a camp is live, so
  // cap how often the timer may actually refetch it.
  async loadIfStale(linkshellId: number, maxAgeMs = 10_000): Promise<void> {
    if (!linkshellId) return;
    if (this.loadedFor === linkshellId && Date.now() - this.loadedAt < maxAgeMs) return;
    await this.load(linkshellId);
  }

  // Run the archive's search from page 1. Any page but the first is meaningless against a
  // different result set.
  async searchClosed(linkshellId: number, query: string): Promise<void> {
    this.closedQuery.set((query ?? '').trim());
    this.closedPage.set(1);
    await this.reloadArchive(linkshellId);
  }

  async goToClosedPage(linkshellId: number, page: number): Promise<void> {
    this.closedPage.set(Math.max(1, page));
    await this.reloadArchive(linkshellId);
  }

  // Refetch with the archive state as it stands NOW. Deliberately does not join an in-flight
  // request the way load() does: that one was issued with the previous query/page, so joining it
  // would land a stale payload as the answer to this search and leave the input disagreeing with
  // the cards under it.
  private async reloadArchive(linkshellId: number): Promise<void> {
    if (!linkshellId) return;
    // Let a poll already on the wire settle first, or its late response would overwrite ours.
    const pending = this.inFlight;
    if (pending) await pending.catch(() => undefined);
    await this.load(linkshellId);
  }

  private resetClosedPaging(): void {
    this.closedQuery.set('');
    this.closedPage.set(1);
  }

  private async loadCore(linkshellId: number): Promise<void> {
    this.busy.set(true);
    this.auth.setActionError(null);
    try {
      const query = this.closedQuery();
      const result = await this.http.fetchActivityJson<ActivityWindowEventsResponse>(
        `/api/activity/window-events?linkshellId=${linkshellId}`
        + `&attQ=${encodeURIComponent(query)}&attPage=${this.closedPage()}`
      );
      // The server clamps the page to the result set, so mirror its answer back into our state —
      // otherwise a page that no longer exists (a search narrowed the archive, or an event was
      // deleted) stays in the pager and every later request asks for it again.
      this.closedPage.set(result.closedPage);
      this.data.set(result);
      this.loadedFor = linkshellId;
      this.loadedAt = Date.now();
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading attendance events failed.'));
    } finally {
      this.busy.set(false);
      this.inFlight = null;
    }
  }

  async rename(windowEventId: number, name: string, linkshellId: number): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/rename`, { name }, 'Attendance event renamed.');
  }

  async close(windowEventId: number, linkshellId: number): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/close`, undefined, 'Attendance event closed.');
  }

  async reopen(windowEventId: number, linkshellId: number): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/reopen`, undefined, 'Attendance event reopened.');
  }

  // windowEventId attaches to an existing attendance event, name find-or-creates one.
  // linkedEventId is separate from both: it records which CAMP the snapshot belongs to so the
  // camp's own card can show it. Picking a live camp in the UI sends name + linkedEventId
  // together — the name groups it for payroll, the link puts it on the camp.
  // createNew forces a brand-new attendance event rather than folding into an open one of the
  // same name — what "Create New Event" means, as against the dropdown's attach-to-existing.
  async attachSnapshot(
    snapshotId: number,
    linkshellId: number,
    input: {
      windowEventId?: number | null;
      name?: string | null;
      linkedEventId?: number | null;
      createNew?: boolean;
      // The filing decision. Ingest classifies nothing now, so this is where a capture first
      // becomes a window post or a misc post.
      slotKind?: string | null;
      windowNumber?: number | null;
    },
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/attach`, input, 'Snapshot attached.');
  }

  async setSnapshotStatus(snapshotId: number, linkshellId: number, status: string): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/status`, { status }, 'Snapshot updated.');
  }

  // Renames the SNAPSHOT, and nothing else. Distinct from attachSnapshot's `name`, which
  // find-or-creates an attendance event to file it under.
  async renameSnapshot(snapshotId: number, linkshellId: number, name: string | null): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/rename`,
      { name }, 'Snapshot renamed.');
  }

  // Corrects the alliance a poster claimed. It cannot be detected in game — the client only sees
  // your own alliance — so it is typed at a pop and is the field most likely to arrive wrong.
  async setSnapshotAlliance(snapshotId: number, linkshellId: number, allianceNumber: number): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/alliance`,
      { allianceNumber }, 'Snapshot alliance updated.');
  }

  // Moves an already-filed capture between a numbered window and Misc, without detaching it.
  // Filing is entirely manual now, so mis-filing is routine rather than exceptional.
  async setSnapshotSlot(
    snapshotId: number, linkshellId: number, slotKind: string, windowNumber?: number | null,
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots//slot`,
      { slotKind, windowNumber: windowNumber ?? null }, "Snapshot moved.");
  }

  // Confirm (verified: true) or Reject (verified: false) a member-posted capture. Confirming is
  // what puts its members into the combined roster and therefore into the payout.
  async verifySnapshot(snapshotId: number, linkshellId: number, verified: boolean): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/verify`,
      { verified }, verified ? 'Snapshot confirmed.' : 'Snapshot rejected.');
  }

  // DKP posting (set amount + entry type + per-character overrides, then
  // push/reconcile the AttInput tab).
  async saveDkp(
    windowEventId: number, linkshellId: number, dkpAmount: number, entryType: string,
    memberDkp?: ActivityWindowEventMemberDkpInput[], miscDkpAmount?: number | null
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events//save-dkp`,
      { dkpAmount, entryType, memberDkp, miscDkpAmount: miscDkpAmount ?? null }, "DKP details saved.");
  }

  async postToSheet(
    windowEventId: number, linkshellId: number, dkpAmount: number, entryType: string,
    memberDkp?: ActivityWindowEventMemberDkpInput[], miscDkpAmount?: number | null
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events//post`,
      { dkpAmount, entryType, memberDkp, miscDkpAmount: miscDkpAmount ?? null }, "Posting to the DKP sheet...");
  }

  async editPosted(
    windowEventId: number, linkshellId: number, dkpAmount: number, entryType: string,
    memberDkp?: ActivityWindowEventMemberDkpInput[], miscDkpAmount?: number | null
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events//edit-posted`,
      { dkpAmount, entryType, memberDkp, miscDkpAmount: miscDkpAmount ?? null }, "Updating the DKP sheet...");
  }

  async deleteEvent(windowEventId: number, linkshellId: number): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/delete`,
      undefined, 'Attendance event deleted.');
  }

  async deleteSnapshot(snapshotId: number, linkshellId: number): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/delete`,
      undefined, 'Snapshot deleted.');
  }

  async addSnapshotEntry(
    snapshotId: number, linkshellId: number, input: ActivityAddSnapshotEntryInput
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/entries`,
      input, 'Added person to the snapshot.');
  }

  async deleteSnapshotEntry(
    snapshotId: number, entryId: number, linkshellId: number
  ): Promise<void> {
    await this.run(
      linkshellId,
      `/api/activity/window-events/snapshots/${snapshotId}/entries/${entryId}/delete`,
      undefined,
      'Removed person from the snapshot.');
  }

  private async run(linkshellId: number, path: string, body: unknown, message: string): Promise<void> {
    this.busy.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(path, body);
      await this.load(linkshellId);
      this.auth.setActionMessage(message);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating attendance events failed.'));
      throw error;
    } finally {
      this.busy.set(false);
    }
  }
}
