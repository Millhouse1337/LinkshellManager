using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed record ActivityOverviewDto(
    ActivityAppUserDto AppUser,
    IReadOnlyList<ActivityLinkshellDto> Linkshells,
    ActivityPrimaryLinkshellDto? PrimaryLinkshell,
    IReadOnlyList<ActivityEventDto> ActiveEvents,
    IReadOnlyList<ActivityInviteDto> PendingInvites,
    IReadOnlyList<ActivityInviteDto> SentInvites,
    IReadOnlyList<ActivityInviteDto> IncomingJoinRequests,
    IReadOnlyList<ActivityInviteDto> OutgoingJoinRequests,
    IReadOnlyList<ActivityHistoryDto> RecentHistory,
    IReadOnlyList<ActivityTodDto> RecentTods,
    ActivityOverviewStatsDto Stats,
    // True when the user has at least one non-revoked AddonApiToken.
    // Drives the onboarding "Set up the addon" checklist item.
    bool AddonConfigured,
    // True when a super admin has globally disabled the addon. Hides the
    // Game Addon pairing card in the Configurations tab.
    bool AddonGloballyDisabled);

public sealed record ActivityAppUserDto(
    string Id,
    string UserName,
    string? CharacterName,
    string? AltCharacterName1,
    string? AltCharacterName2,
    string? TimeZone,
    int? PrimaryLinkshellId,
    string? PrimaryLinkshellName,
    // Per-job levels for the 15 classic jobs in EventJobCatalog.MainJobOptions
    // order (index 0 = WAR ... 14 = SMN). Pre-fills the profile "My Jobs" editor.
    IReadOnlyList<int> JobLevels,
    // Same catalog-aligned per-job levels for the two alt characters; pre-fill the
    // per-alt job tabs.
    IReadOnlyList<int> Alt1JobLevels,
    IReadOnlyList<int> Alt2JobLevels,
    // Catalog-aligned "strong" flags parallel to the level arrays above (true =
    // the member marked that job well-geared/merited). Pre-fill the profile
    // editor's per-job Strong toggles.
    IReadOnlyList<bool> StrongJobs,
    IReadOnlyList<bool> Alt1StrongJobs,
    IReadOnlyList<bool> Alt2StrongJobs,
    // Per-craft levels (main + alts) in CraftCatalog order (Alchemy … Fishing).
    // Pre-fill the profile "Crafts" editor.
    IReadOnlyList<int> CraftLevels,
    IReadOnlyList<int> Alt1CraftLevels,
    IReadOnlyList<int> Alt2CraftLevels,
    // Per-job free-text merit notes (main + alts), catalog-aligned (WAR … SMN).
    // Pre-fill the "Merited" modal for each job.
    IReadOnlyList<string> MeritJobs,
    IReadOnlyList<string> Alt1MeritJobs,
    IReadOnlyList<string> Alt2MeritJobs);

public sealed record ActivityLinkshellDto(
    int Id,
    string Name,
    string? Rank,
    string? Status,
    double? LinkshellDkp,
    int MemberCount,
    int ItemCount,
    long Revenue,
    string? Details,
    ActivityPermissionsDto? Permissions,
    ActivityLinkshellSettingsDto Settings,
    bool AuctionsLocked = false,
    string? BannerUrl = null);

public sealed record ActivityLinkshellSettingsDto(
    string LootStructure,
    bool EnableHnmSection,
    bool EnableMissions,
    bool EnableAuctions,
    bool EnableToDs,
    bool EnableEndgame,
    bool EnableEvents,
    bool EnableDkp,
    bool EnableItems,
    bool EnableRevenue,
    string DkpRoundingIncrement,
    // Mob names the linkshell admin has chosen to hide from the ToD Tracker.
    // Stored on the server as a single pipe-separated string; the wire DTO
    // surfaces them as a list to keep the client side ergonomic.
    IReadOnlyList<string> HiddenTodMonsters,
    // SkySeaDynamis | HnmOnly | Both — which content this linkshell runs.
    string LinkshellType,
    // The single Discord server (guild) this linkshell is associated with, or
    // null when not tied to any server. Setting it scopes member search / roster
    // to that server's members and powers channel posting. It does NOT, by
    // itself, restrict who can view the linkshell — that's LockToDiscordGuild.
    // DiscordGuildName is a display cache so the Configurations UI can show which
    // server it is.
    string? DiscordGuildId,
    string? DiscordGuildName,
    // Optional, separate access lock. When true, the Activity can only open this
    // linkshell from DiscordGuildId. Off by default (associated but not locked).
    bool LockToDiscordGuild,
    // Member activity tracking: opt-in Active/Inactive badge from event attendance.
    // Streak hysteresis — Inactive after N consecutive uncredited counting events,
    // back to Active after M consecutive credited ones.
    bool EnableActivityTracking,
    int InactiveAfterAbsences,
    int ActiveAfterAttendances,
    // Palette key for this linkshell's rendered event-board image (one of the
    // EventBoardThemes keys: Crystal, Abyss, Ember, Verdant, Royal, Tome).
    string EventBoardTheme,
    // Allow Discord members with no LSM account to sign up for NON-HNM events from
    // the party board. Backed by a placeholder member, so they DO earn DKP + are tracked.
    bool OutsidePartySignupEnabled,
    // "Fill earlier alliances first" signup nudge (default on; no-op on single-alliance boards).
    bool FillAlliancesInOrder,
    // HNM Outside Sign Up: gates the HNM event type in the create dropdown + account-less
    // Discord signups onto HNM boards. Independent of OutsidePartySignupEnabled.
    // Roster memory only — HNM signups earn no DKP and no active/absent credit.
    bool HnmOutsideSignupEnabled,
    // Experimental: post event boards as Components V2 (wide media-gallery card) instead
    // of the classic image-in-embed. Only affects boards posted after it's turned on.
    bool UseComponentsV2Boards,
    // Discord channel id new post-event discussion comments are mirrored to, or
    // null to keep discussion in-app only.
    string? DiscussionChannelId = null);

