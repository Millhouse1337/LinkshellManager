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

  // Force a refetch. Used after every write and by the manual Refresh button.
  async load(linkshellId: number): Promise<void> {
    if (!linkshellId) {
      this.data.set(null);
      this.loadedFor = 0;
      return;
    }
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

  private async loadCore(linkshellId: number): Promise<void> {
    this.busy.set(true);
    this.auth.setActionError(null);
    try {
      const result = await this.http.fetchActivityJson<ActivityWindowEventsResponse>(
        `/api/activity/window-events?linkshellId=${linkshellId}`
      );
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
    },
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/attach`, input, 'Snapshot attached.');
  }

  async setSnapshotStatus(snapshotId: number, linkshellId: number, status: string): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/status`, { status }, 'Snapshot updated.');
  }

  // DKP posting (set amount + entry type + per-character overrides, then
  // push/reconcile the AttInput tab).
  async saveDkp(
    windowEventId: number, linkshellId: number, dkpAmount: number, entryType: string,
    memberDkp?: ActivityWindowEventMemberDkpInput[]
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/save-dkp`,
      { dkpAmount, entryType, memberDkp }, 'DKP details saved.');
  }

  async postToSheet(
    windowEventId: number, linkshellId: number, dkpAmount: number, entryType: string,
    memberDkp?: ActivityWindowEventMemberDkpInput[]
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/post`,
      { dkpAmount, entryType, memberDkp }, 'Posting to the DKP sheet...');
  }

  async editPosted(
    windowEventId: number, linkshellId: number, dkpAmount: number, entryType: string,
    memberDkp?: ActivityWindowEventMemberDkpInput[]
  ): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/edit-posted`,
      { dkpAmount, entryType, memberDkp }, 'Updating the DKP sheet...');
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
