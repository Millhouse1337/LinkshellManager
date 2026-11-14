using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// Who is signing up from a Discord board, and as which character. Shared by every sign-up flow
// (slot sign-up, "Sign Up (No Slot)", the job select, Manual Check In) and by the board buttons
// that act on the clicker's own row (Withdraw, Make Me Lead, Stay Next Window, Readiness).
//
// It also owns the two steps that can interrupt a sign-up: the "which character?" picker for a
// member with alts, and the "not synced — register yourself" modal for a player with no linked
// account when the linkshell allows outside sign-ups.
public sealed class DiscordSignupIdentity : DiscordInteractionHandlerBase
{
    // Prefix for the ephemeral "which character?" select shown before signup when
    // a member has alts. Tail: "{token}:{eventId}" (+ ":{job}" for the ad-hoc job
    // flow), where token ∈ slot | slotL | join | job picks the flow to resume.
    internal const string CharPickPrefix = "evt:charpick:";

    // Prefix for the "Outside Party Signup" character-name MODAL — shown when a
    // Discord member with NO linked LSM account signs up (and the linkshell has the
    // setting on). Tail mirrors CharPickPrefix: "{token}:{eventId}[:{job}]". The
    // typed name is cached per (Discord user, event) so it's only asked once.
    internal const string OutsideNamePrefix = "evt:outsidename:";
    internal const string OutsideNameFieldId = "outside_name";
    internal const string OutsideAlt1FieldId = "outside_alt1";
    internal const string OutsideAlt2FieldId = "outside_alt2";

    private readonly ApplicationDbContext _db;
    private readonly SignupCharacterChoiceCache _charChoice;

    public DiscordSignupIdentity(
        DiscordInteractionContext interaction, ApplicationDbContext db, SignupCharacterChoiceCache charChoice)
        : base(interaction)
    {
        _db = db;
        _charChoice = charChoice;
    }

    // The identity + character name a signup handler should use, OR an Interrupt to
    // return instead (a "not a member" / "sign in" ephemeral, the alt-character
    // picker, or the outside-signup name modal). Account = AppUserId only; Outside =
    // DiscordUserId only; PlaceholderMatch = BOTH (an outside clicker whose typed name
    // matched a linkshell-only member, so it's keyed by the placeholder's AppUserId for
    // DKP but keeps the clicker's DiscordUserId so withdraw still finds the row).
    internal sealed record SignupContext(
        IActionResult? Interrupt, string? AppUserId, string? DiscordUserId, string? CharacterName)
    {
        public bool ShouldStop => Interrupt is not null;
        public static SignupContext Stop(IActionResult response) => new(response, null, null, null);
        public static SignupContext Account(string appUserId, string characterName) => new(null, appUserId, null, characterName);
        public static SignupContext Outside(string discordUserId, string characterName) => new(null, null, discordUserId, characterName);
        public static SignupContext PlaceholderMatch(string appUserId, string discordUserId, string characterName) => new(null, appUserId, discordUserId, characterName);
    }

    // The outside-signup gate, one flag for every event type. HNM used to have its own
    // (HnmOutsideSignupEnabled) on the theory that HNM boards were a different kind of
    // thing; they aren't any more, so a linkshell makes this decision once.
    internal async Task<bool> OutsideSignupAllowedAsync(Event ev, CancellationToken cancellationToken) =>
        await _db.Linkshells
            .Where(l => l.Id == ev.LinkshellId)
            .Select(l => l.OutsidePartySignupEnabled)
            .FirstOrDefaultAsync(cancellationToken);

    // Resolves who is signing up and the character name to record. Account users go
    // through the existing membership + alt-picker path. When there's no linked
    // account, the linkshell must have OutsidePartySignupEnabled; the member is then
    // identified by Discord id and named via a one-time modal (cached per event).
    // `promptTail` ("slot:42", "join:42", "job:42:War", …) lets the picker/modal
    // resume the right flow afterwards.
    internal async Task<SignupContext> ResolveSignupContextAsync(
        Event ev, string? appUserId, string promptTail, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(appUserId))
        {
            var membership = await _db.AppUserLinkshells
                .Include(link => link.AppUser)
                .FirstOrDefaultAsync(
                    link => link.LinkshellId == ev.LinkshellId && link.AppUserId == appUserId, cancellationToken);
            if (membership is null)
            {
                // A synced user signing up on a linkshell they don't belong to. If that
                // linkshell allows outside signups, add their REAL account to the roster
                // as a normal member so DKP/history land on their real identity (no
                // placeholder duplicate); otherwise they can't sign up here.
                membership = await TryJoinAsOutsideMemberAsync(ev, appUserId, cancellationToken);
                if (membership is null)
                {
                    return SignupContext.Stop(Ephemeral("You're not a member of this linkshell, so you can't sign up for its events."));
                }
            }
            if (NeedsCharacterPick(membership, ev.Id, appUserId))
            {
                return SignupContext.Stop(CharacterPicker(membership, promptTail, ev.EventName));
            }
            return SignupContext.Account(appUserId, ResolveSignupCharacter(membership, ev.Id, appUserId));
        }

