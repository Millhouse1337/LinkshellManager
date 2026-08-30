using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
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
    //
    // Returns null when the DKP was applied, or a human-readable error when the batch would
    // overdraw a winner — same contract as LootDkpGuard.CheckEventLootAsync, which is what event
    // loot has always used. Nothing is appended when an error is returned. ToD loot went years
    // without this check, which is how members ended up with negative balances.
    internal static async Task<string?> AdjustTodLootDkpAsync(
        ApplicationDbContext dbContext,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        Tod tod,
        IReadOnlyList<TodLootDetail> lootDetails,
        DateTime occurredAtUtc,
        bool isRefund,
        CancellationToken cancellationToken,
        // Run the affordability pre-pass and return its verdict WITHOUT appending anything. For
        // callers that must persist a parent row before they can charge loot (submission approval)
        // and would otherwise be left with an orphaned ToD when the batch is rejected.
        bool checkOnly = false)
    {
        var actionableLoot = lootDetails
            .Where(detail => !string.IsNullOrWhiteSpace(detail.ItemWinner) && detail.WinningDkpSpent.GetValueOrDefault() > 0)
            .ToList();
        if (actionableLoot.Count == 0)
        {
            return null;
        }

        var linkshell = tod.Linkshell ?? await dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(ls => ls.Id == tod.LinkshellId, cancellationToken);

        var structure = NormalizeLootStructure(linkshell?.LootStructure ?? "Dkp");
        if (structure == "LootCouncil")
        {
            // Loot council linkshells skip DKP math entirely.
            return null;
        }
        var isHybrid = structure == "Hybrid";
        var roundingStep = DkpRounding.StepFor(linkshell?.DkpRoundingIncrement);

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
            return null;
        }

        // A ToD has a monster, not an event type, so its pool can't be derived — it's PINNED.
        // New loot defaults to whichever pool "HNM" maps to (a linkshell that separates HNM DKP
        // wants ToD loot paid from it) and is stamped onto the loot row. A refund then reads that
        // stamp back, so removing loot always credits the pool it was taken from — even if the
        // officer has remapped event types in between.
        var map = await dkpPools.GetMapAsync(tod.LinkshellId, cancellationToken);
        var hnmPoolId = map.Resolve("HNM");

        // Affordability pre-pass — runs BEFORE a single row is appended, so a rejected batch
        // leaves nothing half-charged and the officer gets a message naming the member and the
        // shortfall instead of DkpLedgerWriter's backstop exception.
        //
        // Costs are summed PER WALLET, so two items won by the same member in one ToD can't each
        // pass individually and together overdraw.
        //
        // Two exemptions: refunds are credits, and a Hybrid debit is a percentage of a balance
        // already clamped at zero (ComputeHybridDebit below), so it can shrink a wallet but never
        // push it under.
        if (!isRefund && !isHybrid)
        {
            var plannedByWallet = new Dictionary<(int MembershipId, int PoolId), double>();
            foreach (var detail in actionableLoot)
            {
                if (!membershipsByCharacterName.TryGetValue(detail.ItemWinner!.Trim(), out var winner)
                    || string.IsNullOrWhiteSpace(winner.AppUserId))
                {
                    continue;
                }
                var key = (winner.Id, detail.DkpPoolId ?? hnmPoolId);
                plannedByWallet[key] = plannedByWallet.GetValueOrDefault(key)
                    + detail.WinningDkpSpent.GetValueOrDefault();
            }

            foreach (var (key, planned) in plannedByWallet)
            {
                var winner = memberships.First(link => link.Id == key.MembershipId);
                var available = await dkpLedger.GetPoolBalanceAsync(winner, key.PoolId, cancellationToken);
                if (planned > available + DkpLedgerWriter.OverdraftEpsilon)
                {
                    // Name the pool only when the linkshell actually has more than one — mirrors
                    // the wording LootDkpGuard has always used for event loot.
                    var poolLabel = map.HasMultiplePools ? $" {map.NameFor(key.PoolId)}" : string.Empty;
                    return $"{winner.CharacterName} only has {available:0.##}{poolLabel} DKP available "
                        + $"— not enough for this loot ({planned:0.##} DKP).";
                }
            }
        }

        if (checkOnly)
        {
            return null;
        }

        foreach (var detail in actionableLoot)
        {
            if (!membershipsByCharacterName.TryGetValue(detail.ItemWinner!.Trim(), out var winnerMembership) || string.IsNullOrWhiteSpace(winnerMembership.AppUserId))
            {
                continue;
            }

            var poolId = detail.DkpPoolId ?? (isRefund ? map.DefaultPoolId : hnmPoolId);
            var rawValue = detail.WinningDkpSpent.GetValueOrDefault();
            double amount;
            string detailsText;
            if (isHybrid)
            {
                var pct = Math.Clamp((double)rawValue, 0, 100);
                if (isRefund)
                {
                    if (detail.ActualDeductedDkp.HasValue)
                    {
                        amount = detail.ActualDeductedDkp.Value;
                        detailsText = $"Refunded Hybrid DKP ({pct}%, {amount:0.##} DKP) for removed ToD loot on {tod.MonsterName ?? "Unknown monster"}.";
                    }
                    else
                    {
                        // Legacy approximation when the deducted amount wasn't stored. That debit
                        // was taken as a percentage of the member's TOTAL (pools didn't exist yet),
                        // so the inverse has to work off the total too or it under-refunds.
                        if (pct >= 100d)
                        {
                            continue;
                        }
                        var legacyBalance = Math.Max(0, winnerMembership.LinkshellDkp ?? 0);
                        amount = LootDkpCalculator.ComputeHybridRefund(legacyBalance, pct, roundingStep);
                        detailsText = $"Refunded Hybrid DKP ({pct}%) for removed ToD loot on {tod.MonsterName ?? "Unknown monster"}.";
                    }
                }
                else
                {
                    // Hybrid takes a percentage of a wallet — and the wallet is the pool.
                    var poolBalance = Math.Max(0, await dkpLedger.GetPoolBalanceAsync(winnerMembership, poolId, cancellationToken));
                    amount = -LootDkpCalculator.ComputeHybridDebit(poolBalance, pct, roundingStep);
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

            if (!isRefund)
            {
                detail.DkpPoolId = poolId;
            }

            await dkpLedger.AppendAsync(
                winnerMembership,
                isRefund ? "LootRefund" : "LootSpent",
                amount,
                occurredAtUtc,
                DkpPoolRef.Pinned(poolId),
                new DkpEntryContext(
                    CharacterName: winnerMembership.CharacterName,
                    // Surface the monster name in the Event/Context column. Without this the cell
                    // renders blank because ToD loot has no parent Event row to source it from.
                    // Type and location stay null so the Activity's conditional subtitle stays
                    // hidden (otherwise it would render "Behemoth · ToD · Unknown location").
                    EventName: tod.MonsterName,
                    ItemName: detail.ItemName,
                    Details: detailsText,
                    // Never set before, which left Loot History unable to trace a ToD ledger row
                    // back to the loot that produced it.
                    SourceTodLootDetailId: detail.Id > 0 ? detail.Id : null),
                cancellationToken);
        }

        return null;
    }

    // Tod.Cooldown is stored as a human label ("22 Hour", "45 Min") because a dozen surfaces print
    // it verbatim. This turns it back into the number RepopTime is computed from.
    //
    // Every preset the form used to offer parses through the shared parser identically ("84 Hour"
    // -> 84, "5 Min" -> 5/60), so the old preset-by-preset chain is gone rather than kept beside
    // it. A bare number still means HOURS, which is the unit this field has always been written in.
    internal static double ResolveTodCooldownHours(string? cooldown) =>
        TodDurationFormat.TryParseMinutes(cooldown, TodDurationFormat.HoursUnit, out var minutes)
            ? minutes / 60d
            : 22d;

    // Cooldowns and intervals are free-form now that each monster carries its own configured
    // value: anything the shared parser reads as a positive duration is acceptable. The preset
    // lists are still what the pickers OFFER, they are just no longer what validation ALLOWS.
    internal static bool IsAcceptableTodCooldown(string? cooldown) =>
        TodDurationFormat.TryParseMinutes(cooldown, TodDurationFormat.HoursUnit, out _);

    // A bare number in an interval means MINUTES — the opposite default to a cooldown, matching
    // how the two fields have always been written. The old "minutes must be < 60" cap is gone: it
    // existed because an interval was always an (hours, minutes) pair, and a configured cadence of
    // 90 minutes is now a legitimate answer.
    internal static bool IsAcceptableTodInterval(string? interval) =>
        TodDurationFormat.TryParseMinutes(interval, TodDurationFormat.MinutesUnit, out _);

    // The GLOBAL default cooldown/interval for a monster, as a label. MonsterTimingDefaults owns
    // the underlying numbers so the seeded table and these fallbacks cannot disagree; these are
    // only the formatting wrappers.
    //
    // Prefer the per-linkshell overloads below — these are the answer for a linkshell that has
    // never configured anything.
    internal static string GetDefaultTodCooldown(string? monsterName) =>
        MonsterTimingDefaults.DefaultCooldownLabel(monsterName);

    internal static string GetDefaultTodInterval(string? monsterName) =>
        MonsterTimingDefaults.DefaultIntervalLabel(monsterName);

    // The per-linkshell defaults, which is what every ToD-posting path should use: the addon, the
    // Discord End Camp button and the web form all ignored the linkshell's configured cooldown
    // before this existed, because the config only ever reached the Activity's client.
    internal static async Task<string> GetDefaultTodCooldownAsync(
        MonsterTimingResolver resolver, int linkshellId, string? monsterName, CancellationToken cancellationToken)
    {
        var timing = await resolver.ResolveAsync(linkshellId, monsterName, cancellationToken);
        return TodDurationFormat.Format(timing.CooldownMinutes);
    }

    internal static async Task<string> GetDefaultTodIntervalAsync(
        MonsterTimingResolver resolver, int linkshellId, string? monsterName, CancellationToken cancellationToken)
    {
        var timing = await resolver.ResolveAsync(linkshellId, monsterName, cancellationToken);
        return TodDurationFormat.Format(timing.TodIntervalMinutes);
    }

    // "Popped on window" off the ToD forms. Windows are 1-based, so 0/negative (or a blanked
    // input) means the officer didn't record one.
    private static int? NormalizePopWindow(int? popWindow)
    {
        return popWindow is > 0 ? popWindow : null;
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
