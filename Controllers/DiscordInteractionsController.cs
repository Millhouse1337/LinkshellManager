using System.Text;
using System.Text.Json;
using LinkshellManagerDiscordApp.Controllers.Discord;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static LinkshellManagerDiscordApp.Controllers.Discord.DiscordInteractionHandlerBase;
using static LinkshellManagerDiscordApp.Controllers.Discord.DiscordSignupIdentity;

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

    // "🏁 End Camp / Enter ToD" modal (every windowed HNM board — Standard + Manual Check In).
    // custom_id carries the eventId; the fields capture the Time of Death (with seconds), NQ/HQ
    // (only for the three merge-pair families), the camp's outcome, the window it popped on, and
    // the auto-re-post lead.
    //
    // That is FIVE rows on an HQ family and Discord's modal cap is exactly five, which is what
    // decided the outcome field's shape: Claimed and Killed were briefly two Yes/No dropdowns, and
    // they are one three-way dropdown again because pop window + re-post need the row back. Nothing
    // is lost in the merge — "somebody else claimed it, and we killed it" isn't a state that exists,
    // so the 2x2 only ever had three reachable corners.
    //
    // The day number is still not asked: the event form owns it, and it's the one field here the
    // officer would be re-typing rather than reporting. The wire ids keep the "wdpop" spelling so
    // boards posted before this stopped being Manual Check In-only keep working.
    private const string WdPopModalPrefix = "evt:wdpopmodal:";
    private const string WdPopTodFieldId = "wdpop_tod";
    // The Date half of the Time of Death. Its own box so a kill that is not from today can be
    // entered without the officer having to know that the time box would have taken a date too.
    private const string WdPopTodDateFieldId = "wdpop_tod_date";
    private const string WdPopHqFieldId = "wdpop_hq";
    private const string WdPopOutcomeFieldId = "wdpop_outcome";
    // Retired as a QUESTION — the Date box took its row — but still read on submit, so a modal
    // opened before the picker was removed still records the window its officer actually picked.
    // (WdPopWindowUnknownValue went with it: "unknown" and absent both read as null here, and null
    // is now the ordinary path rather than an opt-in.)
    private const string WdPopWindowFieldId = "wdpop_window";
    private const string WdPopRepostFieldId = "wdpop_repost";
    // Retired — no longer rendered, but still READ, so a modal opened before the field changed and
    // submitted afterwards is still recorded correctly instead of silently taking a default. A
    // Discord modal can sit open on someone's screen indefinitely, so both spellings of the outcome
    // have to keep parsing for as long as an old one might still be sitting there.
    private const string WdPopDayFieldId = "wdpop_day";
    private const string WdPopClaimFieldId = "wdpop_claim";
    private const string WdPopKillFieldId = "wdpop_kill";

    // The "/lsm" slash command (posts a launch card) and its "Join" button (launches the
    // Activity for the clicker via the LAUNCH_ACTIVITY callback). Every event board's green
    // "Open LSManager App" button sends this same id.
    private const string LaunchCommandName = "lsm";
    private const string LaunchButtonId = DiscordEventMessageBuilder.LaunchAppButtonId;

    private readonly DiscordInteractionVerifier _verifier;
    private readonly ApplicationDbContext _db;
    private readonly DiscordEventChannelQueue _eventQueue;
    private readonly ILogger<DiscordInteractionsController> _logger;
    private readonly SignupCharacterChoiceCache _charChoice;
    private readonly OfficerAddTargetCache _officerAddTargets;
    private readonly ManualMemberService _manualMembers;
    private readonly TimeZoneConversionService _timeZones;
    private readonly DiscordSignupIdentity _identity;
    private readonly DiscordPermissionGates _gates;
    private readonly DiscordBoardSupport _board;
    private readonly PartyBoardHandler _partyBoard;
    private readonly HnmCampBoardHandler _hnmCamp;

    // This request's interaction: the token, the clicker's Discord id + display name, and
    // whether the click came from an ephemeral picker. Shared with the classes above.
    private DiscordInteractionContext Interaction { get; }

    public DiscordInteractionsController(
        DiscordInteractionVerifier verifier,
        ApplicationDbContext db,
        DiscordEventChannelQueue eventQueue,
        ILogger<DiscordInteractionsController> logger,
        SignupCharacterChoiceCache charChoice,
        OfficerAddTargetCache officerAddTargets,
        ManualMemberService manualMembers,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        TimeZoneConversionService timeZones,
        DiscordInteractionContext interaction,
        DiscordSignupIdentity identity,
        DiscordPermissionGates gates,
        DiscordBoardSupport board,
        PartyBoardHandler partyBoard,
        HnmCampBoardHandler hnmCamp)
    {
        _verifier = verifier;
        _db = db;
        _eventQueue = eventQueue;
        _logger = logger;
        _charChoice = charChoice;
        _officerAddTargets = officerAddTargets;
        _manualMembers = manualMembers;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
        _timeZones = timeZones;
        Interaction = interaction;
        _identity = identity;
        _gates = gates;
        _board = board;
        _partyBoard = partyBoard;
        _hnmCamp = hnmCamp;
    }

    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;

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

        Interaction.HttpContext = HttpContext;

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
            try
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

                if (type == InteractionApplicationCommand)
                {
                    var commandName = root.TryGetProperty("data", out var cmdData)
                        && cmdData.TryGetProperty("name", out var cmdName) ? cmdName.GetString() : null;
                    // "/lsm" → post an officer-only launch card into the channel.
                    if (string.Equals(commandName, LaunchCommandName, StringComparison.OrdinalIgnoreCase))
                    {
                        return await HandleLaunchCardCommandAsync(root, cancellationToken);
                    }
                    // The Activity's entry-point "Launch" command (now app-handled, so the
                    // public "Game Invitation" card is suppressed): just launch the Activity
                    // for the clicker with a quiet LAUNCH_ACTIVITY callback — no channel post.
                    return Ok(new { type = ResponseLaunchActivity, data = new { } });
                }

                // Unhandled interaction type — acknowledge with a no-op deferred
                // update so Discord doesn't surface an error to the user.
                return Ok(new { type = ResponseDeferredUpdate });
            }
            catch (Exception ex)
            {
                // A handler threw (e.g. a DB/schema fault such as a missing column from an
                // unapplied migration). Without this catch the exception becomes a raw 500 /
                // HTML response that Discord can only surface as the opaque "This interaction
                // failed", hiding the real cause. Return a valid ephemeral instead and log the
                // full stack so the actual error is visible in the server logs.
                _logger.LogError(ex, "Discord interaction handler threw; returning an ephemeral error to the user.");
                return Ephemeral("Something went wrong handling that — please try again. If it keeps happening, let an officer know.");
            }
        }
    }

    // "/lsm": post a public "Join" launch card into the channel — but only for someone with
    // Manage events in a linkshell linked to THIS Discord server (see CanPostLaunchCardAsync;
    // the card's Join button is then clickable by anyone). Everyone else gets a private nudge.
    private async Task<IActionResult> HandleLaunchCardCommandAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var guildId = root.TryGetProperty("guild_id", out var guildEl) ? guildEl.GetString() : null;
        if (string.IsNullOrEmpty(guildId))
        {
            return Ephemeral("Run /lsm in a server channel.");
        }
        var discordUserId = ResolveDiscordUserId(root);
        if (string.IsNullOrEmpty(discordUserId))
        {
            return Ephemeral("Couldn't read your Discord account from that command.");
        }

        var appUserId = await _db.DiscordActivityUsers
            .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
            .Select(link => link.IdentityUserId!)
            .FirstOrDefaultAsync(cancellationToken);
        if (!await _gates.CanPostLaunchCardAsync(appUserId, guildId, cancellationToken))
        {
            return Ephemeral("You need the **Manage events** permission in a linkshell linked to this server to post the launch card here.");
        }

        // Public channel message (no ephemeral flag) — a launch card with a Join button.
        return Ok(new
        {
            type = ResponseChannelMessage,
            data = new
            {
                embeds = new[]
                {
                    new
                    {
                        title = "🎮 LinkshellManager",
                        description = "Tap **Join** to open the LinkshellManager app — DKP, events, party setups, and more.",
                        color = 0x5865F2,
                        // Discord fetches the image itself, so it needs an absolute public URL. This
                        // request arrived through our own host (nginx forwards Host + https), so it
                        // is that address — no App:PublicBaseUrl needed.
                        thumbnail = new { url = $"{Request.Scheme}://{Request.Host}/images/LinkshellManagerIcon.png" },
                    }
                },
                components = new[]
                {
                    new
                    {
                        type = 1, // action row
                        components = new object[]
                        {
                            new { type = 2, style = 1, label = "Join", custom_id = LaunchButtonId }
                        }
                    }
                }
            }
        });
    }

    private async Task<IActionResult> HandleComponentAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("custom_id", out var customIdEl)
            || customIdEl.GetString() is not { Length: > 0 } customId)
        {
            return Ephemeral("That action isn't recognized.");
        }

        // The "Join" button on a posted launch card → launch the Activity for the clicker
        // (per-user, quiet; Discord opens the embedded app). No identity/DB work needed.
        if (string.Equals(customId, LaunchButtonId, StringComparison.Ordinal))
        {
            return Ok(new { type = ResponseLaunchActivity, data = new { } });
        }

        // Captured so a successful signup can silently dismiss the ephemeral
        // picker/wizard it ran in (delete it) instead of leaving a confirmation.
        Interaction.InteractionToken = root.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() : null;

        var discordUserId = ResolveDiscordUserId(root);
        if (string.IsNullOrEmpty(discordUserId))
        {
            return Ephemeral("Couldn't read your Discord account from that click.");
        }
        // Captured for the outside-signup path (identity + a name modal prefill).
        Interaction.DiscordUserId = discordUserId;
        Interaction.DiscordDisplayName = ResolveDiscordDisplayName(root);

        // Did this click come from an ephemeral picker (vs the public board)? If so,
        // the next picker step morphs it in place instead of stacking a new message.
        Interaction.IsEphemeralSource = root.TryGetProperty("message", out var srcMsg)
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

        if (customId.StartsWith(DiscordEventMessageBuilder.JobSelectPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.JobSelectPrefix);
            var job = data.TryGetProperty("values", out var values)
                && values.ValueKind == JsonValueKind.Array
                && values.GetArrayLength() > 0
                ? values[0].GetString()
                : null;
            return await _partyBoard.HandleJobSignupAsync(eventId, appUserId, job, cancellationToken);
        }

        if (customId.StartsWith(DiscordEventMessageBuilder.WithdrawPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.WithdrawPrefix);
            return await _partyBoard.HandleWithdrawAsync(eventId, appUserId, cancellationToken);
        }

        // Board "Sign Up as Party Leader" → the OPEN-slot picker, restricted to
        // parties with no leader yet; claiming marks the member as that party's
        // leader. Checked before the normal "Sign Up" prefix (distinct strings, but
        // the leader path is the more specific intent).
        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotLeaderSignUpPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotLeaderSignUpPrefix);
            return await _partyBoard.HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: true, cancellationToken);
        }

        // Board "Sign Up" → an ephemeral picker of the OPEN slots; claiming joins as
        // a regular member (no crown).
        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotSignUpPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotSignUpPrefix);
            return await _partyBoard.HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: false, cancellationToken);
        }

        // Drill-down step 1 → 2: the member picked an alliance (value = alliance index);
        // morph the ephemeral to its party picker. Leader checked first (distinct string).
        if (customId.StartsWith(DiscordEventMessageBuilder.AlliancePickLeaderPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.AlliancePickLeaderPrefix);
            return await _partyBoard.HandleAlliancePickedAsync(eventId, SelectedSlotId(data), asLeader: true, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.AlliancePickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.AlliancePickPrefix);
            return await _partyBoard.HandleAlliancePickedAsync(eventId, SelectedSlotId(data), asLeader: false, cancellationToken);
        }

        // Drill-down step 2 → 3: the member picked a party (value = party id); morph the
        // ephemeral to its open-slot picker (which reuses the claim prefix).
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyPickLeaderPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartyPickLeaderPrefix);
            return await _partyBoard.HandlePartyPickedAsync(eventId, SelectedSlotId(data), asLeader: true, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartyPickPrefix);
            return await _partyBoard.HandlePartyPickedAsync(eventId, SelectedSlotId(data), asLeader: false, cancellationToken);
        }

        // Leader picker select → the member chose a slot to lead; claim it as leader
        // (checked before the normal claim prefix — distinct strings).
        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotClaimLeaderPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotClaimLeaderPrefix);
            return await _partyBoard.HandlePartySlotClaimAsync(eventId, SelectedSlotId(data), appUserId, asLeader: true, cancellationToken);
        }

        // Picker select → the member chose a slot; claim it as a regular member.
        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotClaimPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotClaimPrefix);
            return await _partyBoard.HandlePartySlotClaimAsync(eventId, SelectedSlotId(data), appUserId, asLeader: false, cancellationToken);
        }

        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotLeavePrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotLeavePrefix);
            return await _partyBoard.HandlePartySlotLeaveAsync(eventId, appUserId, cancellationToken);
        }

        // "Make Me Party Lead" → a member already holding a slot takes their party's
        // crown from whoever currently holds it (overrides the existing leader).
        if (customId.StartsWith(DiscordEventMessageBuilder.MakeLeaderPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.MakeLeaderPrefix);
            return await _partyBoard.HandleMakePartyLeaderAsync(eventId, appUserId, cancellationToken);
        }

        // "Make Me Alliance Lead" → a member already holding a slot takes their whole
        // alliance's crown from whoever currently holds it (overrides the existing lead).
        if (customId.StartsWith(DiscordEventMessageBuilder.MakeAllianceLeaderPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.MakeAllianceLeaderPrefix);
            return await _partyBoard.HandleMakeAllianceLeaderAsync(eventId, appUserId, cancellationToken);
        }

        // "🔒 Stay Next Window" → the clicker toggles the lock on their OWN slot so it
        // survives the next automatic window turnover (window-cycle HNM boards).
        if (customId.StartsWith(DiscordEventMessageBuilder.LockNextWindowPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.LockNextWindowPrefix);
            return await _hnmCamp.HandleLockNextWindowAsync(eventId, appUserId, cancellationToken);
        }

        // "🎖️ My Readiness" → the clicker's own ephemeral tag picker, and that picker's select
        // writing the chosen set back. The pick prefix is checked first: it's the more specific
        // string, though the two don't actually overlap under StartsWith.
        if (customId.StartsWith(DiscordEventMessageBuilder.ReadinessPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.ReadinessPickPrefix);
            return await _partyBoard.HandleReadinessPickedAsync(eventId, appUserId, SelectedValues(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.ReadinessButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.ReadinessButtonPrefix);
            return await _partyBoard.HandleReadinessStartAsync(eventId, appUserId, cancellationToken);
        }

        // The window viewer's own navigation, sent from INSIDE the ephemeral it opened: the jump
        // list (value = the window) and the ◀ / ▶ arrows (window baked into the id). Checked ahead
        // of the board button below purely to keep the three ids together — they don't actually
        // overlap under StartsWith, since "evt:winviewpick:" diverges at the 'p'.
        if (customId.StartsWith(DiscordEventMessageBuilder.WindowViewPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.WindowViewPickPrefix);
            return await _hnmCamp.HandleViewPrevWindowAsync(
                eventId,
                int.TryParse(SelectedValue(data), out var picked) ? picked : null,
                cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.WindowViewPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.WindowViewPrefix);
            return await _hnmCamp.HandleViewPrevWindowAsync(
                eventId,
                HnmCampBoardHandler.ParseTrailingWindowNumber(customId, DiscordEventMessageBuilder.WindowViewPrefix),
                cancellationToken);
        }

        // "📋 View Camp Details" on the DEFEATED note → anyone in the linkshell: the camp that just
        // ended, read-only. The trailing id is an EventHistory, not an event — the board row is
        // recycled for the next pop, so an event id here would open whatever is being camped now.
        if (customId.StartsWith(DiscordEventMessageBuilder.PastEventDetailsPrefix, StringComparison.Ordinal))
        {
            var historyId = ParseTrailingId(customId, DiscordEventMessageBuilder.PastEventDetailsPrefix);
            return await _hnmCamp.HandlePastEventDetailsAsync(historyId, appUserId, cancellationToken);
        }

        // "◀ View Previous Window" on the board → anyone: read-only ephemeral opening on the newest
        // captured roster, navigable from there. Boards posted before this change send the same id
        // from what was "Prev Window", so an old button now opens the viewer instead of stepping the
        // counter.
        if (customId.StartsWith(DiscordEventMessageBuilder.ViewPrevWindowPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.ViewPrevWindowPrefix);
            return await _hnmCamp.HandleViewPrevWindowAsync(eventId, requestedWindow: null, cancellationToken);
        }

        // "✅ Check In (this window)" (Manual Check In boards) → member self-serve attendance for the current window.
        if (customId.StartsWith(DiscordEventMessageBuilder.XinPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.XinPrefix);
            return await _hnmCamp.HandleXinAsync(eventId, appUserId, cancellationToken);
        }

        // "🚪 Check Out" (Manual Check In boards) → member leaves mid-camp; credit stops at the current window.
        if (customId.StartsWith(DiscordEventMessageBuilder.CheckOutPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.CheckOutPrefix);
            return await _hnmCamp.HandleCheckOutAsync(eventId, appUserId, cancellationToken);
        }

        // "🏁 Pop / End Camp" (Manual Check In boards) → officers only: open the pop-window + ToD modal.
        if (customId.StartsWith(DiscordEventMessageBuilder.WdPopPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.WdPopPrefix);
            return await HandleWdPopButtonAsync(eventId, appUserId, cancellationToken);
        }

        // "➕ Add Member (officers)" → officers only: ephemeral picker of who to seat.
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerAddButtonPrefix);
            return await HandleOfficerAddStartAsync(eventId, appUserId, cancellationToken);
        }

        // Officer "Move Member" — pick a participant → pick a destination slot (or bench).
        // The more-specific src/dest/bench prefixes are checked before the bare button.
        if (customId.StartsWith(DiscordEventMessageBuilder.MoveSourcePickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.MoveSourcePickPrefix);
            return await HandleMoveSourcePickedAsync(eventId, appUserId, SelectedValue(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.MoveDestClaimPrefix, StringComparison.Ordinal))
        {
            return await HandleMoveDestinationPickedAsync(customId, appUserId, SelectedSlotId(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.MoveBenchPrefix, StringComparison.Ordinal))
        {
            return await HandleMoveBenchAsync(customId, appUserId, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.MoveMemberButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.MoveMemberButtonPrefix);
            return await HandleMoveStartAsync(eventId, appUserId, cancellationToken);
        }

        // Officer "Set Leader" — pick a seated member → set their party's 👑.
        if (customId.StartsWith(DiscordEventMessageBuilder.SetLeaderPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.SetLeaderPickPrefix);
            return await HandleSetLeaderPickedAsync(eventId, appUserId, SelectedValue(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.SetLeaderButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.SetLeaderButtonPrefix);
            return await HandleSetLeaderStartAsync(eventId, appUserId, cancellationToken);
        }

        // Officer "🔒 Lock Member" — pick a seated member (value = s:{slotId}), toggle their
        // "stay next window" lock. The pick prefix is checked first (it's the more specific
        // string, though the two don't actually overlap under StartsWith).
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerLockPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerLockPickPrefix);
            return await HandleOfficerLockPickedAsync(eventId, appUserId, SelectedValue(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerLockButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerLockButtonPrefix);
            return await HandleOfficerLockStartAsync(eventId, appUserId, cancellationToken);
        }

        // Officer "Remove Member" — pick a participant → confirm → remove completely.
        if (customId.StartsWith(DiscordEventMessageBuilder.WithdrawMemberConfirmPrefix, StringComparison.Ordinal))
        {
            return await HandleWithdrawConfirmAsync(customId, appUserId, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.WithdrawMemberPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.WithdrawMemberPickPrefix);
            return await HandleWithdrawPickedAsync(eventId, appUserId, SelectedValue(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.WithdrawMemberButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.WithdrawMemberButtonPrefix);
            return await HandleWithdrawStartAsync(eventId, appUserId, cancellationToken);
        }

        // Officer-add member picker → an existing member (value = AppUserId) or "add new"
        // (sentinel → name modal). The chosen target is cached, then the slot picker shows.
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddMemberPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerAddMemberPickPrefix);
            return await HandleOfficerAddMemberPickedAsync(eventId, appUserId, SelectedValue(data), cancellationToken);
        }

        // Officer-add slot picker select → claim the chosen slot for the cached target member.
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddSlotClaimPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerAddSlotClaimPrefix);
            return await _partyBoard.HandlePartySlotClaimAsync(
                eventId, SelectedSlotId(data), appUserId, asLeader: false, cancellationToken, officerAdd: true);
        }

        // Officer-add job wizard (role → main → sub) — same shape as the self-signup
        // wizard, but each step re-enters with officerAdd:true so the final claim is
        // attributed to the cached target rather than the clicking officer.
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddWizardRolePrefix, StringComparison.Ordinal))
        {
            var p = customId[DiscordEventMessageBuilder.OfficerAddWizardRolePrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            return await _partyBoard.AdvancePartyJobWizardAsync(
                eventId, slotId, SelectedValue(data), null, null, false, appUserId, asLeader: false, cancellationToken, officerAdd: true);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddWizardMainPrefix, StringComparison.Ordinal))
        {
            var p = customId[DiscordEventMessageBuilder.OfficerAddWizardMainPrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            var role = p.Length > 2 ? NormalizeWizardValue(p[2]) : null;
            return await _partyBoard.AdvancePartyJobWizardAsync(
                eventId, slotId, role, SelectedValue(data), null, false, appUserId, asLeader: false, cancellationToken, officerAdd: true);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddWizardSubPrefix, StringComparison.Ordinal))
        {
            var p = customId[DiscordEventMessageBuilder.OfficerAddWizardSubPrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            var role = p.Length > 2 ? NormalizeWizardValue(p[2]) : null;
            var main = p.Length > 3 ? NormalizeWizardValue(p[3]) : null;
            return await _partyBoard.AdvancePartyJobWizardAsync(
                eventId, slotId, role, main, SelectedValue(data), true, appUserId, asLeader: false, cancellationToken, officerAdd: true);
        }

        // "Sign Up (No Slot)" → open an ephemeral job-pick wizard (role optional, job
        // required) so the attendee still says what they're coming as.
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyJoinEventPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartyJoinEventPrefix);
            return await _partyBoard.StartGeneralJoinAsync(eventId, appUserId, cancellationToken);
        }

        // General-join wizard selects (role → main → sub). Raw picks ride in the
        // custom_id; the sub step (or last needed step) creates the attendance row.
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyJoinWizardRolePrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartyJoinWizardRolePrefix);
            return await _partyBoard.AdvanceGeneralJoinWizardAsync(eventId, SelectedValue(data), null, null, false, appUserId, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyJoinWizardMainPrefix, StringComparison.Ordinal))
        {
            var p = customId[DiscordEventMessageBuilder.PartyJoinWizardMainPrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var role = p.Length > 1 ? p[1] : null; // raw (may be the "no role" sentinel)
            return await _partyBoard.AdvanceGeneralJoinWizardAsync(eventId, role, SelectedValue(data), null, false, appUserId, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyJoinWizardSubPrefix, StringComparison.Ordinal))
        {
            var p = customId[DiscordEventMessageBuilder.PartyJoinWizardSubPrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var role = p.Length > 1 ? p[1] : null; // raw
            var main = p.Length > 2 ? p[2] : null; // raw
            return await _partyBoard.AdvanceGeneralJoinWizardAsync(eventId, role, main, SelectedValue(data), true, appUserId, cancellationToken);
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
            return await _partyBoard.AdvancePartyJobWizardAsync(
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
            return await _partyBoard.AdvancePartyJobWizardAsync(
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
            return await _partyBoard.AdvancePartyJobWizardAsync(
                eventId, slotId, role, main, SelectedValue(data), true, appUserId, isLeaderSub, cancellationToken);
        }

        // The auction card's item picker → the bid-amount modal for the picked item, the same one
        // the old per-item buttons opened. The option's value is the auction item id.
        if (customId.StartsWith(AuctionBidService.PickSelectPrefix, StringComparison.Ordinal))
        {
            var itemId = int.TryParse(SelectedValue(data), out var picked) ? picked : 0;
            return await BidModalAsync(itemId, appUserId, cancellationToken);
        }

        if (customId.StartsWith(AuctionBidService.MyBidsPrefix, StringComparison.Ordinal))
        {
            var auctionId = ParseTrailingId(customId, AuctionBidService.MyBidsPrefix);
            return await MyAuctionBidsAsync(auctionId, appUserId, cancellationToken);
        }

        // Auction bid button → open the bid-amount modal. The bid itself is
        // placed on modal submit (handled in HandleModalSubmitAsync).
        if (customId.StartsWith(AuctionBidService.BidButtonPrefix, StringComparison.Ordinal))
        {
            var itemId = ParseTrailingId(customId, AuctionBidService.BidButtonPrefix);
            return await BidModalAsync(itemId, appUserId, cancellationToken);
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
            "slot" => _partyBoard.HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: false, cancellationToken),
            "slotL" => _partyBoard.HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: true, cancellationToken),
            "join" => _partyBoard.StartGeneralJoinAsync(eventId, appUserId, cancellationToken),
            "job" => _partyBoard.HandleJobSignupAsync(eventId, appUserId, job, cancellationToken),
            "xin" => _hnmCamp.HandleXinAsync(eventId, appUserId, cancellationToken),
            _ => Task.FromResult(Ephemeral("That action isn't recognized."))
        };

    // Returns a Discord modal (type 9) asking for the bid amount. custom_id
    // carries the auction item id so the submit handler knows what to bid on.
    //
    // The label and placeholder name the DKP pool this auction draws from and how much the bidder
    // has in it — otherwise someone in a linkshell with separate Sky and Sea wallets has no way to
    // know which one they're spending, and finds out only when the bid is rejected.
    private async Task<IActionResult> BidModalAsync(int itemId, string? appUserId, CancellationToken cancellationToken)
    {
        if (itemId <= 0)
        {
            return Ephemeral("That auction item isn't recognized.");
        }

        // Both default to the plain, pool-free text this modal has always shown. A linkshell with
        // one pool never sees a difference, and if we can't resolve the caller we quietly fall back
        // rather than block the bid.
        var label = "Your bid (DKP)";
        var placeholder = "e.g. 100";

        var auction = await _db.AuctionItems
            .Where(item => item.Id == itemId)
            .Select(item => new { item.Auction!.LinkshellId, item.Auction.DkpPoolId })
            .FirstOrDefaultAsync(cancellationToken);

        if (auction is not null && !string.IsNullOrWhiteSpace(appUserId))
        {
            var map = await _dkpPools.GetMapAsync(auction.LinkshellId, cancellationToken);
            if (map.HasMultiplePools)
            {
                var poolId = auction.DkpPoolId ?? map.DefaultPoolId;
                var poolName = map.NameFor(poolId);
                var available = await AuctionDkpService.ComputePoolAvailableDkpAsync(
                    _db, _dkpPoolBalances, appUserId, auction.LinkshellId, poolId, cancellationToken);

                // Discord caps a text input's label at 45 chars and its placeholder at 100.
                label = Truncate($"Your bid ({poolName} DKP)", 45);
                placeholder = Truncate($"You have {available:0.##} {poolName} DKP available", 100);
            }
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
                                label,
                                style = 1, // short
                                min_length = 1,
                                max_length = 7,
                                required = true,
                                placeholder
                            }
                        }
                    }
                }
            }
        });
    }

    // The auction card's "My bids": an ephemeral list of the clicker's bids on that auction and
    // whether each still leads. Needs a linked account -- bids are recorded against it.
    private async Task<IActionResult> MyAuctionBidsAsync(int auctionId, string? appUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appUserId))
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }

        // Only this member's own bids are loaded; the card's numbering needs every item.
        var items = await _db.AuctionItems
            .AsNoTracking()
            .Include(item => item.Bids.Where(bid => bid.AppUserId == appUserId))
            .Where(item => item.AuctionId == auctionId)
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return Ephemeral("That auction has closed — see its results in the app.");
        }

        return Ephemeral(AuctionBoardMessageBuilder.BuildMyBidsText(items, appUserId));
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

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
        Interaction.InteractionToken = root.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() : null;
        Interaction.DiscordUserId = discordUserId;
        Interaction.DiscordDisplayName = ResolveDiscordDisplayName(root);

        // "🏁 Pop / End Camp" modal (every windowed HNM board) → officers only: log the ToD, cap
        // credit at the pop window, record claim/kill, tear the camp down, and stage its roster as
        // a pending review row in the Event System page's attendance sections. Delegates to the shared HnmCampPopService.
        if (customId.StartsWith(WdPopModalPrefix, StringComparison.Ordinal))
        {
            var popEventId = int.TryParse(customId[WdPopModalPrefix.Length..], out var pid) ? pid : 0;
            if (popEventId <= 0)
            {
                return Ephemeral("That event isn't recognized.");
            }
            var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == popEventId, cancellationToken);
            if (ev is null)
            {
                return Ephemeral("That event is no longer open.");
            }
            if (!DiscordEventMessageBuilder.UsesWindows(ev))
            {
                return Ephemeral("This board doesn't use windows.");
            }
            if (ev.WdFinalizedAt is not null)
            {
                return Ephemeral("This camp has already been processed.");
            }

            var officerAppUserId = await _db.DiscordActivityUsers
                .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
                .Select(link => link.IdentityUserId!)
                .FirstOrDefaultAsync(cancellationToken);
            if (!await _gates.CanEndCampAsync(officerAppUserId, ev.LinkshellId, cancellationToken))
            {
                return Ephemeral("You need the **Manage events** permission to end the camp.");
            }

            // NQ/HQ only exists for the three merge-pair families, and it's asked even when we didn't
            // claim (which spawn it was still drives the next pop).
            var hasHq = HnmConfig.HasHqVariant(ev.AssignedMonsterName);
            bool? hq = hasHq ? ParseYesNo(ExtractModalValue(data, WdPopHqFieldId), defaultValue: false) : null;
            // The outcome is ONE three-way field again. A modal opened while it was briefly two
            // Yes/No dropdowns still submits those ids, so they win when present — an old modal
            // sitting open on someone's screen must record what they actually picked rather than
            // fall through to the merged field's default.
            var claimRaw = ExtractModalValue(data, WdPopClaimFieldId);
            var killRaw = ExtractModalValue(data, WdPopKillFieldId);
            var (claimed, killed) = claimRaw is null && killRaw is null
                ? ParseCampOutcome(ExtractModalValue(data, WdPopOutcomeFieldId))
                : (ParseYesNo(claimRaw, defaultValue: true), ParseYesNo(killRaw, defaultValue: true));

            // The window it popped on, capping credit. The modal no longer ASKS — the Date box took
            // its row — so this is normally null and the pop service falls back to the board's own
            // counter, which is exactly what the picker's "I don't know" answer already did.
            //
            // Still READ, because a modal opened before the picker was removed can be submitted
            // afterwards, and an officer who did pick a window deserves to have that answer kept
            // rather than quietly replaced by the fallback. "unknown" and absent both mean null,
            // which is the same thing here.
            var effectiveWindows = DiscordEventMessageBuilder.EffectiveWindowCount(ev);
            var popWindowRaw = ExtractModalValue(data, WdPopWindowFieldId)?.Trim();
            int? popWindow = int.TryParse(popWindowRaw, out var pw) && pw >= 1
                ? Math.Clamp(pw, 1, effectiveWindows)
                : (int?)null;

            // Day number is still not asked — it only finds a value when a modal opened before its
            // removal is submitted afterwards. Absent (the normal path) leaves the board's day alone.
            int? dayNumber = int.TryParse(ExtractModalValue(data, WdPopDayFieldId)?.Trim(), out var dn) && dn > 0
                ? dn : (int?)null;
            // The re-post lead: a positive number of hours enables Repeat-on-ToD at that lead
            // (creating this monster's standing board if it has none), "no"/"off"/"0" turns it off
            // for this cycle, and blank leaves whatever is configured untouched.
            //
            // FRACTIONAL, matching the quarter-hour box on the app's own forms. It took whole hours
            // only while nothing could set a fractional lead but the End Camp form itself; the
            // create-event form asks for the lead now, so "1.5" is an ordinary value to arrive here
            // — and rejecting it would have meant an officer's prefilled 1.5 could only be submitted
            // by first changing it.
            //
            // The off-spellings are matched BEFORE the number, so "0" reads as "turn it off" rather
            // than falling through to the reject below as a non-positive number.
            //
            // Anything else is an error rather than a shrug. This field used to swallow whatever it
            // couldn't parse, which on an optional field is the worst of both worlds: the officer
            // typed something, the camp closed reporting success, and the re-post they thought they
            // had set silently never existed. Bailing here costs them a re-open — nothing has been
            // written yet at this point — which is the cheaper of the two mistakes.
            bool? repost = null;
            double? repostLead = null;
            var repostRaw = ExtractModalValue(data, WdPopRepostFieldId)?.Trim();
            if (!string.IsNullOrEmpty(repostRaw))
            {
                if (repostRaw.Equals("no", StringComparison.OrdinalIgnoreCase)
                    || repostRaw.Equals("off", StringComparison.OrdinalIgnoreCase)
                    || repostRaw == "0")
                {
                    repost = false;
                }
                else if (double.TryParse(repostRaw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var lead)
                    && double.IsFinite(lead) && lead > 0)
                {
                    repost = true;
                    // Same ceiling HnmRecurringBoardService clamps to.
                    repostLead = Math.Min(lead, 168);
                }
                else
                {
                    return Ephemeral(
                        $"**Repost Event** takes a number of hours (like `1`, `1.5` or `4`), or `no` to turn "
                        + $"re-posting off — `{repostRaw}` is neither. Nothing was saved; hit **End Camp** again.");
                }
            }

            // Time of Death: blank/"now" → now; "HH:mm[:ss]" or a full date-time → the officer's local
            // time, converted to UTC. Unparseable → error (don't silently guess a time).
            var officerTimeZone = string.IsNullOrEmpty(officerAppUserId)
                ? null
                : await _db.Users.Where(u => u.Id == officerAppUserId).Select(u => u.TimeZone).FirstOrDefaultAsync(cancellationToken);
            if (!TryParseCampTod(
                    _timeZones,
                    ExtractModalValue(data, WdPopTodDateFieldId),
                    ExtractModalValue(data, WdPopTodFieldId),
                    officerTimeZone, out var todUtc, out var todError))
            {
                return Ephemeral(todError!);
            }

            var isWd = DiscordEventMessageBuilder.IsWd(ev);
            var popService = HttpContext.RequestServices.GetRequiredService<HnmCampPopService>();
            HnmCampPopService.Result result;
            try
            {
                result = await popService.PopAsync(new HnmCampPopService.Request(
                    EventId: popEventId,
                    TodTimeUtc: todUtc,
                    Cooldown: null,
                    Interval: null,
                    DayNumber: dayNumber,
                    Claimed: claimed,
                    Killed: killed,
                    PopWindow: popWindow, // null → the pop service falls back to the board's counter
                    Hq: hq ?? false,
                    Repost: repost,
                    RepostLeadHours: repostLead), cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The camp (or a member's balance) was saved by someone else while this ran, so the
                // event's xmin check turned the save away. Say so in Discord rather than letting the
                // interaction fail with no message (see HnmCampPopService.PopAsync).
                return Ephemeral(ConcurrencyRetry.ConflictMessage);
            }
            if (!result.Success)
            {
                return Ephemeral(result.Error ?? "Couldn't end the camp.");
            }

            // Nothing to say when it worked: the board has already turned into the "defeated" note.
            // The exception is a camp ended with no ToD on a monster that re-posts, because nothing
            // on the board says the re-post isn't coming.
            if (todUtc is null && !isWd)
            {
                return Ephemeral(
                    "Camp ended, but no Time of Death was recorded — the tracker shows **Not entered** "
                    + "and the board won't auto-re-post. Post the next one by hand.");
            }
            return Ok(new { type = ResponseDeferredUpdate });
        }

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

        // Officer-only "find a member by name" modal → look the typed name up among the people
        // ALREADY on this linkshell's roster and seat the one meant. Nobody is created here: a
        // player joins the roster by signing up from the board themselves. One match goes
        // straight to the slot picker; several show a picker of just those. Re-checks the
        // officer here since a modal submit is its own request.
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddFindModalPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerAddFindModalPrefix);
            if (eventId <= 0)
            {
                return Ephemeral("That event isn't recognized.");
            }
            var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
            if (ev is null || ev.PartySetup is null)
            {
                return Ephemeral("That event is no longer open.");
            }

            var officerAppUserId = await _db.DiscordActivityUsers
                .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
                .Select(link => link.IdentityUserId!)
                .FirstOrDefaultAsync(cancellationToken);
            if (!await _gates.CanEditPartyBoardAsync(ev, officerAppUserId, cancellationToken))
            {
                return Ephemeral("You need the **Manage parties** permission to add members to the board.");
            }

            var typed = ExtractModalValue(data, DiscordEventMessageBuilder.OfficerAddFindNameFieldId)?.Trim();
            if (string.IsNullOrWhiteSpace(typed))
            {
                return Ephemeral("Enter a character name to look for.");
            }

            var roster = await LinkshellRosterNames.LoadCandidatesAsync(_db, ev.LinkshellId, cancellationToken);
            var matches = RosterMemberSearch.Find(roster, typed);
            if (matches.Count == 0)
            {
                return Ephemeral(
                    $"Nobody on the roster matches \"{typed}\". Add Member only seats people already on the roster — a new player joins by signing up from the board themselves.");
            }
            if (matches.Count == 1)
            {
                return await SetOfficerAddTargetAsync(ev, discordUserId, matches[0].AppUserId, cancellationToken);
            }

            // Several matches: a picker of just those, alts shown so two similar names can be told
            // apart. The pick lands on the normal member-picker handler.
            var options = matches
                .Select(match => new
                {
                    Name = (match.MembershipCharacterName ?? match.AccountCharacterName ?? "Member").Trim(),
                    Alts = string.Join(", ", new[] { match.AltCharacterName1, match.AltCharacterName2 }
                        .Where(alt => !string.IsNullOrWhiteSpace(alt)).Select(alt => alt!.Trim())),
                    match.AppUserId,
                })
                .OrderBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
                .Take(25)
                .Select(match =>
                {
                    var label = match.Alts.Length > 0 ? $"{match.Name} (alts: {match.Alts})" : match.Name;
                    return (object)new { label = label.Length > 100 ? label[..100] : label, value = match.AppUserId };
                })
                .ToArray();
            var heading = matches.Count > 25
                ? $"{matches.Count} members match \"{typed}\" — showing 25, type more of the name to narrow it. Pick who to seat:"
                : $"{matches.Count} members match \"{typed}\" — pick who to seat:";
            return PickerResponse(
                EventHeading(ev.EventName, heading),
                SelectRow(DiscordEventMessageBuilder.OfficerAddMemberPickPrefix, eventId.ToString(), "Pick a member", options));
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
                _db, _dkpPools, _dkpPoolBalances, account.IdentityUserId!, fallbackName, itemId, amount, cancellationToken);

            return Ephemeral(result.Success
                ? $"✅ Bid placed: {result.Amount} DKP on {result.ItemName ?? "the item"}."
                : result.Error ?? "Placing your bid failed.");
        }

        return Ephemeral("That action isn't recognized.");
    }

    // Loose yes/no parse for modal text inputs (blank falls back to the default).
    private static bool ParseYesNo(string? text, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(text)) return defaultValue;
        return text.Trim().ToLowerInvariant() switch
        {
            "y" or "yes" or "true" or "1" => true,
            "n" or "no" or "false" or "0" => false,
            _ => defaultValue,
        };
    }

    // The End Camp modal's single "Outcome" field → (claimed, killed). "killed" = we had it and it
    // died; "claimed" = we had it but it got away or wiped us; "missed" = somebody else claimed it.
    // Blank or unrecognized stays on the button's happy path (killed), the same default the old
    // separate kill field used.
    private static (bool Claimed, bool Killed) ParseCampOutcome(string? text)
    {
        var s = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return (true, true);
        return s switch
        {
            "claimed" or "claim" or "c" or "no kill" or "nokill" or "wipe" or "wiped" => (true, false),
            "missed" or "miss" or "m" or "no claim" or "noclaim" or "none" or "no" or "n" => (false, false),
            _ => (true, true),
        };
    }

    // "🏁 End Camp / Enter ToD" (every windowed HNM board) → officers only. Opens the ToD modal.
    private async Task<IActionResult> HandleWdPopButtonAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That board isn't recognized.");
        }
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        if (!DiscordEventMessageBuilder.UsesWindows(ev))
        {
            return Ephemeral("This board doesn't use windows.");
        }
        if (!await _gates.CanEndCampAsync(appUserId, ev.LinkshellId, cancellationToken))
        {
            return Ephemeral("You need the **Manage events** permission to end the camp.");
        }
        if (ev.WdFinalizedAt is not null)
        {
            return Ephemeral("This camp has already been processed.");
        }

        // What this monster's Repeat-on-ToD is set to right now, so the re-post field can show it as
        // its placeholder. Only an ENABLED board has a lead in effect; a disabled one reports null
        // so the field doesn't advertise a re-post that isn't going to happen.
        var monsterName = ev.AssignedMonsterName?.Trim();
        var standingBoard = string.IsNullOrWhiteSpace(monsterName)
            ? null
            : await HnmRecurringBoardService.FindAsync(_db, ev.LinkshellId, monsterName, cancellationToken);
        var currentLead = standingBoard is { Enabled: true, LeadHours: > 0 } ? standingBoard.LeadHours : (double?)null;

        // How this monster's next repop is worked out, for the note on the Time of Death box. The
        // same setup HnmCampPopService reads when the modal comes back.
        var timing = await HttpContext.RequestServices.GetRequiredService<MonsterTimingResolver>()
            .ResolveAsync(ev.LinkshellId, monsterName, cancellationToken);
        var repopFormula = TodRepopNote.Formula(
            TodDurationFormat.Format(timing.CooldownMinutes), timing.AdditionalSeconds);

        return WdPopModal(ev, currentLead, repopFormula);
    }

    // The End Camp / Enter ToD modal, and Discord's cap of 5 components is the whole design
    // constraint. Rows, in order:
    //
    //   1. Time of Death   — text, blank = not entered, accepts seconds
    //   2. Was it HQ?      — the three NQ/HQ families only
    //   3. Outcome         — killed / claimed-no-kill / somebody else claimed it
    //   4. Pop window      — multi-window camps only; REQUIRED, and starts empty
    //   5. Re-post lead    — text, blank = leave the standing Repeat-on-ToD config alone
    //
    // Five on an HQ family, four elsewhere. Rows 2 and 4 are the two that can drop out, and they
    // never both apply to a camp small enough to matter — every HQ family is a 7-window king/dragon,
    // so an HQ camp always shows all five. That is why Claimed/Killed had to fold back into one
    // Outcome dropdown to make room; see the field-id block for why nothing is lost by it.
    //
    // Day # stays absent: the event form owns it, and unlike everything above it isn't something
    // the officer OBSERVED at the camp — it's a value they'd be copying back in.
    private IActionResult WdPopModal(Event ev, double? currentLeadHours, string? repopFormula) =>
        Ok(new
        {
            type = ResponseModal,
            data = new
            {
                custom_id = $"{WdPopModalPrefix}{ev.Id}",
                title = "End Camp / Enter ToD",
                components = BuildWdPopModalFields(ev, currentLeadHours, repopFormula),
            }
        });

    // The modal's rows, split out from the response envelope so the 5-row cap above — the one
    // constraint the whole layout is built around — can be asserted in tests instead of discovered
    // as a Discord 400 the first time an officer ends an HQ camp.
    //
    // `currentLeadHours` is this monster's standing Repeat-on-ToD lead when that board exists AND is
    // enabled, else null. Passed in rather than looked up here so this stays a pure function of the
    // camp; the caller owns the query.
    //
    // `repopFormula` is TodRepopNote.Formula for this monster, null when it has no Additional Seconds.
    public static object[] BuildWdPopModalFields(Event ev, double? currentLeadHours, string? repopFormula = null)
    {
        // `value` PREFILLS the box with real, editable text; `placeholder` is the grey hint that
        // shows only while it's empty. A field carrying a prefill sends no placeholder at all —
        // the two would never be visible at the same moment anyway, and a prefilled box's hint is
        // the value itself.
        //
        // `description` is a line of text under the label, and is what moves the box into a Label
        // (type 18), the only component that can carry one. It still counts as one row, so the
        // 5-row cap is untouched. A text input inside a Label must not have a `label` of its own.
        static object TextRow(string fieldId, string label, string placeholder, bool required,
            int maxLength, string? value = null, string? description = null)
        {
            var input = new Dictionary<string, object?>
            {
                ["type"] = 4, // text input
                ["custom_id"] = fieldId,
                ["style"] = 1, // short
                ["min_length"] = required ? 1 : 0,
                ["max_length"] = maxLength,
                ["required"] = required,
            };
            if (value is { Length: > 0 })
            {
                input["value"] = value;
            }
            else
            {
                input["placeholder"] = placeholder;
            }
            if (description is { Length: > 0 })
            {
                return new Dictionary<string, object?>
                {
                    ["type"] = 18, // label
                    ["label"] = label,
                    ["description"] = description,
                    ["component"] = input,
                };
            }
            input["label"] = label;
            return new { type = 1, components = new object[] { input } };
        }

        // A modal dropdown is a string select (type 3) wrapped in a Label (type 18) — a bare select
        // in an action row is rejected. Label's own `label` caps around 45 chars, so the longer hint
        // goes in `description`. Nothing is pre-selected: the box opens on a "Select…" placeholder and
        // is REQUIRED, so Discord won't submit until the officer actually picks. It has to be required,
        // because a blank outcome parses as "claimed and killed" (ParseCampOutcome) and pays bonuses.
        static object SelectRow(string fieldId, string label, string description,
            params (string Value, string Label)[] options)
        {
            var select = new Dictionary<string, object?>
            {
                ["type"] = 3, // string select
                ["custom_id"] = fieldId,
                ["placeholder"] = "Select…",
                ["required"] = true,
                ["min_values"] = 1,
                ["max_values"] = 1,
                ["options"] = options
                    .Select(option => new Dictionary<string, object?>
                    {
                        ["label"] = option.Label,
                        ["value"] = option.Value,
                    })
                    .ToArray(),
            };
            return new Dictionary<string, object?>
            {
                ["type"] = 18, // label
                ["label"] = label,
                ["description"] = description,
                ["component"] = select,
            };
        }

        // (WindowSelectRow lived here: the pop-window picker's builder — a required select with no
        // default, so the officer had to answer rather than let the board's clock-driven counter be
        // recorded by a submit-without-looking. Nothing renders a required select on this modal any
        // more; SelectRow above covers the optional ones that remain.)

        var fields = new List<object>
        {
            // DATE and TIME are two boxes, and the Date one is why the pop-window picker is gone:
            // Discord caps a modal at 5 rows, an HQ family fills all five, and every HQ family is a
            // 7-window king/dragon — so the camps that most need a date field were exactly the ones
            // with no room. The window is recovered from the ToD tracker's own capture instead.
            //
            // A date has to be VISIBLE, not merely accepted, because the case it exists for is the
            // one where nobody is reading carefully: the mob died overnight and is logged the next
            // morning, or on Monday. A bare time still rolls back a day on its own (see
            // TryParseCampTod), but that only ever reaches yesterday, and an officer who does not
            // know a date can be typed will round to a time that is simply wrong.
            //
            // Blank Date = today, which is the ordinary path and stays a one-box form in practice.
            TextRow(WdPopTodDateFieldId, "Date (blank = today)",
                "Ex: 9/5  ·  9/5/2026  ·  yesterday", false, 12),
            // "now" and blank parse too, and are deliberately NOT in the placeholder: blank is
            // spelled out in the label already, and a placeholder listing every accepted spelling
            // reads as a syntax reference rather than as an example of what to type.
            //
            // The note sits on the box it is about: the time typed here is not the time the reposted
            // board will show when the monster has Additional Seconds.
            TextRow(WdPopTodFieldId, "Time of Death (blank = not entered)",
                "Ex: 9:05:15 PM, or 21:05:15", false, 16,
                description: repopFormula is null ? null : $"Reposted event starts at {repopFormula}."),
        };
        if (HnmConfig.HasHqVariant(ev.AssignedMonsterName))
        {
            fields.Add(SelectRow(WdPopHqFieldId, "NQ or HQ?",
                "Which spawn it was — this drives the next pop even if we didn't claim.",
                ("no", "NQ"), ("yes", "HQ")));
        }
        // One three-way field rather than two Yes/No ones — the row it saves is what pays for the
        // pop window below. Each option answers BOTH halves of the label in one line, which is the
        // reason the merge doesn't cost the officer anything: the fourth combination ("no claim,
        // but we killed it") isn't a state that exists, so nothing here is unreachable.
        // Values are the ones ParseCampOutcome already reads.
        fields.Add(SelectRow(WdPopOutcomeFieldId, "Claim? Kill?",
            "Claim and kill decide the camp's bonuses.",
            ("killed", "Yes, we claimed, and killed"),
            ("claimed", "Yes, we claimed, but did not kill"),
            ("missed", "No, we did not claim")));

        // (The "Which window did it pop on?" picker lived here. It was the fifth row, and the Date
        // box above needed one — Discord caps a modal at 5 rows and an HQ family fills every one of
        // them, so a date and a window picker could not both exist on a king or a dragon, which are
        // the camps that have windows in the first place.
        //
        // The window is not lost, it just stops being typed: the in-game Repop Timer captures the pop
        // window at the moment of the pop and posts it on the ToD, which is a better answer than an
        // officer reconstructing it from memory an hour later. The pop service keeps that reading,
        // and falls back to the board's own counter only when there is none -- see HnmCampPopService.)

        // The auto-re-post lead. Free text rather than a dropdown because the useful values run
        // from "half an hour" to "two days" depending on the monster, and because BLANK has to mean
        // "leave the standing config alone" — a dropdown has no natural way to say that without
        // spending an option on it. Parsed by the same reader that has always handled this field.
        //
        // A monster that already has a lead gets it PREFILLED as a real, editable value rather than
        // hinted at — the officer sees the number itself, edits it or leaves it, and either way what
        // they're looking at is what gets saved. A monster whose standing board is off (or absent)
        // has no lead in effect and shows the example instead: prefilling its stored number there
        // would arm a re-post the officer never asked for, just by their submitting the form.
        //
        // Printed at full precision, because the field takes fractional hours now. It used to round
        // to a whole number to match a whole-number parser, which was safe only while nothing but
        // this modal could set a lead — the app's ToD form takes quarter-hours, so rounding here
        // would quietly rewrite an officer's 1.5 to 2 just by their submitting this modal.
        fields.Add(TextRow(WdPopRepostFieldId, "Repost Event (Hours Before Repop)",
            "Example: 1 = 1 hr before, or no = dont re-post",
            false, 10,
            currentLeadHours is { } saved && saved > 0
                ? saved.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : null));

        return fields.ToArray();
    }

    // Parses the End Camp modal's Time of Death from its TWO boxes — Date and Time — in the
    // officer's local zone.
    //
    // Two fields rather than one because the case this exists for is a mob that died overnight and
    // gets logged the next morning, or a Friday kill entered on Monday. A single box could carry a
    // date and did, but nothing about it said so: an officer who does not know a date is accepted
    // types a bare time, and a bare time silently means today.
    //
    // Date accepts: blank (today), "today", "yesterday", "9/5", "9/5/2026", "2026-09-05".
    // Time accepts: blank (see below), "21:05", "21:05:15", "9:05 PM", "9:05:15 PM", "now".
    //
    //   both blank → todUtc = null, meaning NOT ENTERED: the camp ended without anyone seeing it
    //                die, so no time and no repop get recorded.
    //   date only  → an error. A date is not a Time of Death, and choosing a time on the officer's
    //                behalf would invent the number the whole repop clock is counted from.
    //   time only  → today in the officer's zone. When that is still AHEAD of their LSM clock:
    //                  - by more than LastNightThreshold (3 hours): last night, so rolled back a
    //                    day. That is the 11:50pm kill logged at 12:20am;
    //                  - by up to 3 hours: REFUSED, showing both clocks. A kill typed in soon after
    //                    it happened only looks an hour or two "in the future" when the officer's
    //                    Profile zone is behind the clock they read the time from, and rolling that
    //                    back used to save it 23 hours early (logic-09).
    //   any typed time needs a time zone on the officer's LSM Profile. With none it used to be
    //                read as UTC, hours off for everyone outside it. `now` needs none.
    //
    // Returns false with a user-facing message on unparseable input — a ToD is never silently
    // guessed.
    //
    // Static, and internal so the accepted spellings can be pinned directly in tests. The zone
    // service and `nowUtc` are passed in rather than read off the controller and the system clock
    // for the same reason: every branch here turns on the officer's LOCAL clock, so a test that
    // cannot choose the zone and the moment cannot actually test the rolling-back rules.
    internal static bool TryParseCampTod(
        TimeZoneConversionService timeZones,
        string? rawDate, string? rawTime, string? timeZoneId,
        out DateTime? todUtc, out string? error, DateTime? nowUtc = null)
    {
        todUtc = null;
        error = null;
        var datePart = rawDate?.Trim() ?? string.Empty;
        var timePart = rawTime?.Trim() ?? string.Empty;

        if (datePart.Length == 0 && timePart.Length == 0)
        {
            return true; // null → "Not entered"; the pop service leaves Time + RepopTime unset
        }

        var utcNow = nowUtc ?? DateTime.UtcNow;
        var localNow = timeZones.ToUserTime(utcNow, timeZoneId) ?? utcNow;

        // "now" carries its own date, so a Date beside it is a contradiction rather than extra
        // information — better to say so than to silently pick one of the two.
        if (timePart.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            if (datePart.Length > 0)
            {
                error = "Leave **Date** blank when the time is `now` — `now` already carries its own date.";
                return false;
            }
            todUtc = utcNow;
            return true;
        }

        // A date with no time. Blank/blank above is the deliberate "nobody saw it die"; this is a
        // half-filled form, and guessing midnight would look like a real answer forever after.
        if (timePart.Length == 0)
        {
            error = "Enter a **Time of Death** to go with the date — the repop clock is counted from it. "
                  + "Leave BOTH boxes blank if nobody saw it die.";
            return false;
        }

        // NormalizeMeridiem folds any AM/PM spelling down to the " AM"/" PM" the "tt" specifier
        // wants, so the officer can type it however they like; a 24-hour entry passes through.
        if (!TimeOnly.TryParseExact(
                NormalizeMeridiem(timePart),
                new[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm tt", "h:mm:ss tt" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var timeOnly))
        {
            error = "Enter a valid **Time of Death** — `9:05 PM`, `9:05:15 PM`, `21:05`, or `now`.";
            return false;
        }

        // A typed time is read in the Profile's zone, so without one there is nothing to read it in.
        if (!timeZones.IsKnownZone(timeZoneId))
        {
            error = "Your LSM Profile has no time zone, so a typed **Time of Death** can't be read. "
                  + "Set your time zone on your LSM Profile, then hit **End Camp** again "
                  + "(or type `now` if it just died). Nothing was saved.";
            return false;
        }

        if (!TryResolveCampTodDate(datePart, localNow, out var localDate, out var dateError))
        {
            error = dateError;
            return false;
        }

        var localDt = localDate.ToDateTime(timeOnly, DateTimeKind.Unspecified);
        if (timeZones.ToUtc(localDt, timeZoneId) is not { } candidateUtc)
        {
            return false;
        }

        // Only when NO date was typed. An explicit date is an instruction; second-guessing it would
        // move an officer's deliberate answer by a day. Compared in UTC rather than on the wall
        // clock, so a daylight-saving change between the two moments can't make "how far ahead" an
        // hour wrong.
        if (datePart.Length == 0 && candidateUtc > utcNow)
        {
            if (candidateUtc - utcNow <= LastNightThreshold)
            {
                error = CampTodNotYetMessage(timeZoneId, localDt, localNow);
                return false;
            }
            localDt = localDt.AddDays(-1); // well ahead of "now" today: last night
            if (timeZones.ToUtc(localDt, timeZoneId) is not { } lastNightUtc)
            {
                return false;
            }
            candidateUtc = lastNightUtc;
        }

        todUtc = candidateUtc;
        return true;
    }

    // How far ahead of the LSM clock a time with no date must be before it reads as last night.
    // Up to this it is refused instead: being an hour or two "in the future" is what a Profile zone
    // behind the officer's own clock looks like.
    private static readonly TimeSpan LastNightThreshold = TimeSpan.FromHours(3);

    // Why a ToD was refused for being ahead of the LSM clock, with both clocks spelled out so the
    // officer can see which one is wrong.
    private static string CampTodNotYetMessage(string? timeZoneId, DateTime typedLocal, DateTime localNow) =>
        $"**{FormatLsmClock(typedLocal, withSeconds: typedLocal.Second != 0)}** hasn't happened yet by your "
        + $"LSM clock: it's **{FormatLsmClock(localNow)}** ({timeZoneId}). Nothing was saved.\n"
        + "• If your clock shows a different time, fix the time zone on your LSM Profile, then hit "
        + "**End Camp** again.\n"
        + "• If it died yesterday, type `yesterday` in **Date**.";

    // "Nov 8, 8:10 AM", or with seconds "Nov 8, 9:07:16 AM".
    private static string FormatLsmClock(DateTime local, bool withSeconds = false) =>
        local.ToString(withSeconds ? "MMM d, h:mm:ss tt" : "MMM d, h:mm tt",
            System.Globalization.CultureInfo.InvariantCulture);

    // The Date box → a local calendar day. Blank is today.
    //
    // A year-less "9/5" resolves to its MOST RECENT occurrence at or before today, which is what
    // carries it across New Year: "12/31" entered on January 1st is a few days ago, not eleven
    // months away. An explicit year is taken exactly as typed, because the officer said it.
    private static bool TryResolveCampTodDate(
        string datePart, DateTime localNow, out DateOnly localDate, out string? error)
    {
        var today = DateOnly.FromDateTime(localNow);
        localDate = today;
        error = null;

        if (datePart.Length == 0 || datePart.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (datePart.Equals("yesterday", StringComparison.OrdinalIgnoreCase)
            || datePart.Equals("yday", StringComparison.OrdinalIgnoreCase))
        {
            localDate = today.AddDays(-1);
            return true;
        }

        // Explicit year FIRST: "9/5/2026" would otherwise never be reached, and the year the
        // officer typed would be silently replaced by the most-recent-occurrence rule below.
        if (DateOnly.TryParseExact(
                datePart,
                new[] { "M/d/yyyy", "M-d-yyyy", "yyyy-MM-dd", "yyyy/M/d" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var exact))
        {
            localDate = exact;
            return true;
        }

        if (DateOnly.TryParseExact(
                datePart,
                new[] { "M/d", "M-d" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var yearless))
        {
            // TryParseExact defaults a missing year to the CURRENT one, so this only steps back
            // when that lands ahead of the officer's own day.
            var resolved = yearless.AddYears(today.Year - yearless.Year);
            if (resolved > today)
            {
                resolved = resolved.AddYears(-1);
            }
            localDate = resolved;
            return true;
        }

        error = "Enter a valid **Date** — blank for today, `yesterday`, `9/5`, `9/5/2026`, or `2026-09-05`.";
        return false;
    }

    // Rewrites a trailing AM/PM marker — "pm", " PM", "p.m.", bare "p" — as the " AM"/" PM" that
    // the "tt" format specifier matches, leaving everything before it (clock time or full date-time)
    // alone. Input with no such marker comes back unchanged, so 24-hour entries are unaffected.
    private static readonly System.Text.RegularExpressions.Regex MeridiemSuffix = new(
        @"^(?<head>.*\d)\s*(?<half>[ap])\.?\s*m?\.?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string NormalizeMeridiem(string text)
    {
        var match = MeridiemSuffix.Match(text);
        return match.Success
            ? $"{match.Groups["head"].Value.TrimEnd()} {char.ToUpperInvariant(match.Groups["half"].Value[0])}M"
            : text;
    }

    // ─── Officer "Add Member" ───────────────────────────────────────────────────────────

    // "➕ Add Member (officers)" → officers only: an ephemeral select of roster members who
    // aren't already on this board, plus a "find a member by name" option (opens a name modal).
    // The select is capped at Discord's 25-option limit; rosters past that are reachable via
    // the find, which only ever matches people already on the roster (RosterMemberSearch).
    private async Task<IActionResult> HandleOfficerAddStartAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }
        var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        if (!await _gates.CanEditPartyBoardAsync(ev, appUserId, cancellationToken))
        {
            return Ephemeral("You need the **Manage parties** permission to add members to the board.");
        }

        // Exclude anyone already on the board (a slot signup or a no-slot attendance) so the
        // list is just people who still need seating.
        var slotUserIds = await _db.EventPartySlotSignups
            .Where(s => s.EventId == eventId && s.AppUserId != null)
            .Select(s => s.AppUserId!)
            .ToListAsync(cancellationToken);
        var attendeeUserIds = await _db.AppUserEvents
            .Where(p => p.EventId == eventId && p.AppUserId != null)
            .Select(p => p.AppUserId!)
            .ToListAsync(cancellationToken);
        var onBoard = new HashSet<string>(slotUserIds.Concat(attendeeUserIds), StringComparer.Ordinal);

        var members = await _db.AppUserLinkshells
            .Where(m => m.LinkshellId == ev.LinkshellId && m.AppUserId != null && m.CharacterName != null)
            .Select(m => new { m.AppUserId, m.CharacterName })
            .ToListAsync(cancellationToken);

        var eligible = members
            .Where(m => !onBoard.Contains(m.AppUserId!))
            .OrderBy(m => m.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var options = eligible
            .Take(24) // 25-option cap, less one reserved for "find a member by name"
            .Select(m => (object)new { label = m.CharacterName!.Trim(), value = m.AppUserId })
            .Append((object)new { label = "🔎 Find a member by name…", value = DiscordEventMessageBuilder.OfficerAddFindSentinel })
            .ToArray();

        // Say when the list is cut short. Not knowing the rest are reachable is what used to send
        // officers to type a name that then created a duplicate of someone already on the roster.
        var heading = eligible.Count > 24
            ? $"Add a member to the board — pick who to seat (showing 24 of {eligible.Count}; use 🔎 Find for anyone else):"
            : "Add a member to the board — pick who to seat:";
        return PickerResponse(
            EventHeading(ev.EventName, heading),
            SelectRow(DiscordEventMessageBuilder.OfficerAddMemberPickPrefix, eventId.ToString(), "Pick a member", options));
    }

    // Officer-add member picker select → either open the "find a member by name" modal, or
    // (a listed member) cache them as the add target and show the slot picker.
    private async Task<IActionResult> HandleOfficerAddMemberPickedAsync(
        int eventId, string? appUserId, string? value, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }
        var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        if (!await _gates.CanEditPartyBoardAsync(ev, appUserId, cancellationToken))
        {
            return Ephemeral("You need the **Manage parties** permission to add members to the board.");
        }
        if (string.IsNullOrEmpty(Interaction.DiscordUserId))
        {
            return Ephemeral("Couldn't read your Discord account from that click.");
        }

        if (string.Equals(value, DiscordEventMessageBuilder.OfficerAddFindSentinel, StringComparison.Ordinal))
        {
            return OfficerAddFindModal(eventId);
        }

        return await SetOfficerAddTargetAsync(ev, Interaction.DiscordUserId, value, cancellationToken);
    }

    // The chosen roster member becomes the officer-add target (cached per officer + event), and
    // the slot picker shows. Shared by a pick from the member list and by the find modal.
    private async Task<IActionResult> SetOfficerAddTargetAsync(
        Event ev, string officerDiscordUserId, string? targetAppUserId, CancellationToken cancellationToken)
    {
        var membership = string.IsNullOrEmpty(targetAppUserId)
            ? null
            : await _db.AppUserLinkshells
                .FirstOrDefaultAsync(m => m.LinkshellId == ev.LinkshellId && m.AppUserId == targetAppUserId, cancellationToken);
        if (membership?.AppUserId is null)
        {
            return Ephemeral("That member isn't in this linkshell anymore.");
        }

        var targetName = membership.CharacterName?.Trim() is { Length: > 0 } cn ? cn : "Member";
        _officerAddTargets.Set(officerDiscordUserId, ev.Id,
            new OfficerAddTargetCache.Target(membership.AppUserId, targetName, membership.DiscordUserId));

        return await ShowOfficerAddSlotPickerAsync(ev, targetName, cancellationToken);
    }

    // The "find a member by name" modal: the officer types a name (main or alt, or part of
    // one). On submit (HandleModalSubmitAsync) it's looked up among the people already on the
    // roster. A single text field — Discord modals are text-only.
    private IActionResult OfficerAddFindModal(int eventId)
    {
        return Ok(new
        {
            type = ResponseModal,
            data = new
            {
                custom_id = $"{DiscordEventMessageBuilder.OfficerAddFindModalPrefix}{eventId}",
                title = "Find a member",
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
                                custom_id = DiscordEventMessageBuilder.OfficerAddFindNameFieldId,
                                label = "Character name (main or alt)",
                                style = 1, // short
                                min_length = 1,
                                max_length = ManualMemberService.MaxCharacterNameLength,
                                required = true,
                                placeholder = "e.g. Millhouse",
                            }
                        }
                    }
                }
            }
        });
    }

    // Ephemeral OPEN-slot picker for the officer-add flow, routed through the officer-add
    // claim prefix so a pick seats the cached target member. `ev` must have its party-setup
    // tree loaded.
    private async Task<IActionResult> ShowOfficerAddSlotPickerAsync(
        Event ev, string targetName, CancellationToken cancellationToken)
    {
        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, ev.Id, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildSlotPickerComponents(
            ev.Id, ev.PartySetup!, slotSignups, asLeader: false,
            claimPrefixOverride: DiscordEventMessageBuilder.OfficerAddSlotClaimPrefix);
        if (picker.Length == 0)
        {
            return Ephemeral("Every slot is taken right now — free one up first.");
        }
        return PickerResponse(
            EventHeading(ev.EventName, $"Seat {targetName} — pick a slot:"),
            picker);
    }

    // ─── Officer "Move / Set Leader / Remove Member" ────────────────────────────────────
    //
    // All three share the participant picker (BuildMoveSourceComponents). The chosen
    // member rides in the select VALUE as a source token: "s:{slotId}" (seated),
    // "a:{appUserId}" or "d:{discordUserId}" (Also Attending). Each step re-checks the
    // officer gate (the buttons are visible to everyone). Final actions reuse the same
    // backend the web Activity uses.

    private static bool IsValidSourceToken(string? src)
        => src is not null && src.Length > 2
           && (src.StartsWith("s:", StringComparison.Ordinal)
               || src.StartsWith("a:", StringComparison.Ordinal)
               || src.StartsWith("d:", StringComparison.Ordinal));

    // Maps a source token → MoveMemberAsync's (fromSlotId, appUserId, discordUserId).
    private static (int? FromSlotId, string? AppUserId, string? DiscordUserId) MapSource(string src)
    {
        var val = src.Length > 2 ? src[2..] : string.Empty;
        if (src.StartsWith("s:", StringComparison.Ordinal))
        {
            return (int.TryParse(val, out var slotId) ? slotId : (int?)null, null, null);
        }
        if (src.StartsWith("a:", StringComparison.Ordinal)) { return (null, val, null); }
        if (src.StartsWith("d:", StringComparison.Ordinal)) { return (null, null, val); }
        return (null, null, null);
    }

    // Splits "{prefix}{eventId}:{kind}:{val}[:{ai}]" → (eventId, "kind:val").
    private static (int EventId, string? Src) ParseEventAndSource(string customId, string prefix)
    {
        var parts = customId[prefix.Length..].Split(':');
        var eventId = parts.Length > 0 && int.TryParse(parts[0], out var e) ? e : 0;
        var src = parts.Length >= 3 ? $"{parts[1]}:{parts[2]}" : null;
        return (eventId, src);
    }

    // Builds the "Also Attending" picker options (no-slot AppUserEvent rows that aren't
    // currently seated), value-encoded as a:/d: source tokens.
    private async Task<List<(string Label, string Value)>> LoadAttendeeSourceOptionsAsync(
        int eventId, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, CancellationToken cancellationToken)
    {
        var seatedAppUsers = new HashSet<string>(
            slotSignups.Values.Where(s => s.AppUserId != null).Select(s => s.AppUserId!), StringComparer.Ordinal);
        var seatedDiscord = new HashSet<string>(
            slotSignups.Values.Where(s => s.DiscordUserId != null).Select(s => s.DiscordUserId!), StringComparer.Ordinal);

        var attendees = await _db.AppUserEvents
            .AsNoTracking()
            .Where(p => p.EventId == eventId)
            .Select(p => new { p.AppUserId, p.DiscordUserId, p.CharacterName, p.JobType, p.JobName, p.SubJobName })
            .ToListAsync(cancellationToken);

        var options = new List<(string Label, string Value)>();
        foreach (var a in attendees)
        {
            if (a.AppUserId != null && seatedAppUsers.Contains(a.AppUserId)) { continue; }
            if (a.AppUserId == null && a.DiscordUserId != null && seatedDiscord.Contains(a.DiscordUserId)) { continue; }
            var value = a.AppUserId != null ? $"a:{a.AppUserId}"
                : a.DiscordUserId != null ? $"d:{a.DiscordUserId}"
                : null;
            if (value is null) { continue; }
            var name = string.IsNullOrWhiteSpace(a.CharacterName) ? "Member" : a.CharacterName!.Trim();
            var role = string.IsNullOrWhiteSpace(a.JobType) ? null : a.JobType!.Trim();
            var job = string.IsNullOrWhiteSpace(a.JobName) ? null
                : (string.IsNullOrWhiteSpace(a.SubJobName) ? a.JobName!.Trim() : $"{a.JobName!.Trim()}/{a.SubJobName!.Trim()}");
            var jobs = string.Join(" - ", new[] { role, job }.Where(s => !string.IsNullOrEmpty(s)));
            options.Add((jobs.Length > 0 ? $"{name} — {jobs}" : name, value));
        }
        return options;
    }

    private async Task<string?> ResolveSourceNameAsync(int eventId, string src, CancellationToken cancellationToken)
    {
        var val = src.Length > 2 ? src[2..] : string.Empty;
        if (src.StartsWith("s:", StringComparison.Ordinal) && int.TryParse(val, out var slotId))
        {
            return await _db.EventPartySlotSignups.AsNoTracking()
                .Where(s => s.EventId == eventId && s.PartySetupSlotId == slotId)
                .Select(s => s.CharacterName).FirstOrDefaultAsync(cancellationToken);
        }
        if (src.StartsWith("a:", StringComparison.Ordinal))
        {
            return await _db.AppUserEvents.AsNoTracking()
                .Where(p => p.EventId == eventId && p.AppUserId == val)
                .Select(p => p.CharacterName).FirstOrDefaultAsync(cancellationToken);
        }
        if (src.StartsWith("d:", StringComparison.Ordinal))
        {
            return await _db.AppUserEvents.AsNoTracking()
                .Where(p => p.EventId == eventId && p.DiscordUserId == val)
                .Select(p => p.CharacterName).FirstOrDefaultAsync(cancellationToken);
        }
        return null;
    }

    // The (appUserId, discordUserId) for a source token, used for a complete removal.
    // For a seated source we read the slot signup so BOTH identity columns are passed
    // (a placeholder-matched slot carries an AppUserId AND a DiscordUserId). Null when
    // the seated source row is gone.
    private async Task<(string? AppUserId, string? DiscordUserId)?> ResolveSourceIdentityAsync(
        int eventId, string src, CancellationToken cancellationToken)
    {
        var val = src.Length > 2 ? src[2..] : string.Empty;
        if (src.StartsWith("s:", StringComparison.Ordinal) && int.TryParse(val, out var slotId))
        {
            var row = await _db.EventPartySlotSignups.AsNoTracking()
                .Where(s => s.EventId == eventId && s.PartySetupSlotId == slotId)
                .Select(s => new { s.AppUserId, s.DiscordUserId })
                .FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : (row.AppUserId, row.DiscordUserId);
        }
        if (src.StartsWith("a:", StringComparison.Ordinal)) { return (val, null); }
        if (src.StartsWith("d:", StringComparison.Ordinal)) { return (null, val); }
        return null;
    }

    // Loads the event (with party setup) and verifies the clicker may edit the party board.
    // Returns the event on success, or an ephemeral error result in `error`.
    private async Task<Event?> LoadOfficerEventAsync(
        int eventId, string? appUserId, string deniedMessage, CancellationToken cancellationToken,
        Func<string, IActionResult> ephemeral, Action<IActionResult> setError)
    {
        if (eventId <= 0) { setError(ephemeral("That event isn't recognized.")); return null; }
        var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null) { setError(ephemeral("That event is no longer open.")); return null; }
        if (!await _gates.CanEditPartyBoardAsync(ev, appUserId, cancellationToken)) { setError(ephemeral(deniedMessage)); return null; }
        return ev;
    }

    // ── Move ──
    private async Task<IActionResult> HandleMoveStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to move members on the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var attendees = await LoadAttendeeSourceOptionsAsync(eventId, slotSignups, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildMoveSourceComponents(
            eventId, ev.PartySetup!, slotSignups, attendees,
            DiscordEventMessageBuilder.MoveSourcePickPrefix, seatedOnly: false);
        if (picker.Length == 0) { return Ephemeral("Nobody's on the board to move yet."); }
        return PickerResponse(EventHeading(ev.EventName, "Move a member — pick who to move:"), picker);
    }

    private async Task<IActionResult> HandleMoveSourcePickedAsync(int eventId, string? appUserId, string? src, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to move members on the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (!IsValidSourceToken(src)) { return Ephemeral("That selection isn't recognized."); }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildSlotPickerComponents(
            eventId, ev.PartySetup!, slotSignups, asLeader: false,
            claimPrefixOverride: DiscordEventMessageBuilder.MoveDestClaimPrefix,
            idSuffixOverride: $":{src}");

        var rows = new List<object>(picker);
        var seated = src!.StartsWith("s:", StringComparison.Ordinal);
        if (seated && rows.Count < 5)
        {
            rows.Add(new
            {
                type = 1,
                components = new object[]
                {
                    new
                    {
                        type = 2, style = 2,
                        label = "⬇ Bench → Also Attending",
                        custom_id = $"{DiscordEventMessageBuilder.MoveBenchPrefix}{eventId}:{src}",
                    },
                },
            });
        }
        if (rows.Count == 0) { return Ephemeral("Every slot is taken and there's nothing to bench."); }
        return PickerResponse(
            EventHeading(ev.EventName, "Pick a destination slot" + (seated ? ", or bench:" : ":")),
            rows.ToArray());
    }

    private async Task<IActionResult> HandleMoveDestinationPickedAsync(string customId, string? appUserId, int slotId, CancellationToken cancellationToken)
    {
        var (eventId, src) = ParseEventAndSource(customId, DiscordEventMessageBuilder.MoveDestClaimPrefix);
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to move members on the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (slotId <= 0 || !IsValidSourceToken(src)) { return Ephemeral("That selection isn't recognized."); }

        var (fromSlotId, mAppUser, mDiscord) = MapSource(src!);
        var result = await EventPartyBoardEditService.MoveMemberAsync(
            _db, eventId, fromSlotId, toSlotId: slotId, mAppUser, mDiscord, cancellationToken);
        if (!result.Success) { return WizardStep($"⚠️ {result.Error}", Array.Empty<object>()); }
        _eventQueue.Enqueue(eventId);
        return _board.DismissPickerSilently();
    }

    private async Task<IActionResult> HandleMoveBenchAsync(string customId, string? appUserId, CancellationToken cancellationToken)
    {
        var (eventId, src) = ParseEventAndSource(customId, DiscordEventMessageBuilder.MoveBenchPrefix);
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to move members on the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (src is null || !src.StartsWith("s:", StringComparison.Ordinal)) { return Ephemeral("Only a seated member can be benched."); }

        var (fromSlotId, _, _) = MapSource(src);
        var result = await EventPartyBoardEditService.MoveMemberAsync(
            _db, eventId, fromSlotId, toSlotId: null, null, null, cancellationToken);
        if (!result.Success) { return WizardStep($"⚠️ {result.Error}", Array.Empty<object>()); }
        _eventQueue.Enqueue(eventId);
        return _board.DismissPickerSilently();
    }

    // ── Set Leader ──
    private async Task<IActionResult> HandleSetLeaderStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to set the party leader.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildMoveSourceComponents(
            eventId, ev.PartySetup!, slotSignups, Array.Empty<(string Label, string Value)>(),
            DiscordEventMessageBuilder.SetLeaderPickPrefix, seatedOnly: true);
        if (picker.Length == 0) { return Ephemeral("Nobody's in a slot to make leader yet."); }
        return PickerResponse(EventHeading(ev.EventName, "Set the 👑 party leader — pick a member:"), picker);
    }

    private async Task<IActionResult> HandleSetLeaderPickedAsync(int eventId, string? appUserId, string? src, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to set the party leader.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (src is null || !src.StartsWith("s:", StringComparison.Ordinal)
            || !int.TryParse(src[2..], out var slotId) || slotId <= 0)
        {
            return Ephemeral("That selection isn't recognized.");
        }

        var result = await EventPartySignupService.SetPartyLeaderBySlotAsync(_db, eventId, slotId, cancellationToken);
        if (!result.Success) { return WizardStep($"⚠️ {result.Error}", Array.Empty<object>()); }
        await _db.SaveChangesAsync(cancellationToken);
        _eventQueue.Enqueue(eventId);
        return _board.DismissPickerSilently();
    }

    // ── Lock Member (stay next window) ──
    // Officers pin a member's slot so it survives the window-turnover wipe. Same shape as
    // Set Leader: button → seated-member picker → the select toggles the chosen slot's lock.
    private async Task<IActionResult> HandleOfficerLockStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to lock members.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildMoveSourceComponents(
            eventId, ev.PartySetup!, slotSignups, Array.Empty<(string Label, string Value)>(),
            DiscordEventMessageBuilder.OfficerLockPickPrefix, seatedOnly: true);
        if (picker.Length == 0) { return Ephemeral("Nobody's in a slot to lock yet."); }
        return PickerResponse(EventHeading(ev.EventName, "🔒 Lock a member for next window — pick who stays:"), picker);
    }

    private async Task<IActionResult> HandleOfficerLockPickedAsync(int eventId, string? appUserId, string? src, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to lock members.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (src is null || !src.StartsWith("s:", StringComparison.Ordinal)
            || !int.TryParse(src[2..], out var slotId) || slotId <= 0)
        {
            return Ephemeral("That selection isn't recognized.");
        }

        var result = await EventPartySignupService.SetStayNextWindowBySlotAsync(_db, eventId, slotId, cancellationToken);
        if (!result.Success) { return WizardStep($"⚠️ {result.Error}", Array.Empty<object>()); }
        await _db.SaveChangesAsync(cancellationToken);
        _eventQueue.Enqueue(eventId);

        // Re-render the picker so an officer can lock/unlock several members in one sitting;
        // the board itself (with the 🔒 marks + count) refreshes via the queue above.
        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildMoveSourceComponents(
            eventId, ev.PartySetup!, slotSignups, Array.Empty<(string Label, string Value)>(),
            DiscordEventMessageBuilder.OfficerLockPickPrefix, seatedOnly: true);
        if (picker.Length == 0) { return _board.DismissPickerSilently(); }
        var who = string.IsNullOrWhiteSpace(result.Name) ? "that member" : result.Name!.Trim();
        var verb = result.Locked ? $"🔒 Locked **{who}** for next window" : $"🔓 Unlocked **{who}**";
        return PickerResponse(EventHeading(ev.EventName, $"{verb} — pick another to toggle, or dismiss:"), picker);
    }

    // ── Remove Member ──
    private async Task<IActionResult> HandleWithdrawStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to remove members from the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var attendees = await LoadAttendeeSourceOptionsAsync(eventId, slotSignups, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildMoveSourceComponents(
            eventId, ev.PartySetup!, slotSignups, attendees,
            DiscordEventMessageBuilder.WithdrawMemberPickPrefix, seatedOnly: false);
        if (picker.Length == 0) { return Ephemeral("Nobody's on the board to remove yet."); }
        return PickerResponse(EventHeading(ev.EventName, "Remove a member — pick who to remove:"), picker);
    }

    private async Task<IActionResult> HandleWithdrawPickedAsync(int eventId, string? appUserId, string? src, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to remove members from the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (!IsValidSourceToken(src)) { return Ephemeral("That selection isn't recognized."); }

        var name = await ResolveSourceNameAsync(eventId, src!, cancellationToken);
        var who = string.IsNullOrWhiteSpace(name) ? "this member" : name!.Trim();
        var confirm = new object[]
        {
            new
            {
                type = 1,
                components = new object[]
                {
                    new
                    {
                        type = 2, style = 4, // danger
                        label = "Remove from event",
                        custom_id = $"{DiscordEventMessageBuilder.WithdrawMemberConfirmPrefix}{eventId}:{src}",
                    },
                },
            },
        };
        return PickerResponse(
            EventHeading(ev.EventName, $"Remove **{who}** from the event entirely? This frees their slot and drops their attendance/DKP."),
            confirm);
    }

    private async Task<IActionResult> HandleWithdrawConfirmAsync(string customId, string? appUserId, CancellationToken cancellationToken)
    {
        var (eventId, src) = ParseEventAndSource(customId, DiscordEventMessageBuilder.WithdrawMemberConfirmPrefix);
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to remove members from the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (!IsValidSourceToken(src)) { return Ephemeral("That selection isn't recognized."); }

        var identity = await ResolveSourceIdentityAsync(eventId, src!, cancellationToken);
        if (identity is null) { return WizardStep("⚠️ That member is no longer on the board.", Array.Empty<object>()); }
        var (mAppUser, mDiscord) = identity.Value;
        await EventPartySignupService.RemoveMemberCompletelyAsync(_db, eventId, mAppUser, mDiscord, cancellationToken);
        _eventQueue.Enqueue(eventId);
        return _board.DismissPickerSilently();
    }

    // A picker/select step in a signup flow. When the click came from an ephemeral
    // picker (an earlier step), MORPH that message in place so the chain never piles
    // up stale "Which character…" messages; otherwise (a board click)
    // send a fresh ephemeral. The terminal step deletes the single message.
    private IActionResult PickerResponse(string content, object[] components) =>
        Interaction.IsEphemeralSource
            ? Ok(new { type = ResponseUpdateMessage, data = new { content, components } })
            : Ok(new { type = ResponseChannelMessage, data = new { content, components, flags = EphemeralFlag } });
}
