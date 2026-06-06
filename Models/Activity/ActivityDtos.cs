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
    bool AddonConfigured);

public sealed record ActivityAppUserDto(
    string Id,
    string UserName,
    string? CharacterName,
    string? AltCharacterName1,
    string? AltCharacterName2,
    string? TimeZone,
    int? PrimaryLinkshellId,
    string? PrimaryLinkshellName);

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
    ActivityLinkshellSettingsDto Settings);

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
    // Discord server (guild) ID this linkshell is locked to, or null when
    // unlocked. When set, only members of this server can access the linkshell.
    string? DiscordGuildId);

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
    bool CanCustomizeLinkshell,
    bool CanManageParties);

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
    DateTime UpdatedAt);

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

public sealed record ActivityRuleDto(
    int Id,
    int LinkshellId,
    string Title,
    string Details,
    string? CreatedByAppUserId,
    string? CreatedByCharacterName,
    DateTime CreatedAt);

public sealed record ActivityAnnouncementDto(
    int Id,
    int LinkshellId,
    string Title,
    string Details,
    string? CreatedByAppUserId,
    string? CreatedByCharacterName,
    DateTime CreatedAt);

public sealed record ActivityCreateRuleRequest(string Title, string Details);

public sealed record ActivityCreateAnnouncementRequest(string Title, string Details);

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
    DateTime? DateJoined);

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
    int WindowCount,
    IReadOnlyList<ActivityAttendanceWindowDto> AttendanceWindows,
    string? CreatorCharacterName,
    string? StarterCharacterName);

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
    double? Duration,
    double? EventDkp,
    IReadOnlyList<ActivityStatusLedgerDto> StatusLedger);

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
    bool CanCustomizeLinkshell,
    bool CanManageParties);

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
    bool CanCustomizeLinkshell,
    bool CanManageParties);

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
    IReadOnlyList<ActivityDkpLedgerEntryDto> Entries);

public sealed record ActivityDkpHistoryMemberDto(
    string AppUserId,
    string CharacterName,
    double CurrentBalance);

public sealed record ActivityDkpAuditRequest(
    int LinkshellId,
    string TargetAppUserId,
    string Mode,
    int? RelatedLedgerEntryId,
    int? SourceWindowEventId,
    double Amount,
    string Reason);

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
    // The viewer's available DKP in this auction's linkshell (total minus
    // DKP locked by bids they're currently winning). Null when not computed
    // (single-auction action responses); the list endpoint always sets it.
    double? AvailableDkp = null);

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
    int? SourceItemId);

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
    string? ImagePath);

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
    string? JobType = null);

public sealed record ActivityQuickJoinRequest(
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
    int? PartySetupId);

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
    string? DiscordGuildId);

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
    string? ImagePath);

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
    string? ImagePath);

public sealed record ActivityCreateTodLootRequest(
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent);

public sealed record ActivityUpdateMemberRoleRequest(string Role);

public sealed record ActivityUpdateProfileRequest(
    string CharacterName,
    string? TimeZone,
    string? AltCharacterName1 = null,
    string? AltCharacterName2 = null);

public sealed record ActivityAuctionItemInput(
    int Id,
    string? ItemName,
    string? ItemType,
    int? StartingBidDkp,
    string? Notes,
    int? SourceItemId);

public sealed record ActivityCreateAuctionRequest(
    int LinkshellId,
    string Title,
    string? StartTimeLocal,
    string? EndTimeLocal,
    IReadOnlyList<ActivityAuctionItemInput> Items);

public sealed record ActivityAuctionBidRequest(int BidAmount);

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