public sealed record ActivityPermissionsDto(
    bool CanManageRoles,
    bool CanManageMembers,
    bool CanManageEvents,
    bool CanModerateLiveEvent,
    bool CanAddLoot,
    bool CanManageInventory,
    bool CanManageTreasury,
    bool CanManageRules,
    bool CanManageAnnouncements,
    bool CanManageTods,
    bool CanAuditDkp,
    bool CanManageAuctions,
    bool CanLockAuctions,
    bool CanCustomizeLinkshell,
    bool CanManageParties,
    bool CanManageInvites,
    bool CanBid);

public sealed record ActivityPrimaryLinkshellDto(
    int Id,
    string Name,
    int MemberCount,
    string? Details,
    IReadOnlyList<ActivityMemberDto> Members,
    IReadOnlyList<ActivityRuleDto> Rules,
    IReadOnlyList<ActivityAnnouncementDto> Announcements,
    IReadOnlyList<ActivityItemDto> Items,
    IReadOnlyList<ActivityRevenueEntryDto> RevenueEntries,
    // News-feed sources: recent auctions (open/close) and DKP adjustments.
    IReadOnlyList<ActivityNewsAuctionDto> RecentAuctions,
    IReadOnlyList<ActivityNewsDkpDto> RecentDkpAudits);

// One auction surfaced in the News & Updates feed. `Closed` => the EndTime
// (auction wrapped up); otherwise `When` is when bidding opened.
public sealed record ActivityNewsAuctionDto(int Id, string Title, DateTime When, bool Closed);

// One DKP adjustment surfaced in the News & Updates feed.
public sealed record ActivityNewsDkpDto(string CharacterName, double Amount, bool IsCorrection, DateTime OccurredAt);

public sealed record ActivityItemDto(
    int Id,
    int LinkshellId,
    string ItemName,
    string? ItemType,
    int Quantity,
    string? Notes,
    string? CreatedByAppUserId,
    string? CreatedByCharacterName,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsSold = false,
    long? SoldPrice = null,
    string? SoldByCharacterName = null);

public sealed record ActivityRevenueEntryDto(
    int Id,
    int LinkshellId,
    string EntryType,
    string? Category,
    long Value,
    string? Details,
    DateTime OccurredAt,
    string? CreatedByAppUserId,
    string? CreatedByCharacterName,
    DateTime CreatedAt);

public sealed record ActivityCreateItemRequest(string ItemName, string? ItemType, int Quantity, string? Notes);

public sealed record ActivityUpdateItemRequest(string ItemName, string? ItemType, int Quantity, string? Notes);

public sealed record ActivityCreateRevenueRequest(string EntryType, string? Category, long Value, string? Details, DateTime? OccurredAt);

public sealed record ActivityMarkItemSoldRequest(long SalePrice);

public sealed record ActivityRuleDto(
    int Id,
    int LinkshellId,
    string Title,
    string Details,
    string? Category,
    string? CreatedByAppUserId,
    string? CreatedByCharacterName,
    DateTime CreatedAt);

public sealed record ActivityAnnouncementDto(
    int Id,
    int LinkshellId,
    string Title,
    string Details,
    string? Category,
    string? CreatedByAppUserId,
    string? CreatedByCharacterName,
    DateTime CreatedAt);

public sealed record ActivityCreateRuleRequest(string Title, string Details, string? Category = null);

public sealed record ActivityCreateAnnouncementRequest(string Title, string Details, string? Category = null);

public sealed record ActivityLinkshellDetailDto(
    int Id,
    string Name,
    int MemberCount,
    string? Details,
    string? Status,
    IReadOnlyList<ActivityMemberDto> Members);

