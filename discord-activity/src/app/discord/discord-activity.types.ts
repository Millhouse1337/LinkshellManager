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
  // Per-craft levels (main + alts) in PROFILE_CRAFT_OPTIONS order
  // (index 0 = Alchemy ... 8 = Fishing). Pre-fill the profile "Crafts" editor.
  craftLevels?: number[] | null;
  alt1CraftLevels?: number[] | null;
  alt2CraftLevels?: number[] | null;
  // Per-job free-text merit notes (main + alts), catalog-aligned (WAR … SMN).
  // Pre-fill the "Merited" modal for each job.
  meritJobs?: string[] | null;
  alt1MeritJobs?: string[] | null;
  alt2MeritJobs?: string[] | null;
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
  auctionsLocked?: boolean;
  // Dashboard banner image URL (already cache-busted), or null when none is set.
  bannerUrl?: string | null;
}

export type ActivityLootStructure = 'Dkp' | 'LootCouncil' | 'Hybrid';

export type ActivityDkpRoundingIncrement = 'Quarter' | 'Half';

export interface ActivityTodMonsterTiming {
  monsterName: string;
  cooldownHours: number;
  intervalHours: number;
  intervalMinutes: number;
  category?: string | null;
}

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
  // Per-monster cooldown and interval overrides. Empty uses built-in defaults.
  todMonsterTimings: ActivityTodMonsterTiming[];
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
  // Allow Discord members with no LSM account to sign up for NON-HNM events from the
  // party board. Backed by a placeholder member, so they DO earn DKP + are tracked.
  outsidePartySignupEnabled?: boolean;
  // "Fill earlier alliances first" signup nudge (default on; no-op on single-alliance boards).
  fillAlliancesInOrder?: boolean;
  // HNM Outside Sign Up: gates the HNM event type in the create dropdown and account-less
  // Discord signups onto HNM boards. Independent of outsidePartySignupEnabled.
  // Roster memory only — HNM signups earn no DKP and no active/absent credit.
  hnmOutsideSignupEnabled?: boolean;
  // Experimental: post event boards as Components V2 (wide media-gallery card) instead of
  // the classic image-in-embed. Only affects boards posted after it's turned on.
  useComponentsV2Boards?: boolean;
  // Discord channel id new post-event discussion comments mirror to, or null to
  // keep discussion in-app only.
  discussionChannelId?: string | null;
  // Manual Check In HNM attendance: mode (Standard | Wd) + scoring. Only used when
  // hnmAttendanceMode === 'Wd'. dkpPerWindow is a fraction (0.25 default) — the
  // per-window rate; bonuses are added once per attendee at finalize.
  hnmAttendanceMode?: string;
  wdDkpPerWindow?: number;
  wdClaimBonus?: number;
  wdKillBonus?: number;
  // Manual Check In open / close bonuses, paid once on top of the per-window rate. Gated on the
  // member's own check-in range: open = checked in from window 1, close = still checked in at the
  // camp's last credited window. See WdCampFinalizer.ComputeDkp.
  wdOpenBonus?: number;
  wdCloseBonus?: number;
  // Standard-mode HNM bonuses. Only used when hnmAttendanceMode === 'Standard':
  // extra DKP for being on the roster at the camp's open / close, plus claim / kill
  // outcome bonuses.
  hnmStandardOpenBonus?: number;
  hnmStandardCloseBonus?: number;
  hnmStandardClaimBonus?: number;
  hnmStandardKillBonus?: number;
  // What a REGULAR (in-between) window pays each attendee scanned in it on a Standard camp — the
  // base rate the open / close bonuses ride on top of. 0 = the old open/close-only payout.
  hnmStandardWindowBonus?: number;
  // Automatic per-window attendance snapshots (both modes). When on, an officer running the LSM
  // addon can ARM a live camp and the addon posts THEIR ALLIANCE as that window's snapshot
  // ~hnmAutoSnapshotDelaySeconds after each window opens. Arming stays an explicit per-officer,
  // per-camp action in the addon — this only says officers may arm. Delay is clamped [5, 300].
  hnmAutoSnapshotEnabled?: boolean;
  hnmAutoSnapshotDelaySeconds?: number;
}
// is the caller). Populates the Configurations "Discord server lock" dropdown.
export interface ActivityGuildOption {
  id: string;
  name: string;
}

// LOCKSTEP: a permission added here must ALSO be added to ActivityLinkshellRole and
// ActivityLinkshellRolePermissionsInput below, to the three matching C# records in
// Models/Activity/ActivityDtos.cs, to BOTH permissionKeys and saveRoleDraft in
// configurations-tab.component.ts, and to the plain-JS `permissions` array in
// Views/Linkshell/Permissions.cshtml. Only the last one fails silently.
export interface ActivityLinkshellPermissions {
  canManageRoles: boolean;
  canManageMembers: boolean;
  canManageEvents: boolean;
  canModerateLiveEvent: boolean;
  canAddLoot: boolean;
  canManageInventory: boolean;
  canManageCharts: boolean;
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
  canManageCharts: boolean;
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
  canManageCharts: boolean;
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
  category?: string | null;
  createdByAppUserId?: string | null;
  createdByCharacterName?: string | null;
  createdAt: string;
}

