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
    // The dashboard's HNM Claims donut, aggregated server-side over ALL of the linkshell's
    // claimed ToDs. RecentTods is a 25-row tail of every monster, so the client cannot count
    // this itself — it charted only the claims that happened to survive in that tail.
    ActivityHnmClaimsDto HnmClaims,
    // The same card's other tab: which window of its band each HNM pops on, off Tod.PopWindow.
    ActivityHnmWindowsDto HnmWindows,
    ActivityOverviewStatsDto Stats,
    // True when the user has at least one non-revoked AddonApiToken.
    // Drives the onboarding "Set up the addon" checklist item.
    bool AddonConfigured,
    // True when a super admin has globally disabled the addon. Hides the
    // Game Addon pairing card in the Configurations tab.
    bool AddonGloballyDisabled,
    // (HnmWindowSetups lived here: a global, read-only monster → windows × cadence list. Window
    // setups are per-linkshell now and ride on each linkshell's settings.monsterSetups, so a
    // second global copy would only be something for them to disagree with.)
    // True when the app-wide admin override is switched ON and this account carries it
    // (AppUser.IsSuperAdmin). Grants every permission in every linkshell the user is a
    // MEMBER of — `linkshells[].permissions` already arrives all-true, so this exists
    // for the coarse rank-string checks and the ADMIN badge. See AdminOverrideService.
    bool AdminOverrideActive = false,
    // True when a super admin has globally switched Claim Shield off. The per-monster switches in
    // Monster setups are inert while it is, so the editor has to grey them out and say why —
    // otherwise every checkbox claims a state the addon is ignoring.
    bool ClaimShieldGloballyDisabled = false);

public sealed record ActivityAppUserDto(
    string Id,
    string UserName,
    string? CharacterName,
    string? AltCharacterName1,
    string? AltCharacterName2,
    string? TimeZone,
    int? PrimaryLinkshellId,
    string? PrimaryLinkshellName,
    // Per-job levels for the selectable jobs in EventJobCatalog.MainJobOptions
    // order (index 0 = WAR … 14 = SMN, 15 = BLU … 17 = PUP). Pre-fills the profile "My Jobs" editor.
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
    // Per-job free-text merit notes (main + alts), catalog-aligned (WAR … PUP).
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

// LEGACY. The per-linkshell ToD cooldown blob (Linkshell.TodMonsterTimings) this described was
// replaced by the LinkshellMonsterTimings table. Nothing writes it any more; it survives only so
// LinkshellMonsterTimingProvisioner can import a linkshell's old values on first seed, and goes
// with the column in a later deploy.
public sealed record ActivityTodMonsterTimingDto(
    string MonsterName,
    double CooldownHours,
    int IntervalHours,
    int IntervalMinutes,
    string? Category = null);

// One monster's setup in the Monster Setups editor.
//
// Durations travel as the value + unit the officer typed ("22" + "hours") rather than as raw
// minutes, so the field echoes back the way they entered it. MonsterTimingEditor normalizes to
// minutes on the way in; the Default* fields are the built-in values, which the row shows as
// placeholders and the Reset link restores.
public sealed record ActivityMonsterTimingDto(
    int Id,
    string MonsterName,
    int? Windows,
    int? CadenceValue,
    string? CadenceUnit,
    int CooldownValue,
    string CooldownUnit,
    string Category,
    bool IsCustom,
    int? DefaultWindows,
    int? DefaultCadenceMinutes,
    int DefaultCooldownMinutes,
    // Whether the addon captures claim-shield lotteries for this monster. Defaults on; off is for
    // monsters the linkshell doesn't contest, whose rolls would otherwise be noise.
    bool ClaimShieldEnabled);

// The COMPACT form carried on the polled overview: only what the ToD form and the create-event
// picker need (the option list, plus the values they auto-fill from). The editor's own GET returns
// the fuller ActivityMonsterTimingDto with ids, defaults and the custom flag.
//
// Always populated, even for a linkshell that has never opened the editor — the server projects
// the built-in defaults in that case, so the client needs no catalog constants of its own.
public sealed record ActivityMonsterSetupDto(
    string MonsterName,
    int? Windows,
    int? CadenceMinutes,
    int CooldownMinutes,
    string Category,
    // This monster's standing Repeat-on-ToD lead, in fractional hours, or null when it has no
    // ENABLED HnmRecurringBoard. It rides on the monster catalog rather than on a list of its own
    // because that is the shape the consumer wants: the create-event form asks "will this camp
    // repeat, and how long before the pop?" the moment a monster is picked, and this is the answer
    // for the one that was picked.
    //
    // Null therefore means BOTH "recurrence is off" and "no lead set", which are the same state:
    // a DISABLED board's stored lead is stale bookkeeping, and surfacing it would re-apply it the
    // moment the toggle was flipped back on. Appended last (with a default) so the overview's
    // other constructions of this record need no edit.
    double? RepeatLeadHours = null);

public sealed record ActivityMonsterTimingsDto(
    IReadOnlyList<ActivityMonsterTimingDto> Rows,
    IReadOnlyList<string> Categories,
    int MaxWindows);

public sealed record ActivityMonsterTimingInput(
    int? Id,
    string? MonsterName,
    int? Windows,
    double? CadenceValue,
    string? CadenceUnit,
    double? CooldownValue,
    string? CooldownUnit,
    string? Category,
    // Nullable so a client that predates the column leaves the stored value alone rather than
    // silently switching every monster off on its next save.
    bool? ClaimShieldEnabled);

public sealed record ActivitySaveMonsterTimingsRequest(
    IReadOnlyList<ActivityMonsterTimingInput>? Rows);

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
    // Allow Discord members with no LSM account to sign up (or Check In) from a board, for
    // EVERY event type including HNM. Backed by a placeholder member, so they DO earn DKP
    // + are tracked.
    bool OutsidePartySignupEnabled,
    // Experimental: post event boards as Components V2 (wide media-gallery card) instead
    // of the classic image-in-embed. Only affects boards posted after it's turned on.
    bool UseComponentsV2Boards,
    // Discord channel id new post-event discussion comments are mirrored to, or
    // null to keep discussion in-app only.
    string? DiscussionChannelId = null,
    // Manual Check In HNM attendance: mode (Standard | Wd) + scoring. DkpPerHour is an int and can't
    // hold 0.25, so the per-window rate is a double here. Bonuses are added once per crediting
    // attendee when the camp is marked claimed/killed. These set what a camp PROPOSES — End Camp
    // stages a review row in the Attendance System and an officer's Post is what pays, which is
    // why there is no longer an "Awaiting Processing" grace to configure. Window counts and
    // auto-advance cadence are built in per monster (HnmConfig), not configurable here.
    string HnmAttendanceMode = "Standard",
    double WdDkpPerWindow = 0.25,
    double WdClaimBonus = 0,
    double WdKillBonus = 0,
    // Standard-mode HNM bonuses (only used when HnmAttendanceMode == Standard): extra DKP for
    // being on the roster at the camp's open / close, plus claim / kill outcome bonuses.
    double HnmStandardOpenBonus = 0,
    double HnmStandardCloseBonus = 0,
    double HnmStandardClaimBonus = 0,
    double HnmStandardKillBonus = 0,
    // Every monster this linkshell can log a ToD for, with its configured windows / cadence /
    // cooldown. Replaced TodMonsterTimings, which carried only the cooldown half and only for
    // monsters the linkshell had explicitly overridden.
    IReadOnlyList<ActivityMonsterSetupDto>? MonsterSetups = null,
    // Automatic per-window attendance snapshots (both modes). When enabled, an officer running
    // the LSM addon can ARM a live camp and the addon posts their ALLIANCE as that window's
    // snapshot ~DelaySeconds after each window opens. Appended LAST deliberately: the
    // `linkshell is null` early-return in HelpersMappers passes only the leading positional
    // arguments and relies on defaults for the rest, so appending here needs no edit there.
    bool HnmAutoSnapshotEnabled = false,
    int HnmAutoSnapshotDelaySeconds = 20,
    // What a REGULAR (in-between) window pays per attendee on a Standard camp — the base rate the
    // open / close bonuses ride on top of. 0 keeps the old open/close-only payout.
    double HnmStandardWindowBonus = 0,
    // Manual Check In open / close bonuses, gated on the member's own check-in range: open =
    // checked in from window 1, close = still checked in at the camp's last credited window.
    //
    // These three are appended LAST for the same reason the auto-snapshot pair above was — the
    // `linkshell is null` early-return in HelpersMappers passes only the leading positional
    // arguments and relies on defaults for the rest, so appending here needs no edit there.
    double WdOpenBonus = 0,
    double WdCloseBonus = 0);

