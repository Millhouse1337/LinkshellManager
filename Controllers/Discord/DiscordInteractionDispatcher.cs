using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// Sends each Discord interaction to the handler that owns its id.
//
// Every registered IDiscordInteractionHandler lists the ids it answers; this class puts them in
// one table and, per interaction, reads who clicked what (into DiscordInteractionContext), then
// runs the FIRST route whose id matches. No id in the table may match another's (the constructor
// refuses one that does), so "first" never has to break a tie - which route answers depends only
// on the id, exactly as it did in the old if/else chain.
//
// The reading steps are the ones DiscordInteractionsController ran before each feature's code:
// unchanged, and in the same order, because what a click with no Discord id or no linked account
// is told depends on that order.
public sealed class DiscordInteractionDispatcher : DiscordInteractionHandlerBase
{
    private readonly ApplicationDbContext _db;

    public DiscordInteractionDispatcher(
        DiscordInteractionContext interaction,
        ApplicationDbContext db,
        IEnumerable<IDiscordInteractionHandler> handlers)
        : base(interaction)
    {
        _db = db;
        Routes = handlers.SelectMany(handler => handler.Routes).ToList();
        if (FindOverlap(Routes) is { } overlap)
        {
            throw new InvalidOperationException(
                $"Discord interaction ids overlap: '{overlap.First.Id}' ({overlap.First.Handler}) and "
                + $"'{overlap.Second.Id}' ({overlap.Second.Handler}). Each id must reach exactly one handler.");
        }
    }

    // Every id the bot answers, in table order.
    public IReadOnlyList<DiscordInteractionRoute> Routes { get; }

    // A button or select click.
    public async Task<IActionResult> HandleComponentAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("custom_id", out var customIdEl)
            || customIdEl.GetString() is not { Length: > 0 } customId)
        {
            return Ephemeral("That action isn't recognized.");
        }
        Interaction.Root = root;
        Interaction.Data = data;
        Interaction.CustomId = customId;

        // The launch card's "Join" button needs no identity or database work.
        if (Find(DiscordInteractionKind.Component, customId, beforeIdentity: true) is { } early)
        {
            return await early.HandleAsync(cancellationToken);
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
        Interaction.AppUserId = await _db.DiscordActivityUsers
            .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
            .Select(link => link.IdentityUserId!)
            .FirstOrDefaultAsync(cancellationToken);

        var route = Find(DiscordInteractionKind.Component, customId, beforeIdentity: false);
        return route is null
            ? Ephemeral("That action isn't recognized.")
            : await route.HandleAsync(cancellationToken);
    }

    // A modal submit.
    public async Task<IActionResult> HandleModalSubmitAsync(JsonElement root, CancellationToken cancellationToken)
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
        Interaction.Root = root;
        Interaction.Data = data;
        Interaction.CustomId = customId;

        // The End Camp, outside sign-up and find-a-member modals run before any account lookup:
        // each resolves (or does without) the submitter's account itself.
        var route = Find(DiscordInteractionKind.Modal, customId, beforeIdentity: false);
        if (route is { NeedsLinkedAccount: false })
        {
            return await route.HandleAsync(cancellationToken);
        }

        // Everything else needs a linked account, and an unknown modal is asked to sign in too
        // before it is told it isn't recognized.
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
        Interaction.LinkedAccount = account;

        return route is null
            ? Ephemeral("That action isn't recognized.")
            : await route.HandleAsync(cancellationToken);
    }

    // A slash command.
    public async Task<IActionResult> HandleCommandAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var commandName = root.TryGetProperty("data", out var cmdData)
            && cmdData.TryGetProperty("name", out var cmdName) ? cmdName.GetString() : null;
        Interaction.Root = root;

        if (commandName is not null && Find(DiscordInteractionKind.Command, commandName, beforeIdentity: false) is { } route)
        {
            return await route.HandleAsync(cancellationToken);
        }

        // The Activity's entry-point "Launch" command (now app-handled, so the
        // public "Game Invitation" card is suppressed): just launch the Activity
        // for the clicker with a quiet LAUNCH_ACTIVITY callback — no channel post.
        return Ok(new { type = ResponseLaunchActivity, data = new { } });
    }

    private DiscordInteractionRoute? Find(DiscordInteractionKind kind, string id, bool beforeIdentity) =>
        Routes.FirstOrDefault(route => route.Kind == kind && route.BeforeIdentity == beforeIdentity && route.Matches(id));

    // Two routes of one kind where an id could reach either: the same id twice, or one prefix that
    // starts with another ("evt:olock:" and "evt:olock:pick:" would be; "evt:olockpick:" is not).
    internal static (DiscordInteractionRoute First, DiscordInteractionRoute Second)? FindOverlap(
        IReadOnlyList<DiscordInteractionRoute> routes)
    {
        for (var i = 0; i < routes.Count; i++)
        {
            for (var j = i + 1; j < routes.Count; j++)
            {
                var (a, b) = (routes[i], routes[j]);
                if (a.Kind == b.Kind && (a.Matches(b.Id) || b.Matches(a.Id)))
                {
                    return (a, b);
                }
            }
        }
        return null;
    }
}