export interface ActivityAnnouncement {
  id: number;
  linkshellId: number;
  title: string;
  details: string;
  category?: string | null;
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
  isSold?: boolean;
  soldPrice?: number | null;
  soldByCharacterName?: string | null;
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

/**
 * Treasury: what the linkshell has, what moved, and what can happen to it.
 *
 * Every user-visible string comes from the server (TreasuryLabels / TreasuryTransactionKinds), so the
 * website and Discord cannot end up calling the same thing two different names — which is exactly what
 * happened before, when one column was "Source" on the web and "Type" here.
 */
export interface ActivityTreasuryLine {
  /**
   * Unique within the entry, unlike accountNumber: a split payout puts one line per member on the
   * same category. Track the list on this — duplicate @for keys throw.
   */
  lineNumber: number;
  accountNumber: number;
  accountName: string;
  classLabel: string;
  presentedAmount: number;
  counterpartyCharacterName?: string | null;
}

/** One member's share of a split. membershipId is null once they have left the linkshell. */
export interface ActivityTreasuryRecipient {
  membershipId?: number | null;
  appUserId?: string | null;
  characterName: string;
  share: number;
}

/** Someone who can be given a share. Only sent to officers who can record. */
export interface ActivityTreasuryMember {
  membershipId: number;
  appUserId?: string | null;
  characterName: string;
  rank?: string | null;
}

export interface ActivityTreasuryEntry {
  id: number;
  linkshellId: number;
  entryNumber: string;
  status: string;
  statusLabel: string;
  kind: string;
  source: string;
  transactionKind?: string | null;
  /** The plain-English sentence the officer picked, e.g. "Sold an item". */
  whatHappened: string;
  amount: number;
  /** Signed: negative means gil left. Zero when the entry only records something owed. */
  cashDelta: number;
  transactionDate: string;
  memo?: string | null;
  counterpartyCharacterName?: string | null;
  enteredByCharacterName?: string | null;
  recordedAt?: string | null;
  reversesEntryId?: number | null;
  reversesEntryNumber?: string | null;
  isReversed: boolean;
  correctionReason?: string | null;
  /** Everyone who got a share. Empty for an ordinary entry, one name for a single-member one. */
  recipients: ActivityTreasuryRecipient[];
  lines: ActivityTreasuryLine[];
}

export interface ActivityTreasuryCategory {
  id: number;
  accountNumber: number;
  name: string;
  description?: string | null;
  classLabel: string;
  isCash: boolean;
  isPostable: boolean;
  isActive: boolean;
  sortOrder: number;
}

/** One option in the "What happened?" picker. */
export interface ActivityTreasuryKind {
  key: string;
  label: string;
  help: string;
  group: string;
  showsMember: boolean;
  /** Shares one amount between several members instead of naming one. */
  isSplittable: boolean;
  /** Picking a member fills in what they are still owed, rather than asking for a number. */
  settlesMemberDebt: boolean;
  /** "{0}" is the formatted amount. */
  previewTemplate: string;
}

/** One member the linkshell still owes. These always add up to `weOwe`. */
export interface ActivityTreasuryMemberObligation {
  characterName: string;
  amount: number;
}

export interface ActivityTreasurySnapshot {
  cashOnHand: number;
  owedToUs: number;
  weOwe: number;
  moneyIn: number;
  moneyOut: number;
  netChange: number;
  netWorth: number;
  startingBalance: number;
  balances: boolean;
  uncategorizedCount: number;
  lockedThroughUtc?: string | null;
  basisNote: string;
  /** Who `weOwe` is owed to, largest first. Projected from the same lines, so it always adds up. */
  owedToMembers: ActivityTreasuryMemberObligation[];
}

export interface ActivityTreasuryPage {
  summary: ActivityTreasurySnapshot;
  entries: ActivityTreasuryEntry[];
  totalEntries: number;
  page: number;
  pageSize: number;
  categories: ActivityTreasuryCategory[];
  kinds: ActivityTreasuryKind[];
  /** Who a split can be shared with. Empty unless canManage. */
  members: ActivityTreasuryMember[];
  canManage: boolean;
}

export interface ActivityTreasuryEntryInput {
  transactionKind: string;
  amount: number;
  transactionDate?: string | null;
  memo?: string | null;
  counterpartyAppUserId?: string | null;
  counterpartyCharacterName?: string | null;
  /** False keeps it an editable draft; true puts it on the books. */
  confirm: boolean;
  /** Membership rows, not names. The server resolves each one against this linkshell's roster. */
  recipientMembershipIds?: number[] | null;
}

export interface ActivityTreasuryFixInput extends Omit<ActivityTreasuryEntryInput, 'confirm'> {
  reason: string;
}

export type ActivityTreasuryFilter = 'all' | 'in' | 'out' | 'drafts' | 'reversed';

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
  postDkpSheet: boolean;
  eventTypeFilter: string[];
  // Per-monster narrowing for an HNM route (only meaningful when eventTypeFilter has HNM).
  hnmMonsterFilter: string[];
}