// LOCKSTEP: a permission added here must ALSO be added to the two records below, to the three
// matching interfaces in discord-activity/src/app/discord/discord-activity.types.ts, to BOTH the
// label list and the save mapping in configurations-tab.component.ts, and to the plain-JS
// `permissions` array in Views/Linkshell/Permissions.cshtml. Only the C# and TS sides fail the
// build when you miss one — the Razor list is unchecked, and omitting it there makes the web role
// editor silently POST the flag as false on every save.
public sealed record ActivityPermissionsDto(
    bool CanManageRoles,
    bool CanManageMembers,
    bool CanManageEvents,
    bool CanModerateLiveEvent,
    bool CanAddLoot,
    bool CanManageInventory,
    bool CanManageCharts,
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

public static class ActivityPermissions
{
    // Every permission granted. Used by the app-wide admin override so the SPA
    // unlocks the same surfaces the server already allows. Add a `true` here when
    // you add a permission above — the compiler enforces it.
    public static readonly ActivityPermissionsDto All = new(
        CanManageRoles: true,
        CanManageMembers: true,
        CanManageEvents: true,
        CanModerateLiveEvent: true,
        CanAddLoot: true,
        CanManageInventory: true,
        CanManageCharts: true,
        CanManageTreasury: true,
        CanManageRules: true,
        CanManageAnnouncements: true,
        CanManageTods: true,
        CanAuditDkp: true,
        CanManageAuctions: true,
        CanLockAuctions: true,
        CanCustomizeLinkshell: true,
        CanManageParties: true,
        CanManageInvites: true,
        CanBid: true);
}

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

// SUPERSEDED by ActivityRecordTreasuryEntryRequest below. The three revenue routes stay for one
// release as delegating shims so a cached Angular bundle against a new server keeps working.
public sealed record ActivityCreateRevenueRequest(string EntryType, string? Category, long Value, string? Details, DateTime? OccurredAt);

// --- Treasury: the two halves of a transaction, its categories, and what can happen to it. ---
// Every user-visible string here comes from TreasuryLabels or TreasuryTransactionKinds, so the web and
// Discord cannot drift, and none of it is bookkeeping jargon.

// One half of a transaction. Only ever rendered inside the collapsed "show the bookkeeping details"
// panel — the list itself shows one plain-English line per entry.
public sealed record ActivityTreasuryLineDto(
    // Unique within the entry, unlike the account number: a split payout puts one line per member on
    // the same category. This is what the front-end keys the list on.
    int LineNumber,
    int AccountNumber,
    string AccountName,
    string ClassLabel,
    long PresentedAmount,
    string? CounterpartyCharacterName);

// One member's share of a split. MembershipId is null when they are no longer in the linkshell — the
// name is still on the entry, but there is nothing left to re-pick when fixing it.
public sealed record ActivityTreasuryRecipientDto(
    int? MembershipId,
    string? AppUserId,
    string CharacterName,
    long Share);

// Someone who can be given a share. Only sent to officers who can record, since nobody else can use it.
public sealed record ActivityTreasuryMemberDto(
    int MembershipId,
    string? AppUserId,
    string CharacterName,
    string? Rank);

public sealed record ActivityTreasuryEntryDto(
    int Id,
    int LinkshellId,
    string EntryNumber,
    string Status,
    string StatusLabel,
    string Kind,
    string Source,
    string? TransactionKind,
    // The plain-English sentence the officer picked, e.g. "Sold an item".
    string WhatHappened,
    // The magnitude, for display.
    long Amount,
    // The signed gil-on-hand movement: negative means gil left. Zero for an entry that only records
    // something owed either way.
    long CashDelta,
    DateTime TransactionDate,
    string? Memo,
    string? CounterpartyCharacterName,
    string? EnteredByCharacterName,
    DateTime? RecordedAt,
    int? ReversesEntryId,
    string? ReversesEntryNumber,
    // Whether something later cancels this one out. Read as an EXISTS rather than stored, because
    // storing it would mean updating a confirmed entry.
    bool IsReversed,
    // Cancelled by a FIX rather than an outright reversal: something recorded the right numbers in
    // its place. Both are true of a corrected entry, so the row shows the more specific word.
    bool IsFixed,
    string? CorrectionReason,
    // Everyone who got a share, when this was split. Empty for an ordinary entry, and one name for an
    // entry that names a single member — CounterpartyCharacterName above stays the quick read.
    IReadOnlyList<ActivityTreasuryRecipientDto> Recipients,
    IReadOnlyList<ActivityTreasuryLineDto> Lines,
    // Whose mule this entry's gil landed on or came off. Null for an entry that moved no gil, and
    // for everything recorded before the question was asked. Shown on the row so an officer can
    // answer "where did that 8M go" without opening the bookkeeping details.
    string? HolderCharacterName = null);

public sealed record ActivityTreasuryCategoryDto(
    int Id,
    int AccountNumber,
    string Name,
    string? Description,
    string ClassLabel,
    bool IsCash,
    bool IsPostable,
    bool IsActive,
    int SortOrder);

// One of the four things that can happen to gil. The picker asks for this first.
public sealed record ActivityTreasuryActionDto(string Key, string Label);

// One reason under an action.
//
// EVERY kind is sent, not just the pickable ones. The client resolves the selected kind out of this
// list, and everything about the form hangs off that — whether to show the split picker, whether to
// name a member, the preview sentence, and whether Submit does anything at all. Send only the
// pickable ones and a Fix on an app-recorded entry does not merely lose its option: the whole form
// silently degrades and the button stops responding.
public sealed record ActivityTreasuryKindDto(
    string Key,
    // What the transactions list calls it.
    string Label,
    // What the picker calls it, under its action. Short, and only unique within that action.
    string ReasonLabel,
    string Help,
    string Action,
    bool ShowsMember,
    // A member is REQUIRED, not merely offered: the entry creates or settles gil owed to one, and
    // an obligation with nobody attached can never appear on the who-we-owe list. The server
    // refuses it, so the form disables its own submit rather than letting that come back as a
    // failed save with nothing on screen pointing at the empty box.
    bool RequiresMember,
    // Whether this option shares one amount between several members instead of naming one.
    bool IsSplittable,
    // Whether picking a member should fill in what they are still owed, rather than asking.
    bool SettlesMemberDebt,
    // Offered in the picker. False for the ones the app records for you, and for retired ones.
    bool IsPickable,
    // Superseded — reachable only from Fix, and refused on any other write.
    bool IsRetired,
    string PreviewTemplate,
    // What the single name box is CALLED for this option. "Member" for most; the owed-to-us pair
    // asks for a typed name instead, because whoever owes a linkshell gil is usually not in it.
    string CounterpartyLabel,
    // Whether this option needs a mule named — true exactly when it moves gil on hand. Sent rather
    // than re-derived client-side, for the same reason RequiresMember is: the account pair that
    // decides it never crosses the wire.
    bool RequiresHolder,
    // And what that box is called, which flips with the direction: naming who ends up with the gil
    // is a different question from naming whose stack it came out of.
    string HolderLabel,
    // Which way the gil is going. Sent because the two labels above do not compose into a sentence
    // the same way — "Say who's holding this gil" reads, "Say whose gil is this coming out of" does
    // not — so the front-end picks its own wording off the direction rather than off the label.
    bool BringsCashIn);

// One member the linkshell still owes. Projected from the same lines as the snapshot, so these
// always add up to its WeOwe figure.
// One person and the slice of the linkshell's gil sitting on their mule. Projected from the same
// lines as the snapshot, so these always add up to its CashOnHand figure — including the
// nobody-named bucket, which is why CharacterName is nullable here and the front-ends label it
// rather than dropping it.
public sealed record ActivityTreasuryGilHolderDto(string? CharacterName, long Amount);

public sealed record ActivityTreasuryMemberObligationDto(
    string CharacterName,
    long Amount,
    // Whether this row can be ticked and paid off. False for the "no member named" bucket — a
    // payment has to name who it went to — and for a row overpaid into a negative, where paying
    // "in full" has nothing to mean.
    bool CanSettle);

// One member an officer ticked, and the figure the screen was showing beside them. The server pays
// what the books say and refuses the row if the two disagree, so a page left open while someone else
// records more gil owed cannot hand over the newer figure.
public sealed record ActivityTreasurySettlePickDto(string CharacterName, long ExpectedAmount);

// HolderCharacterName is whose mule the gil leaves from, or arrives on. Required for the same reason
// it is on the record form: ticking a name here writes a real gil movement, and one that names no
// mule lands in the nobody-named bucket the who's-holding-it list exists to keep empty.
//
// ONE holder for the whole batch, not one per tick: a payout run is somebody sitting at a mule
// handing gil out, and asking again for every name on the list would be asking the same question
// eight times.
public sealed record ActivitySettleOwedRequest(
    IReadOnlyList<ActivityTreasurySettlePickDto>? Picks,
    string? HolderCharacterName = null,
    string? HolderAppUserId = null);

// What a payout run did. The message is built server-side so the website and the Activity report
// the same outcome in the same words.
public sealed record ActivitySettleOwedResultDto(
    bool Success,
    string Message,
    long TotalPaid,
    IReadOnlyList<string> Settled,
    IReadOnlyList<string> Skipped);

public sealed record ActivityTreasurySnapshotDto(
    long CashOnHand,
    long OwedToUs,
    long WeOwe,
    long MoneyIn,
    long MoneyOut,
    long NetChange,
    long NetWorth,
    long StartingBalance,
    // Whether what we hold minus what we owe still matches what we started with plus the net movement.
    bool Balances,
    DateTime? LockedThroughUtc,
    // Disclosed as data, not baked into either front-end, so both render the same sentence.
    string BasisNote,
    // Who the WeOwe figure above is owed to. Sent to every reader, not just officers — the treasury
    // is member-visible and these names already appear in the transactions list.
    IReadOnlyList<ActivityTreasuryMemberObligationDto> OwedToMembers,
    // And who owes the LINKSHELL, behind the OwedToUs figure. The mirror list, ticked the same way:
    // both halves of the sheet are settled by ticking a name rather than typing one back in.
    IReadOnlyList<ActivityTreasuryMemberObligationDto> OwedToUsBy,
    // Whose mules the CashOnHand figure is spread across. The third figure on the sheet to get names
    // behind it, and unlike the other two it is not settled by ticking — gil moves off a mule by
    // being spent, so this list is read, not acted on.
    IReadOnlyList<ActivityTreasuryGilHolderDto> GilHolders);

public sealed record ActivityTreasuryPageDto(
    ActivityTreasurySnapshotDto Summary,
    IReadOnlyList<ActivityTreasuryEntryDto> Entries,
    int TotalEntries,
    int Page,
    int PageSize,
    IReadOnlyList<ActivityTreasuryCategoryDto> Categories,
    IReadOnlyList<ActivityTreasuryKindDto> Kinds,
    // The picker's top level, in display order. Server-supplied so the action names live in
    // TreasuryLabels with every other word this feature shows — the group headings they replace
    // were hardcoded separately in three front-end files and could drift.
    IReadOnlyList<ActivityTreasuryActionDto> Actions,
    // Who a split can be shared with. Empty unless CanManage — a reader has nothing to pick.
    IReadOnlyList<ActivityTreasuryMemberDto> Members,
    bool CanManage);

public sealed record ActivityRecordTreasuryEntryRequest(
    string TransactionKind,
    long Amount,
    DateTime? TransactionDate,
    string? Memo,
    string? CounterpartyAppUserId,
    string? CounterpartyCharacterName,
    // False saves a draft the officer can still edit; true puts it straight on the books.
    bool Confirm,
    // Membership rows, not names: a name is not unique, not stable, and not something the server can
    // check against a roster. The server resolves each one and records the name it finds.
    IReadOnlyList<int>? RecipientMembershipIds = null,
    // Whose mule the gil lands on or comes off. A NAME rather than a membership row, unlike the
    // split recipients above: gil regularly sits on a mule that is not on the roster, and refusing
    // to record that would push it into the nobody-named bucket — the one thing this field exists to
    // empty. Optional on the wire, required by the kind — see TreasuryTransactionKind.RequiresHolder.
    string? HolderAppUserId = null,
    string? HolderCharacterName = null);

public sealed record ActivityFixTreasuryEntryRequest(
    string TransactionKind,
    long Amount,
    DateTime? TransactionDate,
    string? Memo,
    string? CounterpartyAppUserId,
    string? CounterpartyCharacterName,
    string Reason,
    IReadOnlyList<int>? RecipientMembershipIds = null,
    string? HolderAppUserId = null,
    string? HolderCharacterName = null);

public sealed record ActivityReverseTreasuryEntryRequest(string Reason);

// The gil actually sitting on the mule, counted by hand. The app works out which way the difference
// goes; the officer never picks.

public sealed record ActivityLockTreasuryRequest(DateTime? LockedThrough, string? Reason);

// SoldByCharacterName is who actually sold the thing, which is NOT the same as who is clicking the
// button — an officer records sales somebody else made all the time, and the old code recorded the
// clicker. It is also the answer to where the gil went, so it becomes the holder on the treasury
// entry: whoever sold it is holding the gil until they hand it on.
public sealed record ActivityMarkItemSoldRequest(
    long SalePrice, string? SoldByCharacterName = null, string? SoldByAppUserId = null);

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
    bool HasSyncedActivity = false,
    // True when this member carries the app-wide admin override AND it is switched on.
    // Rendered as an "ADMIN" tag BESIDE — never instead of — their stored Rank.
    bool IsAdmin = false);

