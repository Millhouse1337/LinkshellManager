using LinkshellManagerDiscordApp.Controllers.Discord;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using H = LinkshellManager.Tests.DiscordInteractionHarness;

namespace LinkshellManager.Tests;

// Pins the Discord bot's routing table: every custom_id prefix, modal id and command it answers,
// how each is matched, and which handler method it reaches.
//
// Why: DiscordInteractionsController used to route with one long if/else chain; the routes now
// live with their feature handlers and DiscordInteractionDispatcher joins them. Buttons already
// posted in Discord keep their ids forever, so an id silently dropped or moved to other code would
// break boards nobody is looking at. DiscordInteractionRoutingSnapshotTests pins what each id
// ANSWERS; this pins where it GOES.
//
// When a routing change is MEANT (a new button, a handler renamed), regenerate the file:
//   PowerShell:  $env:UPDATE_DISCORD_ROUTES=1; dotnet test tests/LinkshellManager.Tests --filter DiscordInteractionRouteTableTests; Remove-Item Env:UPDATE_DISCORD_ROUTES
//   bash:        UPDATE_DISCORD_ROUTES=1 dotnet test tests/LinkshellManager.Tests --filter DiscordInteractionRouteTableTests
// and review the diff of Snapshots/discord-routes.txt in the commit.
public class DiscordInteractionRouteTableTests
{
    private const string SnapshotFile = "discord-routes.txt";
    private const string UpdateVariable = "UPDATE_DISCORD_ROUTES";

