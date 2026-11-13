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

public sealed partial class ActivityWindowEventsController
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

    // Removes an uploaded ToD screenshot from disk, but only when nothing else still shows it.
    //
    // Two rows can name the same file: create accepts any /uploads/tods/ path, and
    // "/uploads/tods/x/abc.png" lands on the same file as "/uploads/tods/abc.png" because only the
    // file name is used here. Deleting or editing one ToD must never take another ToD's (or a
    // waiting submission's) picture with it, so the check matches on the FILE NAME.
    // Call it after the row's own change is saved, so a failed save never loses the image.
    private async Task DeleteUploadedTodImageIfUnusedAsync(
        string? relativePath, int excludingTodId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || SanitizeUploadedImagePath(relativePath) is null)
        {
            return;
        }

        var fileName = Path.GetFileName(relativePath.Trim());
        if (string.IsNullOrWhiteSpace(fileName)
            || await IsTodImageUsedElsewhereAsync(_dbContext, fileName, excludingTodId, cancellationToken))
        {
            return;
        }

        HttpContext.RequestServices.GetRequiredService<TodImageUploadService>().DeleteIfExists(fileName);
    }

    // True when any ToD other than excludingTodId, or a ToD still waiting for approval, points at
    // this image file - under any path spelling that ends in it.
    internal static async Task<bool> IsTodImageUsedElsewhereAsync(
        ApplicationDbContext db, string fileName, int excludingTodId, CancellationToken cancellationToken)
    {
        var suffix = "/" + fileName;
        if (await db.Tods.AnyAsync(
                t => t.Id != excludingTodId && t.ImagePath != null && t.ImagePath.EndsWith(suffix),
                cancellationToken))
        {
            return true;
        }

        return await db.PendingTodSubmissions.AnyAsync(
            p => p.ImagePath != null && p.ImagePath.EndsWith(suffix),
            cancellationToken);
    }

    // What an edit may do with a ToD's picture: keep it, clear it, or swap in a NEW upload.
    //
    // It may not point at a file another ToD (or a waiting submission) already uses. The old
    // picture is deleted from disk when it changes, so "point at someone else's file, then clear
    // it" was a way to delete their screenshot. A new picture must also be in the plain
    // "/uploads/tods/<file>" shape the upload endpoint hands out, so it can't reach another file
    // through an extra folder. Allowed = false means refuse the edit.
    internal static async Task<(bool Allowed, string? ImagePath)> ResolveTodImageForUpdateAsync(
        ApplicationDbContext db, Tod tod, string? requestedPath, CancellationToken cancellationToken)
    {
        var sanitized = SanitizeUploadedImagePath(requestedPath);
        if (sanitized is null)
        {
            // Blank (or not an upload path at all) clears the picture, as it always has.
            return (true, null);
        }

        if (string.Equals(sanitized, tod.ImagePath?.Trim(), StringComparison.Ordinal))
        {
            return (true, tod.ImagePath);
        }

        var fileName = Path.GetFileName(sanitized);
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains('\\')
            || !string.Equals(sanitized, "/uploads/tods/" + fileName, StringComparison.Ordinal))
        {
            return (false, tod.ImagePath);
        }

        if (await IsTodImageUsedElsewhereAsync(db, fileName, tod.Id, cancellationToken))
        {
            return (false, tod.ImagePath);
        }

        return (true, sanitized);
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
