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