export interface ActivityChannelRoutesResponse {
  guildConfigured: boolean;
  availableChannels: ActivityDiscordChannelOption[];
  postTypes: ActivityChannelPostType[];
  eventTypes: string[];
  // HNM monster picklist for the per-monster route narrowing.
  monsterOptions: string[];
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
  postDkpSheet: boolean;
  eventTypeFilter: string[];
  hnmMonsterFilter: string[];
}

// ---- DKP pools ----
//
// A pool is a wallet. Each event type earns into exactly one pool, and loot from that event type is
// paid out of the same pool. Exactly one pool is the default: the catch-all for every event type
// nobody assigned (including custom ones), plus adjustments and imports.

export interface ActivityDkpPool {
  id: number;
  name: string;
  isDefault: boolean;
  sortOrder: number;
  accent: string;
  eventTypes: string[];
}

// An event type an officer can assign. earnedTotal is what it has earned lifetime — it makes a
// remap legible ("moving Sea moves 980 DKP") and flags types nobody has mapped yet.
export interface ActivityDkpPoolEventType {
  key: string;
  isCustom: boolean;
  earnedTotal: number;
  inUse: boolean;
}

export interface ActivityDkpPoolsResponse {
  pools: ActivityDkpPool[];
  assignableEventTypes: ActivityDkpPoolEventType[];
  accents: string[];
}

// id is null for a new pool. eventTypes is the COMPLETE set assigned to it — the save is a full
// replace, so anything left out falls back to the default pool.
export interface ActivityDkpPoolInput {
  id: number | null;
  name: string | null;
  isDefault: boolean;
  accent: string | null;
  eventTypes: string[];
}

export interface ActivityDkpPoolMove {
  eventType: string;
  fromPool: string;
  toPool: string;
  earnedTotal: number;
  ledgerRows: number;
}

