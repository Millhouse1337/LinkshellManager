using System.Text.Json;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// What one Discord interaction request knows about itself: who clicked, the id they clicked, the
// interaction token, and the HTTP request it arrived on. One per request (scoped in DI); the
// endpoint fills it in before any handler runs, and every handler and shared service of the
// Discord bot reads it from here.
//
// These were fields on DiscordInteractionsController while it held all the bot's code. A
// controller is built per request, so the fields were per request too; a scoped object keeps
// exactly that lifetime now that the code lives in several classes.
public sealed class DiscordInteractionContext
{
    // The request the interaction arrived on. Needed to delete a finished picker after the response
    // is sent, to find request-scoped services, and to build the launch card's image URL.
    public HttpContext HttpContext { get; set; } = null!;

    // The whole interaction payload, and its "data" object (custom_id, values, modal fields).
    public JsonElement Root { get; set; }
    public JsonElement Data { get; set; }

    // The custom_id clicked or submitted. Empty for a slash command.
    public string CustomId { get; set; } = string.Empty;

    // This interaction's token, captured so a success handler can dismiss (delete) the ephemeral
    // picker/wizard it lives on.
    public string? InteractionToken { get; set; }

    // The clicker's Discord user id + display name, so the outside-signup path (no linked account)
    // can identify and name them.
    public string? DiscordUserId { get; set; }
    public string? DiscordDisplayName { get; set; }

    // True when this component click is ON an ephemeral picker (a previous step in the same signup
    // flow). The next picker step then MORPHS that message in place instead of stacking a new
    // "Which character…" ephemeral.
    public bool IsEphemeralSource { get; set; }

    // The LSM account linked to the clicker's Discord id, resolved for a button/select click. MAY be
    // null: a member who has never launched the app has no link.
    public string? AppUserId { get; set; }
}
