import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type { ActivityWindowEventsResponse } from './discord-activity.types';

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
      this.auth.setActionError(formatActionError(error, 'Loading Window Events failed.'));
    } finally {
      this.busy.set(false);
    }
  }

  async rename(windowEventId: number, name: string, linkshellId: number): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/rename`, { name }, 'Window Event renamed.');
  }

  async close(windowEventId: number, linkshellId: number): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/close`, undefined, 'Window Event closed.');
  }

  async reopen(windowEventId: number, linkshellId: number): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/${windowEventId}/reopen`, undefined, 'Window Event reopened.');
  }

  async attachSnapshot(snapshotId: number, linkshellId: number, input: { windowEventId?: number | null; name?: string | null }): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/attach`, input, 'Snapshot attached.');
  }

  async setSnapshotStatus(snapshotId: number, linkshellId: number, status: string): Promise<void> {
    await this.run(linkshellId, `/api/activity/window-events/snapshots/${snapshotId}/status`, { status }, 'Snapshot updated.');
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
      this.auth.setActionError(formatActionError(error, 'Updating Window Events failed.'));
      throw error;
    } finally {
      this.busy.set(false);
    }
  }
}
