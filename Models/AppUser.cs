using Microsoft.AspNetCore.Identity;

namespace LinkshellManagerDiscordApp.Models;

public class AppUser : IdentityUser
{
    // The main character's name: the account's display name. Read everywhere, but never written by
    // hand since Round 5: every save sets it to the main's (slot 0) Characters name, or null
    // (ApplicationDbContext.StampAccountsFromCharactersAsync).
    public string? CharacterName { get; set; }

    // Every character of the account (slot 0 main, 1 and 2 the alts) with its jobs and crafts: the
    // only place names, jobs and crafts live since Round 5 (written by CharacterWriter, read through
    // AccountCharacters). The account's old alt, job and craft columns were dropped by
    // Round5Contract (R5.7).
    public ICollection<Character> Characters { get; set; } = new List<Character>();

    public string? TimeZone { get; set; }

    public int? PrimaryLinkshellId { get; set; }

    // App-wide super admin. NOT linkshell-scoped — grants access to global
    // server controls (e.g. the addon kill-switch on the website's Admin tab).
    // Seeded on startup for the configured SuperAdmin account; see Program.cs.
    public bool IsSuperAdmin { get; set; }

    // A "linkshell-only" placeholder account a leader created for a player who has
    // no real login (they never launched the app). It has no password and no Discord
    // link, so it can never sign in or appear in invite search — it exists only to
    // give that player a real AppUserId so the (AppUserId-keyed) DKP/history/loot
    // systems can track them, and so an Outside Party Signup matching their character
    // name can be attributed here. See Services/ManualMemberService.
    public bool IsPlaceholder { get; set; }

    public ICollection<AppUserLinkshell> AppUserLinkshells { get; set; } = new List<AppUserLinkshell>();

    public ICollection<AppUserEvent> AppUserEvents { get; set; } = new List<AppUserEvent>();

    public ICollection<AppUserEventHistory> AppUserEventHistories { get; set; } = new List<AppUserEventHistory>();
}