        // No linked account → outside path, gated by the one Outside Party Signup setting.
        var enabled = await OutsideSignupAllowedAsync(ev, cancellationToken);
        if (!enabled || string.IsNullOrEmpty(Interaction.DiscordUserId))
        {
            // The Discord id is already guaranteed (the click handler rejects an unreadable
            // account earlier), so reaching here means the toggle is OFF for this linkshell.
            // Name it, instead of telling the player to "sign in" — which doesn't address
            // the real cause.
            return SignupContext.Stop(Ephemeral(
                "This linkshell doesn't allow account-less signups. Ask a leader to enable Outside Party Signup in the linkshell settings — or open LSM and sign in with Discord to sign up with a linked account."));
        }
        // Already registered? An "unsynced" member (placeholder) linked to this Discord
        // user is recognized automatically — attribute the signup to it (earns DKP), and
        // keep the Discord id on the row so Withdraw still works. No re-typing needed.
        var linkedMember = await _db.AppUserLinkshells
            .Include(link => link.AppUser)
            .FirstOrDefaultAsync(
                link => link.LinkshellId == ev.LinkshellId
                        && link.DiscordUserId == Interaction.DiscordUserId
                        && link.AppUserId != null
                        && link.AppUser!.IsPlaceholder,
                cancellationToken);
        if (linkedMember?.AppUserId is not null)
        {
            // Same alt-picker as a linked account, but the choice is cached under the
            // clicker's Discord id (a placeholder has no synced AppUserId of its own to
            // key on). Their alts come from the onboarding modal (AppUser.AltCharacterName1/2).
            if (NeedsCharacterPick(linkedMember, ev.Id, Interaction.DiscordUserId))
            {
                return SignupContext.Stop(CharacterPicker(linkedMember, promptTail, ev.EventName));
            }
            return SignupContext.PlaceholderMatch(
                linkedMember.AppUserId, Interaction.DiscordUserId,
                ResolveSignupCharacter(linkedMember, ev.Id, Interaction.DiscordUserId));
        }

