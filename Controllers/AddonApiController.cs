using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[ApiController]
[Route("api/addon")]
public sealed partial class AddonApiController : ControllerBase
{
    private const string AddonSource = "lsm-addon";

    private readonly ApplicationDbContext _dbContext;
    private readonly AddonApiAuthService _auth;
    private readonly UserManager<AppUser> _userManager;
    private readonly DiscordIdentityService _discordIdentityService;
    private readonly DiscordEventChannelQueue _eventQueue;
    private readonly GlobalSettingsService _globalSettings;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;
    private readonly MonsterTimingResolver _monsterTimings;
    private readonly WindowEventLinkService _windowEventLinks;
    private readonly LinkshellPermissions _permissions;
    private readonly ILogger<AddonApiController> _logger;

    public AddonApiController(
        ApplicationDbContext dbContext,
        AddonApiAuthService auth,
        UserManager<AppUser> userManager,
        DiscordIdentityService discordIdentityService,
        DiscordEventChannelQueue eventQueue,
        GlobalSettingsService globalSettings,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        MonsterTimingResolver monsterTimings,
        WindowEventLinkService windowEventLinks,
        LinkshellPermissions permissions,
        ILogger<AddonApiController> logger)
    {
        _dbContext = dbContext;
        _auth = auth;
        _userManager = userManager;
        _discordIdentityService = discordIdentityService;
        _eventQueue = eventQueue;
        _globalSettings = globalSettings;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
        _monsterTimings = monsterTimings;
        _windowEventLinks = windowEventLinks;
        _permissions = permissions;
        _logger = logger;
    }

    // Dual-auth resolver for the management endpoints: tries the Discord OAuth
    // bearer token first (so the Activity SPA can call them) and otherwise
    // uses ASP.NET Identity cookie auth (used by the app on the website).
    // When a bearer token is presented but fails validation, the
    // request is rejected — we do NOT silently downgrade to cookie auth, since
    // an attacker who can disrupt outbound calls to discord.com would
    // otherwise be able to force the cookie path and exploit any CSRF gap.
    private async Task<AppUser?> ResolveManagementUserAsync(CancellationToken cancellationToken)
    {
        if (TryGetManagementBearerToken(out var accessToken))
        {
            try
            {
                var localUser = await _discordIdentityService.GetCurrentLocalUserAsync(accessToken, cancellationToken);
                if (!string.IsNullOrWhiteSpace(localUser.AppUser?.Id))
                {
                    return await _userManager.FindByIdAsync(localUser.AppUser.Id);
                }
            }
            catch (DiscordApiException)
            {
                // Hard reject — never fall through to cookie auth when a
                // bearer token is presented.
            }
            return null;
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return await _userManager.GetUserAsync(User);
        }

        return null;
    }

