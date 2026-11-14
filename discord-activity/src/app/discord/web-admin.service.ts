import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type { WebAdminSettings } from './discord-activity.types';
import { mutateThenRefresh } from './mutate-then-refresh';

// The website-only Admin tab: the server-wide switches a super admin flips
// (/api/web/admin, WebAdminController). Every change answers with the fresh settings plus a
// sentence saying what it did, and the overview is reloaded because it carries some of them
// (the admin badge, the addon and Claim Shield kill switches).
@Injectable({ providedIn: 'root' })
export class WebAdminService {
  private readonly http = inject(ActivityHttpClient);
  private readonly auth = inject(AuthService);

  readonly settings = signal<WebAdminSettings | null>(null);
  readonly busy = signal(false);

  async load(): Promise<void> {
    try {
      this.settings.set(await this.http.fetchActivityJson<WebAdminSettings>('/api/web/admin/settings'));
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the admin settings failed.'));
    }
  }

  setAdminOverride(enabled: boolean): Promise<void> {
    return this.change('admin-override', { enabled }, 'Changing the admin override failed.');
  }

  setAddonKillSwitch(disabled: boolean): Promise<void> {
    return this.change('addon-kill-switch', { disabled }, 'Changing the addon switch failed.');
  }

  setClaimShieldKillSwitch(disabled: boolean): Promise<void> {
    return this.change('claim-shield-kill-switch', { disabled }, 'Changing the Claim Shield switch failed.');
  }

  setLauncherDownload(enabled: boolean): Promise<void> {
    return this.change('launcher-download', { enabled }, 'Changing the launcher link failed.');
  }

  private async change(path: string, body: object, failure: string): Promise<void> {
    this.busy.set(true);
    try {
      // A reload that fails after the switch was saved never reports the save as failed.
      await mutateThenRefresh(this.auth, {
        mutate: async () => {
          const result = await this.http.postActivityJson<WebAdminSettings>(`/api/web/admin/settings/${path}`, body);
          this.settings.set(result);
          return result;
        },
        refresh: () => this.auth.refreshOverview(),
        success: result => result.message ?? null,
        failure
      });
    } finally {
      this.busy.set(false);
    }
  }
}
