import { Injectable, computed, inject, signal } from '@angular/core';
import { DiscordSDK } from '@discord/embedded-app-sdk';

import { ActivityHttpClient } from './activity-http.client';
import { AddonTokenService } from './addon-token.service';
import { AuctionService } from './auction.service';
import { AuthService } from './auth.service';
import { DkpService } from './dkp.service';
import { LootHistoryService } from './loot-history.service';
import { EventService } from './event.service';
import { InviteService } from './invite.service';
import { LinkshellContentService } from './linkshell-content.service';
import { LinkshellService } from './linkshell.service';
import { TodService } from './tod.service';
import {
  formatError,
  resolveBrowserTimeZone,
  withTimeout
} from './discord-activity.helpers';
import type {
  ActivityAddonPairingCodeResponse,
  ActivityAddonTokenList,
  ActivityCreateAuctionInput,
  ActivityCreateEventInput,
  ActivityCreateLinkshellInput,
  ActivityCreateTodInput,
  ActivityDkpHistory,
  ActivityDkpRoundingIncrement,
  ActivityHistory,
  ActivityLootEditInput,
  ActivityLootHistoryList,
  ActivityHistoryDetail,
  ActivityItemInput,
  ActivityLinkshellRolePermissionsInput,
  ActivityLinkshellRolesResponse,
  ActivityLootInput,
  ActivityLootStructure,
  ActivityQuickJoinInput,
  ActivityRevenueInput,
  ActivityStatus,
  ActivityUpdateProfileInput,
  ActivityUpdateTodInput,
  DiscordContext,
  DiscordParticipant,
  DiscordSdkContextSource,
  DiscordSession,
  DiscordTokenExchangeResponse,
  LocalActivityUser
} from './discord-activity.types';

// Re-export types so existing consumers that import from
// `./discord-activity.service` keep compiling without source changes.
export type {
  ActivityAddonPairingCodeResponse,
  ActivityAddonToken,
  ActivityAddonTokenList,
  ActivityAnnouncement,
  ActivityAttendanceWindow,
  ActivityAttendanceWindowAttendee,
  ActivityAuction,
  ActivityAuctionBid,
  ActivityAuctionHistory,
  ActivityAuctionItemInput,
  ActivityCreateAuctionInput,
  ActivityCreateEventInput,
  ActivityCreateEventJobInput,
  ActivityCreateLinkshellInput,
  ActivityCreateTodInput,
  ActivityDkpHistory,
  ActivityDkpRoundingIncrement,
  ActivityEventParticipant,
  ActivityHistory,
  ActivityHistoryDetail,
  ActivityItem,
  ActivityItemInput,
  ActivityLinkshellPermissions,
  ActivityLinkshellRole,
  ActivityLinkshellRolePermissionsInput,
  ActivityLootEditInput,
  ActivityLootHistoryItem,
  ActivityLootHistoryList,
  ActivityLinkshellRolesResponse,
  ActivityLinkshellSearchResult,
  ActivityLinkshellSettings,
  ActivityLootInput,
  ActivityLootStructure,
  ActivityOverview,
  ActivityParticipantInviteCandidate,
  ActivityQuickJoinInput,
  ActivityRevenueEntry,
  ActivityRevenueInput,
  ActivityRule,
  ActivityStatusLedgerEntry,
  ActivityTodEntry,
  ActivityTodLootInput,
  ActivityUpdateProfileInput,
  ActivityUpdateTodInput,
  ActivityUserSearchResult,
  DiscordParticipant
} from './discord-activity.types';

declare const NG_APP_DISCORD_CLIENT_ID: string;

/**
 * Facade for the Activity feature. Discord SDK init + OAuth lives here;
 * everything else delegates to per-domain services. Public surface (methods
 * and signals) is preserved exactly for the consumer components.
 */
@Injectable({ providedIn: 'root' })
export class DiscordActivityService {
  private readonly clientId = NG_APP_DISCORD_CLIENT_ID ?? '';
  private readonly exchangePath = '/auth/discord/exchange';
  private readonly authorizeScopes = ['identify', 'guilds', 'applications.commands'] as const;
  private readonly browserTimeZone = resolveBrowserTimeZone();
  private initializationPromise: Promise<void> | null = null;
  private sdk: DiscordSdkContextSource | null = null;