    private bool TryGetManagementBearerToken(out string accessToken)
    {
        accessToken = string.Empty;

        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var headerValue))
        {
            return false;
        }
        if (!"Bearer".Equals(headerValue.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            return false;
        }

        // Skip addon att_ tokens — those are handled by [AddonApiAuth] on
        // separate endpoints. Only Discord OAuth tokens flow through here.
        if (headerValue.Parameter.StartsWith("att_", StringComparison.Ordinal))
        {
            return false;
        }

        accessToken = headerValue.Parameter;
        return true;
    }

    // ---------------------------------------------------------------------
    // Who may do what. Every check below goes through LinkshellPermissions, the rule the app
    // uses: no membership -> no; super-admin override -> yes; Leader -> yes; otherwise the role's
    // flag (an unknown rank falls back to the Member role). These used to be hand-written copies
    // that skipped the Leader and the override, so a Leader whose role row lacked a flag was
    // locked out of the addon while the app let them in.
    // ---------------------------------------------------------------------

    // May the person this addon token was issued to do X in this linkshell? A token with no
    // issuer cannot prove whose membership it rides on, so it gets nothing.
    internal Task<bool> TokenIssuerCanAsync(
        AddonApiToken token, int linkshellId, Func<LinkshellRole, bool> selector, CancellationToken cancellationToken)
        => _permissions.CanAsync(token.IssuedToAppUserId, linkshellId, selector, cancellationToken);

    // "Moderate" here means acting for the linkshell from the addon (camps, window rosters, DKP),
    // which is LinkshellRole.CanManageAttendanceWithAddon -- not the app's CanModerateLiveEvent.
    // The two are separate so a linkshell can hand a member the addon's attendance tools without
    // making them an officer in the app.
    internal Task<bool> TokenIssuerCanModerateAsync(
        AddonApiToken token, int linkshellId, CancellationToken cancellationToken)
        => TokenIssuerCanAsync(token, linkshellId, r => r.CanManageAttendanceWithAddon, cancellationToken);

    // Posting loot from the addon takes DKP off the winner, so it needs a loot right: the app's
    // Add loot (CanAddLoot), or the addon's officer right -- the people running the camp in game.
    internal static bool PostsAddonLoot(LinkshellRole role)
        => role.CanAddLoot || role.CanManageAttendanceWithAddon;

    private async Task<string?> ResolveTokenIssuerNameAsync(
        AddonApiToken token, CancellationToken cancellationToken)
    {
        // Null when the issuer is unknown. (It used to fall back to the token's Label, which was
        // always null: nothing ever set one.)
        if (string.IsNullOrEmpty(token.IssuedToAppUserId)) return null;
        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(m => m.LinkshellId == token.LinkshellId && m.AppUserId == token.IssuedToAppUserId, cancellationToken);
        return membership?.CharacterName;
    }

    // Shared deletion path: drop the join row, so re-posting the same attendee posts cleanly.
    private async Task RemoveWindowAttendeeRowAsync(
        AppUserEventWindow attendee, CancellationToken cancellationToken)
    {
        _dbContext.AppUserEventWindows.Remove(attendee);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // The live-camp editors the app calls on the management routes here (window DKP, open / close
    // / kill bonus, the closing tick, a Misc post's DKP and new posts, deleting a window or one
    // attendee). They used to need the Leader or Officer RANK, while the app's own routes on the
    // same camp card (unlink / delete a capture, file and post captures) asked for a permission,
    // so a custom role could do half a card. One rule now: LinkshellPermissions.EditsLiveCamp.
    // No Discord guild lock here, as before: these routes never had one.
    internal Task<bool> CanEditLiveCampAsync(
        AppUserLinkshell? membership, CancellationToken cancellationToken)
        => _permissions.CanAsync(membership, LinkshellPermissions.EditsLiveCamp, cancellationToken);

    // Can this member pair and use the game addon? The rule lives in AddonApiAuthService so the
    // Pair button, the pairing fan-out and every addon token check read the same one.
    private Task<bool> CanUseAddonAsync(
        AppUserLinkshell? membership, CancellationToken cancellationToken)
        => _auth.MemberCanUseAddonAsync(membership, cancellationToken);

    // ---------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------

    public sealed record PairRequest(string Code);

    public sealed record AddonAttendanceRequest(
        DateTime? RecordedAtUtc,
        List<AddonAttendanceEntry> Entries,
        int? WindowSequence = null,
        // The addon's currently-logged-in character name. Used to identify
        // which entry represents the token issuer so we can backfill their
        // linkshell membership / AppUser CharacterName on first attendance
        // post when those columns were left blank at signup time.
        string? SelfCharacterName = null,
        // (Auto sat here: true for the addon's armed Auto-Post captures. The feature is gone, and an
        // older addon that still sends `auto` is unaffected -- unknown JSON fields are ignored.)
        // What this window pays each attendee, from the officer's "Dkp this window" box. Rides on
        // the post because this request is what CREATES the window row — see the write in
        // PostAttendanceAsync. Null means "leave the window at whatever price it already has",
        // which is what an automatic capture and every older addon build both send.
        double? WindowDkp = null,
        // True when this is the addon's "Post Kill" roster: who was standing there when the mob
        // died, which is a different list from who sat the window. Files its own window row rather
        // than folding into the close — see EventAttendanceWindow.IsKillWindow. Defaults false, so
        // every existing caller keeps posting ordinary windows.
        bool IsKillWindow = false,
        // Which alliance this roster came from, and the identity it was recognised by.
        //
        // The addon has been sending allianceNumber on every window post since per-alliance
        // attendance landed (api.lua post_attendance -> render/callbacks_event.lua), but this record
        // had no member for it, so the binder discarded it on arrival and every window row was
        // written alliance-blind. AllianceKey is the addon's derived identity for that alliance --
        // its leader's character name where the game confirms one -- which is what lets two posters
        // in one alliance agree without anyone typing a number.
        //
        // Both null from an addon that predates this. Stored as null, never resolved to 1.
        int? AllianceNumber = null,
        string? AllianceKey = null);

    public sealed record AddonAttendanceEntry(
        string CharacterName,
        string? MainJob,
        string? SubJob,
        string? Zone);

    // A "label" in the body (nothing sends one) is ignored like any unknown JSON field.
    public sealed record CreatePairingCodeRequest(
        int LinkshellId);

    public sealed record AddonPostTodRequest(
        string MonsterName,
        DateTime? DefeatedAtUtc,
        string? CapturedMessage,
        // Tri-state: true=Claimed, false=Unclaimed, null=Not Specified
        // (auto-posted from the loot-pool flow before the user picked).
        bool? Claim,
        // Day cycle counter for day-tracked HNMs (Nidhogg/King Behemoth/
        // Aspidochelone). The addon's launcher captures this via
        // state.eventPresetDayInputs. Null/0 for non-day-tracked monsters
        // and for HNMs where the officer left the day input blank.
        int? DayNumber,
        // Which spawn window the mob popped on, stamped by the addon's ToD Tracker at the
        // moment it attributed the pop signal rather than derived afterwards — by the time a
        // ToD is posted the row has usually cycled on, so a later reading would name the wrong
        // window. Null when the tracker was not running, missed the pop, or the monster has no
        // window grid; the ToD form's "Popped on window" box is then the officer's to fill.
        int? PopWindow = null);

    public sealed record AddonUpdateTodClaimRequest(bool? Claim);

    public sealed record AddonPostLootRequest(
        string ItemName,
        string ItemWinner,
        int? WinningDkpSpent,
        // One id per drop, the same on every retry of it. A post whose id already succeeded gets
        // that first answer back and charges nobody again (AddonLootReplayGuard). Optional: an
        // older addon sends none and is handled exactly as before.
        string? ClientRequestId = null);

    // Per-character job-level array published by the LSM addon so other
    // installations can see who can equip a given drop. Index = FFXI job id
    // (0..21), value = level (0 if unleveled).
    public sealed record AddonPostJobLevelsRequest(
        string CharacterName,
        int[] JobLevels);
}
