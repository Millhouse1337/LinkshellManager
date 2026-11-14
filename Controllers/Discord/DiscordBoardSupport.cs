using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// Board steps several Discord flows share: loading an event with its party setup, refreshing the
// board a button sits on, quietly closing a finished picker, and re-running a sign-up that lost a
// double-click race.
public sealed class DiscordBoardSupport : DiscordInteractionHandlerBase
{
    private readonly ApplicationDbContext _db;
    private readonly DiscordEventChannelQueue _eventQueue;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _discordClientId;
    // Logged under the controller's name, as it was before this code moved out of it.
    private readonly ILogger<DiscordInteractionsController> _logger;

    public DiscordBoardSupport(
        DiscordInteractionContext interaction,
        ApplicationDbContext db,
        DiscordEventChannelQueue eventQueue,
        IHttpClientFactory httpClientFactory,
        IOptions<DiscordOAuthOptions> discordOptions,
        ILogger<DiscordInteractionsController> logger)
        : base(interaction)
    {
        _db = db;
        _eventQueue = eventQueue;
        _httpClientFactory = httpClientFactory;
        _discordClientId = discordOptions.Value.ClientId;
        _logger = logger;
    }

    // Sign Up, Check In and the "Sign Up (No Slot)" wizard each find the clicker's participation
    // and add one when there is none. A double click runs two of them at once; both find none and
    // the one-participation-per-event index turns the later insert away. Re-running that click
    // finds the row the first one saved and updates it, exactly as if it had arrived a moment
    // later. Nothing these handlers save before that insert minds being done twice (a join as an
    // outside member finds its own membership on the re-run).
    internal Task<IActionResult> RetryIfParticipationRaceAsync(Func<Task<IActionResult>> click) =>
        UniqueRaceRetry.RunAsync(_db, click, UniqueRaceRetry.Participations);

    // Loads an event with its party-setup tree (for the picker + board render).
    internal Task<Event?> LoadEventWithSetupAsync(int eventId, CancellationToken cancellationToken)
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
    internal IActionResult DismissPickerSilently()
    {
        var token = Interaction.InteractionToken;
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
    internal async Task<IActionResult> UpdatedEventMessageAsync(int eventId, CancellationToken cancellationToken)
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
            .Select(signup => new
            {
                signup.CharacterName, signup.JobName, signup.SubJobName, signup.JobType, signup.WdArrivalWindow,
                signup.EnfeebReady, signup.ResistReady, signup.RelicWeapon,
            })
            .ToListAsync(cancellationToken);
        var signups = rows
            .Select(row => new EventSignupLine(
                row.CharacterName ?? "Unknown", row.JobName, row.SubJobName, row.JobType, row.WdArrivalWindow,
                row.EnfeebReady, row.ResistReady, row.RelicWeapon))
            .ToList();

        var slotSignups = ev.PartySetup is null
            ? null
            : await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        // A PARTY board is never rebuilt from here. The publisher owns its lifecycle — re-posting
        // a board whose edit failed, deleting messages left over from the old full-width mode —
        // so queue the refresh and acknowledge silently; the re-rendered board is the feedback,
        // exactly as it is for every other board button.
        if (ev.PartySetup is not null)
        {
            _eventQueue.Enqueue(eventId);
            return Ok(new { type = ResponseDeferredUpdate });
        }

        // Ad-hoc events (no party setup) are a single embed this endpoint owns, so refreshing it
        // in the interaction response is both correct and instant.
        var data = DiscordEventMessageBuilder.Build(ev, signups, ev.PartySetup, slotSignups);
        return Ok(new { type = ResponseUpdateMessage, data });
    }
}
