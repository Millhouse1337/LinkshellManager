import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type { ActivityDkpSheetResponse } from './discord-activity.types';
import { ResponseOrder } from './refresh-order';

// Read-only client for the always-on DKP sheet — the linkshell's live DKP
// computed from the app's own data (no Google connection).
@Injectable({ providedIn: 'root' })
export class DkpSheetService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly data = signal<ActivityDkpSheetResponse | null>(null);
  readonly busy = signal(false);

  // The tab and the refresh timer can both load at once; only the newest reply for the current
  // linkshell is shown (see refresh-order.ts).
  private readonly order = new ResponseOrder();

  async load(linkshellId: number): Promise<void> {
    if (!linkshellId) {
      this.order.invalidate();
      this.data.set(null);
      return;
    }

    this.busy.set(true);
    this.auth.setActionError(null);
    try {
      const ticket = this.order.begin(linkshellId);
      const result = await this.http.fetchActivityJson<ActivityDkpSheetResponse>(
        `/api/activity/dkp-sheet?linkshellId=${linkshellId}`
      );
      if (this.order.accept(ticket, linkshellId)) this.data.set(result);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the DKP sheet failed.'));
    } finally {
      this.busy.set(false);
    }
  }
}