public sealed record ActivityMemberDto(
    int Id,
    string? AppUserId,
    string CharacterName,
    string? AltCharacterName1,
    string? AltCharacterName2,
    string? Rank,
    string? Status,
    double? LinkshellDkp,
    DateTime? DateJoined,
    // Current active-credit streak: consecutive most-recent counting events the
    // member was credited for. 0 = on an absence run / no counting events.
    int ActiveCreditStreak = 0,
    // Current absent streak: consecutive most-recent counting events NOT credited.
    // Mutually exclusive with ActiveCreditStreak (one is always 0).
    int AbsentStreak = 0,
    // Computed activity state (event-attendance streak). Null when the linkshell
    // hasn't enabled activity tracking, so the client hides the badge.
    bool? Active = null,
    // True for a "linkshell-only" member (a placeholder account with no real login),
    // so the roster can badge it. See Services/ManualMemberService.
    bool IsPlaceholder = false,
    // Spendable DKP right now = LinkshellDkp − bid locks − pending live-event loot spend.
    // Shown next to the committed balance so officers see real bidding power.
    double BiddableDkp = 0,
    // True when the member has actually opened/synced the Discord Activity at least
    // once (a DiscordActivityUser row points at their AppUserId). Distinguishes real
    // app users from members who only ever interacted via the outside sign-up board
    // (who get an AppUserId without ever opening the Activity). Drives the roster's
    // "App Sync" badge + filter.
    bool HasSyncedActivity = false);

// "Jobs Roster" — every member's leveled jobs (the levels they entered on their
// Profile), for the linkshell's main + alt characters. JobCatalog is the job
// name order (WAR..SMN); each member's level arrays are aligned to it.
public sealed record ActivityJobsRosterDto(
    IReadOnlyList<string> JobCatalog,
    IReadOnlyList<ActivityJobsRosterMemberDto> Members);

public sealed record ActivityJobsRosterMemberDto(
    int Id,
    string CharacterName,
    string? Rank,
    IReadOnlyList<int> JobLevels,
    string? Alt1Name,
    IReadOnlyList<int> Alt1JobLevels,
    string? Alt2Name,
    IReadOnlyList<int> Alt2JobLevels,
    // Catalog-aligned "strong" flags parallel to each character's level array
    // (true = well-geared/merited). Rendered as a marker on the job pills.
    IReadOnlyList<bool> StrongJobs,
    IReadOnlyList<bool> Alt1StrongJobs,
    IReadOnlyList<bool> Alt2StrongJobs,
    // Catalog-aligned relic flags (true = the member marked owning that job's
    // relic on their profile). Rendered as a flaming border on the job pill.
    IReadOnlyList<bool> RelicFlags,
    IReadOnlyList<bool> Alt1RelicFlags,
    IReadOnlyList<bool> Alt2RelicFlags,
    // Catalog-aligned per-job merit notes (free text), shown on merited job pills.
    IReadOnlyList<string> MeritJobs,
    IReadOnlyList<string> Alt1MeritJobs,
    IReadOnlyList<string> Alt2MeritJobs,
    // Catalog-aligned per-job relic weapon names (e.g. "Bravura"); empty when none.
    IReadOnlyList<string> RelicNames,
    IReadOnlyList<string> Alt1RelicNames,
    IReadOnlyList<string> Alt2RelicNames);

public sealed record ActivityEventDto(
    int Id,
    int LinkshellId,
    string? Name,
    string? Type,
    string? Location,
    DateTime? StartTime,
    DateTime? EndTime,
    DateTime? CommencementStartTime,
    double? Duration,
    int? DkpPerHour,
    string? Details,
    bool AutoStart,
    bool CountsTowardActive,
    int ParticipantCount,
    ActivityParticipationDto? CurrentParticipation,
    IReadOnlyList<ActivityEventParticipantDto> Participants,
    IReadOnlyList<ActivityLootDto> Loot,
    // Optional FK to a PartySetup in the same linkshell. The expanded event
    // card fetches the full setup tree (alliances → parties → slots) on
    // demand from /api/activity/party-setups/{id}.
    int? PartySetupId,
    string? PartySetupName,
    string? PartySetupAssignedMonsterName,
    // The event's own HNM monster (Event.AssignedMonsterName), for manually-created
    // HNM boards. Used to pre-fill the "Post ToD" form. Null for non-HNM events.
    string? AssignedMonsterName,
    // Optional "Day N" label for HNM boards (round-trips into the edit form + shown on the board).
    int? DayNumber,
    // HNM "defeated / awaiting re-post" state: true once the ToD is logged from the board.
    // StartTime is the predicted repop; HnmRepostAt is when the board auto-re-posts (null if
    // Repeat-on-ToD is off). SourceTodId is the logged ToD, so "Edit ToD" can pre-fill it.
    bool HnmAwaitingRepost,
    DateTime? HnmRepostAt,
    int? SourceTodId,
    // The HNM recurring-board settings (from the matching enabled HnmRecurringBoard), so
    // the edit form can repopulate them. RepeatOnTod = an enabled board exists;
    // RepeatLeadHours = its lead time in fractional hours. Null when no board.
    bool RepeatOnTod,
    double? RepeatLeadHours,
    int WindowCount,
    IReadOnlyList<ActivityAttendanceWindowDto> AttendanceWindows,
    string? CreatorCharacterName,
    string? StarterCharacterName,
    // The DKP pool this event earns into and pays its loot out of. Null when the linkshell has only
    // one pool — the client's cue to render the loot UI exactly as it did before pools existed.
    string? DkpPoolName = null);