    [Fact]
    public void TheRouteTableMatchesTheSnapshot()
    {
        var actual = H.RouteTable().Select(Describe).ToList();
        var path = Path.Combine(SnapshotsFolder(), SnapshotFile);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, string.Join("\n", actual) + "\n");
            return;
        }

        Assert.True(File.Exists(path), $"No snapshot at {path}. Run the test once with {UpdateVariable}=1 to write it.");
        var expected = File.ReadAllText(path)
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
            "The Discord routing table no longer matches tests/LinkshellManager.Tests/Snapshots/" + SnapshotFile + ".\n"
            + "(- = in the snapshot, + = routed now)\n"
            + string.Join("\n", removed.Concat(added)) + "\n\n"
            + $"If this change is meant, regenerate the file: set {UpdateVariable}=1, run this test again, "
            + "and commit the new snapshot with the change.");
    }

    // The old chain tested prefixes one after another, so a prefix that was the start of another
    // ("evt:olock:" vs a hypothetical "evt:olock:pick:") would have depended on which came first.
    // No two ids overlap, so which route answers depends on the id alone - never on table order.
    [Fact]
    public void NoTwoRoutesCanMatchTheSameId()
    {
        var overlap = DiscordInteractionDispatcher.FindOverlap(H.RouteTable());
        Assert.True(overlap is null,
            overlap is { } o ? $"'{o.First.Id}' ({o.First.Handler}) and '{o.Second.Id}' ({o.Second.Handler}) overlap." : "");
    }

    // And the dispatcher refuses to start with a table that has one.
    [Theory]
    [InlineData("evt:olock:", "evt:olock:pick:")]
    [InlineData("evt:job:", "evt:job:")]
    public void TheDispatcherRefusesOverlappingIds(string first, string second)
    {
        var handler = new FixedRoutes(
            Route(DiscordInteractionKind.Component, first),
            Route(DiscordInteractionKind.Component, second));

        var error = Assert.Throws<InvalidOperationException>(() =>
            new DiscordInteractionDispatcher(new DiscordInteractionContext(), null!, new[] { handler }));
        Assert.Contains(first, error.Message);
    }

    // The same text is fine across kinds: a button and a modal never compete.
    [Fact]
    public void TheSameIdOnADifferentKindIsNotAnOverlap()
    {
        var handler = new FixedRoutes(
            Route(DiscordInteractionKind.Component, "evt:x:"),
            Route(DiscordInteractionKind.Modal, "evt:x:"));

        Assert.Single(new DiscordInteractionDispatcher(new DiscordInteractionContext(), null!, new[] { handler })
            .Routes, route => route.Kind == DiscordInteractionKind.Modal);
    }

    // Every route is exercised by at least one sample in the answer snapshot, so a route cannot be
    // added (or moved) without its answers being pinned.
    [Fact]
    public void EveryRouteHasASampleInTheAnswerSnapshot()
    {
        var samples = DiscordInteractionRoutingSnapshotTests.Samples
            .Select(sample => sample.Key.Split(' '))
            .Select(parts => (Kind: parts[0], Id: parts.Length > 1 ? parts[1] : string.Empty))
            .ToList();

        var missing = H.RouteTable()
            .Where(route => !samples.Any(sample => sample.Kind switch
            {
                "component" => route.Kind == DiscordInteractionKind.Component && route.Matches(sample.Id),
                "modal" => route.Kind == DiscordInteractionKind.Modal && route.Matches(sample.Id),
                "command" => route.Kind == DiscordInteractionKind.Command && route.Matches(sample.Id.TrimStart('/')),
                _ => false,
            }))
            .Select(Describe)
            .ToList();

        Assert.True(missing.Count == 0, "No answer-snapshot sample reaches:\n" + string.Join("\n", missing));
    }

    // Each feature area owns its ids; spot-check one per handler so a route list emptied by
    // mistake fails here by name rather than only as a snapshot diff.
    [Theory]
    [InlineData(DiscordInteractionKind.Command, "lsm", "LaunchHandler.")]
    [InlineData(DiscordInteractionKind.Component, "evt:launch", "LaunchHandler.")]
    [InlineData(DiscordInteractionKind.Component, "evt:charpick:slot:1", "OutsideSignupHandler.")]
    [InlineData(DiscordInteractionKind.Modal, "evt:outsidename:slot:1", "OutsideSignupHandler.")]
    [InlineData(DiscordInteractionKind.Component, "evt:pssignup:1", "PartyBoardHandler.")]
    [InlineData(DiscordInteractionKind.Component, "evt:xin:1", "HnmCampBoardHandler.")]
    [InlineData(DiscordInteractionKind.Component, "evt:wdpop:1", "EndCampHandler.")]
    [InlineData(DiscordInteractionKind.Modal, "evt:wdpopmodal:1", "EndCampHandler.")]
    [InlineData(DiscordInteractionKind.Component, "evt:omove:1", "OfficerToolsHandler.")]
    [InlineData(DiscordInteractionKind.Modal, "evt:oaddnew:1", "OfficerToolsHandler.")]
    [InlineData(DiscordInteractionKind.Component, "auc:bid:1", "AuctionInteractionHandler.")]
    [InlineData(DiscordInteractionKind.Modal, "auc:bidmodal:1", "AuctionInteractionHandler.")]
    public void EachFeatureOwnsItsIds(DiscordInteractionKind kind, string id, string handlerPrefix)
    {
        var route = Assert.Single(H.RouteTable(), r => r.Kind == kind && r.Matches(id));
        Assert.StartsWith(handlerPrefix, route.Handler);
    }

    private static string Describe(DiscordInteractionRoute route) =>
        $"{route.Kind.ToString().ToLowerInvariant()} {route.Id}"
        + (route.Exact ? " (exact)" : string.Empty)
        + (route.BeforeIdentity ? " (before identity)" : string.Empty)
        + (route.NeedsLinkedAccount ? " (needs linked account)" : string.Empty)
        + $" -> {route.Handler}";

    private static DiscordInteractionRoute Route(DiscordInteractionKind kind, string id) =>
        new(kind, id, "Test", _ => Task.FromResult<IActionResult>(new OkResult()));

    private sealed class FixedRoutes : IDiscordInteractionHandler
    {
        public FixedRoutes(params DiscordInteractionRoute[] routes) => Routes = routes;

        public IReadOnlyList<DiscordInteractionRoute> Routes { get; }
    }

    private static string SnapshotsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var project = Path.Combine(directory.FullName, "tests", "LinkshellManager.Tests");
            if (Directory.Exists(project))
            {
                return Path.Combine(project, "Snapshots");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate tests/LinkshellManager.Tests above {AppContext.BaseDirectory}.");
    }
}
