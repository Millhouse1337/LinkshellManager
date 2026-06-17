using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Creates an "unsynced" member: a roster entry for a player who has no real login
// (they haven't synced/linked an account). Backed by a PLACEHOLDER AppUser (no password,
// no Discord login → can never sign in or show up in invite search) so the player gets
// a real AppUserId — which lets the entire AppUserId-keyed DKP / history / loot system
// track them. Called from the Discord board's self-service onboarding modal
// (DiscordInteractionsController), which keys the member to the player's Discord id so
// later signups auto-recognize them.
public sealed class ManualMemberService
{
    public const int MaxCharacterNameLength = 64;

    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;

    public ManualMemberService(ApplicationDbContext context, UserManager<AppUser> userManager)
    {
        _context = context;
        _userManager = userManager;
    }

    public sealed record CreateResult(bool Success, string? Error, string? AppUserId);

    // Self-service path from the Discord board: a player who isn't synced registers
    // themselves (main + optional alts), keyed to their Discord id so later signups
    // auto-recognize them. Find-or-create by MAIN name: adopt+link an existing unsynced
    // member of that name (e.g. one a leader pre-created), refusing to hijack a real
    // synced member or a placeholder already linked to a different Discord user.
    public async Task<CreateResult> FindOrCreateForOutsideAsync(
        int linkshellId, string? mainName, string? alt1, string? alt2, string discordUserId, CancellationToken cancellationToken)
    {
        var main = (mainName ?? string.Empty).Trim();
        if (main.Length == 0)
        {
            return new CreateResult(false, "Enter your main character name to register.", null);
        }
        if (main.Length > MaxCharacterNameLength) { main = main[..MaxCharacterNameLength]; }
        var a1 = Truncate(alt1);
        var a2 = Truncate(alt2);
        // Both alts are required (the onboarding modal enforces this client-side; this
        // guards a malformed payload). Backstop for the registration flow only.
        if (string.IsNullOrEmpty(a1) || string.IsNullOrEmpty(a2))
        {
            return new CreateResult(false, "Enter both alt character names to register.", null);
        }

        var members = await _context.AppUserLinkshells
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId && m.CharacterName != null)
            .ToListAsync(cancellationToken);
        var match = members.FirstOrDefault(m =>
            string.Equals(m.CharacterName!.Trim(), main, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            if (match.AppUser?.IsPlaceholder != true)
            {
                return new CreateResult(false, $"\"{main}\" is a synced member — sign in with that account instead.", null);
            }
            if (!string.IsNullOrEmpty(match.DiscordUserId) && match.DiscordUserId != discordUserId)
            {
                return new CreateResult(false, $"\"{main}\" is already linked to another Discord user.", null);
            }
            match.DiscordUserId = discordUserId;
            if (match.AppUser is not null)
            {
                if (!string.IsNullOrEmpty(a1)) { match.AppUser.AltCharacterName1 = a1; }
                if (!string.IsNullOrEmpty(a2)) { match.AppUser.AltCharacterName2 = a2; }
            }
            await _context.SaveChangesAsync(cancellationToken);
            return new CreateResult(true, null, match.AppUserId);
        }

        var placeholder = new AppUser
        {
            UserName = $"placeholder-{Guid.NewGuid():N}",
            CharacterName = main,
            AltCharacterName1 = a1,
            AltCharacterName2 = a2,
            IsPlaceholder = true,
        };
        var createResult = await _userManager.CreateAsync(placeholder);
        if (!createResult.Succeeded)
        {
            return new CreateResult(false, string.Join("; ", createResult.Errors.Select(e => e.Description)), null);
        }

        _context.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = placeholder.Id,
            LinkshellId = linkshellId,
            DiscordUserId = discordUserId,
            LinkshellDkp = 0,
            DateJoined = DateTime.UtcNow,
            CharacterName = main,
            Rank = LinkshellRanks.Member,
            Status = "Active",
        });
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateResult(true, null, placeholder.Id);
    }

    private static string? Truncate(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) { return null; }
        return trimmed.Length > MaxCharacterNameLength ? trimmed[..MaxCharacterNameLength] : trimmed;
    }
}