// "Jobs Roster" — every member's leveled jobs (the levels they entered on their
// Profile), for the linkshell's main + alt characters. JobCatalog is the job
// name order (WAR..PUP); each member's level arrays are aligned to it.
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
    // How many SPAWN windows the camp runs — pop chances, 7 on a king/dragon. This is the
    // "Window N of M" the card heads with, matching the Discord board.
    int WindowCount,
    // How many ATTENDANCE POSTS it takes — roster reads, 2 on a Standard king/dragon. A
    // different number from WindowCount above, and the one the Attendance Windows card counts
    // against: those tabs are posts, not pop chances. See
    // DiscordEventMessageBuilder.AttendancePostCount.
    int AttendancePostCount,
    IReadOnlyList<ActivityAttendanceWindowDto> AttendanceWindows,
    IReadOnlyList<ActivityLinkedSnapshotDto> LinkedSnapshots,
    IReadOnlyList<ActivityClaimShieldCaptureDto> ClaimShieldCaptures,
    string? CreatorCharacterName,
    string? StarterCharacterName,
    // The DKP pool this event earns into and pays its loot out of. Null when the linkshell has only
    // one pool — the client's cue to render the loot UI exactly as it did before pools existed.
    string? DkpPoolName = null,
    // Live HNM camp state, so the Activity can render a started camp (window N of M + a next-window
    // countdown) and offer End Camp. AttendanceMode "Wd" ⇒ Manual Check In; null ⇒ Standard.
    // NextWindowAt is when the next window opens (null on the final window / not timed). The Wd*
    // fields mark the Awaiting-Processing / finalized states (always null on a Standard board).
    string? AttendanceMode = null,
    // The window that has already OPENED (Event.HnmWindowNumber). Pop-window semantics key off
    // this — End Camp pre-fills it as "the window it popped on" — so it must stay the raw
    // counter. For anything the user READS, use HnmFocusWindow instead.
    int HnmWindowNumber = 1,
    DateTime? NextWindowAt = null,
    DateTime? WdAwaitingProcessingSince = null,
    DateTime? WdFinalizedAt = null,
    int? WdPopWindow = null,
    // The window number to DISPLAY — the one the Discord board shows (the window being awaited).
    // See DiscordEventMessageBuilder.FocusWindow. Kept separate from HnmWindowNumber so fixing
    // the display can't quietly move the pop window and change DKP credit.
    int HnmFocusWindow = 1,
    // Whether the Break Room (take break / force break / return / verify / deny, and the live
    // "Withdraw From Event" that parks a member there) applies at all. False for windowed HNM
    // camps, which credit per posted window and so have no timer to pause. Server-computed from
    // Services/EventBreakPolicy so the client can't disagree with the endpoints — the Activity
    // must branch on THIS, not re-derive its own windowCount test.
    bool SupportsBreakRoom = true,
    // Whether the camp's ToD actually carries an observed Time of Death. False when the camp was
    // ended without one (the window closed, or another linkshell took it) — there is then no
    // predicted repop, StartTime still points at the pop that just passed, and nothing will
    // auto-re-post. The defeated-board banner branches on this so it can't promise a repop that
    // was never derived. True whenever there is no source ToD at all (nothing to contradict).
    bool HnmTodRecorded = true,
    // Per-camp payout overrides, so the edit form can show "Change DKP" already open with
    // the camp's own numbers. Null = this camp uses the linkshell default.
    double? HnmOpenBonusOverride = null,
    double? HnmCloseBonusOverride = null,
    double? HnmClaimBonusOverride = null,
    double? HnmKillBonusOverride = null,
    double? HnmPerWindowOverride = null);

