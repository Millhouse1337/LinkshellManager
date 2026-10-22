import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type { ActivityDkpImportPreview } from './discord-activity.types';

// DKP import (website only): a linkshell migrating from another tracker uploads a spreadsheet
// of its existing DKP. Two steps, like the old Razor page: preview classifies every row without
// changing anything, then commit applies the previewed rows. ActivityDataController.DkpImport.
@Injectable({ providedIn: 'root' })
export class DkpImportService {
  private readonly http = inject(ActivityHttpClient);
  private readonly auth = inject(AuthService);

  // The pending preview remembers which linkshell it was built for, so switching linkshells
  // between preview and confirm can never apply one linkshell's sheet to another.
  private readonly pending = signal<{ linkshellId: number; preview: ActivityDkpImportPreview } | null>(null);
  readonly busy = signal(false);

  previewFor(linkshellId: number): ActivityDkpImportPreview | null {
    const pending = this.pending();
    return pending?.linkshellId === linkshellId ? pending.preview : null;
  }

  async loadPreview(linkshellId: number, file: File): Promise<void> {
    this.busy.set(true);
    this.auth.clearActionState();
    this.pending.set(null);
    try {
      const form = new FormData();
      form.append('file', file);
      const preview = await this.http.sendActivityRequest<ActivityDkpImportPreview>(
        'POST', `/api/activity/linkshells/${linkshellId}/dkp-import/preview`, form);
      this.pending.set({ linkshellId, preview });
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Reading the DKP file failed.'));
    } finally {
      this.busy.set(false);
    }
  }

  // Returns true once the import is applied.
  async commit(): Promise<boolean> {
    const pending = this.pending();
    if (!pending) {
      return false;
    }
    this.busy.set(true);
    this.auth.clearActionState();
    try {
      const result = await this.http.postActivityJson<{ message: string }>(
        `/api/activity/linkshells/${pending.linkshellId}/dkp-import/commit`,
        { rows: pending.preview.rows, sourceLabel: pending.preview.sourceLabel });
      this.pending.set(null);
      this.auth.setActionMessage(result.message);
      await this.auth.refreshOverview();
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Importing DKP failed.'));
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  cancel(): void {
    this.pending.set(null);
  }
}
