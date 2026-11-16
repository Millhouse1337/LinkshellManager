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
// (OutsideSignupHandler), which keys the member to the player's Discord id so
// later signups auto-recognize them.
//
// Names are written as the account's characters (CharacterWriter); the save copies the main onto
// the account's CharacterName. A name the account already has is skipped.
public sealed class ManualMemberService
{
    public const int MaxCharacterNameLength = CharacterWriter.MaxNameLength;

    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly PlaceholderClaimRequestService _claimRequests;

    public ManualMemberService(
        ApplicationDbContext context, UserManager<AppUser> userManager, PlaceholderClaimRequestService claimRequests)
    {
        _context = context;
        _userManager = userManager;
        _claimRequests = claimRequests;
    }

    public sealed record CreateResult(bool Success, string? Error, string? AppUserId);

    // Self-service path from the Discord board: a player who isn't synced registers
    // themselves (main + optional alts), keyed to their Discord id so later signups
    // auto-recognize them. Find-or-create by MAIN name. A name already on the roster is
    // never handed over here: the form is open to everyone in the Discord server and
    // names are public, so an unlinked placeholder of that name becomes a claim request an
    // officer approves (PlaceholderClaimRequest), and a synced member or a placeholder
    // linked to another Discord user is refused. Re-sending the form for the placeholder
    // it already made for this Discord user just reuses it.
    //
    // discordDisplayName: shown to officers on the claim request so they know who asked.
    public async Task<CreateResult> FindOrCreateForOutsideAsync(
        int linkshellId, string? mainName, string? alt1, string? alt2, string discordUserId, CancellationToken cancellationToken,
        string? discordDisplayName = null)
    {
        var main = Truncate(mainName);
        if (main is null)
        {
            return new CreateResult(false, "Enter your main character name to register.", null);
        }
        // Alts are OPTIONAL — the main name is the only required field. a1/a2 may be
        // null here, and the find/create logic below only writes them when present.
        var a1 = Truncate(alt1);
        var a2 = Truncate(alt2);

        var members = await _context.AppUserLinkshells
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId && m.AppUser != null && m.AppUser.CharacterName != null)
            .ToListAsync(cancellationToken);
        var match = members.FirstOrDefault(m =>
            string.Equals(m.AppUser!.CharacterName!.Trim(), main, StringComparison.OrdinalIgnoreCase));
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
            if (string.IsNullOrEmpty(match.DiscordUserId))
            {
                // Someone else's roster spot until an officer says otherwise (an imported member,
                // with their DKP). The player is told to sign up again once it is approved.
                await _claimRequests.RequestForDiscordAsync(
                    linkshellId, match.AppUserId!, discordUserId, discordDisplayName ?? main, cancellationToken);
                return new CreateResult(false,
                    $"\"{main}\" is already on this linkshell's roster. An officer has been asked to confirm that's you - "
                    + "once they approve it, press Sign Up again.",
                    null);
            }
            if (match.AppUserId is not null)
            {
                // Their own placeholder: the alts they typed replace the ones it has.
                var characters = await CharacterWriter.LoadAsync(_context, match.AppUserId, cancellationToken);
                characters.TrySetName(Character.Alt1Slot, a1);
                characters.TrySetName(Character.Alt2Slot, a2);
            }
            await _context.SaveChangesAsync(cancellationToken);
            return new CreateResult(true, null, match.AppUserId);
        }

        var (placeholder, error) = await CreatePlaceholderAsync(main, a1, a2);
        if (placeholder is null)
        {
            return new CreateResult(false, error, null);
        }

        _context.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = placeholder.Id,
            LinkshellId = linkshellId,
            DiscordUserId = discordUserId,
            // Made just now FOR this Discord user, so nobody else's DKP is in it: their first LSM
            // launch may take it over (DiscordIdentityService).
            DiscordIdConfirmed = true,
            LinkshellDkp = 0,
            DateJoined = DateTime.UtcNow,
            Rank = LinkshellRanks.Member,
            Status = "Active",
        });
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateResult(true, null, placeholder.Id);
    }

    // Officer-initiated path (the DKP import — a migrating linkshell's sheet, with Alt 1 / Alt 2
    // columns): find-or-create a roster member by MAIN name with NO Discord link — the officer
    // is bringing in other people, so we must not attach the officer's Discord id. Matches an
    // existing member of that name (synced OR unsynced) and otherwise creates an unsynced
    // placeholder so the AppUserId-keyed DKP / history system can track them. Unlike
    // FindOrCreateForOutsideAsync this never rejects a synced match. On a name match, fills only
    // alts that are currently empty (never clobbers existing data); on create, seeds them.
    //
    // The board's officer "Add Member" no longer comes here: it only FINDS existing members
    // (RosterMemberSearch), because a typed name that missed by a symbol created a duplicate.
    public async Task<CreateResult> FindOrCreateByNameAsync(
        int linkshellId, string? mainName, string? alt1, string? alt2, CancellationToken cancellationToken)
    {
        var main = Truncate(mainName);
        if (main is null)
        {
            return new CreateResult(false, "Enter a character name to add.", null);
        }
        var a1 = Truncate(alt1);
        var a2 = Truncate(alt2);

        var members = await _context.AppUserLinkshells
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId != null && m.AppUser != null && m.AppUser.CharacterName != null)
            .ToListAsync(cancellationToken);
        var match = members.FirstOrDefault(m =>
            string.Equals(m.AppUser!.CharacterName!.Trim(), main, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            if (match.AppUserId is not null)
            {
                var characters = await CharacterWriter.LoadAsync(_context, match.AppUserId, cancellationToken);
                if (characters[Character.Alt1Slot] is null) { characters.TrySetName(Character.Alt1Slot, a1); }
                if (characters[Character.Alt2Slot] is null) { characters.TrySetName(Character.Alt2Slot, a2); }
                await _context.SaveChangesAsync(cancellationToken);
            }
            return new CreateResult(true, null, match.AppUserId);
        }

        var (placeholder, error) = await CreatePlaceholderAsync(main, a1, a2);
        if (placeholder is null)
        {
            return new CreateResult(false, error, null);
        }

        _context.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = placeholder.Id,
            LinkshellId = linkshellId,
            LinkshellDkp = 0,
            DateJoined = DateTime.UtcNow,
            Rank = LinkshellRanks.Member,
            Status = "Active",
        });
        await _context.SaveChangesAsync(cancellationToken);
        return new CreateResult(true, null, placeholder.Id);
    }

    // A new placeholder account with its characters, saved together. An alt that repeats the main
    // (or the other alt) is left out: one account never holds a name twice.
    private async Task<(AppUser? Account, string? Error)> CreatePlaceholderAsync(string main, string? a1, string? a2)
    {
        var placeholder = new AppUser
        {
            UserName = $"placeholder-{Guid.NewGuid():N}",
            IsPlaceholder = true,
        };
        var characters = CharacterWriter.ForNewAccount(_context, placeholder);
        characters.TrySetName(Character.MainSlot, main);
        characters.TrySetName(Character.Alt1Slot, a1);
        characters.TrySetName(Character.Alt2Slot, a2);

        var createResult = await _userManager.CreateAsync(placeholder);
        if (!createResult.Succeeded)
        {
            characters.DiscardChanges();
            return (null, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }
        return (placeholder, null);
    }

    // Trimmed and cut to the longest name the writer takes (counted as Postgres counts
    // characters), or null when blank.
    private static string? Truncate(string? value)
    {
        var trimmed = CharacterWriter.Clean(value);
        if (trimmed is null || !CharacterWriter.IsTooLong(trimmed))
        {
            return trimmed;
        }
        var cut = string.Concat(trimmed.EnumerateRunes().Take(MaxCharacterNameLength).Select(rune => rune.ToString()));
        return CharacterWriter.Clean(cut);
    }
}
