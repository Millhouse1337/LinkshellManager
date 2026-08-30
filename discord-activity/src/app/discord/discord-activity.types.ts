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
  // Per-job levels for the selectable jobs in PROFILE_JOB_OPTIONS order
  // (index 0 = WAR … 14 = SMN, 15 = BLU … 17 = PUP). Pre-fills the "My Jobs" editor.
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
  // Per-job free-text merit notes (main + alts), catalog-aligned (WAR … PUP).
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

// One monster's setup as it rides on the polled overview: the option list for the pickers plus
// the values they auto-fill from. Always complete — a linkshell that has never opened the editor
// receives the built-in defaults, which is what lets this client keep no cadence/cooldown tables
// of its own.
//
// windows/cadenceMinutes are null together when a monster has no spawn grid (Sky gods, most
// ground NMs); cadenceMinutes doubles as the ToD form's suggested Interval.
export interface ActivityMonsterSetup {
  monsterName: string;
  windows: number | null;
  cadenceMinutes: number | null;
  cooldownMinutes: number;
  category: string;
  // This monster's standing "repeat the sign-up board" lead in fractional hours, or null when it
  // has no ENABLED recurring board. Null means both "recurrence is off" and "no lead set" — the
  // same state, since a disabled board's stored lead is stale bookkeeping.
  //
  // The create-event form reads it the moment a monster is picked, so its recurrence toggle and
  // lead open on what that monster is already configured for rather than on a blank choice.
  repeatLeadHours?: number | null;
}

// The fuller row the Monster Setups editor loads: adds the row id, the built-in defaults (shown as
// placeholders and restored by Reset) and whether the linkshell added this monster itself.
export interface ActivityMonsterTiming {
  id: number;
  monsterName: string;
  windows: number | null;
  cadenceValue: number | null;
  cadenceUnit: string | null;
  // Nullable on the CLIENT even though the server always sends a number: a row someone just added
  // starts blank, so they type the repop they know instead of editing a 22 that was never theirs.
  // A blank one saves as the built-in band for that monster (MonsterTimingEditor.NormalizeCooldown)
  // — never as a zero cooldown, which would repop the monster the instant it died.
  cooldownValue: number | null;
  cooldownUnit: string;
  category: string;
  isCustom: boolean;
  defaultWindows: number | null;
  defaultCadenceMinutes: number | null;
  defaultCooldownMinutes: number;
  // Whether the in-game addon records claim-shield lotteries for this monster. Defaults on; turn
  // it off for monsters the linkshell doesn't contest, whose rolls are just noise in the capture
  // panel. Overridden by the server-wide Claim Shield switch, which a super admin owns.
  claimShieldEnabled: boolean;
}

export interface ActivityMonsterTimingsResponse {
  rows: ActivityMonsterTiming[];
  categories: string[];
  maxWindows: number;
}

