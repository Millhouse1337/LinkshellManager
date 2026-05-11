import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import type {
  ActivityCreateLinkshellInput,
  ActivityDkpRoundingIncrement,
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
        hiddenTodMonsters: input.hiddenTodMonsters ?? null
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
