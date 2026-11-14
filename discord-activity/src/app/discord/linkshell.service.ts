import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { formatActionError } from './discord-activity.helpers';
import { type MutationOutcome, mutateThenRefresh } from './mutate-then-refresh';
import type {
  ActivityCreateLinkshellInput,
  ActivityChannelRouteInput,
  ActivityChannelRoutesResponse,
  ActivityDkpPoolInput,
  ActivityDkpPoolPreview,
  ActivityDkpPoolsResponse,
  ActivityDkpRoundingIncrement,
  ActivityGuildOption,
  ActivityJobRatingCommentSummary,
  ActivityJobRatingOverall,
  ActivityJobRatingsResponse,
  ActivityJobsRoster,
  ActivityLinkshellDetail,
  ActivityLinkshellRolePermissionsInput,
  ActivityLinkshellRolesResponse,
  ActivityLootStructure,
  ActivityMonsterTimingInput,
  ActivityMonsterTimingsResponse
} from './discord-activity.types';

@Injectable({ providedIn: 'root' })
export class LinkshellService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);

  readonly linkshellDetail = signal<ActivityLinkshellDetail | null>(null);
  readonly busyLinkshellId = signal<number | null>(null);
  readonly busyMemberId = signal<number | null>(null);
  readonly busyRoles = signal(false);
  readonly busyDiscordChannels = signal(false);
  readonly busyDkpPools = signal(false);
  readonly busyMonsterTimings = signal(false);
  readonly busyJobsRoster = signal(false);

  // Most saves here send one change and then reload the overview. Kept apart by mutateThenRefresh,
  // so a reload that fails after a save that worked never reports the save as failed.
  private saveThenRefreshOverview<T>(
    mutate: () => Promise<T>, success: string, failure: string
  ): Promise<MutationOutcome<T>> {
    return mutateThenRefresh(this.auth, {
      mutate,
      refresh: () => this.auth.refreshOverview(),
      success,
      failure
    });
  }

  async loadLinkshellDetail(linkshellId: number): Promise<void> {
    if (linkshellId <= 0) {
      this.linkshellDetail.set(null);
      return;
    }

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
    }
  }

  clearLinkshellDetail(): void {
    this.linkshellDetail.set(null);
  }

  async createLinkshell(input: ActivityCreateLinkshellInput): Promise<void> {
    const outcome = await this.saveThenRefreshOverview(
      () => this.http.postActivityAction('/api/activity/linkshells', {
        name: input.name,
        details: input.details || null
      }),
      'Linkshell created.',
      'Creating the linkshell failed.');
    if (!outcome.ok) throw outcome.error;
  }

  async updateLinkshell(
    linkshellId: number,
    input: ActivityCreateLinkshellInput & {
      lootStructure?: ActivityLootStructure | null;
      enableHnmSection?: boolean | null;
      enableAuctions?: boolean | null;
      enableToDs?: boolean | null;
      enableEndgame?: boolean | null;
      enableEvents?: boolean | null;
      enableDkp?: boolean | null;
      enableTreasury?: boolean | null;
      dkpRoundingIncrement?: ActivityDkpRoundingIncrement | null;
      // Member activity tracking (null = leave unchanged).
      enableActivityTracking?: boolean | null;
      inactiveAfterAbsences?: number | null;
      activeAfterAttendances?: number | null;
      // null = leave unchanged; [] = clear; [...names] = replace.
      hiddenTodMonsters?: string[] | null;
      // null = leave unchanged; "" = clear association; digits = associate with
      // that Discord server (does NOT lock — use setLinkshellGuildLock for that).
      discordGuildId?: string | null;
      // null = leave unchanged. Allow account-less Discord board signups (every event type).
      outsidePartySignupEnabled?: boolean | null;
      // Manual Check In HNM attendance (null = leave unchanged). Mode: 'Standard' | 'Wd'.
      hnmAttendanceMode?: string | null;
      wdDkpPerWindow?: number | null;
      wdClaimBonus?: number | null;
      wdKillBonus?: number | null;
      wdOpenBonus?: number | null;
      wdCloseBonus?: number | null;
      // Standard-mode HNM bonuses (null = leave unchanged).
      hnmStandardOpenBonus?: number | null;
      hnmStandardCloseBonus?: number | null;
      hnmStandardClaimBonus?: number | null;
      hnmStandardKillBonus?: number | null;
      hnmStandardWindowBonus?: number | null;
      hnmStandardMiscBonus?: number | null;
    }
  ): Promise<void> {
    this.busyLinkshellId.set(linkshellId);

    try {
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/update`, {
          name: input.name,
          details: input.details || null,
          lootStructure: input.lootStructure ?? null,
          enableHnmSection: input.enableHnmSection ?? null,
          enableAuctions: input.enableAuctions ?? null,
          enableToDs: input.enableToDs ?? null,
          enableEndgame: input.enableEndgame ?? null,
          enableEvents: input.enableEvents ?? null,
          enableDkp: input.enableDkp ?? null,
          enableTreasury: input.enableTreasury ?? null,
          dkpRoundingIncrement: input.dkpRoundingIncrement ?? null,
          enableActivityTracking: input.enableActivityTracking ?? null,
          inactiveAfterAbsences: input.inactiveAfterAbsences ?? null,
          activeAfterAttendances: input.activeAfterAttendances ?? null,
          hiddenTodMonsters: input.hiddenTodMonsters ?? null,
          discordGuildId: input.discordGuildId ?? null,
          outsidePartySignupEnabled: input.outsidePartySignupEnabled ?? null,
          hnmAttendanceMode: input.hnmAttendanceMode ?? null,
          wdDkpPerWindow: input.wdDkpPerWindow ?? null,
          wdClaimBonus: input.wdClaimBonus ?? null,
          wdKillBonus: input.wdKillBonus ?? null,
          wdOpenBonus: input.wdOpenBonus ?? null,
          wdCloseBonus: input.wdCloseBonus ?? null,
          hnmStandardOpenBonus: input.hnmStandardOpenBonus ?? null,
          hnmStandardCloseBonus: input.hnmStandardCloseBonus ?? null,
          hnmStandardClaimBonus: input.hnmStandardClaimBonus ?? null,
          hnmStandardKillBonus: input.hnmStandardKillBonus ?? null,
          hnmStandardWindowBonus: input.hnmStandardWindowBonus ?? null,
          hnmStandardMiscBonus: input.hnmStandardMiscBonus ?? null
        }),
        'Linkshell updated.',
        'Updating the linkshell failed.');
      if (!outcome.ok) throw outcome.error;
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

  // Associate this linkshell with a Discord server (does NOT lock access).
  // guildId = a server chosen from the eligible-guilds dropdown; null = fall back
  // to the server the Activity is launched in (X-Discord-Guild-Id header).
  // guildName is the display label.
  async setLinkshellGuild(
    linkshellId: number,
    guildId: string | null,
    guildName: string | null
  ): Promise<boolean> {
    this.busyLinkshellId.set(linkshellId);

    try {
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/set-guild`, {
          guildId: guildId?.trim() || null,
          guildName: guildName?.trim() || null
        }),
        'Discord server set for this linkshell.',
        'Setting the linkshell\'s Discord server failed.');
      return outcome.ok;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  // Clear the linkshell's Discord server association (also turns off the lock).
  async clearLinkshellGuild(linkshellId: number): Promise<boolean> {
    this.busyLinkshellId.set(linkshellId);

    try {
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/clear-guild`),
        'Discord server cleared for this linkshell.',
        'Clearing the linkshell\'s Discord server failed.');
      return outcome.ok;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  // Toggle the optional access lock. Requires a server already set; when locking,
  // the caller must be in that server (enforced server-side).
  async setLinkshellGuildLock(linkshellId: number, locked: boolean): Promise<boolean> {
    this.busyLinkshellId.set(linkshellId);

    try {
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/set-guild-lock`, { locked }),
        locked
          ? 'Linkshell locked — only accessible from its Discord server.'
          : 'Linkshell unlocked — accessible from any server.',
        locked ? 'Locking the linkshell failed.' : 'Unlocking the linkshell failed.');
      return outcome.ok;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  // Set (or clear with null) the channel new post-event discussion comments
  // mirror to.
  async setDiscussionChannel(linkshellId: number, channelId: string | null): Promise<boolean> {
    this.busyLinkshellId.set(linkshellId);
    try {
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/discussion-channel`, { channelId }),
        channelId
          ? 'Discussion channel set — new post-event comments mirror there.'
          : 'Discussion channel cleared — comments stay in-app.',
        'Updating the discussion channel failed.');
      return outcome.ok;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  // Phase 2 channel config: load the linkshell's channel bindings + the bot's
  // available channels.
  async loadDiscordChannels(linkshellId: number, refresh = false): Promise<ActivityChannelRoutesResponse | null> {
    this.busyDiscordChannels.set(true);
    this.auth.setActionError(null);
    try {
      const accessToken = this.auth.currentAccessToken();
      // refresh=true bypasses the bot's channel cache so a just-created Discord
      // channel appears immediately.
      const query = refresh ? '?refresh=true' : '';
      const data = await this.http.fetchActivityJson<ActivityChannelRoutesResponse>(
        `/api/activity/linkshells/${linkshellId}/discord-channels${query}`,
        accessToken
      );
      // Confirm a manual refresh visibly (the success flash banner) so the user
      // can see the list actually re-pulled.
      if (refresh && data) {
        const count = data.availableChannels?.length ?? 0;
        this.auth.setActionMessage(`Channel list refreshed — ${count} channel${count === 1 ? '' : 's'} found.`);
      }
      return data;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading Discord channels failed.'));
      return null;
    } finally {
      this.busyDiscordChannels.set(false);
    }
  }

  async saveDiscordChannels(
    linkshellId: number, routes: ActivityChannelRouteInput[]): Promise<boolean> {
    this.busyDiscordChannels.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(
        `/api/activity/linkshells/${linkshellId}/discord-channels`, { routes });
      this.auth.setActionMessage('Discord channels updated.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Saving Discord channels failed.'));
      return false;
    } finally {
      this.busyDiscordChannels.set(false);
    }
  }

  // ---- DKP pools ----
  //
  // Deliberately its own endpoint trio rather than fields on the linkshell-update request: the
  // Configurations tab re-sends every setting on any save, so a nullable pool list there would let
  // a rename silently wipe the pool config.

  async loadDkpPools(linkshellId: number): Promise<ActivityDkpPoolsResponse | null> {
    this.busyDkpPools.set(true);
    this.auth.setActionError(null);
    try {
      return await this.http.fetchActivityJson<ActivityDkpPoolsResponse>(
        `/api/activity/linkshells/${linkshellId}/dkp-pools`,
        this.auth.currentAccessToken()
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading DKP pools failed.'));
      return null;
    } finally {
      this.busyDkpPools.set(false);
    }
  }

  // Dry run: what would this save move, and what would it break? Writes nothing.
  async previewDkpPools(linkshellId: number, pools: ActivityDkpPoolInput[]): Promise<ActivityDkpPoolPreview | null> {
    this.busyDkpPools.set(true);
    this.auth.setActionError(null);
    try {
      return await this.http.postActivityJson<ActivityDkpPoolPreview>(
        `/api/activity/linkshells/${linkshellId}/dkp-pools/preview`,
        { pools }
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Previewing the DKP pool changes failed.'));
      return null;
    } finally {
      this.busyDkpPools.set(false);
    }
  }

  async saveDkpPools(linkshellId: number, pools: ActivityDkpPoolInput[]): Promise<boolean> {
    this.busyDkpPools.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);
    try {
      await this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/dkp-pools`, { pools });
      this.auth.setActionMessage('DKP pools updated.');
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Saving DKP pools failed.'));
      return false;
    } finally {
      this.busyDkpPools.set(false);
    }
  }

  // ---- Monster setups ----
  //
  // Same reasoning as the DKP pools above: its own endpoint pair, so the Configurations tab's
  // whole-payload settings save can never wipe the per-monster rows.

  async loadMonsterTimings(linkshellId: number): Promise<ActivityMonsterTimingsResponse | null> {
    this.busyMonsterTimings.set(true);
    this.auth.setActionError(null);
    try {
      return await this.http.fetchActivityJson<ActivityMonsterTimingsResponse>(
        `/api/activity/linkshells/${linkshellId}/monster-timings`,
        this.auth.currentAccessToken()
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading monster setups failed.'));
      return null;
    } finally {
      this.busyMonsterTimings.set(false);
    }
  }

  async saveMonsterTimings(
    linkshellId: number,
    rows: ActivityMonsterTimingInput[]
  ): Promise<ActivityMonsterTimingsResponse | null> {
    this.busyMonsterTimings.set(true);
    try {
      // The overview carries the compact copy the ToD form and the create-event picker read, so
      // it has to be refreshed or those two keep offering the old catalog until the next poll.
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityJson<ActivityMonsterTimingsResponse>(
          `/api/activity/linkshells/${linkshellId}/monster-timings`,
          { rows }
        ),
        'Monster setups updated.',
        'Saving monster setups failed.');
      return outcome.ok ? outcome.result : null;
    } finally {
      this.busyMonsterTimings.set(false);
    }
  }

  async setPrimaryLinkshell(linkshellId: number): Promise<void> {
    this.busyLinkshellId.set(linkshellId);

    try {
      await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/primary`),
        'Selected linkshell updated.',
        'Updating the selected linkshell failed.');
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async deleteLinkshell(linkshellId: number): Promise<void> {
    this.busyLinkshellId.set(linkshellId);

    try {
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/delete`),
        'Linkshell deleted.',
        'Deleting the linkshell failed.');
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async leaveLinkshell(linkshellId: number): Promise<void> {
    this.busyLinkshellId.set(linkshellId);

    try {
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/leave`),
        'Linkshell membership updated.',
        'Leaving the linkshell failed.');
      if (!outcome.ok) throw outcome.error;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async removeLinkshellMember(linkshellId: number, memberId: number): Promise<void> {
    this.busyMemberId.set(memberId);

    try {
      await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(
          `/api/activity/linkshells/${linkshellId}/members/${memberId}/remove`
        ),
        'Linkshell member removed.',
        'Removing the linkshell member failed.');
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

    try {
      const who = characterName?.trim() || 'Member';
      await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(
          `/api/activity/linkshells/${linkshellId}/members/${memberId}/role`,
          { role }
        ),
        role === 'Leader'
          ? `Leadership transferred to ${who}.`
          : role === 'Officer'
            ? `${who} promoted to officer.`
            : `${who}'s role changed to ${role}.`,
        'Updating the member role failed.');
    } finally {
      this.busyMemberId.set(null);
    }
  }

  // ----- Peer job ratings -----

  async loadJobRatings(linkshellId: number, targetAppUserId: string, slot = 0): Promise<ActivityJobRatingsResponse | null> {
    this.auth.setActionError(null);
    try {
      return await this.http.fetchActivityJson<ActivityJobRatingsResponse>(
        `/api/activity/job-ratings/${encodeURIComponent(targetAppUserId)}?linkshellId=${linkshellId}&slot=${slot}`
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading job ratings failed.'));
      return null;
    }
  }

  async rateJob(linkshellId: number, targetAppUserId: string, jobIndex: number, gear: number, skill: number, hasRelic: boolean, slot = 0, relicNames: string[] = []): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.postActivityAction('/api/activity/job-ratings', {
        linkshellId, targetAppUserId, jobIndex, gear, skill, hasRelic, characterSlot: slot, relicNames
      });
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Saving the rating failed.'));
      return false;
    }
  }

  async rateJobComment(linkshellId: number, targetAppUserId: string, comment: string, slot = 0): Promise<boolean> {
    this.auth.setActionError(null);
    try {
      await this.http.postActivityAction('/api/activity/job-ratings/comment', {
        linkshellId, targetAppUserId, comment, characterSlot: slot
      });
      return true;
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Saving the comment failed.'));
      return false;
    }
  }

  async loadJobRatingCommentSummary(linkshellId: number, targetAppUserId: string, slot = 0): Promise<ActivityJobRatingCommentSummary | null> {
    this.auth.setActionError(null);
    try {
      return await this.http.fetchActivityJson<ActivityJobRatingCommentSummary>(
        `/api/activity/job-ratings/${encodeURIComponent(targetAppUserId)}/comment-summary?linkshellId=${linkshellId}&slot=${slot}`
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the feedback summary failed.'));
      return null;
    }
  }

  // Overall ratings rollup across all the member's characters (averages + AI comment summary).
  async loadJobRatingOverall(linkshellId: number, targetAppUserId: string): Promise<ActivityJobRatingOverall | null> {
    this.auth.setActionError(null);
    try {
      return await this.http.fetchActivityJson<ActivityJobRatingOverall>(
        `/api/activity/job-ratings/${encodeURIComponent(targetAppUserId)}/overall?linkshellId=${linkshellId}`
      );
    } catch (error) {
      this.auth.setActionError(formatActionError(error, 'Loading the overall ratings failed.'));
      return null;
    }
  }

  async updateLinkshellMemberStatus(
    linkshellId: number,
    memberId: number,
    status: string,
    characterName?: string | null
  ): Promise<void> {
    this.busyMemberId.set(memberId);

    try {
      const who = characterName?.trim() || 'Member';
      await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(
          `/api/activity/linkshells/${linkshellId}/members/${memberId}/status`,
          { status }
        ),
        `${who}'s status set to ${status}.`,
        'Updating the member status failed.');
    } finally {
      this.busyMemberId.set(null);
    }
  }

  // Officer override of a member's active-credit "Count" — stores the number and
  // recomputes their Active/Inactive status from the linkshell's threshold.
  async setMemberActiveCreditCount(
    linkshellId: number,
    memberId: number,
    count: number,
    characterName?: string | null,
    streakType: 'credit' | 'absent' = 'credit'
  ): Promise<void> {
    this.busyMemberId.set(memberId);

    try {
      const who = characterName?.trim() || 'Member';
      const label = streakType === 'absent' ? 'absence streak' : 'active credit';
      await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(
          `/api/activity/linkshells/${linkshellId}/members/${memberId}/active-credit-count`,
          { count, streakType }
        ),
        `${who}'s ${label} set to ${count}.`,
        'Updating active credit failed.');
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
    try {
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/roles`, input),
        'Role created.',
        'Creating the role failed.');
      return outcome.ok;
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
    try {
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(
          `/api/activity/linkshells/${linkshellId}/roles/${roleId}/update`,
          input
        ),
        'Role updated.',
        'Updating the role failed.');
      return outcome.ok;
    } finally {
      this.busyRoles.set(false);
    }
  }

  async deleteLinkshellRole(linkshellId: number, roleId: number): Promise<boolean> {
    this.busyRoles.set(true);
    try {
      const outcome = await this.saveThenRefreshOverview(
        () => this.http.postActivityAction(
          `/api/activity/linkshells/${linkshellId}/roles/${roleId}/delete`
        ),
        'Role deleted.',
        'Deleting the role failed.');
      return outcome.ok;
    } finally {
      this.busyRoles.set(false);
    }
  }

  // Dashboard banner upload via base64 JSON (the Discord iframe can't multipart).
  // dataUrl is a "data:image/...;base64,..." string from FileReader; the server
  // strips the prefix and validates the bytes.
  async uploadBanner(linkshellId: number, dataUrl: string): Promise<boolean> {
    const outcome = await this.saveThenRefreshOverview(
      () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/banner`, { dataBase64: dataUrl }),
      'Banner updated.',
      'Updating the banner failed.');
    return outcome.ok;
  }

  async removeBanner(linkshellId: number): Promise<boolean> {
    const outcome = await this.saveThenRefreshOverview(
      () => this.http.postActivityAction(`/api/activity/linkshells/${linkshellId}/banner/remove`),
      'Banner removed.',
      'Removing the banner failed.');
    return outcome.ok;
  }
}
