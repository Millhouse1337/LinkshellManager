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

    private readonly DiscordBotClient _discordBot;
    private readonly AdminOverrideService _adminOverride;
    private readonly MemberActivityService _memberActivity;
    private readonly ChannelRouteEditor _channelRoutes;
    private readonly ItemSaleRecorder _itemSales;
    private readonly MonsterTimingResolver _monsterTimings;

    public ActivityDataController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        DiscordBotClient discordBot,
        UserManager<AppUser> userManager,
        ILogger<ActivityDataController> logger,
        AdminOverrideService adminOverride,
        MemberActivityService memberActivity,
        ChannelRouteEditor channelRoutes,
        ItemSaleRecorder itemSales,
        MonsterTimingResolver monsterTimings,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _discordBot = discordBot;
        _adminOverride = adminOverride;
        _memberActivity = memberActivity;
        _channelRoutes = channelRoutes;
        _itemSales = itemSales;
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