public sealed record ActivityAttendanceWindowDto(
    int Id,
    int SequenceNumber,
    string? Label,
    DateTime PostedAt,
    IReadOnlyList<ActivityAttendanceWindowAttendeeDto> Attendees);

public sealed record ActivityAttendanceWindowAttendeeDto(
    int Id,
    string? CharacterName,
    string? JobName,
    string? SubJobName,
    string? Zone,
    DateTime VerifiedAt,
    string? VerifiedBy);

public sealed record ActivityParticipationDto(
    int Id,
    string? CharacterName,
    string? JobName,
    string? SubJobName,
    string? JobType,
    bool IsQuickJoin,
    bool? IsVerified,
    bool? IsOnBreak,
    IReadOnlyList<ActivityStatusLedgerDto> StatusLedger);

public sealed record ActivityEventParticipantDto(
    int Id,
    string? AppUserId,
    string? CharacterName,
    string? JobName,
    string? SubJobName,
    string? JobType,
    bool IsQuickJoin,
    bool? IsVerified,
    string? Proctor,
    DateTime? StartTime,
    DateTime? ResumeTime,
    DateTime? PauseTime,
    bool? IsOnBreak,
    bool WithdrewFromEvent,
    double? Duration,
    double? EventDkp,
    IReadOnlyList<ActivityStatusLedgerDto> StatusLedger,
    // Spendable DKP right now = LinkshellDkp − bid locks − pending live-event loot spend.
    // Shown next to each live participant so bidding power is clear during the event.
    double BiddableDkp = 0);

public sealed record ActivityEventAddMemberCandidateDto(
    string AppUserId,
    string CharacterName,
    string? Rank);

public sealed record ActivityStatusLedgerDto(
    int Id,
    string ActionType,
    DateTime OccurredAt,
    bool RequiresVerification,
    DateTime? VerifiedAt,
    string? VerifiedBy,
    DateTime? DeniedAt,
    string? DeniedBy,
    string? Source);

public sealed record ActivityHistoryDto(
    int Id,
    int LinkshellId,
    string? Name,
    string? Type,
    string? Location,
    DateTime? EndTime,
    double? Duration,
    int ParticipantCount);

public sealed record ActivityHistoryDetailDto(
    int Id,
    int LinkshellId,
    string? Name,
    string? Type,
    string? Location,
    DateTime? StartTime,
    DateTime? EndTime,
    double? Duration,
    int? DkpPerHour,
    string? Details,
    IReadOnlyList<ActivityHistoryParticipantDto> Participants);

public sealed record ActivityHistoryParticipantDto(
    int Id,
    string? AppUserId,
    string? CharacterName,
    string? JobName,
    string? SubJobName,
    string? JobType,
    double? Duration,
    double? EventDkp,
    bool? IsVerified);

public sealed record ActivityLinkshellRolePermissions(
    string? Name,
    bool CanManageRoles,
    bool CanManageMembers,
    bool CanManageEvents,
    bool CanModerateLiveEvent,
    bool CanAddLoot,
    bool CanManageInventory,
    bool CanManageTreasury,
    bool CanManageRules,
    bool CanManageAnnouncements,
    bool CanManageTods,
    bool CanAuditDkp,
    bool CanManageAuctions,
    bool CanLockAuctions,
    bool CanCustomizeLinkshell,
    bool CanManageParties,
    bool CanManageInvites,
    bool CanBid = true);

public sealed record ActivityLinkshellRoleDto(
    int Id,
    string Name,
    bool IsSystem,
    int SortOrder,
    bool CanManageRoles,
    bool CanManageMembers,
    bool CanManageEvents,
    bool CanModerateLiveEvent,
    bool CanAddLoot,
    bool CanManageInventory,
    bool CanManageTreasury,
    bool CanManageRules,
    bool CanManageAnnouncements,
    bool CanManageTods,
    bool CanAuditDkp,
    bool CanManageAuctions,
    bool CanLockAuctions,
    bool CanCustomizeLinkshell,
    bool CanManageParties,
    bool CanManageInvites,
    bool CanBid);

public sealed record ActivityLinkshellRolesResponse(
    int LinkshellId,
    IReadOnlyList<ActivityLinkshellRoleDto> Roles);

public sealed record ActivityDkpHistoryDto(
    int? LinkshellId,
    string? LinkshellName,
    string? SelectedAppUserId,
    string? SelectedMemberName,
    double CurrentBalance,
    IReadOnlyList<ActivityDkpHistoryMemberDto> Members,
    IReadOnlyList<ActivityDkpLedgerEntryDto> Entries,
    // Selected member's DKP spent on loot in still-live events, not yet committed. Shown as
    // a pending deduction; already removed from biddable power.
    double SelectedPendingLootSpend = 0);