export interface ActivityDkpPoolPreview {
  moves: ActivityDkpPoolMove[];
  affectedLedgerRows: number;
  affectedMembers: number;
  warnings: string[];
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
  // Current active-credit streak: consecutive most-recent counting events the
  // member was credited for (0 = on an absence run / no counting events).
  activeCreditStreak?: number;
  // Current absent streak: consecutive most-recent counting events NOT credited
  // (mutually exclusive with activeCreditStreak — one is always 0).
  absentStreak?: number;
  // Computed Active/Inactive from event attendance. Null when the linkshell has
  // not enabled activity tracking — the badge is hidden in that case.
  active?: boolean | null;
  // True for an "unsynced" member (a player who hasn't linked an account) — badged in
  // the roster. (Backed by a placeholder account server-side; see AppUser.IsPlaceholder.)
  isPlaceholder?: boolean;
  // True when the member has actually opened/synced the Discord Activity at least once
  // (server checks for a DiscordActivityUser row on their AppUserId). Distinguishes real
  // app users from members who only ever used the outside sign-up board. Drives the
  // roster's "App Sync" badge and filter.
  hasSyncedActivity?: boolean;
  // Spendable DKP right now (committed − bid locks − pending live-event loot spend).
  biddableDkp?: number;
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
  // Catalog-aligned relic flags (true = member marked owning that job's relic).
  // Rendered as a flaming border on the job pill.
  relicFlags: boolean[];
  alt1RelicFlags: boolean[];
  alt2RelicFlags: boolean[];
  // Catalog-aligned per-job merit notes (free text), shown on merited job pills.
  meritJobs: string[];
  alt1MeritJobs: string[];
  alt2MeritJobs: string[];
  // Catalog-aligned per-job relic weapon names (e.g. "Bravura"); empty when none.
  relicNames: string[];
  alt1RelicNames: string[];
  alt2RelicNames: string[];
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
  // True when they used "Withdraw From Event" (parked in the Break Room, not intending
  // to return) vs a normal break — drives a "Not returning" label. Cleared on resume.
  withdrewFromEvent?: boolean | null;
  // Spendable DKP right now (committed − bid locks − pending live-event loot spend).
  biddableDkp?: number;
  proctor?: string | null;
  startTime?: string | null;
  resumeTime?: string | null;
  pauseTime?: string | null;
  duration?: number | null;
  eventDkp?: number | null;
  statusLedger: ActivityStatusLedgerEntry[];
  // Manual Check In only. The window this member first checked in for, and the one they
  // checked out on (null = still in). Credit runs arrival..min(departure, popWindow),
  // inclusive. Null on Standard camps and on anyone who never checked in.
  wdArrivalWindow?: number | null;
  wdDepartureWindow?: number | null;
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
  // Which pop window it showed up on (round-trips into the form on edit).
  popWindow?: number | null;
  // Whether the kill was HQ (shown in the ToD list).
  hq?: boolean;
  // Extra seconds folded into the repop time (round-trips into the form on edit).
  additionalSeconds?: number;
  // The observed Time of Death. Null = "Not entered": the camp ended without anyone seeing it
  // die (the window closed, or another linkshell took it), so no time was recorded. A null
  // `time` always comes with a null `repopTime` — there's nothing to derive a repop from.
  time?: string | null;
  // When the row was written, as distinct from `time`. Sort on time ?? timeStamp so a
  // not-entered ToD still counts as the monster's newest entry (see todSortKey).
  timeStamp?: string | null;
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
  // The event's own HNM monster (for manual HNM boards); used to pre-fill "Post ToD".
  assignedMonsterName?: string | null;
  // Optional "Day N" label shown on the board (round-trips into the edit form).
  dayNumber?: number | null;
  // HNM "defeated / awaiting re-post" state: true once a ToD is logged from the board.
  // startTime = predicted repop; hnmRepostAt = when the board auto-re-posts (null if
  // Repeat-on-ToD off); sourceTodId = the logged ToD so "Edit ToD" can pre-fill it.
  hnmAwaitingRepost?: boolean;
  hnmRepostAt?: string | null;
  sourceTodId?: number | null;
  // HNM recurring-board settings (from the matching enabled board), so the edit form can
  // repopulate the "Repeat post" toggle + lead time. repeatLeadHours is fractional hours.
  repeatOnTod?: boolean;
  repeatLeadHours?: number | null;
  // How many SPAWN windows the camp runs — pop chances, 7 on a king/dragon. Heads the card as
  // "Window N of M", matching the Discord board.
  windowCount: number;
  // How many ATTENDANCE POSTS it takes — roster reads, 2 on a Standard king/dragon (an Open and
  // a Close). A DIFFERENT number from windowCount, and the one the Attendance Windows card and
  // every window NAME go by: those tabs are posts, not pop chances. Optional for payloads from a
  // server predating the field — fall back to windowCount, which is what the two were before
  // they were split. See DiscordEventMessageBuilder.AttendancePostCount.
  attendancePostCount?: number;
  // Whether the Break Room applies (take break / force break / return / verify / deny, and the
  // live "Withdraw From Event" that parks a member there). False for windowed HNM camps: they
  // credit per posted window, so there is no timer to pause. Server-computed from
  // Services/EventBreakPolicy — branch on this, never on a local windowCount test, so the UI
  // can't offer a control the endpoints would refuse. Optional only for payloads from a server
  // predating the flag; see supportsBreakRoom() in events-tab.component.ts for the fallback.
  supportsBreakRoom?: boolean;
  // False = this camp was ended with NO Time of Death (the window closed, or another linkshell
  // took it). There is then no predicted repop: startTime still points at the pop that just
  // passed and nothing auto-re-posts, so the defeated banner must not announce a repop.
  // Optional for payloads from a server predating the flag; treat undefined as true.
  hnmTodRecorded?: boolean;
  // Per-camp overrides for the linkshell's four Standard-mode HNM bonuses. Null/undefined
  // means this camp pays the linkshell default; a number means the creator priced this camp
  // itself via "Change DKP" on the create/edit form.
  hnmOpenBonusOverride?: number | null;
  hnmCloseBonusOverride?: number | null;
  hnmClaimBonusOverride?: number | null;
  hnmKillBonusOverride?: number | null;
  hnmPerWindowOverride?: number | null;
  attendanceWindows: ActivityAttendanceWindow[];
  linkedSnapshots: ActivityLinkedSnapshot[];
  claimShieldCaptures: ActivityClaimShieldCapture[];
  creatorCharacterName?: string | null;
  starterCharacterName?: string | null;
  // The DKP pool this event earns into and pays its loot out of. Null when the linkshell has a
  // single pool — the cue to render the loot UI exactly as it did before pools existed.
  dkpPoolName?: string | null;
  // Live HNM camp state. attendanceMode 'Wd' = Manual Check In, null = Standard. nextWindowAt is
  // when the next window opens (null on the final window / not timed). wdFinalizedAt is stamped at
  // End Camp (always null on a Standard board); wdAwaitingProcessingSince is a legacy column that
  // nothing writes any more — camps hand their roster to the attendance sections of the Event System tab for review instead.
  attendanceMode?: string | null;
  // The window that has already OPENED. Pop-window semantics use this (End Camp treats it as the
  // window it popped on). Do NOT show it — it reads one lower than the Discord board.
  hnmWindowNumber?: number;
  // The window to DISPLAY: what the Discord board shows (the window being awaited). Always use
  // this for any "Window N of M" the user reads.
  hnmFocusWindow?: number;
  nextWindowAt?: string | null;
  wdAwaitingProcessingSince?: string | null;
  wdFinalizedAt?: string | null;
  wdPopWindow?: number | null;
}

export interface ActivityAttendanceWindow {
  id: number;
  sequenceNumber: number;
  label?: string | null;
  postedAt: string;
  attendees: ActivityAttendanceWindowAttendee[];
  // What an officer priced THIS window at, or null when they never did and the camp's own open /
  // close bonuses apply. An explicit amount REPLACES those bonuses rather than adding to them —
  // see EventsTabComponent.windowValue, which mirrors HnmStandardCampFinalizer.WindowValue.
  // Only ever non-null on a Standard HNM camp; the server refuses the write on any other kind.
  dkpAmount?: number | null;
}

