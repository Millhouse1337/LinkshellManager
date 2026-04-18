import { Injectable, computed, signal } from '@angular/core';
import { DiscordSDK } from '@discord/embedded-app-sdk';

declare const NG_APP_DISCORD_CLIENT_ID: string;

type ActivityStatus = 'idle' | 'initializing' | 'standalone' | 'ready' | 'error';

type DiscordSdkContextSource = DiscordSDK & {
  channelId?: string;
  guildId?: string;
  instanceId?: string;
  platform?: string;
};

interface DiscordTokenExchangeResponse {
  accessToken: string;
  expiresIn: number;
  localUser?: LocalActivityUser;
  scope: string;
  tokenType: string;
}

interface DiscordUser {
  id: string;
  username: string;
  discriminator: string;
  global_name?: string | null;
  avatar?: string | null;
}

interface DiscordApplication {
  id: string;
  name: string;
  description?: string;
}

interface DiscordSession {
  access_token: string;
  expires: string;
  scopes: string[];
  user: DiscordUser;
  application: DiscordApplication;
}

export interface DiscordParticipant {
  id: string;
  username: string;
  global_name?: string | null;
}

interface DiscordContext {
  channelId: string | null;
  guildId: string | null;
  instanceId: string | null;
  platform: string | null;
}

interface LocalActivityUser {
  id: string;
  discordUserId: string;
  username: string;
  discriminator: string;
  globalName?: string | null;
  avatar?: string | null;
  identityUserId?: string | null;
  createdAtUtc: string;
  lastSeenAtUtc: string;
  isNewUser: boolean;
  appUser?: ActivityAppUser | null;
}

interface ActivityAppUser {
  id: string;
  userName: string;
  characterName?: string | null;
  timeZone?: string | null;
  primaryLinkshellId?: number | null;
  primaryLinkshellName?: string | null;
}

interface ActivityLinkshell {
  id: number;
  name: string;
  rank?: string | null;
  status?: string | null;
  linkshellDkp?: number | null;
  memberCount: number;
  details?: string | null;
}

interface ActivityPrimaryLinkshell {
  id: number;
  name: string;
  memberCount: number;
  details?: string | null;
  members: ActivityMember[];
}

interface ActivityLinkshellDetail {
  id: number;
  name: string;
  memberCount: number;
  details?: string | null;
  status?: string | null;
  members: ActivityMember[];
}

interface ActivityMember {
  id: number;
  appUserId?: string | null;
  characterName: string;
  rank?: string | null;
  status?: string | null;
  linkshellDkp?: number | null;
}

interface ActivityEventJob {
  id: number;
  jobName?: string | null;
  subJobName?: string | null;
  jobType?: string | null;
  quantity?: number | null;
  signedUp?: number | null;
  enlisted: string[];
}

interface ActivityEventParticipant {
  id: number;
  appUserId?: string | null;
  characterName?: string | null;
  jobName?: string | null;
  subJobName?: string | null;
  jobType?: string | null;
  isQuickJoin: boolean;
  isVerified?: boolean | null;
  isOnBreak?: boolean | null;
  proctor?: string | null;
  startTime?: string | null;
  resumeTime?: string | null;
  pauseTime?: string | null;
  duration?: number | null;
  eventDkp?: number | null;
  statusLedger: ActivityStatusLedgerEntry[];
}

interface ActivityLootEntry {
  id: number;
  itemName?: string | null;
  itemWinner?: string | null;
  winningDkpSpent?: number | null;
}

interface ActivityTodLootEntry {
  id: number;
  itemName?: string | null;
  itemWinner?: string | null;
  winningDkpSpent?: number | null;
}

interface ActivityTodEntry {
  id: number;
  linkshellId: number;
  monsterName: string;
  dayNumber?: number | null;
  time?: string | null;
  claim: boolean;
  cooldown?: string | null;
  repopTime?: string | null;
  interval?: string | null;
  lootCount: number;
  lootDetails: ActivityTodLootEntry[];
}

interface ActivityEvent {
  id: number;
  linkshellId: number;
  name?: string | null;
  type?: string | null;
  location?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  commencementStartTime?: string | null;
  duration?: number | null;
  dkpPerHour?: number | null;
  details?: string | null;
  participantCount: number;
  requestedSlots: number;
  currentParticipation?: ActivityParticipation | null;
  participants: ActivityEventParticipant[];
  loot: ActivityLootEntry[];
  jobs: ActivityEventJob[];
}

interface ActivityParticipation {
  id: number;
  characterName?: string | null;
  jobName?: string | null;
  subJobName?: string | null;
  jobType?: string | null;
  isQuickJoin: boolean;
  isVerified?: boolean | null;
  isOnBreak?: boolean | null;
  statusLedger: ActivityStatusLedgerEntry[];
}

interface ActivityStatusLedgerEntry {
  id: number;
  actionType: string;
  occurredAt: string;
  requiresVerification: boolean;
  verifiedAt?: string | null;
  verifiedBy?: string | null;
}

