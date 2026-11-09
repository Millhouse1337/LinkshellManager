import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityDiscordRoster,
  ActivityDiscordRosterCandidate,
  ActivityLinkshellSearchResult,
  ActivityUserSearchResult
} from './discord-activity.types';

@Injectable({ providedIn: 'root' })
export class InviteService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly inviteSearchResults = signal<ActivityUserSearchResult[]>([]);
  readonly inviteSearchBusy = signal(false);
  // Paginated "browse all eligible members" list (no Discord bot needed — these
  // are users already in our database).
  readonly inviteBrowseResults = signal<ActivityUserSearchResult[]>([]);
  readonly inviteBrowseTotal = signal(0);
  readonly inviteBrowseBusy = signal(false);
  readonly busyInviteId = signal<number | null>(null);
  readonly linkshellSearchResults = signal<ActivityLinkshellSearchResult[]>([]);
  readonly linkshellSearchBusy = signal(false);
  // "From My Discord Server": members of the Discord server the app was opened in who
  // can be added straight to the linkshell (including people who have never used LSM),
  // and that server's name for the card's note.
  readonly discordRosterCandidates = signal<ActivityDiscordRosterCandidate[]>([]);
  readonly discordRosterGuildName = signal<string | null>(null);
  readonly discordRosterBusy = signal(false);
  readonly busyDiscordUserId = signal<string | null>(null);

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

  // Through fetchActivityJson so the request carries X-Discord-Guild-Id: inside Discord each
  // result then says whether they're in the server the app was opened in.
  async browsePlayers(
    linkshellId: number,
    options: { query?: string; page?: number; pageSize?: number }
  ): Promise<void> {
    if (linkshellId <= 0) {
      this.inviteBrowseResults.set([]);
      this.inviteBrowseTotal.set(0);
      return;
    }

    this.inviteBrowseBusy.set(true);
    this.auth.setActionError(null);

    try {
      const params = new URLSearchParams();
      params.set('linkshellId', String(linkshellId));
      if (options.query && options.query.trim()) params.set('query', options.query.trim());
      params.set('page', String(options.page ?? 1));
      params.set('pageSize', String(options.pageSize ?? 10));

      const payload = await this.http.fetchActivityJson<{ items?: ActivityUserSearchResult[]; total?: number }>(
        `/api/activity/players/browse?${params.toString()}`);
      this.inviteBrowseResults.set(payload.items ?? []);
      this.inviteBrowseTotal.set(payload.total ?? 0);
    } catch (error) {
      this.inviteBrowseResults.set([]);
      this.inviteBrowseTotal.set(0);
      this.auth.setActionError(formatActionError(error, 'Browsing players failed.'));
    } finally {
      this.inviteBrowseBusy.set(false);
    }
  }

  clearInviteSearch(): void {
    this.inviteSearchResults.set([]);
  }

  clearLinkshellSearch(): void {
    this.linkshellSearchResults.set([]);
  }

  async sendInvite(linkshellId: number, appUserId: string): Promise<void> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/invites`, {
        appUserId
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Member added to the linkshell.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Adding the member failed.'));
    }
  }

  // The server reads which Discord server this is from X-Discord-Guild-Id, which
  // fetchActivityJson sends.
  async loadDiscordRoster(linkshellId: number): Promise<void> {
    if (linkshellId <= 0) {
      this.clearDiscordRoster();
      return;
    }

    this.discordRosterBusy.set(true);

    try {
      const roster = await this.http.fetchActivityJson<ActivityDiscordRoster>(
        `/api/activity/linkshells/${linkshellId}/discord-roster`);
      this.discordRosterCandidates.set(roster.members ?? []);
      this.discordRosterGuildName.set(roster.guildName ?? null);
    } catch (error) {
      this.clearDiscordRoster();
      this.auth.setActionError(formatActionError(error, 'Loading the Discord roster failed.'));
    } finally {
      this.discordRosterBusy.set(false);
    }
  }

  async inviteDiscordUser(linkshellId: number, discordUserId: string): Promise<void> {
    this.busyDiscordUserId.set(discordUserId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/invites/discord`, {
        discordUserId
      });
      // Drop the invited person from the roster and refresh sent invites.
      this.discordRosterCandidates.update(list =>
        list.filter(candidate => candidate.discordUserId !== discordUserId)
      );
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Member added. (New players join on their first sign-in.)');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Adding the member failed.'));
    } finally {
      this.busyDiscordUserId.set(null);
    }
  }

  clearDiscordRoster(): void {
    this.discordRosterCandidates.set([]);
    this.discordRosterGuildName.set(null);
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