public sealed record ActivityDkpHistoryMemberDto(
    string AppUserId,
    string CharacterName,
    double CurrentBalance,
    // DKP spent on loot in still-live events, not yet committed to the ledger. Shown as a
    // pending deduction; already removed from biddable power so it can't be double-spent.
    double PendingLootSpend = 0);

// ---- DKP pools (the Configurations tab's "DKP Pools" card) ----

public sealed record ActivityDkpPoolsDto(
    IReadOnlyList<ActivityDkpPoolDto> Pools,
    // Every event type assignable in this linkshell: the built-in vocabulary plus any custom
    // string their own events have actually used. EarnedTotal makes a remap legible ("Sea moves
    // 980 DKP") and drives the unmapped-earners warning.
    IReadOnlyList<ActivityDkpPoolEventTypeDto> AssignableEventTypes,
    IReadOnlyList<string> Accents);

public sealed record ActivityDkpPoolDto(
    int Id,
    string Name,
    bool IsDefault,
    int SortOrder,
    string Accent,
    IReadOnlyList<string> EventTypes);

public sealed record ActivityDkpPoolEventTypeDto(string Key, bool IsCustom, double EarnedTotal, bool InUse);

public sealed record ActivitySaveDkpPoolsRequest(IReadOnlyList<ActivityDkpPoolInput>? Pools);

// Id is null for a new pool. EventTypes is the COMPLETE set assigned to it — the save is a full
// replace, so anything left out falls back to the default pool.
public sealed record ActivityDkpPoolInput(
    int? Id,
    string? Name,
    bool IsDefault,
    string? Accent,
    IReadOnlyList<string>? EventTypes);

public sealed record ActivityDkpPoolPreviewDto(
    IReadOnlyList<ActivityDkpPoolMoveDto> Moves,
    int AffectedLedgerRows,
    int AffectedMembers,
    IReadOnlyList<string> Warnings);

public sealed record ActivityDkpPoolMoveDto(
    string EventType, string FromPool, string ToPool, double EarnedTotal, int LedgerRows);

public sealed record ActivityDkpAuditRequest(
    int LinkshellId,
    string TargetAppUserId,
    string Mode,
    int? RelatedLedgerEntryId,
    int? SourceWindowEventId,
    double Amount,
    string Reason,
    // Which DKP pool a "Misc" audit lands in. Ignored for Adjust/Add, which inherit the pool of the
    // entry they correct. Null falls back to the linkshell's default pool.
    int? DkpPoolId = null);

public sealed record ActivityDkpLedgerEntryDto(
    int Id,
    string EntryType,
    double Amount,
    double RunningBalance,
    DateTime OccurredAt,
    string? EventName,
    string? EventType,
    string? EventLocation,
    DateTime? EventStartTime,
    DateTime? EventEndTime,
    string? ItemName,
    string? Details,
    // Surfaces the officer's reason for "LootEditRefund" / "LootEditSpent"
    // entries so the DKP history view can render "Edited" annotation.
    string? EditReason);

public sealed record ActivityAuctionDto(
    int Id,
    int LinkshellId,
    string? Title,
    string? CreatedBy,
    DateTime? StartTime,
    DateTime? EndTime,
    DateTime? StartedAt,
    string Status,
    bool CanEdit,
    bool CanStart,
    // Live, creator-only — stops bidding now without archiving the run.
    // Distinct from CanClose so the UI can offer two separate actions:
    // "End auction" while it's live, "Close auction" once the timer is up.
    bool CanEnd,
    // Ended (timer expired), creator-only — runs the delivery confirmation,
    // archives to history, and removes any inventory-sourced items that the
    // creator marks as delivered.
    bool CanClose,
    IReadOnlyList<ActivityAuctionItemDto> Items,
    // The viewer's available DKP for THIS auction — the balance in the pool the auction draws
    // from, minus DKP locked by bids they're currently winning on it. Null when not computed
    // (single-auction action responses); the list endpoint always sets it.
    double? AvailableDkp = null,
    // True when leadership has frozen bidding for the linkshell (set on the list
    // endpoint). Drives the "Locked" badge + disabled bid inputs in the client.
    bool AuctionsLocked = false,
    // The DKP pool bids are drawn from. Name is null when the linkshell has only one pool, which
    // is the client's cue to hide the pool chip entirely.
    int? DkpPoolId = null,
    string? DkpPoolName = null);

public sealed record ActivityAuctionItemDto(
    int Id,
    string? ItemName,
    string? ItemType,
    int? StartingBidDkp,
    int? CurrentHighestBid,
    string? CurrentHighestBidder,
    string? CurrentHighestBidderAppUserId,
    DateTime? StartTime,
    DateTime? EndTime,
    string? Status,
    string? Notes,
    int BidCount,
    int? SourceItemId,
    // Set when this item is a gil sale (treasury gil sold for DKP).
    long? GilAmount);

public sealed record ActivityAuctionBidDto(
    int Id,
    string CharacterName,
    int BidAmount,
    DateTime CreatedAt);

