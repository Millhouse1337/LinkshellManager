import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type { ActivityClaimCandidate } from './discord-activity.types';

// "Claim your DKP": when the signed-in user matches an unclaimed PLACEHOLDER the
// DKP import created (by character name), this surfaces a one-time prompt so they
// can inherit that imported DKP. The Discord-id case is handled silently on first
// launch by the server; this covers the name-matched, user-confirmed case.
@Injectable({ providedIn: 'root' })
export class ClaimService {
  private readonly http = inject(ActivityHttpClient);
  private readonly auth = inject(AuthService);

  readonly candidates = signal<ActivityClaimCandidate[]>([]);
  readonly busy = signal(false);
  readonly dismissed = signal(false);
  private loaded = false;

  async load(force = false): Promise<void> {
    if (this.loaded && !force) {
      return;
    }
    this.loaded = true;
    try {
      const result = await this.http.fetchActivityJson<{ candidates: ActivityClaimCandidate[] }>(
        '/api/activity/claim/candidates'
      );
      this.candidates.set(result?.candidates ?? []);
    } catch {
      // Non-fatal — a failed candidate lookup just means no prompt is shown.
      this.candidates.set([]);
    }
  }

  async claim(candidate: ActivityClaimCandidate): Promise<void> {
    this.busy.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityJson('/api/activity/claim', {
        placeholderAppUserId: candidate.placeholderAppUserId,
        linkshellId: candidate.linkshellId
      });
      this.candidates.update(list =>
        list.filter(
          c =>
            !(c.placeholderAppUserId === candidate.placeholderAppUserId && c.linkshellId === candidate.linkshellId)
        )
      );
      // Reflect the inherited DKP/roster everywhere.
      await this.auth.refreshOverview();
      this.auth.setActionMessage(`Claimed your DKP in ${candidate.linkshellName}.`);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Claiming your DKP failed.'));
    } finally {
      this.busy.set(false);
    }
  }

  dismiss(): void {
    this.dismissed.set(true);
  }
}
