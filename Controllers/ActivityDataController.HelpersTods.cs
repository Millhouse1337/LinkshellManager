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

public sealed partial class ActivityDataController
{
    internal static List<TodLootDetail> NormalizeTodLootDetails(IReadOnlyList<ActivityCreateTodLootRequest>? lootDetails)
    {
        return (lootDetails ?? Array.Empty<ActivityCreateTodLootRequest>())
            .Where(detail =>
                !string.IsNullOrWhiteSpace(detail.ItemName) ||
                !string.IsNullOrWhiteSpace(detail.ItemWinner) ||
                detail.WinningDkpSpent.HasValue)
            .Select(detail => new TodLootDetail
            {
                ItemName = detail.ItemName?.Trim(),
                ItemWinner = detail.ItemWinner?.Trim(),
                WinningDkpSpent = detail.WinningDkpSpent
            })
            .ToList();
    }

    // Refactored from instance method to static so AddonApiController can
    // share the same DKP-ledger logic without depending on an
    // ActivityDataController instance. _dbContext references became the
    // explicit `dbContext` parameter; behavior is otherwise identical.
    internal static async Task AdjustTodLootDkpAsync(
        ApplicationDbContext dbContext,
        Tod tod,
        IReadOnlyList<TodLootDetail> lootDetails,
        DateTime occurredAtUtc,
        bool isRefund,
        CancellationToken cancellationToken)
    {
        var actionableLoot = lootDetails
            .Where(detail => !string.IsNullOrWhiteSpace(detail.ItemWinner) && detail.WinningDkpSpent.GetValueOrDefault() > 0)
            .ToList();
        if (actionableLoot.Count == 0)
        {
            return;
        }

        var linkshell = tod.Linkshell ?? await dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(ls => ls.Id == tod.LinkshellId, cancellationToken);

        var structure = NormalizeLootStructure(linkshell?.LootStructure ?? "Dkp");
        if (structure == "LootCouncil")
        {
            // Loot council linkshells skip DKP math entirely.
            return;
        }
        var isHybrid = structure == "Hybrid";

        var winnerNames = actionableLoot
            .Select(detail => detail.ItemWinner!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var memberships = await dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == tod.LinkshellId && link.AppUserId != null && winnerNames.Contains(link.CharacterName!))
            .ToListAsync(cancellationToken);

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

        var nextSequenceByAppUserId = appUserIds.Count == 0
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : await dbContext.DkpLedgerEntries
                .Where(entry => entry.LinkshellId == tod.LinkshellId && entry.AppUserId != null && appUserIds.Contains(entry.AppUserId))
                .GroupBy(entry => entry.AppUserId!)
                .Select(group => new { AppUserId = group.Key, NextSequence = group.Max(entry => entry.Sequence) + 1 })
                .ToDictionaryAsync(item => item.AppUserId, item => item.NextSequence, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var ledgerEntries = new List<DkpLedgerEntry>();
        foreach (var detail in actionableLoot)
        {
            if (!membershipsByCharacterName.TryGetValue(detail.ItemWinner!.Trim(), out var winnerMembership) || string.IsNullOrWhiteSpace(winnerMembership.AppUserId))
            {
                continue;
            }

            var rawValue = detail.WinningDkpSpent.GetValueOrDefault();
            double amount;
            string detailsText;
            if (isHybrid)
            {
                var pct = Math.Clamp((double)rawValue, 0, 100);
                var currentBalance = Math.Max(0, winnerMembership.LinkshellDkp ?? 0);
                if (isRefund)
                {
                    if (detail.ActualDeductedDkp.HasValue)
                    {
                        amount = detail.ActualDeductedDkp.Value;
                        detailsText = $"Refunded Hybrid DKP ({pct}%, {amount:0.##} DKP) for removed ToD loot on {tod.MonsterName ?? "Unknown monster"}.";
                    }
                    else
                    {
                        // Legacy approximation when the deducted amount wasn't stored.
                        if (pct >= 100d)
                        {
                            continue;
                        }
                        amount = Math.Round(currentBalance * pct / (100d - pct), 2);
                        detailsText = $"Refunded Hybrid DKP ({pct}%) for removed ToD loot on {tod.MonsterName ?? "Unknown monster"}.";
                    }
                }
                else
                {
                    amount = -Math.Round(currentBalance * pct / 100d, 2);
                    detail.ActualDeductedDkp = Math.Abs(amount);
                    detailsText = $"Hybrid DKP spent ({pct}%, {Math.Abs(amount):0.##} DKP) on ToD loot from {tod.MonsterName ?? "Unknown monster"}.";
                }
            }
            else
            {
                if (isRefund)
                {
                    amount = detail.ActualDeductedDkp ?? (double)rawValue;
                    detailsText = $"Refunded DKP for deleted ToD loot on {tod.MonsterName ?? "Unknown monster"}.";
                }
                else
                {
                    amount = -(double)rawValue;
                    detail.ActualDeductedDkp = Math.Abs(amount);
                    detailsText = $"DKP spent on ToD loot from {tod.MonsterName ?? "Unknown monster"}.";
                }
            }

            winnerMembership.LinkshellDkp = (winnerMembership.LinkshellDkp ?? 0d) + amount;

            var currentSequence = nextSequenceByAppUserId.GetValueOrDefault(winnerMembership.AppUserId, 1);
            nextSequenceByAppUserId[winnerMembership.AppUserId] = currentSequence + 1;

            ledgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = winnerMembership.AppUserId,
                LinkshellId = tod.LinkshellId,
                EntryType = isRefund ? "LootRefund" : "LootSpent",
                Amount = amount,
                Sequence = currentSequence,
                OccurredAt = occurredAtUtc,
                CharacterName = winnerMembership.CharacterName,
                // Surface the monster name in the Event/Context column. Without
                // this the cell renders blank because ToD loot has no parent
                // Event row to source it from. Type and location stay null so
                // the discord activity's conditional subtitle stays hidden
                // (otherwise it would render "Behemoth Â· ToD Â· Unknown location").
                EventName = tod.MonsterName,
                ItemName = detail.ItemName,
                Details = detailsText
            });
        }

