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
    ActivityOverviewStatsDto Stats);

public sealed record ActivityAppUserDto(
    string Id,
    string UserName,
    string? CharacterName,
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
    string DkpRoundingIncrement);

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
    bool CanCustomizeLinkshell);

public sealed record ActivityPrimaryLinkshellDto(
    int Id,
    string Name,
    int MemberCount,
    string? Details,
    IReadOnlyList<ActivityMemberDto> Members,
    IReadOnlyList<ActivityRuleDto> Rules,
    IReadOnlyList<ActivityAnnouncementDto> Announcements,
    IReadOnlyList<ActivityItemDto> Items,
    IReadOnlyList<ActivityRevenueEntryDto> RevenueEntries);

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
    string? Rank,
    string? Status,
    double? LinkshellDkp);

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
    int RequestedSlots,
    ActivityParticipationDto? CurrentParticipation,
    IReadOnlyList<ActivityEventParticipantDto> Participants,
    IReadOnlyList<ActivityLootDto> Loot,
    IReadOnlyList<ActivityJobDto> Jobs,
    int WindowCount,
    IReadOnlyList<ActivityAttendanceWindowDto> AttendanceWindows);

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

public sealed record ActivityJobDto(
    int Id,
    string? JobName,
    string? SubJobName,
    string? JobType,
    int? Quantity,
    int? SignedUp,
    IReadOnlyList<string> Enlisted);

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
    bool CanCustomizeLinkshell);

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
    bool CanCustomizeLinkshell);

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
    string? Details);

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
    bool CanClose,
    IReadOnlyList<ActivityAuctionItemDto> Items);

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
    string AppUserId,
    int LinkshellId,
    string AppUserDisplayName,
    string LinkshellName,
    string Status);

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
    bool Claim,
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
    string? JobName,
    string? SubJobName,
    string? JobType);

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
    IReadOnlyList<ActivityCreateJobRequest> Jobs);

public sealed record ActivityCreateJobRequest(
    string? JobName,
    string? SubJobName,
    string? JobType,
    int? Quantity,
    string? Details);

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
    string? DkpRoundingIncrement);

public sealed record ActivitySendInviteRequest(string AppUserId);

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
    bool Claim,
    string? TimeLocal,
    string? Cooldown,
    string? Interval,
    bool NoLoot,
    IReadOnlyList<ActivityCreateTodLootRequest> LootDetails,
    string? ImagePath);

public sealed record ActivityUpdateTodRequest(
    string? MonsterName,
    int? DayNumber,
    bool Claim,
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

public sealed record ActivityUpdateProfileRequest(string CharacterName, string? TimeZone);

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