// A snapshot an officer attached to this camp from the Event System's unlinked list.
// Presentational only — payroll still runs off the snapshot's attendance event — so these
// rows are read-only here, unlike the attendance windows above them.
export interface ActivityLinkedSnapshot {
  id: number;
  name?: string | null;
  capturedAtUtc: string;
  capturedByCharacterName?: string | null;
  // Already named the way the window tabs are ("Open" / "Close" / "Window 3"), or null on a
  // camp with no window grid.
  windowLabel?: string | null;
  snapshotStatus: string;
  entries: ActivityLinkedSnapshotEntry[];
}

export interface ActivityLinkedSnapshotEntry {
  id: number;
  characterName?: string | null;
  mainJob?: string | null;
  mainJobLevel?: number | null;
  subJob?: string | null;
  subJobLevel?: number | null;
  zone?: string | null;
  // A name an officer typed rather than one the addon scanned; tinted in the table.
  addedManually: boolean;
}

// One claim-shield lottery captured by the addon during this camp.
export interface ActivityClaimShieldCapture {
  id: number;
  monsterName: string;
  won: boolean;
  // Players in the lottery server-wide, from the game's own result line — NOT a
  // linkshell figure. members.length is the linkshell's share of it.
  totalPlayers: number;
  capturedAtUtc: string;
  capturedMessage?: string | null;
  // The posted window this pop falls inside (the last one posted at or before
  // it), or null when the camp has posted none yet. This is what ties the claim
  // to a window: the game stamped the lottery line, so the capture dates the
  // window rather than the other way round.
  nearestWindowSequence?: number | null;
  members: ActivityClaimShieldMember[];
}

export interface ActivityClaimShieldMember {
  characterName: string;
  // "Azurth casts Dia on the Aspidochelone." — the line the tag rests on. Null
  // on captures stored before actions were recorded; show the name alone.
  actionMessage?: string | null;
  matched: boolean;
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
  // Set when this row came from ending an HNM camp rather than an addon "/lsm now" capture.
  // Camp rows arrive with every member's DKP already computed from the camp's scoring; snapshot
  // rows don't. Drives the "Camp" tag on the card header.
  sourceEventId?: number | null;
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
  // The spawn window this capture was taken in, off the camp's fixed grid (10-minute steps on the
  // 7-window kings/dragons, hourly on the 25-window wyrms). Null on camps with no cadence at all —
  // Sky gods, farm NMs, ad-hoc `/lsm now` posts — which show no window tag.
  windowNumber?: number | null;
  // Pre-rendered "Window 3 of 25" for display; null whenever windowNumber is.
  windowLabel?: string | null;
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
  // Typed in by an officer via "+ Add person" rather than scanned by the addon. Sorted to the
  // bottom of the snapshot server-side, and tinted here so an asserted name never reads as
  // captured evidence.
  addedManually?: boolean;
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
  // The linkshell the representative token row is bound to. One pairing spans
  // several, so this is not necessarily the linkshell currently selected.
  linkshellId: number;
  prefix: string;
  label?: string | null;
  createdAt: string;
  lastUsedAt?: string | null;
  issuedToAppUserId?: string | null;
  // True when the pairing belongs to the viewer — those show on every linkshell
  // they cover, not just the selected one.
  mine?: boolean;
  // Names of every linkshell this one pairing code connected.
  linkshells?: string[];
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
  // DKP spent on loot in still-live events, not yet committed. Shown as a pending
  // deduction; already removed from biddable power so it can't be double-spent.
  pendingLootSpend?: number;
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
  // Selected member's DKP spent on loot in still-live events, not yet committed. Shown as
  // a pending deduction; already removed from biddable power.
  selectedPendingLootSpend?: number;
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
  // Viewer's available DKP for THIS auction — the balance in the pool it draws from, minus DKP
  // locked by bids they're currently winning on it. Set by the list endpoint.
  availableDkp?: number | null;
  // True when leadership has frozen bidding for the linkshell.
  auctionsLocked?: boolean;
  // The DKP pool bids are drawn from. Name is null when the linkshell has a single pool — the cue
  // to hide the pool chip entirely.
  dkpPoolId?: number | null;
  dkpPoolName?: string | null;
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
  // Built-in per-monster spawn-window setups. Global and read-only — surfaced so the HNM
  // Settings card can show the real numbers instead of a hand-copied duplicate.
  hnmWindowSetups?: HnmWindowSetup[];
}

