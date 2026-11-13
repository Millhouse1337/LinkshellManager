import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import { mutateThenRefresh } from './mutate-then-refresh';
import type {
  ActivityAddSnapshotEntryInput,
  ActivityEventAddMemberCandidate,
  ActivityWindowEventCaptureDkpInput,
  ActivityWindowEventMemberDkpInput,
  ActivityWindowEventsResponse
} from './discord-activity.types';
import { ResponseOrder } from './refresh-order';

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
  // Linkshell the in-flight request is for, so a request for the linkshell the user just left is
  // never shared with a caller asking for the new one.
  private inFlightFor = 0;
  // Only the newest reply for the current linkshell is shown (see refresh-order.ts).
  private readonly order = new ResponseOrder();

  // ----- Attendance Archive search + paging -----
  //
  // Server-side, and held HERE rather than in the component, because the archive is paged by the
  // endpoint: a client-side filter could only ever search the one page it was handed, so a hit on
  // an older event would silently read as "no results". Keeping it on the service also means the
  // 5s poll and every write's refetch re-issue the officer's current query instead of resetting it.
  readonly closedQuery = signal('');
  readonly closedPage = signal(1);

  // Fetch now, or share a fetch for the same linkshell already on the wire. Behind first paint
  // (ensureLoaded) and the refresh timer (loadIfStale); writes and archive searches use reloadFresh.
  async load(linkshellId: number): Promise<void> {
    if (!linkshellId) {
      this.order.invalidate();
      this.data.set(null);
      this.loadedFor = 0;
      this.resetClosedPaging();
      return;
    }
    // A different linkshell's archive is a different list, so neither the query nor the page
    // carries over — page 3 of the old one is very likely past the end of the new one.
    if (this.loadedFor && this.loadedFor !== linkshellId) this.resetClosedPaging();
    // Since attendance moved into the Event System tab there are callers that can land in the same
    // frame — the tab's first-paint effect and the refresh timer. Share one request for the same
    // linkshell rather than firing two.
    if (this.inFlight && this.inFlightFor === linkshellId) {
      await this.inFlight;
      return;
    }
    await this.startLoad(linkshellId);
  }

  // First paint. No-op when this linkshell's payload is already loaded or in flight.
  async ensureLoaded(linkshellId: number): Promise<void> {
    if (!linkshellId || this.loadedFor === linkshellId || this.inFlightFor === linkshellId) return;
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
    await this.reloadFresh(linkshellId);
  }

  async goToClosedPage(linkshellId: number, page: number): Promise<void> {
    this.closedPage.set(Math.max(1, page));
    await this.reloadFresh(linkshellId);
  }

  // Refetch with the state as it stands NOW. Deliberately does not join an in-flight request the
  // way load() does: that one was issued BEFORE this search, page change or write, so joining it
  // would land a stale payload as the answer (the search input disagreeing with the cards under
  // it, or a change the officer just saved seeming to vanish until the next poll).
  private async reloadFresh(linkshellId: number): Promise<void> {
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

  private startLoad(linkshellId: number): Promise<void> {
    const request: Promise<void> = this.loadCore(linkshellId).finally(() => {
      // Only clear the slot if it still holds THIS request; a newer one may have replaced it.
      if (this.inFlight === request) {
        this.inFlight = null;
        this.inFlightFor = 0;
      }
    });
    this.inFlight = request;
    this.inFlightFor = linkshellId;
    return request;
  }

  // A background load: it reports a failure in its own banner (auth.reportLoadError) and never
  // touches the action banner, so a poll can neither erase a real error nor invent one.
  private async loadCore(linkshellId: number): Promise<void> {
    this.busy.set(true);
    try {
      const ticket = this.order.begin(linkshellId);
      const query = this.closedQuery();
      const result = await this.http.fetchActivityJson<ActivityWindowEventsResponse>(
        `/api/activity/window-events?linkshellId=${linkshellId}`
        + `&attQ=${encodeURIComponent(query)}&attPage=${this.closedPage()}`
      );
      if (!this.order.accept(ticket, linkshellId)) return;
      // The server clamps the page to the result set, so mirror its answer back into our state —
      // otherwise a page that no longer exists (a search narrowed the archive, or an event was
      // deleted) stays in the pager and every later request asks for it again.
      this.closedPage.set(result.closedPage);
      this.data.set(result);
      this.loadedFor = linkshellId;
      this.loadedAt = Date.now();
      this.auth.clearLoadError('window-events');
    } catch (error) {
      this.auth.reportLoadError('window-events', error, 'Loading attendance events failed.');
    } finally {
      this.busy.set(false);
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
      // No slot: the server files every capture as a misc post.
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

  // Confirm (verified: true) or Reject (verified: false) a member-posted capture. Confirming is
  // what puts its members into the combined roster and therefore into the payout.
  async verifySnapshot(snapshotId: number, linkshellId: number, verified: boolean): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/verify`,
      { verified }, verified ? 'Snapshot confirmed.' : 'Snapshot rejected.');
  }

  // DKP posting (set amount + entry type + per-character overrides, then
  // push/reconcile the AttInput tab).
  //
  // Every path here interpolates its id -- the server routes are
  // window-events/{windowEventId}/... and reject anything else. All three of these had lost
  // their ${windowEventId} and were posting to "window-events//post", which 404s. The button
  // did nothing at all, which is exactly what a dropped id looks like from the outside: no
  // error on screen, no row in the ledger, nothing in the server log because the request never
  // reached a handler.
  async saveDkp(
    windowEventId: number, linkshellId: number, dkpAmount: number | null, entryType: string,
    memberDkp?: ActivityWindowEventMemberDkpInput[], miscDkpAmount?: number | null,
    captureDkp?: ActivityWindowEventCaptureDkpInput[]
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/save-dkp`,
      { dkpAmount, entryType, memberDkp, miscDkpAmount: miscDkpAmount ?? null, captureDkp }, "DKP details saved.");
  }

  // A live LSM Now event: keeps the card's DKP (as Save does) and moves it to Events Pending DKP
  // Post, where it is reviewed and posted to the sheet.
  async sendToReview(
    windowEventId: number, linkshellId: number, dkpAmount: number | null, entryType: string,
    memberDkp?: ActivityWindowEventMemberDkpInput[], miscDkpAmount?: number | null,
    captureDkp?: ActivityWindowEventCaptureDkpInput[]
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/send-to-review`,
      { dkpAmount, entryType, memberDkp, miscDkpAmount: miscDkpAmount ?? null, captureDkp },
      'Sent to review. It is now under Events Pending DKP Post.');
  }

  async postToSheet(
    windowEventId: number, linkshellId: number, dkpAmount: number | null, entryType: string,
    memberDkp?: ActivityWindowEventMemberDkpInput[], miscDkpAmount?: number | null,
    captureDkp?: ActivityWindowEventCaptureDkpInput[]
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/post`,
      { dkpAmount, entryType, memberDkp, miscDkpAmount: miscDkpAmount ?? null, captureDkp }, "Posting to the DKP sheet...");
  }

  async editPosted(
    windowEventId: number, linkshellId: number, dkpAmount: number | null, entryType: string,
    memberDkp?: ActivityWindowEventMemberDkpInput[], miscDkpAmount?: number | null,
    captureDkp?: ActivityWindowEventCaptureDkpInput[]
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/edit-posted`,
      { dkpAmount, entryType, memberDkp, miscDkpAmount: miscDkpAmount ?? null, captureDkp }, "Updating the DKP sheet...");
  }

  // Pays the people on a posted event who registered after it was posted. The server skips anyone
  // already paid from it, so a second press pays nobody.
  async payRegistered(windowEventId: number, linkshellId: number): Promise<void> {
    this.busy.set(true);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityJson<{ paid: number }>(
          `/api/activity/window-events/${windowEventId}/pay-registered`),
        refresh: () => this.reloadFresh(linkshellId),
        success: result => {
          const paid = result?.paid ?? 0;
          return paid === 0
            ? 'Nobody new to pay. Everyone registered on this event is already paid.'
            : `Paid ${paid} newly registered member${paid === 1 ? '' : 's'}.`;
        },
        failure: 'Paying newly registered members failed.'
      });
    } finally {
      this.busy.set(false);
    }
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

  // The linkshell roster, for a card's New Post member picker. [] on failure (the error is shown).
  async loadMemberCandidates(windowEventId: number): Promise<ActivityEventAddMemberCandidate[]> {
    this.auth.setActionError(null);
    try {
      return await this.http.fetchActivityJson<ActivityEventAddMemberCandidate[]>(
        `/api/activity/window-events/${windowEventId}/member-candidates`);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading linkshell members failed.'));
      return [];
    }
  }

  // New Post on a card not yet posted to the DKP sheet: one Misc post with the picked members.
  // dkpAmount null = the linkshell's Misc post rate.
  async addMiscPost(
    windowEventId: number, linkshellId: number,
    name: string | null, appUserIds: string[], dkpAmount: number | null
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/misc-posts`,
      { name, appUserIds, dkpAmount }, 'Misc post added.');
  }

  private async run(linkshellId: number, path: string, body: unknown, message: string): Promise<void> {
    this.busy.set(true);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(path, body),
        // A poll already on the wire was sent before this write, so wait for it and fetch again
        // rather than sharing its now-stale answer.
        refresh: () => this.reloadFresh(linkshellId),
        success: message,
        failure: 'Updating attendance events failed.'
      });
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busy.set(false);
    }
  }
}
