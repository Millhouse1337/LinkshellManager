using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Promotes the configured super admin on startup (called from Program.cs).
//
// The ONLY key is the owner's Discord user id (config SuperAdmin:DiscordUserId, set as
// SuperAdmin__DiscordUserId in /etc/lsmanager/env). Discord ids are immutable and can't be
// picked by anyone else. The old Email / CharacterName matching is gone on purpose: any
// user could set their main character name to the owner's and be promoted on the next
// restart, and new accounts copy their Discord display name into CharacterName.
//
// Matching goes DiscordActivityUsers.DiscordUserId -> IdentityUserId -> AppUser, the same
// link Discord sign-in uses. Idempotent: it only ever turns the flag ON. It never demotes
// anyone, so an account promoted by the old rules has to be cleared by hand (see RUNBOOK).
public static class SuperAdminSeeder
{
    public enum Outcome
    {
        // SuperAdmin:DiscordUserId is missing or not a Discord id (digits only).
        NotConfigured,
        // The id is set but no signed-in account is linked to it yet.
        NoMatch,
        // Matched; Promoted says how many accounts were newly flagged (0 = already set).
        Matched,
    }

    public sealed record Result(Outcome Outcome, int Matched, int Promoted);

    // Trimmed id when it looks like a Discord snowflake, else null. A typo (or a stray
    // name pasted into the setting) must not match anything.
    public static string? NormalizeDiscordUserId(string? raw)
    {
        var id = raw?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > 32 || !id.All(char.IsAsciiDigit))
        {
            return null;
        }
        return id;
    }

    public static async Task<Result> SeedAsync(
        ApplicationDbContext db, string? configuredDiscordUserId, CancellationToken ct = default)
    {
        var discordUserId = NormalizeDiscordUserId(configuredDiscordUserId);
        if (discordUserId is null)
        {
            return new Result(Outcome.NotConfigured, 0, 0);
        }

        var identityUserIds = await db.DiscordActivityUsers
            .Where(d => d.DiscordUserId == discordUserId && d.IdentityUserId != null)
            .Select(d => d.IdentityUserId!)
            .ToListAsync(ct);
        if (identityUserIds.Count == 0)
        {
            return new Result(Outcome.NoMatch, 0, 0);
        }

        var matches = await db.Users
            .Where(u => identityUserIds.Contains(u.Id))
            .ToListAsync(ct);
        if (matches.Count == 0)
        {
            return new Result(Outcome.NoMatch, 0, 0);
        }

        var promoted = 0;
        foreach (var user in matches.Where(u => !u.IsSuperAdmin))
        {
            user.IsSuperAdmin = true;
            promoted++;
        }
        if (promoted > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return new Result(Outcome.Matched, matches.Count, promoted);
    }
}
