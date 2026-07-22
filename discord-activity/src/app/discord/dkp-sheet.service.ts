import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type { ActivityDkpSheetResponse } from './discord-activity.types';

// Read-only client for the always-on DKP sheet — the linkshell's live DKP
// computed from the app's own data (no Google connection).
@Injectable({ providedIn: 'root' })
export class DkpSheetService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly data = signal<ActivityDkpSheetResponse | null>(null);
  readonly busy = signal(false);

  async load(linkshellId: number): Promise<void> {
    if (!linkshellId) {
      this.data.set(null);
      return;
    }

    this.busy.set(true);
    this.auth.setActionError(null);
    try {
      const result = await this.http.fetchActivityJson<ActivityDkpSheetResponse>(
        `/api/activity/dkp-sheet?linkshellId=${linkshellId}`
      );
      this.data.set(result);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the DKP sheet failed.'));
    } finally {
      this.busy.set(false);
    }
  }
}