public sealed record ActivityAuctionHistoryDto(
    int Id,
    int LinkshellId,
    string? Title,
    string? CreatedBy,
    DateTime? StartTime,
    DateTime? EndTime,
    DateTime? StartedAt,
    DateTime ClosedAt,
    IReadOnlyList<ActivityAuctionItemDto> Items);

public sealed record ActivityInviteDto(
    int Id,
    // Null for a Discord-roster invite whose target hasn't signed into LSM yet.
    string? AppUserId,
    int LinkshellId,
    string AppUserDisplayName,
    string LinkshellName,
    string Status);

// A member of a locked linkshell's Discord server, offered as an invite target
// in the "From your Discord server" roster. HasLsmAccount is true when this
// person already has an LSM account (a normal invite is sent); false means a
// Discord-keyed invite is sent and they auto-join on first sign-in.
public sealed record ActivityDiscordRosterCandidateDto(
    string DiscordUserId,
    string DisplayName,
    string AvatarUrl,
    bool HasLsmAccount);

public sealed record ActivityUserSearchResultDto(
    string Id,
    string DisplayName,
    string? UserName,
    string? PrimaryLinkshellName);

public sealed record ActivityLinkshellSearchResultDto(
    int Id,
    string Name,
    string? Details,
    int MemberCount,
    string? Status);

public sealed record ActivityParticipantInviteCandidateDto(
    string AppUserId,
    string DiscordUserId,
    string DisplayName,
    string? UserName,
    string? PrimaryLinkshellName);

public sealed record ActivityLootDto(
    int Id,
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent);

public sealed record ActivityTodDto(
    int Id,
    int LinkshellId,
    string MonsterName,
    int? DayNumber,
    DateTime? Time,
    // Tri-state: true=Claimed, false=Unclaimed, null=Not Specified.
    bool? Claim,
    string? Cooldown,
    DateTime? RepopTime,
    string? Interval,
    int LootCount,
    IReadOnlyList<ActivityTodLootDto> LootDetails,
    string? ImagePath,
    // Whether the kill was HQ (shown in the ToD list).
    bool Hq = false,
    // Extra seconds folded into RepopTime, so the Log ToD form round-trips on edit.
    int AdditionalSeconds = 0);

public sealed record ActivityTodLootDto(
    int Id,
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent);

public sealed record ActivityOverviewStatsDto(
    int LinkshellCount,
    int ActiveEventCount,
    int CompletedEventCount,
    int LiveEventCount);

public sealed record ActivityEventSignupRequest(
    int JobId,
    string? JobName = null,
    string? SubJobName = null,
    string? JobType = null,
    // Which character to sign up as (main or an alt name). Blank = main.
    string? CharacterName = null);

public sealed record ActivityQuickJoinRequest(
    [Required, StringLength(64, MinimumLength = 1)] string? JobName,
    [StringLength(64)] string? SubJobName,
    [StringLength(64)] string? JobType,
    [StringLength(64)] string? CharacterName = null);

public sealed record ActivityAddEventMemberRequest(
    [Required] string AppUserId,
    [Required, StringLength(64, MinimumLength = 1)] string? JobName,
    [StringLength(64)] string? SubJobName,
    [StringLength(64)] string? JobType);

public sealed record ActivityCreateEventRequest(
    int LinkshellId,
    string EventName,
    string? EventType,
    string? EventLocation,
    string? StartTimeLocal,
    string? EndTimeLocal,
    double? Duration,
    int? DkpPerHour,
    string? Details,
    // Optional FK to a PartySetup in the same linkshell. Null means "no party
    // setup attached" (event becomes ad-hoc signup only). The old inline Jobs
    // editor was removed in favour of this dropdown.
    int? PartySetupId,
    // When true, the event auto-starts at its StartTime (background service)
    // instead of waiting for a manual Start. Ignored for HNM events.
    bool AutoStart = false,
    // When true, attendees earn active-member credit (reconciled at close).
    // Default true, matching the web event form.
    bool CountsTowardActive = true,
    // HNM signup board only: the canonical monster the board is for, and whether
    // to re-post the board N hours before the next predicted pop when a new ToD
    // for that monster is recorded. Ignored for non-HNM events.
    string? MonsterName = null,
    bool RepeatOnTod = false,
    // Lead time before the pop to re-post, in fractional hours (the form enters it as
    // hours/minutes/seconds and combines them, e.g. 1.5 = 1h30m).
    double? RepeatLeadHours = null,
    // HNM signup board only: optional "Day N" label shown on the board.
    int? DayNumber = null);

public sealed record ActivityCreateLinkshellRequest(string Name, string? Details);