export interface ActivityMonsterTimingInput {
  id: number | null;
  monsterName: string;
  windows: number | null;
  cadenceValue: number | null;
  cadenceUnit: string | null;
  cooldownValue: number | null;
  cooldownUnit: string | null;
  category: string;
  claimShieldEnabled: boolean;
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
  // Every monster this linkshell can log a ToD for, with its configured windows / cadence /
  // cooldown. Replaced todMonsterTimings, which carried only the cooldown half and only for
  // monsters the linkshell had explicitly overridden.
  monsterSetups: ActivityMonsterSetup[];
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
  // Allow Discord members with no LSM account to sign up (or Check In) from a board, for
  // EVERY event type including HNM. Backed by a placeholder member, so they DO earn DKP +
  // are tracked.
  outsidePartySignupEnabled?: boolean;
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
  /** Cancelled by a FIX rather than an outright reversal: the right numbers were recorded in its
      place. Both are true of a corrected entry, so the row shows the more specific word. */
  isFixed: boolean;
  correctionReason?: string | null;
  /** Everyone who got a share. Empty for an ordinary entry, one name for a single-member one. */
  recipients: ActivityTreasuryRecipient[];
  lines: ActivityTreasuryLine[];
  /** Whose mule this entry's gil landed on, or came off. Null when it moved no gil, and for
      everything recorded before the question was asked. */
  holderCharacterName?: string | null;
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
/** One of the things that can happen to gil. The picker asks for this first, then for a reason. */
export interface ActivityTreasuryAction {
  key: string;
  label: string;
}

/**
 * One reason under an action.
 *
 * The server sends EVERY kind, not just the pickable ones, because the selected kind is what the
 * whole form is derived from — the split picker, the member box, the preview, and whether Submit
 * does anything at all. Filter on `isPickable` when building the menu; never when resolving what is
 * currently selected, or a Fix on an app-recorded entry silently loses its form.
 */
export interface ActivityTreasuryKind {
  key: string;
  /** What the transactions list calls it. */
  label: string;
  /** What the picker calls it, under its action. Short, and only unique within that action. */
  reasonLabel: string;
  help: string;
  action: string;
  showsMember: boolean;
  /** A member is required, not merely offered — the server refuses the entry without one. */
  requiresMember: boolean;
  /** Shares one amount between several members instead of naming one. */
  isSplittable: boolean;
  /** Picking a member fills in what they are still owed, rather than asking for a number. */
  settlesMemberDebt: boolean;
  /** Offered in the picker. False for the ones the app records for you, and for retired ones. */
  isPickable: boolean;
  /** Superseded — reachable only from Fix, and refused on any other write. */
  isRetired: boolean;
  /** "{0}" is the formatted amount. */
  previewTemplate: string;
  /**
   * What the single name box is CALLED for this option. "Member" for most; the owed-to-us pair asks
   * for a typed name instead, because whoever owes a linkshell gil is usually not in it.
   */
  counterpartyLabel: string;
  /** Whether a mule has to be named — true exactly when this option moves gil on hand. The account
      pair that decides it never crosses the wire, so the server sends the answer. */
  requiresHolder: boolean;
  /** And what that box is called, which flips with the direction: naming who ends up with the gil is
      a different question from naming whose stack it came out of. */
  holderLabel: string;
  /** Which way the gil is going. The two labels above do not compose into a sentence the same way,
      so the blocker picks its wording off the direction rather than off the label. */
  bringsCashIn: boolean;
}

/** One member the linkshell still owes. These always add up to `weOwe`. */
export interface ActivityTreasuryMemberObligation {
  characterName: string;
  amount: number;
  /**
   * Whether this row can be ticked and paid off. False for the "no member named" bucket — a payment
   * has to name who it went to — and for a row overpaid into a negative.
   */
  canSettle: boolean;
}

/**
 * One member being paid what they are owed, and the figure the panel was showing beside them.
 *
 * The server pays what the books say, not this number; it compares the two and refuses the row when
 * they differ, so a panel left open while another officer records more gil owed cannot hand over the
 * newer figure.
 */
export interface ActivityTreasurySettlePick {
  characterName: string;
  expectedAmount: number;
}

/** What a payout run did. `message` is built server-side so both front-ends say the same thing. */
export interface ActivitySettleOwedResult {
  success: boolean;
  message: string;
  totalPaid: number;
  settled: string[];
  skipped: string[];
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
  lockedThroughUtc?: string | null;
  basisNote: string;
  /** Who `weOwe` is owed to, largest first. Projected from the same lines, so it always adds up. */
  owedToMembers: ActivityTreasuryMemberObligation[];
  /** And who owes the LINKSHELL, behind `owedToUs`. The mirror list, ticked the same way. */
  owedToUsBy: ActivityTreasuryMemberObligation[];
  /** Whose mules `cashOnHand` is spread across, largest first. Projected from the same lines, so it
      always adds up to it — including the null-name bucket, which is gil recorded before anyone was
      asked. Unlike the two lists above this one is never ticked: gil leaves a mule by being spent. */
  gilHolders: ActivityTreasuryGilHolder[];
}

/** One person and the slice of the linkshell's gil sitting on their character. */
export interface ActivityTreasuryGilHolder {
  /** Null for gil recorded before holders existed, and for gil-auction payouts, which have no
      answer to give. The front-end labels it rather than dropping it, or the rows would visibly
      fail to add up to the figure above them. */
  characterName?: string | null;
  amount: number;
}

export interface ActivityTreasuryPage {
  summary: ActivityTreasurySnapshot;
  entries: ActivityTreasuryEntry[];
  totalEntries: number;
  page: number;
  pageSize: number;
  categories: ActivityTreasuryCategory[];
  kinds: ActivityTreasuryKind[];
  /** The picker's top level, in display order. Server-supplied so the wording cannot drift from the
      website's — the group headings these replaced were hardcoded separately in both apps. */
  actions: ActivityTreasuryAction[];
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
  /** Whose mule the gil lands on, or comes off. A NAME rather than a membership row, unlike the
      recipients above: gil regularly sits on a mule that is not on the roster. Required by the
      server whenever the chosen kind has `requiresHolder`. */
  holderAppUserId?: string | null;
  holderCharacterName?: string | null;
}

export interface ActivityTreasuryFixInput extends Omit<ActivityTreasuryEntryInput, 'confirm'> {
  reason: string;
}

export type ActivityTreasuryFilter = 'all' | 'in' | 'out' | 'fixed' | 'reversed';

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
  // True when this member carries the app-wide admin override AND it is switched on.
  // Rendered as an "ADMIN" tag BESIDE — never instead of — their rank.
  isAdmin?: boolean;
}

