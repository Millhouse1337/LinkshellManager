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

    // "🏁 End Camp / Enter ToD" modal (every windowed HNM board — Standard + Manual Check In).
    // custom_id carries the eventId; the fields capture the Time of Death (with seconds), NQ/HQ
    // (only for the three merge-pair families), and Claimed/Killed as their own Yes/No dropdowns.
    // The pop window is NOT asked — with timed auto-advance the current window IS where it popped.
    // The day number and the re-post lead aren't asked either: the app owns both (the event form's
    // Day field and the ToD form's re-post toggle), so the modal passes null and leaves whatever is
    // configured there alone. The wire ids keep the "wdpop" spelling so boards posted before this
    // stopped being Manual Check In-only keep working.
    private const string WdPopModalPrefix = "evt:wdpopmodal:";
    private const string WdPopTodFieldId = "wdpop_tod";
    private const string WdPopHqFieldId = "wdpop_hq";
    private const string WdPopClaimFieldId = "wdpop_claim";
    private const string WdPopKillFieldId = "wdpop_kill";
    // Retired — no longer rendered, but still read so a modal opened before the day/re-post removal
    // and the Outcome split is recorded correctly when it's submitted afterwards.
    private const string WdPopDayFieldId = "wdpop_day";
    private const string WdPopOutcomeFieldId = "wdpop_outcome";
    private const string WdPopRepostFieldId = "wdpop_repost";

    // The "/lsm" slash command (posts a launch card) and its "Join" button (launches the
    // Activity for the clicker via the LAUNCH_ACTIVITY callback).
    private const string LaunchCommandName = "lsm";
    private const string LaunchButtonId = "evt:launch";

    private readonly DiscordInteractionVerifier _verifier;
    private readonly ApplicationDbContext _db;
    private readonly DiscordEventChannelQueue _eventQueue;
    private readonly ILogger<DiscordInteractionsController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _discordClientId;
    private readonly SignupCharacterChoiceCache _charChoice;
    private readonly OfficerAddTargetCache _officerAddTargets;
    private readonly ManualMemberService _manualMembers;
    private readonly TimeZoneConversionService _timeZones;

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
        OfficerAddTargetCache officerAddTargets,
        ManualMemberService manualMembers,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        TimeZoneConversionService timeZones)
    {
        _verifier = verifier;
        _db = db;
        _eventQueue = eventQueue;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _discordClientId = discordOptions.Value.ClientId;
        _charChoice = charChoice;
        _officerAddTargets = officerAddTargets;
        _manualMembers = manualMembers;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
        _timeZones = timeZones;
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

    // "/lsm": post a public "Join" launch card into the channel — but only for a leader
    // or officer of a linkshell linked to THIS Discord server (the card's Join button is
    // then clickable by anyone). Non-officers get a private nudge instead.
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
        var isOfficer = !string.IsNullOrEmpty(appUserId) && await (
            from membership in _db.AppUserLinkshells
            join linkshell in _db.Linkshells on membership.LinkshellId equals linkshell.Id
            where membership.AppUserId == appUserId
                  && linkshell.DiscordGuildId == guildId
                  && (membership.Rank == LinkshellRanks.Leader || membership.Rank == LinkshellRanks.Officer)
            select membership.Id).AnyAsync(cancellationToken);
        if (!isOfficer)
        {
            return Ephemeral("Only a linkshell leader or officer can post the launch card here.");
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

        // Drill-down step 1 → 2: the member picked an alliance (value = alliance index);
        // morph the ephemeral to its party picker. Leader checked first (distinct string).
        if (customId.StartsWith(DiscordEventMessageBuilder.AlliancePickLeaderPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.AlliancePickLeaderPrefix);
            return await HandleAlliancePickedAsync(eventId, SelectedSlotId(data), asLeader: true, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.AlliancePickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.AlliancePickPrefix);
            return await HandleAlliancePickedAsync(eventId, SelectedSlotId(data), asLeader: false, cancellationToken);
        }

        // Drill-down step 2 → 3: the member picked a party (value = party id); morph the
        // ephemeral to its open-slot picker (which reuses the claim prefix).
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyPickLeaderPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartyPickLeaderPrefix);
            return await HandlePartyPickedAsync(eventId, SelectedSlotId(data), asLeader: true, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyPickPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartyPickPrefix);
            return await HandlePartyPickedAsync(eventId, SelectedSlotId(data), asLeader: false, cancellationToken);
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

        // "Fill earlier alliances first" nudge buttons: Take = claim the suggested
        // earlier slot; Keep = claim the slot they chose anyway. Both carry the
        // resolved picks in the custom_id and bypass the nudge re-check.
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyNudgeTakePrefix, StringComparison.Ordinal))
        {
            return await HandlePartyNudgeClaimAsync(customId, DiscordEventMessageBuilder.PartyNudgeTakePrefix, appUserId, cancellationToken);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.PartyNudgeKeepPrefix, StringComparison.Ordinal))
        {
            return await HandlePartyNudgeClaimAsync(customId, DiscordEventMessageBuilder.PartyNudgeKeepPrefix, appUserId, cancellationToken);
        }

        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotLeavePrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotLeavePrefix);
            return await HandlePartySlotLeaveAsync(eventId, appUserId, cancellationToken);
        }

        // "Make Me Party Lead" → a member already holding a slot takes their party's
        // crown from whoever currently holds it (overrides the existing leader).
        if (customId.StartsWith(DiscordEventMessageBuilder.MakeLeaderPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.MakeLeaderPrefix);
            return await HandleMakePartyLeaderAsync(eventId, appUserId, cancellationToken);
        }

        // "Make Me Alliance Lead" → a member already holding a slot takes their whole
        // alliance's crown from whoever currently holds it (overrides the existing lead).
        if (customId.StartsWith(DiscordEventMessageBuilder.MakeAllianceLeaderPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.MakeAllianceLeaderPrefix);
            return await HandleMakeAllianceLeaderAsync(eventId, appUserId, cancellationToken);
        }

        // "🔒 Stay Next Window" → the clicker toggles the lock on their OWN slot so it
        // survives the next automatic window turnover (window-cycle HNM boards).
        if (customId.StartsWith(DiscordEventMessageBuilder.LockNextWindowPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.LockNextWindowPrefix);
            return await HandleLockNextWindowAsync(eventId, appUserId, cancellationToken);
        }

        // "◀ View Previous Window" → anyone: read-only ephemeral of the previous window's roster
        // snapshot. Boards posted before this change send the same id from what was "Prev Window",
        // so an old button now opens the viewer instead of stepping the counter.
        if (customId.StartsWith(DiscordEventMessageBuilder.ViewPrevWindowPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.ViewPrevWindowPrefix);
            return await HandleViewPrevWindowAsync(eventId, cancellationToken);
        }

        // "✅ Check In (this window)" (Manual Check In boards) → member self-serve attendance for the current window.
        if (customId.StartsWith(DiscordEventMessageBuilder.XinPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.XinPrefix);
            return await HandleXinAsync(eventId, appUserId, cancellationToken);
        }

        // "🚪 Check Out" (Manual Check In boards) → member leaves mid-camp; credit stops at the current window.
        if (customId.StartsWith(DiscordEventMessageBuilder.CheckOutPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.CheckOutPrefix);
            return await HandleCheckOutAsync(eventId, appUserId, cancellationToken);
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
            return await HandlePartySlotClaimAsync(
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
            return await AdvancePartyJobWizardAsync(
                eventId, slotId, SelectedValue(data), null, null, false, appUserId, asLeader: false, cancellationToken, officerAdd: true);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddWizardMainPrefix, StringComparison.Ordinal))
        {
            var p = customId[DiscordEventMessageBuilder.OfficerAddWizardMainPrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            var role = p.Length > 2 ? NormalizeWizardValue(p[2]) : null;
            return await AdvancePartyJobWizardAsync(
                eventId, slotId, role, SelectedValue(data), null, false, appUserId, asLeader: false, cancellationToken, officerAdd: true);
        }
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddWizardSubPrefix, StringComparison.Ordinal))
        {
            var p = customId[DiscordEventMessageBuilder.OfficerAddWizardSubPrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            var role = p.Length > 2 ? NormalizeWizardValue(p[2]) : null;
            var main = p.Length > 3 ? NormalizeWizardValue(p[3]) : null;
            return await AdvancePartyJobWizardAsync(
                eventId, slotId, role, main, SelectedValue(data), true, appUserId, asLeader: false, cancellationToken, officerAdd: true);
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
            "slot" => HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: false, cancellationToken),
            "slotL" => HandlePartySlotSignUpAsync(eventId, appUserId, asLeader: true, cancellationToken),
            "join" => StartGeneralJoinAsync(eventId, appUserId, cancellationToken),
            "job" => HandleJobSignupAsync(eventId, appUserId, job, cancellationToken),
            "xin" => HandleXinAsync(eventId, appUserId, cancellationToken),
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
    // "◀ View Previous Window" on a window-cycle HNM board (Tiamat/Jormungand/Vrtra). READ-ONLY:
    // replies ephemerally with the roster snapshot taken when the previous window turned over, and
    // touches nothing. Open to everyone — it shows the same roster the board displayed publicly for
    // that window, so there is no gate to enforce.
    //
    // There is no "Next Window" counterpart any more: the counter belongs to the cadence
    // (HnmWindowAdvanceBackgroundService) alone.
    private async Task<IActionResult> HandleViewPrevWindowAsync(int eventId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That board is no longer available.");
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
        if (ev.HnmWindowNumber <= 1)
        {
            return Ephemeral("This camp is still on its first window — there's nothing before it yet.");
        }

        // The newest snapshot below the live window. Normally that's exactly HnmWindowNumber - 1,
        // but a camp that sat through a boundary while the app was down has a gap there (the
        // advancer deliberately skips the clear rather than wipe a roster over an unwatched
        // boundary), so take the most recent one that exists instead of assuming.
        var previousWindow = await _db.EventWindowRosterSnapshots
            .Where(s => s.EventId == eventId && s.WindowNumber < ev.HnmWindowNumber)
            .Select(s => (int?)s.WindowNumber)
            .OrderByDescending(n => n)
            .FirstOrDefaultAsync(cancellationToken);
        if (previousWindow is null)
        {
            return Ephemeral(
                $"No roster was captured for a window before {ev.HnmWindowNumber} — "
                + "either nobody was signed up, or this camp turned over while the bot was down.");
        }

        var rows = await _db.EventWindowRosterSnapshots
            .Where(s => s.EventId == eventId && s.WindowNumber == previousWindow)
            .OrderBy(s => s.AllianceSortOrder).ThenBy(s => s.PartySortOrder).ThenBy(s => s.SlotSortOrder)
            .ToListAsync(cancellationToken);

        return Ephemeral(RenderWindowSnapshot(ev, previousWindow.Value, rows));
    }

    // Renders a captured window roster as plain text for the ephemeral. Grouped alliance → party in
    // the board's own order, since that's the shape people remember seeing. Kept text-only on
    // purpose: the live board is a rendered image, but this is a throwaway "who was on last hour?"
    // look and must come back inside Discord's 3s interaction window.
    private static string RenderWindowSnapshot(
        Event ev, int windowNumber, IReadOnlyList<EventWindowRosterSnapshot> rows)
    {
        var effectiveMax = DiscordEventMessageBuilder.EffectiveWindowCount(ev);
        var lines = new List<string>
        {
            $"**{ev.EventName} — Window {windowNumber} of {effectiveMax}** (now on window {ev.HnmWindowNumber})",
        };
        if (rows.Count == 0)
        {
            lines.Add("_Nobody was signed up for that window._");
            return string.Join("\n", lines);
        }

        string? currentGroup = null;
        foreach (var row in rows)
        {
            var group = $"{row.AllianceName ?? "Alliance"} · {row.PartyName ?? "Party"}";
            if (group != currentGroup)
            {
                lines.Add(string.Empty);
                lines.Add($"__{group}__");
                currentGroup = group;
            }

            // "PLD/WAR", "PLD", or the role when no job was committed — matching how the board reads.
            var job = row.MainJob is { Length: > 0 }
                ? (row.SubJob is { Length: > 0 } ? $"{row.MainJob}/{row.SubJob}" : row.MainJob)
                : row.Role;
            var marks = string.Concat(
                row.IsAllianceLeader || row.IsPartyLeader ? " 👑" : string.Empty,
                row.WasLocked ? " 🔒" : string.Empty);
            lines.Add(
                $"• {row.CharacterName ?? "(unnamed)"}"
                + (job is { Length: > 0 } ? $" — {job}" : string.Empty)
                + (row.SlotLabel is { Length: > 0 } ? $" {row.SlotLabel}" : string.Empty)
                + marks);
        }

        var locked = rows.Count(r => r.WasLocked);
        lines.Add(string.Empty);
        lines.Add($"_{rows.Count} signed up_" + (locked > 0 ? $" · _{locked} carried over via 🔒_" : string.Empty));
        return string.Join("\n", lines);
    }

    // Reply for a click that arrives from a board copy predating the no-signup change
    // (DiscordEventMessageBuilder.IsAddonSnapshotCamp). Names where credit DOES come from so the
    // member knows what to do instead of assuming the button is broken.
    private const string SnapshotCampNoSignupNotice =
        "This camp doesn't use Discord sign-ups — attendance is recorded from the roster snapshots "
        + "the LSM addon posts in game each window. Just be in the zone when a window is posted.";

    // Picks the outside-signup gate for an event: HNM boards are gated by
    // HnmOutsideSignupEnabled, every other event by OutsidePartySignupEnabled. The two
    // toggles are independent per linkshell, so HNM can be open while general outside
    // signups are closed (and vice-versa).
    private async Task<bool> OutsideSignupAllowedAsync(Event ev, CancellationToken cancellationToken)
    {
        var isHnm = DiscordEventMessageBuilder.IsHnm(ev);
        var flags = await _db.Linkshells
            .Where(l => l.Id == ev.LinkshellId)
            .Select(l => new { l.OutsidePartySignupEnabled, l.HnmOutsideSignupEnabled })
            .FirstOrDefaultAsync(cancellationToken);
        return isHnm ? (flags?.HnmOutsideSignupEnabled ?? false) : (flags?.OutsidePartySignupEnabled ?? false);
    }

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

        // No linked account → outside path, gated by the linkshell setting (HNM boards
        // use HnmOutsideSignupEnabled; other events use OutsidePartySignupEnabled).
        var isHnm = string.Equals((ev.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        var enabled = await OutsideSignupAllowedAsync(ev, cancellationToken);
        if (!enabled || string.IsNullOrEmpty(_discordUserId))
        {
            // The Discord id is already guaranteed (the click handler rejects an unreadable
            // account earlier), so reaching here means the relevant outside-signup toggle is
            // OFF for this linkshell. Name the one they need, instead of telling the player
            // to "sign in" — which doesn't address the real cause.
            return SignupContext.Stop(Ephemeral(isHnm
                ? "This linkshell doesn't allow HNM outside sign-ups. Ask a leader to enable HNM Outside Sign Up in the linkshell settings."
                : "This linkshell doesn't allow account-less signups. Ask a leader to enable Outside Party Signup in the linkshell settings — or open LSM and sign in with Discord to sign up with a linked account."));
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
        catch (DbUpdateException)
        {
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
    private async Task<(string? AppUserId, string? DiscordUserId)?> ResolveWithdrawIdentityAsync(
        Event ev, string? appUserId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(appUserId))
        {
            return (appUserId, null);
        }
        // Outside withdraw uses the same per-event-type gate as signup, so an HNM-only
        // linkshell (Outside Party Signup off, HNM Outside Sign Up on) can still withdraw.
        var enabled = await OutsideSignupAllowedAsync(ev, cancellationToken);
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

        static object NameRow(string fieldId, string label, bool required, string value, string placeholder)
        {
            // Build the text input as a dictionary so we can OMIT "value" entirely when
            // there's no prefill. Discord can reject a modal whose text input ships an
            // empty default value ("") together with min_length >= 1 (the default would
            // be shorter than the minimum). An absent value is the normal "starts empty"
            // state and is unambiguously valid — keys mirror the Discord field names 1:1.
            var input = new Dictionary<string, object?>
            {
                ["type"] = 4, // text input
                ["custom_id"] = fieldId,
                ["label"] = label,
                ["style"] = 1, // short
                ["min_length"] = required ? 1 : 0,
                ["max_length"] = 64,
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
                    NameRow(OutsideNameFieldId, "Your MAIN FFXI character name", true, prefill, "e.g. Millhouse"),
                    NameRow(OutsideAlt1FieldId, "Alt 1 character name (optional)", false, string.Empty, "e.g. Millhouse2401"),
                    NameRow(OutsideAlt2FieldId, "Alt 2 character name (optional)", false, string.Empty, "e.g. Millhouse2402"),
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
        _interactionToken = root.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() : null;
        _discordUserId = discordUserId;
        _discordDisplayName = ResolveDiscordDisplayName(root);

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
            var officer = string.IsNullOrEmpty(officerAppUserId)
                ? null
                : await _db.AppUserLinkshells.FirstOrDefaultAsync(
                    m => m.AppUserId == officerAppUserId && m.LinkshellId == ev.LinkshellId, cancellationToken);
            if (!LinkshellRanks.IsLeaderOrOfficer(officer?.Rank))
            {
                return Ephemeral("Only officers can end the camp.");
            }

            // NQ/HQ only exists for the three merge-pair families, and it's asked even when we didn't
            // claim (which spawn it was still drives the next pop). Claimed and Killed are separate
            // Yes/No dropdowns now that day + re-post moved to the app and freed up the rows.
            var hasHq = HnmConfig.HasHqVariant(ev.AssignedMonsterName);
            bool? hq = hasHq ? ParseYesNo(ExtractModalValue(data, WdPopHqFieldId), defaultValue: false) : null;
            var claimRaw = ExtractModalValue(data, WdPopClaimFieldId);
            var killRaw = ExtractModalValue(data, WdPopKillFieldId);
            // Both null (not blank) = the modal was opened while the combined Outcome field was still
            // rendered — read that instead so an in-flight submission isn't silently mis-recorded.
            var (claimed, killed) = claimRaw is null && killRaw is null
                ? ParseCampOutcome(ExtractModalValue(data, WdPopOutcomeFieldId))
                : (ParseYesNo(claimRaw, defaultValue: true), ParseYesNo(killRaw, defaultValue: true));

            // Day number and re-post lead are no longer asked — these only find a value when a modal
            // opened before that removal is submitted afterwards. Absent (the normal path) leaves
            // both null: the board keeps its current day and its standing Repeat-on-ToD config.
            int? dayNumber = int.TryParse(ExtractModalValue(data, WdPopDayFieldId)?.Trim(), out var dn) && dn > 0
                ? dn : (int?)null;
            bool? repost = null;
            double? repostLead = null;
            var repostRaw = ExtractModalValue(data, WdPopRepostFieldId)?.Trim();
            if (!string.IsNullOrEmpty(repostRaw))
            {
                if (double.TryParse(repostRaw, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var lead) && lead > 0)
                {
                    repost = true;
                    repostLead = lead;
                }
                else if (repostRaw.Equals("no", StringComparison.OrdinalIgnoreCase)
                    || repostRaw.Equals("off", StringComparison.OrdinalIgnoreCase)
                    || repostRaw == "0")
                {
                    repost = false;
                }
            }

            // Time of Death: blank/"now" → now; "HH:mm[:ss]" or a full date-time → the officer's local
            // time, converted to UTC. Unparseable → error (don't silently guess a time).
            var officerTimeZone = string.IsNullOrEmpty(officerAppUserId)
                ? null
                : await _db.Users.Where(u => u.Id == officerAppUserId).Select(u => u.TimeZone).FirstOrDefaultAsync(cancellationToken);
            if (!TryParseCampTod(ExtractModalValue(data, WdPopTodFieldId), officerTimeZone, out var todUtc, out var todError))
            {
                return Ephemeral(todError!);
            }

            var isWd = DiscordEventMessageBuilder.IsWd(ev);
            var popService = HttpContext.RequestServices.GetRequiredService<HnmCampPopService>();
            var result = await popService.PopAsync(new HnmCampPopService.Request(
                EventId: popEventId,
                TodTimeUtc: todUtc,
                Cooldown: null,
                Interval: null,
                DayNumber: dayNumber,
                Claimed: claimed,
                Killed: killed,
                PopWindow: null, // timed auto-advance → the current window is where it popped
                Hq: hq ?? false,
                Repost: repost,
                RepostLeadHours: repostLead), cancellationToken);
            if (!result.Success)
            {
                return Ephemeral(result.Error ?? "Couldn't end the camp.");
            }

            // No ToD = no predicted repop, so nothing re-posts and there's no "next pop" to
            // count back from. Say so plainly rather than promising a re-post that won't come.
            // "Event System" (not the old "Attendance System"): the snapshot review rows now render
            // in that page's Current Field Activity section, beside the camps that produced them.
            var tail = todUtc is null
                ? (isWd
                    ? "No Time of Death recorded. The roster is waiting in **Event System** — review it there and hit **Post** to pay DKP."
                    : "No Time of Death recorded — the tracker shows **Not entered** and the board won't auto-re-post. Post the next one by hand.")
                : (isWd
                    ? "Board closed. The roster is waiting in **Event System** — review it there and hit **Post** to pay DKP."
                    : "Board closed — it'll re-post before the next predicted pop. The roster is waiting in **Event System**; review it there and hit **Post** to pay DKP.");
            var repostNote = repost == true && todUtc is not null
                ? $" · re-posting {repostLead:0.##}h before the next pop"
                : string.Empty;
            var outcomeNote = claimed
                ? (killed ? "claimed + killed" : "claimed, no kill")
                : "no claim";
            return Ephemeral(
                $"🏁 Camp ended — {outcomeNote}{(hq == true ? ", HQ" : string.Empty)}.{repostNote} " + tail);
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
            var outsideEnabled = await OutsideSignupAllowedAsync(ev, cancellationToken);
            if (!outsideEnabled)
            {
                return Ephemeral(isHnm
                    ? "HNM outside sign-ups aren't enabled for this event."
                    : "Outside signups aren't enabled for this event.");
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

        // Officer-only "add a new player" modal → find-or-create the named member (no Discord
        // link — the officer is seating someone else), cache them as the add target, and show
        // the slot picker. Re-checks the officer here since a modal submit is its own request.
        if (customId.StartsWith(DiscordEventMessageBuilder.OfficerAddNewModalPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerAddNewModalPrefix);
            if (eventId <= 0)
            {
                return Ephemeral("That event isn't recognized.");
            }
            var ev = await LoadEventWithSetupAsync(eventId, cancellationToken);
            if (ev is null || ev.PartySetup is null)
            {
                return Ephemeral("That event is no longer open.");
            }

            var officerAppUserId = await _db.DiscordActivityUsers
                .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
                .Select(link => link.IdentityUserId!)
                .FirstOrDefaultAsync(cancellationToken);
            if (!await IsEventOfficerAsync(ev, officerAppUserId, cancellationToken))
            {
                return Ephemeral("Only officers can add members to the board.");
            }

            var name = ExtractModalValue(data, DiscordEventMessageBuilder.OfficerAddNewNameFieldId)?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Ephemeral("Enter a character name to add.");
            }

            var addResult = await _manualMembers.FindOrCreateByNameAsync(ev.LinkshellId, name, cancellationToken);
            if (!addResult.Success || string.IsNullOrEmpty(addResult.AppUserId))
            {
                return Ephemeral(addResult.Error ?? "Couldn't add that player.");
            }
            // Use the member's canonical name (a pre-existing match may differ in case) and
            // carry any Discord link so a placeholder can still self-withdraw from the board.
            var membership = await _db.AppUserLinkshells
                .FirstOrDefaultAsync(m => m.LinkshellId == ev.LinkshellId && m.AppUserId == addResult.AppUserId, cancellationToken);
            var targetName = membership?.CharacterName?.Trim() is { Length: > 0 } cn ? cn : name!;
            _officerAddTargets.Set(discordUserId, eventId,
                new OfficerAddTargetCache.Target(addResult.AppUserId!, targetName, membership?.DiscordUserId));

            return await ShowOfficerAddSlotPickerAsync(ev, targetName, cancellationToken);
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

    // Pulls a submitted value out of a MODAL_SUBMIT payload by its custom_id. Handles both shapes a
    // modal can render: a text input inside an action row's `components` array, and a Label-wrapped
    // string select whose child sits under the singular `component` property. Returns null when the
    // modal never rendered the field at all — callers lean on that to spot an in-flight submission
    // from an older version of a form.
    private static string? ExtractModalValue(JsonElement data, string fieldId)
    {
        if (!data.TryGetProperty("components", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var row in rows.EnumerateArray())
        {
            // Action row → components[] (text inputs). Label → component (a single select).
            if (row.TryGetProperty("components", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
            {
                foreach (var input in inputs.EnumerateArray())
                {
                    if (TryReadModalField(input, fieldId, out var nested)) return nested;
                }
            }
            if (row.TryGetProperty("component", out var single) && TryReadModalField(single, fieldId, out var labeled))
            {
                return labeled;
            }
            // Belt and braces: some payload shapes hoist the component straight into the row.
            if (TryReadModalField(row, fieldId, out var direct)) return direct;
        }
        return null;
    }

    // One submitted component → its value, if its custom_id matches. Text inputs carry a scalar
    // `value`; string selects carry a `values` array, which is empty when nothing was picked — that
    // comes back as "" (present but unanswered), which every caller's parse treats as its default.
    private static bool TryReadModalField(JsonElement component, string fieldId, out string? value)
    {
        value = null;
        if (component.ValueKind != JsonValueKind.Object
            || !component.TryGetProperty("custom_id", out var cid)
            || cid.GetString() != fieldId)
        {
            return false;
        }
        if (component.TryGetProperty("value", out var scalar) && scalar.ValueKind == JsonValueKind.String)
        {
            value = scalar.GetString();
            return true;
        }
        if (component.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            value = values.GetArrayLength() > 0 ? values[0].GetString() : string.Empty;
            return true;
        }
        return false;
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
        // These camps no longer post a job select (DiscordEventMessageBuilder.Build), but a client
        // holding an older copy of the message can still fire one — and a signup recorded here
        // would show up on a board that has no way to display or withdraw it.
        if (DiscordEventMessageBuilder.IsAddonSnapshotCamp(ev))
        {
            return Ephemeral(SnapshotCampNoSignupNotice);
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

    // Manual Check In "X-in (this window)": records the clicker's arrival window on their AppUserEvent.
    // The finalizer credits windows arrival..last, so a single click covers every later window;
    // re-clicking a later window just overwrites the arrival (the Manual Check In "x2 -> x3" correction).
    // Identity resolution (member / placeholder / outside onboard / character picker) is fully
    // delegated to ResolveSignupContextAsync — the same path Sign Up uses.
    private async Task<IActionResult> HandleXinAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That board isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        if (!DiscordEventMessageBuilder.IsWd(ev))
        {
            return Ephemeral("This board doesn't use Manual Check In.");
        }
        if (ev.WdFinalizedAt is not null)
        {
            return Ephemeral("This camp has already been processed — attendance is locked.");
        }

        var ctx = await ResolveSignupContextAsync(ev, appUserId, $"xin:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }
        var characterName = ctx.CharacterName!;
        // Credit is recorded against the window the BOARD shows — the one being awaited — so the
        // number in this reply matches the heading directly above the button. See
        // DiscordEventMessageBuilder.FocusWindow. HnmCampPopService stamps WdPopWindow off the
        // same helper, so arrival and pop stay on one scale and total credit is unchanged.
        var arrivalWindow = DiscordEventMessageBuilder.FocusWindow(ev);
        var openedWindow = Math.Clamp(ev.HnmWindowNumber, 1, DiscordEventMessageBuilder.EffectiveWindowCount(ev));
        var checkedInEarly = arrivalWindow > openedWindow;

        // First x-in makes the camp live (mirrors the addon participation path), so the board
        // shows "Started" and the finalizer has a sensible StartTime.
        ev.CommencementStartTime ??= DateTime.UtcNow;

        var existing = ctx.AppUserId is not null
            ? await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.AppUserId == ctx.AppUserId, cancellationToken)
            : await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.AppUserId == null && item.DiscordUserId == ctx.DiscordUserId, cancellationToken);
        if (existing is not null)
        {
            existing.CharacterName = characterName;
            existing.WdArrivalWindow = arrivalWindow; // overwrite = the late-arrival correction
            existing.WdDepartureWindow = null;        // checking in clears any prior check-out
            existing.IsVerified = true;
            existing.IsQuickJoin = true;
            existing.StartTime ??= ev.CommencementStartTime;
        }
        else
        {
            _db.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = ctx.AppUserId,
                DiscordUserId = ctx.DiscordUserId,
                EventId = eventId,
                CharacterName = characterName,
                WdArrivalWindow = arrivalWindow,
                IsVerified = true,
                IsQuickJoin = true,
                EventDkp = 0,
                StartTime = ev.CommencementStartTime,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        _eventQueue.Enqueue(eventId); // refresh the board roster off the 3s window
        return Ephemeral(
            (checkedInEarly
                ? $"✅ You checked in **before Window {arrivalWindow} opens** — you'll get credit for Window {arrivalWindow} through the kill. "
                : $"✅ You're checked in for **Window {arrivalWindow}** — you'll get credit for Window {arrivalWindow} through the kill. ")
            + "Arrived late? Tap **Check In** again on a later window to correct it.");
    }

    // "🚪 Check Out" (Manual Check In boards): the member is leaving mid-camp. Records their
    // departure window so credit stops there (they keep DKP for arrival..this window, inclusive).
    // They can Check In again to come back.
    private async Task<IActionResult> HandleCheckOutAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
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
        if (!DiscordEventMessageBuilder.IsWd(ev) || ev.WdFinalizedAt is not null)
        {
            return Ephemeral("This board isn't accepting check-outs.");
        }

        // Find the clicker's participation by their linked account OR their Discord id (placeholder/
        // outside members are keyed by Discord id). They must already be checked in.
        var uid = appUserId;
        var did = _discordUserId;
        var existing = await _db.AppUserEvents.FirstOrDefaultAsync(p =>
            p.EventId == eventId
            && ((uid != null && p.AppUserId == uid) || (did != null && p.DiscordUserId == did)),
            cancellationToken);
        if (existing?.WdArrivalWindow is not { } arrival)
        {
            return Ephemeral("You're not checked in, so there's nothing to check out of.");
        }

        // Same scale as check-in and the pop window: the board's number. Clamped to >= arrival so
        // checking out can never record a departure before the member arrived.
        var departWindow = Math.Clamp(
            DiscordEventMessageBuilder.FocusWindow(ev), arrival, DiscordEventMessageBuilder.EffectiveWindowCount(ev));
        existing.WdDepartureWindow = departWindow;
        await _db.SaveChangesAsync(cancellationToken);

        _eventQueue.Enqueue(eventId);
        return Ephemeral(
            $"🚪 Checked out at **Window {departWindow}** — you'll get credit for Windows {arrival} through {departWindow}. " +
            "Tap **Check In** again if you come back.");
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
        var membership = string.IsNullOrEmpty(appUserId)
            ? null
            : await _db.AppUserLinkshells.FirstOrDefaultAsync(
                m => m.AppUserId == appUserId && m.LinkshellId == ev.LinkshellId, cancellationToken);
        if (!LinkshellRanks.IsLeaderOrOfficer(membership?.Rank))
        {
            return Ephemeral("Only officers can end the camp.");
        }
        if (ev.WdFinalizedAt is not null)
        {
            return Ephemeral("This camp has already been processed.");
        }

        return WdPopModal(ev);
    }

    // The End Camp / Enter ToD modal (max 5 components). Always: Time of Death (blank = now, accepts
    // seconds) plus Claimed? and Killed? as Yes/No dropdowns. The three NQ/HQ families get an NQ/HQ
    // dropdown too, for four rows at most. Day # and the re-post lead are deliberately absent — the
    // app owns both (the event form's Day field, the ToD form's re-post toggle), and dropping them
    // here is what freed the rows for a separate Claimed and Killed. The pop window isn't asked
    // either — with timed auto-advance the current window is where it popped.
    private IActionResult WdPopModal(Event ev)
    {
        static object TextRow(string fieldId, string label, string placeholder, bool required, int maxLength)
        {
            var input = new Dictionary<string, object?>
            {
                ["type"] = 4, // text input
                ["custom_id"] = fieldId,
                ["label"] = label,
                ["style"] = 1, // short
                ["min_length"] = required ? 1 : 0,
                ["max_length"] = maxLength,
                ["required"] = required,
                ["placeholder"] = placeholder,
            };
            return new { type = 1, components = new object[] { input } };
        }

        // A modal dropdown is a string select (type 3) wrapped in a Label (type 18) — a bare select
        // in an action row is rejected. Label's own `label` caps around 45 chars, so the longer hint
        // goes in `description`. The first option is pre-selected as the happy path so the officer
        // can submit without touching the field; the submit-side parse defaults to the same value.
        static object SelectRow(string fieldId, string label, string description,
            params (string Value, string Label)[] options)
        {
            var select = new Dictionary<string, object?>
            {
                ["type"] = 3, // string select
                ["custom_id"] = fieldId,
                ["required"] = false,
                ["min_values"] = 0,
                ["max_values"] = 1,
                ["options"] = options
                    .Select((option, index) => new Dictionary<string, object?>
                    {
                        ["label"] = option.Label,
                        ["value"] = option.Value,
                        ["default"] = index == 0,
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

        var fields = new List<object>
        {
            TextRow(WdPopTodFieldId, "Time of Death (blank = not entered)", "now, 9:05:15 PM, 21:05:15, or leave blank", false, 25),
        };
        if (HnmConfig.HasHqVariant(ev.AssignedMonsterName))
        {
            fields.Add(SelectRow(WdPopHqFieldId, "Was it HQ?",
                "Which spawn it was — this drives the next pop even if we didn't claim.",
                ("no", "NQ"), ("yes", "HQ")));
        }
        fields.Add(SelectRow(WdPopClaimFieldId, "Claimed?",
            "No = somebody else got the claim.", ("yes", "Yes"), ("no", "No")));
        fields.Add(SelectRow(WdPopKillFieldId, "Killed?",
            "No = we had the claim but it got away or wiped us.", ("yes", "Yes"), ("no", "No")));

        return Ok(new
        {
            type = ResponseModal,
            data = new
            {
                custom_id = $"{WdPopModalPrefix}{ev.Id}",
                title = "End Camp / Enter ToD",
                components = fields.ToArray(),
            }
        });
    }

    // Parses the Pop / End Camp modal's free-text Time of Death in the officer's local zone.
    // Accepts: blank (→ todUtc = null, meaning NOT ENTERED — the camp ended without anyone seeing
    // it die, so no time and no repop get recorded); "now" (→ this moment, the explicit shortcut);
    // a bare clock time in either 24-hour ("21:05:15") or 12-hour ("9:05:15 PM") form (today local,
    // rolled to yesterday if that's still in the future); or a full "yyyy-MM-dd" date with either
    // time form. Returns false with a user-facing message on unparseable input — a ToD is never
    // silently guessed.
    private bool TryParseCampTod(string? raw, string? timeZoneId, out DateTime? todUtc, out string? error)
    {
        todUtc = null;
        error = null;
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return true; // null → "Not entered"; the pop service leaves Time + RepopTime unset
        }
        if (s.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            todUtc = DateTime.UtcNow;
            return true;
        }

        // Fold any AM/PM spelling down to the " AM"/" PM" the "tt" specifier wants, so the officer
        // can type it however they like; a 24-hour entry passes through untouched.
        s = NormalizeMeridiem(s);

        // A bare clock time → today's date in the officer's zone at that wall-clock time.
        if (TimeOnly.TryParseExact(
                s,
                new[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm tt", "h:mm:ss tt" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var timeOnly))
        {
            var localNow = _timeZones.ToUserTime(DateTime.UtcNow, timeZoneId) ?? DateTime.UtcNow;
            var localDt = new DateTime(localNow.Year, localNow.Month, localNow.Day,
                timeOnly.Hour, timeOnly.Minute, timeOnly.Second, DateTimeKind.Unspecified);
            if (localDt > localNow)
            {
                localDt = localDt.AddDays(-1); // a ToD later than "now" today must mean yesterday
            }
            todUtc = _timeZones.ToUtc(localDt, timeZoneId);
            return todUtc.HasValue;
        }

        // Full date-time: a space or 'T' separator, with or without seconds, 24-hour or AM/PM.
        if (DateTime.TryParseExact(
                s,
                new[]
                {
                    "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss",
                    "yyyy-MM-dd h:mm tt", "yyyy-MM-dd h:mm:ss tt", "yyyy-MM-ddTh:mm tt", "yyyy-MM-ddTh:mm:ss tt",
                },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
        {
            todUtc = _timeZones.ToUtc(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), timeZoneId);
            return todUtc.HasValue;
        }

        error = "Enter a valid Time of Death — blank for now, `9:05:15 PM`, `21:05:15`, or `2026-07-23 9:05 PM`.";
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
        // Same stale-message case as the job select above: there is nothing to withdraw FROM on a
        // snapshot camp, so say why rather than silently reporting "you weren't signed up".
        if (DiscordEventMessageBuilder.IsAddonSnapshotCamp(ev))
        {
            return Ephemeral(SnapshotCampNoSignupNotice);
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

    // ─── Officer "Add Member" ───────────────────────────────────────────────────────────

    // True when `appUserId` is a Leader/Officer of the event's linkshell. Used to gate the
    // shared (visible-to-everyone) "Add Member" button + its follow-up steps on click.
    private async Task<bool> IsEventOfficerAsync(Event ev, string? appUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(appUserId))
        {
            return false;
        }
        var rank = await _db.AppUserLinkshells
            .Where(m => m.AppUserId == appUserId && m.LinkshellId == ev.LinkshellId)
            .Select(m => m.Rank)
            .FirstOrDefaultAsync(cancellationToken);
        return LinkshellRanks.IsLeaderOrOfficer(rank);
    }

    // "➕ Add Member (officers)" → officers only: an ephemeral select of roster members who
    // aren't already on this board, plus an "add a new player" option (opens a name modal).
    // The select is capped at Discord's 25-option limit; rosters past that are reachable via
    // the "add a new player" path, which find-or-creates by name (so an over-the-cap member
    // typed exactly is matched, not duplicated).
    private async Task<IActionResult> HandleOfficerAddStartAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
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
        if (!await IsEventOfficerAsync(ev, appUserId, cancellationToken))
        {
            return Ephemeral("Only officers can add members to the board.");
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

        var options = members
            .Where(m => !onBoard.Contains(m.AppUserId!))
            .OrderBy(m => m.CharacterName, StringComparer.OrdinalIgnoreCase)
            .Take(24) // 25-option cap, less one reserved for "add a new player"
            .Select(m => (object)new { label = m.CharacterName!.Trim(), value = m.AppUserId })
            .Append((object)new { label = "➕ Add a new player…", value = DiscordEventMessageBuilder.OfficerAddNewSentinel })
            .ToArray();

        return PickerResponse(
            EventHeading(ev.EventName, "Add a member to the board — pick who to seat:"),
            SelectRow(DiscordEventMessageBuilder.OfficerAddMemberPickPrefix, eventId.ToString(), "Pick a member", options));
    }

    // Officer-add member picker select → either open the "add a new player" name modal, or
    // (an existing member) cache them as the add target and show the slot picker.
    private async Task<IActionResult> HandleOfficerAddMemberPickedAsync(
        int eventId, string? appUserId, string? value, CancellationToken cancellationToken)
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
        if (!await IsEventOfficerAsync(ev, appUserId, cancellationToken))
        {
            return Ephemeral("Only officers can add members to the board.");
        }
        if (string.IsNullOrEmpty(_discordUserId))
        {
            return Ephemeral("Couldn't read your Discord account from that click.");
        }

        if (string.Equals(value, DiscordEventMessageBuilder.OfficerAddNewSentinel, StringComparison.Ordinal))
        {
            return OfficerAddNewModal(eventId);
        }

        var membership = string.IsNullOrEmpty(value)
            ? null
            : await _db.AppUserLinkshells
                .FirstOrDefaultAsync(m => m.LinkshellId == ev.LinkshellId && m.AppUserId == value, cancellationToken);
        if (membership?.AppUserId is null)
        {
            return Ephemeral("That member isn't in this linkshell anymore.");
        }

        var targetName = membership.CharacterName?.Trim() is { Length: > 0 } cn ? cn : "Member";
        _officerAddTargets.Set(_discordUserId, eventId,
            new OfficerAddTargetCache.Target(membership.AppUserId, targetName, membership.DiscordUserId));

        return await ShowOfficerAddSlotPickerAsync(ev, targetName, cancellationToken);
    }

    // The "add a new player" modal: the officer types a character name. On submit
    // (HandleModalSubmitAsync) it's find-or-created in the linkshell (no Discord link) and
    // becomes the add target. A single text field — Discord modals are text-only.
    private IActionResult OfficerAddNewModal(int eventId)
    {
        return Ok(new
        {
            type = ResponseModal,
            data = new
            {
                custom_id = $"{DiscordEventMessageBuilder.OfficerAddNewModalPrefix}{eventId}",
                title = "Add a new player",
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
                                custom_id = DiscordEventMessageBuilder.OfficerAddNewNameFieldId,
                                label = "Character name",
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

    // Loads the event (with party setup) and verifies the clicker is an officer.
    // Returns the event on success, or an ephemeral error result in `error`.
    private async Task<Event?> LoadOfficerEventAsync(
        int eventId, string? appUserId, string deniedMessage, CancellationToken cancellationToken,
        Func<string, IActionResult> ephemeral, Action<IActionResult> setError)
    {
        if (eventId <= 0) { setError(ephemeral("That event isn't recognized.")); return null; }
        var ev = await LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null) { setError(ephemeral("That event is no longer open.")); return null; }
        if (!await IsEventOfficerAsync(ev, appUserId, cancellationToken)) { setError(ephemeral(deniedMessage)); return null; }
        return ev;
    }

    // ── Move ──
    private async Task<IActionResult> HandleMoveStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can move members on the board.",
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
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can move members on the board.",
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
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can move members on the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (slotId <= 0 || !IsValidSourceToken(src)) { return Ephemeral("That selection isn't recognized."); }

        var (fromSlotId, mAppUser, mDiscord) = MapSource(src!);
        var result = await EventPartyBoardEditService.MoveMemberAsync(
            _db, eventId, fromSlotId, toSlotId: slotId, mAppUser, mDiscord, cancellationToken);
        if (!result.Success) { return WizardStep($"⚠️ {result.Error}", Array.Empty<object>()); }
        _eventQueue.Enqueue(eventId);
        return DismissPickerSilently();
    }

    private async Task<IActionResult> HandleMoveBenchAsync(string customId, string? appUserId, CancellationToken cancellationToken)
    {
        var (eventId, src) = ParseEventAndSource(customId, DiscordEventMessageBuilder.MoveBenchPrefix);
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can move members on the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (src is null || !src.StartsWith("s:", StringComparison.Ordinal)) { return Ephemeral("Only a seated member can be benched."); }

        var (fromSlotId, _, _) = MapSource(src);
        var result = await EventPartyBoardEditService.MoveMemberAsync(
            _db, eventId, fromSlotId, toSlotId: null, null, null, cancellationToken);
        if (!result.Success) { return WizardStep($"⚠️ {result.Error}", Array.Empty<object>()); }
        _eventQueue.Enqueue(eventId);
        return DismissPickerSilently();
    }

    // ── Set Leader ──
    private async Task<IActionResult> HandleSetLeaderStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can set the party leader.",
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
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can set the party leader.",
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
        return DismissPickerSilently();
    }

    // ── Lock Member (stay next window) ──
    // Officers pin a member's slot so it survives the window-turnover wipe. Same shape as
    // Set Leader: button → seated-member picker → the select toggles the chosen slot's lock.
    private async Task<IActionResult> HandleOfficerLockStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can lock members.",
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
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can lock members.",
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
        if (picker.Length == 0) { return DismissPickerSilently(); }
        var who = string.IsNullOrWhiteSpace(result.Name) ? "that member" : result.Name!.Trim();
        var verb = result.Locked ? $"🔒 Locked **{who}** for next window" : $"🔓 Unlocked **{who}**";
        return PickerResponse(EventHeading(ev.EventName, $"{verb} — pick another to toggle, or dismiss:"), picker);
    }

    // ── Remove Member ──
    private async Task<IActionResult> HandleWithdrawStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can remove members from the board.",
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
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can remove members from the board.",
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
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "Only officers can remove members from the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (!IsValidSourceToken(src)) { return Ephemeral("That selection isn't recognized."); }

        var identity = await ResolveSourceIdentityAsync(eventId, src!, cancellationToken);
        if (identity is null) { return WizardStep("⚠️ That member is no longer on the board.", Array.Empty<object>()); }
        var (mAppUser, mDiscord) = identity.Value;
        await EventPartySignupService.RemoveMemberCompletelyAsync(_db, eventId, mAppUser, mDiscord, cancellationToken);
        _eventQueue.Enqueue(eventId);
        return DismissPickerSilently();
    }

    // The signer a slot claim / job wizard should be attributed to. For a normal signup
    // that's the clicker (ResolveSignupContextAsync). For an officer-add it's the cached
    // TARGET member — the clicker (the officer) is re-verified, then their cached target is
    // returned (keyed by the officer's Discord id + event). The target always has an
    // AppUserId; its Discord id is carried only for placeholders (so they can still
    // self-withdraw), which PlaceholderMatch dual-stamps onto the claim.
    private async Task<SignupContext> ResolveSignerForClaimAsync(
        Event ev, string? clickerAppUserId, bool officerAdd, string promptTail, CancellationToken cancellationToken)
    {
        if (!officerAdd)
        {
            return await ResolveSignupContextAsync(ev, clickerAppUserId, promptTail, cancellationToken);
        }
        if (!await IsEventOfficerAsync(ev, clickerAppUserId, cancellationToken))
        {
            return SignupContext.Stop(Ephemeral("Only officers can add members to the board."));
        }
        if (string.IsNullOrEmpty(_discordUserId))
        {
            return SignupContext.Stop(Ephemeral("Couldn't read your Discord account from that click."));
        }
        var target = _officerAddTargets.Peek(_discordUserId, ev.Id);
        if (target is null)
        {
            return SignupContext.Stop(Ephemeral("That add-member session expired — tap **➕ Add Member** again."));
        }
        return string.IsNullOrEmpty(target.DiscordUserId)
            ? SignupContext.Account(target.AppUserId, target.CharacterName)
            : SignupContext.PlaceholderMatch(target.AppUserId, target.DiscordUserId, target.CharacterName);
    }

    // "Sign Up" / "Sign Up as Party Leader" on the board → the ephemeral drill-down:
    // Alliance → Party → Slot, each a select that morphs the previous in place. Single-
    // choice levels are skipped so you never pick from a list of one. The final slot pick
    // runs HandlePartySlotClaimAsync (job wizard + claim), unchanged. The leader path
    // restricts every level to leaderless parties.
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
        // before showing the picker — the claim itself re-resolves the same way.
        var ctx = await ResolveSignupContextAsync(ev, appUserId, $"{(asLeader ? "slotL" : "slot")}:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);

        // Start the drill-down at the alliance step (skipping it when only one alliance
        // has an opening — then the party step, likewise skipped when only one party has one).
        var alliances = ev.PartySetup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var openAllianceIndexes = Enumerable.Range(0, alliances.Count)
            .Where(i => AllianceHasOpening(alliances[i], slotSignups, asLeader))
            .ToList();
        if (openAllianceIndexes.Count == 0)
        {
            return Ephemeral(asLeader
                ? "There's no party to lead right now — every party already has a leader (or has no open slots)."
                : "Every slot is taken right now.");
        }
        if (openAllianceIndexes.Count == 1)
        {
            return ShowPartyStep(ev, slotSignups, openAllianceIndexes[0], asLeader);
        }

        var allianceRows = DiscordEventMessageBuilder.BuildAlliancePickerComponents(eventId, ev.PartySetup, slotSignups, asLeader);
        return PickerResponse(
            EventHeading(ev.EventName, asLeader ? "Pick an alliance to lead in:" : "Pick an alliance:"),
            allianceRows);
    }

    // Drill-down step 1 → 2: the member chose an alliance (value = its SortOrder index).
    private async Task<IActionResult> HandleAlliancePickedAsync(
        int eventId, int allianceIndex, bool asLeader, CancellationToken cancellationToken)
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
        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        if (allianceIndex < 0 || allianceIndex >= ev.PartySetup.Alliances.Count)
        {
            return Ephemeral("That alliance is no longer available.");
        }
        return ShowPartyStep(ev, slotSignups, allianceIndex, asLeader);
    }

    // Drill-down step 2 → 3: the member chose a party (value = party id).
    private async Task<IActionResult> HandlePartyPickedAsync(
        int eventId, int partyId, bool asLeader, CancellationToken cancellationToken)
    {
        if (eventId <= 0 || partyId <= 0)
        {
            return Ephemeral("That party isn't recognized.");
        }
        var ev = await LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        return ShowSlotStep(ev, slotSignups, partyId, asLeader);
    }

    // Show the party picker for one alliance, or skip straight to the slot step when that
    // alliance has just one party with an opening.
    private IActionResult ShowPartyStep(
        Event ev, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, int allianceIndex, bool asLeader)
    {
        var alliance = ev.PartySetup!.Alliances.OrderBy(a => a.SortOrder).ElementAt(allianceIndex);
        var openParties = alliance.Parties.OrderBy(p => p.SortOrder)
            .Where(p => PartyHasOpening(p, slotSignups, asLeader))
            .ToList();
        if (openParties.Count == 0)
        {
            // Reached only on a race (parties filled between clicks) → morph the picker to
            // the notice in place (PickerResponse falls back to a fresh ephemeral on a
            // board-click source, so it never edits the public board).
            return PickerResponse(
                asLeader ? "That alliance has no party to lead right now." : "That alliance is full right now.",
                Array.Empty<object>());
        }
        if (openParties.Count == 1)
        {
            return ShowSlotStep(ev, slotSignups, openParties[0].Id, asLeader);
        }
        var rows = DiscordEventMessageBuilder.BuildPartyPickerComponents(ev.Id, ev.PartySetup, slotSignups, allianceIndex, asLeader);
        return PickerResponse(
            EventHeading(ev.EventName, asLeader ? "Pick a party to lead:" : "Pick a party:"),
            rows);
    }

    // Show the open-slot picker for one party (the terminal drill-down step; picking a
    // slot routes to the existing claim + job wizard).
    private IActionResult ShowSlotStep(
        Event ev, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, int partyId, bool asLeader)
    {
        var rows = DiscordEventMessageBuilder.BuildPartySlotPickerComponents(ev.Id, ev.PartySetup!, slotSignups, partyId, asLeader);
        if (rows.Length == 0)
        {
            // Race: slots filled between clicks → morph in place (fresh ephemeral on a
            // board-click source).
            return PickerResponse("Every slot in that party was just taken. Tap Sign Up again to pick another.", Array.Empty<object>());
        }
        return PickerResponse(
            EventHeading(ev.EventName, asLeader ? "Pick a slot to claim as party leader 👑:" : "Pick a slot to claim:"),
            rows);
    }

    // An alliance/party still has an opening for this flow (leader: a leaderless party
    // with an open slot; regular: any open slot).
    private static bool AllianceHasOpening(
        PartySetupAlliance alliance, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, bool asLeader)
        => alliance.Parties.Any(p => PartyHasOpening(p, slotSignups, asLeader));

    private static bool PartyHasOpening(
        PartySetupParty party, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, bool asLeader)
    {
        if (asLeader && party.Slots.Any(s => slotSignups.TryGetValue(s.Id, out var su) && su.IsPartyLeader))
        {
            return false; // leader flow: skip parties that already have a leader
        }
        return party.Slots.Any(s => !slotSignups.ContainsKey(s.Id));
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
    // With ignoreOccupied=true the taken check is skipped, so it answers "does a slot
    // of this shape exist on the board AT ALL" — used to tell "full" (slot exists but
    // taken) apart from "not on the board" (no such slot) in the quick-signup message.
    private static PartySetupSlot? FindBestOpenSlotForCombo(
        PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> signups, JobCombo combo,
        bool ignoreOccupied = false)
    {
        PartySetupSlot? jobMatch = null, roleMatch = null, anyMatch = null;
        foreach (var party in setup.Alliances.SelectMany(a => a.Parties))
        {
            foreach (var slot in party.Slots.OrderBy(s => s.SortOrder))
            {
                if (!ignoreOccupied && signups.ContainsKey(slot.Id)) continue; // taken
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
    // claims its matching slot) plus a manual fallback. Unavailable recent combos are
    // only described in the text (never selectable), and worded accurately: a combo
    // whose matching slot exists but is taken is "Full right now", while a combo with
    // no slot of that shape on the board at all is "Not on the board" — the old code
    // called both "full", which read as wrong when the slot simply didn't exist.
    private IActionResult QuickComboPicker(
        int eventId, List<JobCombo> available, List<JobCombo> unavailable,
        PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> signups, string? eventName)
    {
        var options = available
            .Select(c => (object)new { label = $"{ComboLabel(c)} · open", value = $"c|{c.Main}|{c.Sub}|{c.Role}" })
            .Append((object)new { label = "Pick a slot manually →", value = "m" })
            .ToArray();

        var content = "⚡ Quick sign up — pick a recent job, or choose a slot manually:";

        // A matching slot exists (ignoring occupancy) ⇒ it's genuinely full; otherwise
        // there's no slot of that shape on the board.
        var full = unavailable
            .Where(c => FindBestOpenSlotForCombo(setup, signups, c, ignoreOccupied: true) is not null)
            .ToList();
        var notOnBoard = unavailable.Where(c => !full.Contains(c)).ToList();
        if (full.Count > 0)
        {
            content += $"\nFull right now: {string.Join(", ", full.Select(ComboLabel))}.";
        }
        if (notOnBoard.Count > 0)
        {
            content += $"\nNot on the board: {string.Join(", ", notOnBoard.Select(ComboLabel))}.";
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
        int eventId, int slotId, string? appUserId, bool asLeader, CancellationToken cancellationToken,
        bool officerAdd = false)
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
        // ephemeral dropdown wizard (role → main → sub). An "ANY" sub
        // (slot pins the main but leaves the sub open) counts too: the member must
        // still specify which sub they're bringing, so an empty SubJob enters the
        // wizard and the pinned role/main steps are simply skipped inside it.
        if (string.IsNullOrWhiteSpace(slot.Role)
            || string.IsNullOrWhiteSpace(slot.MainJob)
            || string.IsNullOrWhiteSpace(slot.SubJob))
        {
            return await AdvancePartyJobWizardAsync(eventId, slotId, null, null, null, false, appUserId, asLeader, cancellationToken, officerAdd);
        }

        var ctx = await ResolveSignerForClaimAsync(ev, appUserId, officerAdd, $"{(asLeader ? "slotL" : "slot")}:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        // Fully-pinned slot: nudge toward an open earlier-alliance slot first (if enabled).
        var pinnedNudge = await TryPartyFillNudgeAsync(ev, slot, null, null, null, asLeader, officerAdd, cancellationToken);
        if (pinnedNudge is not null) { return pinnedNudge; }

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
        if (officerAdd) { ClearOfficerAddTarget(eventId); }

        // The select lives on the ephemeral picker; queue the board refresh (the
        // image render runs off the 3s window) and silently dismiss the picker.
        _eventQueue.Enqueue(eventId);
        return DismissPickerSilently();
    }

    // "Fill earlier alliances first" nudge: when the linkshell wants it and an open
    // slot this member's job can fill is still free in an EARLIER alliance, returns an
    // ephemeral prompt (Take that slot / Sign up here anyway). Null = no nudge, proceed.
    // Bypassed for officer-add. role/main/sub are the member's resolved picks (or null
    // to resolve from the slot's pins).
    private async Task<IActionResult?> TryPartyFillNudgeAsync(
        Event ev, PartySetupSlot slot, string? role, string? main, string? sub,
        bool asLeader, bool officerAdd, CancellationToken cancellationToken)
    {
        if (officerAdd || ev.PartySetupId is null) { return null; }
        var fillInOrder = await _db.Linkshells
            .Where(l => l.Id == ev.LinkshellId)
            .Select(l => l.FillAlliancesInOrder)
            .FirstOrDefaultAsync(cancellationToken);
        if (!fillInOrder) { return null; }

        var jobs = PartySetupSignupService.ResolveSignupJobs(slot, role, main, sub);
        if (!jobs.Success) { return null; }

        var setup = await _db.PartySetups
            .Include(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
            .FirstOrDefaultAsync(ps => ps.Id == ev.PartySetupId.Value, cancellationToken);
        if (setup is null) { return null; }

        var signups = await EventPartySignupService.GetSignupsForEventAsync(_db, ev.Id, cancellationToken);
        var suggestion = PartyFillSuggestion.SuggestEarlierSlot(setup, signups, slot, jobs.Role, jobs.MainJob);
        if (suggestion is null || suggestion.Id == slot.Id) { return null; }

        var location = PartyFillSuggestion.DescribeSlot(setup, suggestion);
        var requirement = PartyFillSuggestion.RequirementLabel(suggestion);
        var spot = string.Equals(requirement, "open", StringComparison.OrdinalIgnoreCase) ? "an open spot" : $"an open {requirement} spot";
        var l = asLeader ? "1" : "0";
        var r = ToNudgeArg(jobs.Role);
        var m = ToNudgeArg(jobs.MainJob);
        var s = ToNudgeArg(jobs.SubJob);
        var takeId = $"{DiscordEventMessageBuilder.PartyNudgeTakePrefix}{ev.Id}:{suggestion.Id}:{l}:{r}:{m}:{s}";
        var keepId = $"{DiscordEventMessageBuilder.PartyNudgeKeepPrefix}{ev.Id}:{slot.Id}:{l}:{r}:{m}:{s}";
        var takeLabel = $"Take {location}";
        if (takeLabel.Length > 80) { takeLabel = takeLabel[..80]; }

        return PickerResponse(
            $"⚠️ There's still **{spot}** in **{location}**. Filling earlier alliances first keeps parties together — take that slot, or sign up where you chose.",
            new object[]
            {
                new
                {
                    type = 1,
                    components = new object[]
                    {
                        new { type = 2, style = 1, label = takeLabel, custom_id = takeId },
                        new { type = 2, style = 2, label = "Sign up here anyway", custom_id = keepId },
                    },
                },
            });
    }

    // Take/keep nudge buttons: claim the carried slot (suggested or original) with the
    // carried resolved picks; no further nudge. Tail: {eventId}:{slotId}:{L}:{role}:{main}:{sub}.
    private async Task<IActionResult> HandlePartyNudgeClaimAsync(string customId, string prefix, string? appUserId, CancellationToken cancellationToken)
    {
        var parts = customId[prefix.Length..].Split(':');
        if (parts.Length < 6 || !int.TryParse(parts[0], out var eventId) || !int.TryParse(parts[1], out var slotId))
        {
            return Ephemeral("That action isn't recognized.");
        }
        var asLeader = parts[2] == "1";
        var role = FromNudgeArg(parts[3]);
        var main = FromNudgeArg(parts[4]);
        var sub = FromNudgeArg(parts[5]);

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

        var ctx = await ResolveSignerForClaimAsync(ev, appUserId, false, $"{(asLeader ? "slotL" : "slot")}:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        var result = await EventPartySignupService.ClaimSlotAsync(
            _db, eventId, slot, ctx.AppUserId, ctx.CharacterName!, role, main, sub, cancellationToken, asLeader,
            discordUserId: ctx.DiscordUserId);
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't claim that slot.");
        }
        if (!await TryCommitSlotClaimAsync(cancellationToken))
        {
            return Ephemeral("That slot was just taken by another member. Pick another open slot.");
        }
        await EventPartySignupService.SyncParticipationAfterClaimAsync(_db, ev, ctx.AppUserId, cancellationToken, ctx.DiscordUserId);
        await _db.SaveChangesAsync(cancellationToken);
        await EventPartySignupService.ResolvePartyLeadershipAsync(_db, eventId, slot.PartySetupPartyId, cancellationToken);
        _eventQueue.Enqueue(eventId);
        return DismissPickerSilently();
    }

    private static string ToNudgeArg(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    private static string? FromNudgeArg(string value) => value == "-" || string.IsNullOrWhiteSpace(value) ? null : value;

    // Drives the job-pick wizard: presents the next needed dropdown (role → main →
    // sub) as an ephemeral message update, carrying the picks made so far in the
    // select custom_ids; once everything needed is gathered, claims the slot, edits
    // the board, and confirms. role/main/sub are the picks so far (null = not yet
    // picked, or pinned by the slot).
    private async Task<IActionResult> AdvancePartyJobWizardAsync(
        int eventId, int slotId, string? role, string? main, string? sub, bool subPicked,
        string? appUserId, bool asLeader, CancellationToken cancellationToken, bool officerAdd = false)
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
        // won't re-prompt). For an officer-add this is the cached TARGET member, not the
        // clicking officer. Used at the final claim below.
        var ctx = await ResolveSignerForClaimAsync(ev, appUserId, officerAdd, $"{(asLeader ? "slotL" : "slot")}:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        // Carry the flow's intent through the wizard via the matching prefix family (same
        // tail format): officer-add seats the cached target, the leader variant claims as
        // party leader, and the plain prefixes drive a normal self-signup. Officer-add never
        // also claims leadership (asLeader is forced false on that path).
        var rolePrefix = officerAdd ? DiscordEventMessageBuilder.OfficerAddWizardRolePrefix
            : asLeader ? DiscordEventMessageBuilder.PartyWizardLeaderRolePrefix : DiscordEventMessageBuilder.PartyWizardRolePrefix;
        var mainPrefix = officerAdd ? DiscordEventMessageBuilder.OfficerAddWizardMainPrefix
            : asLeader ? DiscordEventMessageBuilder.PartyWizardLeaderMainPrefix : DiscordEventMessageBuilder.PartyWizardMainPrefix;
        var subPrefix = officerAdd ? DiscordEventMessageBuilder.OfficerAddWizardSubPrefix
            : asLeader ? DiscordEventMessageBuilder.PartyWizardLeaderSubPrefix : DiscordEventMessageBuilder.PartyWizardSubPrefix;
        var leaderTag = asLeader ? " (as leader 👑)" : string.Empty;
        // Officer-add phrases the steps about the target ("Seat X" / "the main job") rather
        // than the clicker ("Sign up" / "your main job").
        var stepLead = officerAdd ? $"Seat {ctx.CharacterName}" : $"Sign up{leaderTag}";
        var possessive = officerAdd ? "the" : "your";

        // Present the next unpinned-and-not-yet-picked field as a dropdown.
        if (string.IsNullOrWhiteSpace(slot.Role) && string.IsNullOrWhiteSpace(role))
        {
            return WizardStep(
                EventHeading(ev.EventName, $"{stepLead} — {DiscordEventMessageBuilder.SlotRequirement(slot)}"),
                JobSelectRow(rolePrefix, $"{eventId}:{slotId}",
                    "Pick a role", EventJobCatalog.JobTypeOptions));
        }
        if (string.IsNullOrWhiteSpace(slot.MainJob) && string.IsNullOrWhiteSpace(main))
        {
            return WizardStep(
                EventHeading(ev.EventName, $"Pick {possessive} main job:"),
                JobSelectRow(mainPrefix, $"{eventId}:{slotId}:{role ?? "-"}",
                    $"Pick {possessive} main job", EventJobCatalog.MainJobOptions));
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
                EventHeading(ev.EventName, $"Pick {possessive} sub job:"),
                SelectRow(subPrefix, $"{eventId}:{slotId}:{role ?? "-"}:{main ?? "-"}",
                    $"Pick {possessive} sub job", subOptions));
        }

        // All picks gathered → nudge toward an open earlier-alliance slot first (if enabled).
        var wizardNudge = await TryPartyFillNudgeAsync(
            ev, slot, NormalizeWizardValue(role), NormalizeWizardValue(main), NormalizeWizardValue(sub),
            asLeader, officerAdd, cancellationToken);
        if (wizardNudge is not null) { return wizardNudge; }

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
        if (officerAdd) { ClearOfficerAddTarget(eventId); }
        _eventQueue.Enqueue(eventId); // async board refresh (image render off the 3s window)
        return DismissPickerSilently();
    }

    // Forget the officer's cached add target for this event (a successful seat completes the
    // flow; a stale target would otherwise mis-route a later officer-add wizard step).
    private void ClearOfficerAddTarget(int eventId)
    {
        if (!string.IsNullOrEmpty(_discordUserId)) { _officerAddTargets.Clear(_discordUserId, eventId); }
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

        // Self-withdraw is allowed even after the event is live: many players only ever
        // use the Discord board (never the app), so they must be able to free their slot
        // and re-sign mid-run rather than waiting on an officer. The live Event row is
        // deleted when the event ends, so this is naturally bounded — a post-end click
        // finds no event and is rejected above. (The web/Activity path stays officer-
        // gated; see EventController.Lifecycle's MemberCanWithdraw check.)
        var identity = await ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;
        var isLive = ev.CommencementStartTime is not null;

        var leftPartyId = await EventPartySignupService.LeaveAsync(_db, eventId, idAppUser, cancellationToken, idDiscord);

        // Before the event starts there's no DKP yet, so a withdrawal is a clean full
        // exit — also drop any "Join (no slot)" attendance they hold.
        //
        // A WINDOWED camp is a full exit too, live or not. Those credit attendance per posted
        // window rather than by time on the clock (EventBreakPolicy.IsWindowedAttendance — the same
        // test that refuses Break Room and mid-run withdraw on the app side), so there is no
        // accruing clock for the kept row to protect: keeping it only parked the member under
        // "Also Attending", which reads as still-signed-up to everyone looking at the board. On a
        // wyrm board it was doubly pointless, since the next window's roster clear deletes those
        // rows wholesale anyway (EventPartySignupService.ClearWindowRosterAsync).
        //
        // Everywhere else — a timed event paying by duration — the live case still KEEPS the
        // materialized participation: withdrawing from a slot must NOT wipe the event DKP they've
        // earned by attending. Only the party slot is freed, and they can re-sign into a new slot
        // with no DKP reset (the re-claim adopts the kept participation).
        var droppedAttendance = false;
        if (!isLive || EventBreakPolicy.IsWindowedAttendance(ev))
        {
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
                droppedAttendance = true;
            }
        }

        if (leftPartyId is null && !droppedAttendance)
        {
            // Leave lives on the shared board (the same button for everyone, which
            // Discord can't hide/grey per-user), so a click with nothing to free gets a
            // private notice instead of refreshing the board. On a duration-paid live event
            // that means they hold no slot but keep attendance/DKP — point them at an officer.
            // Windowed camps fall through to the plain "nothing to leave", because Withdraw
            // there removes everything and so leaves nothing behind to explain.
            return Ephemeral(isLive && !EventBreakPolicy.IsWindowedAttendance(ev)
                ? "You're attending this live event, so your DKP stays. Withdraw only frees a party slot — ask an officer if you need to be removed entirely."
                : "You're not signed up for this event, so there's nothing to leave.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        ClearCharacterChoice(idAppUser, idDiscord, eventId);
        // The board is a rendered image — queue the refresh (render runs off the 3s
        // window) and silently acknowledge the click (no confirmation message); the
        // refreshed board is the feedback.
        _eventQueue.Enqueue(eventId);
        return Ok(new { type = ResponseDeferredUpdate });
    }

    // "Make Me Party Lead" → the pressing member, already in a party slot, takes that
    // party's leadership (👑), replacing whoever currently holds it. Like Withdraw,
    // it's a shared board button (Discord can't gate per-user), so the handler resolves
    // the clicker, finds their slot, and refuses with a private notice when there's
    // nothing to do. Leadership is purely a board designation (no perms), so it's
    // allowed before AND during a live event.
    private async Task<IActionResult> HandleMakePartyLeaderAsync(
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

        // Identity resolves the same way as Withdraw: an account signup by AppUserId, a
        // board-only player by Discord id (no alt picker / name modal — they must
        // already hold a slot, so their identity is already on record).
        var identity = await ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;

        var result = await EventPartySignupService.MakePartyLeaderAsync(_db, eventId, idAppUser, idDiscord, cancellationToken);
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't make you the party leader.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        // Queue the board re-render (runs off the 3s window) and silently acknowledge —
        // the moved 👑 on the refreshed board is the feedback.
        _eventQueue.Enqueue(eventId);
        return Ok(new { type = ResponseDeferredUpdate });
    }

    // "Make Me Alliance Lead" → the pressing member, already in a party slot, takes their
    // whole ALLIANCE's lead (👑 by the alliance name), replacing whoever currently holds it.
    // Mirrors HandleMakePartyLeaderAsync one rung up: shared board button (Discord can't gate
    // per-user), so the handler resolves the clicker, finds their slot's alliance, and
    // refuses with a private notice when there's nothing to do. Purely a board designation
    // (no perms), so it's allowed before AND during a live event.
    private async Task<IActionResult> HandleMakeAllianceLeaderAsync(
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

        var identity = await ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;

        var result = await EventPartySignupService.MakeAllianceLeaderAsync(_db, eventId, idAppUser, idDiscord, cancellationToken);
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't make you the alliance lead.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        // Queue the board re-render (runs off the 3s window) and silently acknowledge —
        // the moved 👑 on the refreshed board is the feedback.
        _eventQueue.Enqueue(eventId);
        return Ok(new { type = ResponseDeferredUpdate });
    }

    // "🔒 Stay Next Window" → the clicker toggles the lock on their OWN slot so it survives
    // the automatic window-turnover wipe. Shared board button (Discord can't gate per-user), so
    // the handler resolves the clicker (like Withdraw / Make Me Party Lead), toggles their
    // slot, and confirms privately — the board's 🔒 marker + count is the shared feedback.
    private async Task<IActionResult> HandleLockNextWindowAsync(
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
        // The button only shows on Standard windowed HNM boards, but re-check on click (a stale
        // board could surface it, and there's nothing to "stay" for without a window advance).
        if (!DiscordEventMessageBuilder.UsesWindows(ev))
        {
            return Ephemeral("This board doesn't use windows, so there's nothing to stay for.");
        }

        var identity = await ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;

        var nowLocked = await EventPartySignupService.ToggleStayNextWindowAsync(
            _db, eventId, idAppUser, idDiscord, cancellationToken);
        if (nowLocked is null)
        {
            return Ephemeral("You need to hold a slot to stay next window — sign up first.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        _eventQueue.Enqueue(eventId); // refresh the board so the 🔒 marker + count update
        return Ephemeral(nowLocked.Value
            ? "🔒 You're locked in — you'll keep your slot when an officer advances the window."
            : "🔓 Lock removed — you'll be cleared on the next window like everyone else.");
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
            .Select(signup => new { signup.CharacterName, signup.JobName, signup.SubJobName, signup.JobType, signup.WdArrivalWindow })
            .ToListAsync(cancellationToken);
        var signups = rows
            .Select(row => new EventSignupLine(row.CharacterName ?? "Unknown", row.JobName, row.SubJobName, row.JobType, row.WdArrivalWindow))
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
