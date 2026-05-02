import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityCreateTodInput,
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
      await this.http.postActivityAction('/api/activity/tods', {
        linkshellId: input.linkshellId,
        monsterName: input.monsterName,
        dayNumber: input.dayNumber ?? null,
        claim: input.claim,
        timeLocal: input.timeLocal,
        cooldown: input.cooldown || null,
        interval: input.interval || null,
        noLoot: input.noLoot,
        lootDetails: input.lootDetails.map(detail => ({
          itemName: detail.itemName || null,
          itemWinner: detail.itemWinner || null,
          winningDkpSpent: detail.winningDkpSpent ?? null
        })),
        imagePath: input.imagePath ?? null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('ToD entry saved.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Saving the ToD entry failed.'));
      throw error;
    } finally {
      this.busyTodSave.set(false);
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
        claim: input.claim,
        timeLocal: input.timeLocal,
        cooldown: input.cooldown || null,
        interval: input.interval || null,
        noLoot: input.noLoot,
        lootDetails: input.lootDetails.map(detail => ({
          itemName: detail.itemName || null,
          itemWinner: detail.itemWinner || null,
          winningDkpSpent: detail.winningDkpSpent ?? null
        })),
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
