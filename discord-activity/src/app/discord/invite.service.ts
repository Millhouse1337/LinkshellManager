import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityDiscordRosterCandidate,
  ActivityLinkshellSearchResult,
  ActivityUserSearchResult
} from './discord-activity.types';
import { mutateThenRefresh } from './mutate-then-refresh';

@Injectable({ providedIn: 'root' })
export class InviteService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  // Paginated "browse all eligible members" list (no Discord bot needed — these
  // are users already in our database).
  readonly inviteBrowseResults = signal<ActivityUserSearchResult[]>([]);
  readonly inviteBrowseTotal = signal(0);
  readonly inviteBrowseBusy = signal(false);
  readonly busyInviteId = signal<number | null>(null);
  // "From My Discord Server": members of the linkshell's Discord server (set in
  // Configurations) who can be added straight to the linkshell (including people who
  // have never used LSM).
  readonly discordRosterCandidates = signal<ActivityDiscordRosterCandidate[]>([]);
  readonly discordRosterBusy = signal(false);
  readonly busyDiscordUserId = signal<string | null>(null);
  // A player looking for a linkshell to ask to join (the Onboarding page).
  readonly linkshellSearchResults = signal<ActivityLinkshellSearchResult[]>([]);
  readonly linkshellSearchBusy = signal(false);
  readonly busyJoinLinkshellId = signal<number | null>(null);

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

  async sendInvite(linkshellId: number, appUserId: string): Promise<void> {
    await mutateThenRefresh(this.auth, {
      mutate: () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/invites`, {
        appUserId
      }),
      refresh: () => this.auth.refreshOverview(),
      success: 'Member added to the linkshell.',
      failure: 'Adding the member failed.'
    });
  }

  async loadDiscordRoster(linkshellId: number): Promise<void> {
    if (linkshellId <= 0) {
      this.clearDiscordRoster();
      return;
    }

    this.discordRosterBusy.set(true);

    try {
      this.discordRosterCandidates.set(await this.http.fetchActivityJson<ActivityDiscordRosterCandidate[]>(
        `/api/activity/linkshells/${linkshellId}/discord-roster`));
    } catch (error) {
      this.clearDiscordRoster();
      this.auth.setActionError(formatActionError(error, 'Loading the Discord roster failed.'));
    } finally {
      this.discordRosterBusy.set(false);
    }
  }

  async inviteDiscordUser(linkshellId: number, discordUserId: string): Promise<void> {
    this.busyDiscordUserId.set(discordUserId);

    try {
      await mutateThenRefresh(this.auth, {
        mutate: async () => {
          await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/invites/discord`, {
            discordUserId
          });
          // Drop the invited person from the roster; the refresh below updates sent invites.
          this.discordRosterCandidates.update(list =>
            list.filter(candidate => candidate.discordUserId !== discordUserId)
          );
        },
        refresh: () => this.auth.refreshOverview(),
        success: 'Member added. (New players join on their first sign-in.)',
        failure: 'Adding the member failed.'
      });
    } finally {
      this.busyDiscordUserId.set(null);
    }
  }

  clearDiscordRoster(): void {
    this.discordRosterCandidates.set([]);
  }

  /** Linkshells the player could ask to join, by name (two letters or more). */
  async searchLinkshells(query: string): Promise<void> {
    const text = query.trim();
    if (text.length < 2) {
      this.linkshellSearchResults.set([]);
      return;
    }

    this.linkshellSearchBusy.set(true);
    this.auth.setActionError(null);
    try {
      this.linkshellSearchResults.set(await this.http.fetchActivityJson<ActivityLinkshellSearchResult[]>(
        `/api/activity/linkshells/search?query=${encodeURIComponent(text)}`));
    } catch (error) {
      this.linkshellSearchResults.set([]);
      this.auth.setActionError(formatActionError(error, 'Searching linkshells failed.'));
    } finally {
      this.linkshellSearchBusy.set(false);
    }
  }

  /** Asks to join a linkshell. True when the request was sent. */
  async requestToJoin(linkshellId: number, message: string): Promise<boolean> {
    this.busyJoinLinkshellId.set(linkshellId);
    try {
      const outcome = await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/join-request`, {
          message: message.trim() || null
        }),
        refresh: () => this.auth.refreshOverview(),
        success: 'Request sent. An officer will answer it.',
        failure: 'Sending the request failed.'
      });
      if (outcome.ok) {
        // It now shows under "Your requests" instead.
        this.linkshellSearchResults.update(list => list.filter(result => result.id !== linkshellId));
      }
      return outcome.ok;
    } finally {
      this.busyJoinLinkshellId.set(null);
    }
  }

  async cancelJoinRequest(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/join-requests/${inviteId}/cancel`),
        refresh: () => this.auth.refreshOverview(),
        success: 'Request cancelled.',
        failure: 'Cancelling the request failed.'
      });
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async approveJoinRequest(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/join-requests/${inviteId}/approve`),
        refresh: () => this.auth.refreshOverview(),
        success: 'Join request approved.',
        failure: 'Approving the join request failed.'
      });
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async declineJoinRequest(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/join-requests/${inviteId}/decline`),
        refresh: () => this.auth.refreshOverview(),
        success: 'Join request denied.',
        failure: 'Denying the join request failed.'
      });
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async acceptInvite(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/invites/${inviteId}/accept`),
        refresh: () => this.auth.refreshOverview(),
        success: 'Invite accepted.',
        failure: 'Accepting the invite failed.'
      });
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async declineInvite(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/invites/${inviteId}/decline`),
        refresh: () => this.auth.refreshOverview(),
        success: 'Invite declined.',
        failure: 'Declining the invite failed.'
      });
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async revokeInvite(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    try {
      await mutateThenRefresh(this.auth, {
        mutate: () => this.http.postActivityAction(`/api/activity/invites/${inviteId}/revoke`),
        refresh: () => this.auth.refreshOverview(),
        success: 'Invite revoked.',
        failure: 'Revoking the invite failed.'
      });
    } finally {
      this.busyInviteId.set(null);
    }
  }
}
