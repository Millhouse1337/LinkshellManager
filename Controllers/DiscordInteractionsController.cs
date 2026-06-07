using System.Text;
using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
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
    private readonly ILogger<DiscordInteractionsController> _logger;

    public DiscordInteractionsController(
        DiscordInteractionVerifier verifier,
        ApplicationDbContext db,
        ILogger<DiscordInteractionsController> logger)
    {
        _verifier = verifier;
        _db = db;
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
            || customIdEl.GetString() is not { Length: > 0 } customId
            || !customId.StartsWith(AuctionBidService.BidModalPrefix, StringComparison.Ordinal))
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

        return await UpdatedEventMessageAsync(ev, cancellationToken);
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

        return await UpdatedEventMessageAsync(ev, cancellationToken);
    }

    // Builds the type-7 UPDATE_MESSAGE response that refreshes the event's signup
    // roster in the same Discord message the user clicked.
    private async Task<IActionResult> UpdatedEventMessageAsync(Event ev, CancellationToken cancellationToken)
    {
        var rows = await _db.AppUserEvents
            .AsNoTracking()
            .Where(signup => signup.EventId == ev.Id)
            .OrderBy(signup => signup.CharacterName)
            .Select(signup => new { signup.CharacterName, signup.JobName })
            .ToListAsync(cancellationToken);
        var signups = rows
            .Select(row => new EventSignupLine(row.CharacterName ?? "Unknown", row.JobName))
            .ToList();

        var data = DiscordEventMessageBuilder.Build(ev, signups);
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
