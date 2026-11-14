using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using LinkshellManagerDiscordApp.Services;
using Xunit;
using H = LinkshellManager.Tests.DiscordInteractionHarness;

namespace LinkshellManager.Tests;

// Pins what the Discord bot answers to EVERY button, select, modal and command it handles, for
// three clickers (no LSM account, a member, an officer), against one small seeded linkshell.
//
// Why: the handler code behind POST /api/discord/interactions is being split out of one long
// controller. Boards already posted in Discord carry their button ids forever ("evt:pssignup:42"
// and so on), so after the split each id must reach the same code and give the same answer:
// the same response body, the same "delete the picker" step, the same board refresh, the same
// rows written. The ids are written out literally in the snapshot file, so changing a prefix
// constant's text shows up here too.
//
// When a change in what the bot answers is MEANT, regenerate the file:
//   PowerShell:  $env:UPDATE_DISCORD_SNAPSHOT=1; dotnet test tests/LinkshellManager.Tests --filter DiscordInteractionRoutingSnapshotTests; Remove-Item Env:UPDATE_DISCORD_SNAPSHOT
//   bash:        UPDATE_DISCORD_SNAPSHOT=1 dotnet test tests/LinkshellManager.Tests --filter DiscordInteractionRoutingSnapshotTests
// and review the diff of Snapshots/discord-interactions.txt in the commit.
public class DiscordInteractionRoutingSnapshotTests
{
    private const string SnapshotFile = "discord-interactions.txt";
    private const string UpdateVariable = "UPDATE_DISCORD_SNAPSHOT";

    // One sample interaction. `Build` makes its payload for a given clicker.
    internal sealed record Sample(string Key, Func<string, JsonObject> Build);

    private static Sample Click(string customId, string[]? values = null, bool fromEphemeral = false) =>
        new($"component {customId}"
            + (values is null ? string.Empty : $" values=[{string.Join(",", values)}]")
            + (fromEphemeral ? " (ephemeral)" : string.Empty),
            clicker => H.Component(clicker, customId, values, fromEphemeral));

    private static Sample Submit(string customId, params (string Field, string Value)[] fields) =>
        new($"modal {customId} {string.Join(" ", fields.Select(f => $"{f.Field}={f.Value}"))}".TrimEnd(),
            clicker => H.Modal(clicker, customId, fields));

    private static Sample Slash(string name, bool inGuild = true) =>
        new($"command /{name}" + (inGuild ? string.Empty : " (no guild)"), clicker => H.Command(clicker, name, inGuild));

