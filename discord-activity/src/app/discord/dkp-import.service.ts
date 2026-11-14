import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type { ActivityDkpImportPreview } from './discord-activity.types';
import { mutateThenRefresh } from './mutate-then-refresh';

// DKP import (website and Discord): a linkshell migrating from another tracker uploads a spreadsheet
// of its existing DKP. Two steps, like the old Razor page: preview classifies every row without
// changing anything, then commit applies the previewed rows. ActivityDkpController.DkpImport.
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
      // Base64 in JSON rather than a multipart upload: the Discord iframe can't send multipart
      // (the dashboard banner upload does the same), and it's one path for both hosts.
      const dataBase64 = await readAsDataUrl(file);
      const preview = await this.http.postActivityJson<ActivityDkpImportPreview>(
        `/api/activity/linkshells/${linkshellId}/dkp-import/preview`,
        { fileName: file.name, dataBase64 });
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
    try {
      // A reload that fails after the import was applied must not read as a failed import.
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: async () => {
          const result = await this.http.postActivityJson<{ message: string }>(
            `/api/activity/linkshells/${pending.linkshellId}/dkp-import/commit`,
            { rows: pending.preview.rows, sourceLabel: pending.preview.sourceLabel });
          this.pending.set(null);
          return result;
        },
        refresh: () => this.auth.refreshOverview(),
        success: result => result.message,
        failure: 'Importing DKP failed.'
      });
      return outcome.ok;
    } finally {
      this.busy.set(false);
    }
  }

  cancel(): void {
    this.pending.set(null);
  }
}

// The file as a "data:...;base64,..." URL, which the preview endpoint accepts as-is.
function readAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error ?? new Error('Could not read the file.'));
    reader.readAsDataURL(file);
  });
}
