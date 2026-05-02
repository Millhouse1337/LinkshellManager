import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityLinkshellSearchResult,
  ActivityParticipantInviteCandidate,
  ActivityUserSearchResult
} from './discord-activity.types';

@Injectable({ providedIn: 'root' })
export class InviteService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly inviteSearchResults = signal<ActivityUserSearchResult[]>([]);
  readonly inviteSearchBusy = signal(false);
  readonly busyInviteId = signal<number | null>(null);
  readonly participantInviteCandidates = signal<ActivityParticipantInviteCandidate[]>([]);
  readonly participantInviteBusy = signal(false);
  readonly linkshellSearchResults = signal<ActivityLinkshellSearchResult[]>([]);
  readonly linkshellSearchBusy = signal(false);

  async searchPlayers(query: string, linkshellId: number): Promise<void> {
    if (!query.trim() || query.trim().length < 2 || linkshellId <= 0) {
      this.inviteSearchResults.set([]);
      return;
    }

    this.inviteSearchBusy.set(true);
    this.auth.setActionError(null);

    try {
      const headers: Record<string, string> = {};
      const accessToken = this.auth.currentAccessToken();
      if (accessToken) {
        headers['Authorization'] = `Bearer ${accessToken}`;
      }

      const response = await fetch(
        `/api/activity/players/search?query=${encodeURIComponent(query.trim())}&linkshellId=${linkshellId}`,
        {
          headers,
          cache: 'no-store',
          credentials: 'include'
        }
      );

      const responseText = await response.text();
      let payload: unknown = [];

      if (responseText) {
        try {
          payload = JSON.parse(responseText);
        } catch {
          payload = { error: responseText };
        }
      }

      if (!response.ok) {
        const errorPayload = payload as { error?: unknown };
        throw new Error(
          typeof errorPayload.error === 'string'
            ? errorPayload.error
            : `Searching players failed with status ${response.status}.`
        );
      }

      this.inviteSearchResults.set(payload as ActivityUserSearchResult[]);
    } catch (error) {
      this.inviteSearchResults.set([]);
      this.auth.setActionError(formatActionError(error, 'Searching players failed.'));
    } finally {
      this.inviteSearchBusy.set(false);
    }
  }

  clearInviteSearch(): void {
    this.inviteSearchResults.set([]);
  }

  clearParticipantInviteCandidates(): void {
    this.participantInviteCandidates.set([]);
  }

  clearLinkshellSearch(): void {
    this.linkshellSearchResults.set([]);
  }

  async loadParticipantInviteCandidates(linkshellId: number, discordUserIds: string[]): Promise<void> {
    const normalizedIds = Array.from(
      new Set(discordUserIds.map(id => id.trim()).filter(id => id.length > 0))
    );

    if (linkshellId <= 0 || normalizedIds.length === 0) {
      this.participantInviteCandidates.set([]);
      return;
    }

    this.participantInviteBusy.set(true);

    try {
      const response = await this.http.postActivityJson<ActivityParticipantInviteCandidate[]>(
        '/api/activity/invites/participants',
        {
          linkshellId,
          discordUserIds: normalizedIds
        }
      );

      this.participantInviteCandidates.set(response);
    } catch (error) {
      this.participantInviteCandidates.set([]);
      this.auth.setActionError(
        formatActionError(error, 'Loading connected participant invite targets failed.')
      );
    } finally {
      this.participantInviteBusy.set(false);
    }
  }

  async sendInvite(linkshellId: number, appUserId: string): Promise<void> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/invites`, {
        appUserId
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Invite sent.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Sending the invite failed.'));
    }
  }

  async searchLinkshells(query: string): Promise<void> {
    this.linkshellSearchBusy.set(true);
    this.auth.setActionError(null);

    try {
      const headers: Record<string, string> = {};
      const accessToken = this.auth.currentAccessToken();
      if (accessToken) {
        headers['Authorization'] = `Bearer ${accessToken}`;
      }

      const response = await fetch(
        `/api/activity/linkshells/search?query=${encodeURIComponent(query.trim())}`,
        {
          headers,
          cache: 'no-store',
          credentials: 'include'
        }
      );

      const responseText = await response.text();
      let payload: unknown = [];

      if (responseText) {
        try {
          payload = JSON.parse(responseText);
        } catch {
          payload = { error: responseText };
        }
      }

      if (!response.ok) {
        const errorPayload = payload as { error?: unknown };
        throw new Error(
          typeof errorPayload.error === 'string'
            ? errorPayload.error
            : `Searching linkshells failed with status ${response.status}.`
        );
      }

      this.linkshellSearchResults.set(payload as ActivityLinkshellSearchResult[]);
    } catch (error) {
      this.linkshellSearchResults.set([]);
      this.auth.setActionError(formatActionError(error, 'Searching linkshells failed.'));
    } finally {
      this.linkshellSearchBusy.set(false);
    }
  }

  async requestJoinLinkshell(linkshellId: number): Promise<void> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/join-request`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Join request sent.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Sending the join request failed.'));
    }
  }

  async approveJoinRequest(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/join-requests/${inviteId}/approve`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Join request approved.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Approving the join request failed.'));
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async declineJoinRequest(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/join-requests/${inviteId}/decline`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Join request declined.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Declining the join request failed.'));
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async acceptInvite(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/invites/${inviteId}/accept`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Invite accepted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Accepting the invite failed.'));
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async declineInvite(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/invites/${inviteId}/decline`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Invite declined.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Declining the invite failed.'));
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async revokeInvite(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/invites/${inviteId}/revoke`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Invite revoked.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Revoking the invite failed.'));
    } finally {
      this.busyInviteId.set(null);
    }
  }
}
