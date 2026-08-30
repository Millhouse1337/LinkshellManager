using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[ApiController]
[Route("api/activity")]
public sealed partial class ActivityDataController : ControllerBase
{
    private const string PendingInviteStatus = "PendingInvite";
    private const string PendingJoinRequestStatus = "PendingJoinRequest";
    // (SupportedTodMonsters lived here. Which monsters a linkshell may assign is a per-linkshell
    // question now — its built-ins plus the ones it added itself — answered by
    // MonsterTimingMap.Allows off the request's MonsterTimingResolver.)
    private static readonly HashSet<string> SupportedTodCooldowns = new(TodManagerViewModel.SupportedCooldowns, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedTodIntervals = new(TodManagerViewModel.SupportedIntervals, StringComparer.OrdinalIgnoreCase);

    private readonly ApplicationDbContext _dbContext;
    private readonly DiscordIdentityService _discordIdentityService;
    private readonly InviteCandidateService _inviteCandidates;
    private readonly DiscordBotClient _discordBot;
    private readonly AppUserProfileService _appUserProfileService;
    private readonly UserManager<AppUser> _userManager;
    private readonly IHostEnvironment _environment;
    private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _webHostEnvironment;
    private readonly TimeZoneConversionService _timeZones;
    private readonly WindowEventDkpLedgerService _windowEventDkpLedger;
    private readonly WindowEventLinkService _windowEventLinks;
    private readonly DkpSheetService _dkpSheet;
    private readonly ILogger<ActivityDataController> _logger;
    private readonly GlobalSettingsService _globalSettings;
    private readonly AdminOverrideService _adminOverride;
    private readonly MemberActivityService _memberActivity;
    private readonly ChannelRouteEditor _channelRoutes;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;
    private readonly DkpPoolEditor _dkpPoolEditor;
    private readonly DkpPoolEventTypeCatalog _dkpPoolEventTypes;
    private readonly TreasuryBalanceService _treasury;
    private readonly TreasuryJournalWriter _treasuryJournal;
    private readonly TreasurySettlementService _treasurySettlements;
    private readonly LedgerAccountProvisioner _ledgerAccounts;
    private readonly LedgerPeriodGuard _ledgerPeriods;
    private readonly ItemSaleRecorder _itemSales;
    private readonly ChartBoardService _chartBoards;
    private readonly ChartWishlistService _chartWishlist;
    private readonly ChartKeyItemService _chartKeyItems;
    private readonly MonsterTimingResolver _monsterTimings;
    private readonly MonsterTimingEditor _monsterTimingEditor;
    private readonly LinkshellMonsterTimingProvisioner _monsterTimingProvisioner;

    public ActivityDataController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        InviteCandidateService inviteCandidates,
        DiscordBotClient discordBot,
        AppUserProfileService appUserProfileService,
        UserManager<AppUser> userManager,
        IHostEnvironment environment,
        Microsoft.AspNetCore.Hosting.IWebHostEnvironment webHostEnvironment,
        TimeZoneConversionService timeZones,
        WindowEventDkpLedgerService windowEventDkpLedger,
        WindowEventLinkService windowEventLinks,
        DkpSheetService dkpSheet,
        ILogger<ActivityDataController> logger,
        GlobalSettingsService globalSettings,
        AdminOverrideService adminOverride,
        MemberActivityService memberActivity,
        ChannelRouteEditor channelRoutes,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        DkpPoolEditor dkpPoolEditor,
        DkpPoolEventTypeCatalog dkpPoolEventTypes,
        TreasuryBalanceService treasury,
        TreasuryJournalWriter treasuryJournal,
        TreasurySettlementService treasurySettlements,
        LedgerAccountProvisioner ledgerAccounts,
        LedgerPeriodGuard ledgerPeriods,
        ItemSaleRecorder itemSales,
        ChartBoardService chartBoards,
        ChartWishlistService chartWishlist,
        ChartKeyItemService chartKeyItems,
        MonsterTimingResolver monsterTimings,
        MonsterTimingEditor monsterTimingEditor,
        LinkshellMonsterTimingProvisioner monsterTimingProvisioner)
    {
        _dbContext = dbContext;
        _discordIdentityService = discordIdentityService;
        _inviteCandidates = inviteCandidates;
        _discordBot = discordBot;
        _appUserProfileService = appUserProfileService;
        _userManager = userManager;
        _environment = environment;
        _webHostEnvironment = webHostEnvironment;
        _timeZones = timeZones;
        _windowEventDkpLedger = windowEventDkpLedger;
        _windowEventLinks = windowEventLinks;
        _dkpSheet = dkpSheet;
        _logger = logger;
        _globalSettings = globalSettings;
        _adminOverride = adminOverride;
        _memberActivity = memberActivity;
        _channelRoutes = channelRoutes;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
        _dkpPoolEditor = dkpPoolEditor;
        _dkpPoolEventTypes = dkpPoolEventTypes;
        _treasury = treasury;
        _treasuryJournal = treasuryJournal;
        _treasurySettlements = treasurySettlements;
        _ledgerAccounts = ledgerAccounts;
        _ledgerPeriods = ledgerPeriods;
        _itemSales = itemSales;
        _chartBoards = chartBoards;
        _chartWishlist = chartWishlist;
        _chartKeyItems = chartKeyItems;
        _monsterTimings = monsterTimings;
        _monsterTimingEditor = monsterTimingEditor;
        _monsterTimingProvisioner = monsterTimingProvisioner;
    }

