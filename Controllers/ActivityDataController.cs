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
public sealed partial class ActivityDataController : ActivityControllerBase
{
    // (SupportedTodMonsters lived here. Which monsters a linkshell may assign is a per-linkshell
    // question now — its built-ins plus the ones it added itself — answered by
    // MonsterTimingMap.Allows off the request's MonsterTimingResolver.)
    private static readonly HashSet<string> SupportedTodCooldowns = new(TodCatalog.SupportedCooldowns, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedTodIntervals = new(TodCatalog.SupportedIntervals, StringComparer.OrdinalIgnoreCase);

    private readonly InviteCandidateService _inviteCandidates;
    private readonly DiscordBotClient _discordBot;
    private readonly TimeZoneConversionService _timeZones;
    private readonly AdminOverrideService _adminOverride;
    private readonly MemberActivityService _memberActivity;
    private readonly ChannelRouteEditor _channelRoutes;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;
    private readonly TreasuryBalanceService _treasury;
    private readonly TreasuryJournalWriter _treasuryJournal;
    private readonly TreasurySettlementService _treasurySettlements;
    private readonly LedgerAccountProvisioner _ledgerAccounts;
    private readonly ItemSaleRecorder _itemSales;
    private readonly ChartBoardService _chartBoards;
    private readonly ChartWishlistService _chartWishlist;
    private readonly ChartKeyItemService _chartKeyItems;
    private readonly MonsterTimingResolver _monsterTimings;

    public ActivityDataController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        InviteCandidateService inviteCandidates,
        DiscordBotClient discordBot,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        ILogger<ActivityDataController> logger,
        AdminOverrideService adminOverride,
        MemberActivityService memberActivity,
        ChannelRouteEditor channelRoutes,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        TreasuryBalanceService treasury,
        TreasuryJournalWriter treasuryJournal,
        TreasurySettlementService treasurySettlements,
        LedgerAccountProvisioner ledgerAccounts,
        ItemSaleRecorder itemSales,
        ChartBoardService chartBoards,
        ChartWishlistService chartWishlist,
        ChartKeyItemService chartKeyItems,
        MonsterTimingResolver monsterTimings,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _inviteCandidates = inviteCandidates;
        _discordBot = discordBot;
        _timeZones = timeZones;
        _adminOverride = adminOverride;
        _memberActivity = memberActivity;
        _channelRoutes = channelRoutes;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
        _treasury = treasury;
        _treasuryJournal = treasuryJournal;
        _treasurySettlements = treasurySettlements;
        _ledgerAccounts = ledgerAccounts;
        _itemSales = itemSales;
        _chartBoards = chartBoards;
        _chartWishlist = chartWishlist;
        _chartKeyItems = chartKeyItems;
        _monsterTimings = monsterTimings;
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
}
