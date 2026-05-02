using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

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
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public ActivityDataController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        AppUserProfileService appUserProfileService,
        UserManager<AppUser> userManager,
        IHostEnvironment environment,
        Microsoft.AspNetCore.Hosting.IWebHostEnvironment webHostEnvironment,
        IDateTimeZoneProvider dateTimeZoneProvider)
    {
        _dbContext = dbContext;
        _discordIdentityService = discordIdentityService;
        _appUserProfileService = appUserProfileService;
        _userManager = userManager;
        _environment = environment;
        _webHostEnvironment = webHostEnvironment;
        _dateTimeZoneProvider = dateTimeZoneProvider;
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
        return membership?.Rank?.Equals("Leader", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string? NormalizeMemberRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        return role.Trim().ToLowerInvariant() switch
        {
            "member" => "Member",
            "officer" => "Officer",
            "leader" => "Leader",
            _ => null
        };
    }

    private static string CreateJobSignature(ActivityCreateJobRequest job)
    {
        return $"{job.JobName?.Trim()}|{job.SubJobName?.Trim()}|{job.JobType?.Trim()}|{job.Quantity}";
    }

    private static string CreateJobSignature(Job job)
    {
        return $"{job.JobName?.Trim()}|{job.SubJobName?.Trim()}|{job.JobType?.Trim()}|{job.Quantity}";
    }

    private bool TryConvertUserTimeZoneToUtc(string? localDateTimeValue, string? timeZoneId, out DateTime? utcDateTime)
    {
        utcDateTime = null;

        if (string.IsNullOrWhiteSpace(localDateTimeValue))
        {
            return true;
        }

        var trimmed = localDateTimeValue.Trim();

        if (HasExplicitUtcOffset(trimmed)
            && DateTime.TryParse(
                trimmed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsedUtc))
        {
            utcDateTime = DateTime.SpecifyKind(parsedUtc, DateTimeKind.Utc);
            return true;
        }

        if (!DateTime.TryParseExact(
                trimmed,
                ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDateTime))
        {
            return false;
        }

        var zone = ResolveTimeZone(timeZoneId);
        utcDateTime = zone.AtLeniently(LocalDateTime.FromDateTime(localDateTime)).ToDateTimeUtc();
        return true;
    }

    private static bool HasExplicitUtcOffset(string value)
    {
        if (value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var tIndex = value.IndexOf('T');
        if (tIndex < 0)
        {
            return false;
        }

        for (var i = value.Length - 1; i > tIndex; i--)
        {
            var c = value[i];
            if (c == '+' || c == '-')
            {
                return true;
            }
            if (c == ':' || char.IsDigit(c))
            {
                continue;
            }
            return false;
        }

        return false;
    }

    private DateTimeZone ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && _dateTimeZoneProvider.Ids.Contains(timeZoneId))
        {
            return _dateTimeZoneProvider[timeZoneId];
        }

        return DateTimeZone.Utc;
    }
}