    [HttpGet("antiforgery")]
    public IActionResult GetAntiforgeryToken(
        [FromServices] Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new
        {
            headerName = tokens.HeaderName,
            requestToken = tokens.RequestToken
        });
    }

    private static bool IsValidLootStructure(string? structure)
    {
        return string.Equals(structure, "Dkp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(structure, "LootCouncil", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(structure, "Hybrid", StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeLootStructure(string structure)
    {
        if (string.Equals(structure, "LootCouncil", StringComparison.OrdinalIgnoreCase)) return "LootCouncil";
        if (string.Equals(structure, "Hybrid", StringComparison.OrdinalIgnoreCase)) return "Hybrid";
        return "Dkp";
    }

    private static double CalculateAccumulatedDurationHours(AppUserEvent participation, DateTime referenceUtc, DateTime? eventStartUtc)
    {
        var accumulatedHours = Math.Max(0, participation.Duration ?? 0);
        if (participation.IsOnBreak == true)
        {
            return accumulatedHours;
        }

        var segmentStart = participation.ResumeTime ?? participation.StartTime ?? eventStartUtc;
        if (!segmentStart.HasValue)
        {
            return accumulatedHours;
        }

        var segmentHours = Math.Max(0, (referenceUtc - segmentStart.Value).TotalHours);
        return accumulatedHours + segmentHours;
    }

    private static AppUserLinkshell? ResolveLootWinnerMembership(
        string? itemWinner,
        IReadOnlyDictionary<string, AppUserLinkshell> membershipsByAppUserId,
        IReadOnlyDictionary<string, AppUserEvent> participantsByCharacterName,
        IEnumerable<AppUserLinkshell> linkshellMemberships)
    {
        var normalizedWinner = NormalizeLookupKey(itemWinner);
        if (normalizedWinner is null)
        {
            return null;
        }

        if (participantsByCharacterName.TryGetValue(normalizedWinner, out var participation) &&
            !string.IsNullOrWhiteSpace(participation.AppUserId) &&
            membershipsByAppUserId.TryGetValue(participation.AppUserId, out var participantMembership))
        {
            return participantMembership;
        }

        return linkshellMemberships.FirstOrDefault(link =>
            string.Equals(NormalizeLookupKey(link.CharacterName), normalizedWinner, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeLookupKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsLeader(AppUserLinkshell? membership)
    {
        return membership?.Rank?.Equals(LinkshellRanks.Leader, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? NormalizeMemberRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        return role.Trim().ToLowerInvariant() switch
        {
            "member" => LinkshellRanks.Member,
            "officer" => LinkshellRanks.Officer,
            "leader" => LinkshellRanks.Leader,
            _ => null
        };
    }

    private bool TryConvertUserTimeZoneToUtc(string? localDateTimeValue, string? timeZoneId, out DateTime? utcDateTime)
        => _timeZones.TryParseUserLocalOrUtc(localDateTimeValue, timeZoneId, out utcDateTime);
}