        if (ledgerEntries.Count > 0)
        {
            await dbContext.DkpLedgerEntries.AddRangeAsync(ledgerEntries, cancellationToken);
        }
    }

    internal static double ResolveTodCooldownHours(string? cooldown)
    {
        if (string.IsNullOrWhiteSpace(cooldown))
        {
            return 22d;
        }

        var trimmed = cooldown.Trim();

        if (SupportedTodCooldowns.Contains(trimmed))
        {
            if (string.Equals(trimmed, TodManagerViewModel.SeventyTwoHourCooldown, StringComparison.OrdinalIgnoreCase))
                return 72d;
            if (string.Equals(trimmed, TodManagerViewModel.TwoHourCooldown, StringComparison.OrdinalIgnoreCase))
                return 2d;
            if (string.Equals(trimmed, TodManagerViewModel.FiveMinuteCooldown, StringComparison.OrdinalIgnoreCase))
                return 5d / 60d;
            return 22d;
        }

        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\s*(\d+(?:\.\d+)?)\s*(?:Hours?|Hr|H)?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0)
        {
            return hours;
        }

        return 22d;
    }

    private static bool IsAcceptableTodCooldown(string? cooldown)
    {
        if (string.IsNullOrWhiteSpace(cooldown))
        {
            return false;
        }

        if (SupportedTodCooldowns.Contains(cooldown.Trim()))
        {
            return true;
        }

        var match = System.Text.RegularExpressions.Regex.Match(cooldown.Trim(), @"^\s*(\d+(?:\.\d+)?)\s*(?:Hours?|Hr|H)?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success
            && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hours)
            && hours > 0;
    }

    internal static string GetDefaultTodCooldown(string? monsterName)
    {
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return TodManagerViewModel.TwentyTwoHourCooldown;
        }
        var trimmed = monsterName.Trim();
        if (HnmConfig.LongWindowHnms.Contains(trimmed))
        {
            return TodManagerViewModel.SeventyTwoHourCooldown;
        }
        if (HnmConfig.SkyGods.Contains(trimmed) || HnmConfig.SeaNms.Contains(trimmed))
        {
            return TodManagerViewModel.FiveMinuteCooldown;
        }
        if (HnmConfig.SkyFarmNms.Contains(trimmed))
        {
            return TodManagerViewModel.TwoHourCooldown;
        }
        return TodManagerViewModel.TwentyTwoHourCooldown;
    }

    internal static string GetDefaultTodInterval(string? monsterName)
    {
        return !string.IsNullOrWhiteSpace(monsterName) && HnmConfig.LongWindowHnms.Contains(monsterName.Trim())
            ? TodManagerViewModel.OneHourInterval
            : TodManagerViewModel.TenMinuteInterval;
    }

    private void DeleteUploadedTodImage(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || SanitizeUploadedImagePath(relativePath) is null)
        {
            return;
        }

        var webRoot = _webHostEnvironment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRoot))
        {
            webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        }

        var fileName = Path.GetFileName(relativePath);
        var absolutePath = Path.Combine(webRoot, "uploads", "tods", fileName);
        try
        {
            if (System.IO.File.Exists(absolutePath))
            {
                System.IO.File.Delete(absolutePath);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string? SanitizeUploadedImagePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith("/uploads/tods/", StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed;
    }
}
