import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type { ActivityPendingSubmissions, ActivityPendingTodEdits } from './discord-activity.types';

// The website-only review queue: ToDs, attendance windows and snapshots that members submitted
// for approval (ActivityDataController.PendingSubmissions). Every approve / reject reloads the
// queue, and the overview too, since an approved ToD or snapshot shows up across the app.
@Injectable({ providedIn: 'root' })
export class PendingSubmissionsService {
  private readonly http = inject(ActivityHttpClient);
  private readonly auth = inject(AuthService);

  // Remembers its linkshell, so a queue loaded for one linkshell is never shown for another.
  private readonly loaded = signal<{ linkshellId: number; queue: ActivityPendingSubmissions } | null>(null);
  readonly loading = signal(false);
  // Which card is mid-action, e.g. 'tod-12', so only its buttons disable.
  readonly busyKey = signal<string | null>(null);

  queueFor(linkshellId: number): ActivityPendingSubmissions | null {
    const loaded = this.loaded();
    return loaded?.linkshellId === linkshellId ? loaded.queue : null;
  }

  async load(linkshellId: number): Promise<void> {
    this.loading.set(true);
    try {
      const queue = await this.http.fetchActivityJson<ActivityPendingSubmissions>(
        `/api/activity/linkshells/${linkshellId}/pending-submissions`);
      this.loaded.set({ linkshellId, queue });
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the pending submissions failed.'));
    } finally {
      this.loading.set(false);
    }
  }

  approveTod(linkshellId: number, id: number, edits: ActivityPendingTodEdits): Promise<void> {
    return this.act(linkshellId, `tod-${id}`, `tod/${id}/approve`, edits, 'Approving the ToD failed.');
  }

  rejectTod(linkshellId: number, id: number): Promise<void> {
    return this.act(linkshellId, `tod-${id}`, `tod/${id}/reject`, {}, 'Rejecting the ToD failed.');
  }

  approveWindow(linkshellId: number, id: number, keepIds: number[]): Promise<void> {
    return this.act(linkshellId, `window-${id}`, `attendance-window/${id}/approve`, { keepIds }, 'Approving the window failed.');
  }

  rejectWindow(linkshellId: number, id: number): Promise<void> {
    return this.act(linkshellId, `window-${id}`, `attendance-window/${id}/reject`, {}, 'Rejecting the window failed.');
  }

  approveSnapshot(linkshellId: number, id: number, keepIds: number[]): Promise<void> {
    return this.act(linkshellId, `snapshot-${id}`, `attendance-snapshot/${id}/approve`, { keepIds }, 'Approving the snapshot failed.');
  }

  rejectSnapshot(linkshellId: number, id: number): Promise<void> {
    return this.act(linkshellId, `snapshot-${id}`, `attendance-snapshot/${id}/reject`, {}, 'Rejecting the snapshot failed.');
  }

  private async act(linkshellId: number, key: string, path: string, body: object, failure: string): Promise<void> {
    this.busyKey.set(key);
    this.auth.clearActionState();
    try {
      const result = await this.http.postActivityJson<{ message: string }>(
        `/api/activity/linkshells/${linkshellId}/pending-submissions/${path}`, body);
      this.auth.setActionMessage(result.message);
      await this.auth.refreshOverview();
    } catch (error) {
      this.auth.setActionError(formatActionError(error, failure));
    } finally {
      this.busyKey.set(null);
    }
    // Also after a failure: "already reviewed" means the queue moved on without us.
    await this.load(linkshellId);
  }
}