    // Every id the bot handles, one or more samples each. Written as literal text on purpose.
    internal static readonly Sample[] Samples =
    {
        // Pings, unknown types, commands.
        new("ping", _ => H.Bare(1)),
        new("interaction type 4 (autocomplete, unhandled)", _ => H.Bare(4)),
        Slash("lsm"),
        Slash("lsm", inGuild: false),
        Slash("launch"),

        // Buttons and selects.
        Click("evt:launch"),
        Click("evt:charpick:slot:10", new[] { "Mill" }, fromEphemeral: true),
        Click("evt:charpick:join:10", new[] { "Mill" }, fromEphemeral: true),
        Click("evt:charpick:job:20:WAR", new[] { "Mill" }, fromEphemeral: true),
        Click("evt:charpick:xin:30", new[] { "Mill" }, fromEphemeral: true),
        Click("evt:charpick:bogus:10", new[] { "Mill" }, fromEphemeral: true),
        Click("evt:job:20", new[] { "WAR" }),
        Click("evt:job:10", new[] { "WAR" }),
        Click("evt:withdraw:20"),
        Click("evt:withdraw:10"),
        Click("evt:psLsignup:10"),
        Click("evt:pssignup:10"),
        Click("evt:psLA:10", new[] { "0" }, fromEphemeral: true),
        Click("evt:psA:10", new[] { "0" }, fromEphemeral: true),
        Click("evt:psLP:10", new[] { "1" }, fromEphemeral: true),
        Click("evt:psP:10", new[] { "1" }, fromEphemeral: true),
        Click("evt:psLclaim:10", new[] { "1" }, fromEphemeral: true),
        Click("evt:psclaim:10", new[] { "2" }, fromEphemeral: true),
        Click("evt:psclaim:10:0", new[] { "1" }, fromEphemeral: true),
        Click("evt:psleave:10"),
        Click("evt:mkleader:10"),
        Click("evt:mkalead:10"),
        Click("evt:lockwin:10"),
        Click("evt:readypick:10", new[] { "enfeeb", "relic" }, fromEphemeral: true),
        Click("evt:ready:10"),
        Click("evt:winviewpick:10", new[] { "1" }, fromEphemeral: true),
        Click("evt:winview:10:1", fromEphemeral: true),
        Click("evt:winview:20:1", fromEphemeral: true),
        Click("evt:pastdet:5"),
        Click("evt:pastdet:6"),
        Click("evt:prevwindow:10"),
        Click("evt:xin:10"),
        Click("evt:xin:30"),
        Click("evt:checkout:10"),
        Click("evt:checkout:30"),
        Click("evt:wdpop:10"),
        Click("evt:wdpop:20"),
        Click("evt:wdpop:30"),
        Click("evt:oadd:10"),
        Click("evt:omovesrc:10", new[] { "s:2" }, fromEphemeral: true),
        Click("evt:omovedst:10:s:2", new[] { "1" }, fromEphemeral: true),
        Click("evt:omovebench:10:s:2", fromEphemeral: true),
        Click("evt:omove:10"),
        Click("evt:osetleadpick:10", new[] { "s:2" }, fromEphemeral: true),
        Click("evt:osetlead:10"),
        Click("evt:olockpick:10", new[] { "s:2" }, fromEphemeral: true),
        Click("evt:olock:10"),
        Click("evt:owithdrawgo:10:s:2", fromEphemeral: true),
        Click("evt:owithdrawpick:10", new[] { "s:2" }, fromEphemeral: true),
        Click("evt:owithdraw:10"),
        Click("evt:oaddpick:10", new[] { "u1" }, fromEphemeral: true),
        Click("evt:oaddpick:10", new[] { "__new__" }, fromEphemeral: true),
        Click("evt:oaclaim:10", new[] { "1" }, fromEphemeral: true),
        Click("evt:oawr:10:1", new[] { "Tank" }, fromEphemeral: true),
        Click("evt:oawm:10:1:Tank", new[] { "PLD" }, fromEphemeral: true),
        Click("evt:oaws:10:1:Tank:PLD", new[] { "WAR" }, fromEphemeral: true),
        Click("evt:joinevent:10"),
        Click("evt:joinevent:20"),
        Click("evt:gjwr:20", new[] { "Tank" }, fromEphemeral: true),
        Click("evt:gjwm:20:Tank", new[] { "PLD" }, fromEphemeral: true),
        Click("evt:gjws:20:Tank:PLD", new[] { "WAR" }, fromEphemeral: true),
        Click("evt:psLwr:10:1", new[] { "Tank" }, fromEphemeral: true),
        Click("evt:pswr:10:1", new[] { "Tank" }, fromEphemeral: true),
        Click("evt:psLwm:10:1:Tank", new[] { "PLD" }, fromEphemeral: true),
        Click("evt:pswm:10:1:Tank", new[] { "PLD" }, fromEphemeral: true),
        Click("evt:psLws:10:1:Tank:PLD", new[] { "WAR" }, fromEphemeral: true),
        Click("evt:psws:10:1:Tank:PLD", new[] { "WAR" }, fromEphemeral: true),
        Click("auc:pick:3", new[] { "7" }),
        Click("auc:mybids:3"),
        Click("auc:bid:7"),
        Click("evt:nosuchthing:10"),
        Click(string.Empty),

        // Modal submits.
        Submit("evt:wdpopmodal:10", ("wdpop_repost", "abc")),
        Submit("evt:wdpopmodal:10", ("wdpop_tod", "25:99")),
        Submit("evt:wdpopmodal:20"),
        Submit("evt:wdpopmodal:30", ("wdpop_outcome", "missed"), ("wdpop_repost", "no")),
        Submit("evt:outsidename:slot:10", ("outside_name", "Newguy")),
        Submit("evt:oaddnew:10", ("oadd_name", "Mill")),
        Submit("evt:oaddnew:10", ("oadd_name", "Zzz")),
        Submit("auc:bidmodal:7", ("amount", "abc")),
        Submit("auc:bidmodal:7", ("amount", "50")),
        Submit("evt:nosuchmodal:10"),
    };

