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

    public ActivityDataController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        UserManager<AppUser> userManager,
        ILogger<ActivityDataController> logger,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
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
