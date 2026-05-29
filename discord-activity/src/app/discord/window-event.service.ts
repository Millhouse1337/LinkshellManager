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

  async load(linkshellId: number): Promise<void> {
    if (!linkshellId) {
      this.data.set(null);
      return;
    }

    this.busy.set(true);
    this.auth.setActionError(null);
    try {
      const result = await this.http.fetchActivityJson<ActivityWindowEventsResponse>(
        `/api/activity/window-events?linkshellId=${linkshellId}`
      );
      this.data.set(result);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading attendance events failed.'));
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

  async attachSnapshot(snapshotId: number, linkshellId: number, input: { windowEventId?: number | null; name?: string | null }): Promise<void> {
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
