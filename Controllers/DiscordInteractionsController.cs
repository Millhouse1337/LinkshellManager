using System.Text;
using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LinkshellManagerDiscordApp.Controllers;

// Discord HTTP interactions endpoint (button/select clicks on event
// announcements). Discord POSTs here with an Ed25519 signature we must verify
// (it actively probes with bad signatures → fail closed with 401). Handles the
// PING handshake and MESSAGE_COMPONENT interactions, responding with a type-7
// UPDATE_MESSAGE that refreshes the event's signup roster in place. Anonymous +
// reads the raw body itself (signature is over timestamp + raw body).
[ApiController]
[AllowAnonymous]
[Route("api/discord")]
public sealed class DiscordInteractionsController : ControllerBase
{
    // Discord interaction request types.
    private const int InteractionPing = 1;
    private const int InteractionApplicationCommand = 2;
    private const int InteractionMessageComponent = 3;
    private const int InteractionModalSubmit = 5;

    // Discord interaction response types.
    private const int ResponsePong = 1;
    private const int ResponseChannelMessage = 4; // ephemeral replies
    private const int ResponseDeferredUpdate = 6;
    private const int ResponseUpdateMessage = 7;
    private const int ResponseModal = 9;
    private const int ResponseLaunchActivity = 12; // launch the Activity (no public card)
    private const int EphemeralFlag = 64;

    // Prefix for the ephemeral "which character?" select shown before signup when
    // a member has alts. Tail: "{token}:{eventId}" (+ ":{job}" for the ad-hoc job
    // flow), where token ∈ slot | slotL | join | job picks the flow to resume.
    private const string CharPickPrefix = "evt:charpick:";

    // Prefix for the "quick sign up" select that lists a member's 3 most recent
    // job combos. Tail: "{eventId}". Option values: "c|{main}|{sub}|{role}" to
    // claim a matching open slot, or "m" to fall through to the manual picker.
    private const string QuickComboPrefix = "evt:quickcombo:";

    // Prefix for the "Outside Party Signup" character-name MODAL — shown when a
    // Discord member with NO linked LSM account signs up (and the linkshell has the
    // setting on). Tail mirrors CharPickPrefix: "{token}:{eventId}[:{job}]". The
    // typed name is cached per (Discord user, event) so it's only asked once.
    private const string OutsideNamePrefix = "evt:outsidename:";
    private const string OutsideNameFieldId = "outside_name";
    private const string OutsideAlt1FieldId = "outside_alt1";
    private const string OutsideAlt2FieldId = "outside_alt2";

    private readonly DiscordInteractionVerifier _verifier;
    private readonly ApplicationDbContext _db;
    private readonly DiscordEventChannelQueue _eventQueue;
    private readonly ILogger<DiscordInteractionsController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _discordClientId;
    private readonly SignupCharacterChoiceCache _charChoice;
    private readonly ManualMemberService _manualMembers;

    // This component interaction's token, captured per request so a success
    // handler can dismiss (delete) the ephemeral picker/wizard it lives on.
    private string? _interactionToken;

    // The clicker's Discord user id + display name, captured per request so the
    // outside-signup path (no linked account) can identify and name them.
    private string? _discordUserId;
    private string? _discordDisplayName;

    // True when this component click is ON an ephemeral picker (a previous step in
    // the same signup flow). The next picker step then MORPHS that message in place
    // instead of stacking a new "Which character…/Quick sign up…" ephemeral.
    private bool _isEphemeralSource;

    public DiscordInteractionsController(
        DiscordInteractionVerifier verifier,
        ApplicationDbContext db,
        DiscordEventChannelQueue eventQueue,
        ILogger<DiscordInteractionsController> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<DiscordOAuthOptions> discordOptions,
        SignupCharacterChoiceCache charChoice,
        ManualMemberService manualMembers)
    {
        _verifier = verifier;
        _db = db;
        _eventQueue = eventQueue;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _discordClientId = discordOptions.Value.ClientId;
        _charChoice = charChoice;
        _manualMembers = manualMembers;
    }

    [HttpPost("interactions")]
    public async Task<IActionResult> HandleAsync(CancellationToken cancellationToken)
    {
        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
        }

        var signature = Request.Headers["X-Signature-Ed25519"].FirstOrDefault();
        var timestamp = Request.Headers["X-Signature-Timestamp"].FirstOrDefault();
        if (!_verifier.Verify(signature, timestamp, rawBody))
        {
            // Discord requires 401 for an invalid signature (it probes for this).
            return Unauthorized();
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return BadRequest();
        }

        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetInt32() : 0;

            if (type == InteractionPing)
            {
                return Ok(new { type = ResponsePong });
            }

            if (type == InteractionMessageComponent)
            {
                return await HandleComponentAsync(root, cancellationToken);
            }

            if (type == InteractionModalSubmit)
            {
                return await HandleModalSubmitAsync(root, cancellationToken);
            }

            // The Activity's entry-point "Launch" command (now app-handled, so the
            // public "Game Invitation" card is suppressed): just launch the Activity
            // for the clicker with a quiet LAUNCH_ACTIVITY callback — no channel post.
            if (type == InteractionApplicationCommand)
            {
                return Ok(new { type = ResponseLaunchActivity, data = new { } });
            }