public sealed record ActivityAttendanceWindowDto(
    int Id,
    int SequenceNumber,
    string? Label,
    DateTime PostedAt,
    IReadOnlyList<ActivityAttendanceWindowAttendeeDto> Attendees,
    // What an officer priced THIS window at, or null when they never did and the camp's own
    // open / close bonuses apply. An explicit amount REPLACES those bonuses rather than adding to
    // them — HnmStandardCampFinalizer.WindowValue is the rule, and EventsTabComponent.windowValue
    // mirrors it. Only ever non-null on a Standard HNM camp; see HnmCampPricing.HonoursWindowAmount.
    double? DkpAmount = null,
    // The officer's "this window closes the camp out" tick. Drives the close bonus, and drives it
    // ALONE — the close used to be derived as "the newest window posted", which is what put a close
    // bonus on every window of every camp. At most one window per event carries it.
    bool IsClosingWindow = false,
    // The addon's Post Kill roster: who was there when the mob died. Its own row because that list
    // differs from who sat the window. Worth 0 as a window; being in it is what earns the kill
    // bonus, and it can never be the closing window.
    bool IsKillWindow = false);

// An attendance snapshot an officer attached to this camp (AttendanceSnapshot.LinkedEventId).
// Shown on the camp's own card so a roster reviewed over in the Event System doesn't have to be
// hunted for here. The link is presentational — payroll still runs off the snapshot's attendance
// event, not off this — which is why nothing on this DTO is editable.
public sealed record ActivityLinkedSnapshotDto(
    int Id,
    string? Name,
    DateTime CapturedAtUtc,
    string? CapturedByCharacterName,
    // The spawn window the capture was taken in, already named the way the window tabs are
    // ("Open" / "Close" / "Window 3"). Null when the camp runs no window grid.
    string? WindowLabel,
    string SnapshotStatus,
    IReadOnlyList<ActivityLinkedSnapshotEntryDto> Entries);