public sealed record ActivityUpdateLinkshellRequest(
    string Name,
    string? Details,
    string? LootStructure,
    bool? EnableHnmSection,
    bool? EnableMissions,
    bool? EnableAuctions,
    bool? EnableToDs,
    bool? EnableEndgame,
    bool? EnableEvents,
    bool? EnableDkp,
    bool? EnableItems,
    bool? EnableRevenue,
    string? DkpRoundingIncrement,
    // null = leave unchanged, [] = clear, [...names] = replace.
    IReadOnlyList<string>? HiddenTodMonsters,
    // null/blank = leave unchanged. SkySeaDynamis | HnmOnly | Both.
    string? LinkshellType,
    // null = leave unchanged, "" = unlock, digits = lock to that Discord server.
    string? DiscordGuildId,
    // Member activity tracking (null = leave unchanged). Thresholds are clamped to
    // a minimum of 1 server-side.
    bool? EnableActivityTracking = null,
    int? InactiveAfterAbsences = null,
    int? ActiveAfterAttendances = null,
    // null/blank = leave unchanged. One of the EventBoardThemes keys; an unknown
    // value is normalised to the default server-side.
    string? EventBoardTheme = null,
    // null = leave unchanged. Allow account-less Discord party-board signups (non-HNM).
    bool? OutsidePartySignupEnabled = null,
    // null = leave unchanged. "Fill earlier alliances first" signup nudge.
    bool? FillAlliancesInOrder = null,
    // null = leave unchanged. Gate HNM (event type + account-less HNM-board signups).
    bool? HnmOutsideSignupEnabled = null,
    // null = leave unchanged. Post event boards as Components V2 (wide media-gallery card).
    bool? UseComponentsV2Boards = null);

// Set/clear the post-event discussion mirror channel. ChannelId blank = clear
// (discussion stays in-app); a non-empty value must be a numeric Discord channel id.
public sealed record ActivitySetDiscussionChannelRequest(string? ChannelId);

// Associate a linkshell with a Discord server (does NOT lock access). GuildId is
// a server chosen from the eligible-guilds dropdown (the bot's servers the caller
// is also in) — verified server-side. When GuildId is omitted, the server falls
// back to the guild the Activity is launched in (X-Discord-Guild-Id header).
// GuildName is a display cache.
public sealed record ActivitySetGuildRequest(string? GuildId, string? GuildName);

// Toggle the optional access lock on a linkshell that already has a server set.
// When Locked is true, the Activity can only open the linkshell from its server.
public sealed record ActivitySetGuildLockRequest(bool Locked);

// One Discord server the caller can lock a linkshell to (the bot is in it and
// so is the caller). Mirrors the web Customize page's eligible-guild dropdown.
public sealed record ActivityGuildOptionDto(string Id, string Name);

public sealed record ActivitySendInviteRequest(string AppUserId);

public sealed record ActivityDiscordInviteRequest(string DiscordUserId);

public sealed record ActivityParticipantInviteCandidatesRequest(int LinkshellId, IReadOnlyList<string> DiscordUserIds);

public sealed record ActivityStartEventRequest(IReadOnlyList<int>? AbsentParticipantIds);

public sealed record ActivityVerifyParticipantRequest(int ParticipantId, bool IsVerified);

public sealed record ActivityResetParticipantRequest(int ParticipantId);

public sealed record ActivityVerifyReturnRequest(int LedgerEntryId);

public sealed record ActivityForceBreakRequest(int ParticipantId);

public sealed record ActivityForceResumeRequest(int ParticipantId);

public sealed record ActivityAddLootRequest(string ItemName, string? ItemWinner, int? WinningDkpSpent);

public sealed record ActivityCreateTodRequest(
    int LinkshellId,
    string? MonsterName,
    int? DayNumber,
    // Tri-state: true=Claimed, false=Unclaimed, null=Not Specified.
    bool? Claim,
    string? TimeLocal,
    string? Cooldown,
    string? Interval,
    bool NoLoot,
    IReadOnlyList<ActivityCreateTodLootRequest> LootDetails,
    string? ImagePath,
    bool Hq = false,
    int AdditionalSeconds = 0);

public sealed record ActivityUpdateTodRequest(
    string? MonsterName,
    int? DayNumber,
    // Tri-state: true=Claimed, false=Unclaimed, null=Not Specified.
    bool? Claim,
    string? TimeLocal,
    string? Cooldown,
    string? Interval,
    bool NoLoot,
    IReadOnlyList<ActivityCreateTodLootRequest> LootDetails,
    string? ImagePath,
    bool Hq = false,
    int AdditionalSeconds = 0);

public sealed record ActivityCreateTodLootRequest(
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent);

// Logs (or edits) a ToD from an HNM signup board's "Post ToD" / "Edit ToD" button. The
// monster + linkshell come from the event (path id), so the board form only sends the
// time + cooldown/interval/day/claim. No loot/screenshot — those are for the ToDs tab.
public sealed record ActivityPostBoardTodRequest(
    string? TimeLocal,
    string? Cooldown,
    string? Interval,
    int? DayNumber,
    // Tri-state: true=Claimed, false=Unclaimed, null=Not Specified.
    bool? Claim,
    bool Hq = false,
    int AdditionalSeconds = 0);

public sealed record ActivityUpdateMemberRoleRequest(string Role);

// Manual Active/Pending/Inactive status set from the Activity roster. Auto
// activity-tracking (if the linkshell enables it) may recompute this later.
public sealed record ActivityUpdateMemberStatusRequest(string Status);

