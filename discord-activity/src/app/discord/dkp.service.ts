import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import { mutateThenRefresh } from './mutate-then-refresh';
import type { ActivityDkpAddCandidate, ActivityDkpHistory } from './discord-activity.types';

@Injectable({ providedIn: 'root' })
export class DkpService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly dkpHistory = signal<ActivityDkpHistory | null>(null);
  readonly dkpHistoryBusy = signal(false);
  readonly busyDkpAudit = signal(false);
  readonly dkpAddCandidates = signal<ActivityDkpAddCandidate[]>([]);
  readonly dkpAddCandidatesBusy = signal(false);

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
      this.auth.clearLoadError('dkp-history');
      return history;
    } catch (error) {
      this.dkpHistory.set(null);
      // Reloaded on every overview refresh: its own notice, never the action banner
      // (see AuthService.loadError).
      this.auth.reportLoadError('dkp-history', error, 'Loading DKP History failed.');
      return null;
    } finally {
      this.dkpHistoryBusy.set(false);
    }
  }

  clearDkpHistory(): void {
    this.dkpHistory.set(null);
  }

  // Posted attendance/window events the target member was missed by — eligible
  // for the audit "Add to a previous entry" mode.
  async loadAddCandidates(linkshellId: number, targetAppUserId: string): Promise<void> {
    if (!linkshellId || !targetAppUserId) {
      this.dkpAddCandidates.set([]);
      return;
    }

    this.dkpAddCandidatesBusy.set(true);
    try {
      const accessToken = this.auth.currentAccessToken();
      const query = new URLSearchParams();
      query.set('linkshellId', String(linkshellId));
      query.set('targetAppUserId', targetAppUserId);
      const result = await this.http.fetchActivityJson<{ entries: ActivityDkpAddCandidate[] }>(
        `/api/activity/dkp-audit/add-candidates?${query.toString()}`,
        accessToken
      );
      this.dkpAddCandidates.set(result?.entries ?? []);
    } catch (error) {
      this.dkpAddCandidates.set([]);
      this.auth.setActionError(formatActionError(error, 'Loading snapshot entries failed.'));
    } finally {
      this.dkpAddCandidatesBusy.set(false);
    }
  }

  clearAddCandidates(): void {
    this.dkpAddCandidates.set([]);
  }

  async submitDkpAudit(input: {
    linkshellId: number;
    targetAppUserId: string;
    mode: 'Adjust' | 'Add' | 'Misc';
    relatedLedgerEntryId?: number | null;
    sourceWindowEventId?: number | null;
    amount: number;
    reason: string;
    // Misc only: which DKP pool it lands in. Null = the linkshell's default pool.
    dkpPoolId?: number | null;
  }): Promise<boolean> {
    this.busyDkpAudit.set(true);

    try {
      // A failed reload after the audit was recorded must not read as a failed audit: a second
      // press would record it twice.
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction('/api/activity/dkp-audit', {
          linkshellId: input.linkshellId,
          targetAppUserId: input.targetAppUserId,
          mode: input.mode,
          relatedLedgerEntryId: input.relatedLedgerEntryId ?? null,
          sourceWindowEventId: input.sourceWindowEventId ?? null,
          amount: input.amount,
          reason: input.reason,
          dkpPoolId: input.dkpPoolId ?? null
        }),
        refresh: async () => {
          await this.loadDkpHistory(input.linkshellId, input.targetAppUserId);
          await this.auth.refreshOverview();
        },
        success: 'DKP audit recorded.',
        failure: 'Submitting the DKP audit failed.'
      });
      return outcome.ok;
    } finally {
      this.busyDkpAudit.set(false);
    }
  }
}
