import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityAddonPairingCodeResponse,
  ActivityAddonTokenList
} from './discord-activity.types';

@Injectable({ providedIn: 'root' })
export class AddonTokenService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly busyAddonTokens = signal(false);

  // Game Addon (att) pairing — calls the same /api/addon/management endpoints
  // the web Customize Linkshell page uses (cookie-authenticated; the Activity's
  // session cookie carries through via credentials:'include' on every fetch).
  async loadAddonTokens(linkshellId: number): Promise<ActivityAddonTokenList | null> {
    this.busyAddonTokens.set(true);
    this.auth.setActionError(null);
    try {
      const accessToken = this.auth.currentAccessToken();
      return await this.http.fetchActivityJson<ActivityAddonTokenList>(
        `/api/addon/management/tokens?linkshellId=${linkshellId}`,
        accessToken
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading addon tokens failed.'));
      return null;
    } finally {
      this.busyAddonTokens.set(false);
    }
  }

  async createAddonPairingCode(
    linkshellId: number
  ): Promise<ActivityAddonPairingCodeResponse | null> {
    this.busyAddonTokens.set(true);
    this.auth.setActionError(null);
    try {
      return await this.http.postActivityJson<ActivityAddonPairingCodeResponse>(
        '/api/addon/management/pairing-code',
        { linkshellId }
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Generating the pairing code failed.'));
      return null;
    } finally {
      this.busyAddonTokens.set(false);
    }
  }

  async revokeAddonToken(tokenId: number, linkshellId: number): Promise<boolean> {
    this.busyAddonTokens.set(true);
    this.auth.setActionError(null);
    try {
      await this.http.postActivityAction(
        `/api/addon/management/tokens/${tokenId}/revoke?linkshellId=${linkshellId}`
      );
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Revoking the addon token failed.'));
      return false;
    } finally {
      this.busyAddonTokens.set(false);
    }
  }
}