// Officer override of a member's active-credit "Count" on the roster. Stored as a
// manual streak override that drives Active/Inactive until the next recompute.
public sealed record ActivitySetActiveCreditCountRequest(int Count, string? StreakType = "credit");

// Edit a closed event (EventHistory). Changing DkpPerHour rescales every
// attendee's earned DKP (balance + lifetime), via EventHistoryEditService.
public sealed record ActivityEditEventHistoryRequest(
    string? EventName,
    string? EventType,
    string? EventLocation,
    string? Details,
    double? Duration,
    int? DkpPerHour);

public sealed record ActivitySetParticipantDkpRequest(double Amount);

// Add a member to a closed event after the fact + grant DKP (wired into the ledger/balance).
public sealed record ActivityAddEventHistoryParticipantRequest(
    string? AppUserId, double Dkp, string? JobType, string? JobName, string? SubJobName);

public sealed record ActivitySetActiveCreditRequest(bool Credited);

public sealed record ActivityUpdateProfileRequest(
    string CharacterName,
    string? TimeZone,
    string? AltCharacterName1 = null,
    string? AltCharacterName2 = null,
    // Per-job levels for the 15 classic jobs in EventJobCatalog.MainJobOptions
    // order (index 0 = WAR ... 14 = SMN). Persisted to the user's memberships.
    int[]? JobLevels = null,
    // Catalog-aligned job levels for the two alt characters; persisted on the
    // AppUser (not per-membership).
    int[]? Alt1JobLevels = null,
    int[]? Alt2JobLevels = null,
    // Catalog-aligned "strong" flags parallel to the level arrays above; null
    // leaves the existing flags unchanged.
    bool[]? StrongJobs = null,
    bool[]? Alt1StrongJobs = null,
    bool[]? Alt2StrongJobs = null,
    // Per-craft levels (main + alts) in CraftCatalog order (Alchemy … Fishing),
    // stored account-level on the AppUser. Null leaves the existing values.
    int[]? CraftLevels = null,
    int[]? Alt1CraftLevels = null,
    int[]? Alt2CraftLevels = null,
    // Per-job free-text merit notes (main + alts), catalog-aligned (WAR … SMN).
    // Null leaves the existing notes unchanged.
    string[]? MeritJobs = null,
    string[]? Alt1MeritJobs = null,
    string[]? Alt2MeritJobs = null);

// One user-defined channel route: a channel the bot posts the ticked post types
// to. Id is null for a new route. EventTypeFilter (only meaningful when
// PostEvents) is the list of event types this route handles; empty = catch-all.
public sealed record ActivityChannelRouteInput(
    int? Id,
    string? Name,
    string? ChannelId,
    bool PostEvents,
    bool PostLoot,
    bool PostAuctions,
    bool PostAttendance,
    bool PostTodBoard,
    bool PostDkpSheet,
    IReadOnlyList<string>? EventTypeFilter,
    // Per-monster narrowing for an HNM route (only used when EventTypeFilter includes HNM).
    IReadOnlyList<string>? HnmMonsterFilter = null);

public sealed record ActivitySaveChannelRoutesRequest(IReadOnlyList<ActivityChannelRouteInput>? Routes);

public sealed record ActivityAuctionItemInput(
    int Id,
    string? ItemName,
    string? ItemType,
    int? StartingBidDkp,
    string? Notes,
    int? SourceItemId,
    // When > 0 this item is a gil sale: gil sold for DKP, paid from treasury.
    long? GilAmount);

public sealed record ActivityCreateAuctionRequest(
    int LinkshellId,
    string Title,
    string? StartTimeLocal,
    string? EndTimeLocal,
    IReadOnlyList<ActivityAuctionItemInput> Items,
    // Which DKP pool bids are drawn from. An auction has no event type, so unlike event loot this
    // can't be derived — the officer picks it. Null = the linkshell's default pool.
    int? DkpPoolId = null);

public sealed record ActivityAuctionBidRequest(int BidAmount);

public sealed record ActivityAuctionsLockRequest(bool Locked);

public sealed record ActivityCloseAuctionRequest(IReadOnlyList<int>? DeliveredItemIds);

// One row in the Loot History view. Unifies TodLootDetail + EventLootDetail
// behind a `Source` discriminator so the client can render a single table.
// ParentId is the Tod.Id (when Source == "Tod") or the Event.Id /
// EventHistory.Id (when Source == "Event").
public sealed record ActivityLootHistoryItemDto(
    int LootDetailId,
    string Source,
    int ParentId,
    string? Context,
    DateTime? OccurredAt,
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent,
    double? ActualDeductedDkp,
    bool IsEdited,
    string? LastEditReason,
    DateTime? EditedAt,
    string? EditedByCharacterName,
    bool CanEdit);

public sealed record ActivityLootHistoryListDto(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<ActivityLootHistoryItemDto> Items);

public sealed record ActivityLootEditRequest(
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent,
    string? Reason);

public sealed record ActivityLootAddRequest(
    string? Context,
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent);