// One monster's built-in spawn-window setup: how many windows the camp runs and how many
// minutes apart they open.
export interface HnmWindowSetup {
  monster: string;
  windows: number;
  minutes: number;
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
  // HNM signup board only: the monster the board is for, plus whether the board re-posts
  // before the next predicted pop when a new ToD is recorded. No lead time here — that's
  // entered on the End Camp / Post ToD form, so creating or editing an event never
  // overwrites it.
  monsterName?: string | null;
  repeatOnTod?: boolean;
  // HNM signup board only: optional "Day N" label shown on the board.
  dayNumber?: number | null;
  // HNM signup board only: per-camp overrides for the linkshell's payout amounts. Null =
  // use the linkshell default (what the form sends unless the creator opened "Change DKP").
  // Open/Close apply in Standard mode, PerWindow in Wd mode, Claim/Kill in both.
  hnmOpenBonusOverride?: number | null;
  hnmCloseBonusOverride?: number | null;
  hnmClaimBonusOverride?: number | null;
  hnmKillBonusOverride?: number | null;
  hnmPerWindowOverride?: number | null;
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
  // Which pop window it showed up on. null = not recorded.
  popWindow?: number | null;
  hq: boolean;
  additionalSeconds: number;
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
  // Which pop window it showed up on. null = not recorded.
  popWindow?: number | null;
  hq: boolean;
  additionalSeconds: number;
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
  // Which character to sign up as (main or an alt name). Blank/undefined = main.
  characterName?: string;
}

// A past (closed) event for the Activity event-history view.
export interface ActivityEventHistoryParticipant {
  id: number;
  appUserId?: string | null;
  characterName?: string | null;
  // The member's other character names (alts), shown in small text next to the name.
  altNames?: string[];
  jobName?: string | null;
  subJobName?: string | null;
  duration?: number | null;
  eventDkp?: number | null;
  activeCredit?: boolean;
}

// A linkshell member who did NOT attend a given past event (so they can be marked
// Absent — the default — or added with DKP).
export interface ActivityEventHistoryAbsentee {
  appUserId: string;
  characterName?: string | null;
  // The member's other character names (alts), shown in small text next to the name.
  altNames?: string[];
}

export interface ActivityEventHistory {
  id: number;
  eventName?: string | null;
  eventType?: string | null;
  eventLocation?: string | null;
  startTime?: string | null;
  endTime?: string | null;
  duration?: number | null;
  dkpPerHour?: number | null;
  eventDkp?: number | null;
  participants: ActivityEventHistoryParticipant[];
  absentees?: ActivityEventHistoryAbsentee[];
}

export interface ActivityEventHistoryResponse {
  canManage: boolean;
  histories: ActivityEventHistory[];
}

// Post-event discussion comment (author shows "Anonymous" when posted anonymously).
export interface ActivityEventComment {
  id: number;
  author: string;
  isAnonymous: boolean;
  body: string;
  createdAt: string;
  canDelete: boolean;
}

export interface ActivityEventCommentsResponse {
  canManage: boolean;
  comments: ActivityEventComment[];
}

// Edit payload for a closed event (changing dkpPerHour rescales attendee DKP).
export interface ActivityEditEventHistoryInput {
  eventName?: string | null;
  eventType?: string | null;
  eventLocation?: string | null;
  details?: string | null;
  duration?: number | null;
  dkpPerHour?: number | null;
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
  // Per-craft levels (main + alts) in PROFILE_CRAFT_OPTIONS order; null leaves
  // the existing values unchanged (alts cleared when their name is removed).
  craftLevels?: number[] | null;
  alt1CraftLevels?: number[] | null;
  alt2CraftLevels?: number[] | null;
  // Per-job free-text merit notes (main + alts), catalog-aligned; null leaves
  // the existing notes unchanged.
  meritJobs?: string[] | null;
  alt1MeritJobs?: string[] | null;
  alt2MeritJobs?: string[] | null;
}

// One job's peer-rating summary for a target member (gear/skill are 1-5, 0 = unset).
export interface ActivityJobRating {
  jobIndex: number;
  // The target character's level for this job (0 = unleveled / unknown), so the
  // rater can see what they're scoring.
  level: number;
  hasSelf: boolean;
  selfGear: number;
  selfSkill: number;
  selfRelic: boolean;
  selfRelicNames: string[];
  myGear: number;
  mySkill: number;
  myRelic: boolean;
  myRelicNames: string[];
  peerCount: number;
  peerAvgGear: number;
  peerAvgSkill: number;
  peerRelicYes: number;
}

export interface ActivityJobRatingsResponse {
  isSelf: boolean;
  jobs: ActivityJobRating[];
  // Distinct teammates who rated this character (one teammate = one count across
  // all jobs they rated). Per-job averages live on each ActivityJobRating.
  peerRaterCount: number;
  // Anonymous peer comments left about the target; the caller's own comment (editable).
  peerComments: string[];
  peerCommentCount: number;
  myComment: string;
}

// AI summary of the peer comments a member has received. `summary` is null when
// the AI service is unconfigured or the call failed — callers fall back to the
// raw `peerComments` list. `configured` reports whether a key is present at all.
export interface ActivityJobRatingCommentSummary {
  commentCount: number;
  summary: string | null;
  configured: boolean;
}