interface ActivityHistory {
  id: number;
  linkshellId: number;
  name?: string | null;
  type?: string | null;
  location?: string | null;
  endTime?: string | null;
  duration?: number | null;
  participantCount: number;
}

interface ActivityHistoryParticipant {
  id: number;
  appUserId?: string | null;
  characterName?: string | null;
  jobName?: string | null;
  subJobName?: string | null;
  jobType?: string | null;
  duration?: number | null;
  eventDkp?: number | null;
  isVerified?: boolean | null;
}

interface ActivityHistoryDetail {
  id: number;
  linkshellId: number;
  name?: string | null;
  type?: string | null;
  location?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  duration?: number | null;
  dkpPerHour?: number | null;
  details?: string | null;
  participants: ActivityHistoryParticipant[];
}

interface ActivityDkpHistoryMember {
  appUserId: string;
  characterName: string;
  currentBalance: number;
}

interface ActivityDkpLedgerEntry {
  id: number;
  entryType: string;
  amount: number;
  runningBalance: number;
  occurredAt: string;
  eventName?: string | null;
  eventType?: string | null;
  eventLocation?: string | null;
  eventStartTime?: string | null;
  eventEndTime?: string | null;
  itemName?: string | null;
  details?: string | null;
}

interface ActivityDkpHistory {
  linkshellId?: number | null;
  linkshellName?: string | null;
  selectedAppUserId?: string | null;
  selectedMemberName?: string | null;
  currentBalance: number;
  members: ActivityDkpHistoryMember[];
  entries: ActivityDkpLedgerEntry[];
}

interface ActivityAuctionItem {
  id: number;
  itemName?: string | null;
  itemType?: string | null;
  startingBidDkp?: number | null;
  currentHighestBid?: number | null;
  currentHighestBidder?: string | null;
  currentHighestBidderAppUserId?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  status?: string | null;
  notes?: string | null;
  bidCount: number;
}

interface ActivityAuction {
  id: number;
  linkshellId: number;
  title?: string | null;
  createdBy?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  startedAt?: string | null;
  status: string;
  canEdit: boolean;
  canStart: boolean;
  canClose: boolean;
  items: ActivityAuctionItem[];
}

interface ActivityAuctionBid {
  id: number;
  characterName: string;
  bidAmount: number;
  createdAt: string;
}

interface ActivityAuctionHistory {
  id: number;
  linkshellId: number;
  title?: string | null;
  createdBy?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  startedAt?: string | null;
  closedAt: string;
  items: ActivityAuctionItem[];
}

interface ActivityInvite {
  id: number;
  appUserId: string;
  linkshellId: number;
  appUserDisplayName: string;
  linkshellName: string;
  status: string;
}

export interface ActivityUserSearchResult {
  id: string;
  displayName: string;
  userName?: string | null;
  primaryLinkshellName?: string | null;
}

export interface ActivityLinkshellSearchResult {
  id: number;
  name: string;
  details?: string | null;
  memberCount: number;
  status?: string | null;
}

export interface ActivityParticipantInviteCandidate {
  appUserId: string;
  discordUserId: string;
  displayName: string;
  userName?: string | null;
  primaryLinkshellName?: string | null;
}

interface ActivityOverviewStats {
  linkshellCount: number;
  activeEventCount: number;
  completedEventCount: number;
  liveEventCount: number;
}

interface ActivityOverview {
  appUser: ActivityAppUser;
  linkshells: ActivityLinkshell[];
  primaryLinkshell?: ActivityPrimaryLinkshell | null;
  activeEvents: ActivityEvent[];
  pendingInvites: ActivityInvite[];
  sentInvites: ActivityInvite[];
  incomingJoinRequests: ActivityInvite[];
  outgoingJoinRequests: ActivityInvite[];
  recentHistory: ActivityHistory[];
  recentTods: ActivityTodEntry[];
  stats: ActivityOverviewStats;
}

export interface ActivityCreateEventJobInput {
  jobName: string;
  subJobName: string;
  jobType?: string | null;
  quantity?: number | null;
  details?: string | null;
}

export interface ActivityCreateEventInput {
  linkshellId: number;
  eventName: string;
  eventType?: string | null;
  eventLocation?: string | null;
  startTimeLocal?: string | null;
  endTimeLocal?: string | null;
  duration?: number | null;
  dkpPerHour?: number | null;
  details?: string | null;
  jobs: ActivityCreateEventJobInput[];
}

export interface ActivityCreateLinkshellInput {
  name: string;
  details?: string | null;
}

export interface ActivityLootInput {
  itemName: string;
  itemWinner?: string | null;
  winningDkpSpent?: number | null;
}

export interface ActivityTodLootInput {
  itemName?: string | null;
  itemWinner?: string | null;
  winningDkpSpent?: number | null;
}

export interface ActivityCreateTodInput {
  linkshellId: number;
  monsterName: string;
  dayNumber?: number | null;
  claim: boolean;
  timeLocal: string;
  cooldown?: string | null;
  interval?: string | null;
  noLoot: boolean;
  lootDetails: ActivityTodLootInput[];
}