// "Jobs Roster" — every member's leveled jobs. jobCatalog is the job-name order
// (WAR..PUP); each member's level arrays are aligned to it (0 = not leveled).
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

// One pop the linkshell is still waiting on: the newest ToD for a spawn whose predicted repop
// hasn't happened yet. Fed to the create-event form so picking that monster pre-fills Start with
// the pop the camp is almost certainly for (see Services/UpcomingRepopLookup.cs).
export interface ActivityUpcomingRepop {
  todId: number;
  // The name as the ToD stored it, which may be a combined "Base/Stronger" label.
  monsterName: string;
  // Every spelling of the same spawn, server-built from HnmConfig.MonsterMatchNames — match a
  // picked monster against THIS (case-insensitively), never against monsterName, so a "Fafnir"
  // ToD is still found by a "Fafnir/Nidhogg" camp and vice versa.
  matchNames: string[];
  // Predicted repop, UTC ISO.
  repopTime: string;
  dayNumber?: number | null;
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
  // The officer's "this window closes the camp out" tick, and the ONLY thing that decides the
  // close bonus. It used to be derived as "the newest window posted", which is what put a close
  // bonus on every window of every camp. At most one window per event carries it.
  isClosingWindow?: boolean;
  // The addon's Post Kill roster: who was there when the mob died, which is a different list from
  // who sat the window. Worth 0 as a window — being on it earns the kill bonus — and it can never
  // be the closing window.
  isKillWindow?: boolean;
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
  // The character the roster read actually SAW — the alt, when the player was on one.
  characterName?: string | null;
  // Their roster main, set ONLY when characterName above is an alt of it. Renders as the
  // "(alt of Edicius)" note beside the name; null/absent is the ordinary case and shows nothing.
  mainCharacterName?: string | null;
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
  // Attendance Archive paging. `closedEvents` is ONE PAGE of the archive, so the tally and pager
  // have to come off these rather than off its length. `closedQuery` is the trimmed query the
  // server actually built the page from — what the "no results" copy names.
  closedQuery: string | null;
  closedPage: number;
  closedPageSize: number;
  closedTotalCount: number;
  // How many unlinked captures exist versus how many are listed. Every /lsm now post lands there
  // now, so the list can genuinely be hiding some and the panel has to be able to say so.
  unlinkedTotalCount: number;
  unlinkedDisplayCap: number;
}

export interface ActivityWindowEventMemberDkpInput {
  characterName: string;
  dkpAmount: number | null;
}

