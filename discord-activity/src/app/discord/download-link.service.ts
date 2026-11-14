import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { DiscordActivityService } from './discord-activity.service';
import { formatActionError } from './discord-activity.helpers';

// What a download link can fetch (ActivityOverviewController.Downloads): the linkshell's DKP Sheet as
// .xlsx, or the sample Import DKP template.
export type DownloadKind = 'dkp-sheet' | 'dkp-template';

// Downloads inside Discord. The Activity's iframe has nowhere to save a file, so it asks the
// server for a short-lived, single-use link and opens that in the member's own browser, which then
// downloads the file without needing to be signed in. The website just uses an ordinary link.
@Injectable({ providedIn: 'root' })
export class DownloadLinkService {
  private readonly http = inject(ActivityHttpClient);
  private readonly auth = inject(AuthService);
  private readonly activity = inject(DiscordActivityService);

  readonly busy = signal(false);

  async openInBrowser(kind: DownloadKind, linkshellId?: number): Promise<void> {
    this.busy.set(true);
    this.auth.clearActionState();
    try {
      const { url } = await this.http.postActivityJson<{ url: string }>(
        '/api/activity/download-links', { kind, linkshellId });
      await this.activity.openExternalLink(url);
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Opening the download failed.'));
    } finally {
      this.busy.set(false);
    }
  }
}
