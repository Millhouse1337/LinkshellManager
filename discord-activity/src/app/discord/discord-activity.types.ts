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
  // Names of monsters the linkshell admin has elected to hide from the
  // ToD Tracker (Dashboard + ToDs tab). Empty when none are hidden.
  hiddenTodMonsters: string[];
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
  canCustomizeLinkshell: boolean;
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
  canCustomizeLinkshell: boolean;
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
  canCustomizeLinkshell: boolean;
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
}

export interface ActivityEventJob {
  id: number;
  jobName?: string | null;
  subJobName?: string | null;
  jobType?: string | null;
  quantity?: number | null;
  signedUp?: number | null;
  enlisted: string[];
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
  participantCount: number;
  requestedSlots: number;
  currentParticipation?: ActivityParticipation | null;
  participants: ActivityEventParticipant[];
  loot: ActivityLootEntry[];
  jobs: ActivityEventJob[];
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