// A member's OVERALL ratings rollup across ALL their characters: average self
// gear/skill (their own assessment) + average peer gear/skill (what the linkshell
// thinks) + distinct teammate count, plus an AI summary over every peer comment.
export interface ActivityJobRatingOverall {
  selfCount: number;
  selfAvgGear: number;
  selfAvgSkill: number;
  peerRaterCount: number;
  peerAvgGear: number;
  peerAvgSkill: number;
  commentCount: number;
  comments: string[];
  summary: string | null;
  configured: boolean;
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
  // partyId/allianceId are 0 on the reusable-template board and the real ids on an
  // event board (so officers can target slots/parties for drag-drop + inline edits).
  partyId: number;
  name: string;
  slots: ActivityPartySetupSlot[];
}

export interface ActivityPartySetupAlliance {
  allianceId: number;
  name: string;
  parties: ActivityPartySetupParty[];
  // Event boards only: the member designated this alliance's lead (👑 by the
  // alliance name), or null. Set via "Make Me Alliance Lead".
  leadAppUserId?: string | null;
  leadCharacterName?: string | null;
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
  // Event boards only: members attending without a party slot.
  alsoAttending?: ActivityAlsoAttending[] | null;
}

export interface ActivityAlsoAttending {
  characterName?: string | null;
  role?: string | null;
  mainJob?: string | null;
  subJob?: string | null;
  // Present on event boards so a member can withdraw their own no-slot signup.
  appUserId?: string | null;
}

export interface ActivityPartySetupSignUpInput {
  role?: string | null;
  mainJob?: string | null;
  subJob?: string | null;
  // Event boards only: also claim this party's leader spot (first-claim-wins).
  asLeader?: boolean;
  // Event boards only: sign up as a specific character (main or an alt). Omitted
  // / null = the member's main character.
  characterName?: string | null;
  // Bypass the "fill earlier alliances first" nudge ("Sign up here anyway").
  force?: boolean;
}

// "Fill earlier alliances first" nudge returned by the event signup endpoint when
// an open slot the member's job can fill is still free in an earlier alliance.
export interface PartySignupNudge {
  suggestedSlotId: number;
  location: string;
  requirement: string;
  role?: string | null;
  mainJob?: string | null;
  subJob?: string | null;
}

// ----- Officer board-edit request bodies (live event party board) -----
export interface BoardSlotRequirementInput {
  role?: string | null;
  mainJob?: string | null;
  subJob?: string | null;
}

export interface BoardMoveMemberInput {
  // null fromSlotId = the member is currently in "Also Attending"; null toSlotId =
  // move them TO "Also Attending". Identify the member by app-user id (or Discord id
  // for an unsynced/outside member) when moving from Also Attending.
  fromSlotId?: number | null;
  toSlotId?: number | null;
  appUserId?: string | null;
  discordUserId?: string | null;
}

export interface BoardAddSlotInput {
  partyId: number;
  role?: string | null;
  mainJob?: string | null;
  subJob?: string | null;
}

