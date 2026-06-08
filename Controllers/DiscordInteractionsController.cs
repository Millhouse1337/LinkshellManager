using System.Text;
using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    private const int InteractionMessageComponent = 3;
    private const int InteractionModalSubmit = 5;

    // Discord interaction response types.
    private const int ResponsePong = 1;
    private const int ResponseChannelMessage = 4; // ephemeral replies
    private const int ResponseDeferredUpdate = 6;
    private const int ResponseUpdateMessage = 7;
    private const int ResponseModal = 9;
    private const int EphemeralFlag = 64;

    private readonly DiscordInteractionVerifier _verifier;
    private readonly ApplicationDbContext _db;
    private readonly DiscordBotClient _bot;
    private readonly ILogger<DiscordInteractionsController> _logger;

    public DiscordInteractionsController(
        DiscordInteractionVerifier verifier,
        ApplicationDbContext db,
        DiscordBotClient bot,
        ILogger<DiscordInteractionsController> logger)
    {
        _verifier = verifier;
        _db = db;
        _bot = bot;
        _logger = logger;
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

        var discordUserId = ResolveDiscordUserId(root);
        if (string.IsNullOrEmpty(discordUserId))
        {
            return Ephemeral("Couldn't read your Discord account from that click.");
        }

        // Resolve the LSM account linked to this Discord user.
        var appUserId = await _db.DiscordActivityUsers
            .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
            .Select(link => link.IdentityUserId!)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(appUserId))
        {
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
            return await HandleJobSignupAsync(eventId, appUserId, job, cancellationToken);
        }

        if (customId.StartsWith(DiscordEventMessageBuilder.WithdrawPrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.WithdrawPrefix);
            return await HandleWithdrawAsync(eventId, appUserId, cancellationToken);
        }

        // Party-setup board: "Sign Up" / "Sign up as leader" open the ephemeral
        // slot picker (the leader variant additionally claims party leadership).
        var isLeaderSignUp = customId.StartsWith(DiscordEventMessageBuilder.PartySlotLeaderSignUpPrefix, StringComparison.Ordinal);
        if (isLeaderSignUp || customId.StartsWith(DiscordEventMessageBuilder.PartySlotSignUpPrefix, StringComparison.Ordinal))
        {
            var prefix = isLeaderSignUp ? DiscordEventMessageBuilder.PartySlotLeaderSignUpPrefix : DiscordEventMessageBuilder.PartySlotSignUpPrefix;
            var eventId = ParseTrailingId(customId, prefix);
            return await HandlePartySlotSignUpAsync(eventId, appUserId, isLeaderSignUp, cancellationToken);
        }

        // Picker select → claim the chosen slot (slot id is the selected value).
        var isLeaderClaim = customId.StartsWith(DiscordEventMessageBuilder.PartySlotClaimLeaderPrefix, StringComparison.Ordinal);
        if (isLeaderClaim || customId.StartsWith(DiscordEventMessageBuilder.PartySlotClaimPrefix, StringComparison.Ordinal))
        {
            var prefix = isLeaderClaim ? DiscordEventMessageBuilder.PartySlotClaimLeaderPrefix : DiscordEventMessageBuilder.PartySlotClaimPrefix;
            var eventId = ParseTrailingId(customId, prefix);
            var slotId = data.TryGetProperty("values", out var slotValues)
                && slotValues.ValueKind == JsonValueKind.Array
                && slotValues.GetArrayLength() > 0
                && int.TryParse(slotValues[0].GetString(), out var parsedSlotId)
                ? parsedSlotId
                : 0;
            return await HandlePartySlotClaimAsync(eventId, slotId, appUserId, isLeaderClaim, cancellationToken);
        }

        if (customId.StartsWith(DiscordEventMessageBuilder.PartySlotLeavePrefix, StringComparison.Ordinal))
        {
            var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.PartySlotLeavePrefix);
            return await HandlePartySlotLeaveAsync(eventId, appUserId, cancellationToken);
        }

        // "Join (no slot)" → open an ephemeral job-pick wizard (role optional, job
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
        int eventId, string appUserId, string? job, CancellationToken cancellationToken)
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

        var membership = await _db.AppUserLinkshells
            .Include(link => link.AppUser)
            .FirstOrDefaultAsync(
                link => link.LinkshellId == ev.LinkshellId && link.AppUserId == appUserId, cancellationToken);
        if (membership is null)
        {
            return Ephemeral("You're not a member of this linkshell, so you can't sign up for its events.");
        }

        var characterName = membership.CharacterName
            ?? membership.AppUser?.CharacterName
            ?? membership.AppUser?.UserName
            ?? "Unknown";

        // Signing up again just replaces the prior job (mirrors the app's signup).
        var existing = await _db.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUserId, cancellationToken);
        if (existing is not null)
        {
            _db.AppUserEvents.Remove(existing);
        }

        _db.AppUserEvents.Add(new AppUserEvent
        {
            AppUserId = appUserId,
            EventId = eventId,
            CharacterName = characterName,
            JobName = job!.Trim(),
            EventDkp = 0,
            StartTime = ev.CommencementStartTime
        });
        await _db.SaveChangesAsync(cancellationToken);

        return await UpdatedEventMessageAsync(ev.Id, cancellationToken);
    }

    private async Task<IActionResult> HandleWithdrawAsync(
        int eventId, string appUserId, CancellationToken cancellationToken)
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

        var existing = await _db.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUserId, cancellationToken);
        if (existing is not null)
        {
            _db.AppUserEvents.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return await UpdatedEventMessageAsync(ev.Id, cancellationToken);
    }

    // "Sign Up" / "Sign up as leader" on the board → an ephemeral message with a
    // select of the OPEN slots (per-event). Picking one runs the claim (see
    // HandlePartySlotClaimAsync). When asLeader, the picker is scoped to parties
    // that don't already have a leader, and the claim marks them party leader.
    private async Task<IActionResult> HandlePartySlotSignUpAsync(
        int eventId, string appUserId, bool asLeader, CancellationToken cancellationToken)
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

        var isMember = await _db.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == ev.LinkshellId && link.AppUserId == appUserId, cancellationToken);
        if (!isMember)
        {
            return Ephemeral("You're not a member of this linkshell, so you can't sign up for its events.");
        }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildSlotPickerComponents(eventId, ev.PartySetup, slotSignups, asLeader);
        if (picker.Length == 0)
        {
            return Ephemeral(asLeader
                ? "Every party already has a leader (or is full)."
                : "Every slot is taken right now.");
        }

        return Ok(new
        {
            type = ResponseChannelMessage,
            data = new
            {
                content = asLeader ? "Pick a slot to claim as your party's leader:" : "Pick a slot to claim:",
                components = picker,
                flags = EphemeralFlag
            }
        });
    }

    // Picker select → claim the chosen slot for THIS event. If the slot pins both
    // a role and a main job, claim immediately; otherwise open a modal to collect
    // the missing job pick(s) (the claim then happens on modal submit).
    private async Task<IActionResult> HandlePartySlotClaimAsync(
        int eventId, int slotId, string appUserId, bool asLeader, CancellationToken cancellationToken)
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

        var membership = await _db.AppUserLinkshells
            .Include(link => link.AppUser)
            .FirstOrDefaultAsync(
                link => link.LinkshellId == ev.LinkshellId && link.AppUserId == appUserId, cancellationToken);
        if (membership is null)
        {
            return Ephemeral("You're not a member of this linkshell, so you can't sign up for its events.");
        }

        // Needs a job pick whenever a required field (role / main) isn't pinned →
        // start the ephemeral dropdown wizard (role → main → sub).
        if (string.IsNullOrWhiteSpace(slot.Role) || string.IsNullOrWhiteSpace(slot.MainJob))
        {
            return await AdvancePartyJobWizardAsync(eventId, slotId, null, null, null, false, appUserId, asLeader, cancellationToken);
        }

        var characterName = membership.CharacterName
            ?? membership.AppUser?.CharacterName ?? membership.AppUser?.UserName ?? "Member";
        var result = await EventPartySignupService.ClaimSlotAsync(
            _db, eventId, slot, appUserId, characterName, null, null, null, cancellationToken, asLeader);
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't claim that slot.");
        }
        await _db.SaveChangesAsync(cancellationToken);
        // Auto-promote earliest signup if the party just filled with no leader.
        await EventPartySignupService.ResolvePartyLeadershipAsync(_db, eventId, slot.PartySetupPartyId, cancellationToken);

        // The select lives on the ephemeral picker, so edit the board via the bot
        // and replace the picker with a confirmation.
        await EditBoardViaBotAsync(eventId, cancellationToken);
        var confirm = asLeader
            ? $"✅ Signed up as party leader 👑: {DiscordEventMessageBuilder.SlotRequirement(slot)}."
            : $"✅ Signed up: {DiscordEventMessageBuilder.SlotRequirement(slot)}.";
        return EphemeralReplace(confirm);
    }

    // Drives the job-pick wizard: presents the next needed dropdown (role → main →
    // sub) as an ephemeral message update, carrying the picks made so far in the
    // select custom_ids; once everything needed is gathered, claims the slot, edits
    // the board, and confirms. role/main/sub are the picks so far (null = not yet
    // picked, or pinned by the slot).
    private async Task<IActionResult> AdvancePartyJobWizardAsync(
        int eventId, int slotId, string? role, string? main, string? sub, bool subPicked,
        string appUserId, bool asLeader, CancellationToken cancellationToken)
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

        var membership = await _db.AppUserLinkshells
            .Include(link => link.AppUser)
            .FirstOrDefaultAsync(
                link => link.LinkshellId == ev.LinkshellId && link.AppUserId == appUserId, cancellationToken);
        if (membership is null)
        {
            return Ephemeral("You're not a member of this linkshell, so you can't sign up for its events.");
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
                $"Sign up{leaderTag} — {DiscordEventMessageBuilder.SlotRequirement(slot)}",
                JobSelectRow(rolePrefix, $"{eventId}:{slotId}",
                    "Pick a role", EventJobCatalog.JobTypeOptions));
        }
        if (string.IsNullOrWhiteSpace(slot.MainJob) && string.IsNullOrWhiteSpace(main))
        {
            return WizardStep(
                "Pick your main job:",
                JobSelectRow(mainPrefix, $"{eventId}:{slotId}:{role ?? "-"}",
                    "Pick your main job", EventJobCatalog.MainJobOptions));
        }
        if (string.IsNullOrWhiteSpace(slot.SubJob) && !subPicked)
        {
            // Sub options exclude the effective main (collected or pinned) so a
            // member can't pick e.g. PLD/PLD, plus an explicit "no sub" option.
            var effectiveMain = main ?? slot.MainJob;
            var subOptions = new[] { (object)new { label = "No sub job", value = DiscordEventMessageBuilder.PartyWizardNoSub } }
                .Concat(EventJobCatalog.SubJobOptions
                    .Where(j => !string.Equals(j, effectiveMain, StringComparison.OrdinalIgnoreCase))
                    .Select(j => (object)new { label = j, value = j }))
                .ToArray();
            return WizardStep(
                "Pick your sub job (optional):",
                SelectRow(subPrefix, $"{eventId}:{slotId}:{role ?? "-"}:{main ?? "-"}",
                    "Pick your sub job", subOptions));
        }

        // Everything needed is collected → claim, edit the board, confirm.
        var characterName = membership.CharacterName
            ?? membership.AppUser?.CharacterName ?? membership.AppUser?.UserName ?? "Member";
        var result = await EventPartySignupService.ClaimSlotAsync(
            _db, eventId, slot, appUserId, characterName,
            NormalizeWizardValue(role), NormalizeWizardValue(main), NormalizeWizardValue(sub), cancellationToken, asLeader);
        if (!result.Success)
        {
            return WizardStep($"⚠️ {result.Error}", Array.Empty<object>());
        }
        await _db.SaveChangesAsync(cancellationToken);
        await EventPartySignupService.ResolvePartyLeadershipAsync(_db, eventId, slot.PartySetupPartyId, cancellationToken);
        await EditBoardViaBotAsync(eventId, cancellationToken);
        var confirm = asLeader
            ? $"✅ Signed up as party leader 👑: {DiscordEventMessageBuilder.SlotRequirement(slot)}."
            : $"✅ Signed up: {DiscordEventMessageBuilder.SlotRequirement(slot)}.";
        return WizardStep(confirm, Array.Empty<object>());
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

    private static string? SelectedValue(JsonElement data) =>
        data.TryGetProperty("values", out var values)
        && values.ValueKind == JsonValueKind.Array
        && values.GetArrayLength() > 0
            ? values[0].GetString()
            : null;

    private static string? NormalizeWizardValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "-"
        || value == DiscordEventMessageBuilder.PartyWizardNoSub
        || value == DiscordEventMessageBuilder.PartyWizardNoRole
            ? null
            : value;

    // "Leave event" lives on the board itself, so refresh the board in place. Drops
    // BOTH the member's party slot (if any) AND their general attendance (if any).
    private async Task<IActionResult> HandlePartySlotLeaveAsync(
        int eventId, string appUserId, CancellationToken cancellationToken)
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

        var leftPartyId = await EventPartySignupService.LeaveAsync(_db, eventId, appUserId, cancellationToken);

        // Also drop their general-attendance row (the "Join (no slot)" roster).
        var attendance = await _db.AppUserEvents
            .Where(p => p.EventId == eventId && p.AppUserId == appUserId)
            .ToListAsync(cancellationToken);
        if (attendance.Count > 0)
        {
            _db.AppUserEvents.RemoveRange(attendance);
        }

        if (leftPartyId is not null || attendance.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return await UpdatedEventMessageAsync(eventId, cancellationToken);
    }

    // "Join (no slot)" button on the board → a NEW ephemeral wizard message (so the
    // board itself isn't replaced). Subsequent picks morph this ephemeral via
    // AdvanceGeneralJoinWizardAsync. Starts at the role step (optional).
    private async Task<IActionResult> StartGeneralJoinAsync(
        int eventId, string appUserId, CancellationToken cancellationToken)
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

        var isMember = await _db.AppUserLinkshells
            .AnyAsync(link => link.LinkshellId == ev.LinkshellId && link.AppUserId == appUserId, cancellationToken);
        if (!isMember)
        {
            return Ephemeral("You're not a member of this linkshell, so you can't join its events.");
        }

        var roleOptions = new[] { (object)new { label = "No specific role", value = DiscordEventMessageBuilder.PartyWizardNoRole } }
            .Concat(EventJobCatalog.JobTypeOptions.Select(r => (object)new { label = r, value = r }))
            .ToArray();
        return Ok(new
        {
            type = ResponseChannelMessage,
            data = new
            {
                content = "Join (no slot) — pick your role (optional):",
                components = SelectRow(DiscordEventMessageBuilder.PartyJoinWizardRolePrefix, $"{eventId}", "Pick a role", roleOptions),
                flags = EphemeralFlag
            }
        });
    }

    // "Join (no slot)" job-pick wizard: role (optional) → main job (required) →
    // sub (optional). Mirrors the slot wizard but there's no slot to claim — the
    // picks become a general-attendance AppUserEvent so the attendee always says
    // what job they're coming as (no more blank "Role Unassigned / Job/Sub" rows).
    // Re-running replaces the member's existing attendance for this event.
    private async Task<IActionResult> AdvanceGeneralJoinWizardAsync(
        int eventId, string? role, string? main, string? sub, bool subPicked,
        string appUserId, CancellationToken cancellationToken)
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

        var membership = await _db.AppUserLinkshells
            .Include(link => link.AppUser)
            .FirstOrDefaultAsync(
                link => link.LinkshellId == ev.LinkshellId && link.AppUserId == appUserId, cancellationToken);
        if (membership is null)
        {
            return Ephemeral("You're not a member of this linkshell, so you can't join its events.");
        }

        // Step 1 — role (optional; an explicit "No specific role" choice lets a
        // member proceed without one but still pick a job next).
        if (string.IsNullOrWhiteSpace(role))
        {
            var roleOptions = new[] { (object)new { label = "No specific role", value = DiscordEventMessageBuilder.PartyWizardNoRole } }
                .Concat(EventJobCatalog.JobTypeOptions.Select(r => (object)new { label = r, value = r }))
                .ToArray();
            return WizardStep(
                "Join (no slot) — pick your role (optional):",
                SelectRow(DiscordEventMessageBuilder.PartyJoinWizardRolePrefix, $"{eventId}", "Pick a role", roleOptions));
        }

        // Step 2 — main job (required).
        if (string.IsNullOrWhiteSpace(main))
        {
            return WizardStep(
                "Pick your main job:",
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
                "Pick your sub job (optional):",
                SelectRow(DiscordEventMessageBuilder.PartyJoinWizardSubPrefix, $"{eventId}:{role}:{main}",
                    "Pick your sub job", subOptions));
        }

        // Done — (re)create the general-attendance row carrying the chosen job.
        var characterName = membership.CharacterName
            ?? membership.AppUser?.CharacterName ?? membership.AppUser?.UserName ?? "Member";

        var existing = await _db.AppUserEvents
            .Where(p => p.EventId == eventId && p.AppUserId == appUserId)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            _db.AppUserEvents.RemoveRange(existing);
        }

        _db.AppUserEvents.Add(new AppUserEvent
        {
            AppUserId = appUserId,
            EventId = eventId,
            CharacterName = characterName,
            JobType = NormalizeWizardValue(role),
            JobName = NormalizeWizardValue(main),
            SubJobName = NormalizeWizardValue(sub),
            EventDkp = 0,
            StartTime = ev.CommencementStartTime,
        });
        await _db.SaveChangesAsync(cancellationToken);
        await EditBoardViaBotAsync(eventId, cancellationToken);

        var jobLabel = NormalizeWizardValue(main) ?? "your job";
        var subLabel = NormalizeWizardValue(sub) is { } chosenSub ? $"/{chosenSub}" : string.Empty;
        return WizardStep($"✅ Joined (no slot) as {jobLabel}{subLabel}.", Array.Empty<object>());
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

    // Edits the posted board message via the bot — used when the triggering
    // interaction was on the ephemeral picker (so UPDATE_MESSAGE would hit the
    // ephemeral, not the board). No-op if the board was never posted.
    private async Task EditBoardViaBotAsync(int eventId, CancellationToken cancellationToken)
    {
        var ev = await LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null
            || string.IsNullOrEmpty(ev.DiscordChannelId) || string.IsNullOrEmpty(ev.DiscordMessageId))
        {
            return;
        }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var payload = DiscordEventMessageBuilder.Build(
            ev, Array.Empty<EventSignupLine>(), ev.PartySetup, slotSignups);
        await _bot.EditMessageAsync(ev.DiscordChannelId, ev.DiscordMessageId, payload, cancellationToken);
    }

    // Replaces the ephemeral picker (the message the select was on) with a
    // confirmation, clearing its components.
    private IActionResult EphemeralReplace(string message) =>
        Ok(new { type = ResponseUpdateMessage, data = new { content = message, components = Array.Empty<object>() } });

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
            .Select(signup => new { signup.CharacterName, signup.JobName })
            .ToListAsync(cancellationToken);
        var signups = rows
            .Select(row => new EventSignupLine(row.CharacterName ?? "Unknown", row.JobName))
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
        return int.TryParse(tail, out var id) ? id : 0;
    }

    private IActionResult Ephemeral(string message) =>
        Ok(new { type = ResponseChannelMessage, data = new { content = message, flags = EphemeralFlag } });
}
