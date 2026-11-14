import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityCreateTodInput,
  ActivityLatestTod,
  ActivityLinkedTod,
  ActivityUpcomingRepop,
  ActivityUpdateTodInput
} from './discord-activity.types';
import { mutateThenRefresh } from './mutate-then-refresh';

@Injectable({ providedIn: 'root' })
export class TodService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly busyTodId = signal<number | null>(null);
  readonly busyTodSave = signal(false);

  async createTod(input: ActivityCreateTodInput): Promise<void> {
    this.busyTodSave.set(true);

    try {
      const outcome = await mutateThenRefresh(this.auth, {
        // Use postActivityJson so we can inspect the {pending} flag the server
        // returns when the caller has only CanSubmitTodForApproval (member-tier).
        // alreadyPosted: the kill was already saved (the addon or another officer, within a couple of
        // minutes), so this one's answers were added to that ToD instead of saving a second.
        mutate: () => this.http.postActivityJson<{ pending?: boolean; alreadyPosted?: boolean }>('/api/activity/tods', {
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
          imagePath: input.imagePath ?? null
        }),
        refresh: () => this.auth.refreshOverview(),
        success: response => response?.pending
          ? 'ToD submitted for officer approval.'
          : response?.alreadyPosted
            ? 'This kill was already logged, so your answers were added to that ToD.'
            : 'ToD entry saved.',
        failure: 'Saving the ToD entry failed.'
      });
      if (!outcome.ok) throw outcome.error;
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

  // Each monster's newest ToD, for the event form's "Last ToD" readout. Failures answer an empty
  // list for the same reason as loadUpcomingRepops: it's a convenience, not something to block on.
  async loadLatestTods(linkshellId: number): Promise<ActivityLatestTod[]> {
    if (!linkshellId) {
      return [];
    }

    try {
      const payload = await this.http.fetchActivityJson<{ entries?: ActivityLatestTod[] }>(
        `/api/activity/linkshells/${linkshellId}/latest-tods`);
      return payload?.entries ?? [];
    } catch {
      return [];
    }
  }

  // Throws on failure rather than answering null: null means "no ToD moves", and saying that after
  // a failed lookup would leave the event and the ToD page disagreeing with nobody told.
  async loadLinkedTod(eventId: number): Promise<ActivityLinkedTod | null> {
    const payload = await this.http.fetchActivityJson<{ tod?: ActivityLinkedTod | null }>(
      `/api/activity/events/${eventId}/linked-tod`);
    return payload?.tod ?? null;
  }

  async updateTod(input: ActivityUpdateTodInput): Promise<void> {
    this.busyTodSave.set(true);

    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/tods/${input.todId}/update`, {
          monsterName: input.monsterName,
          dayNumber: input.dayNumber ?? null,
          popWindow: input.popWindow ?? null,
          hq: input.hq,
          additionalSeconds: input.additionalSeconds,
          claim: input.claim,
          timeLocal: input.timeLocal,
          cooldown: input.cooldown || null,
          interval: input.interval || null,
          imagePath: input.imagePath ?? null
        }),
        'ToD entry updated.',
        'Updating the ToD entry failed.');
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

    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/events/${eventId}/post-tod`, {
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
        }),
        'Time of Death posted — the board re-posts automatically before the next pop.',
        'Posting the Time of Death failed.');
    } finally {
      this.busyTodSave.set(false);
    }
  }

  async deleteTod(todId: number): Promise<void> {
    this.busyTodId.set(todId);

    try {
      await this.save(
        () => this.http.postActivityAction(`/api/activity/tods/${todId}/delete`),
        'ToD entry deleted.',
        'Deleting the ToD entry failed.');
    } finally {
      this.busyTodId.set(null);
    }
  }

  // Send one change, then reload the overview. Throws only when the SAVE failed (see
  // mutate-then-refresh.ts): a reload that fails after a save that worked never reports it as failed.
  private async save(mutate: () => Promise<unknown>, success: string, failure: string): Promise<void> {
    const outcome = await mutateThenRefresh(this.auth, {
      mutate,
      refresh: () => this.auth.refreshOverview(),
      success,
      failure
    });
    if (!outcome.ok) throw outcome.error;
  }

  // The server checks the uploader can log or submit ToDs in this linkshell.
  async uploadTodImage(file: File, linkshellId: number): Promise<string | null> {
    this.auth.setActionError(null);
    try {
      const formData = new FormData();
      formData.append('file', file);
      formData.append('linkshellId', String(linkshellId));

      const payload = await this.http.sendActivityRequest<{ imagePath?: string } | undefined>(
        'POST', '/api/activity/tods/upload-image', formData);
      return payload?.imagePath ?? null;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Uploading the ToD image failed.'));
      return null;
    }
  }
}