public sealed record ActivityLinkedSnapshotEntryDto(
    int Id,
    string? CharacterName,
    string? MainJob,
    int? MainJobLevel,
    string? SubJob,
    int? SubJobLevel,
    string? Zone,
    // True for a name an officer typed in rather than one the addon scanned. The UI tints
    // these so an asserted attendee never reads as an observed one.
    bool AddedManually);

// A claim-shield lottery captured during this camp: who from the linkshell
// actually landed an action on the mob before the lottery resolved, and what
// they did. Shown under the attendance windows because it is evidence about the
// same camp -- and because its timestamp pins the pop against the posted
// windows (see NearestWindowSequence).
public sealed record ActivityClaimShieldCaptureDto(
    int Id,
    string MonsterName,
    bool Won,
    // Players in the lottery server-wide, from the game's own result line --
    // not a linkshell number. Members.Count is the linkshell's share.
    int TotalPlayers,
    DateTime CapturedAtUtc,
    string? CapturedMessage,
    // The posted attendance window this pop falls inside, or the nearest one
    // when it lands outside them all. Null when the camp has posted none yet.
    // This is the "claim data proves which window we were on" link: the capture
    // is timestamped by the game, so it dates the window rather than the other
    // way round.
    int? NearestWindowSequence,
    IReadOnlyList<ActivityClaimShieldMemberDto> Members);

public sealed record ActivityClaimShieldMemberDto(
    string CharacterName,
    // "Azurth casts Dia on the Aspidochelone." Null on rows captured before the
    // addon recorded actions -- render the name alone in that case.
    string? ActionMessage,
    // False when the name didn't resolve to a current member (kept visible
    // rather than dropped, so a rename or a missing roster entry is obvious).
    bool Matched);

public sealed record ActivityAttendanceWindowAttendeeDto(
    int Id,
    // The character the roster read actually SAW — the alt, when the player was on one.
    string? CharacterName,
    // Their roster main, non-null ONLY when CharacterName above is an alt of it. Drives the
    // "(alt of Edicius)" note next to the name; null is the ordinary case and shows nothing.
    string? MainCharacterName,
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
    double BiddableDkp = 0,
    // Manual Check In only. The window this member first checked in for, and the one they
    // checked out on (null = still in). Credit runs arrival..min(departure, popWindow)
    // inclusive — see WdCampFinalizer, which is the authority on the payout.
    //
    // Exposed so the camp card can show late arrivals and what they have earned SO FAR, while
    // the camp is still running. Null on Standard camps and on anyone who never checked in.
    int? WdArrivalWindow = null,
    int? WdDepartureWindow = null);

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
    bool CanManageCharts,
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
    // Keep CanBid last: it is the only defaulted parameter, and a positional parameter without a
    // default cannot follow one that has it (CS1737). New permissions go ABOVE this line.
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
    bool CanManageCharts,
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
    int? WinningDkpSpent,
    // What was ACTUALLY taken off the winner's balance when the row was written, as opposed to
    // WinningDkpSpent, which is what the row is priced at. Non-null means the ledger entry already
    // exists -- which is true of every row that reaches this list, in-game or hand-entered, and is
    // exactly what an officer looking at a live camp's Loot section needs to know before deciding
    // whether to charge someone again at End Event.
    double? ActualDeductedDkp);

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
    int AdditionalSeconds = 0,
    // Which pop window it showed up on, so the Log ToD form round-trips on edit.
    int? PopWindow = null,
    // When the row was written, as distinct from Time (the observed ToD). A camp that ended with
    // no ToD has a null Time, so the client sorts on Time ?? TimeStamp to keep that row as the
    // monster's newest entry instead of letting the pop it superseded show as current.
    DateTime? TimeStamp = null);

public sealed record ActivityTodLootDto(
    int Id,
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent);

// One slice of the HNM Claims donut. Percent is already relative to its own window's total,
// and ColorClass is the palette letter the donut/legend paint with.
//
// The three NQ/HQ families chart as two slices each (Fafnir beside Nidhogg, and so on). Both
// halves carry the FAMILY's ColorClass and are told apart by IsHq, which the donut renders as a
// lighter arc and the legend as an HQ badge. HasHqVariant is false for monsters with no stronger
// half, so they show no badge at all.
public sealed record ActivityHnmClaimSliceDto(
    string MonsterName,
    int Count,
    double Percent,
    string ColorClass,
    bool IsHq,
    bool HasHqVariant);

// The donut's three windows, so the 7d / 30d / All toggle is instant and never re-queries.
public sealed record ActivityHnmClaimsDto(
    IReadOnlyList<ActivityHnmClaimSliceDto> Last7Days,
    IReadOnlyList<ActivityHnmClaimSliceDto> Last30Days,
    IReadOnlyList<ActivityHnmClaimSliceDto> AllTime);

// One window of one monster's spawn band on the card's second tab. Percent is the share of THAT
// monster's pops; HeightPercent is the share of its busiest window, which is what the bar draws to
// (a monster whose best window holds 30% would otherwise render as a row of stubs).
public sealed record ActivityHnmWindowBarDto(int Window, int Count, double Percent, double HeightPercent);

// One monster's window distribution. ColorClass is the family's donut colour, so a monster looks
// the same on both tabs. The NQ/HQ halves share a row here — unlike the Claims donut — because the
// spawn grid belongs to the family, not to which half showed up.
public sealed record ActivityHnmWindowMonsterDto(
    string MonsterName,
    string ColorClass,
    int TotalPops,
    int WindowCount,
    int PeakWindow,
    double PeakPercent,
    IReadOnlyList<ActivityHnmWindowBarDto> Bars);

