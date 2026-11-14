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

    // The "/lsm" slash command (posts a launch card) and its "Join" button (launches the
    // Activity for the clicker via the LAUNCH_ACTIVITY callback). Every event board's green
    // "Open LSManager App" button sends this same id.
    private const string LaunchCommandName = "lsm";
    private const string LaunchButtonId = DiscordEventMessageBuilder.LaunchAppButtonId;

    private readonly DiscordInteractionVerifier _verifier;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<DiscordInteractionsController> _logger;
    private readonly SignupCharacterChoiceCache _charChoice;
    private readonly ManualMemberService _manualMembers;
    private readonly DiscordSignupIdentity _identity;
    private readonly DiscordPermissionGates _gates;
    private readonly PartyBoardHandler _partyBoard;
    private readonly HnmCampBoardHandler _hnmCamp;
    private readonly EndCampHandler _endCamp;
    private readonly AuctionInteractionHandler _auctions;
    private readonly OfficerToolsHandler _officerTools;

    // This request's interaction: the token, the clicker's Discord id + display name, and
    // whether the click came from an ephemeral picker. Shared with the classes above.
    private DiscordInteractionContext Interaction { get; }

    public DiscordInteractionsController(
        DiscordInteractionVerifier verifier,
        ApplicationDbContext db,
        ILogger<DiscordInteractionsController> logger,
        SignupCharacterChoiceCache charChoice,
        ManualMemberService manualMembers,
        DiscordInteractionContext interaction,
        DiscordSignupIdentity identity,
        DiscordPermissionGates gates,
        PartyBoardHandler partyBoard,
        HnmCampBoardHandler hnmCamp,
        EndCampHandler endCamp,
        AuctionInteractionHandler auctions,
        OfficerToolsHandler officerTools)
    {
        _verifier = verifier;
        _db = db;
        _logger = logger;
        _charChoice = charChoice;
        _manualMembers = manualMembers;
        Interaction = interaction;
        _identity = identity;
        _gates = gates;
        _partyBoard = partyBoard;
        _hnmCamp = hnmCamp;
        _endCamp = endCamp;
        _auctions = auctions;
        _officerTools = officerTools;
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
            return await _endCamp.HandleWdPopButtonAsync(eventId, appUserId, cancellationToken);
        }

        // "➕ Add Member (officers)" → officers only: ephemeral picker of who to seat.
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerAddButtonPrefix);
            return await _officerTools.HandleOfficerAddStartAsync(eventId, appUserId, cancellationToken);
        }

        // Officer "Move Member" — pick a participant → pick a destination slot (or bench).
        // The more-specific src/dest/bench prefixes are checked before the bare button.
        if (customId.StartsWith(DiscordEventMessageBuilder.MoveSourcePickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.MoveSourcePickPrefix);
            return await _officerTools.HandleMoveSourcePickedAsync(eventId, appUserId, SelectedValue(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.MoveDestClaimPrefix, StringComparison.Ordinal))
        {
            return await _officerTools.HandleMoveDestinationPickedAsync(customId, appUserId, SelectedSlotId(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.MoveBenchPrefix, StringComparison.Ordinal))
        {
            return await _officerTools.HandleMoveBenchAsync(customId, appUserId, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.MoveMemberButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.MoveMemberButtonPrefix);
            return await _officerTools.HandleMoveStartAsync(eventId, appUserId, cancellationToken);
        }

        // Officer "Set Leader" — pick a seated member → set their party's 👑.
        if (customId.StartsWith(DiscordEventMessageBuilder.SetLeaderPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.SetLeaderPickPrefix);
            return await _officerTools.HandleSetLeaderPickedAsync(eventId, appUserId, SelectedValue(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.SetLeaderButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.SetLeaderButtonPrefix);
            return await _officerTools.HandleSetLeaderStartAsync(eventId, appUserId, cancellationToken);
        }

        // Officer "🔒 Lock Member" — pick a seated member (value = s:{slotId}), toggle their
        // "stay next window" lock. The pick prefix is checked first (it's the more specific
        // string, though the two don't actually overlap under StartsWith).
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerLockPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerLockPickPrefix);
            return await _officerTools.HandleOfficerLockPickedAsync(eventId, appUserId, SelectedValue(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerLockButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerLockButtonPrefix);
            return await _officerTools.HandleOfficerLockStartAsync(eventId, appUserId, cancellationToken);
        }

        // Officer "Remove Member" — pick a participant → confirm → remove completely.
        if (customId.StartsWith(DiscordEventMessageBuilder.WithdrawMemberConfirmPrefix, StringComparison.Ordinal))
        {
            return await _officerTools.HandleWithdrawConfirmAsync(customId, appUserId, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.WithdrawMemberPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.WithdrawMemberPickPrefix);
            return await _officerTools.HandleWithdrawPickedAsync(eventId, appUserId, SelectedValue(data), cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.WithdrawMemberButtonPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.WithdrawMemberButtonPrefix);
            return await _officerTools.HandleWithdrawStartAsync(eventId, appUserId, cancellationToken);
        }

        // Officer-add member picker → an existing member (value = AppUserId) or "add new"
        // (sentinel → name modal). The chosen target is cached, then the slot picker shows.
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddMemberPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerAddMemberPickPrefix);
            return await _officerTools.HandleOfficerAddMemberPickedAsync(eventId, appUserId, SelectedValue(data), cancellationToken);
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
            return await _auctions.BidModalAsync(itemId, appUserId, cancellationToken);
        }

        if (customId.StartsWith(AuctionBidService.MyBidsPrefix, StringComparison.Ordinal))
        {
            var auctionId = ParseTrailingId(customId, AuctionBidService.MyBidsPrefix);
            return await _auctions.MyAuctionBidsAsync(auctionId, appUserId, cancellationToken);
        }

        // Auction bid button → open the bid-amount modal. The bid itself is
        // placed on modal submit (handled in HandleModalSubmitAsync).
        if (customId.StartsWith(AuctionBidService.BidButtonPrefix, StringComparison.Ordinal))
        {
            var itemId = ParseTrailingId(customId, AuctionBidService.BidButtonPrefix);
            return await _auctions.BidModalAsync(itemId, appUserId, cancellationToken);
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
        if (customId.StartsWith(EndCampHandler.WdPopModalPrefix, StringComparison.Ordinal))
        {
            return await _endCamp.HandleEndCampModalAsync(customId, data, discordUserId, cancellationToken);
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
            return await _officerTools.HandleOfficerAddFindModalAsync(customId, data, discordUserId, cancellationToken);
        }

        var account = await _db.DiscordActivityUsers
            .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
            .Select(link => new DiscordLinkedAccount(
                link.IdentityUserId,
                link.IdentityUser!.CharacterName,
                link.IdentityUser.UserName))
            .FirstOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }

        if (customId.StartsWith(AuctionBidService.BidModalPrefix, StringComparison.Ordinal))
        {
            return await _auctions.HandleBidModalAsync(customId, data, account, cancellationToken);
        }

        return Ephemeral("That action isn't recognized.");
    }
}
