import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityCreateTodInput,
  ActivityUpcomingRepop,
  ActivityUpdateTodInput
} from './discord-activity.types';

@Injectable({ providedIn: 'root' })
export class TodService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly busyTodId = signal<number | null>(null);
  readonly busyTodSave = signal(false);

  async createTod(input: ActivityCreateTodInput): Promise<void> {
    this.busyTodSave.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      // Use postActivityJson so we can inspect the {pending} flag the server
      // returns when the caller has only CanSubmitTodForApproval (member-tier).
      const response = await this.http.postActivityJson<{ pending?: boolean }>('/api/activity/tods', {
        linkshellId: input.linkshellId,
        monsterName: input.monsterName,
        dayNumber: input.dayNumber ?? null,
        popWindow: input.popWindow ?? null,
        hq: input.hq,
        additionalSeconds: input.additionalSeconds,
        claim: input.claim,
        timeLocal: input.timeLocal,
        cooldown: input.cooldown || null,
        interval: input.interval || null,
        noLoot: input.noLoot,
        // NULL, not an empty array. Loot can no longer be entered from a ToD, and on update the
        // server reads null as "leave the loot alone" -- an empty array means "replace with
        // nothing", which would refund and delete the loot on any legacy ToD an officer edited.
        lootDetails: input.lootDetails.length > 0
          ? input.lootDetails.map(detail => ({
              itemName: detail.itemName || null,
              itemWinner: detail.itemWinner || null,
              winningDkpSpent: detail.winningDkpSpent ?? null
            }))
          : null,
        imagePath: input.imagePath ?? null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage(response?.pending
        ? 'ToD submitted for officer approval.'
        : 'ToD entry saved.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Saving the ToD entry failed.'));
      throw error;
    } finally {
      this.busyTodSave.set(false);
    }
  }

  // The pops this linkshell is still waiting on, newest ToD per spawn. Read when the
  // create-event form opens so picking an HNM can pre-fill Start with its predicted repop.
  // Failures are swallowed to an empty list: this only feeds a convenience pre-fill, and a
  // red banner over a form the officer can still fill in by hand would be noise.
  async loadUpcomingRepops(linkshellId: number): Promise<ActivityUpcomingRepop[]> {
    if (!linkshellId) {
      return [];
    }

    try {
      const payload = await this.http.fetchActivityJson<{ entries?: ActivityUpcomingRepop[] }>(
        `/api/activity/linkshells/${linkshellId}/upcoming-repops`);
      return payload?.entries ?? [];
    } catch {
      return [];
    }
  }

  async updateTod(input: ActivityUpdateTodInput): Promise<void> {
    this.busyTodSave.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/tods/${input.todId}/update`, {
        monsterName: input.monsterName,
        dayNumber: input.dayNumber ?? null,
        popWindow: input.popWindow ?? null,
        hq: input.hq,
        additionalSeconds: input.additionalSeconds,
        claim: input.claim,
        timeLocal: input.timeLocal,
        cooldown: input.cooldown || null,
        interval: input.interval || null,
        noLoot: input.noLoot,
        // NULL, not an empty array. Loot can no longer be entered from a ToD, and on update the
        // server reads null as "leave the loot alone" -- an empty array means "replace with
        // nothing", which would refund and delete the loot on any legacy ToD an officer edited.
        lootDetails: input.lootDetails.length > 0
          ? input.lootDetails.map(detail => ({
              itemName: detail.itemName || null,
              itemWinner: detail.itemWinner || null,
              winningDkpSpent: detail.winningDkpSpent ?? null
            }))
          : null,
        imagePath: input.imagePath ?? null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('ToD entry updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the ToD entry failed.'));
      throw error;
    } finally {
      this.busyTodSave.set(false);
    }
  }

  // Log (or edit) the ToD from an HNM signup board's Post/Edit ToD button. The server
  // also moves the event's StartTime to the repop, wipes the board's signups, marks it
  // defeated, and replaces the Discord message with a defeated note.
  async postBoardTod(eventId: number, fields: {
    timeLocal: string;
    cooldown: string | null;
    interval: string | null;
    dayNumber: number | null;
    claim: boolean | null;
    hq: boolean;
    additionalSeconds: number;
    // End Camp only: the window it popped on (caps credit) + whether it was killed. null = current
    // window / server default.
    popWindow?: number | null;
    killed?: boolean | null;
    // End Camp only: re-post the board before the next pop? (null = leave standing config alone) and
    // how many hours before the pop.
    repost?: boolean | null;
    repostLeadHours?: number | null;
    // End Camp only: the optional kill screenshot. null/omitted leaves any existing image alone.
    imagePath?: string | null;
  }): Promise<void> {
    this.busyTodSave.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/events/${eventId}/post-tod`, {
        timeLocal: fields.timeLocal,
        cooldown: fields.cooldown || null,
        interval: fields.interval || null,
        dayNumber: fields.dayNumber ?? null,
        claim: fields.claim,
        hq: fields.hq,
        additionalSeconds: fields.additionalSeconds,
        popWindow: fields.popWindow ?? null,
        killed: fields.killed ?? null,
        repost: fields.repost ?? null,
        repostLeadHours: fields.repostLeadHours ?? null,
        imagePath: fields.imagePath ?? null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Time of Death posted — the board re-posts automatically before the next pop.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Posting the Time of Death failed.'));
      throw error;
    } finally {
      this.busyTodSave.set(false);
    }
  }

  async deleteTod(todId: number): Promise<void> {
    this.busyTodId.set(todId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/tods/${todId}/delete`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('ToD entry deleted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the ToD entry failed.'));
      throw error;
    } finally {
      this.busyTodId.set(null);
    }
  }

  async uploadTodImage(file: File): Promise<string | null> {
    this.auth.setActionError(null);
    try {
      const accessToken = this.auth.currentAccessToken();
      const formData = new FormData();
      formData.append('file', file);

      const headers: Record<string, string> = {};
      if (accessToken) {
        headers['Authorization'] = `Bearer ${accessToken}`;
      }

      const response = await fetch('/api/activity/tods/upload-image', {
        method: 'POST',
        body: formData,
        credentials: 'include',
        headers
      });

      if (!response.ok) {
        const text = await response.text();
        throw new Error(text || 'Upload failed');
      }

      const payload = await response.json() as { imagePath?: string };
      return payload.imagePath ?? null;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Uploading the ToD image failed.'));
      return null;
    }
  }
}