// All-time on purpose: a spawn distribution needs volume, so there is no 7d/30d split to toggle.
public sealed record ActivityHnmWindowsDto(
    IReadOnlyList<ActivityHnmWindowMonsterDto> Monsters,
    int TotalPops);

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
    // HNM signup board only: the canonical monster the board is for, plus the monster's
    // standing "re-post before the next predicted pop" settings. Ignored for non-HNM events.
    //
    // RepeatOnTod is NULLABLE because the Activity's event form deliberately doesn't ask:
    // recurrence is switched on and off from the End Camp / Post ToD form, which is where an
    // officer knows the next pop. Null = "this form has no opinion, leave the standing board
    // exactly as it is". It used to be a plain bool, which meant the Activity silently posted
    // false on every create and switched the monster's recurring board OFF. Only an explicit
    // true/false (the web form's checkbox) enables or disables it.
    //
    // RepostLeadHours is fractional (1.5 = 1h30m) and likewise null when not entered, meaning
    // "keep the board's current lead" — so editing an event can't overwrite a lead set at End
    // Camp. The Activity only sends it from the EDIT form.
    string? MonsterName = null,
    bool? RepeatOnTod = null,
    double? RepostLeadHours = null,
    // HNM signup board only: optional "Day N" label shown on the board.
    int? DayNumber = null,
    // HNM only: per-camp overrides for the linkshell's payout amounts. Null = use the
    // linkshell default (the normal case — the form only sends these when the creator
    // opened "Change DKP"). Ignored for non-HNM events. Open/Close apply in Standard
    // mode, PerWindow in Wd mode, Claim/Kill in both.
    double? HnmOpenBonusOverride = null,
    double? HnmCloseBonusOverride = null,
    double? HnmClaimBonusOverride = null,
    double? HnmKillBonusOverride = null,
    double? HnmPerWindowOverride = null);

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
    // null = leave unchanged. Allow account-less Discord board signups (every event type).
    bool? OutsidePartySignupEnabled = null,
    // null = leave unchanged. Post event boards as Components V2 (wide media-gallery card).
    bool? UseComponentsV2Boards = null,
    // Manual Check In HNM attendance (all null = leave unchanged). Mode is normalized (Standard | Wd)
    // and fails closed to Standard; the doubles are clamped to >= 0.
    string? HnmAttendanceMode = null,
    double? WdDkpPerWindow = null,
    double? WdClaimBonus = null,
    double? WdKillBonus = null,
    // Standard-mode HNM bonuses (null = leave unchanged; clamped to >= 0).
    double? HnmStandardOpenBonus = null,
    double? HnmStandardCloseBonus = null,
    double? HnmStandardClaimBonus = null,
    double? HnmStandardKillBonus = null,
    // (TodMonsterTimings was here. Per-monster setups POST to their own monster-timings endpoint
    // now — precisely so this whole-payload update, which re-sends every setting on any save,
    // cannot wipe them.)
    // Automatic per-window snapshots (null = leave unchanged). Delay clamped to [5, 300].
    bool? HnmAutoSnapshotEnabled = null,
    int? HnmAutoSnapshotDelaySeconds = null,
    // Standard regular-window rate and the Manual Check In open / close bonuses
    // (null = leave unchanged; clamped to >= 0).
    double? HnmStandardWindowBonus = null,
    double? WdOpenBonus = null,
    double? WdCloseBonus = null);

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
    // NULLABLE on purpose. The ToD form no longer carries loot, so the client sends null, and on
    // update null means "leave the existing loot alone" (an explicit list, empty included, still
    // replaces it). It must ALSO stay nullable because [ApiController] + <Nullable>enable gives a
    // non-nullable reference property an IMPLICIT [Required] -- which rejected every ToD save with
    // a ProblemDetails 400 before the action body ever ran.
    IReadOnlyList<ActivityCreateTodLootRequest>? LootDetails,
    string? ImagePath,
    bool Hq = false,
    int AdditionalSeconds = 0,
    // Which pop window the monster showed up on. null/0 = not recorded.
    int? PopWindow = null);

public sealed record ActivityUpdateTodRequest(
    string? MonsterName,
    int? DayNumber,
    // Tri-state: true=Claimed, false=Unclaimed, null=Not Specified.
    bool? Claim,
    string? TimeLocal,
    string? Cooldown,
    string? Interval,
    bool NoLoot,
    // NULLABLE on purpose. The ToD form no longer carries loot, so the client sends null, and on
    // update null means "leave the existing loot alone" (an explicit list, empty included, still
    // replaces it). It must ALSO stay nullable because [ApiController] + <Nullable>enable gives a
    // non-nullable reference property an IMPLICIT [Required] -- which rejected every ToD save with
    // a ProblemDetails 400 before the action body ever ran.
    IReadOnlyList<ActivityCreateTodLootRequest>? LootDetails,
    string? ImagePath,
    bool Hq = false,
    int AdditionalSeconds = 0,
    // Which pop window the monster showed up on. null/0 = not recorded.
    int? PopWindow = null);

public sealed record ActivityCreateTodLootRequest(
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent);

// Logs (or edits) a ToD from an HNM signup board's "Post ToD" / "Edit ToD" button. The
// monster + linkshell come from the event (path id), so the board form only sends the
// time + cooldown/interval/day/claim (+ an optional End Camp kill screenshot). No loot —
// that stays on the ToDs tab.
public sealed record ActivityPostBoardTodRequest(
    string? TimeLocal,
    string? Cooldown,
    string? Interval,
    int? DayNumber,
    // Tri-state: true=Claimed, false=Unclaimed, null=Not Specified.
    bool? Claim,
    bool Hq = false,
    int AdditionalSeconds = 0,
    // BOTH modes: did this linkshell get the kill? Drives the kill bonus at finalize — via
    // Event.WdKilled → WdCampFinalizer in Manual Check In, and straight into
    // HnmStandardCampFinalizer.StageCreditAsync in Standard. Also persisted as Tod.Killed.
    // null = unspecified (defaults to true, since posting a board ToD normally means the LS killed
    // it; an officer can uncheck if the pop was stolen).
    bool? Killed = null,
    // Manual Check In only: the window the monster popped on. Caps credit at finalize (nobody is
    // paid past this window even if the counter auto-advanced further). null = use the current window.
    int? PopWindow = null,
    // End Camp "re-post the sign-up board before the next pop?" choice: null = leave the monster's
    // standing Repeat-on-ToD config alone; true = enable it (RepostLeadHours = hours before the pop);
    // false = disable it for this cycle.
    bool? Repost = null,
    double? RepostLeadHours = null,
    // End Camp's optional kill screenshot (an /uploads/tods/... path from the upload endpoint).
    // null/blank = don't touch whatever image the ToD already has.
    string? ImagePath = null);

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
    // Per-job levels for the selectable jobs in EventJobCatalog.MainJobOptions
    // order (index 0 = WAR … 14 = SMN, 15 = BLU … 17 = PUP). Persisted to the user's memberships.
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
    // Per-job free-text merit notes (main + alts), catalog-aligned (WAR … PUP).
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

