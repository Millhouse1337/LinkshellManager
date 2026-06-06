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
    private static readonly HashSet<string> SupportedTodMonsters = new(TodManagerViewModel.SupportedMonsters, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedTodCooldowns = new(TodManagerViewModel.SupportedCooldowns, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedTodIntervals = new(TodManagerViewModel.SupportedIntervals, StringComparer.OrdinalIgnoreCase);

    private readonly ApplicationDbContext _dbContext;
    private readonly DiscordIdentityService _discordIdentityService;
    private readonly AppUserProfileService _appUserProfileService;
    private readonly UserManager<AppUser> _userManager;
    private readonly IHostEnvironment _environment;
    private readonly Microsoft.AspNetCore.Hosting.IWebHostEnvironment _webHostEnvironment;
    private readonly TimeZoneConversionService _timeZones;
    private readonly SheetSyncQueue _sheetSync;
    private readonly WindowEventDkpLedgerService _windowEventDkpLedger;
    private readonly SnapshotAttInputAuditService _snapshotAttInputAudit;
    private readonly GoogleSheetsSyncService _sheets;
    private readonly ILogger<ActivityDataController> _logger;

    public ActivityDataController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        AppUserProfileService appUserProfileService,
        UserManager<AppUser> userManager,
        IHostEnvironment environment,
        Microsoft.AspNetCore.Hosting.IWebHostEnvironment webHostEnvironment,
        TimeZoneConversionService timeZones,
        SheetSyncQueue sheetSync,
        WindowEventDkpLedgerService windowEventDkpLedger,
        SnapshotAttInputAuditService snapshotAttInputAudit,
        GoogleSheetsSyncService sheets,
        ILogger<ActivityDataController> logger)
    {
        _dbContext = dbContext;
        _discordIdentityService = discordIdentityService;
        _appUserProfileService = appUserProfileService;
        _userManager = userManager;
        _environment = environment;
        _webHostEnvironment = webHostEnvironment;
        _timeZones = timeZones;
        _sheetSync = sheetSync;
        _windowEventDkpLedger = windowEventDkpLedger;
        _snapshotAttInputAudit = snapshotAttInputAudit;
        _sheets = sheets;
        _logger = logger;
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
