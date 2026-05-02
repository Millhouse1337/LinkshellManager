import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type { ActivityDkpHistory } from './discord-activity.types';

@Injectable({ providedIn: 'root' })
export class DkpService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly dkpHistory = signal<ActivityDkpHistory | null>(null);
  readonly dkpHistoryBusy = signal(false);
  readonly busyDkpAudit = signal(false);

  async loadDkpHistory(
    linkshellId?: number | null,
    appUserId?: string | null
  ): Promise<ActivityDkpHistory | null> {
    this.dkpHistoryBusy.set(true);

    try {
      const accessToken = this.auth.currentAccessToken();
      const query = new URLSearchParams();
      if (linkshellId) {
        query.set('linkshellId', String(linkshellId));
      }
      if (appUserId) {
        query.set('appUserId', appUserId);
      }

      const path =
        query.size > 0 ? `/api/activity/dkp-history?${query.toString()}` : '/api/activity/dkp-history';
      const history = await this.http.fetchActivityJson<ActivityDkpHistory>(path, accessToken);
      this.dkpHistory.set(history);
      return history;
    } catch (error) {
      this.dkpHistory.set(null);
      this.auth.setActionError(formatActionError(error, 'Loading DKP History failed.'));
      return null;
    } finally {
      this.dkpHistoryBusy.set(false);
    }
  }

  clearDkpHistory(): void {
    this.dkpHistory.set(null);
  }

  async submitDkpAudit(input: {
    linkshellId: number;
    targetAppUserId: string;
    mode: 'Adjust' | 'Misc';
    relatedLedgerEntryId?: number | null;
    amount: number;
    reason: string;
  }): Promise<boolean> {
    this.busyDkpAudit.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction('/api/activity/dkp-audit', {
        linkshellId: input.linkshellId,
        targetAppUserId: input.targetAppUserId,
        mode: input.mode,
        relatedLedgerEntryId: input.relatedLedgerEntryId ?? null,
        amount: input.amount,
        reason: input.reason
      });
      await this.loadDkpHistory(input.linkshellId, input.targetAppUserId);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('DKP audit recorded.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Submitting the DKP audit failed.'));
      return false;
    } finally {
      this.busyDkpAudit.set(false);
    }
  }
}
