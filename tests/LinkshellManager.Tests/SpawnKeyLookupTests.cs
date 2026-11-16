using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// ToDs, pending ToDs and standing boards are found by SpawnKey now (one equality), where they used
// to be found by "is the stored name one of every spelling of this spawn" (MonsterMatchNames). For
// every monster name production holds, in any capitals, both rules must pick exactly the same rows,
// so the same-kill merge, the board lookup, the day cycle and the repop all behave as before.
//
// The rows below are seeded under the spellings older builds stored ("Nidhogg", "fafnir",
// "Fafnir/Nidhogg"), not the combined name TodRecorder saves today.
public class SpawnKeyLookupTests
{
    private const int LinkshellId = 21;
    private const int OtherLinkshellId = 22;
    private static readonly DateTime KilledAt = new(2026, 11, 10, 20, 0, 0, DateTimeKind.Utc);

    // Every monster name on a production ToD or standing board, and the halves of the merge pairs.
    public static readonly string[] ProductionNames =
    {
        "Adamantoise", "Aspidochelone", "Adamantoise/Aspidochelone",
        "Behemoth", "King Behemoth", "Behemoth/King Behemoth",
        "Fafnir", "Nidhogg", "Fafnir/Nidhogg",
        "Cerberus", "Hydra", "Jormungand", "Khimaira", "King Arthro",
        "Misc Monster", "Testing", "Tiamat", "Vrtra",
    };

    public static IEnumerable<object[]> NamePairs() =>
        from asked in ProductionNames.SelectMany(Spellings)
        from stored in ProductionNames.SelectMany(Spellings)
        select new object[] { asked, stored };

    // A name as an officer might type it or an older build might store it.
    private static IEnumerable<string> Spellings(string name) =>
        new[] { name, name.ToLowerInvariant(), name.ToUpperInvariant(), $" {name} " }.Distinct();

    [Theory]
    [MemberData(nameof(NamePairs))]
    public void SpawnKey_MatchesTheSameRows_AsEverySpellingOfTheSpawn(string asked, string stored)
    {
        // The old rule compared the stored name as written, so a padded stored name never matched.
        // No production name is padded; the new rule trims, so it finds those rows too.
        // (The old rule's lower-cased list, MonsterMatchNamesLower, is spelled out here: the helper
        // was deleted once nothing in the app used it.)
        var oldRule = HnmConfig.MonsterMatchNames(asked)
            .Select(name => name.ToLowerInvariant())
            .Contains(stored.ToLowerInvariant());
        var newRule = HnmConfig.SpawnKey(asked) == HnmConfig.SpawnKey(stored);
        if (stored == stored.Trim())
        {
            Assert.Equal(oldRule, newRule);
        }
        else
        {
            Assert.Equal(HnmConfig.SpawnKey(asked) == HnmConfig.SpawnKey(stored.Trim()), newRule);
        }
    }

    [Theory]
    [InlineData("Fafnir")]
    [InlineData("Nidhogg")]
    [InlineData("Fafnir/Nidhogg")]
    [InlineData("fafnir")]
    public async Task SameKill_FindsTheKill_UnderAnySpellingOfTheSpawn(string asked)
    {
        foreach (var stored in new[] { "Fafnir", "Nidhogg", "Fafnir/Nidhogg", "FAFNIR" })
        {
            await using var db = await SeededAsync(new Tod
            {
                Id = 1, LinkshellId = LinkshellId, MonsterName = stored, Time = KilledAt,
            });
            var found = await TodSameKill.FindAsync(db, LinkshellId, asked, KilledAt.AddSeconds(30), CancellationToken.None);
            Assert.Equal(1, found?.Id);
        }
    }

    [Fact]
    public async Task SameKill_KeepsOtherMonstersAndOtherLinkshellsApart()
    {
        await using var db = await SeededAsync(
            new Tod { Id = 1, LinkshellId = LinkshellId, MonsterName = "Tiamat", Time = KilledAt },
            new Tod { Id = 2, LinkshellId = OtherLinkshellId, MonsterName = "Fafnir", Time = KilledAt },
            new Tod { Id = 3, LinkshellId = LinkshellId, MonsterName = "King Behemoth", Time = KilledAt });

        Assert.Null(await TodSameKill.FindAsync(db, LinkshellId, "Fafnir", KilledAt, CancellationToken.None));
        Assert.Equal(1, (await TodSameKill.FindAsync(db, LinkshellId, "tiamat", KilledAt, CancellationToken.None))?.Id);
        Assert.Equal(3, (await TodSameKill.FindAsync(db, LinkshellId, "Behemoth/King Behemoth", KilledAt, CancellationToken.None))?.Id);
    }

    // A blank name has no spawn key. It must find nothing, not every other row with no key.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankName_FindsNothing_EvenBesideRowsWithNoName(string? asked)
    {
        await using var db = await SeededAsync(new Tod { Id = 1, LinkshellId = LinkshellId, MonsterName = "", Time = KilledAt });
        db.PendingTodSubmissions.Add(new PendingTodSubmission
        {
            Id = 1, LinkshellId = LinkshellId, MonsterName = " ", Time = KilledAt, SubmittedAtUtc = KilledAt,
        });
        await db.SaveChangesAsync();
        Assert.Null(await db.Tods.Where(t => t.Id == 1).Select(t => t.SpawnKey).SingleAsync());

        Assert.Null(await TodSameKill.FindAsync(db, LinkshellId, asked, KilledAt, CancellationToken.None));
        Assert.Null(await TodSameKill.FindPendingAsync(db, LinkshellId, asked, KilledAt, CancellationToken.None));
        Assert.Null(await HnmRecurringBoardService.LatestTodIdAsync(db, LinkshellId, asked ?? string.Empty, CancellationToken.None));
        Assert.Null(await HnmRecurringBoardService.FindAsync(db, LinkshellId, asked ?? string.Empty, CancellationToken.None));
    }

