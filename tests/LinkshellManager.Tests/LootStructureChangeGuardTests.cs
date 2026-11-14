using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.Tests;

// dead-dkp-02: uncharged loot on a live event is charged at close in whatever unit the Loot
// Structure says THEN (DKP, or a percent for Hybrid). The settings save refuses a structure change
// while any such loot waits, and this is the question it asks.
public class LootStructureChangeGuardTests
{
    private const int Ls = 7;
    private const int OtherLs = 8;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();
        db.Linkshells.AddRange(new Linkshell { Id = Ls, LinkshellName = "LS" }, new Linkshell { Id = OtherLs, LinkshellName = "Other" });
        db.Events.AddRange(
            new Event { Id = 1, LinkshellId = Ls, EventName = "Sky" },
            new Event { Id = 2, LinkshellId = OtherLs, EventName = "Sea" });
        db.EventHistories.Add(new EventHistory { Id = 10, LinkshellId = Ls, EventName = "Dynamis" });
        await db.SaveChangesAsync();
        return db;
    }

    private static Task<bool> AskAsync(ApplicationDbContext db)
        => LootDkpGuard.HasUnchargedLiveLootAsync(db, Ls, CancellationToken.None);

    [Fact]
    public async Task PricedUnchargedLootOnALiveEvent_BlocksTheChange()
    {
        await using var db = await SeededAsync();
        db.EventLootDetails.Add(new EventLootDetail { EventId = 1, ItemName = "Osode", WinningDkpSpent = 30 });
        await db.SaveChangesAsync();

        Assert.True(await AskAsync(db));
    }

    [Fact]
    public async Task NothingWaiting_AllowsTheChange()
    {
        await using var db = await SeededAsync();
        db.EventLootDetails.AddRange(
            // Already charged (hand-entered loot is charged on the spot).
            new EventLootDetail { EventId = 1, ItemName = "Charged", WinningDkpSpent = 30, DkpDebitedAt = DateTime.UtcNow },
            // No price: nothing to charge either way.
            new EventLootDetail { EventId = 1, ItemName = "Free", WinningDkpSpent = 0 },
            new EventLootDetail { EventId = 1, ItemName = "Council", WinningDkpSpent = null },
            // A past event's loot was charged when it closed.
            new EventLootDetail { EventHistoryId = 10, ItemName = "Archived", WinningDkpSpent = 30 },
            // Another linkshell's live loot is not ours.
            new EventLootDetail { EventId = 2, ItemName = "Theirs", WinningDkpSpent = 30 });
        await db.SaveChangesAsync();

        Assert.False(await AskAsync(db));
    }

    // The save only asks when the structure actually changes, so re-saving the same structure (the
    // settings page sends it with every save) is never refused.
    [Fact]
    public void SettingsSave_AsksOnlyWhenTheStructureChanges()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "Controllers", "Activity", "Settings", "ActivitySettingsController.LinkshellCrud.cs"));
        var start = source.IndexOf("public async Task<IActionResult> UpdateLinkshellAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "UpdateLinkshellAsync not found -- did it move?");
        var body = source[start..];

        var changed = body.IndexOf("newStructure != NormalizeLootStructure(linkshell.LootStructure", StringComparison.Ordinal);
        var asked = body.IndexOf("LootDkpGuard.HasUnchargedLiveLootAsync(", StringComparison.Ordinal);
        var saved = body.IndexOf("linkshell.LootStructure = newStructure", StringComparison.Ordinal);
        Assert.True(changed >= 0 && asked > changed && saved > asked,
            "The structure change check must run, after the 'did it change' test, before the save.");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Controllers")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate the repo root above {AppContext.BaseDirectory}.");
    }
}