    [Fact]
    public async Task EveryInteractionMatchesTheSnapshot()
    {
        var actual = await BuildLinesAsync();
        var path = Path.Combine(FindSnapshotsFolder(), SnapshotFile);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(path, string.Join("\n", actual) + "\n", new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"No snapshot at {path}. Run the test once with {UpdateVariable}=1 to write it.");
        var expected = (await File.ReadAllTextAsync(path, Encoding.UTF8))
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();

        if (expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            return;
        }

        var removed = expected.Except(actual, StringComparer.Ordinal).Select(line => "- " + line);
        var added = actual.Except(expected, StringComparer.Ordinal).Select(line => "+ " + line);
        Assert.Fail(
            "The Discord bot's answers no longer match tests/LinkshellManager.Tests/Snapshots/" + SnapshotFile + ".\n"
            + "(- = in the snapshot, + = what the bot answers now)\n"
            + string.Join("\n", removed.Concat(added)) + "\n\n"
            + $"If this change is meant, regenerate the file: set {UpdateVariable}=1, run this test again, "
            + "and commit the new snapshot with the change.");
    }

    // A bad signature is turned away before any routing, as Discord requires.
    [Fact]
    public async Task ABadSignatureIsRefusedBeforeAnyRouting()
    {
        var outcome = await H.SendAsync(H.Component("member", "evt:pssignup:10"), signed: false);
        Assert.Equal("{\"status\":401}", outcome.Body);
    }

    // Every custom_id prefix the app puts on a Discord message has at least one sample above, so
    // a new button cannot be added without its answer being pinned here.
    [Fact]
    public void EveryPrefixTheAppPostsHasASample()
    {
        var prefixes = new[] { typeof(DiscordEventMessageBuilder), typeof(AuctionBidService) }
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)
                && (field.Name.EndsWith("Prefix", StringComparison.Ordinal) || field.Name.EndsWith("ButtonId", StringComparison.Ordinal)))
            .Select(field => (string)field.GetRawConstantValue()!)
            // The bot's own ids that no shared constant names.
            .Concat(new[] { "evt:charpick:", "evt:outsidename:", "evt:wdpopmodal:" })
            .Distinct()
            .ToList();
        Assert.True(prefixes.Count > 50, "Found too few prefixes; the reflection above is broken.");

        var sampleIds = Samples
            .Select(sample => sample.Key.Split(' ') is { Length: > 1 } parts ? parts[1] : string.Empty)
            .ToList();
        var missing = prefixes
            .Where(prefix => !sampleIds.Any(id => id.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(missing.Count == 0, "No sample for: " + string.Join(", ", missing));
    }

    private static async Task<List<string>> BuildLinesAsync()
    {
        var lines = new List<string>();
        foreach (var sample in Samples)
        {
            foreach (var clicker in H.Clickers)
            {
                var outcome = await H.SendAsync(sample.Build(clicker));
                lines.Add($"{sample.Key} [{clicker}] => {outcome.Describe()}");
            }
        }
        return lines;
    }

    private static string FindSnapshotsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var project = Path.Combine(directory.FullName, "tests", "LinkshellManager.Tests");
            if (Directory.Exists(project))
            {
                var snapshots = Path.Combine(project, "Snapshots");
                Directory.CreateDirectory(snapshots);
                return snapshots;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate tests/LinkshellManager.Tests above {AppContext.BaseDirectory}.");
    }
}
