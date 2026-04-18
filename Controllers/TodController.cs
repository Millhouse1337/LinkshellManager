using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class TodController : Controller
{
    private static readonly HashSet<string> SupportedMonsters = new(TodManagerViewModel.SupportedMonsters, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedCooldowns = new(TodManagerViewModel.SupportedCooldowns, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedIntervals = new(TodManagerViewModel.SupportedIntervals, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LongWindowMonsters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tiamat",
        "Jormungand",
        "Vrtra"
    };

    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public TodController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        IDateTimeZoneProvider dateTimeZoneProvider)
    {
        _context = context;
        _userManager = userManager;
        _dateTimeZoneProvider = dateTimeZoneProvider;
    }

    [HttpGet]
    public async Task<IActionResult> Index(int? linkshellId = null)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var model = await BuildViewModelAsync(user, new TodManagerViewModel
        {
            LinkshellId = linkshellId ?? 0,
            Tod = new Tod
            {
                LinkshellId = linkshellId ?? user.PrimaryLinkshellId ?? 0,
                Claim = true,
                Cooldown = TodManagerViewModel.TwentyTwoHourCooldown,
                Interval = TodManagerViewModel.TenMinuteInterval
            }
        });

        return View(model);
    }

    [HttpGet]
    public IActionResult Create(int? linkshellId = null)
    {
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TodManagerViewModel model)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        model.Tod ??= new Tod { Claim = true };
        model.Tod.Cooldown = string.IsNullOrWhiteSpace(model.Tod.Cooldown)
            ? GetDefaultCooldown(model.Tod.MonsterName)
            : model.Tod.Cooldown.Trim();
        model.Tod.Interval = string.IsNullOrWhiteSpace(model.Tod.Interval)
            ? GetDefaultInterval(model.Tod.MonsterName)
            : model.Tod.Interval.Trim();

        var hasLinkshellAccess = model.Tod.LinkshellId > 0 && await HasLinkshellAccessAsync(user.Id, model.Tod.LinkshellId);
        var linkshellCharacterNames = hasLinkshellAccess
            ? await _context.AppUserLinkshells
                .Where(link => link.LinkshellId == model.Tod.LinkshellId)
                .Select(link => link.CharacterName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToListAsync()
            : new List<string>();

        ValidateTodSubmission(model, hasLinkshellAccess, linkshellCharacterNames);
        if (!ModelState.IsValid)
        {
            return View(nameof(Index), await BuildViewModelAsync(user, model));
        }

        var todTimeUtc = ConvertUserTimeZoneToUtc(model.Tod.Time, user.TimeZone);
        var occurredAtUtc = DateTime.UtcNow;
        var newTod = new Tod
        {
            MonsterName = model.Tod.MonsterName?.Trim(),
            DayNumber = model.Tod.DayNumber,
            Claim = model.Tod.Claim,
            Time = todTimeUtc,
            Cooldown = model.Tod.Cooldown,
            RepopTime = todTimeUtc?.AddHours(ResolveCooldownHours(model.Tod.Cooldown)),
            Interval = model.Tod.Interval,
            LinkshellId = model.Tod.LinkshellId,
            TimeStamp = occurredAtUtc,
            TotalTods = 1,
            TotalClaims = model.Tod.Claim ? 1 : 0
        };

        _context.Tods.Add(newTod);
        await _context.SaveChangesAsync();

        var normalizedLootDetails = model.Tod.Claim && !model.NoLoot
            ? NormalizeLootDetails(model.TodLootDetails)
            : new List<TodLootDetail>();
        if (normalizedLootDetails.Count > 0)
        {
            foreach (var lootDetail in normalizedLootDetails)
            {
                lootDetail.TodId = newTod.Id;
            }

            await _context.TodLootDetails.AddRangeAsync(normalizedLootDetails);
            await ApplyLootDkpAsync(newTod, normalizedLootDetails, occurredAtUtc);
            await _context.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Index), new { linkshellId = newTod.LinkshellId });
    }

    [HttpGet]
    public async Task<IActionResult> GetLootDetails(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var tod = await _context.Tods
            .AsNoTracking()
            .Include(item => item.TodLootDetails)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (tod is null)
        {
            return NotFound();
        }

        if (!await HasLinkshellAccessAsync(user.Id, tod.LinkshellId))
        {
            return Forbid();
        }

        return Json(tod.TodLootDetails
            .OrderBy(detail => detail.Id)
            .Select(detail => new
            {
                itemName = detail.ItemName,
                itemWinner = detail.ItemWinner,
                winningDkpSpent = detail.WinningDkpSpent
            }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var tod = await _context.Tods
            .Include(item => item.TodLootDetails)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (tod is null)
        {
            return NotFound();
        }

        if (!await HasLinkshellAccessAsync(user.Id, tod.LinkshellId))
        {
            return Forbid();
        }

        await ReverseLootDkpAsync(tod, tod.TodLootDetails, DateTime.UtcNow);
        _context.TodLootDetails.RemoveRange(tod.TodLootDetails);
        _context.Tods.Remove(tod);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index), new { linkshellId = tod.LinkshellId });
    }

    private async Task<TodManagerViewModel> BuildViewModelAsync(AppUser user, TodManagerViewModel? source = null)
    {
        var linkshells = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.Linkshell!)
            .OrderBy(link => link.LinkshellName)
            .ToListAsync();

        var selectedLinkshellId = source?.Tod?.LinkshellId > 0
            ? source.Tod.LinkshellId
            : source?.LinkshellId > 0
                ? source.LinkshellId
                : user.PrimaryLinkshellId ?? linkshells.FirstOrDefault()?.Id ?? 0;

        var characterNames = selectedLinkshellId > 0
            ? await _context.AppUserLinkshells
                .Where(link => link.LinkshellId == selectedLinkshellId)
                .Select(link => link.CharacterName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name)
                .ToListAsync()
            : new List<string>();

        var todEntities = selectedLinkshellId > 0
            ? await _context.Tods
                .AsNoTracking()
                .Include(item => item.TodLootDetails)
                .Where(item => item.LinkshellId == selectedLinkshellId)
                .OrderByDescending(item => item.Time)
                .ThenByDescending(item => item.Id)
                .ToListAsync()
            : new List<Tod>();

        var todDraft = source?.Tod ?? new Tod();
        if (todDraft.LinkshellId == 0)
        {
            todDraft.LinkshellId = selectedLinkshellId;
        }

        todDraft.Claim = source?.Tod?.Claim ?? todDraft.Claim;
        todDraft.Cooldown = string.IsNullOrWhiteSpace(todDraft.Cooldown)
            ? GetDefaultCooldown(todDraft.MonsterName)
            : todDraft.Cooldown;
        todDraft.Interval = string.IsNullOrWhiteSpace(todDraft.Interval)
            ? GetDefaultInterval(todDraft.MonsterName)
            : todDraft.Interval;

        var lootDetails = source?.TodLootDetails?.Count > 0
            ? source.TodLootDetails
            : new List<TodLootDetail> { new() };

        return new TodManagerViewModel
        {
            LinkshellId = selectedLinkshellId,
            Linkshells = linkshells,
            Tod = todDraft,
            TodItems = todEntities.Select(item => new TodTableRowViewModel
            {
                Id = item.Id,
                MonsterName = item.MonsterName ?? string.Empty,
                DayNumber = item.DayNumber,
                TodDisplay = ConvertUtcToUserTimeZone(item.Time, user.TimeZone)?.ToString("M/d/yyyy h:mm:ss tt") ?? "-",
                Cooldown = item.Cooldown ?? string.Empty,
                RepopTimeDisplay = ConvertUtcToUserTimeZone(item.RepopTime, user.TimeZone)?.ToString("M/d/yyyy h:mm:ss tt") ?? "-",
                Interval = item.Interval ?? string.Empty,
                RepopTimeUtc = item.RepopTime,
                Claim = item.Claim,
                LootCount = item.TodLootDetails.Count
            }).ToList(),
            TodLootDetails = lootDetails,
            NoLoot = source?.NoLoot ?? false,
            Notifications = source?.Notifications ?? new List<string>(),
            MonsterOptions = TodManagerViewModel.SupportedMonsters.ToList(),
            CooldownOptions = TodManagerViewModel.SupportedCooldowns.ToList(),
            IntervalOptions = TodManagerViewModel.SupportedIntervals.ToList(),
            CharacterNames = characterNames
        };
    }

    private void ValidateTodSubmission(TodManagerViewModel model, bool hasLinkshellAccess, IReadOnlyCollection<string> validCharacterNames)
    {
        if (model.Tod.LinkshellId <= 0 || !hasLinkshellAccess)
        {
            ModelState.AddModelError("Tod.LinkshellId", "Select a linkshell you can access.");
        }

        if (string.IsNullOrWhiteSpace(model.Tod.MonsterName) || !SupportedMonsters.Contains(model.Tod.MonsterName.Trim()))
        {
            ModelState.AddModelError("Tod.MonsterName", "Select a valid monster.");
        }

        if (!model.Tod.Time.HasValue)
        {
            ModelState.AddModelError("Tod.Time", "Enter a Time of Death.");
        }

        if (string.IsNullOrWhiteSpace(model.Tod.Cooldown) || !SupportedCooldowns.Contains(model.Tod.Cooldown.Trim()))
        {
            ModelState.AddModelError("Tod.Cooldown", "Select a valid cooldown.");
        }

        if (string.IsNullOrWhiteSpace(model.Tod.Interval) || !SupportedIntervals.Contains(model.Tod.Interval.Trim()))
        {
            ModelState.AddModelError("Tod.Interval", "Select a valid interval.");
        }

        for (var index = 0; index < model.TodLootDetails.Count; index++)
        {
            var lootDetail = model.TodLootDetails[index];
            var hasAnyValue = !string.IsNullOrWhiteSpace(lootDetail.ItemName)
                || !string.IsNullOrWhiteSpace(lootDetail.ItemWinner)
                || lootDetail.WinningDkpSpent.HasValue;
            if (!hasAnyValue)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(lootDetail.ItemName))
            {
                ModelState.AddModelError($"TodLootDetails[{index}].ItemName", "Enter an item name.");
            }

            if (string.IsNullOrWhiteSpace(lootDetail.ItemWinner))
            {
                ModelState.AddModelError($"TodLootDetails[{index}].ItemWinner", "Select an item winner.");
            }
            else if (!validCharacterNames.Contains(lootDetail.ItemWinner.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                ModelState.AddModelError($"TodLootDetails[{index}].ItemWinner", "Select a winner from the current linkshell.");
            }

            if (!lootDetail.WinningDkpSpent.HasValue || lootDetail.WinningDkpSpent <= 0)
            {
                ModelState.AddModelError($"TodLootDetails[{index}].WinningDkpSpent", "Enter DKP spent as a positive number.");
            }
        }
    }

    private static List<TodLootDetail> NormalizeLootDetails(IEnumerable<TodLootDetail>? lootDetails)
    {
        return lootDetails?
            .Where(detail =>
                !string.IsNullOrWhiteSpace(detail.ItemName)
                || !string.IsNullOrWhiteSpace(detail.ItemWinner)
                || detail.WinningDkpSpent.HasValue)
            .Select(detail => new TodLootDetail
            {
                ItemName = detail.ItemName?.Trim(),
                ItemWinner = detail.ItemWinner?.Trim(),
                WinningDkpSpent = detail.WinningDkpSpent
            })
            .ToList() ?? new List<TodLootDetail>();
    }

    private async Task ApplyLootDkpAsync(Tod tod, IEnumerable<TodLootDetail> lootDetails, DateTime occurredAtUtc)
    {
        await AdjustLootDkpAsync(tod, lootDetails, occurredAtUtc, isRefund: false);
    }

    private async Task ReverseLootDkpAsync(Tod tod, IEnumerable<TodLootDetail> lootDetails, DateTime occurredAtUtc)
    {
        await AdjustLootDkpAsync(tod, lootDetails, occurredAtUtc, isRefund: true);
    }

    private async Task AdjustLootDkpAsync(Tod tod, IEnumerable<TodLootDetail> lootDetails, DateTime occurredAtUtc, bool isRefund)
    {
        var actionableLoot = lootDetails
            .Where(detail => !string.IsNullOrWhiteSpace(detail.ItemWinner) && detail.WinningDkpSpent.GetValueOrDefault() > 0)
            .ToList();
        if (actionableLoot.Count == 0)
        {
            return;
        }

        var winnerNames = actionableLoot
            .Select(detail => detail.ItemWinner!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var memberships = await _context.AppUserLinkshells
            .Where(link => link.LinkshellId == tod.LinkshellId && link.AppUserId != null && winnerNames.Contains(link.CharacterName!))
            .ToListAsync();

        var membershipsByCharacterName = memberships
            .Where(link => !string.IsNullOrWhiteSpace(link.CharacterName) && !string.IsNullOrWhiteSpace(link.AppUserId))
            .GroupBy(link => link.CharacterName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (membershipsByCharacterName.Count == 0)
        {
            return;
        }

        var appUserIds = membershipsByCharacterName.Values
            .Select(link => link.AppUserId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nextSequenceByAppUserId = await _context.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == tod.LinkshellId && entry.AppUserId != null && appUserIds.Contains(entry.AppUserId))
            .GroupBy(entry => entry.AppUserId!)
            .Select(group => new { AppUserId = group.Key, NextSequence = group.Max(entry => entry.Sequence) + 1 })
            .ToDictionaryAsync(item => item.AppUserId, item => item.NextSequence, StringComparer.OrdinalIgnoreCase);

        var ledgerEntries = new List<DkpLedgerEntry>();
        foreach (var detail in actionableLoot)
        {
            if (!membershipsByCharacterName.TryGetValue(detail.ItemWinner!.Trim(), out var membership) || string.IsNullOrWhiteSpace(membership.AppUserId))
            {
                continue;
            }

            var dkpValue = detail.WinningDkpSpent.GetValueOrDefault();
            var amount = isRefund ? dkpValue : -dkpValue;
            membership.LinkshellDkp = (membership.LinkshellDkp ?? 0d) + amount;

            var currentSequence = nextSequenceByAppUserId.GetValueOrDefault(membership.AppUserId, 1);
            nextSequenceByAppUserId[membership.AppUserId] = currentSequence + 1;

            ledgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = membership.AppUserId,
                LinkshellId = tod.LinkshellId,
                EntryType = isRefund ? "LootRefund" : "LootSpent",
                Amount = amount,
                Sequence = currentSequence,
                OccurredAt = occurredAtUtc,
                CharacterName = membership.CharacterName,
                ItemName = detail.ItemName,
                Details = isRefund
                    ? $"Refunded DKP for deleted ToD loot on {tod.MonsterName ?? "Unknown monster"}."
                    : $"DKP spent on ToD loot from {tod.MonsterName ?? "Unknown monster"}."
            });
        }

        if (ledgerEntries.Count > 0)
        {
            await _context.DkpLedgerEntries.AddRangeAsync(ledgerEntries);
        }
    }

    private async Task<AppUser?> RequireCurrentUserAsync()
    {
        return await _userManager.GetUserAsync(User);
    }

    private async Task<bool> HasLinkshellAccessAsync(string userId, int linkshellId)
    {
        return await _context.AppUserLinkshells.AnyAsync(link => link.AppUserId == userId && link.LinkshellId == linkshellId);
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
    {
        if (!utcDateTime.HasValue)
        {
            return null;
        }

        var zone = ResolveTimeZone(timeZoneId);
        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utcDateTime.Value, DateTimeKind.Utc));
        return instant.InZone(zone).ToDateTimeUnspecified();
    }

    private DateTime? ConvertUserTimeZoneToUtc(DateTime? localDateTime, string? timeZoneId)
    {
        if (!localDateTime.HasValue)
        {
            return null;
        }

        var zone = ResolveTimeZone(timeZoneId);
        var zonedDateTime = zone.AtLeniently(LocalDateTime.FromDateTime(localDateTime.Value));
        return zonedDateTime.ToDateTimeUtc();
    }

    private DateTimeZone ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && _dateTimeZoneProvider.Ids.Contains(timeZoneId))
        {
            return _dateTimeZoneProvider[timeZoneId];
        }

        return DateTimeZone.Utc;
    }

    private static double ResolveCooldownHours(string? cooldown)
    {
        return string.Equals(cooldown, TodManagerViewModel.SeventyTwoHourCooldown, StringComparison.OrdinalIgnoreCase)
            ? 72d
            : 22d;
    }

    private static string GetDefaultCooldown(string? monsterName)
    {
        return !string.IsNullOrWhiteSpace(monsterName) && LongWindowMonsters.Contains(monsterName.Trim())
            ? TodManagerViewModel.SeventyTwoHourCooldown
            : TodManagerViewModel.TwentyTwoHourCooldown;
    }

    private static string GetDefaultInterval(string? monsterName)
    {
        return !string.IsNullOrWhiteSpace(monsterName) && LongWindowMonsters.Contains(monsterName.Trim())
            ? TodManagerViewModel.OneHourInterval
            : TodManagerViewModel.TenMinuteInterval;
    }
}