// Hand-entered loot. SourceKind picks which id below is read: "live" an Event, "past" an
// EventHistory, "none" neither. Context is gone -- loot points at a real event now, not at a
// free-text label that used to become a throwaway ToD.
public sealed record ActivityLootAddRequest(
    string? SourceKind,
    int? EventId,
    int? EventHistoryId,
    string? ItemName,
    string? ItemWinner,
    int? WinningDkpSpent,
    int? DkpPoolId);

// One selectable event on the Add loot form.
public sealed record ActivityLootEventOptionDto(int Id, string Name, string? Detail);

// Live events plus the recent past ones (widened by a search), for the Add loot pickers.
public sealed record ActivityLootEventOptionsDto(
    IReadOnlyList<ActivityLootEventOptionDto> LiveEvents,
    IReadOnlyList<ActivityLootEventOptionDto> PastEvents,
    string? Query);

// ---- Charts (Sky, Sea, …) ----------------------------------------------------------------------
//
// One payload for a whole board. The boss cards and the Farming Credit Ledger are two views of the
// same rows, so they are built together and shipped together — a client that fetched them separately
// could render halves that disagree.
//
// Board-agnostic: Sky's five gods and Sea's eight Jailers use these same records, differing only in
// what ChartBoardCatalog says.

/// <summary>Everything a board shows. <c>CanManage</c> is the server's own answer, not a copy of the rule.</summary>
public sealed record ActivityChartBoardDto(
    int LinkshellId,
    string Board,
    string BoardLabel,
    string Blurb,
    IReadOnlyList<ActivityChartBossDto> Bosses,
    /// <summary>Group labels drawn as vertical columns rather than rows of the grid — Sky's four
    /// paths. Empty for every other board. A run not named here renders below the columns.</summary>
    IReadOnlyList<string> PathColumns,
    /// <summary>Draw as centred rows of fixed-width cards rather than a stretch-to-fit grid, for a
    /// board that chose its own row lengths (Dynamis, Limbus, HENM).</summary>
    bool CentersRows,
    ActivityChartLedgerDto Ledger,
    // Who a credit can be attributed to. Empty unless CanManage — a reader has nothing to pick.
    IReadOnlyList<ActivityChartRosterMemberDto> Roster,
    DateTime? LastUpdatedUtc,
    bool CanManage,
    /// <summary>Which affordances this board offers. The client branches on these, never on whether
    /// a list happens to be populated.</summary>
    ActivityChartBoardFeaturesDto Features,
    /// <summary>The board's item requests. Folded into THIS payload rather than fetched separately,
    /// for the same reason the ledger is: a card's badge and the list below it are two views of the
    /// same rows, and fetching them apart lets one screen contradict itself.</summary>
    ActivityChartWishlistDto Wishlist,
    /// <summary>Per-member key item progress. Empty columns on a board that tracks none.</summary>
    ActivityChartKeyItemGridDto KeyItems,
    /// <summary>The VIEWER's own membership, so the client knows which key item row is theirs to
    /// tick. Null for somebody with no membership row. The server re-checks on every write.</summary>
    int? ViewerMembershipId);

/// <summary>
/// What a board offers, straight off ChartBoardCatalog. Sent rather than inferred client-side: a
/// board can declare no pop items and still take them (HENM), and can declare none and no longer
/// offer the form at all (Dynamis, Limbus). Those are different facts.
/// </summary>
public sealed record ActivityChartBoardFeaturesDto(
    bool PopItems,
    bool DropItems,
    bool Wishlist,
    bool KeyItems);

/// <summary>The boards the Charts sub-nav offers, so the client does not keep its own list.</summary>
public sealed record ActivityChartBoardSummaryDto(string Board, string Label);

/// <summary>
/// One boss's card. <c>ThemeKey</c> names a CSS class and never carries a colour value; <c>Kind</c>
/// chooses the layout (Standard grid card, MiniNm, or the board's Final encounter panel).
/// </summary>
public sealed record ActivityChartBossDto(
    string Boss,
    string ThemeKey,
    string Kind,
    /// <summary>Section heading this card sits under, or null on an ungrouped board. Presentation
    /// only — nothing is stored against it, and cards sharing a label are always adjacent.</summary>
    string? Group,
    string EmblemPath,
    string? Subtitle,
    /// <summary>Static reference content — currently only the final encounter's reward list.</summary>
    IReadOnlyList<string> Rewards,
    string? ReferenceNote,
    /// <summary>The pop items this boss takes. Non-empty turns the client's "Pop item" box into a
    /// picker; empty leaves it free text, which is what every board but Sky does today.</summary>
    IReadOnlyList<ActivityChartPopItemOptionDto> PopItemOptions,
    IReadOnlyList<ActivityChartPopItemDto> Items,
    int TotalItems,
    int TotalQuantity,
    /// <summary>The card this one's drops feed ("Suzaku"), or null. Renders as the arrow badge.</summary>
    string? LeadsTo,
    /// <summary>That card's OWN theme key, so the badge is tinted in the TARGET's hue rather than
    /// this card's. Resolved off the catalog server-side, so neither surface maps a boss name to a
    /// colour itself and an arrow can never be a different colour from the card it points at.</summary>
    string? LeadsToThemeKey,
    /// <summary>Start a new row after this card. A board that sets it anywhere is drawn as centred
    /// rows of fixed-width cards rather than as a stretch-to-fit grid.</summary>
    bool EndsRow,
    /// <summary>What falls OFF this boss, as distinct from what is traded TO it. Non-empty turns
    /// the client's drop-item box into a picker, exactly as PopItemOptions does for the pop one.
    /// Appended LAST, like every addition to this record: a new optional member earlier would
    /// silently capture the value meant for the one after it.</summary>
    IReadOnlyList<ActivityChartPopItemOptionDto> DropItemOptions,
    /// <summary>Pending item requests tied to THIS card. Board-level requests count toward none.</summary>
    int PendingRequestCount,
    /// <summary>The key item earned here, or null for a card that grants none.</summary>
    string? KeyItemName,
    int KeyItemHaveCount,
    int KeyItemTotalMembers,
    /// <summary>Exactly who still needs it, in roster order — what the card's drawer lists.</summary>
    IReadOnlyList<string> KeyItemMissing);

/// <summary>
/// <c>Name</c> is what gets stored; <c>Source</c> is the mob that drops it; <c>Label</c> is the two
/// composed the way the website composes them, so the picker reads identically on both surfaces.
/// </summary>
public sealed record ActivityChartPopItemOptionDto(string Name, string? Source, string Label);