    [Theory]
    [InlineData("Nidhogg")]
    [InlineData("Aspidochelone")]
    [InlineData("King Behemoth")]
    public async Task SamePendingKill_FindsTheQueuedOne_UnderAnySpelling(string asked)
    {
        await using var db = await SeededAsync();
        var stored = HnmConfig.MonsterMergePairs
            .Single(pair => pair.Stronger == asked).Base;
        db.PendingTodSubmissions.Add(new PendingTodSubmission
        {
            Id = 5, LinkshellId = LinkshellId, MonsterName = stored, Time = KilledAt, SubmittedAtUtc = KilledAt,
        });
        await db.SaveChangesAsync();

        var found = await TodSameKill.FindPendingAsync(db, LinkshellId, asked, KilledAt.AddMinutes(1), CancellationToken.None);
        Assert.Equal(5, found?.Id);
    }

    [Theory]
    [InlineData("Fafnir")]
    [InlineData("Nidhogg")]
    [InlineData("fafnir/nidhogg")]
    public async Task TheStandingBoard_AndItsLatestTod_AreFoundUnderAnySpelling(string asked)
    {
        await using var db = await SeededAsync(
            new Tod { Id = 1, LinkshellId = LinkshellId, MonsterName = "Nidhogg", Time = KilledAt, RepopTime = KilledAt.AddHours(84) },
            new Tod { Id = 2, LinkshellId = LinkshellId, MonsterName = "Fafnir", Time = KilledAt.AddDays(-4), RepopTime = KilledAt.AddHours(-12) },
            new Tod { Id = 3, LinkshellId = LinkshellId, MonsterName = "Tiamat", Time = KilledAt, RepopTime = KilledAt.AddHours(90) });
        db.HnmRecurringBoards.AddRange(
            new HnmRecurringBoard { Id = 7, LinkshellId = LinkshellId, MonsterName = "Fafnir/Nidhogg", Enabled = true },
            new HnmRecurringBoard { Id = 8, LinkshellId = LinkshellId, MonsterName = "Tiamat", Enabled = true },
            new HnmRecurringBoard { Id = 9, LinkshellId = OtherLinkshellId, MonsterName = "Fafnir/Nidhogg", Enabled = true });
        await db.SaveChangesAsync();

        Assert.Equal(7, (await HnmRecurringBoardService.FindAsync(db, LinkshellId, asked, CancellationToken.None))?.Id);
        Assert.Equal(1, await HnmRecurringBoardService.LatestTodIdAsync(db, LinkshellId, asked, CancellationToken.None));
    }

    [Fact]
    public async Task LatestAndUpcomingLookups_GiveOneEntryPerSpawn()
    {
        await using var db = await SeededAsync(
            new Tod { Id = 1, LinkshellId = LinkshellId, MonsterName = "Nidhogg", Time = KilledAt, TimeStamp = KilledAt, RepopTime = KilledAt.AddHours(84) },
            new Tod { Id = 2, LinkshellId = LinkshellId, MonsterName = "Fafnir/Nidhogg", Time = KilledAt.AddDays(-4), TimeStamp = KilledAt.AddDays(-4), RepopTime = KilledAt.AddHours(1) },
            new Tod { Id = 3, LinkshellId = LinkshellId, MonsterName = "tiamat", Time = KilledAt, TimeStamp = KilledAt, RepopTime = KilledAt.AddHours(90) },
            new Tod { Id = 4, LinkshellId = LinkshellId, MonsterName = "Tiamat", Time = KilledAt.AddDays(-5), TimeStamp = KilledAt.AddDays(-5), RepopTime = KilledAt.AddHours(2) },
            new Tod { Id = 5, LinkshellId = LinkshellId, MonsterName = "  ", Time = KilledAt, TimeStamp = KilledAt, RepopTime = KilledAt.AddHours(3) });

        var latest = await LatestTodLookup.ForLinkshellAsync(db, LinkshellId, CancellationToken.None);
        Assert.Equal(new[] { 1, 3 }, latest.Select(entry => entry.TodId).Order().ToArray());
        Assert.Contains("Fafnir/Nidhogg", latest.Single(entry => entry.TodId == 1).MatchNames);

        var upcoming = await UpcomingRepopLookup.ForLinkshellAsync(db, LinkshellId, KilledAt, CancellationToken.None);
        Assert.Equal(new[] { 1, 3 }, upcoming.Select(entry => entry.TodId).Order().ToArray());
    }

    private static async Task<ApplicationDbContext> SeededAsync(params Tod[] tods)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Linkshells.Add(new Linkshell { Id = OtherLinkshellId, LinkshellName = "Other" });
        db.Tods.AddRange(tods);
        await db.SaveChangesAsync();
        return db;
    }
}
