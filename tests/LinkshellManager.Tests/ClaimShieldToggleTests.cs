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

// The per-monster Claim Shield switch, and the one thing about it that can go badly wrong: a save
// that quietly turns everything off.
//
// Monster setups saves are a FULL REPLACE — every row is rewritten from the posted form — so the
// meaning of "the client didn't send a value" decides whether an older client, or the web page
// while the server-wide switch is off, wipes the linkshell's choices. Null must mean "leave it
// alone"; only an explicit false may switch a monster off.
public class ClaimShieldToggleTests
{
    private const int LinkshellId = 42;
    private static readonly DateTime Now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static MonsterTimingEditor NewEditor(ApplicationDbContext db) =>
        new(db, new LinkshellMonsterTimingProvisioner(db), new MonsterTimingResolver(db));

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewInMemoryContext();
        db.LinkshellMonsterTimings.AddRange(
            LinkshellMonsterTimingProvisioner.BuildSeed(LinkshellId, null, Now));
        await db.SaveChangesAsync();
        return db;
    }

    // Every seeded row round-tripped, carrying `claimShield` for the toggle under test.
    private static async Task<string?> SaveAllAsync(
        ApplicationDbContext db, bool? claimShield)
    {
        var rows = await db.LinkshellMonsterTimings
            .Where(row => row.LinkshellId == LinkshellId)
            .OrderBy(row => row.SortOrder)
            .ToListAsync();

        var edits = rows.Select(row => new MonsterTimingEdit(
            row.Id,
            row.MonsterName,
            row.WindowCount,
            row.WindowCadenceMinutes,
            "mins",
            row.CooldownMinutes,
            "mins",
            row.Category,
            claimShield)).ToList();

        return await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None);
    }

    private static Task<LinkshellMonsterTiming> FafnirAsync(ApplicationDbContext db) =>
        db.LinkshellMonsterTimings.FirstAsync(row =>
            row.LinkshellId == LinkshellId && row.NormalizedMonsterName.StartsWith("fafnir"));

    [Fact]
    public async Task SeededRows_StartWithClaimShieldOn()
    {
        await using var db = await SeededAsync();
        Assert.All(
            await db.LinkshellMonsterTimings.Where(r => r.LinkshellId == LinkshellId).ToListAsync(),
            row => Assert.True(row.ClaimShieldEnabled));
    }

    // THE case this file exists for. A client that predates the column sends null for every row; a
    // full-replace save that read that as false would switch Claim Shield off for the whole
    // linkshell, and the symptom — captures silently stop — is invisible until someone goes looking.
    [Fact]
    public async Task SaveWithoutTheField_LeavesEveryRowAlone()
    {
        await using var db = await SeededAsync();
        var fafnir = await FafnirAsync(db);
        fafnir.ClaimShieldEnabled = false;   // one monster deliberately switched off
        await db.SaveChangesAsync();

        Assert.Null(await SaveAllAsync(db, claimShield: null));

        // The deliberate off stayed off, and nothing else was dragged off with it.
        Assert.False((await FafnirAsync(db)).ClaimShieldEnabled);
        Assert.All(
            await db.LinkshellMonsterTimings
                .Where(r => r.LinkshellId == LinkshellId && r.Id != fafnir.Id).ToListAsync(),
            row => Assert.True(row.ClaimShieldEnabled));
    }

    [Fact]
    public async Task ExplicitFalse_SwitchesAMonsterOff()
    {
        await using var db = await SeededAsync();
        Assert.Null(await SaveAllAsync(db, claimShield: false));

        Assert.All(
            await db.LinkshellMonsterTimings.Where(r => r.LinkshellId == LinkshellId).ToListAsync(),
            row => Assert.False(row.ClaimShieldEnabled));
    }

    [Fact]
    public async Task ExplicitTrue_SwitchesItBackOn()
    {
        await using var db = await SeededAsync();
        await SaveAllAsync(db, claimShield: false);
        Assert.Null(await SaveAllAsync(db, claimShield: true));

        Assert.All(
            await db.LinkshellMonsterTimings.Where(r => r.LinkshellId == LinkshellId).ToListAsync(),
            row => Assert.True(row.ClaimShieldEnabled));
    }

    // A newly added custom monster is one the linkshell camps, so it arrives on.
    [Fact]
    public async Task NewCustomMonster_DefaultsToOn()
    {
        await using var db = await SeededAsync();
        var existing = await db.LinkshellMonsterTimings
            .Where(row => row.LinkshellId == LinkshellId)
            .OrderBy(row => row.SortOrder)
            .ToListAsync();

        var edits = existing.Select(row => new MonsterTimingEdit(
                row.Id, row.MonsterName, row.WindowCount, row.WindowCadenceMinutes, "mins",
                row.CooldownMinutes, "mins", row.Category, true))
            .Append(new MonsterTimingEdit(
                null, "Ouryu", null, 10, "mins", 22, "hours",
                MonsterTimingDefaults.OtherCategory, true))
            .ToList();

        Assert.Null(await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None));

        var added = await db.LinkshellMonsterTimings
            .FirstAsync(row => row.LinkshellId == LinkshellId && row.MonsterName == "Ouryu");
        Assert.True(added.IsCustom);
        Assert.True(added.ClaimShieldEnabled);
    }
}