  private readonly auth = inject(AuthService);
  private readonly http = inject(ActivityHttpClient);
  private readonly addonTokenService = inject(AddonTokenService);
  private readonly auctionService = inject(AuctionService);
  private readonly dkpService = inject(DkpService);
  private readonly eventService = inject(EventService);
  private readonly inviteService = inject(InviteService);
  private readonly linkshellService = inject(LinkshellService);
  private readonly linkshellContentService = inject(LinkshellContentService);
  private readonly lootHistoryService = inject(LootHistoryService);
  private readonly todService = inject(TodService);

  // --- Top-level state owned by the facade itself ---
  readonly status = signal<ActivityStatus>('idle');
  readonly phase = signal('Waiting to initialize');
  readonly error = signal<string | null>(null);
  readonly participants = signal<DiscordParticipant[]>([]);
  readonly context = signal<DiscordContext | null>(null);
  readonly actionMessage = this.auth.actionMessage;
  readonly actionError = this.auth.actionError;
  readonly busyProfileSave = signal(false);
  readonly busyRefresh = signal(false);
  readonly historyList = signal<ActivityHistory[]>([]);
  readonly historyDetail = signal<ActivityHistoryDetail | null>(null);
  readonly historyBusy = signal(false);
  readonly busyRuleSave = this.linkshellContentService.busyRuleSave;
  readonly busyRuleId = this.linkshellContentService.busyRuleId;
  readonly busyAnnouncementSave = this.linkshellContentService.busyAnnouncementSave;
  readonly busyAnnouncementId = this.linkshellContentService.busyAnnouncementId;
  readonly busyItemSave = this.linkshellContentService.busyItemSave;
  readonly busyItemId = this.linkshellContentService.busyItemId;
  readonly busyRevenueSave = this.linkshellContentService.busyRevenueSave;
  readonly busyRevenueId = this.linkshellContentService.busyRevenueId;

  // --- Facade signal accessors (re-export per-domain signals) ---
  readonly session = this.auth.session;
  readonly localUser = this.auth.localUser;
  readonly overview = this.auth.overview;
  readonly busyEventId = this.eventService.busyEventId;
  readonly busyLinkshellId = this.linkshellService.busyLinkshellId;
  readonly busyMemberId = this.linkshellService.busyMemberId;
  readonly busyRoles = this.linkshellService.busyRoles;
  readonly linkshellDetail = this.linkshellService.linkshellDetail;
  readonly linkshellDetailBusy = this.linkshellService.linkshellDetailBusy;
  readonly inviteSearchResults = this.inviteService.inviteSearchResults;
  readonly inviteSearchBusy = this.inviteService.inviteSearchBusy;
  readonly busyInviteId = this.inviteService.busyInviteId;
  readonly participantInviteCandidates = this.inviteService.participantInviteCandidates;
  readonly participantInviteBusy = this.inviteService.participantInviteBusy;
  readonly linkshellSearchResults = this.inviteService.linkshellSearchResults;
  readonly linkshellSearchBusy = this.inviteService.linkshellSearchBusy;
  readonly dkpHistory = this.dkpService.dkpHistory;
  readonly dkpHistoryBusy = this.dkpService.dkpHistoryBusy;
  readonly busyDkpAudit = this.dkpService.busyDkpAudit;
  readonly lootHistory = this.lootHistoryService.lootHistory;
  readonly lootHistoryBusy = this.lootHistoryService.lootHistoryBusy;
  readonly busyLootEdit = this.lootHistoryService.busyLootEdit;
  readonly auctions = this.auctionService.auctions;
  readonly auctionsBusy = this.auctionService.auctionsBusy;
  readonly auctionHistory = this.auctionService.auctionHistory;
  readonly auctionHistoryBusy = this.auctionService.auctionHistoryBusy;
  readonly auctionBids = this.auctionService.auctionBids;
  readonly busyAuctionId = this.auctionService.busyAuctionId;
  readonly busyAuctionItemId = this.auctionService.busyAuctionItemId;
  readonly busyTodId = this.todService.busyTodId;
  readonly busyTodSave = this.todService.busyTodSave;
  readonly busyAddonTokens = this.addonTokenService.busyAddonTokens;