export interface BoardRenameInput {
  allianceId?: number | null;
  partyId?: number | null;
  name?: string | null;
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

// --- DKP Sheet (always-on, computed from the app's own DKP data) ---

export interface ActivityDkpSheetMember {
  id: number;
  name: string;
  alt1: string;
  alt2: string;
  current: number;
  biddable: number;
  total: number;
  spent: number;
  // Parallel to ActivityDkpSheetResponse.pools, so columns and cells walk in the same order.
  // Empty when the linkshell has a single pool.
  poolCurrent: number[];
}

export interface ActivityDkpSheetPool {
  poolId: number;
  name: string;
  accent: string;
}

export interface ActivityDkpSheetResponse {
  linkshellId: number;
  linkshellName: string;
  totalMembers: number;
  totalDkp: number;
  biddable: number;
  totalSpent: number;
  members: ActivityDkpSheetMember[];
  // Empty unless the linkshell has more than one DKP pool — the cue to render the sheet exactly
  // as it did before pools existed.
  pools: ActivityDkpSheetPool[];
  poolTotals: number[];
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

// An unclaimed PLACEHOLDER member (created by the DKP import, carrying a seeded
// balance) whose character name matches the signed-in user — i.e. likely "them",
// imported before they joined. Surfaced as a "Claim your DKP" prompt.
export interface ActivityClaimCandidate {
  placeholderAppUserId: string;
  linkshellId: number;
  linkshellName: string;
  characterName: string;
  currentDkp: number;
  totalDkp: number;
  totalSpent: number;
}

// ---- Charts (Sky, Sea, …) ----------------------------------------------------------------------
// Mirrors the ActivityChart* records in Models/Activity/ActivityDtos.cs. Hand-written — there is no
// codegen — so the two must be edited together.
//
// Board-agnostic: Sky's five gods and Sea's eight Jailers use these same interfaces, differing only
// in what the server's ChartBoardCatalog says.

/** Lower-case slug naming a CSS class. Never carries a colour value. */
export type ChartThemeKey = string;

/** Chooses the card layout. The data behind every kind is identical. */
export type ChartBossKind = 'Standard' | 'MiniNm' | 'Final';

/** Composed server-side so both surfaces word the ledger identically. */
export type ChartCreditStatus = 'Credited' | 'Partial' | 'None' | 'NotTracked';

export interface ActivityChartCredit {
  membershipId: number | null;
  characterName: string;
  detail?: string | null;
}

export interface ActivityChartPopItem {
  id: number;
  board: string;
  boss: string;
  itemName: string;
  heldByCharacterName?: string | null;
  heldByMembershipId: number | null;
  quantity: number;
  notes?: string | null;
  sortOrder: number;
  credits: ActivityChartCredit[];
  /** What the card's "Farmers Credited" column shows. */
  creditCount: number;
  updatedAt: string;
}

/**
 * One pop item a boss takes. `name` is what gets stored; `source` is the mob that drops it; `label`
 * is the two composed server-side, so this surface and the website word the picker identically.
 */
export interface ActivityChartPopItemOption {
  name: string;
  source?: string | null;
  label: string;
}

export interface ActivityChartBoss {
  boss: string;
  themeKey: ChartThemeKey;
  kind: ChartBossKind;
  /**
   * Section heading this card sits under, or null on an ungrouped board. Cards sharing a label are
   * always adjacent, which is what lets the grid collapse them into runs.
   */
  group?: string | null;
  emblemPath: string;
  subtitle?: string | null;
  /** Static reference content — currently only the final encounter's reward list. */
  rewards: string[];
  referenceNote?: string | null;
  /**
   * The card this one's drops feed ("Suzaku"), or null. Renders as the "→ Suzaku" arrow badge on a
   * farm-NM card. Names a boss, never a colour.
   */
  leadsTo?: string | null;
  /** That card's OWN theme key, so the badge is tinted in the TARGET's hue, not this card's. */
  leadsToThemeKey?: ChartThemeKey | null;
  /**
   * Start a new row after this card. A board that sets it anywhere is drawn as centred rows of
   * fixed-width cards rather than as a stretch-to-fit grid.
   */
  endsRow?: boolean;
  /**
   * The pop items this boss takes. Non-empty makes the form's "Pop item" box a picker; empty leaves
   * it free text, which is what every board but Sky sends today.
   */
  popItemOptions: ActivityChartPopItemOption[];
  items: ActivityChartPopItem[];
  totalItems: number;
  totalQuantity: number;
}

export interface ActivityChartLedgerCell {
  boss: string;
  status: ChartCreditStatus;
  /** "6 / 8", or an em dash when the boss has nothing tracked. */
  detail: string;
  creditedItems: number;
  totalItems: number;
}

export interface ActivityChartLedgerRow {
  membershipId: number | null;
  characterName: string;
  /** False for someone credited on an item who has since left the linkshell. */
  isCurrentMember: boolean;
  rank?: string | null;
  cells: ActivityChartLedgerCell[];
  /** Items credited over items tracked across the whole board, plus the percentage. */
  totalCredited: number;
  totalTracked: number;
  creditedPercent: number;
}

export interface ActivityChartLedger {
  bosses: string[];
  rows: ActivityChartLedgerRow[];
}

export interface ActivityChartRosterMember {
  membershipId: number;
  appUserId?: string | null;
  characterName: string;
  rank?: string | null;
  /**
   * This member's own other characters, for the "Held by" list. Farming credit is not offered per
   * alt — credit belongs to a membership, and an alt is the same person.
   */
  altCharacterNames: string[];
}

export interface ActivityChartBoard {
  linkshellId: number;
  board: string;
  boardLabel: string;
  blurb: string;
  bosses: ActivityChartBoss[];
  /**
   * Group labels drawn as vertical columns rather than rows of the grid — Sky's four paths, where
   * the two farm NMs that feed a god stack above it. Empty for every other board. A run not named
   * here renders below the columns.
   */
  pathColumns: string[];
  /**
   * Draw as centred rows of fixed-width cards rather than a stretch-to-fit grid, for a board that
   * chose its own row lengths (Dynamis, Limbus, HENM).
   */
  centersRows: boolean;
  ledger: ActivityChartLedger;
  /** Empty unless canManage — a reader has nobody to pick. */
  roster: ActivityChartRosterMember[];
  lastUpdatedUtc?: string | null;
  /** The server's own answer on whether this member may edit. Never re-derive it from permissions. */
  canManage: boolean;
}

/** The boards the sub-nav offers, so the client does not keep its own list. */
export interface ActivityChartBoardSummary {
  board: string;
  label: string;
}

export interface ActivityChartPopItemInput {
  boss: string;
  itemName: string;
  heldByCharacterName?: string | null;
  heldByMembershipId?: number | null;
  quantity: number;
  notes?: string | null;
  /**
   * Who farmed it, named while the row is written instead of in a second trip through the credits
   * endpoint. Set-wise: a list REPLACES what the row has, and an empty list clears it. Omitting the
   * field leaves the row's credits alone — it is not the same as sending [].
   */
  credits?: ActivityChartCreditInput[];
}

export interface ActivityChartCreditInput {
  membershipId?: number | null;
  characterName?: string | null;
  detail?: string | null;
}
