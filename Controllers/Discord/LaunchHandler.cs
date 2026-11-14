using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// Opening the LSManager app from Discord: the "/lsm" command that posts a launch card, and the
// card's "Join" button (also every board's green "Open LSManager App" button). Any other slash
// command is the Activity's own entry point, which DiscordInteractionDispatcher answers itself.
public sealed class LaunchHandler : DiscordInteractionHandlerBase, IDiscordInteractionHandler
{
    // The "/lsm" slash command (posts a launch card) and its "Join" button (launches the
    // Activity for the clicker via the LAUNCH_ACTIVITY callback). Every event board's green
    // "Open LSManager App" button sends this same id.
    private const string LaunchCommandName = "lsm";
    private const string LaunchButtonId = DiscordEventMessageBuilder.LaunchAppButtonId;

    private readonly ApplicationDbContext _db;
    private readonly DiscordPermissionGates _gates;

    public LaunchHandler(DiscordInteractionContext interaction, ApplicationDbContext db, DiscordPermissionGates gates)
        : base(interaction)
    {
        _db = db;
        _gates = gates;
    }

    public IReadOnlyList<DiscordInteractionRoute> Routes => new[]
    {
        // "/lsm" → post an officer-only launch card into the channel.
        Command(LaunchCommandName, nameof(HandleLaunchCardCommandAsync), async cancellationToken =>
            await HandleLaunchCardCommandAsync(Interaction.Root, cancellationToken)),

        // The "Join" button on a posted launch card → launch the Activity for the clicker
        // (per-user, quiet; Discord opens the embedded app). No identity/DB work needed.
        Component(LaunchButtonId, nameof(LaunchActivity), _ => Task.FromResult(LaunchActivity()))
            with { Exact = true, BeforeIdentity = true },
    };

    // The quiet LAUNCH_ACTIVITY callback: Discord opens the embedded app for the clicker only.
    private static IActionResult LaunchActivity() => Ok(new { type = ResponseLaunchActivity, data = new { } });

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
}
