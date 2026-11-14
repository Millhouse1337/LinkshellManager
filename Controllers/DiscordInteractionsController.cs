using System.Text;
using System.Text.Json;
using LinkshellManagerDiscordApp.Controllers.Discord;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using static LinkshellManagerDiscordApp.Controllers.Discord.DiscordInteractionHandlerBase;

namespace LinkshellManagerDiscordApp.Controllers;

// Discord HTTP interactions endpoint (button/select clicks on event
// announcements). Discord POSTs here with an Ed25519 signature we must verify
// (it actively probes with bad signatures → fail closed with 401). Handles the
// PING handshake and MESSAGE_COMPONENT interactions, responding with a type-7
// UPDATE_MESSAGE that refreshes the event's signup roster in place. Anonymous +
// reads the raw body itself (signature is over timestamp + raw body).
//
// This class only checks the signature, answers PING, and catches a handler that throws. Which
// code answers a click, modal or command is DiscordInteractionDispatcher's job; the code itself
// lives in the feature handlers beside it (Controllers/Discord).
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

    private readonly DiscordInteractionVerifier _verifier;
    private readonly DiscordInteractionDispatcher _dispatcher;
    private readonly ILogger<DiscordInteractionsController> _logger;

    // This request's interaction (who clicked what), shared with the dispatcher and handlers.
    private DiscordInteractionContext Interaction { get; }

    public DiscordInteractionsController(
        DiscordInteractionVerifier verifier,
        DiscordInteractionDispatcher dispatcher,
        ILogger<DiscordInteractionsController> logger,
        DiscordInteractionContext interaction)
    {
        _verifier = verifier;
        _dispatcher = dispatcher;
        _logger = logger;
        Interaction = interaction;
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
                    return await _dispatcher.HandleComponentAsync(root, cancellationToken);
                }

                if (type == InteractionModalSubmit)
                {
                    return await _dispatcher.HandleModalSubmitAsync(root, cancellationToken);
                }

                if (type == InteractionApplicationCommand)
                {
                    // "/lsm" posts the launch card; any other command launches the Activity.
                    return await _dispatcher.HandleCommandAsync(root, cancellationToken);
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
}