  readonly isReady = computed(() => this.status() === 'ready');
  readonly isStandalonePreview = computed(() => this.status() === 'standalone');

  // --- Discord SDK initialization + OAuth (kept on facade) ---
  async initialize(): Promise<void> {
    if (this.initializationPromise) {
      return this.initializationPromise;
    }

    this.initializationPromise = this.initializeInternal();
    return this.initializationPromise;
  }

  // Used by the error UI to retry initialization after a permanent failure
  // (the cached `initializationPromise` would otherwise re-resolve immediately
  // with the same error). Resets the SDK handle and clears overview state so
  // the retry starts from a clean slate.
  async reconnect(): Promise<void> {
    this.initializationPromise = null;
    this.sdk = null;
    this.auth.session.set(null);
    this.auth.localUser.set(null);
    this.auth.overview.set(null);
    this.participants.set([]);
    this.context.set(null);
    this.error.set(null);
    return this.initialize();
  }

  private async initializeInternal(): Promise<void> {
    this.status.set('initializing');
    this.phase.set('Inspecting host environment');
    this.error.set(null);

    if (!this.clientId) {
      this.setError('Discord client ID is not configured in the Angular build.');
      return;
    }

    if (window.parent === window) {
      this.status.set('standalone');
      this.phase.set('Loaded outside Discord. Embedded auth is skipped.');
      await this.tryLoadStandaloneOverview();
      return;
    }

    try {
      const sdk = new DiscordSDK(this.clientId) as DiscordSdkContextSource;
      this.sdk = sdk;

      this.phase.set('Waiting for the Discord client');
      await withTimeout(sdk.ready(), 8000, 'Discord SDK did not become ready.');

      this.context.set({
        channelId: sdk.channelId ?? null,
        guildId: sdk.guildId ?? null,
        instanceId: sdk.instanceId ?? null,
        platform: sdk.platform ?? null
      });

      this.phase.set('Requesting Discord authorization');
      const { code } = await sdk.commands.authorize({
        client_id: this.clientId,
        response_type: 'code',
        prompt: 'none',
        scope: [...this.authorizeScopes],
        state: `linkshell-${Date.now()}`
      });

      this.phase.set('Exchanging the authorization code');
      const token = await this.exchangeCode(code);

      this.phase.set('Authenticating the embedded client');
      const auth = (await sdk.commands.authenticate({
        access_token: token.accessToken
      })) as unknown as DiscordSession | null;

      if (!auth) {
        throw new Error('Discord authenticate returned no session data.');
      }

      this.auth.session.set(auth);
      this.auth.localUser.set(token.localUser ?? null);

      this.phase.set('Resolving the local app user');
      this.auth.localUser.set(await this.fetchLocalUser(token.accessToken));

      this.phase.set('Loading linkshell activity data');
      this.auth.overview.set(await this.auth.fetchOverview(token.accessToken));

      this.phase.set('Loading activity participants');
      const participantsResponse = (await sdk.commands.getInstanceConnectedParticipants()) as {
        participants?: DiscordParticipant[];
      };
      this.participants.set(participantsResponse.participants ?? []);

      this.status.set('ready');
      this.phase.set('Discord Activity connected');
    } catch (error) {
      this.setError(formatError(error));
    }
  }