export interface ActivityQuickJoinInput {
  jobName: string;
  subJobName: string;
  jobType: string;
}

export interface ActivityAuctionItemInput {
  id: number;
  itemName: string;
  itemType?: string | null;
  startingBidDkp?: number | null;
  notes?: string | null;
}

export interface ActivityCreateAuctionInput {
  linkshellId: number;
  title: string;
  startTimeLocal?: string | null;
  endTimeLocal?: string | null;
  items: ActivityAuctionItemInput[];
}

export interface ActivityUpdateProfileInput {
  characterName: string;
  timeZone?: string | null;
}

interface DiscordRpcErrorLike {
  code?: number;
  cmd?: string;
  data?: {
    code?: number;
    message?: string;
  };
  evt?: string | null;
  message?: string;
}

@Injectable({ providedIn: 'root' })
export class DiscordActivityService {
  private readonly clientId = NG_APP_DISCORD_CLIENT_ID ?? '';
  private readonly exchangePath = '/auth/discord/exchange';
  private readonly authorizeScopes = ['identify', 'guilds', 'applications.commands'] as const;
  private readonly browserTimeZone = this.resolveBrowserTimeZone();
  private initializationPromise: Promise<void> | null = null;
  private sdk: DiscordSdkContextSource | null = null;

  readonly status = signal<ActivityStatus>('idle');
  readonly phase = signal('Waiting to initialize');
  readonly error = signal<string | null>(null);
  readonly session = signal<DiscordSession | null>(null);
  readonly localUser = signal<LocalActivityUser | null>(null);
  readonly participants = signal<DiscordParticipant[]>([]);
  readonly context = signal<DiscordContext | null>(null);
  readonly overview = signal<ActivityOverview | null>(null);
  readonly actionMessage = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly busyEventId = signal<number | null>(null);
  readonly busyLinkshellId = signal<number | null>(null);
  readonly busyMemberId = signal<number | null>(null);
  readonly inviteSearchResults = signal<ActivityUserSearchResult[]>([]);
  readonly inviteSearchBusy = signal(false);
  readonly busyInviteId = signal<number | null>(null);
  readonly busyProfileSave = signal(false);
  readonly participantInviteCandidates = signal<ActivityParticipantInviteCandidate[]>([]);
  readonly participantInviteBusy = signal(false);
  readonly linkshellSearchResults = signal<ActivityLinkshellSearchResult[]>([]);
  readonly linkshellSearchBusy = signal(false);
  readonly busyRefresh = signal(false);
  readonly linkshellDetail = signal<ActivityLinkshellDetail | null>(null);
  readonly linkshellDetailBusy = signal(false);
  readonly historyList = signal<ActivityHistory[]>([]);
  readonly historyDetail = signal<ActivityHistoryDetail | null>(null);
  readonly historyBusy = signal(false);
  readonly dkpHistory = signal<ActivityDkpHistory | null>(null);
  readonly dkpHistoryBusy = signal(false);
  readonly auctions = signal<ActivityAuction[]>([]);
  readonly auctionsBusy = signal(false);
  readonly auctionHistory = signal<ActivityAuctionHistory[]>([]);
  readonly auctionHistoryBusy = signal(false);
  readonly auctionBids = signal<Record<number, ActivityAuctionBid[]>>({});
  readonly busyAuctionId = signal<number | null>(null);
  readonly busyAuctionItemId = signal<number | null>(null);
  readonly busyTodId = signal<number | null>(null);
  readonly busyTodSave = signal(false);

  readonly isReady = computed(() => this.status() === 'ready');
  readonly isStandalonePreview = computed(() => this.status() === 'standalone');