export interface ActivityWindowEventDkpPayload {
  dkpAmount: number;
  entryType: string;
  memberDkp?: ActivityWindowEventMemberDkpInput[];
  // DKP for members credited ONLY by Misc posts. Null means they are paid the same as a window
  // attendee, which is the default.
  miscDkpAmount?: number | null;
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
  ignoredSnapshotCount: number;
  // Captures still awaiting an officer's Confirm. Non-zero disables Post: those members are not in
  // combinedMembers below, so publishing would short them.
  pendingSnapshotCount: number;
  // The alliances contributing to the combined roster, ascending. More than one is the normal
  // shape for a big camp — it means each alliance fielded its own poster.
  allianceNumbers: number[];
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
  // How many of this camp captures were filed as Misc, plus the rate they are paid at and the
  // camp own window grid for the slot picker. hasWindowGrid false means there are no window
  // numbers to offer (Sky gods, farm NMs); Misc is still selectable.
  miscSnapshotCount: number;
  miscDkpAmount?: number | null;
  windowCount: number;
  hasWindowGrid: boolean;
}

export interface ActivityWindowSnapshot {
  id: number;
  windowEventId?: number | null;
  name?: string | null;
  snapshotStatus: string;
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
  // Which alliance posted this capture. A snapshot is exactly one alliance, and the number is
  // chosen by the poster because the FFXI client cannot see past your own alliance. Null on rows
  // captured before per-alliance posting existed, which is why allianceLabel exists too — it says
  // "Unassigned" rather than implying alliance 1.
  allianceNumber?: number | null;
  allianceLabel: string;
  // Posted by a member without moderation rights and not yet confirmed. Shown on the card, but
  // excluded from the combined roster and from DKP until an officer acts on it.
  isPending: boolean;
  // Window or Misc. Distinct from a null windowNumber, which means the camp runs no grid at all —
  // an ungridded camp still files ordinary Window captures.
  slotKind: string;
  isMisc: boolean;
  // What the chip reads: "Misc", or the windowLabel.
  slotLabel?: string | null;
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
  // Which alliances this character was captured in, ascending. Usually one; two means they moved
  // between alliances mid-camp, which is worth showing rather than flattening away.
  allianceNumbers: number[];
  // Per-character override if one is set on this Window Event; null means
  // the event default applies.
  dkpAmountOverride?: number | null;
  // Override (when set) else the event default — used to seed the per-row
  // DKP input on the combined roster table.
  effectiveDkpAmount?: number | null;
  // "Window", "Misc" or "Both" — why this member is priced the way they are.
  creditSource: string;
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

// One selectable event on the Add loot form.
export interface ActivityLootEventOption {
  id: number;
  name: string;
  detail?: string | null;
}

// Live events plus the recent past ones (widened by a search) for the Add loot pickers.
export interface ActivityLootEventOptions {
  liveEvents: ActivityLootEventOption[];
  pastEvents: ActivityLootEventOption[];
  query?: string | null;
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

// One slice of the HNM Claims donut. `percent` is already relative to its own window's total
// and `colorClass` is the palette letter the ring and legend paint with.
export interface ActivityHnmClaimSlice {
  monsterName: string;
  count: number;
  percent: number;
  colorClass: string;
}

// All three windows arrive together, so the 7d / 30d / All toggle never re-queries.
export interface ActivityHnmClaims {
  last7Days: ActivityHnmClaimSlice[];
  last30Days: ActivityHnmClaimSlice[];
  allTime: ActivityHnmClaimSlice[];
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
  // The dashboard's HNM Claims donut, aggregated by the server over ALL claimed ToDs.
  // recentTods is a 25-row tail of every monster, so counting this here charted only the
  // claims that happened to survive in that tail — and "All" could never mean all.
  hnmClaims: ActivityHnmClaims;
  stats: ActivityOverviewStats;
  addonConfigured: boolean;
  addonGloballyDisabled: boolean;
  // (hnmWindowSetups lived here: a global, read-only monster → windows × cadence list. Window
  // setups are per-linkshell now and ride on each linkshell's settings.monsterSetups.)
  // App-wide admin override: ON globally AND carried by this account. Grants every
  // permission in every linkshell the user is a MEMBER of. `linkshells[].permissions`
  // already arrives all-true, so this is only for the coarse rank checks and the badge.
  // It never applies to a linkshell the user has not joined — the server only ever
  // sends memberships. Use canManageLinkshellIn()/isLeaderTierIn() rather than reading
  // this directly, so the membership check is never skipped.
  adminOverrideActive?: boolean;
  // True when a super admin has switched Claim Shield off server-wide (web Settings page). While
  // it is, the per-monster Claim Shield switches in Monster setups do nothing — the editor greys
  // them out and says so rather than showing ticks the addon is ignoring.
  claimShieldGloballyDisabled?: boolean;
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
  // HNM signup board only: the monster the board is for, plus the monster's standing re-post
  // settings.
  //
  // repeatOnTod is tri-state on the wire. The create form and the queued-camp edit form both ASK
  // ("Repeat post when ToD is updated?") and send an explicit true/false. Editing a LIVE camp
  // sends null — that form has no recurrence control, and null tells the server "no opinion,
  // leave the standing board alone" rather than false, which would disable it.
  //
  // repostLeadHours is fractional (1.5 = 1h30m). Null means "keep the board's current lead", so
  // an empty box never overwrites a lead entered at End Camp / Post ToD.
  monsterName?: string | null;
  repeatOnTod?: boolean | null;
  repostLeadHours?: number | null;
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
  // Attendance windows this member was scanned in. Null on a timed event — there presence is
  // measured as duration. On a windowed camp this, not the duration, is what their DKP came from.
  windowsAttended?: number | null;
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
  // How many attendance windows this closed camp archived. > 0 marks it as HNM-style with a
  // surviving window record — the cue to offer the Attendance windows section, whose contents
  // load separately (see ActivityEventHistoryWindowsResponse). Always 0 on a timed event, and on
  // anything closed before the archive existed: those windows were deleted with the camp.
  archivedWindowCount?: number;
}

export interface ActivityEventHistoryResponse {
  canManage: boolean;
  histories: ActivityEventHistory[];
}

// One member the addon scanned into an archived window.
export interface ActivityEventHistoryWindowAttendee {
  characterName: string;
  // Set only when characterName is one of their alts, so the row can read "Athmilk (alt of
  // Edicius)". Null when they were scanned on their main.
  mainCharacterName?: string | null;
  zone?: string | null;
  verifiedAt: string;
}

// One attendance window a closed camp posted, kept from before the event closed.
export interface ActivityEventHistoryWindow {
  id: number;
  sequenceNumber: number;
  // Already resolved server-side: "Open" / "Close" on a 2-post camp, else "Window N".
  label: string;
  postedAt: string;
  postedBySource?: string | null;
  // Only ever the amount an officer priced THIS window at. The camp's own open/close bonuses are
  // not recoverable after close, so a null here means "not explicitly priced", not "worth 0".
  dkpAmount?: number | null;
  isClosingWindow?: boolean;
  isKillWindow?: boolean;
  attendees: ActivityEventHistoryWindowAttendee[];
}

export interface ActivityEventHistoryWindowsResponse {
  // What "Window 3 of N" reads against.
  windowCount: number;
  // Distinct characters across every window. Can exceed the participant list — the addon records
  // who was standing there, not who joined on the site.
  distinctAttendeeCount: number;
  windows: ActivityEventHistoryWindow[];
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
  // Catalog-aligned per-job levels (index 0 = WAR … 17 = PUP), or null to
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

/**
 * Whether a tracked row is something traded TO a boss or something that fell OFF one. The rows are
 * otherwise identical - same fields, same farming credit, same ledger.
 */
export type ChartItemKind = 'Pop' | 'Drop';

/** There is deliberately no 'Withdrawn': withdrawing deletes the request. */
export type ChartWishlistStatus = 'Pending' | 'Fulfilled';

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
  /** Picks the pill beside the name and which option list the edit form offers. */
  kind: ChartItemKind;
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
  /** What falls OFF this boss. Non-empty makes the drop form's item box a picker. */
  dropItemOptions: ActivityChartPopItemOption[];
  items: ActivityChartPopItem[];
  totalItems: number;
  totalQuantity: number;
  /** Pending requests tied to THIS card. Board-level ones ("anywhere") count toward none. */
  pendingRequestCount: number;
  /** The key item earned here, or null for a card that grants none. */
  keyItemName?: string | null;
  keyItemHaveCount: number;
  keyItemTotalMembers: number;
  /** Exactly who still needs it, in roster order - what the card's drawer lists. */
  keyItemMissing: string[];
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
  /** What this board offers. Branch on THESE, never on whether a list happens to be populated. */
  features: ActivityChartBoardFeatures;
  /**
   * The board's item requests. In this payload rather than a fetch of its own, for the reason the
   * ledger is: a card's badge and the list below it are two views of one set of rows.
   */
  wishlist: ActivityChartWishlist;
  /** Per-member key item progress. No columns on a board that tracks none. */
  keyItems: ActivityChartKeyItemGrid;
  /**
   * The VIEWER's own membership, so the key item grid knows which row is theirs to tick. Null for
   * somebody with no membership row. The server re-checks on every write.
   */
  viewerMembershipId: number | null;
}

/**
 * A board can declare no pop items and still take them (HENM), and can declare none and no longer
 * offer the form at all (Dynamis, Limbus). Those are different facts, so the server sends both.
 */
export interface ActivityChartBoardFeatures {
  popItems: boolean;
  dropItems: boolean;
  wishlist: boolean;
  keyItems: boolean;
}

export interface ActivityChartWishlist {
  requests: ActivityChartWishlistRequest[];
  /** Outstanding across the whole board, board-level requests included. */
  pendingCount: number;
}

export interface ActivityChartWishlistRequest {
  id: number;
  board: string;
  /** The card it is tied to, or null for "anywhere on this board". */
  boss?: string | null;
  itemName: string;
  quantity: number;
  notes?: string | null;
  status: ChartWishlistStatus;
  priority: number;
  requestedByMembershipId: number | null;
  requestedByCharacterName: string;
  /**
   * Whether THIS viewer may withdraw it. Decided SERVER-side per viewer - never re-derive it from
   * the membership id, or this surface becomes a second copy of the ownership rule.
   */
  canWithdraw: boolean;
  requestedAt: string;
  fulfilledAt?: string | null;
  fulfilledByCharacterName?: string | null;
}

export interface ActivityChartKeyItemGrid {
  /** Catalog order, so a key item nobody holds still gets a column reading "0 of 14". */
  columns: ActivityChartKeyItemColumn[];
  rows: ActivityChartKeyItemRow[];
}

export interface ActivityChartKeyItemColumn {
  name: string;
  /** The card it is earned on, or null for a board-level prerequisite. */
  boss?: string | null;
  caption?: string | null;
  haveCount: number;
  totalMembers: number;
  missingCharacterNames: string[];
}

export interface ActivityChartKeyItemRow {
  membershipId: number;
  characterName: string;
  rank?: string | null;
  /** Aligned to the column order above, so nothing here is matched by name. */
  has: boolean[];
  haveCount: number;
  totalColumns: number;
  havePercent: number;
}

export interface ActivityChartWishlistInput {
  /** Blank or null means "anywhere on this board", which is what the form opens on. */
  boss?: string | null;
  itemName: string;
  quantity: number;
  notes?: string | null;
}

/** `has: false` DELETES the row - presence is the fact. */
export interface ActivityChartKeyItemInput {
  keyItemName: string;
  membershipId: number;
  has: boolean;
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
  /**
   * Honoured on ADD only. On update the row's OWN kind wins, exactly as its board does: an item
   * moves between bosses, never between kinds. Omitting it reads as 'Pop'.
   */
  kind?: ChartItemKind;
}

export interface ActivityChartCreditInput {
  membershipId?: number | null;
  characterName?: string | null;
  detail?: string | null;
}