  private async exchangeCode(code: string): Promise<DiscordTokenExchangeResponse> {
    const response = await fetch(this.exchangePath, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json'
      },
      cache: 'no-store',
      body: JSON.stringify({ code })
    });

    const responseText = await response.text();
    let payload: unknown = {};

    if (responseText) {
      try {
        payload = JSON.parse(responseText);
      } catch {
        payload = { error: responseText };
      }
    }

    if (!response.ok) {
      const errorPayload = payload as { error?: unknown };
      const message =
        typeof errorPayload.error === 'string'
          ? errorPayload.error
          : `Discord exchange failed with status ${response.status}.`;
      throw new Error(message);
    }

    return payload as DiscordTokenExchangeResponse;
  }

  private async fetchLocalUser(accessToken: string): Promise<LocalActivityUser> {
    const response = await fetch('/api/me', {
      headers: {
        Authorization: `Bearer ${accessToken}`
      },
      cache: 'no-store'
    });

    const responseText = await response.text();
    let payload: unknown = {};

    if (responseText) {
      try {
        payload = JSON.parse(responseText);
      } catch {
        payload = { error: responseText };
      }
    }

    if (!response.ok) {
      const errorPayload = payload as { error?: unknown };
      const message =
        typeof errorPayload.error === 'string'
          ? errorPayload.error
          : `Loading the local app user failed with status ${response.status}.`;
      throw new Error(message);
    }

    return payload as LocalActivityUser;
  }

  private async tryLoadStandaloneOverview(): Promise<void> {
    try {
      this.auth.overview.set(await this.auth.fetchOverview());
      this.phase.set('Loaded outside Discord using the current website session.');
    } catch {
      this.auth.overview.set(null);
    }
  }

  private setError(message: string): void {
    this.status.set('error');
    this.phase.set('Discord Activity initialization failed');
    this.error.set(message);
  }

  // --- Top-level coordination methods (overview, refresh, history) ---
  refreshOverview(): Promise<void> {
    return this.auth.refreshOverview();
  }

  async refreshActivityData(): Promise<void> {
    this.busyRefresh.set(true);
    this.auth.setActionError(null);

    try {
      const accessToken = this.auth.currentAccessToken();
      this.auth.overview.set(await this.auth.fetchOverview(accessToken));

      const appUser = this.auth.overview()?.appUser;
      const currentLocalUser = this.auth.localUser();
      if (appUser && currentLocalUser) {
        this.auth.localUser.set({
          ...currentLocalUser,
          appUser: {
            ...currentLocalUser.appUser,
            ...appUser
          }
        });
      }

      if (this.sdk) {
        const participantsResponse = (await this.sdk.commands.getInstanceConnectedParticipants()) as {
          participants?: DiscordParticipant[];
        };
        this.participants.set(participantsResponse.participants ?? []);
      }

      this.auth.setActionMessage('Activity data refreshed.');
    } catch (error) {
      this.auth.setActionError(this.formatActionErrorMsg(error, 'Refreshing activity data failed.'));
    } finally {
      this.busyRefresh.set(false);
    }
  }

  async loadHistoryList(): Promise<void> {
    this.historyBusy.set(true);

    try {
      const accessToken = this.auth.currentAccessToken();
      this.historyList.set(
        await this.http.fetchActivityJson<ActivityHistory[]>('/api/activity/history', accessToken)
      );
    } catch (error) {
      this.historyList.set([]);
      this.auth.setActionError(this.formatActionErrorMsg(error, 'Loading event history failed.'));
    } finally {
      this.historyBusy.set(false);
    }
  }

  async loadHistoryDetail(historyId: number): Promise<void> {
    if (historyId <= 0) {
      this.historyDetail.set(null);
      return;
    }

    this.historyBusy.set(true);

    try {
      const accessToken = this.auth.currentAccessToken();
      this.historyDetail.set(
        await this.http.fetchActivityJson<ActivityHistoryDetail>(
          `/api/activity/history/${historyId}`,
          accessToken
        )
      );
    } catch (error) {
      this.historyDetail.set(null);
      this.auth.setActionError(
        this.formatActionErrorMsg(error, 'Loading event history details failed.')
      );
    } finally {
      this.historyBusy.set(false);
    }
  }

  clearHistoryDetail(): void {
    this.historyDetail.set(null);
  }

  clearHistoryList(): void {
    this.historyList.set([]);
  }

  clearActionState(): void {
    this.auth.clearActionState();
  }

  // --- Profile (kept on facade — also rewrites localUser snapshot) ---
  async updateProfile(input: ActivityUpdateProfileInput): Promise<void> {
    this.busyProfileSave.set(true);
    this.auth.setActionError(null);
    this.auth.setActionMessage(null);

    try {
      await this.http.postActivityAction('/api/activity/profile', {
        characterName: input.characterName,
        timeZone: input.timeZone || null,
        altCharacterName1: input.altCharacterName1 || null,
        altCharacterName2: input.altCharacterName2 || null
      });
      await this.auth.refreshOverview();

      const appUser = this.auth.overview()?.appUser;
      const currentLocalUser = this.auth.localUser();
      if (appUser && currentLocalUser) {
        this.auth.localUser.set({
          ...currentLocalUser,
          appUser: {
            ...currentLocalUser.appUser,
            ...appUser
          }
        });
      }

      this.auth.setActionMessage('Profile updated.');
    } catch (error) {
      this.auth.setActionError(this.formatActionErrorMsg(error, 'Updating the profile failed.'));
      throw error;
    } finally {
      this.busyProfileSave.set(false);
    }
  }

  // Posts the browser-detected IANA zone to the server when the user's saved
  // TimeZone is still on the server default (null/empty/UTC). Mirrors the
  // accepted value into the local + overview signals so the rest of the app
  // (and the header clock) picks it up without a manual refresh.
  private autoDetectAttempted = false;
  async detectAndSaveTimeZoneIfUnset(): Promise<void> {
    if (this.autoDetectAttempted) return;
    const detected = (this.browserTimeZone || '').trim();
    if (!detected) return;

    const appUser = this.auth.overview()?.appUser;
    const current = (appUser?.timeZone ?? '').trim();
    const isUnset = current === '' || current.toUpperCase() === 'UTC';
    if (!isUnset || current === detected) {
      this.autoDetectAttempted = true;
      return;
    }
    this.autoDetectAttempted = true;

    try {
      const result = await this.http.postActivityJson<{ applied: boolean; timeZone?: string }>(
        '/api/activity/profile/detect-time-zone',
        { timeZone: detected }
      );
      if (!result?.applied || !result.timeZone) return;

      const overview = this.auth.overview();
      if (overview?.appUser) {
        this.auth.overview.set({
          ...overview,
          appUser: { ...overview.appUser, timeZone: result.timeZone }
        });
      }
      const localUser = this.auth.localUser();
      if (localUser?.appUser) {
        this.auth.localUser.set({
          ...localUser,
          appUser: { ...localUser.appUser, timeZone: result.timeZone }
        });
      }
    } catch {
      // Best-effort — the next session will retry.
    }
  }

  // --- Rules / Announcements / Items / Revenue: see linkshellContentService ---
  // (delegates live in the facade-delegates section below)

  // --- Time / formatting helpers (kept on facade for consumer convenience) ---
  //
  // Profile-first priority: the user's saved IANA zone wins over the
  // browser's detected zone. The auto-detect-on-load feature already
  // backfills profile from the browser when it's unset, so the two
  // agree in the common case. When they disagree (the user deliberately
  // chose a different zone, or they're travelling), respecting the
  // profile setting matches what the user explicitly configured and
  // keeps every wall-clock on every screen consistent — which is the
  // whole point of having a profile timezone in the first place.
  viewerTimeZone(): string {
    return (
      this.auth.overview()?.appUser?.timeZone ||
      this.auth.localUser()?.appUser?.timeZone ||
      this.browserTimeZone ||
      'UTC'
    );
  }

  formatDateTime(value?: string | null): string | null {
    if (!value) {
      return null;
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return null;
    }

    return new Intl.DateTimeFormat(undefined, {
      timeZone: this.viewerTimeZone(),
      dateStyle: 'medium',
      timeStyle: 'short'
    }).format(date);
  }

  // Same as formatDateTime but renders seconds. Used for ToD displays
  // (TIME OF DEATH / REPOP STARTS) where the addon now captures down to
  // the second and the user wants the precision to round-trip.
  formatDateTimeWithSeconds(value?: string | null): string | null {
    if (!value) {
      return null;
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return null;
    }

    return new Intl.DateTimeFormat(undefined, {
      timeZone: this.viewerTimeZone(),
      dateStyle: 'medium',
      timeStyle: 'medium'
    }).format(date);
  }

  toViewerLocalInputValue(value?: string | null): string {
    if (!value) {
      return '';
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return '';
    }

    const parts = new Intl.DateTimeFormat('en-CA', {
      timeZone: this.viewerTimeZone(),
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hour12: false
    }).formatToParts(date);

    const lookup = (type: Intl.DateTimeFormatPartTypes): string =>
      parts.find(part => part.type === type)?.value ?? '';

    return `${lookup('year')}-${lookup('month')}-${lookup('day')}T${lookup('hour')}:${lookup('minute')}`;
  }

  // Local re-export of the helper so we don't have to import it into every
  // facade method that needs it.
  private formatActionErrorMsg(error: unknown, fallback: string): string {
    if (error instanceof Error && error.message) {
      return error.message;
    }
    return fallback;
  }

  // ===========================================================================
  // Facade delegate methods — one-line forwarders to the per-domain services.
  // Method signatures and behavior are identical to the pre-refactor versions.
  // ===========================================================================

  // --- AddonTokenService ---
  loadAddonTokens(linkshellId: number): Promise<ActivityAddonTokenList | null> { return this.addonTokenService.loadAddonTokens(linkshellId); }
  createAddonPairingCode(linkshellId: number, label: string | null): Promise<ActivityAddonPairingCodeResponse | null> { return this.addonTokenService.createAddonPairingCode(linkshellId, label); }
  revokeAddonToken(tokenId: number, linkshellId: number): Promise<boolean> { return this.addonTokenService.revokeAddonToken(tokenId, linkshellId); }

  // --- DkpService ---
  loadDkpHistory(linkshellId?: number | null, appUserId?: string | null): Promise<ActivityDkpHistory | null> { return this.dkpService.loadDkpHistory(linkshellId, appUserId); }
  clearDkpHistory(): void { this.dkpService.clearDkpHistory(); }
  submitDkpAudit(input: { linkshellId: number; targetAppUserId: string; mode: 'Adjust' | 'Misc'; relatedLedgerEntryId?: number | null; amount: number; reason: string }): Promise<boolean> { return this.dkpService.submitDkpAudit(input); }

  // --- LootHistoryService ---
  loadLootHistory(source: 'all' | 'tod' | 'event' = 'all', page: number = 1, pageSize: number = 20): Promise<ActivityLootHistoryList | null> { return this.lootHistoryService.loadLootHistory(source, page, pageSize); }
  async editTodLoot(lootDetailId: number, input: ActivityLootEditInput): Promise<boolean> {
    const ok = await this.lootHistoryService.editTodLoot(lootDetailId, input);
    if (ok) { await this.auth.refreshOverview(); }
    return ok;
  }
  async editEventLoot(lootDetailId: number, input: ActivityLootEditInput): Promise<boolean> {
    const ok = await this.lootHistoryService.editEventLoot(lootDetailId, input);
    if (ok) { await this.auth.refreshOverview(); }
    return ok;
  }

  // --- TodService ---
  createTod(input: ActivityCreateTodInput): Promise<void> { return this.todService.createTod(input); }
  updateTod(input: ActivityUpdateTodInput): Promise<void> { return this.todService.updateTod(input); }
  deleteTod(todId: number): Promise<void> { return this.todService.deleteTod(todId); }
  uploadTodImage(file: File): Promise<string | null> { return this.todService.uploadTodImage(file); }

  // --- AuctionService ---
  loadAuctions(linkshellId?: number | null): Promise<void> { return this.auctionService.loadAuctions(linkshellId); }
  loadAuctionHistory(linkshellId?: number | null): Promise<void> { return this.auctionService.loadAuctionHistory(linkshellId); }
  loadAuctionItemBids(itemId: number): Promise<void> { return this.auctionService.loadAuctionItemBids(itemId); }
  clearAuctionState(): void { this.auctionService.clearAuctionState(); }
  createAuction(input: ActivityCreateAuctionInput): Promise<void> { return this.auctionService.createAuction(input); }
  updateAuction(auctionId: number, input: ActivityCreateAuctionInput): Promise<void> { return this.auctionService.updateAuction(auctionId, input); }
  startAuction(auctionId: number, linkshellId: number): Promise<void> { return this.auctionService.startAuction(auctionId, linkshellId); }
  placeAuctionBid(itemId: number, bidAmount: number, linkshellId: number): Promise<void> { return this.auctionService.placeAuctionBid(itemId, bidAmount, linkshellId); }
  endAuction(auctionId: number, linkshellId: number): Promise<void> { return this.auctionService.endAuction(auctionId, linkshellId); }
  closeAuction(auctionId: number, linkshellId: number, deliveredItemIds: number[] = []): Promise<void> { return this.auctionService.closeAuction(auctionId, linkshellId, deliveredItemIds); }
  markAuctionHistoryItemReceived(itemId: number, linkshellId: number): Promise<void> { return this.auctionService.markAuctionHistoryItemReceived(itemId, linkshellId); }
  undoAuctionHistoryItem(itemId: number, linkshellId: number): Promise<void> { return this.auctionService.undoAuctionHistoryItem(itemId, linkshellId); }

  // --- InviteService ---
  searchPlayers(query: string, linkshellId: number): Promise<void> { return this.inviteService.searchPlayers(query, linkshellId); }
  clearInviteSearch(): void { this.inviteService.clearInviteSearch(); }
  clearParticipantInviteCandidates(): void { this.inviteService.clearParticipantInviteCandidates(); }
  clearLinkshellSearch(): void { this.inviteService.clearLinkshellSearch(); }
  loadParticipantInviteCandidates(linkshellId: number, discordUserIds: string[]): Promise<void> { return this.inviteService.loadParticipantInviteCandidates(linkshellId, discordUserIds); }
  sendInvite(linkshellId: number, appUserId: string): Promise<void> { return this.inviteService.sendInvite(linkshellId, appUserId); }
  searchLinkshells(query: string): Promise<void> { return this.inviteService.searchLinkshells(query); }
  requestJoinLinkshell(linkshellId: number): Promise<void> { return this.inviteService.requestJoinLinkshell(linkshellId); }
  approveJoinRequest(inviteId: number): Promise<void> { return this.inviteService.approveJoinRequest(inviteId); }
  declineJoinRequest(inviteId: number): Promise<void> { return this.inviteService.declineJoinRequest(inviteId); }
  acceptInvite(inviteId: number): Promise<void> { return this.inviteService.acceptInvite(inviteId); }
  declineInvite(inviteId: number): Promise<void> { return this.inviteService.declineInvite(inviteId); }
  revokeInvite(inviteId: number): Promise<void> { return this.inviteService.revokeInvite(inviteId); }

  // --- LinkshellService ---
  loadLinkshellDetail(linkshellId: number): Promise<void> { return this.linkshellService.loadLinkshellDetail(linkshellId); }
  clearLinkshellDetail(): void { this.linkshellService.clearLinkshellDetail(); }
  createLinkshell(input: ActivityCreateLinkshellInput): Promise<void> { return this.linkshellService.createLinkshell(input); }
  updateLinkshell(linkshellId: number, input: ActivityCreateLinkshellInput & { lootStructure?: ActivityLootStructure | null; enableHnmSection?: boolean | null; enableMissions?: boolean | null; enableAuctions?: boolean | null; enableToDs?: boolean | null; enableEndgame?: boolean | null; enableEvents?: boolean | null; enableDkp?: boolean | null; enableItems?: boolean | null; enableRevenue?: boolean | null; dkpRoundingIncrement?: ActivityDkpRoundingIncrement | null; hiddenTodMonsters?: string[] | null }): Promise<void> { return this.linkshellService.updateLinkshell(linkshellId, input); }
  setPrimaryLinkshell(linkshellId: number): Promise<void> { return this.linkshellService.setPrimaryLinkshell(linkshellId); }
  deleteLinkshell(linkshellId: number): Promise<void> { return this.linkshellService.deleteLinkshell(linkshellId); }
  leaveLinkshell(linkshellId: number): Promise<void> { return this.linkshellService.leaveLinkshell(linkshellId); }
  removeLinkshellMember(linkshellId: number, memberId: number): Promise<void> { return this.linkshellService.removeLinkshellMember(linkshellId, memberId); }
  updateLinkshellMemberRole(linkshellId: number, memberId: number, role: string, characterName?: string | null): Promise<void> { return this.linkshellService.updateLinkshellMemberRole(linkshellId, memberId, role, characterName); }
  loadLinkshellRoles(linkshellId: number): Promise<ActivityLinkshellRolesResponse | null> { return this.linkshellService.loadLinkshellRoles(linkshellId); }
  createLinkshellRole(linkshellId: number, input: ActivityLinkshellRolePermissionsInput): Promise<boolean> { return this.linkshellService.createLinkshellRole(linkshellId, input); }
  updateLinkshellRole(linkshellId: number, roleId: number, input: ActivityLinkshellRolePermissionsInput): Promise<boolean> { return this.linkshellService.updateLinkshellRole(linkshellId, roleId, input); }
  deleteLinkshellRole(linkshellId: number, roleId: number): Promise<boolean> { return this.linkshellService.deleteLinkshellRole(linkshellId, roleId); }

  // --- EventService ---
  signUpForEvent(eventId: number, jobId: number, adHocJob?: ActivityQuickJoinInput): Promise<void> { return this.eventService.signUpForEvent(eventId, jobId, adHocJob); }
  unsignFromEvent(eventId: number): Promise<void> { return this.eventService.unsignFromEvent(eventId); }
  createEvent(input: ActivityCreateEventInput): Promise<void> { return this.eventService.createEvent(input); }
  updateEvent(eventId: number, input: ActivityCreateEventInput): Promise<void> { return this.eventService.updateEvent(eventId, input); }
  startEvent(eventId: number, absentParticipantIds?: number[]): Promise<void> { return this.eventService.startEvent(eventId, absentParticipantIds); }
  endEvent(eventId: number): Promise<void> { return this.eventService.endEvent(eventId); }
  cancelEvent(eventId: number): Promise<void> { return this.eventService.cancelEvent(eventId); }
  takeBreak(eventId: number): Promise<void> { return this.eventService.takeBreak(eventId); }
  returnFromBreak(eventId: number): Promise<void> { return this.eventService.returnFromBreak(eventId); }
  sendParticipantToBreak(eventId: number, participantId: number): Promise<void> { return this.eventService.sendParticipantToBreak(eventId, participantId); }
  resumeParticipantFromBreak(eventId: number, participantId: number): Promise<void> { return this.eventService.resumeParticipantFromBreak(eventId, participantId); }
  verifyParticipant(eventId: number, participantId: number, isVerified: boolean): Promise<void> { return this.eventService.verifyParticipant(eventId, participantId, isVerified); }
  resetParticipantVerification(eventId: number, participantId: number): Promise<void> { return this.eventService.resetParticipantVerification(eventId, participantId); }
  verifyReturn(eventId: number, ledgerEntryId: number): Promise<void> { return this.eventService.verifyReturn(eventId, ledgerEntryId); }
  denyReturn(eventId: number, ledgerEntryId: number): Promise<void> { return this.eventService.denyReturn(eventId, ledgerEntryId); }
  addLoot(eventId: number, input: ActivityLootInput): Promise<void> { return this.eventService.addLoot(eventId, input); }
  quickJoinLiveEvent(eventId: number, input: ActivityQuickJoinInput): Promise<void> { return this.eventService.quickJoinLiveEvent(eventId, input); }
  removeAttendanceWindowAttendee(attendeeId: number): Promise<boolean> { return this.eventService.removeAttendanceWindowAttendee(attendeeId); }

  // --- LinkshellContentService (rules / announcements / items / revenue) ---
  createRule(linkshellId: number, title: string, details: string): Promise<void> { return this.linkshellContentService.createRule(linkshellId, title, details); }
  updateRule(ruleId: number, title: string, details: string): Promise<void> { return this.linkshellContentService.updateRule(ruleId, title, details); }
  deleteRule(ruleId: number): Promise<void> { return this.linkshellContentService.deleteRule(ruleId); }
  createAnnouncement(linkshellId: number, title: string, details: string): Promise<void> { return this.linkshellContentService.createAnnouncement(linkshellId, title, details); }
  updateAnnouncement(announcementId: number, title: string, details: string): Promise<void> { return this.linkshellContentService.updateAnnouncement(announcementId, title, details); }
  deleteAnnouncement(announcementId: number): Promise<void> { return this.linkshellContentService.deleteAnnouncement(announcementId); }
  createItem(linkshellId: number, input: ActivityItemInput): Promise<void> { return this.linkshellContentService.createItem(linkshellId, input); }
  updateItem(itemId: number, input: ActivityItemInput): Promise<void> { return this.linkshellContentService.updateItem(itemId, input); }
  deleteItem(itemId: number): Promise<void> { return this.linkshellContentService.deleteItem(itemId); }
  createRevenueEntry(linkshellId: number, input: ActivityRevenueInput): Promise<void> { return this.linkshellContentService.createRevenueEntry(linkshellId, input); }
  deleteRevenueEntry(entryId: number): Promise<void> { return this.linkshellContentService.deleteRevenueEntry(entryId); }
}
