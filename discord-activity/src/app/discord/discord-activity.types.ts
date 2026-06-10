import { DiscordSDK } from '@discord/embedded-app-sdk';

export type ActivityStatus = 'idle' | 'initializing' | 'standalone' | 'ready' | 'error';

export type DiscordSdkContextSource = DiscordSDK & {
  channelId?: string;
  guildId?: string;
  instanceId?: string;
  platform?: string;
};

export interface DiscordTokenExchangeResponse {
  accessToken: string;
  expiresIn: number;
  localUser?: LocalActivityUser;
  scope: string;
  tokenType: string;
}

export interface DiscordUser {
  id: string;
  username: string;
  discriminator: string;
  global_name?: string | null;
  avatar?: string | null;
}

export interface DiscordApplication {
  id: string;
  name: string;
  description?: string;
}

export interface DiscordSession {
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

export interface DiscordContext {
  channelId: string | null;
  guildId: string | null;
  instanceId: string | null;
  platform: string | null;
}

export interface LocalActivityUser {
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

export interface ActivityAppUser {
  id: string;
  userName: string;
  characterName?: string | null;
  altCharacterName1?: string | null;
  altCharacterName2?: string | null;
  timeZone?: string | null;
  primaryLinkshellId?: number | null;
  primaryLinkshellName?: string | null;
  // Per-job levels for the 15 classic jobs in PROFILE_JOB_OPTIONS order
  // (index 0 = WAR ... 14 = SMN). Pre-fills the profile "My Jobs" editor.
  jobLevels?: number[] | null;
  // Same catalog-aligned levels for the two alt characters; pre-fill the alt tabs.
  alt1JobLevels?: number[] | null;
  alt2JobLevels?: number[] | null;
  // Catalog-aligned "strong" flags parallel to the level arrays above (true = the
  // member marked that job well-geared/merited). Pre-fill the per-job Strong toggles.
  strongJobs?: boolean[] | null;
  alt1StrongJobs?: boolean[] | null;
  alt2StrongJobs?: boolean[] | null;
}

export interface ActivityLinkshell {
  id: number;
  name: string;
  rank?: string | null;
  status?: string | null;
  linkshellDkp?: number | null;
  memberCount: number;
  itemCount: number;
  revenue: number;
  details?: string | null;
  permissions?: ActivityLinkshellPermissions | null;
  settings?: ActivityLinkshellSettings | null;
}

export type ActivityLootStructure = 'Dkp' | 'LootCouncil' | 'Hybrid';

export type ActivityDkpRoundingIncrement = 'Quarter' | 'Half';

export interface ActivityLinkshellSettings {
  lootStructure: ActivityLootStructure;
  enableHnmSection: boolean;
  enableMissions: boolean;
  enableAuctions: boolean;
  enableToDs: boolean;
  enableEndgame: boolean;
  enableEvents: boolean;
  enableDkp: boolean;
  enableItems: boolean;
  enableRevenue: boolean;
  dkpRoundingIncrement: ActivityDkpRoundingIncrement;
  // Member activity tracking: opt-in Active/Inactive badge from event attendance.
  // Inactive after N consecutive uncredited counting events, back to Active after M.
  enableActivityTracking: boolean;
  inactiveAfterAbsences: number;
  activeAfterAttendances: number;
  // Names of monsters the linkshell admin has elected to hide from the
  // ToD Tracker (Dashboard + ToDs tab). Empty when none are hidden.
  hiddenTodMonsters: string[];
  // SkySeaDynamis | HnmOnly | Both — which content this linkshell runs.
  linkshellType: string;
  // The single Discord server (guild) this linkshell is associated with, or null
  // when not tied to any server. Setting it scopes member search / roster to that
  // server and powers channel posting; it does NOT by itself restrict viewing —
  // that's lockToDiscordGuild. discordGuildName is a display cache for the UI.
  discordGuildId: string | null;
  discordGuildName?: string | null;
  // Optional, separate access lock. When true, the Activity can only open this
  // linkshell from discordGuildId. False by default (associated but not locked).
  lockToDiscordGuild: boolean;
  // Palette key for this linkshell's rendered event-board image. One of the
  // EVENT_BOARD_THEMES keys (Crystal, Abyss, Ember, Verdant, Royal, Tome).
  eventBoardTheme: string;
}

// One Discord server the caller can lock a linkshell to (the bot is in it and so
// is the caller). Populates the Configurations "Discord server lock" dropdown.
export interface ActivityGuildOption {
  id: string;
  name: string;
}

export interface ActivityLinkshellPermissions {
  canManageRoles: boolean;
  canManageMembers: boolean;
  canManageEvents: boolean;
  canModerateLiveEvent: boolean;
  canAddLoot: boolean;
  canManageInventory: boolean;
  canManageTreasury: boolean;
  canManageRules: boolean;
  canManageAnnouncements: boolean;
  canManageTods: boolean;
  canAuditDkp: boolean;
  canManageAuctions: boolean;
  canLockAuctions: boolean;
  canCustomizeLinkshell: boolean;
  canManageParties: boolean;
  canManageInvites: boolean;
  canBid: boolean;
}

export interface ActivityLinkshellRole {
  id: number;
  name: string;
  isSystem: boolean;
  sortOrder: number;
  canManageRoles: boolean;
  canManageMembers: boolean;
  canManageEvents: boolean;
  canModerateLiveEvent: boolean;
  canAddLoot: boolean;
  canManageInventory: boolean;
  canManageTreasury: boolean;
  canManageRules: boolean;
  canManageAnnouncements: boolean;
  canManageTods: boolean;
  canAuditDkp: boolean;
  canManageAuctions: boolean;
  canLockAuctions: boolean;
  canCustomizeLinkshell: boolean;
  canManageParties: boolean;
  canManageInvites: boolean;
  canBid: boolean;
}

export interface ActivityLinkshellRolesResponse {
  linkshellId: number;
  roles: ActivityLinkshellRole[];
}

export interface ActivityLinkshellRolePermissionsInput {
  name?: string | null;
  canManageRoles: boolean;
  canManageMembers: boolean;
  canManageEvents: boolean;
  canModerateLiveEvent: boolean;
  canAddLoot: boolean;
  canManageInventory: boolean;
  canManageTreasury: boolean;
  canManageRules: boolean;
  canManageAnnouncements: boolean;
  canManageTods: boolean;
  canAuditDkp: boolean;
  canManageAuctions: boolean;
  canLockAuctions: boolean;
  canCustomizeLinkshell: boolean;
  canManageParties: boolean;
  canManageInvites: boolean;
  canBid: boolean;
}

export interface ActivityPrimaryLinkshell {
  id: number;
  name: string;
  memberCount: number;
  details?: string | null;
  members: ActivityMember[];
  rules: ActivityRule[];
  announcements: ActivityAnnouncement[];
  items: ActivityItem[];
  revenueEntries: ActivityRevenueEntry[];
  // News & Updates feed sources.
  recentAuctions: ActivityNewsAuction[];
  recentDkpAudits: ActivityNewsDkp[];
}

export interface ActivityNewsAuction {
  id: number;
  title: string;
  when: string;
  closed: boolean;
}

export interface ActivityNewsDkp {
  characterName: string;
  amount: number;
  isCorrection: boolean;
  occurredAt: string;
}

export interface ActivityRule {
  id: number;
  linkshellId: number;
  title: string;
  details: string;
  createdByAppUserId?: string | null;
  createdByCharacterName?: string | null;
  createdAt: string;
}

export interface ActivityAnnouncement {
  id: number;
  linkshellId: number;
  title: string;
  details: string;
  createdByAppUserId?: string | null;
  createdByCharacterName?: string | null;
  createdAt: string;
}

export interface ActivityItem {
  id: number;
  linkshellId: number;
  itemName: string;
  itemType?: string | null;
  quantity: number;
  notes?: string | null;
  createdByAppUserId?: string | null;
  createdByCharacterName?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ActivityRevenueEntry {
  id: number;
  linkshellId: number;
  entryType: string;
  category?: string | null;
  value: number;
  details?: string | null;
  occurredAt: string;
  createdByAppUserId?: string | null;
  createdByCharacterName?: string | null;
  createdAt: string;
}

export interface ActivityItemInput {
  itemName: string;
  itemType?: string | null;
  quantity: number;
  notes?: string | null;
}

export interface ActivityRevenueInput {
  entryType: 'Income' | 'Expense';
  category?: string | null;
  value: number;
  details?: string | null;
  occurredAt?: string | null;
}

// Discord channel-routes config (mirrors the web Customize card). Officers add
// named routes, pick a channel, and tick which post types the bot posts there.
export interface ActivityDiscordChannelOption {
  id: string;
  name: string;
}

export interface ActivityChannelPostType {
  key: string;
  label: string;
}

export interface ActivityChannelRoute {
  id: number;
  name?: string | null;
  channelId: string;
  channelName?: string | null;
  postEvents: boolean;
  postLoot: boolean;
  postAuctions: boolean;
  postAttendance: boolean;
  postTodBoard: boolean;
  eventTypeFilter: string[];
}

export interface ActivityChannelRoutesResponse {
  guildConfigured: boolean;
  availableChannels: ActivityDiscordChannelOption[];
  postTypes: ActivityChannelPostType[];
  eventTypes: string[];
  routes: ActivityChannelRoute[];
}

export interface ActivityChannelRouteInput {
  id: number | null;
  name: string | null;
  channelId: string | null;
  postEvents: boolean;
  postLoot: boolean;
  postAuctions: boolean;
  postAttendance: boolean;
  postTodBoard: boolean;
  eventTypeFilter: string[];
}

export interface ActivityLinkshellDetail {
  id: number;
  name: string;
  memberCount: number;
  details?: string | null;
  status?: string | null;
  members: ActivityMember[];
}

export interface ActivityMember {
  id: number;
  appUserId?: string | null;
  characterName: string;
  altCharacterName1?: string | null;
  altCharacterName2?: string | null;
  rank?: string | null;
  status?: string | null;
  linkshellDkp?: number | null;
  dateJoined?: string | null;
  // Computed Active/Inactive from event attendance. Null when the linkshell has
  // not enabled activity tracking — the badge is hidden in that case.
  active?: boolean | null;
}

// "Jobs Roster" — every member's leveled jobs. jobCatalog is the job-name order
// (WAR..SMN); each member's level arrays are aligned to it (0 = not leveled).
export interface ActivityJobsRoster {
  jobCatalog: string[];
  members: ActivityJobsRosterMember[];
}

export interface ActivityJobsRosterMember {
  id: number;
  characterName: string;
  rank?: string | null;
  jobLevels: number[];
  alt1Name?: string | null;
  alt1JobLevels: number[];
  alt2Name?: string | null;
  alt2JobLevels: number[];
  // Catalog-aligned "strong" flags parallel to each character's level array
  // (true = well-geared/merited). Rendered as a marker on the job pills.
  strongJobs: boolean[];
  alt1StrongJobs: boolean[];
  alt2StrongJobs: boolean[];
}

export interface ActivityEventParticipant {
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

export interface ActivityEventAddMemberCandidate {
  appUserId: string;
  characterName: string;
  rank?: string | null;
}

export interface ActivityLootEntry {
  id: number;
  itemName?: string | null;
  itemWinner?: string | null;
  winningDkpSpent?: number | null;
}

export interface ActivityTodLootEntry {
  id: number;
  itemName?: string | null;
  itemWinner?: string | null;
  winningDkpSpent?: number | null;
}

export interface ActivityTodEntry {
  id: number;
  linkshellId: number;
  monsterName: string;
  dayNumber?: number | null;
  time?: string | null;
  // Tri-state: true = Claimed, false = Unclaimed, null = Not Specified.
  // Null is the auto-posted state from the addon's loot-pool flow.
  claim: boolean | null;
  cooldown?: string | null;
  repopTime?: string | null;
  interval?: string | null;
  lootCount: number;
  lootDetails: ActivityTodLootEntry[];
  imagePath?: string | null;
}

export interface ActivityEvent {
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
  // True when the event is flagged to auto-start at its start time.
  autoStart?: boolean;
  // True when attendees earn active-member credit (reconciled at close).
  countsTowardActive?: boolean;
  participantCount: number;
  currentParticipation?: ActivityParticipation | null;
  participants: ActivityEventParticipant[];
  loot: ActivityLootEntry[];
  // Optional FK to a PartySetup. When set, the expanded event card fetches
  // the full setup tree (alliances → parties → slots) on demand from the
  // PartySetupService.
  partySetupId?: number | null;
  partySetupName?: string | null;
  partySetupAssignedMonsterName?: string | null;
  windowCount: number;
  attendanceWindows: ActivityAttendanceWindow[];
  creatorCharacterName?: string | null;
  starterCharacterName?: string | null;
}

export interface ActivityAttendanceWindow {
  id: number;
  sequenceNumber: number;
  label?: string | null;
  postedAt: string;
  attendees: ActivityAttendanceWindowAttendee[];
}

export interface ActivityAttendanceWindowAttendee {
  // AppUserEventWindow.Id — used as the path segment for the per-row remove call.
  id: number;
  characterName?: string | null;
  jobName?: string | null;
  subJobName?: string | null;
  zone?: string | null;
  verifiedAt: string;
  verifiedBy?: string | null;
}

export interface ActivityWindowEventsResponse {
  openEvents: ActivityWindowEvent[];
  closedEvents: ActivityWindowEvent[];
  unlinkedSnapshots: ActivityWindowSnapshot[];
  canManage: boolean;
  entryTypeOptions: string[];
  rosterCharacterNames: string[];
}

export interface ActivityWindowEventMemberDkpInput {
  characterName: string;
  dkpAmount: number | null;
}

export interface ActivityWindowEventDkpPayload {
  dkpAmount: number;
  entryType: string;
  memberDkp?: ActivityWindowEventMemberDkpInput[];
}

export interface ActivityAddSnapshotEntryInput {
  characterName: string;
  mainJob?: string | null;
  mainJobLevel?: number | null;
  subJob?: string | null;
  subJobLevel?: number | null;
  zone?: string | null;
}

export interface ActivityWindowEvent {
  id: number;
  linkshellId: number;
  name?: string | null;
  status: string;
  firstCapturedAtUtc: string;
  lastCapturedAtUtc: string;
  createdByCharacterName?: string | null;
  snapshotCount: number;
  activeSnapshotCount: number;
  duplicateSnapshotCount: number;
  ignoredSnapshotCount: number;
  combinedMemberCount: number;
  snapshots: ActivityWindowSnapshot[];
  combinedMembers: ActivityWindowCombinedMember[];
  dkpAmount?: number | null;
  entryType?: string | null;
  postedToSheetUtc?: string | null;
}

export interface ActivityWindowSnapshot {
  id: number;
  windowEventId?: number | null;
  name?: string | null;
  snapshotStatus: string;
  duplicateOfSnapshotId?: number | null;
  capturedAtUtc: string;
  capturedByCharacterName?: string | null;
  primaryZone?: string | null;
  entryCount: number;
  entries: ActivityWindowSnapshotEntry[];
}

export interface ActivityWindowSnapshotEntry {
  id: number;
  characterName: string;
  mainJob?: string | null;
  mainJobLevel?: number | null;
  subJob?: string | null;
  subJobLevel?: number | null;
  zone?: string | null;
}

export interface ActivityWindowCombinedMember {
  characterName: string;
  mainJob?: string | null;
  mainJobLevel?: number | null;
  subJob?: string | null;
  subJobLevel?: number | null;
  zone?: string | null;
  snapshotCount: number;
  // Per-character override if one is set on this Window Event; null means
  // the event default applies.
  dkpAmountOverride?: number | null;
  // Override (when set) else the event default — used to seed the per-row
  // DKP input on the combined roster table.
  effectiveDkpAmount?: number | null;
}

export interface ActivityParticipation {
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

export interface ActivityStatusLedgerEntry {
  id: number;
  actionType: string;
  occurredAt: string;
  requiresVerification: boolean;
  verifiedAt?: string | null;
  verifiedBy?: string | null;
  deniedAt?: string | null;
  deniedBy?: string | null;
}

export interface ActivityHistory {
  id: number;
  linkshellId: number;
  name?: string | null;
  type?: string | null;
  location?: string | null;
  endTime?: string | null;
  duration?: number | null;
  participantCount: number;
}

export interface ActivityHistoryParticipant {
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

export interface ActivityHistoryDetail {
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

// Game Addon (att) pairing — mirrors the web app's /Linkshell/Customize card.
export interface ActivityAddonToken {
  id: number;
  prefix: string;
  label?: string | null;
  createdAt: string;
  lastUsedAt?: string | null;
  issuedToAppUserId?: string | null;
}

export interface ActivityAddonTokenList {
  tokens: ActivityAddonToken[];
}

export interface ActivityAddonPairingCodeResponse {
  code: string;
  expiresInMinutes: number;
}

export interface ActivityDkpHistoryMember {
  appUserId: string;
  characterName: string;
  currentBalance: number;
}

export interface ActivityDkpLedgerEntry {
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
  // Populated on "LootEditRefund" / "LootEditSpent" entries — the officer's
  // reason for the loot correction. Rendered under the Details cell as an
  // italic "Reason: ..." line so the audit trail is visible inline.
  editReason?: string | null;
}

// One row in the unified Loot History view. The `source` discriminator
// tells the client whether to call the /tod/{id}/edit or /event/{id}/edit
// endpoint when the officer hits Save. `canEdit` is computed server-side
// from the caller's CanAddLoot role flag so the UI just toggles the button.
export interface ActivityLootHistoryItem {
  lootDetailId: number;
  source: 'Tod' | 'Event';
  parentId: number;
  context?: string | null;
  occurredAt?: string | null;
  itemName?: string | null;
  itemWinner?: string | null;
  winningDkpSpent?: number | null;
  actualDeductedDkp?: number | null;
  isEdited: boolean;
  lastEditReason?: string | null;
  editedAt?: string | null;
  editedByCharacterName?: string | null;
  canEdit: boolean;
}

export interface ActivityLootHistoryList {
  page: number;
  pageSize: number;
  totalCount: number;
  items: ActivityLootHistoryItem[];
}

export interface ActivityLootEditInput {
  itemName: string;
  itemWinner: string;
  winningDkpSpent: number;
  reason: string;
}

export interface ActivityDkpHistory {
  linkshellId?: number | null;
  linkshellName?: string | null;
  selectedAppUserId?: string | null;
  selectedMemberName?: string | null;
  currentBalance: number;
  members: ActivityDkpHistoryMember[];
  entries: ActivityDkpLedgerEntry[];
}

export interface ActivityAuctionItem {
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
  sourceItemId?: number | null;
  // Set when this item is a gil sale (treasury gil sold for DKP).
  gilAmount?: number | null;
}

export interface ActivityAuction {
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
  canEnd: boolean;
  canClose: boolean;
  items: ActivityAuctionItem[];
  // Viewer's available DKP in this auction's linkshell (total minus DKP
  // locked by bids they're currently winning). Set by the list endpoint.
  availableDkp?: number | null;
  // True when leadership has frozen bidding for the linkshell.
  auctionsLocked?: boolean;
}

export interface ActivityAuctionBid {
  id: number;
  characterName: string;
  bidAmount: number;
  createdAt: string;
}

export interface ActivityAuctionHistory {
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

export interface ActivityInvite {
  id: number;
  // Null for a Discord-roster invite whose target hasn't signed into LSM yet.
  appUserId: string | null;
  linkshellId: number;
  appUserDisplayName: string;
  linkshellName: string;
  status: string;
}

// A member of a locked linkshell's Discord server, offered in the
// "From your Discord server" invite roster.
export interface ActivityDiscordRosterCandidate {
  discordUserId: string;
  displayName: string;
  avatarUrl: string;
  hasLsmAccount: boolean;
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

export interface ActivityOverviewStats {
  linkshellCount: number;
  activeEventCount: number;
  completedEventCount: number;
  liveEventCount: number;
}

export interface ActivityOverview {
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
  addonConfigured: boolean;
  addonGloballyDisabled: boolean;
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
  // Optional FK to a PartySetup in the same linkshell. Replaces the old
  // inline jobs/slots editor.
  partySetupId?: number | null;
  // When true, the event auto-starts at its start time (no manual Start).
  autoStart?: boolean;
  // When true, attendees earn active-member credit (reconciled at close). Default true.
  countsTowardActive?: boolean;
}

export interface ActivityAddEventMemberInput {
  appUserId: string;
  jobName: string;
  subJobName: string;
  jobType: string;
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
  imagePath?: string | null;
}

export interface ActivityUpdateTodInput {
  todId: number;
  monsterName: string;
  dayNumber?: number | null;
  claim: boolean;
  timeLocal: string;
  cooldown?: string | null;
  interval?: string | null;
  noLoot: boolean;
  lootDetails: ActivityTodLootInput[];
  imagePath?: string | null;
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
  sourceItemId?: number | null;
  // When > 0 this item is a gil sale: gil sold for DKP, paid from treasury.
  gilAmount?: number | null;
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
  altCharacterName1?: string | null;
  altCharacterName2?: string | null;
  // Catalog-aligned per-job levels (index 0 = WAR ... 14 = SMN), or null to
  // leave job levels unchanged.
  jobLevels?: number[] | null;
  // Catalog-aligned job levels for the two alt characters.
  alt1JobLevels?: number[] | null;
  alt2JobLevels?: number[] | null;
  // Catalog-aligned "strong" flags parallel to the level arrays above; null
  // leaves the existing flags unchanged.
  strongJobs?: boolean[] | null;
  alt1StrongJobs?: boolean[] | null;
  alt2StrongJobs?: boolean[] | null;
}

export interface DiscordRpcErrorLike {
  code?: number;
  cmd?: string;
  data?: {
    code?: number;
    message?: string;
  };
  evt?: string | null;
  message?: string;
}

// --- Party Setup (raid-composition planner) ---

export interface ActivityPartySetupListRow {
  id: number;
  name: string;
  eventType?: string | null;
  assignedMonsterName?: string | null;
  allianceCount: number;
  partyCount: number;
  slotCount: number;
  updatedAt: string;
}

export interface ActivityPartySetupListResponse {
  linkshellId: number;
  linkshellName?: string | null;
  canManage: boolean;
  items: ActivityPartySetupListRow[];
  // Option lists bundled with the list so the editor + sign-up dropdowns need
  // no second call.
  monsterOptions: string[];
  roleOptions: string[];
  mainJobOptions: string[];
  subJobOptions: string[];
}

export interface ActivityPartySetupSlot {
  slotId: number;
  position: number;
  requirementType: string;
  role?: string | null;
  mainJob?: string | null;
  subJob?: string | null;
  label?: string | null;
  isPartyLeader: boolean;
  // Member sign-up snapshot (all null when the slot is open).
  signedUpAppUserId?: string | null;
  signedUpCharacterName?: string | null;
  signedUpRole?: string | null;
  signedUpMainJob?: string | null;
  signedUpSubJob?: string | null;
  // Per-event: whether the member in this slot is the party's leader (👑).
  // Distinct from isPartyLeader above (the template's designated-leader slot).
  signedUpIsPartyLeader?: boolean;
}

export interface ActivityPartySetupParty {
  name: string;
  slots: ActivityPartySetupSlot[];
}

export interface ActivityPartySetupAlliance {
  name: string;
  parties: ActivityPartySetupParty[];
}

export interface ActivityPartySetupDetail {
  id: number;
  linkshellId: number;
  name: string;
  eventType?: string | null;
  assignedMonsterName?: string | null;
  notes?: string | null;
  canManage: boolean;
  alliances: ActivityPartySetupAlliance[];
}

export interface ActivityPartySetupSignUpInput {
  role?: string | null;
  mainJob?: string | null;
  subJob?: string | null;
  // Event boards only: also claim this party's leader spot (first-claim-wins).
  asLeader?: boolean;
}

// Officer editor: a flat slot list (each row carries its alliance/party/slot
// index) the server rebuilds into the tree. RequirementType is derived from the
// picks client-side (Job > Role > Any) so it matches the server's MapSlot.
export interface ActivityPartySetupSlotInput {
  allianceIndex: number;
  partyIndex: number;
  slotIndex: number;
  allianceName?: string | null;
  partyName?: string | null;
  requirementType: string;
  role?: string | null;
  mainJob?: string | null;
  subJob?: string | null;
  isPartyLeader: boolean;
}

export interface ActivityPartySetupEditorInput {
  linkshellId: number;
  name: string;
  eventType?: string | null;
  assignedMonsterName?: string | null;
  notes?: string | null;
  slots: ActivityPartySetupSlotInput[];
}

// --- App DKP Sheet (read-only Google Sheet viewer) ---

export interface ActivityDkpSheetRow {
  rowNumber: number;
  cells: string[];
  // Lowercased join of cells (Tally rows also carry the member name) for the
  // client-side filter box.
  searchText: string;
}

export interface ActivityDkpSheetTab {
  name: string;
  isMainTab: boolean;
  headers: string[];
  rows: ActivityDkpSheetRow[];
}

export interface ActivityDkpSheetResponse {
  connected: boolean;
  linkshellId: number;
  linkshellName?: string | null;
  openInSheetsUrl?: string | null;
  error?: string | null;
  tabs: ActivityDkpSheetTab[];
}

// DKP audit "Add to a previous entry" mode: a posted attendance/window event the
// target member was missed by, eligible to be credited (amount is the event's
// DKP, derived server-side on submit).
export interface ActivityDkpAddCandidate {
  windowEventId: number;
  label: string;
  occurredAt: string;
  amount: number;
  eventName?: string | null;
  entryType?: string | null;
  primaryZone?: string | null;
  memberCount: number;
}