public sealed record ActivityChartPopItemDto(
    int Id,
    string Board,
    string Boss,
    string ItemName,
    string? HeldByCharacterName,
    int? HeldByMembershipId,
    int Quantity,
    string? Notes,
    int SortOrder,
    IReadOnlyList<ActivityChartCreditDto> Credits,
    /// <summary>How many farmers are credited — what the card's "Farmers Credited" column shows.</summary>
    int CreditCount,
    DateTime UpdatedAt,
    /// <summary>ChartItemKinds.Pop or Drop. Picks the pill beside the name and which option list the
    /// edit form offers; nothing else about the row differs.</summary>
    string Kind);

public sealed record ActivityChartCreditDto(int? MembershipId, string CharacterName, string? Detail);

/// <summary><c>Bosses</c> is the column order, decided here so no client re-derives it.</summary>
public sealed record ActivityChartLedgerDto(
    IReadOnlyList<string> Bosses,
    IReadOnlyList<ActivityChartLedgerRowDto> Rows);

public sealed record ActivityChartLedgerRowDto(
    int? MembershipId,
    string CharacterName,
    // False when this name is credited on an item but is no longer on the roster. Removing somebody
    // from the linkshell must not erase the fact that they farmed.
    bool IsCurrentMember,
    string? Rank,
    IReadOnlyList<ActivityChartLedgerCellDto> Cells,
    // Items credited over items tracked across the whole board, plus the percentage the ledger shows.
    int TotalCredited,
    int TotalTracked,
    int CreditedPercent);

/// <summary><c>Status</c> is a ChartCreditStatuses value; <c>Detail</c> ("6 / 8") is composed server-side so both surfaces word it identically.</summary>
public sealed record ActivityChartLedgerCellDto(
    string Boss,
    string Status,
    string Detail,
    int CreditedItems,
    int TotalItems);

public sealed record ActivityChartRosterMemberDto(
    int MembershipId,
    string? AppUserId,
    string CharacterName,
    string? Rank,
    /// <summary>This member's own other characters, for the "Held by" list. Farming credit is not
    /// offered per alt — credit belongs to a membership, and an alt is the same person.</summary>
    IReadOnlyList<string> AltCharacterNames);

public sealed record ActivityChartPopItemRequest(
    string? Board,
    string? Boss,
    string? ItemName,
    string? HeldByCharacterName,
    int? HeldByMembershipId,
    int Quantity,
    string? Notes,
    /// <summary>
    /// Who farmed it, named while the row is being written rather than in a second trip through the
    /// credits endpoint. Set-wise like that endpoint, so a list REPLACES what the row has.
    ///
    /// NULL is not an empty list: it means "leave the credits alone", which is what a caller that
    /// does not know about them sends. An empty list clears them.
    /// </summary>
    IReadOnlyList<ActivityChartCreditInput>? Credits = null,
    /// <summary>
    /// ChartItemKinds.Pop or Drop; null reads as Pop, so a caller written before drops existed
    /// keeps working unchanged.
    ///
    /// Honoured on ADD only. On update the row's OWN kind wins, exactly as its board does: an item
    /// moves between bosses, never between kinds.
    /// </summary>
    string? Kind = null);

public sealed record ActivityChartCreditInput(int? MembershipId, string? CharacterName, string? Detail);

/// <summary>The COMPLETE farmer list for one row. Credits are written set-wise, so an omitted name is a removal.</summary>
public sealed record ActivitySetChartCreditsRequest(IReadOnlyList<ActivityChartCreditInput>? Credits);

// ---- Charts: the wishlist ---------------------------------------------------------
//
// The one part of Charts a member without CanManageCharts may write. CanWithdraw is decided
// server-side PER VIEWER and shipped on the row, so no client works out for itself whether to show
// the button - a second copy of the ownership rule is exactly how one front-end ends up more
// permissive than the other.

public sealed record ActivityChartWishlistDto(
    IReadOnlyList<ActivityChartWishlistRequestDto> Requests,
    /// <summary>How many pending requests are outstanding across the whole board.</summary>
    int PendingCount);

public sealed record ActivityChartWishlistRequestDto(
    int Id,
    string Board,
    /// <summary>The card it is tied to, or null for "anywhere on this board".</summary>
    string? Boss,
    string ItemName,
    int Quantity,
    string? Notes,
    /// <summary>ChartWishlistStatuses.Pending or Fulfilled. There is no Withdrawn - withdrawing
    /// deletes the row.</summary>
    string Status,
    int Priority,
    int? RequestedByMembershipId,
    string RequestedByCharacterName,
    /// <summary>Whether THIS viewer may withdraw it: their own, or anybody's if they can manage.</summary>
    bool CanWithdraw,
    DateTime RequestedAt,
    DateTime? FulfilledAt,
    string? FulfilledByCharacterName);

/// <summary>A member submitting a request. Board comes from the route, never from the body.</summary>
public sealed record ActivityChartWishlistRequestInput(
    /// <summary>Blank or null means "anywhere on this board", which is what the form opens on.</summary>
    string? Boss,
    string? ItemName,
    int Quantity,
    string? Notes);

public sealed record ActivityChartWishlistStatusRequest(string? Status);

/// <summary>The COMPLETE ordered id list for a board. An id from elsewhere refuses the whole thing.</summary>
public sealed record ActivityChartWishlistOrderRequest(IReadOnlyList<int>? OrderedIds);

// ---- Charts: key items ------------------------------------------------------------
//
// Columns come from the CATALOG in catalog order, never from the data, so a key item nobody holds
// still gets a column reading "0 of 14 have it".

public sealed record ActivityChartKeyItemGridDto(
    IReadOnlyList<ActivityChartKeyItemColumnDto> Columns,
    IReadOnlyList<ActivityChartKeyItemRowDto> Rows);

public sealed record ActivityChartKeyItemColumnDto(
    string Name,
    /// <summary>The card it is earned on, or null for a board-level prerequisite.</summary>
    string? Boss,
    /// <summary>Progression note under the column header. Reference text only.</summary>
    string? Caption,
    int HaveCount,
    int TotalMembers,
    IReadOnlyList<string> MissingCharacterNames);

/// <summary><c>Has</c> is aligned to the column order above, so no client matches by name.</summary>
public sealed record ActivityChartKeyItemRowDto(
    int MembershipId,
    string CharacterName,
    string? Rank,
    IReadOnlyList<bool> Has,
    int HaveCount,
    int TotalColumns,
    int HavePercent);

/// <summary>One cell toggled. <c>Has</c> false DELETES the row - presence is the fact.</summary>
public sealed record ActivityChartKeyItemRequest(
    string? KeyItemName,
    int MembershipId,
    bool Has);
