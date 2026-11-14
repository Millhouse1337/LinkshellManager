using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LinkshellManagerDiscordApp.Controllers.Discord.DiscordSignupIdentity;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// The two steps that can interrupt a sign-up, and picking the sign-up back up after them: the
// "which character?" picker for a member with alts, and the "not synced — register yourself"
// modal for a player with no linked account (Outside Party Signup). DiscordSignupIdentity decides
// when to show them; this class handles the answers.
public sealed class OutsideSignupHandler : DiscordInteractionHandlerBase
{
    private readonly ApplicationDbContext _db;
    private readonly SignupCharacterChoiceCache _charChoice;
    private readonly ManualMemberService _manualMembers;
    private readonly DiscordSignupIdentity _identity;
    private readonly PartyBoardHandler _partyBoard;
    private readonly HnmCampBoardHandler _hnmCamp;

    public OutsideSignupHandler(
        DiscordInteractionContext interaction,
        ApplicationDbContext db,
        SignupCharacterChoiceCache charChoice,
        ManualMemberService manualMembers,
        DiscordSignupIdentity identity,
        PartyBoardHandler partyBoard,
        HnmCampBoardHandler hnmCamp)
        : base(interaction)
    {
        _db = db;
        _charChoice = charChoice;
        _manualMembers = manualMembers;
        _identity = identity;
        _partyBoard = partyBoard;
        _hnmCamp = hnmCamp;
    }

    // The character picker's select: remember the pick, then resume the flow it interrupted.
    internal async Task<IActionResult> HandleCharacterPickAsync(
        string customId, JsonElement data, string? appUserId, CancellationToken cancellationToken)
    {
        var parts = customId[CharPickPrefix.Length..].Split(':');
        var token = parts.Length > 0 ? parts[0] : string.Empty;
        var pickEventId = parts.Length > 1 && int.TryParse(parts[1], out var eid) ? eid : 0;
        var chosen = SelectedValue(data);
        var job = parts.Length > 2 ? string.Join(':', parts.Skip(2)) : null;

        // The picker is shown to a linked account (keyed by AppUserId) OR to an
        // "unsynced" placeholder member (no synced AppUserId — keyed by the clicker's
        // Discord id). Cache the pick under that same identity and resume as it; a
        // placeholder resumes with appUserId:null so ResolveSignupContextAsync re-finds
        // it by Discord id (and now sees the cached pick, so it won't re-ask).
        if (!string.IsNullOrEmpty(appUserId))
        {
            if (pickEventId > 0 && !string.IsNullOrWhiteSpace(chosen))
            {
                _charChoice.Set(appUserId, pickEventId, chosen!);
            }
            return await ResumeSignupFlowAsync(token, pickEventId, appUserId, job, cancellationToken);
        }
        if (!string.IsNullOrEmpty(Interaction.DiscordUserId))
        {
            if (pickEventId > 0 && !string.IsNullOrWhiteSpace(chosen))
            {
                _charChoice.Set(Interaction.DiscordUserId, pickEventId, chosen!);
            }
            return await ResumeSignupFlowAsync(token, pickEventId, appUserId: null, job, cancellationToken);
        }
        return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
    }

    // The onboarding modal's submit. `discordUserId` is the submitter, already read from the payload.
    internal async Task<IActionResult> HandleOutsideNameModalAsync(
        string customId, JsonElement data, string discordUserId, CancellationToken cancellationToken)
    {
        var parts = customId[OutsideNamePrefix.Length..].Split(':');
        var token = parts.Length > 0 ? parts[0] : string.Empty;
        var nameEventId = parts.Length > 1 && int.TryParse(parts[1], out var nid) ? nid : 0;
        if (nameEventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == nameEventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        var isHnm = string.Equals((ev.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        var outsideEnabled = await _identity.OutsideSignupAllowedAsync(ev, cancellationToken);
        if (!outsideEnabled)
        {
            return Ephemeral(isHnm
                ? "HNM outside sign-ups aren't enabled for this event."
                : "Outside signups aren't enabled for this event.");
        }

        // Every name goes through CharacterNameFormat: letters only, 3-15 of them, and the
        // game's casing applied for the person. A name the game could never issue would sit
        // on the roster unmatched forever, so it is bounced here with a reason. The main is
        // required; an alt is checked only when something was typed in it.
        if (!CharacterNameFormat.TryNormalize(ExtractModalValue(data, OutsideNameFieldId), out var main, out var mainError))
        {
            return Ephemeral($"Main character name: {mainError}");
        }
        var alt1 = ExtractModalValue(data, OutsideAlt1FieldId)?.Trim();
        var alt2 = ExtractModalValue(data, OutsideAlt2FieldId)?.Trim();
        if (!string.IsNullOrEmpty(alt1))
        {
            if (!CharacterNameFormat.TryNormalize(alt1, out var alt1Clean, out var alt1Error))
            {
                return Ephemeral($"Alt 1 character name: {alt1Error}");
            }
            alt1 = alt1Clean;
        }
        if (!string.IsNullOrEmpty(alt2))
        {
            if (!CharacterNameFormat.TryNormalize(alt2, out var alt2Clean, out var alt2Error))
            {
                return Ephemeral($"Alt 2 character name: {alt2Error}");
            }
            alt2 = alt2Clean;
        }

        var result = await _manualMembers.FindOrCreateForOutsideAsync(
            ev.LinkshellId, main, alt1, alt2, discordUserId, cancellationToken, Interaction.DiscordDisplayName);
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't register you for this event.");
        }

        var job = parts.Length > 2 ? string.Join(':', parts.Skip(2)) : null;
        return await ResumeSignupFlowAsync(token, nameEventId, appUserId: null, job, cancellationToken);
    }

    // Resume a signup flow after an identity step (the account character picker OR
    // the outside-signup name modal). `appUserId` is null for the outside path —
    // each handler re-resolves identity from the captured Discord id.
    private Task<IActionResult> ResumeSignupFlowAsync(
        string token, int eventId, string? appUserId, string? job, CancellationToken cancellationToken)
        => token switch
        {
            "slot" => _partyBoard.HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: false, cancellationToken),
            "slotL" => _partyBoard.HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: true, cancellationToken),
            "join" => _partyBoard.StartGeneralJoinAsync(eventId, appUserId, cancellationToken),
            "job" => _partyBoard.HandleJobSignupAsync(eventId, appUserId, job, cancellationToken),
            "xin" => _hnmCamp.HandleXinAsync(eventId, appUserId, cancellationToken),
            _ => Task.FromResult(Ephemeral("That action isn't recognized."))
        };
}