            // Unhandled interaction type — acknowledge with a no-op deferred
            // update so Discord doesn't surface an error to the user.
            return Ok(new { type = ResponseDeferredUpdate });
        }
    }

    private async Task<IActionResult> HandleComponentAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("custom_id", out var customIdEl)
            || customIdEl.GetString() is not { Length: > 0 } customId)
        {
            return Ephemeral("That action isn't recognized.");
        }

        // Captured so a successful signup can silently dismiss the ephemeral
        // picker/wizard it ran in (delete it) instead of leaving a confirmation.
        _interactionToken = root.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() : null;

        var discordUserId = ResolveDiscordUserId(root);
        if (string.IsNullOrEmpty(discordUserId))
        {
            return Ephemeral("Couldn't read your Discord account from that click.");
        }
        // Captured for the outside-signup path (identity + a name modal prefill).
        _discordUserId = discordUserId;
        _discordDisplayName = ResolveDiscordDisplayName(root);

        // Did this click come from an ephemeral picker (vs the public board)? If so,
        // the next picker step morphs it in place instead of stacking a new message.
        _isEphemeralSource = root.TryGetProperty("message", out var srcMsg)
            && srcMsg.TryGetProperty("flags", out var srcFlags)
            && srcFlags.ValueKind == JsonValueKind.Number
            && (srcFlags.GetInt32() & EphemeralFlag) != 0;

        // Resolve the LSM account linked to this Discord user. This MAY be null:
        // a member who has never launched the app has no link. We no longer reject
        // here — each flow resolves identity against its event's linkshell, so an
        // unlinked user can still sign up when "Outside Party Signup" is enabled.
        var appUserId = await _db.DiscordActivityUsers
            .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
            .Select(link => link.IdentityUserId!)
            .FirstOrDefaultAsync(cancellationToken);

        // The member chose which character to sign up as → remember it, then resume
        // the flow they were on. Tail: "{token}:{eventId}[:{job}]".
        if (customId.StartsWith(CharPickPrefix, StringComparison.Ordinal))
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
            if (!string.IsNullOrEmpty(_discordUserId))
            {
                if (pickEventId > 0 && !string.IsNullOrWhiteSpace(chosen))
                {
                    _charChoice.Set(_discordUserId, pickEventId, chosen!);
                }
                return await ResumeSignupFlowAsync(token, pickEventId, appUserId: null, job, cancellationToken);
            }
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }

        // Quick sign up: the member picked a recent job combo (auto-claim a matching
        // open slot) or "manual" (fall through to the slot picker).
        if (customId.StartsWith(QuickComboPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, QuickComboPrefix);
            var value = SelectedValue(data);
            if (string.Equals(value, "m", StringComparison.Ordinal))
            {
                return await HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: false, cancellationToken, skipQuickCombo: true);
            }
            if (value is not null && value.StartsWith("c|", StringComparison.Ordinal))
            {
                var p = value[2..].Split('|');
                var main = p.Length > 0 ? p[0] : string.Empty;
                var sub = p.Length > 1 && p[1].Length > 0 ? p[1] : null;
                var role = p.Length > 2 && p[2].Length > 0 ? p[2] : null;
                return await HandleQuickComboClaimAsync(eventId, appUserId, main, sub, role, cancellationToken);
            }
            return Ephemeral("That option isn't recognized.");
        }

        if (customId.StartsWith(DiscordEventMessageBuilder.JobSelectPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.JobSelectPrefix);
            var job = data.TryGetProperty("values", out var values)
                && values.ValueKind == JsonValueKind.Array
                && values.GetArrayLength() > 0
                ? values[0].GetString()
                : null;
            return await HandleJobSignupAsync(eventId, appUserId, job, cancellationToken);
        }

        if (customId.StartsWith(DiscordEventMessageBuilder.WithdrawPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.WithdrawPrefix);
            return await HandleWithdrawAsync(eventId, appUserId, cancellationToken);
        }

        // Board "Sign Up as Party Leader" → the OPEN-slot picker, restricted to
        // parties with no leader yet; claiming marks the member as that party's
        // leader. Checked before the normal "Sign Up" prefix (distinct strings, but
        // the leader path is the more specific intent).
        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotLeaderSignUpPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotLeaderSignUpPrefix);
            return await HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: true, cancellationToken);
        }

        // Board "Sign Up" → an ephemeral picker of the OPEN slots; claiming joins as
        // a regular member (no crown).
        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotSignUpPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotSignUpPrefix);
            return await HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: false, cancellationToken);
        }

        // Leader picker select → the member chose a slot to lead; claim it as leader
        // (checked before the normal claim prefix — distinct strings).
        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotClaimLeaderPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotClaimLeaderPrefix);
            return await HandlePartySlotClaimAsync(eventId, SelectedSlotId(data), appUserId, asLeader: true, cancellationToken);
        }

        // Picker select → the member chose a slot; claim it as a regular member.
        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotClaimPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotClaimPrefix);
            return await HandlePartySlotClaimAsync(eventId, SelectedSlotId(data), appUserId, asLeader: false, cancellationToken);
        }

        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotLeavePrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotLeavePrefix);
            return await HandlePartySlotLeaveAsync(eventId, appUserId, cancellationToken);
        }

        // "Sign Up (No Slot)" → open an ephemeral job-pick wizard (role optional, job
        // required) so the attendee still says what they're coming as.
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyJoinEventPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartyJoinEventPrefix);
            return await StartGeneralJoinAsync(eventId, appUserId, cancellationToken);
        }

        // General-join wizard selects (role → main → sub). Raw picks ride in the
        // custom_id; the sub step (or last needed step) creates the attendance row.
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyJoinWizardRolePrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartyJoinWizardRolePrefix);
            return await AdvanceGeneralJoinWizardAsync(eventId, SelectedValue(data), null, null, false, appUserId, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyJoinWizardMainPrefix, StringComparison.Ordinal))
        {
            var p = customId[DiscordEventMessageBuilder.PartyJoinWizardMainPrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var role = p.Length > 1 ? p[1] : null; // raw (may be the "no role" sentinel)
            return await AdvanceGeneralJoinWizardAsync(eventId, role, SelectedValue(data), null, false, appUserId, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyJoinWizardSubPrefix, StringComparison.Ordinal))
        {
            var p = customId[DiscordEventMessageBuilder.PartyJoinWizardSubPrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var role = p.Length > 1 ? p[1] : null; // raw
            var main = p.Length > 2 ? p[2] : null; // raw
            return await AdvanceGeneralJoinWizardAsync(eventId, role, main, SelectedValue(data), true, appUserId, cancellationToken);
        }

        // Job-pick wizard selects (role → main → sub). Each carries the picks made
        // so far in its custom_id; the sub step (or the last needed step) claims.
        // The leader-variant prefixes carry the "claim as leader" intent through.
        var isLeaderRole = customId.StartsWith(DiscordEventMessageBuilder.PartyWizardLeaderRolePrefix, StringComparison.Ordinal);
        if (isLeaderRole || customId.StartsWith(DiscordEventMessageBuilder.PartyWizardRolePrefix, StringComparison.Ordinal))
        {
            var prefix = isLeaderRole ? DiscordEventMessageBuilder.PartyWizardLeaderRolePrefix : DiscordEventMessageBuilder.PartyWizardRolePrefix;
            var p = customId[prefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            return await AdvancePartyJobWizardAsync(
                eventId, slotId, SelectedValue(data), null, null, false, appUserId, isLeaderRole, cancellationToken);
        }
        var isLeaderMain = customId.StartsWith(DiscordEventMessageBuilder.PartyWizardLeaderMainPrefix, StringComparison.Ordinal);
        if (isLeaderMain || customId.StartsWith(DiscordEventMessageBuilder.PartyWizardMainPrefix, StringComparison.Ordinal))
        {
            var prefix = isLeaderMain ? DiscordEventMessageBuilder.PartyWizardLeaderMainPrefix : DiscordEventMessageBuilder.PartyWizardMainPrefix;
            var p = customId[prefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            var role = p.Length > 2 ? NormalizeWizardValue(p[2]) : null;
            return await AdvancePartyJobWizardAsync(
                eventId, slotId, role, SelectedValue(data), null, false, appUserId, isLeaderMain, cancellationToken);
        }
        var isLeaderSub = customId.StartsWith(DiscordEventMessageBuilder.PartyWizardLeaderSubPrefix, StringComparison.Ordinal);
        if (isLeaderSub || customId.StartsWith(DiscordEventMessageBuilder.PartyWizardSubPrefix, StringComparison.Ordinal))
        {
            var prefix = isLeaderSub ? DiscordEventMessageBuilder.PartyWizardLeaderSubPrefix : DiscordEventMessageBuilder.PartyWizardSubPrefix;
            var p = customId[prefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            var role = p.Length > 2 ? NormalizeWizardValue(p[2]) : null;
            var main = p.Length > 3 ? NormalizeWizardValue(p[3]) : null;
            return await AdvancePartyJobWizardAsync(
                eventId, slotId, role, main, SelectedValue(data), true, appUserId, isLeaderSub, cancellationToken);
        }

        // Auction bid button → open the bid-amount modal. The bid itself is
        // placed on modal submit (handled in HandleModalSubmitAsync).
        if (customId.StartsWith(AuctionBidService.BidButtonPrefix, StringComparison.Ordinal))
        {
            var itemId = ParseTrailingId(customId, AuctionBidService.BidButtonPrefix);
            return BidModal(itemId);
        }

        return Ephemeral("That action isn't recognized.");
    }

    // ─── Outside Party Signup helpers ───────────────────────────────────────────

    // Resume a signup flow after an identity step (the account character picker OR
    // the outside-signup name modal). `appUserId` is null for the outside path —
    // each handler re-resolves identity from the captured Discord id.
    private Task<IActionResult> ResumeSignupFlowAsync(
        string token, int eventId, string? appUserId, string? job, CancellationToken cancellationToken)
        => token switch
        {
            "slot" => HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: false, cancellationToken),
            "slotL" => HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: true, cancellationToken),
            "join" => StartGeneralJoinAsync(eventId, appUserId, cancellationToken),
            "job" => HandleJobSignupAsync(eventId, appUserId, job, cancellationToken),
            _ => Task.FromResult(Ephemeral("That action isn't recognized."))
        };

    // The identity + character name a signup handler should use, OR an Interrupt to
    // return instead (a "not a member" / "sign in" ephemeral, the alt-character
    // picker, or the outside-signup name modal). Account = AppUserId only; Outside =
    // DiscordUserId only; PlaceholderMatch = BOTH (an outside clicker whose typed name
    // matched a linkshell-only member, so it's keyed by the placeholder's AppUserId for
    // DKP but keeps the clicker's DiscordUserId so withdraw still finds the row).
    private sealed record SignupContext(
        IActionResult? Interrupt, string? AppUserId, string? DiscordUserId, string? CharacterName)
    {
        public bool ShouldStop => Interrupt is not null;
        public static SignupContext Stop(IActionResult response) => new(response, null, null, null);
        public static SignupContext Account(string appUserId, string characterName) => new(null, appUserId, null, characterName);
        public static SignupContext Outside(string discordUserId, string characterName) => new(null, null, discordUserId, characterName);
        public static SignupContext PlaceholderMatch(string appUserId, string discordUserId, string characterName) => new(null, appUserId, discordUserId, characterName);
    }

    // Resolves who is signing up and the character name to record. Account users go
    // through the existing membership + alt-picker path. When there's no linked
    // account, the linkshell must have OutsidePartySignupEnabled; the member is then
    // identified by Discord id and named via a one-time modal (cached per event).
    // `promptTail` ("slot:42", "join:42", "job:42:War", …) lets the picker/modal
    // resume the right flow afterwards.
    private async Task<SignupContext> ResolveSignupContextAsync(
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
                return SignupContext.Stop(Ephemeral("You're not a member of this linkshell, so you can't sign up for its events."));
            }
            if (NeedsCharacterPick(membership, ev.Id, appUserId))
            {
                return SignupContext.Stop(CharacterPicker(membership, promptTail, ev.EventName));
            }
            return SignupContext.Account(appUserId, ResolveSignupCharacter(membership, ev.Id, appUserId));
        }

        // No linked account → outside path, gated by the linkshell setting.
        var enabled = await _db.Linkshells
            .Where(l => l.Id == ev.LinkshellId)
            .Select(l => l.OutsidePartySignupEnabled)
            .FirstOrDefaultAsync(cancellationToken);
        if (!enabled || string.IsNullOrEmpty(_discordUserId))
        {
            return SignupContext.Stop(Ephemeral("Open LSM and sign in with Discord once to link your account, then try again."));
        }
        // Already registered? An "unsynced" member (placeholder) linked to this Discord
        // user is recognized automatically — attribute the signup to it (earns DKP), and
        // keep the Discord id on the row so Withdraw still works. No re-typing needed.
        var linkedMember = await _db.AppUserLinkshells
            .Include(link => link.AppUser)
            .FirstOrDefaultAsync(
                link => link.LinkshellId == ev.LinkshellId
                        && link.DiscordUserId == _discordUserId
                        && link.AppUserId != null
                        && link.AppUser!.IsPlaceholder,
                cancellationToken);
        if (linkedMember?.AppUserId is not null)
        {
            // Same alt-picker as a linked account, but the choice is cached under the
            // clicker's Discord id (a placeholder has no synced AppUserId of its own to
            // key on). Their alts come from the onboarding modal (AppUser.AltCharacterName1/2).
            if (NeedsCharacterPick(linkedMember, ev.Id, _discordUserId))
            {
                return SignupContext.Stop(CharacterPicker(linkedMember, promptTail, ev.EventName));
            }
            return SignupContext.PlaceholderMatch(
                linkedMember.AppUserId, _discordUserId,
                ResolveSignupCharacter(linkedMember, ev.Id, _discordUserId));
        }

        // Not registered yet → onboard via the "you're not synced" modal, which creates
        // + links their member on submit (then this resolves to the branch above).
        return SignupContext.Stop(OutsideOnboardModal(promptTail, ev.EventName));
    }

    // Lighter identity resolution for withdraw/leave (no character name, no name
    // prompt). Null = the clicker can't act here (no account and the linkshell
    // doesn't allow outside signups).
    private async Task<(string? AppUserId, string? DiscordUserId)?> ResolveWithdrawIdentityAsync(
        Event ev, string? appUserId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(appUserId))
        {
            return (appUserId, null);
        }
        var enabled = await _db.Linkshells
            .Where(l => l.Id == ev.LinkshellId)
            .Select(l => l.OutsidePartySignupEnabled)
            .FirstOrDefaultAsync(cancellationToken);
        if (!enabled || string.IsNullOrEmpty(_discordUserId))
        {
            return null;
        }
        return (null, _discordUserId);
    }

    // A Discord modal that ONBOARDS an outside (not-synced) player: they enter their
    // main + two alt names (all required), and on submit we create — or adopt + link — an
    // "unsynced" member for them keyed to their Discord id (so later signups recognize
    // them). custom_id carries the flow to resume. (Discord modals have a title + text
    // inputs only — no body text — so the "you're not synced" message lives in the
    // title/labels; the main field is prefilled with their Discord display name.)
    private IActionResult OutsideOnboardModal(string tail, string? eventName)
    {
        var prefill = string.IsNullOrWhiteSpace(_discordDisplayName) ? string.Empty : _discordDisplayName!.Trim();
        if (prefill.Length > 64) prefill = prefill[..64];

        static object NameRow(string fieldId, string label, bool required, string value, string placeholder) => new
        {
            type = 1, // action row
            components = new object[]
            {
                new
                {
                    type = 4, // text input
                    custom_id = fieldId,
                    label,
                    style = 1, // short
                    min_length = required ? 1 : 0,
                    max_length = 64,
                    required,
                    value,
                    placeholder
                }
            }
        };

        return Ok(new
        {
            type = ResponseModal,
            data = new
            {
                custom_id = $"{OutsideNamePrefix}{tail}",
                title = "Not synced — register yourself", // 45-char cap
                components = new object[]
                {
                    NameRow(OutsideNameFieldId, "Your MAIN FFXI character name", true, prefill, "e.g. Millhouse"),
                    NameRow(OutsideAlt1FieldId, "Alt 1 character name", true, string.Empty, "e.g. Millhouse2401"),
                    NameRow(OutsideAlt2FieldId, "Alt 2 character name", true, string.Empty, "e.g. Millhouse2402"),
                }
            }
        });
    }

    // The clicker's Discord display name (guild nick → global name → username), for
    // prefilling the outside-signup name modal. Not stored as the signup name.
    private static string? ResolveDiscordDisplayName(JsonElement root)
    {
        if (root.TryGetProperty("member", out var member))
        {
            if (member.TryGetProperty("nick", out var nick) && nick.ValueKind == JsonValueKind.String
                && nick.GetString() is { Length: > 0 } nickName)
            {
                return nickName;
            }
            if (member.TryGetProperty("user", out var memberUser))
            {
                return DisplayNameFromUser(memberUser);
            }
        }
        if (root.TryGetProperty("user", out var user))
        {
            return DisplayNameFromUser(user);
        }
        return null;

        static string? DisplayNameFromUser(JsonElement user)
        {
            if (user.TryGetProperty("global_name", out var global) && global.ValueKind == JsonValueKind.String
                && global.GetString() is { Length: > 0 } globalName)
            {
                return globalName;
            }
            if (user.TryGetProperty("username", out var username) && username.GetString() is { Length: > 0 } userName)
            {
                return userName;
            }
            return null;
        }
    }

    // Returns a Discord modal (type 9) asking for the bid amount. custom_id
    // carries the auction item id so the submit handler knows what to bid on.
    private IActionResult BidModal(int itemId)
    {
        if (itemId <= 0)
        {
            return Ephemeral("That auction item isn't recognized.");
        }

        return Ok(new
        {
            type = ResponseModal,
            data = new
            {
                custom_id = $"{AuctionBidService.BidModalPrefix}{itemId}",
                title = "Place a bid",
                components = new object[]
                {
                    new
                    {
                        type = 1, // action row
                        components = new object[]
                        {
                            new
                            {
                                type = 4, // text input
                                custom_id = AuctionBidService.BidAmountFieldId,
                                label = "Your bid (DKP)",
                                style = 1, // short
                                min_length = 1,
                                max_length = 7,
                                required = true,
                                placeholder = "e.g. 100"
                            }
                        }
                    }
                }
            }
        });
    }

    private async Task<IActionResult> HandleModalSubmitAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("custom_id", out var customIdEl)
            || customIdEl.GetString() is not { Length: > 0 } customId)
        {
            return Ephemeral("That action isn't recognized.");
        }

        var discordUserId = ResolveDiscordUserId(root);
        if (string.IsNullOrEmpty(discordUserId))
        {
            return Ephemeral("Couldn't read your Discord account from that submission.");
        }
        _interactionToken = root.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() : null;
        _discordUserId = discordUserId;
        _discordDisplayName = ResolveDiscordDisplayName(root);

        // Outside Party Signup ONBOARDING modal — no linked account required. Register
        // (create or adopt + link) an "unsynced" member for this Discord user from the
        // main + alt names, then resume; ResolveSignupContextAsync now recognizes them
        // by their Discord id and the signup is attributed to that member (earns DKP).
        if (customId.StartsWith(OutsideNamePrefix, StringComparison.Ordinal))
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
            var outsideEnabled = await _db.Linkshells
                .Where(l => l.Id == ev.LinkshellId)
                .Select(l => l.OutsidePartySignupEnabled)
                .FirstOrDefaultAsync(cancellationToken);
            if (!outsideEnabled)
            {
                return Ephemeral("Outside signups aren't enabled for this event.");
            }

            var main = ExtractModalValue(data, OutsideNameFieldId)?.Trim();
            var alt1 = ExtractModalValue(data, OutsideAlt1FieldId)?.Trim();
            var alt2 = ExtractModalValue(data, OutsideAlt2FieldId)?.Trim();
            if (string.IsNullOrWhiteSpace(main))
            {
                return Ephemeral("Enter your main character name to register.");
            }

            var result = await _manualMembers.FindOrCreateForOutsideAsync(
                ev.LinkshellId, main, alt1, alt2, discordUserId, cancellationToken);
            if (!result.Success)
            {
                return Ephemeral(result.Error ?? "Couldn't register you for this event.");
            }

            var job = parts.Length > 2 ? string.Join(':', parts.Skip(2)) : null;
            return await ResumeSignupFlowAsync(token, nameEventId, appUserId: null, job, cancellationToken);
        }

        var account = await _db.DiscordActivityUsers
            .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
            .Select(link => new
            {
                link.IdentityUserId,
                link.IdentityUser!.CharacterName,
                link.IdentityUser.UserName
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }

        if (customId.StartsWith(AuctionBidService.BidModalPrefix, StringComparison.Ordinal))
        {
            var itemId = ParseTrailingId(customId, AuctionBidService.BidModalPrefix);
            var amountText = ExtractModalValue(data, AuctionBidService.BidAmountFieldId)?.Trim();
            if (!int.TryParse(amountText, out var amount))
            {
                return Ephemeral("Enter a whole number for your bid.");
            }

            var fallbackName = account.CharacterName ?? account.UserName ?? "User";
            var result = await AuctionBidService.PlaceBidAsync(
                _db, account.IdentityUserId!, fallbackName, itemId, amount, cancellationToken);

            return Ephemeral(result.Success
                ? $"✅ Bid placed: {result.Amount} DKP on {result.ItemName ?? "the item"}."
                : result.Error ?? "Placing your bid failed.");
        }

        return Ephemeral("That action isn't recognized.");
    }

    // Pulls a text-input value out of a MODAL_SUBMIT payload by its custom_id.
    private static string? ExtractModalValue(JsonElement data, string fieldId)
    {
        if (!data.TryGetProperty("components", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("components", out var inputs) || inputs.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var input in inputs.EnumerateArray())
            {
                if (input.TryGetProperty("custom_id", out var cid)
                    && cid.GetString() == fieldId
                    && input.TryGetProperty("value", out var value))
                {
                    return value.GetString();
                }
            }
        }
        return null;
    }

    private async Task<IActionResult> HandleJobSignupAsync(
        int eventId, string? appUserId, string? job, CancellationToken cancellationToken)
    {
        if (eventId <= 0 || string.IsNullOrWhiteSpace(job))
        {
            return Ephemeral("Pick a job to sign up.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var ctx = await ResolveSignupContextAsync(ev, appUserId, $"job:{eventId}:{job}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }
        var characterName = ctx.CharacterName!;

        // Signing up again switches jobs IN PLACE so accrued time (StartTime /
        // Duration / break state) is preserved instead of restarting the clock.
        var existing = ctx.AppUserId is not null
            ? await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.AppUserId == ctx.AppUserId, cancellationToken)
            : await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.AppUserId == null && item.DiscordUserId == ctx.DiscordUserId, cancellationToken);
        if (existing is not null)
        {
            existing.CharacterName = characterName;
            existing.JobName = job!.Trim();
        }
        else
        {
            _db.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = ctx.AppUserId,
                DiscordUserId = ctx.DiscordUserId,
                EventId = eventId,
                CharacterName = characterName,
                JobName = job!.Trim(),
                EventDkp = 0,
                StartTime = ev.CommencementStartTime
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        return await UpdatedEventMessageAsync(ev.Id, cancellationToken);
    }

    private async Task<IActionResult> HandleWithdrawAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var identity = await ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }

        var existing = identity.Value.AppUserId is not null
            ? await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.AppUserId == identity.Value.AppUserId, cancellationToken)
            // Outside clicker: match by Discord id ALONE (not also AppUserId == null) so
            // it also finds a placeholder-matched row, which carries a non-null AppUserId.
            : await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.DiscordUserId == identity.Value.DiscordUserId, cancellationToken);
        if (existing is not null)
        {
            _db.AppUserEvents.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        ClearCharacterChoice(identity.Value.AppUserId, identity.Value.DiscordUserId, eventId);
        return await UpdatedEventMessageAsync(ev.Id, cancellationToken);
    }

    // "Sign Up" / "Sign Up as Party Leader" on the board → an ephemeral message with
    // a select of the OPEN slots (per-event). The leader path restricts the picker
    // to leaderless parties (BuildSlotPickerComponents) and routes the select through
    // the leader claim prefix. Picking a slot runs HandlePartySlotClaimAsync.
    private async Task<IActionResult> HandlePartySlotSignUpAsync(
        int eventId, string? appUserId, bool asLeader, CancellationToken cancellationToken, bool skipQuickCombo = false)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        // Resolve identity (and prompt the alt picker / outside-name modal if needed)
        // before showing the slot picker — the claim itself re-resolves the same way.
        var ctx = await ResolveSignupContextAsync(ev, appUserId, $"{(asLeader ? "slotL" : "slot")}:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);

        // Quick sign up (regular flow): offer the member's recent job combos that
        // have a matching open slot, so they can one-tap in. "Manual" or no
        // available combo falls through to the full slot picker below. Outside
        // (account-less) signups have no event history, so skip straight to the picker.
        if (!asLeader && !skipQuickCombo && ctx.AppUserId is not null)
        {
            var combos = await RecentCombosAsync(ctx.AppUserId!, ev.LinkshellId, cancellationToken);
            var available = combos.Where(c => FindBestOpenSlotForCombo(ev.PartySetup, slotSignups, c) is not null).ToList();
            if (available.Count > 0)
            {
                var full = combos.Where(c => !available.Contains(c)).ToList();
                return QuickComboPicker(eventId, available, full, ev.EventName);
            }
        }

        var picker = DiscordEventMessageBuilder.BuildSlotPickerComponents(eventId, ev.PartySetup, slotSignups, asLeader);
        if (picker.Length == 0)
        {
            return Ephemeral(asLeader
                ? "There's no party to lead right now — every party already has a leader (or has no open slots)."
                : "Every slot is taken right now.");
        }

        return PickerResponse(
            EventHeading(ev.EventName, asLeader ? "Pick a slot to claim as party leader 👑:" : "Pick a slot to claim:"),
            picker);
    }

    private sealed record JobCombo(string Main, string? Sub, string? Role);

    private static readonly HashSet<string> ValidMainJobs = new(EventJobCatalog.MainJobOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidSubJobs = new(EventJobCatalog.SubJobOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidRoles = new(EventJobCatalog.JobTypeOptions, StringComparer.OrdinalIgnoreCase);

    // The member's up-to-3 most recent DISTINCT (main, sub, role) combos in this
    // linkshell, newest first, from their completed-event history. Filtered to
    // catalog-valid jobs so an offered combo is reliably claimable (a slot claim
    // always needs a valid role + main; an invalid sub would also be rejected).
    private async Task<List<JobCombo>> RecentCombosAsync(string appUserId, int linkshellId, CancellationToken cancellationToken)
    {
        var rows = await _db.AppUserEventHistories
            .Where(p => p.AppUserId == appUserId
                        && p.EventHistory!.LinkshellId == linkshellId
                        && p.JobName != null && p.JobType != null)
            .OrderByDescending(p => p.EventHistory!.EndTime ?? p.EventHistory!.TimeStamp)
            .Select(p => new { p.JobName, p.SubJobName, p.JobType })
            .Take(50)
            .ToListAsync(cancellationToken);

        var combos = new List<JobCombo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var main = row.JobName?.Trim();
            var role = row.JobType?.Trim();
            if (string.IsNullOrWhiteSpace(main) || string.IsNullOrWhiteSpace(role)) continue;
            if (!ValidMainJobs.Contains(main) || !ValidRoles.Contains(role)) continue;
            var sub = string.IsNullOrWhiteSpace(row.SubJobName) ? null : row.SubJobName!.Trim();
            if (sub is not null && (!ValidSubJobs.Contains(sub) || string.Equals(sub, main, StringComparison.OrdinalIgnoreCase)))
            {
                sub = null; // drop an invalid/duplicate sub rather than reject the combo
            }
            if (!seen.Add($"{main}|{sub}|{role}")) continue;
            combos.Add(new JobCombo(main!, sub, role));
            if (combos.Count == 3) break;
        }
        return combos;
    }

    // The best OPEN slot a combo can fill, or null if none: a Job slot whose main
    // (and pinned sub, if any) match; else a Role slot whose role matches; else any
    // open "Any" slot. Most-specific wins so the member lands in the right slot.
    private static PartySetupSlot? FindBestOpenSlotForCombo(
        PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> signups, JobCombo combo)
    {
        PartySetupSlot? jobMatch = null, roleMatch = null, anyMatch = null;
        foreach (var party in setup.Alliances.SelectMany(a => a.Parties))
        {
            foreach (var slot in party.Slots.OrderBy(s => s.SortOrder))
            {
                if (signups.ContainsKey(slot.Id)) continue; // taken
                var type = slot.RequirementType ?? "Any";
                if (string.Equals(type, "Job", StringComparison.OrdinalIgnoreCase))
                {
                    if (Eq(slot.MainJob, combo.Main)
                        && (string.IsNullOrWhiteSpace(slot.SubJob) || Eq(slot.SubJob, combo.Sub)))
                    {
                        jobMatch ??= slot;
                    }
                }
                else if (string.Equals(type, "Role", StringComparison.OrdinalIgnoreCase))
                {
                    if (Eq(slot.Role, combo.Role)) roleMatch ??= slot;
                }
                else
                {
                    anyMatch ??= slot;
                }
            }
        }
        return jobMatch ?? roleMatch ?? anyMatch;

        static bool Eq(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string ComboLabel(JobCombo combo)
        => $"{combo.Main}/{(string.IsNullOrWhiteSpace(combo.Sub) ? "—" : combo.Sub)}"
           + (string.IsNullOrWhiteSpace(combo.Role) ? string.Empty : $" · {combo.Role}");

    // Ephemeral "quick sign up" select: one option per available recent combo (auto-
    // claims its matching slot) plus a manual fallback. Full recent combos are only
    // listed in the text, so an unavailable combo can't be selected.
    private IActionResult QuickComboPicker(int eventId, List<JobCombo> available, List<JobCombo> full, string? eventName)
    {
        var options = available
            .Select(c => (object)new { label = $"{ComboLabel(c)} · open", value = $"c|{c.Main}|{c.Sub}|{c.Role}" })
            .Append((object)new { label = "Pick a slot manually →", value = "m" })
            .ToArray();

        var content = "⚡ Quick sign up — pick a recent job, or choose a slot manually:";
        if (full.Count > 0)
        {
            content += $"\nFull right now: {string.Join(", ", full.Select(ComboLabel))}.";
        }

        return PickerResponse(EventHeading(eventName, content), SelectRow(QuickComboPrefix, eventId.ToString(), "Pick a recent job", options));
    }

    // Auto-claim the best open slot matching a chosen recent combo. Refuses (no
    // signup) when nothing's open for it — including a last-moment race.
    private async Task<IActionResult> HandleQuickComboClaimAsync(
        int eventId, string? appUserId, string main, string? sub, string? role, CancellationToken cancellationToken)
    {
        if (eventId <= 0 || string.IsNullOrWhiteSpace(main))
        {
            return Ephemeral("That option isn't recognized.");
        }

        var ev = await LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var ctx = await ResolveSignupContextAsync(ev, appUserId, $"slot:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var combo = new JobCombo(main.Trim(), sub, role);
        var slot = FindBestOpenSlotForCombo(ev.PartySetup, slotSignups, combo);
        if (slot is null)
        {
            return Ephemeral($"No open slot for {ComboLabel(combo)} right now — sign up again to pick another, or choose a slot manually.");
        }

        var result = await EventPartySignupService.ClaimSlotAsync(
            _db, eventId, slot, ctx.AppUserId, ctx.CharacterName!, role, main, sub, cancellationToken,
            claimAsLeader: false, discordUserId: ctx.DiscordUserId);
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't claim that slot.");
        }
        if (!await TryCommitSlotClaimAsync(cancellationToken))
        {
            return Ephemeral("That slot was just taken by another member. Sign up again to pick another.");
        }
        await EventPartySignupService.SyncParticipationAfterClaimAsync(_db, ev, ctx.AppUserId, cancellationToken, ctx.DiscordUserId);
        await _db.SaveChangesAsync(cancellationToken);
        await EventPartySignupService.ResolvePartyLeadershipAsync(_db, eventId, slot.PartySetupPartyId, cancellationToken);

        _eventQueue.Enqueue(eventId);
        return DismissPickerSilently();
    }

    // Picker select → claim the chosen slot for THIS event. If the slot pins both
    // a role and a main job, claim immediately; otherwise open a modal to collect
    // the missing job pick(s) (the claim then happens on modal submit).
    private async Task<IActionResult> HandlePartySlotClaimAsync(
        int eventId, int slotId, string? appUserId, bool asLeader, CancellationToken cancellationToken)
    {
        if (eventId <= 0 || slotId <= 0)
        {
            return Ephemeral("That slot isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var slot = await _db.PartySetupSlots
            .Include(s => s.Party!).ThenInclude(p => p.Alliance!)
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);
        if (slot is null || slot.Party?.Alliance?.PartySetupId != ev.PartySetupId)
        {
            return Ephemeral("That slot isn't part of this event.");
        }

        // Needs a job pick whenever a required field isn't pinned → start the
        // ephemeral dropdown wizard (role → main → sub). A "Player's Choice" sub
        // (slot pins the main but leaves the sub open) counts too: the member must
        // still specify which sub they're bringing, so an empty SubJob enters the
        // wizard and the pinned role/main steps are simply skipped inside it.
        if (string.IsNullOrWhiteSpace(slot.Role)
            || string.IsNullOrWhiteSpace(slot.MainJob)
            || string.IsNullOrWhiteSpace(slot.SubJob))
        {
            return await AdvancePartyJobWizardAsync(eventId, slotId, null, null, null, false, appUserId, asLeader, cancellationToken);
        }

        var ctx = await ResolveSignupContextAsync(ev, appUserId, $"{(asLeader ? "slotL" : "slot")}:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        var result = await EventPartySignupService.ClaimSlotAsync(
            _db, eventId, slot, ctx.AppUserId, ctx.CharacterName!, null, null, null, cancellationToken, asLeader,
            discordUserId: ctx.DiscordUserId);
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't claim that slot.");
        }
        if (!await TryCommitSlotClaimAsync(cancellationToken))
        {
            return Ephemeral("That slot was just taken by another member. Pick another open slot.");
        }
        // Pre-start: drop their no-slot attendance. Live: materialize the claim as a
        // participation so a late joiner lands in the running event immediately.
        await EventPartySignupService.SyncParticipationAfterClaimAsync(_db, ev, ctx.AppUserId, cancellationToken, ctx.DiscordUserId);
        await _db.SaveChangesAsync(cancellationToken);
        // Auto-promote earliest signup if the party just filled with no leader.
        await EventPartySignupService.ResolvePartyLeadershipAsync(_db, eventId, slot.PartySetupPartyId, cancellationToken);

        // The select lives on the ephemeral picker; queue the board refresh (the
        // image render runs off the 3s window) and silently dismiss the picker.
        _eventQueue.Enqueue(eventId);
        return DismissPickerSilently();
    }

    // Drives the job-pick wizard: presents the next needed dropdown (role → main →
    // sub) as an ephemeral message update, carrying the picks made so far in the
    // select custom_ids; once everything needed is gathered, claims the slot, edits
    // the board, and confirms. role/main/sub are the picks so far (null = not yet
    // picked, or pinned by the slot).
    private async Task<IActionResult> AdvancePartyJobWizardAsync(
        int eventId, int slotId, string? role, string? main, string? sub, bool subPicked,
        string? appUserId, bool asLeader, CancellationToken cancellationToken)
    {
        if (eventId <= 0 || slotId <= 0)
        {
            return Ephemeral("That slot isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var slot = await _db.PartySetupSlots
            .Include(s => s.Party!).ThenInclude(p => p.Alliance!)
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);
        if (slot is null || slot.Party?.Alliance?.PartySetupId != ev.PartySetupId)
        {
            return Ephemeral("That slot isn't part of this event.");
        }

        // Resolve identity once (the name was cached at the signup entry, so this
        // won't re-prompt). Used at the final claim below.
        var ctx = await ResolveSignupContextAsync(ev, appUserId, $"{(asLeader ? "slotL" : "slot")}:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        // Carry the leader intent through the wizard via the leader-variant
        // prefixes (same tail format); the normal prefixes drive the regular flow.
        var rolePrefix = asLeader ? DiscordEventMessageBuilder.PartyWizardLeaderRolePrefix : DiscordEventMessageBuilder.PartyWizardRolePrefix;
        var mainPrefix = asLeader ? DiscordEventMessageBuilder.PartyWizardLeaderMainPrefix : DiscordEventMessageBuilder.PartyWizardMainPrefix;
        var subPrefix = asLeader ? DiscordEventMessageBuilder.PartyWizardLeaderSubPrefix : DiscordEventMessageBuilder.PartyWizardSubPrefix;
        var leaderTag = asLeader ? " (as leader 👑)" : string.Empty;

        // Present the next unpinned-and-not-yet-picked field as a dropdown.
        if (string.IsNullOrWhiteSpace(slot.Role) && string.IsNullOrWhiteSpace(role))
        {
            return WizardStep(
                EventHeading(ev.EventName, $"Sign up{leaderTag} — {DiscordEventMessageBuilder.SlotRequirement(slot)}"),
                JobSelectRow(rolePrefix, $"{eventId}:{slotId}",
                    "Pick a role", EventJobCatalog.JobTypeOptions));
        }
        if (string.IsNullOrWhiteSpace(slot.MainJob) && string.IsNullOrWhiteSpace(main))
        {
            return WizardStep(
                EventHeading(ev.EventName, "Pick your main job:"),
                JobSelectRow(mainPrefix, $"{eventId}:{slotId}:{role ?? "-"}",
                    "Pick your main job", EventJobCatalog.MainJobOptions));
        }
        if (string.IsNullOrWhiteSpace(slot.SubJob) && !subPicked)
        {
            // A sub job is REQUIRED — no "no sub" option. Options exclude the
            // effective main (collected or pinned) so a member can't pick e.g. PLD/PLD.
            var effectiveMain = main ?? slot.MainJob;
            var subOptions = EventJobCatalog.SubJobOptions
                .Where(j => !string.Equals(j, effectiveMain, StringComparison.OrdinalIgnoreCase))
                .Select(j => (object)new { label = j, value = j })
                .ToArray();
            return WizardStep(
                EventHeading(ev.EventName, "Pick your sub job:"),
                SelectRow(subPrefix, $"{eventId}:{slotId}:{role ?? "-"}:{main ?? "-"}",
                    "Pick your sub job", subOptions));
        }

        // Everything needed is collected → claim, edit the board, confirm.
        var result = await EventPartySignupService.ClaimSlotAsync(
            _db, eventId, slot, ctx.AppUserId, ctx.CharacterName!,
            NormalizeWizardValue(role), NormalizeWizardValue(main), NormalizeWizardValue(sub), cancellationToken, asLeader,
            discordUserId: ctx.DiscordUserId);
        if (!result.Success)
        {
            return WizardStep($"⚠️ {result.Error}", Array.Empty<object>());
        }
        if (!await TryCommitSlotClaimAsync(cancellationToken))
        {
            return WizardStep("⚠️ That slot was just taken by another member. Pick another open slot.", Array.Empty<object>());
        }
        // Pre-start: drop their no-slot attendance. Live: materialize the claim as a
        // participation so a late joiner lands in the running event immediately.
        await EventPartySignupService.SyncParticipationAfterClaimAsync(_db, ev, ctx.AppUserId, cancellationToken, ctx.DiscordUserId);
        await _db.SaveChangesAsync(cancellationToken);
        await EventPartySignupService.ResolvePartyLeadershipAsync(_db, eventId, slot.PartySetupPartyId, cancellationToken);
        _eventQueue.Enqueue(eventId); // async board refresh (image render off the 3s window)
        return DismissPickerSilently();
    }

    // Commits a pending slot claim. ClaimSlotAsync's check-then-insert is a
    // TOCTOU race: two members clicking the same open slot simultaneously can
    // both pass the in-memory "is it free?" check, and the second commit then
    // violates the unique (EventId, PartySetupSlotId) index. We translate that
    // one race into a friendly "taken" outcome (false) instead of letting the
    // DbUpdateException bubble to a 500 / Discord "interaction failed". Any other
    // update failure is a real fault and is allowed to propagate.
    private async Task<bool> TryCommitSlotClaimAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    private static object[] JobSelectRow(string prefix, string idTail, string placeholder, IReadOnlyList<string> jobs)
    {
        return SelectRow(prefix, idTail, placeholder, jobs.Select(j => (object)new { label = j, value = j }).ToArray());
    }

    private static object[] SelectRow(string prefix, string idTail, string placeholder, object[] options)
    {
        return new object[]
        {
            new
            {
                type = 1, // action row
                components = new object[]
                {
                    new
                    {
                        type = 3, // string select
                        custom_id = $"{prefix}{idTail}",
                        placeholder,
                        min_values = 1,
                        max_values = 1,
                        options,
                    }
                }
            }
        };
    }

    // Ephemeral UPDATE_MESSAGE that morphs the wizard message to the next step
    // (or a final confirmation when components is empty).
    private IActionResult WizardStep(string content, object[] components) =>
        Ok(new { type = ResponseUpdateMessage, data = new { content, components } });

    // A picker/select step in a signup flow. When the click came from an ephemeral
    // picker (an earlier step), MORPH that message in place so the chain never piles
    // up stale "Which character…/Quick sign up…" messages; otherwise (a board click)
    // send a fresh ephemeral. The terminal step deletes the single message.
    private IActionResult PickerResponse(string content, object[] components) =>
        _isEphemeralSource
            ? Ok(new { type = ResponseUpdateMessage, data = new { content, components } })
            : Ok(new { type = ResponseChannelMessage, data = new { content, components, flags = EphemeralFlag } });

    // Prefixes a picker/wizard line with the event's name. Discord always shows the
    // ephemeral picker at the BOTTOM of the channel (its position can't be anchored
    // to the board the button is on), so when there are several boards this is how the
    // member tells which event the picker belongs to.
    private static string EventHeading(string? eventName, string body)
        => string.IsNullOrWhiteSpace(eventName) ? body : $"**{eventName.Trim()}**\n{body}";

    private static string? SelectedValue(JsonElement data) =>
        data.TryGetProperty("values", out var values)
        && values.ValueKind == JsonValueKind.Array
        && values.GetArrayLength() > 0
            ? values[0].GetString()
            : null;

    // The slot id from a picker select (value = slotId), or 0 if none/unparsable.
    private static int SelectedSlotId(JsonElement data) =>
        int.TryParse(SelectedValue(data), out var slotId) ? slotId : 0;

    private static string? NormalizeWizardValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "-"
        || value == DiscordEventMessageBuilder.PartyWizardNoSub
        || value == DiscordEventMessageBuilder.PartyWizardNoRole
            ? null
            : value;

    // "Leave event" lives on the board itself, so refresh the board in place. Drops
    // BOTH the member's party slot (if any) AND their general attendance (if any).
    private async Task<IActionResult> HandlePartySlotLeaveAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        // Once the event is live, members can't drop themselves from the board —
        // only an officer can free a slot (from the website / Activity).
        if (!EventPartySignupService.MemberCanWithdraw(ev))
        {
            return Ephemeral("The event is live — ask an officer to remove you.");
        }

        var identity = await ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;

        var leftPartyId = await EventPartySignupService.LeaveAsync(_db, eventId, idAppUser, cancellationToken, idDiscord);

        // Also drop their general-attendance row (the "Join (no slot)" roster).
        var attendance = idAppUser is not null
            ? await _db.AppUserEvents
                .Where(p => p.EventId == eventId && p.AppUserId == idAppUser)
                .ToListAsync(cancellationToken)
            // Outside clicker: match by Discord id ALONE so it also finds a
            // placeholder-matched row (which carries a non-null AppUserId).
            : await _db.AppUserEvents
                .Where(p => p.EventId == eventId && p.DiscordUserId == idDiscord)
                .ToListAsync(cancellationToken);
        if (attendance.Count > 0)
        {
            _db.AppUserEvents.RemoveRange(attendance);
        }

        if (leftPartyId is null && attendance.Count == 0)
        {
            // Leave lives on the shared board (the same button for everyone, which
            // Discord can't hide/grey per-user), so a not-signed-up click just gets
            // a private "nothing to leave" notice instead of refreshing the board.
            return Ephemeral("You're not signed up for this event, so there's nothing to leave.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        ClearCharacterChoice(idAppUser, idDiscord, eventId);
        // The board is a rendered image — queue the refresh (render runs off the 3s
        // window) and silently acknowledge the click (no confirmation message); the
        // refreshed board is the feedback.
        _eventQueue.Enqueue(eventId);
        return Ok(new { type = ResponseDeferredUpdate });
    }

    // "Join (no slot)" button on the board → a NEW ephemeral wizard message (so the
    // board itself isn't replaced). Subsequent picks morph this ephemeral via
    // AdvanceGeneralJoinWizardAsync. Starts at the role step (optional).
    private async Task<IActionResult> StartGeneralJoinAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        // Gate + name prompt (the role/job picks come next; the name is cached now).
        var ctx = await ResolveSignupContextAsync(ev, appUserId, $"join:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        var roleOptions = new[] { (object)new { label = "No specific role", value = DiscordEventMessageBuilder.PartyWizardNoRole } }
            .Concat(EventJobCatalog.JobTypeOptions.Select(r => (object)new { label = r, value = r }))
            .ToArray();
        return PickerResponse(
            EventHeading(ev.EventName, "Sign Up (No Slot) — pick your role (optional):"),
            SelectRow(DiscordEventMessageBuilder.PartyJoinWizardRolePrefix, $"{eventId}", "Pick a role", roleOptions));
    }

    // "Join (no slot)" job-pick wizard: role (optional) → main job (required) →
    // sub (optional). Mirrors the slot wizard but there's no slot to claim — the
    // picks become a general-attendance AppUserEvent so the attendee always says
    // what job they're coming as (no more blank "Role Unassigned / Job/Sub" rows).
    // Re-running replaces the member's existing attendance for this event.
    private async Task<IActionResult> AdvanceGeneralJoinWizardAsync(
        int eventId, string? role, string? main, string? sub, bool subPicked,
        string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        // Resolve identity once (the name was cached at the join entry) — used for
        // the attendance row written at the end of the wizard.
        var ctx = await ResolveSignupContextAsync(ev, appUserId, $"join:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        // Step 1 — role (optional; an explicit "No specific role" choice lets a
        // member proceed without one but still pick a job next).
        if (string.IsNullOrWhiteSpace(role))
        {
            var roleOptions = new[] { (object)new { label = "No specific role", value = DiscordEventMessageBuilder.PartyWizardNoRole } }
                .Concat(EventJobCatalog.JobTypeOptions.Select(r => (object)new { label = r, value = r }))
                .ToArray();
            return WizardStep(
                EventHeading(ev.EventName, "Sign Up (No Slot) — pick your role (optional):"),
                SelectRow(DiscordEventMessageBuilder.PartyJoinWizardRolePrefix, $"{eventId}", "Pick a role", roleOptions));
        }

        // Step 2 — main job (required).
        if (string.IsNullOrWhiteSpace(main))
        {
            return WizardStep(
                EventHeading(ev.EventName, "Pick your main job:"),
                JobSelectRow(DiscordEventMessageBuilder.PartyJoinWizardMainPrefix, $"{eventId}:{role}",
                    "Pick your main job", EventJobCatalog.MainJobOptions));
        }

        // Step 3 — sub job (optional). Exclude the chosen main + offer "no sub".
        if (!subPicked)
        {
            var subOptions = new[] { (object)new { label = "No sub job", value = DiscordEventMessageBuilder.PartyWizardNoSub } }
                .Concat(EventJobCatalog.SubJobOptions
                    .Where(j => !string.Equals(j, main, StringComparison.OrdinalIgnoreCase))
                    .Select(j => (object)new { label = j, value = j }))
                .ToArray();
            return WizardStep(
                EventHeading(ev.EventName, "Pick your sub job (optional):"),
                SelectRow(DiscordEventMessageBuilder.PartyJoinWizardSubPrefix, $"{eventId}:{role}:{main}",
                    "Pick your sub job", subOptions));
        }

        // Done — set the general-attendance row to the chosen job.
        var characterName = ctx.CharacterName!;

        var existing = ctx.AppUserId is not null
            ? await _db.AppUserEvents
                .Where(p => p.EventId == eventId && p.AppUserId == ctx.AppUserId)
                .ToListAsync(cancellationToken)
            : await _db.AppUserEvents
                .Where(p => p.EventId == eventId && p.AppUserId == null && p.DiscordUserId == ctx.DiscordUserId)
                .ToListAsync(cancellationToken);

        // One identity per event: joining "no slot" releases any party slot the
        // member currently holds, so they're never both in a slot and "no slot".
        var leftPartyId = await EventPartySignupService.LeaveAsync(_db, eventId, ctx.AppUserId, cancellationToken, ctx.DiscordUserId);

        if (existing.Count > 0)
        {
            // Switching jobs updates IN PLACE so accrued time (StartTime / Duration /
            // break state) is preserved; drop any duplicate rows.
            var keep = existing[0];
            keep.CharacterName = characterName;
            keep.JobType = NormalizeWizardValue(role);
            keep.JobName = NormalizeWizardValue(main);
            keep.SubJobName = NormalizeWizardValue(sub);
            for (var i = 1; i < existing.Count; i++) { _db.AppUserEvents.Remove(existing[i]); }
        }
        else
        {
            _db.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = ctx.AppUserId,
                DiscordUserId = ctx.DiscordUserId,
                EventId = eventId,
                CharacterName = characterName,
                JobType = NormalizeWizardValue(role),
                JobName = NormalizeWizardValue(main),
                SubJobName = NormalizeWizardValue(sub),
                EventDkp = 0,
                StartTime = ev.CommencementStartTime,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
        await EventPartySignupService.ResolvePartyLeadershipAsync(_db, eventId, leftPartyId, cancellationToken);
        _eventQueue.Enqueue(eventId); // async board refresh (image render off the 3s window)
        return DismissPickerSilently();
    }

    // Loads an event with its party-setup tree (for the picker + board render).
    private Task<Event?> LoadEventWithSetupAsync(int eventId, CancellationToken cancellationToken)
    {
        return _db.Events
            .AsNoTracking()
            .Include(item => item.PartySetup!)
                .ThenInclude(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
            .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
    }

    // Successful signup terminating an ephemeral picker/wizard: acknowledge the
    // select with a no-op deferred update (no visible confirmation) and delete
    // the ephemeral message it ran in, so the channel isn't left with a "✅ Signed
    // up" note. The board itself (refreshed via the queue) is the real feedback.
    // The delete runs after the response flushes so the interaction is acked
    // first (Discord requires the callback before a followup on the token).
    private IActionResult DismissPickerSilently()
    {
        var token = _interactionToken;
        if (!string.IsNullOrEmpty(token))
        {
            HttpContext.Response.OnCompleted(() => DeleteOriginalEphemeralAsync(token));
        }
        return Ok(new { type = ResponseDeferredUpdate });
    }

    // DELETE the interaction's original (ephemeral) message via the webhook the
    // interaction token authorizes. Best-effort: a failure just leaves the
    // picker in place, so it's logged but never surfaced.
    private async Task DeleteOriginalEphemeralAsync(string token)
    {
        if (string.IsNullOrEmpty(_discordClientId))
        {
            return;
        }
        try
        {
            using var client = _httpClientFactory.CreateClient();
            await client.DeleteAsync(
                $"https://discord.com/api/v10/webhooks/{_discordClientId}/{token}/messages/@original");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dismiss the ephemeral signup picker.");
        }
    }

    // type-7 UPDATE_MESSAGE that refreshes the board/ad-hoc message the user
    // clicked — used for actions whose button lives ON the board (e.g. Leave).
    private async Task<IActionResult> UpdatedEventMessageAsync(int eventId, CancellationToken cancellationToken)
    {
        var ev = await LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null)
        {
            // Event vanished (closed/deleted) — acknowledge without editing.
            return Ok(new { type = ResponseDeferredUpdate });
        }

        var rows = await _db.AppUserEvents
            .AsNoTracking()
            .Where(signup => signup.EventId == ev.Id)
            .OrderBy(signup => signup.CharacterName)
            .Select(signup => new { signup.CharacterName, signup.JobName, signup.SubJobName, signup.JobType })
            .ToListAsync(cancellationToken);
        var signups = rows
            .Select(row => new EventSignupLine(row.CharacterName ?? "Unknown", row.JobName, row.SubJobName, row.JobType))
            .ToList();

        var slotSignups = ev.PartySetup is null
            ? null
            : await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var data = DiscordEventMessageBuilder.Build(ev, signups, ev.PartySetup, slotSignups);
        return Ok(new { type = ResponseUpdateMessage, data });
    }

    private static string? ResolveDiscordUserId(JsonElement root)
    {
        // Guild interactions carry member.user.id; DM interactions carry user.id.
        if (root.TryGetProperty("member", out var member)
            && member.TryGetProperty("user", out var memberUser)
            && memberUser.TryGetProperty("id", out var memberUserId))
        {
            return memberUserId.GetString();
        }
        if (root.TryGetProperty("user", out var user) && user.TryGetProperty("id", out var userId))
        {
            return userId.GetString();
        }
        return null;
    }

    private static int ParseTrailingId(string customId, string prefix)
    {
        var tail = customId[prefix.Length..];
        // Some components append a ":suffix" after the id to stay unique within a
        // message (e.g. the per-alliance slot pickers add the alliance index so each
        // select's custom_id differs). Parse just the leading id; the suffix, if any,
        // carries no routing meaning here.
        var colon = tail.IndexOf(':');
        if (colon >= 0) { tail = tail[..colon]; }
        return int.TryParse(tail, out var id) ? id : 0;
    }

    private IActionResult Ephemeral(string message) =>
        Ok(new { type = ResponseChannelMessage, data = new { content = message, flags = EphemeralFlag } });

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
    private void ClearCharacterChoice(string? appUserId, string? discordUserId, int eventId)
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