  async initialize(): Promise<void> {
    if (this.initializationPromise) {
      return this.initializationPromise;
    }

    this.initializationPromise = this.initializeInternal();
    return this.initializationPromise;
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
      await this.withTimeout(sdk.ready(), 8000, 'Discord SDK did not become ready.');

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

      this.session.set(auth);
      this.localUser.set(token.localUser ?? null);

      this.phase.set('Resolving the local app user');
      this.localUser.set(await this.fetchLocalUser(token.accessToken));

      this.phase.set('Loading linkshell activity data');
      this.overview.set(await this.fetchOverview(token.accessToken));

      this.phase.set('Loading activity participants');
      const participantsResponse = (await sdk.commands.getInstanceConnectedParticipants()) as {
        participants?: DiscordParticipant[];
      };
      this.participants.set(participantsResponse.participants ?? []);

      this.status.set('ready');
      this.phase.set('Discord Activity connected');
    } catch (error) {
      this.setError(this.formatError(error));
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

  private async fetchOverview(accessToken?: string): Promise<ActivityOverview> {
    return this.fetchActivityJson<ActivityOverview>('/api/activity/overview', accessToken);
  }

  private async fetchActivityJson<T>(path: string, accessToken?: string): Promise<T> {
    const headers: Record<string, string> = {};
    if (accessToken) {
      headers['Authorization'] = `Bearer ${accessToken}`;
    }

    const response = await fetch(path, {
      headers,
      cache: 'no-store',
      credentials: 'include'
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
          : `Loading linkshell activity data failed with status ${response.status}.`;
      throw new Error(message);
    }

    return payload as T;
  }

  async signUpForEvent(eventId: number, jobId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/signup`, { jobId });
      await this.refreshOverview();
      this.actionMessage.set('Event signup updated.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Signing up for the event failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async unsignFromEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/unsign`);
      await this.refreshOverview();
      this.actionMessage.set('Event signup removed.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Removing the event signup failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async refreshOverview(): Promise<void> {
    const accessToken = this.session()?.access_token;
    this.overview.set(await this.fetchOverview(accessToken));
  }

  async refreshActivityData(): Promise<void> {
    this.busyRefresh.set(true);
    this.actionError.set(null);

    try {
      const accessToken = this.session()?.access_token;
      this.overview.set(await this.fetchOverview(accessToken));

      const appUser = this.overview()?.appUser;
      const currentLocalUser = this.localUser();
      if (appUser && currentLocalUser) {
        this.localUser.set({
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

      this.actionMessage.set('Activity data refreshed.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Refreshing activity data failed.'));
    } finally {
      this.busyRefresh.set(false);
    }
  }

  async loadHistoryList(): Promise<void> {
    this.historyBusy.set(true);

    try {
      const accessToken = this.session()?.access_token;
      this.historyList.set(await this.fetchActivityJson<ActivityHistory[]>('/api/activity/history', accessToken));
    } catch (error) {
      this.historyList.set([]);
      this.actionError.set(this.formatActionError(error, 'Loading event history failed.'));
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
      const accessToken = this.session()?.access_token;
      this.historyDetail.set(
        await this.fetchActivityJson<ActivityHistoryDetail>(`/api/activity/history/${historyId}`, accessToken)
      );
    } catch (error) {
      this.historyDetail.set(null);
      this.actionError.set(this.formatActionError(error, 'Loading event history details failed.'));
    } finally {
      this.historyBusy.set(false);
    }
  }

  clearHistoryDetail(): void {
    this.historyDetail.set(null);
  }

  async loadDkpHistory(linkshellId?: number | null, appUserId?: string | null): Promise<ActivityDkpHistory | null> {
    this.dkpHistoryBusy.set(true);

    try {
      const accessToken = this.session()?.access_token;
      const query = new URLSearchParams();
      if (linkshellId) {
        query.set('linkshellId', String(linkshellId));
      }
      if (appUserId) {
        query.set('appUserId', appUserId);
      }

      const path = query.size > 0 ? `/api/activity/dkp-history?${query.toString()}` : '/api/activity/dkp-history';
      const history = await this.fetchActivityJson<ActivityDkpHistory>(path, accessToken);
      this.dkpHistory.set(history);
      return history;
    } catch (error) {
      this.dkpHistory.set(null);
      this.actionError.set(this.formatActionError(error, 'Loading DKP history failed.'));
      return null;
    } finally {
      this.dkpHistoryBusy.set(false);
    }
  }

  clearDkpHistory(): void {
    this.dkpHistory.set(null);
  }

  async loadAuctions(linkshellId?: number | null): Promise<void> {
    this.auctionsBusy.set(true);

    try {
      const accessToken = this.session()?.access_token;
      const query = new URLSearchParams();
      if (linkshellId) {
        query.set('linkshellId', String(linkshellId));
      }

      const path = query.size > 0 ? `/api/activity/auctions?${query.toString()}` : '/api/activity/auctions';
      this.auctions.set(await this.fetchActivityJson<ActivityAuction[]>(path, accessToken));
    } catch (error) {
      this.auctions.set([]);
      this.actionError.set(this.formatActionError(error, 'Loading auctions failed.'));
    } finally {
      this.auctionsBusy.set(false);
    }
  }

  async loadAuctionHistory(linkshellId?: number | null): Promise<void> {
    this.auctionHistoryBusy.set(true);

    try {
      const accessToken = this.session()?.access_token;
      const query = new URLSearchParams();
      if (linkshellId) {
        query.set('linkshellId', String(linkshellId));
      }

      const path = query.size > 0 ? `/api/activity/auction-history?${query.toString()}` : '/api/activity/auction-history';
      this.auctionHistory.set(await this.fetchActivityJson<ActivityAuctionHistory[]>(path, accessToken));
    } catch (error) {
      this.auctionHistory.set([]);
      this.actionError.set(this.formatActionError(error, 'Loading auction history failed.'));
    } finally {
      this.auctionHistoryBusy.set(false);
    }
  }

  async loadAuctionItemBids(itemId: number): Promise<void> {
    if (itemId <= 0) {
      return;
    }

    try {
      const accessToken = this.session()?.access_token;
      const bids = await this.fetchActivityJson<ActivityAuctionBid[]>(`/api/activity/auction-items/${itemId}/bids`, accessToken);
      this.auctionBids.update(current => ({ ...current, [itemId]: bids }));
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Loading auction bids failed.'));
    }
  }

  clearAuctionState(): void {
    this.auctions.set([]);
    this.auctionHistory.set([]);
    this.auctionBids.set({});
  }

  async createAuction(input: ActivityCreateAuctionInput): Promise<void> {
    this.busyAuctionId.set(input.linkshellId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction('/api/activity/auctions', {
        linkshellId: input.linkshellId,
        title: input.title,
        startTimeLocal: input.startTimeLocal || null,
        endTimeLocal: input.endTimeLocal || null,
        items: input.items
      });
      await this.refreshOverview();
      await this.loadAuctions(input.linkshellId);
      await this.loadAuctionHistory(input.linkshellId);
      this.actionMessage.set('Auction created.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Creating the auction failed.'));
      throw error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async updateAuction(auctionId: number, input: ActivityCreateAuctionInput): Promise<void> {
    this.busyAuctionId.set(auctionId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/auctions/${auctionId}/update`, {
        linkshellId: input.linkshellId,
        title: input.title,
        startTimeLocal: input.startTimeLocal || null,
        endTimeLocal: input.endTimeLocal || null,
        items: input.items
      });
      await this.refreshOverview();
      await this.loadAuctions(input.linkshellId);
      this.actionMessage.set('Auction updated.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Updating the auction failed.'));
      throw error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async startAuction(auctionId: number, linkshellId: number): Promise<void> {
    this.busyAuctionId.set(auctionId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/auctions/${auctionId}/start`);
      await this.loadAuctions(linkshellId);
      this.actionMessage.set('Auction started.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Starting the auction failed.'));
      throw error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async placeAuctionBid(itemId: number, bidAmount: number, linkshellId: number): Promise<void> {
    this.busyAuctionItemId.set(itemId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/auction-items/${itemId}/bid`, { bidAmount });
      await this.loadAuctions(linkshellId);
      await this.loadAuctionItemBids(itemId);
      await this.refreshOverview();
      this.actionMessage.set('Bid submitted.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Submitting the bid failed.'));
      throw error;
    } finally {
      this.busyAuctionItemId.set(null);
    }
  }

  async closeAuction(auctionId: number, linkshellId: number): Promise<void> {
    this.busyAuctionId.set(auctionId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/auctions/${auctionId}/close`);
      await this.refreshOverview();
      await this.loadAuctions(linkshellId);
      await this.loadAuctionHistory(linkshellId);
      this.actionMessage.set('Auction closed and archived.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Closing the auction failed.'));
      throw error;
    } finally {
      this.busyAuctionId.set(null);
    }
  }

  async markAuctionHistoryItemReceived(itemId: number, linkshellId: number): Promise<void> {
    this.busyAuctionItemId.set(itemId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/auction-history/items/${itemId}/received`);
      await this.loadAuctionHistory(linkshellId);
      this.actionMessage.set('Auction history item marked received.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Updating auction history failed.'));
      throw error;
    } finally {
      this.busyAuctionItemId.set(null);
    }
  }

  async undoAuctionHistoryItem(itemId: number, linkshellId: number): Promise<void> {
    this.busyAuctionItemId.set(itemId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/auction-history/items/${itemId}/undo`);
      await this.loadAuctionHistory(linkshellId);
      this.actionMessage.set('Auction history status updated.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Updating auction history failed.'));
      throw error;
    } finally {
      this.busyAuctionItemId.set(null);
    }
  }

  async loadLinkshellDetail(linkshellId: number): Promise<void> {
    if (linkshellId <= 0) {
      this.linkshellDetail.set(null);
      return;
    }

    this.linkshellDetailBusy.set(true);

    try {
      const accessToken = this.session()?.access_token;
      this.linkshellDetail.set(
        await this.fetchActivityJson<ActivityLinkshellDetail>(`/api/activity/linkshells/${linkshellId}`, accessToken)
      );
    } catch (error) {
      this.linkshellDetail.set(null);
      this.actionError.set(this.formatActionError(error, 'Loading linkshell details failed.'));
    } finally {
      this.linkshellDetailBusy.set(false);
    }
  }

  clearLinkshellDetail(): void {
    this.linkshellDetail.set(null);
  }

  async createEvent(input: ActivityCreateEventInput): Promise<void> {
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction('/api/activity/events', {
        linkshellId: input.linkshellId,
        eventName: input.eventName,
        eventType: input.eventType || null,
        eventLocation: input.eventLocation || null,
        startTimeLocal: input.startTimeLocal || null,
        endTimeLocal: input.endTimeLocal || null,
        duration: input.duration ?? null,
        dkpPerHour: input.dkpPerHour ?? null,
        details: input.details || null,
        jobs: input.jobs
          .filter(job => job.jobName.trim().length > 0)
          .map(job => ({
            jobName: job.jobName,
            subJobName: job.subJobName,
            jobType: job.jobType || null,
            quantity: job.quantity ?? null,
            details: job.details || null
          }))
      });

      await this.refreshOverview();
      this.actionMessage.set('Event created.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Creating the event failed.'));
      throw error;
    }
  }

  async updateEvent(eventId: number, input: ActivityCreateEventInput): Promise<void> {
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/update`, {
        linkshellId: input.linkshellId,
        eventName: input.eventName,
        eventType: input.eventType || null,
        eventLocation: input.eventLocation || null,
        startTimeLocal: input.startTimeLocal || null,
        endTimeLocal: input.endTimeLocal || null,
        duration: input.duration ?? null,
        dkpPerHour: input.dkpPerHour ?? null,
        details: input.details || null,
        jobs: input.jobs
          .filter(job => job.jobName.trim().length > 0)
          .map(job => ({
            jobName: job.jobName,
            subJobName: job.subJobName,
            jobType: job.jobType || null,
            quantity: job.quantity ?? null,
            details: job.details || null
          }))
      });

      await this.refreshOverview();
      this.actionMessage.set('Event updated.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Updating the event failed.'));
      throw error;
    }
  }

  async createLinkshell(input: ActivityCreateLinkshellInput): Promise<void> {
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction('/api/activity/linkshells', {
        name: input.name,
        details: input.details || null
      });
      await this.refreshOverview();
      this.actionMessage.set('Linkshell created.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Creating the linkshell failed.'));
      throw error;
    }
  }

  async updateProfile(input: ActivityUpdateProfileInput): Promise<void> {
    this.busyProfileSave.set(true);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction('/api/activity/profile', {
        characterName: input.characterName,
        timeZone: input.timeZone || null
      });
      await this.refreshOverview();

      const appUser = this.overview()?.appUser;
      const currentLocalUser = this.localUser();
      if (appUser && currentLocalUser) {
        this.localUser.set({
          ...currentLocalUser,
          appUser: {
            ...currentLocalUser.appUser,
            ...appUser
          }
        });
      }

      this.actionMessage.set('Profile updated.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Updating the profile failed.'));
      throw error;
    } finally {
      this.busyProfileSave.set(false);
    }
  }

  async updateLinkshell(linkshellId: number, input: ActivityCreateLinkshellInput): Promise<void> {
    this.busyLinkshellId.set(linkshellId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/linkshells/${linkshellId}/update`, {
        name: input.name,
        details: input.details || null
      });
      await this.refreshOverview();
      this.actionMessage.set('Linkshell updated.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Updating the linkshell failed.'));
      throw error;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async setPrimaryLinkshell(linkshellId: number): Promise<void> {
    this.busyLinkshellId.set(linkshellId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/linkshells/${linkshellId}/primary`);
      await this.refreshOverview();
      this.actionMessage.set('Primary linkshell updated.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Updating the primary linkshell failed.'));
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async searchPlayers(query: string, linkshellId: number): Promise<void> {
    if (!query.trim() || query.trim().length < 2 || linkshellId <= 0) {
      this.inviteSearchResults.set([]);
      return;
    }

    this.inviteSearchBusy.set(true);
    this.actionError.set(null);

    try {
      const headers: Record<string, string> = {};
      const accessToken = this.session()?.access_token;
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
      this.actionError.set(this.formatActionError(error, 'Searching players failed.'));
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
      const response = await this.postActivityJson<ActivityParticipantInviteCandidate[]>(
        '/api/activity/invites/participants',
        {
          linkshellId,
          discordUserIds: normalizedIds
        }
      );

      this.participantInviteCandidates.set(response);
    } catch (error) {
      this.participantInviteCandidates.set([]);
      this.actionError.set(this.formatActionError(error, 'Loading connected participant invite targets failed.'));
    } finally {
      this.participantInviteBusy.set(false);
    }
  }

  async sendInvite(linkshellId: number, appUserId: string): Promise<void> {
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/linkshells/${linkshellId}/invites`, { appUserId });
      await this.refreshOverview();
      this.actionMessage.set('Invite sent.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Sending the invite failed.'));
    }
  }

  async searchLinkshells(query: string): Promise<void> {
    this.linkshellSearchBusy.set(true);
    this.actionError.set(null);

    try {
      const headers: Record<string, string> = {};
      const accessToken = this.session()?.access_token;
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
      this.actionError.set(this.formatActionError(error, 'Searching linkshells failed.'));
    } finally {
      this.linkshellSearchBusy.set(false);
    }
  }

  async requestJoinLinkshell(linkshellId: number): Promise<void> {
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/linkshells/${linkshellId}/join-request`);
      await this.refreshOverview();
      this.actionMessage.set('Join request sent.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Sending the join request failed.'));
    }
  }

  async approveJoinRequest(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/join-requests/${inviteId}/approve`);
      await this.refreshOverview();
      this.actionMessage.set('Join request approved.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Approving the join request failed.'));
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async declineJoinRequest(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/join-requests/${inviteId}/decline`);
      await this.refreshOverview();
      this.actionMessage.set('Join request declined.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Declining the join request failed.'));
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async acceptInvite(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/invites/${inviteId}/accept`);
      await this.refreshOverview();
      this.actionMessage.set('Invite accepted.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Accepting the invite failed.'));
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async declineInvite(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/invites/${inviteId}/decline`);
      await this.refreshOverview();
      this.actionMessage.set('Invite declined.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Declining the invite failed.'));
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async revokeInvite(inviteId: number): Promise<void> {
    this.busyInviteId.set(inviteId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/invites/${inviteId}/revoke`);
      await this.refreshOverview();
      this.actionMessage.set('Invite revoked.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Revoking the invite failed.'));
    } finally {
      this.busyInviteId.set(null);
    }
  }

  async removeLinkshellMember(linkshellId: number, memberId: number): Promise<void> {
    this.busyMemberId.set(memberId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/linkshells/${linkshellId}/members/${memberId}/remove`);
      await this.refreshOverview();
      this.actionMessage.set('Linkshell member removed.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Removing the linkshell member failed.'));
    } finally {
      this.busyMemberId.set(null);
    }
  }

  async updateLinkshellMemberRole(linkshellId: number, memberId: number, role: 'Member' | 'Officer' | 'Leader'): Promise<void> {
    this.busyMemberId.set(memberId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/linkshells/${linkshellId}/members/${memberId}/role`, { role });
      await this.refreshOverview();
      this.actionMessage.set(
        role === 'Leader'
          ? 'Leadership transferred.'
          : role === 'Officer'
            ? 'Member promoted to officer.'
            : 'Member role updated.'
      );
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Updating the member role failed.'));
    } finally {
      this.busyMemberId.set(null);
    }
  }

  async deleteLinkshell(linkshellId: number): Promise<void> {
    this.busyLinkshellId.set(linkshellId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/linkshells/${linkshellId}/delete`);
      await this.refreshOverview();
      this.actionMessage.set('Linkshell deleted.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Deleting the linkshell failed.'));
      throw error;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async leaveLinkshell(linkshellId: number): Promise<void> {
    this.busyLinkshellId.set(linkshellId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/linkshells/${linkshellId}/leave`);
      await this.refreshOverview();
      this.actionMessage.set('Linkshell membership updated.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Leaving the linkshell failed.'));
      throw error;
    } finally {
      this.busyLinkshellId.set(null);
    }
  }

  async startEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/start`);
      await this.refreshOverview();
      this.actionMessage.set('Event started.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Starting the event failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async cancelEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/cancel`);
      await this.refreshOverview();
      this.actionMessage.set('Event canceled.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Canceling the event failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async verifyParticipant(eventId: number, participantId: number, isVerified: boolean): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/verify`, {
        participantId,
        isVerified
      });
      await this.refreshOverview();
      this.actionMessage.set(isVerified ? 'Participant marked present.' : 'Participant marked absent.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Updating attendance verification failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async resetParticipantVerification(eventId: number, participantId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/verify/reset`, { participantId });
      await this.refreshOverview();
      this.actionMessage.set('Attendance verification reset.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Resetting attendance verification failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async takeBreak(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/break`);
      await this.refreshOverview();
      this.actionMessage.set('You are now marked as on break.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Updating break status failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async returnFromBreak(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/break/return`);
      await this.refreshOverview();
      this.actionMessage.set('You returned from break and are awaiting verification.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Returning from break failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async verifyReturn(eventId: number, ledgerEntryId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/verify-return`, {
        ledgerEntryId
      });
      await this.refreshOverview();
      this.actionMessage.set('Break return verified.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Verifying the break return failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async addLoot(eventId: number, input: ActivityLootInput): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/loot`, {
        itemName: input.itemName,
        itemWinner: input.itemWinner || null,
        winningDkpSpent: input.winningDkpSpent ?? null
      });
      await this.refreshOverview();
      this.actionMessage.set('Loot entry added.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Adding loot failed.'));
      throw error;
    } finally {
      this.busyEventId.set(null);
    }
  }

  async createTod(input: ActivityCreateTodInput): Promise<void> {
    this.busyTodSave.set(true);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction('/api/activity/tods', {
        linkshellId: input.linkshellId,
        monsterName: input.monsterName,
        dayNumber: input.dayNumber ?? null,
        claim: input.claim,
        timeLocal: input.timeLocal,
        cooldown: input.cooldown || null,
        interval: input.interval || null,
        noLoot: input.noLoot,
        lootDetails: input.lootDetails.map(detail => ({
          itemName: detail.itemName || null,
          itemWinner: detail.itemWinner || null,
          winningDkpSpent: detail.winningDkpSpent ?? null
        }))
      });
      await this.refreshOverview();
      this.actionMessage.set('ToD entry saved.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Saving the ToD entry failed.'));
      throw error;
    } finally {
      this.busyTodSave.set(false);
    }
  }

  async deleteTod(todId: number): Promise<void> {
    this.busyTodId.set(todId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/tods/${todId}/delete`);
      await this.refreshOverview();
      this.actionMessage.set('ToD entry deleted.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Deleting the ToD entry failed.'));
      throw error;
    } finally {
      this.busyTodId.set(null);
    }
  }

  async endEvent(eventId: number): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/end`);
      await this.refreshOverview();
      this.actionMessage.set('Event ended and moved to history.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Ending the event failed.'));
    } finally {
      this.busyEventId.set(null);
    }
  }

  async quickJoinLiveEvent(eventId: number, input: ActivityQuickJoinInput): Promise<void> {
    this.busyEventId.set(eventId);
    this.actionError.set(null);
    this.actionMessage.set(null);

    try {
      await this.postActivityAction(`/api/activity/events/${eventId}/quick-join`, {
        jobName: input.jobName,
        subJobName: input.subJobName,
        jobType: input.jobType
      });
      await this.refreshOverview();
      this.actionMessage.set('Live event join added.');
    } catch (error) {
      this.actionError.set(this.formatActionError(error, 'Joining the live event failed.'));
      throw error;
    } finally {
      this.busyEventId.set(null);
    }
  }

  private async tryLoadStandaloneOverview(): Promise<void> {
    try {
      this.overview.set(await this.fetchOverview());
      this.phase.set('Loaded outside Discord using the current website session.');
    } catch {
      this.overview.set(null);
    }
  }

  clearActionState(): void {
    this.actionError.set(null);
    this.actionMessage.set(null);
  }

  viewerTimeZone(): string {
    return this.overview()?.appUser?.timeZone || this.localUser()?.appUser?.timeZone || this.browserTimeZone || 'UTC';
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

  private async postActivityAction(path: string, body?: unknown): Promise<void> {
    await this.postActivityJson(path, body);
  }

  private async postActivityJson<T = void>(path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = {
      'Content-Type': 'application/json'
    };

    const accessToken = this.session()?.access_token;
    if (accessToken) {
      headers['Authorization'] = `Bearer ${accessToken}`;
    }

    const response = await fetch(path, {
      method: 'POST',
      headers,
      cache: 'no-store',
      credentials: 'include',
      body: body ? JSON.stringify(body) : undefined
    });

    if (response.ok) {
      if (response.status === 204) {
        return undefined as T;
      }

      const responseText = await response.text();
      if (!responseText) {
        return undefined as T;
      }

      return JSON.parse(responseText) as T;
    }

    const responseText = await response.text();
    if (!responseText) {
      throw new Error(`Activity request failed with status ${response.status}.`);
    }

    try {
      const payload = JSON.parse(responseText) as { error?: string };
      throw new Error(payload.error || `Activity request failed with status ${response.status}.`);
    } catch {
      throw new Error(responseText);
    }
  }

  private async withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string): Promise<T> {
    let timer: ReturnType<typeof setTimeout> | null = null;

    try {
      return await Promise.race([
        promise,
        new Promise<T>((_, reject) => {
          timer = setTimeout(() => reject(new Error(message)), timeoutMs);
        })
      ]);
    } finally {
      if (timer) {
        clearTimeout(timer);
      }
    }
  }

  private setError(message: string): void {
    this.status.set('error');
    this.phase.set('Discord Activity initialization failed');
    this.error.set(message);
  }

  private formatError(error: unknown): string {
    if (error instanceof Error && error.message) {
      return this.withDiscordHint(error.message);
    }

    if (this.isDiscordRpcError(error)) {
      const rpcMessage = error.data?.message ?? error.message ?? 'Discord RPC call failed.';
      const rpcCode = error.data?.code ?? error.code;
      const cmd = error.cmd ?? 'unknown';
      const details =
        rpcCode !== undefined
          ? `Discord ${cmd.toLowerCase()} failed (${rpcCode}): ${rpcMessage}`
          : `Discord ${cmd.toLowerCase()} failed: ${rpcMessage}`;

      return this.withDiscordHint(details, cmd);
    }

    return 'An unknown error occurred while initializing the Discord Activity.';
  }

  private isDiscordRpcError(error: unknown): error is DiscordRpcErrorLike {
    if (!error || typeof error !== 'object') {
      return false;
    }

    const candidate = error as DiscordRpcErrorLike;
    return Boolean(candidate.cmd || candidate.message || candidate.data?.message);
  }

  private withDiscordHint(message: string, command?: string): string {
    const normalized = message.toLowerCase();
    const isAuthorizeFailure =
      command?.toLowerCase() === 'authorize' ||
      normalized.includes('authorize') ||
      normalized.includes('authorization');

    if (!isAuthorizeFailure) {
      return message;
    }

    return `${message} Check Discord Developer Portal OAuth2 redirects for https://127.0.0.1 and confirm Activities URL Mapping points '/' at the current public host.`;
  }

  private formatActionError(error: unknown, fallback: string): string {
    if (error instanceof Error && error.message) {
      return error.message;
    }

    return fallback;
  }

  private resolveBrowserTimeZone(): string {
    try {
      return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    } catch {
      return 'UTC';
    }
  }
}
