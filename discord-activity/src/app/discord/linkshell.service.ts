import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityCreateLinkshellInput,
  ActivityDiscordChannelBindingInput,
  ActivityDiscordChannelsResponse,
  ActivityDkpRoundingIncrement,
  ActivityGuildOption,
  ActivityJobsRoster,
  ActivityLinkshellDetail,
  ActivityLinkshellRolePermissionsInput,
  ActivityLinkshellRolesResponse,
  ActivityLootStructure
} from './discord-activity.types';

@Injectable({ providedIn: 'root' })
export class LinkshellService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly linkshellDetail = signal<ActivityLinkshellDetail | null>(null);
  readonly linkshellDetailBusy = signal(false);
  readonly busyLinkshellId = signal<number | null>(null);
  readonly busyMemberId = signal<number | null>(null);
  readonly busyRoles = signal(false);
  readonly busyDiscordChannels = signal(false);
  readonly busyJobsRoster = signal(false);

  async loadLinkshellDetail(linkshellId: number): Promise<void> {
    if (linkshellId <= 0) {
      this.linkshellDetail.set(null);
      return;
    }

    this.linkshellDetailBusy.set(true);

    try {
      const accessToken = this.auth.currentAccessToken();
      this.linkshellDetail.set(
        await this.http.fetchActivityJson<ActivityLinkshellDetail>(
          `/api/activity/linkshells/${linkshellId}`,
          accessToken
        )
      );
    } catch (error) {
      this.linkshellDetail.set(null);
      this.auth.setActionError(formatActionError(error, 'Loading linkshell details failed.'));
    } finally {
      this.linkshellDetailBusy.set(false);
    }
  }

  clearLinkshellDetail(): void {
    this.linkshellDetail.set(null);
  }

  async createLinkshell(input: ActivityCreateLinkshellInput): Promise<void> {
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction('/api/activity/linkshells', {
        name: input.name,
        details: input.details || null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Linkshell created.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Creating the linkshell failed.'));
      throw error;
    }
  }

  async updateLinkshell(
    linkshellId: number,
    input: ActivityCreateLinkshellInput & {
      lootStructure?: ActivityLootStructure | null;
      enableHnmSection?: boolean | null;
      enableMissions?: boolean | null;
      enableAuctions?: boolean | null;
      enableToDs?: boolean | null;
      enableEndgame?: boolean | null;
      enableEvents?: boolean | null;
      enableDkp?: boolean | null;
      enableItems?: boolean | null;
      enableRevenue?: boolean | null;
      dkpRoundingIncrement?: ActivityDkpRoundingIncrement | null;
      // null = leave unchanged; [] = clear; [...names] = replace.
      hiddenTodMonsters?: string[] | null;
      // null/blank = leave unchanged. SkySeaDynamis | HnmOnly | Both.
      linkshellType?: string | null;
      // null = leave unchanged; "" = unlock; digits = lock to that Discord server.
      discordGuildId?: string | null;
    }
  ): Promise<void> {
    this.busyLinkshellId.set(linkshellId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/update`, {
        name: input.name,
        details: input.details || null,
        lootStructure: input.lootStructure ?? null,
        enableHnmSection: input.enableHnmSection ?? null,
        enableMissions: input.enableMissions ?? null,
        enableAuctions: input.enableAuctions ?? null,
        enableToDs: input.enableToDs ?? null,
        enableEndgame: input.enableEndgame ?? null,
        enableEvents: input.enableEvents ?? null,
        enableDkp: input.enableDkp ?? null,
        enableItems: input.enableItems ?? null,
        enableRevenue: input.enableRevenue ?? null,
        dkpRoundingIncrement: input.dkpRoundingIncrement ?? null,
        hiddenTodMonsters: input.hiddenTodMonsters ?? null,
        linkshellType: input.linkshellType ?? null,
        discordGuildId: input.discordGuildId ?? null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Linkshell updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the linkshell failed.'));
      throw error;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  // The Discord servers the caller can lock to (the bot's servers they're also
  // in). Drives the Configurations "Discord server lock" dropdown.
  async loadEligibleGuilds(): Promise<ActivityGuildOption[]> {
    try {
      const accessToken = this.auth.currentAccessToken();
      const data = await this.http.fetchActivityJson<ActivityGuildOption[]>(
        '/api/activity/eligible-guilds',
        accessToken
      );
      return data ?? [];
    } catch {
      // Non-fatal: the lock card falls back to "lock to this server".
      return [];
    }
  }

  // Lock this linkshell to a Discord server. guildId = a server chosen from the
  // eligible-guilds dropdown; null = fall back to the server the Activity is
  // launched in (X-Discord-Guild-Id header). guildName is the display label.
  async lockLinkshellToGuild(
    linkshellId: number,
    guildId: string | null,
    guildName: string | null
  ): Promise<boolean> {
    this.busyLinkshellId.set(linkshellId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/lock-guild`, {
        guildId: guildId?.trim() || null,
        guildName: guildName?.trim() || null
      });
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Linkshell locked to that Discord server.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Locking the linkshell to that server failed.'));
      return false;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async unlockLinkshellGuild(linkshellId: number): Promise<boolean> {
    this.busyLinkshellId.set(linkshellId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/unlock-guild`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Linkshell unlocked — accessible from any server.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Unlocking the linkshell failed.'));
      return false;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  // Phase 2 channel config: load the linkshell's channel bindings + the bot's
  // available channels (mirrors the web Customize "Discord Channels" card).
  async loadDiscordChannels(linkshellId: number): Promise<ActivityDiscordChannelsResponse | null> {
    this.busyDiscordChannels.set(true);
    this.auth.setActionError(null);
    try {
      const accessToken = this.auth.currentAccessToken();
      return await this.http.fetchActivityJson<ActivityDiscordChannelsResponse>(
        `/api/activity/linkshells/${linkshellId}/discord-channels`,
        accessToken
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading Discord channels failed.'));
      return null;
    } finally {
      this.busyDiscordChannels.set(false);
    }
  }

  async saveDiscordChannels(
    linkshellId: number, channels: ActivityDiscordChannelBindingInput[]): Promise<boolean> {
    this.busyDiscordChannels.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(
        `/api/activity/linkshells/${linkshellId}/discord-channels`, { channels });
      this.auth.setActionMessage('Discord channels updated.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Saving Discord channels failed.'));
      return false;
    } finally {
      this.busyDiscordChannels.set(false);
    }
  }

  async setPrimaryLinkshell(linkshellId: number): Promise<void> {
    this.busyLinkshellId.set(linkshellId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/primary`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Primary linkshell updated.');
    } catch (error) {
      this.auth.setActionError(
        formatActionError(error, 'Updating the primary linkshell failed.')
      );
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async deleteLinkshell(linkshellId: number): Promise<void> {
    this.busyLinkshellId.set(linkshellId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/delete`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Linkshell deleted.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the linkshell failed.'));
      throw error;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async leaveLinkshell(linkshellId: number): Promise<void> {
    this.busyLinkshellId.set(linkshellId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/leave`);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Linkshell membership updated.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Leaving the linkshell failed.'));
      throw error;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async removeLinkshellMember(linkshellId: number, memberId: number): Promise<void> {
    this.busyMemberId.set(memberId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(
        `/api/activity/linkshells/${linkshellId}/members/${memberId}/remove`
      );
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Linkshell member removed.');
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Removing the linkshell member failed.'));
    } finally {
      this.busyMemberId.set(null);
    }
  }

  async updateLinkshellMemberRole(
    linkshellId: number,
    memberId: number,
    role: string,
    characterName?: string | null
  ): Promise<void> {
    this.busyMemberId.set(memberId);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction(
        `/api/activity/linkshells/${linkshellId}/members/${memberId}/role`,
        { role }
      );
      await this.auth.refreshOverview();
      const who = characterName?.trim() || 'Member';
      this.auth.setActionMessage(
        role === 'Leader'
          ? `Leadership transferred to ${who}.`
          : role === 'Officer'
            ? `${who} promoted to officer.`
            : `${who}'s role changed to ${role}.`
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the member role failed.'));
    } finally {
      this.busyMemberId.set(null);
    }
  }

  async loadLinkshellRoles(linkshellId: number): Promise<ActivityLinkshellRolesResponse | null> {
    this.busyRoles.set(true);
    this.auth.setActionError(null);
    try {
      const accessToken = this.auth.currentAccessToken();
      const data = await this.http.fetchActivityJson<ActivityLinkshellRolesResponse>(
        `/api/activity/linkshells/${linkshellId}/roles`,
        accessToken
      );
      return data;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading linkshell roles failed.'));
      return null;
    } finally {
      this.busyRoles.set(false);
    }
  }

  async loadJobsRoster(linkshellId: number): Promise<ActivityJobsRoster | null> {
    if (linkshellId <= 0) {
      return null;
    }
    this.busyJobsRoster.set(true);
    this.auth.setActionError(null);
    try {
      const accessToken = this.auth.currentAccessToken();
      return await this.http.fetchActivityJson<ActivityJobsRoster>(
        `/api/activity/linkshells/${linkshellId}/jobs-roster`,
        accessToken
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the jobs roster failed.'));
      return null;
    } finally {
      this.busyJobsRoster.set(false);
    }
  }

  async createLinkshellRole(
    linkshellId: number,
    input: ActivityLinkshellRolePermissionsInput
  ): Promise<boolean> {
    this.busyRoles.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/roles`, input);
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Role created.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Creating the role failed.'));
      return false;
    } finally {
      this.busyRoles.set(false);
    }
  }

  async updateLinkshellRole(
    linkshellId: number,
    roleId: number,
    input: ActivityLinkshellRolePermissionsInput
  ): Promise<boolean> {
    this.busyRoles.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(
        `/api/activity/linkshells/${linkshellId}/roles/${roleId}/update`,
        input
      );
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Role updated.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Updating the role failed.'));
      return false;
    } finally {
      this.busyRoles.set(false);
    }
  }

  async deleteLinkshellRole(linkshellId: number, roleId: number): Promise<boolean> {
    this.busyRoles.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(
        `/api/activity/linkshells/${linkshellId}/roles/${roleId}/delete`
      );
      await this.auth.refreshOverview();
      this.auth.setActionMessage('Role deleted.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Deleting the role failed.'));
      return false;
    } finally {
      this.busyRoles.set(false);
    }
  }
}