        // Not registered yet → onboard via the "you're not synced" modal, which creates
        // + links their member on submit (then this resolves to the branch above).
        return SignupContext.Stop(OutsideOnboardModal(promptTail, ev.EventName));
    }

    // A synced user clicked Sign Up on a linkshell they aren't a member of. When that
    // linkshell has Outside Party Signup enabled, the leader has opted in to letting
    // non-members participate, so we add their REAL account to the roster as a normal
    // member (Rank Member, Active) — DKP/history accrue to their real identity, no
    // placeholder duplicate. Returns the new membership (AppUser loaded for the alt
    // picker), or null when outside signup is off (caller then rejects). Mirrors the
    // unique-index race handling used elsewhere: a concurrent click that created the
    // membership first surfaces as a DbUpdateException → re-load and use that row.
    private async Task<AppUserLinkshell?> TryJoinAsOutsideMemberAsync(
        Event ev, string appUserId, CancellationToken cancellationToken)
    {
        var enabled = await OutsideSignupAllowedAsync(ev, cancellationToken);
        if (!enabled)
        {
            return null;
        }
        var appUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == appUserId, cancellationToken);
        if (appUser is null)
        {
            return null;
        }

        var membership = new AppUserLinkshell
        {
            AppUserId = appUserId,
            LinkshellId = ev.LinkshellId,
            LinkshellDkp = 0,
            DateJoined = DateTime.UtcNow,
            CharacterName = appUser.CharacterName,
            Rank = LinkshellRanks.Member,
            Status = "Active",
        };
        _db.AppUserLinkshells.Add(membership);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueRaceRetry.IsDuplicateMembership(ex))
        {
            // The one-membership-per-linkshell index turned us away: a second click got there first.
            _db.Entry(membership).State = EntityState.Detached;
            return await _db.AppUserLinkshells
                .Include(link => link.AppUser)
                .FirstOrDefaultAsync(
                    link => link.LinkshellId == ev.LinkshellId && link.AppUserId == appUserId, cancellationToken);
        }
        membership.AppUser = appUser;
        return membership;
    }

    // Lighter identity resolution for withdraw/leave (no character name, no name
    // prompt). Null = the clicker can't act here (no account and the linkshell
    // doesn't allow outside signups).
    internal async Task<(string? AppUserId, string? DiscordUserId)?> ResolveWithdrawIdentityAsync(
        Event ev, string? appUserId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(appUserId))
        {
            return (appUserId, null);
        }
        // Outside withdraw reads the same gate as signup, so whoever could sign up can undo it.
        var enabled = await OutsideSignupAllowedAsync(ev, cancellationToken);
        if (!enabled || string.IsNullOrEmpty(Interaction.DiscordUserId))
        {
            return null;
        }
        return (null, Interaction.DiscordUserId);
    }

    // A Discord modal that ONBOARDS an outside (not-synced) player: they enter their
    // main + two alt names (all required), and on submit we create — or adopt + link — an
    // "unsynced" member for them keyed to their Discord id (so later signups recognize
    // them). custom_id carries the flow to resume. (Discord modals have a title + text
    // inputs only — no body text — so the "you're not synced" message lives in the
    // title/labels; the main field is prefilled with their Discord display name.)
    private IActionResult OutsideOnboardModal(string tail, string? eventName)
    {
        // The Discord display name is only a hint: "Mill House 🎮" is not a character name and
        // would fail the field's own limits below, which Discord enforces before submit. Keep
        // the letters that could be one, or start the box empty.
        var prefill = CharacterNameFormat.SuggestFrom(Interaction.DiscordDisplayName);

        static object NameRow(string fieldId, string label, bool required, string value, string placeholder)
        {
            // Build the text input as a dictionary so we can OMIT "value" entirely when
            // there's no prefill. Discord can reject a modal whose text input ships an
            // empty default value ("") together with min_length >= 1 (the default would
            // be shorter than the minimum). An absent value is the normal "starts empty"
            // state and is unambiguously valid — keys mirror the Discord field names 1:1.
            //
            // Lengths are the game's own (CharacterNameFormat): Discord refuses the submit
            // client-side outside them, so the letters-only rule is the only one left for the
            // server to explain.
            var input = new Dictionary<string, object?>
            {
                ["type"] = 4, // text input
                ["custom_id"] = fieldId,
                ["label"] = label,
                ["style"] = 1, // short
                ["min_length"] = required ? CharacterNameFormat.MinLength : 0,
                ["max_length"] = CharacterNameFormat.MaxLength,
                ["required"] = required,
                ["placeholder"] = placeholder,
            };
            if (!string.IsNullOrEmpty(value))
            {
                input["value"] = value;
            }
            return new
            {
                type = 1, // action row
                components = new object[] { input }
            };
        }

        return Ok(new
        {
            type = ResponseModal,
            data = new
            {
                custom_id = $"{OutsideNamePrefix}{tail}",
                title = "Not synced — register yourself", // 45-char cap
                components = new object[]
                {
                    NameRow(OutsideNameFieldId, "Your MAIN FFXI character name", true, prefill, "Enter your in game character name"),
                    NameRow(OutsideAlt1FieldId, "Alt 1 character name (optional)", false, string.Empty, "Enter your alt 1 character name"),
                    NameRow(OutsideAlt2FieldId, "Alt 2 character name (optional)", false, string.Empty, "Enter your alt 2 character name"),
                }
            }
        });
    }

    // Interrupt a signup with a "which character?" step only when it's meaningful:
    // the member has alts AND hasn't already chosen for this event. `choiceKey` is the
    // identity the pick is cached under — a linked account's AppUserId, or an unsynced
    // placeholder clicker's Discord id.
    private bool NeedsCharacterPick(AppUserLinkshell membership, int eventId, string choiceKey)
        => SignupCharacters.HasAlternatives(membership.AppUser, membership)
           && _charChoice.Peek(choiceKey, eventId) is null;

    // Forget any cached "which character" pick for this event on withdrawal, so a
    // later re-signup prompts the character picker / quick-select again instead of
    // silently reusing the prior choice. The choice may be keyed by either identity
    // (linked account → appUserId, outside signup → discordUserId), so clear both.
    internal void ClearCharacterChoice(string? appUserId, string? discordUserId, int eventId)
    {
        if (!string.IsNullOrEmpty(appUserId)) { _charChoice.Clear(appUserId, eventId); }
        if (!string.IsNullOrEmpty(discordUserId)) { _charChoice.Clear(discordUserId, eventId); }
    }

    // Ephemeral select of the member's characters (main + alts). `tail` resumes the
    // original flow after a pick (e.g. "slot:42", "slotL:42", "join:42", "job:42:Warrior").
    private IActionResult CharacterPicker(AppUserLinkshell membership, string tail, string? eventName)
    {
        var options = SignupCharacters.ForMember(membership.AppUser, membership)
            .Select(name => (object)new { label = name, value = name })
            .ToArray();
        return PickerResponse(
            EventHeading(eventName, "Which character are you signing up as?"),
            SelectRow(CharPickPrefix, tail, "Pick your character", options));
    }

    // The character name to record for a Discord signup: the member's cached pick
    // (from the character-pick step) or their main when none was chosen. `choiceKey` is
    // the account's AppUserId or the placeholder clicker's Discord id (see NeedsCharacterPick).
    private string ResolveSignupCharacter(AppUserLinkshell membership, int eventId, string choiceKey)
        => SignupCharacters.Resolve(membership.AppUser, membership, _charChoice.Peek(choiceKey, eventId));
}
