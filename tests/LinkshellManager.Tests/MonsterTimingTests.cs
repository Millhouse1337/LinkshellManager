using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Per-linkshell monster setups: the table that replaced the ToD-cooldown JSON blob and the
// read-only window-setups list, and is now the source of truth for both.
//
// The cases that matter are the ones a type checker can't catch: that all three spellings of a
// merged pair resolve to ONE row, that an unconfigured linkshell still behaves exactly as it did
// before this table existed, and that the seeded catalog and the event dropdown can't drift apart.
public class MonsterTimingTests
{
    private const int LinkshellId = 42;
    private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> SeededAsync(string? legacyBlob = null)
    {
        var db = NewInMemoryContext();
        db.LinkshellMonsterTimings.AddRange(
            LinkshellMonsterTimingProvisioner.BuildSeed(LinkshellId, legacyBlob, Now));
        await db.SaveChangesAsync();
        return db;
    }

    // ---- Defaults ----

    // The seeded catalog and the create-event dropdown must list the same monsters, or the picker
    // offers something the table can't configure (or vice versa). This is the test that stops the
    // two drifting.
    [Fact]
    public void SeededCatalog_ContainsEveryDropdownOption()
    {
        var seeded = MonsterTimingDefaults.BuildAll().Select(t => t.MonsterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var option in HnmConfig.CombinedMonsterOptions(TodManagerViewModel.SupportedMonsters))
        {
            Assert.Contains(option, seeded);
        }
    }

    // The three NQ/HQ families are ONE row, matching how the dropdown offers them and how
    // Event.AssignedMonsterName stores them.
    [Theory]
    [InlineData("Adamantoise/Aspidochelone")]
    [InlineData("Behemoth/King Behemoth")]
    [InlineData("Fafnir/Nidhogg")]
    public void MergedPairs_AreOneSeededRow(string combined)
    {
        var names = MonsterTimingDefaults.BuildAll().Select(t => t.MonsterName).ToList();
        Assert.Contains(combined, names);
        foreach (var half in combined.Split('/'))
        {
            Assert.DoesNotContain(half, names);
        }
    }

    // The Sky farm NMs were seeded as a heading of their own; they are not seeded at all any more.
    // The seed is now EXACTLY the create-event dropdown, so this is the other half of
    // SeededCatalog_ContainsEveryDropdownOption — together they pin the two lists as equal rather
    // than merely overlapping.
    [Fact]
    public void SkyFarmNms_AreNotSeeded()
    {
        var seeded = MonsterTimingDefaults.BuildAll().Select(t => t.MonsterName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var skyNm in HnmConfig.SkyFarmNmOrder)
        {
            Assert.DoesNotContain(skyNm, seeded);
        }
    }

    // ...and the timing FACT survives the catalog removal: a Sky ToD typed through the picker's
    // "Other" branch still resolves the 2-hour repop rather than falling to the 22-hour default.
    [Fact]
    public void SkyFarmNms_StillResolveTheirTwoHourCooldown()
    {
        foreach (var skyNm in HnmConfig.SkyFarmNmOrder)
        {
            Assert.Equal(2 * 60, MonsterTimingDefaults.Build(skyNm).CooldownMinutes);
            Assert.Null(MonsterTimingDefaults.Build(skyNm).WindowCount);
        }
    }

    // The migration that removes the rows already written keeps its own copy of the eight names
    // (SQL in a migration runs once against real data with nothing watching). A name that drifts
    // there leaves a row behind under a heading the editor no longer renders.
    [Fact]
    public void TheRemovalMigration_NamesTheSameEightNms()
    {
        Assert.Equal(
            HnmConfig.SkyFarmNmOrder.OrderBy(name => name, StringComparer.Ordinal),
            RemoveSkyNmMonsterTimings.SkyFarmNms.OrderBy(name => name, StringComparer.Ordinal));
    }

    // The ToAU three are the case worth pinning: they sit INSIDE LongWindowHnms beside the wyrms,
    // so the only thing keeping them off the wyrms' 84h is that ToauHnms is tested first.
    [Theory]
    [InlineData("Tiamat", 84 * 60)]
    [InlineData("Jormungand", 84 * 60)]
    [InlineData("Vrtra", 84 * 60)]
    [InlineData("Cerberus", 48 * 60)]
    [InlineData("Hydra", 48 * 60)]
    [InlineData("Khimaira", 48 * 60)]
    [InlineData("Bloodsucker", 71 * 60)]
    [InlineData("Despot", 2 * 60)]
    [InlineData("Fafnir/Nidhogg", 22 * 60)]
    [InlineData("Serket", 22 * 60)]
    public void DefaultCooldowns_MatchTheBuiltInBands(string monster, int expectedMinutes) =>
        Assert.Equal(expectedMinutes, MonsterTimingDefaults.DefaultCooldownMinutes(monster));

    // A cooldown is the hour the spawn window OPENS; the grid then runs on top of it. Window 1
    // opens AT the cooldown (HnmConfig.WindowNumberAt anchors it there), so the last window opens
    // (WindowCount - 1) x cadence later — 24h for a 25 x 60-min band, not 25h.
    //
    // Storing the close instead is precisely the bug that sent Khimaira's re-post a whole window
    // late, and the shape of it is invisible unless the two numbers are read together: 48 + 24 is
    // exactly the 72 these three used to be seeded with.
    [Theory]
    [InlineData("Tiamat", 84 * 60, 108 * 60)]
    [InlineData("Khimaira", 48 * 60, 72 * 60)]
    public void ALongWindowCooldown_IsTheWindowOpenNotItsClose(string monster, int openMinutes, int closeMinutes)
    {
        var timing = MonsterTimingDefaults.Build(monster);
        Assert.Equal(openMinutes, timing.CooldownMinutes);
        Assert.NotNull(timing.WindowCount);
        Assert.NotNull(timing.WindowCadenceMinutes);
        Assert.Equal(
            closeMinutes,
            timing.CooldownMinutes + (timing.WindowCount!.Value - 1) * timing.WindowCadenceMinutes!.Value);
    }

    // The migration that re-times the rows already written keeps its own copy of the three names
    // and of both minute values (SQL in a migration runs once against real data with nothing
    // watching). A name or a number that drifts there leaves a linkshell scheduling these camps a
    // full window late — the exact failure the migration exists to clear.
    [Fact]
    public void TheToauCooldownMigration_NamesTheSameThreeMonsters()
    {
        Assert.Equal(
            HnmConfig.ToauHnms.OrderBy(name => name, StringComparer.Ordinal),
            AdjustToauHnmCooldowns.ToauHnms.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(MonsterTimingDefaults.ToauCooldownMinutes, AdjustToauHnmCooldowns.NewCooldownMinutes);
    }

    // The seeded setup for the ToAU three: five windows six hours apart, not the wyrms' 25 x 60
    // they would inherit from LongWindowHnms if any resolver tested that set first.
    [Theory]
    [InlineData("Cerberus")]
    [InlineData("Hydra")]
    [InlineData("Khimaira")]
    public void TheToauThree_SeedTheirOwnFiveWindowBand(string monster)
    {
        var timing = MonsterTimingDefaults.Build(monster);
        Assert.Equal(HnmConfig.ToauWindowCount, timing.WindowCount);
        Assert.Equal(HnmConfig.ToauWindowCadenceMinutes, timing.WindowCadenceMinutes);
        Assert.Equal(5, timing.WindowCount);
        Assert.Equal(6 * 60, timing.WindowCadenceMinutes);

        // Same numbers through the two runtime readers, which resolve independently of Build.
        Assert.Equal(HnmConfig.ToauWindowCount, HnmConfig.EffectiveWindowCount(monster));
        Assert.Equal(HnmConfig.ToauWindowCadenceMinutes, HnmConfig.WindowAdvanceMinutes(monster));
        Assert.Equal(HnmConfig.ToauWindowCount, HnmConfig.GetWindowCount(monster));
    }

    // The window band and the repop are two halves of one fact, and the wyrms are the control: both
    // bands open at their cooldown and run a full 24 hours, so a wyrm's 25 hourly windows and a ToAU
    // camp's 5 six-hourly ones close at the same distance from the kill. Changing the bucketing must
    // not move the camp's end.
    [Fact]
    public void TheToauBand_CoversTheSame24HoursTheWyrmsDo()
    {
        var wyrm = MonsterTimingDefaults.Build("Tiamat");
        var toau = MonsterTimingDefaults.Build("Cerberus");

        static int Span(MonsterTimingDefaults.DefaultTiming t) =>
            (t.WindowCount!.Value - 1) * t.WindowCadenceMinutes!.Value;

        Assert.Equal(24 * 60, Span(wyrm));
        Assert.Equal(24 * 60, Span(toau));
    }

    // The migration that re-times already-seeded rows keeps its own copy of the four numbers, for
    // the same reason it keeps its own copy of the names. A NEW value that drifts leaves every
    // existing linkshell on a band the app no longer runs; an OLD value that drifts makes the
    // WHERE clause match nothing and the migration a silent no-op.
    [Fact]
    public void TheToauWindowBandMigration_NamesTheSameMonstersAndNumbers()
    {
        Assert.Equal(
            HnmConfig.ToauHnms.OrderBy(name => name, StringComparer.Ordinal),
            AdjustToauHnmWindowBand.ToauHnms.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(HnmConfig.ToauWindowCount, AdjustToauHnmWindowBand.NewWindowCount);
        Assert.Equal(HnmConfig.ToauWindowCadenceMinutes, AdjustToauHnmWindowBand.NewWindowCadenceMinutes);

        // The old values are the wyrms' band — what these rows were seeded with while the ToAU three
        // were resolved through LongWindowHnms. Read off that set rather than restated, so the guard
        // still means "the value the seeder wrote" if the wyrms are ever re-timed.
        var wyrm = HnmConfig.DefaultWindowCadence("Tiamat");
        Assert.NotNull(wyrm);
        Assert.Equal(AdjustToauHnmWindowBand.OldWindowCount, wyrm!.Value.Windows);
        Assert.Equal(AdjustToauHnmWindowBand.OldWindowCadenceMinutes, wyrm.Value.Minutes);
    }

    // Every ToAU name the migration rewrites is a long-window monster the WYRM branch would
    // otherwise have claimed, which is what makes the ordering inside DefaultCooldownMinutes
    // load-bearing rather than incidental.
    [Fact]
    public void TheToauThree_AreASubsetOfTheLongWindowHnms()
    {
        foreach (var monster in HnmConfig.ToauHnms)
        {
            Assert.Contains(monster, HnmConfig.LongWindowHnms);
        }
        Assert.All(HnmConfig.LongWindowHnms.Except(HnmConfig.ToauHnms),
            wyrm => Assert.Equal(
                MonsterTimingDefaults.WyrmCooldownMinutes,
                MonsterTimingDefaults.DefaultCooldownMinutes(wyrm)));
    }

    [Fact]
    public void EverySeededRow_HasACategoryTheEditorRenders()
    {
        foreach (var timing in MonsterTimingDefaults.BuildAll())
        {
            Assert.Contains(timing.Category, MonsterTimingDefaults.Categories);
        }
    }

    // ---- Resolver ----

    // The whole point of routing lookups through HnmConfig.MonsterMatchNames: a caller holding any
    // spelling of a merged pair lands on the same row. A board stores the combined label, a ToD
    // logged before the pairs were merged holds a bare half, and both must resolve.
    [Theory]
    [InlineData("Fafnir")]
    [InlineData("Nidhogg")]
    [InlineData("Fafnir/Nidhogg")]
    [InlineData("fafnir/nidhogg")]
    public async Task Resolver_ResolvesEverySpellingOfAMergedPair(string spelling)
    {
        await using var db = await SeededAsync();
        var map = await new MonsterTimingResolver(db).GetMapAsync(LinkshellId, CancellationToken.None);

        var timing = map.For(spelling);
        Assert.Equal("Fafnir/Nidhogg", timing.MonsterName);
        Assert.Equal(7, timing.WindowCount);
        Assert.Equal(10, timing.WindowCadenceMinutes);
    }

    // An un-seeded linkshell must behave EXACTLY as the app did before this table existed — that is
    // what makes the deploy a no-op for everyone who never opens the editor.
    [Fact]
    public async Task UnseededLinkshell_FallsBackToTheBuiltInDefaults()
    {
        await using var db = NewInMemoryContext();
        var map = await new MonsterTimingResolver(db).GetMapAsync(LinkshellId, CancellationToken.None);

        Assert.False(map.IsSeeded);
        var cerberus = map.For("Cerberus");
        Assert.Equal(HnmConfig.ToauWindowCount, cerberus.WindowCount);
        Assert.Equal(HnmConfig.ToauWindowCadenceMinutes, cerberus.WindowCadenceMinutes);
        Assert.Equal(48 * 60, cerberus.CooldownMinutes);

        // ...and the dropdown still offers the built-in merged catalog rather than nothing.
        Assert.Equal(
            HnmConfig.CombinedMonsterOptions(TodManagerViewModel.SupportedMonsters),
            map.EventMonsterOptions);
    }

    // A free-text monster name nobody configured resolves rather than throwing.
    [Fact]
    public async Task UnknownMonster_ResolvesToItsBuiltInDefault()
    {
        await using var db = await SeededAsync();
        var map = await new MonsterTimingResolver(db).GetMapAsync(LinkshellId, CancellationToken.None);

        var timing = map.For("Some Unlisted Mob");
        Assert.Null(timing.WindowCount);
        Assert.Equal(22 * 60, timing.CooldownMinutes);
    }

    // A monster with no spawn grid still carries an interval for the ToD form, and must NOT report a
    // window grid — a stamped cadence on a grid-less camp would start auto-advancing a manual board.
    [Fact]
    public async Task NoGridMonster_HasAnIntervalButNoWindows()
    {
        await using var db = await SeededAsync();
        var map = await new MonsterTimingResolver(db).GetMapAsync(LinkshellId, CancellationToken.None);

        var serket = map.For("Serket");
        Assert.Null(serket.WindowCount);
        Assert.Null(serket.WindowCadenceMinutes);
        Assert.False(serket.HasSpawnGrid);
        Assert.Equal(10, serket.TodIntervalMinutes);
    }

    // ---- Legacy blob import ----

    // A timing saved under ONE HALF of a merged pair has to fold onto the combined row. Two rows for
    // one spawn would make every lookup depend on which the alias index saw first, and the unique
    // index cannot catch it — "Nidhogg" and "Fafnir/Nidhogg" are different strings.
    [Fact]
    public async Task LegacyBlob_PerHalfTiming_FoldsOntoTheMergedRow()
    {
        const string blob = """
            [{"MonsterName":"Nidhogg","CooldownHours":30,"IntervalHours":0,"IntervalMinutes":15}]
            """;
        await using var db = await SeededAsync(blob);
        var rows = await db.LinkshellMonsterTimings.ToListAsync();

        Assert.Single(rows, row => HnmConfig.MonsterMatchNames(row.MonsterName).Contains("Nidhogg"));
        var merged = rows.Single(row => row.MonsterName == "Fafnir/Nidhogg");
        Assert.Equal(30 * 60, merged.CooldownMinutes);
        Assert.Equal(15, merged.WindowCadenceMinutes);
    }

    // Both halves configured: the BASE half wins, deterministically, rather than whichever the JSON
    // happened to list last.
    [Fact]
    public async Task LegacyBlob_BothHalvesConfigured_BaseHalfWins()
    {
        const string blob = """
            [{"MonsterName":"Nidhogg","CooldownHours":30,"IntervalHours":0,"IntervalMinutes":15},
             {"MonsterName":"Fafnir","CooldownHours":25,"IntervalHours":0,"IntervalMinutes":5}]
            """;
        await using var db = await SeededAsync(blob);

        var merged = await db.LinkshellMonsterTimings.SingleAsync(row => row.MonsterName == "Fafnir/Nidhogg");
        Assert.Equal(25 * 60, merged.CooldownMinutes);
        Assert.Equal(5, merged.WindowCadenceMinutes);
    }

    // A monster the linkshell added itself survives the migration, and stays deletable.
    [Fact]
    public async Task LegacyBlob_CustomMonster_BecomesACustomRow()
    {
        const string blob = """
            [{"MonsterName":"Homebrew NM","CooldownHours":6,"IntervalHours":0,"IntervalMinutes":20,"Category":"Other NMs"}]
            """;
        await using var db = await SeededAsync(blob);

        var custom = await db.LinkshellMonsterTimings.SingleAsync(row => row.MonsterName == "Homebrew NM");
        Assert.True(custom.IsCustom);
        Assert.Equal(6 * 60, custom.CooldownMinutes);
        Assert.Equal(20, custom.WindowCadenceMinutes);
    }

    [Fact]
    public async Task LegacyBlob_Garbage_LeavesTheDefaultsIntact()
    {
        await using var db = await SeededAsync("not json at all");
        var rows = await db.LinkshellMonsterTimings.ToListAsync();

        Assert.Equal(MonsterTimingDefaults.BuildAll().Count, rows.Count);
        Assert.All(rows, row => Assert.False(row.IsCustom));
    }

    // ---- Editor ----

    private static MonsterTimingEditor NewEditor(ApplicationDbContext db) =>
        new(db, new LinkshellMonsterTimingProvisioner(db), new MonsterTimingResolver(db));

    private static MonsterTimingEdit EditFrom(LinkshellMonsterTiming row) =>
        new(row.Id, row.MonsterName, row.WindowCount,
            row.WindowCadenceMinutes, "mins",
            row.CooldownMinutes, "mins",
            row.Category);

    private static async Task<List<MonsterTimingEdit>> CurrentEditsAsync(ApplicationDbContext db) =>
        (await db.LinkshellMonsterTimings.OrderBy(r => r.SortOrder).ToListAsync())
            .Select(EditFrom).ToList();

    [Fact]
    public async Task Editor_SavesTheNumberAndUnitAsCanonicalMinutes()
    {
        await using var db = await SeededAsync();
        var edits = await CurrentEditsAsync(db);
        var cerberus = edits.Single(e => e.MonsterName == "Cerberus");
        edits[edits.IndexOf(cerberus)] = cerberus with { CooldownValue = 48, CooldownUnit = "hours" };

        Assert.Null(await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None));

        var saved = await db.LinkshellMonsterTimings.SingleAsync(r => r.MonsterName == "Cerberus");
        Assert.Equal(48 * 60, saved.CooldownMinutes);
    }

    // Two rows must never claim one spawn. The unique index can't catch this — the names differ —
    // so the editor has to.
    [Fact]
    public async Task Editor_RejectsTwoRowsForOneSpawn()
    {
        await using var db = await SeededAsync();
        var edits = await CurrentEditsAsync(db);
        edits.Add(new MonsterTimingEdit(null, "Nidhogg", 7, 10, "mins", 22, "hours", "HNMs"));

        var error = await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None);
        Assert.NotNull(error);
        Assert.Contains("same spawn", error);
    }

    // Pop-only mobs come from an item, not a repop timer, so they have no cooldown to configure.
    [Fact]
    public async Task Editor_RejectsAPopOnlyMonster()
    {
        await using var db = await SeededAsync();
        var edits = await CurrentEditsAsync(db);
        edits.Add(new MonsterTimingEdit(null, "Kirin", null, null, "mins", 5, "mins", "HNMs"));

        var error = await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None);
        Assert.NotNull(error);
        Assert.Contains("repop timer", error);
    }

    // A built-in is RESET, not removed — deleting it would only make it reappear on the next seed.
    [Fact]
    public async Task Editor_RefusesToDeleteABuiltIn()
    {
        await using var db = await SeededAsync();
        var edits = await CurrentEditsAsync(db);
        edits.RemoveAll(e => e.MonsterName == "Cerberus");

        var error = await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None);
        Assert.NotNull(error);
        Assert.Contains("built-in", error);
    }

    [Fact]
    public async Task Editor_AddsAndThenDeletesACustomMonster()
    {
        await using var db = await SeededAsync();
        var editor = NewEditor(db);

        var withCustom = await CurrentEditsAsync(db);
        withCustom.Add(new MonsterTimingEdit(null, "Homebrew NM", 5, 30, "mins", 6, "hours", "Other NMs"));
        Assert.Null(await editor.SaveAsync(LinkshellId, withCustom, CancellationToken.None));

        var custom = await db.LinkshellMonsterTimings.SingleAsync(r => r.MonsterName == "Homebrew NM");
        Assert.True(custom.IsCustom);
        Assert.Equal(5, custom.WindowCount);
        Assert.Equal(30, custom.WindowCadenceMinutes);
        Assert.Equal(6 * 60, custom.CooldownMinutes);

        var withoutCustom = (await CurrentEditsAsync(db)).Where(e => e.MonsterName != "Homebrew NM").ToList();
        Assert.Null(await editor.SaveAsync(LinkshellId, withoutCustom, CancellationToken.None));
        Assert.False(await db.LinkshellMonsterTimings.AnyAsync(r => r.MonsterName == "Homebrew NM"));
    }

    // MaxWindow is a hard ceiling everywhere downstream (the board, the addon's clamping), so a
    // bigger number would be silently truncated later rather than honoured.
    [Fact]
    public async Task Editor_ClampsWindowsToMaxWindow()
    {
        await using var db = await SeededAsync();
        var edits = await CurrentEditsAsync(db);
        var index = edits.FindIndex(e => e.MonsterName == "Cerberus");
        edits[index] = edits[index] with { Windows = 999 };

        Assert.Null(await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None));
        var saved = await db.LinkshellMonsterTimings.SingleAsync(r => r.MonsterName == "Cerberus");
        Assert.Equal(HnmConfig.MaxWindow, saved.WindowCount);
    }

    // Blank windows is a real answer — "this monster has no spawn cycle" — and must not become 1.
    [Fact]
    public async Task Editor_BlankWindows_StaysNull()
    {
        await using var db = await SeededAsync();
        var edits = await CurrentEditsAsync(db);
        var index = edits.FindIndex(e => e.MonsterName == "Cerberus");
        edits[index] = edits[index] with { Windows = null };

        Assert.Null(await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None));
        var saved = await db.LinkshellMonsterTimings.SingleAsync(r => r.MonsterName == "Cerberus");
        Assert.Null(saved.WindowCount);
    }

    // A cleared cooldown falls back to the built-in default, never to zero — a zero cooldown would
    // make RepopTime equal the time of death.
    [Fact]
    public async Task Editor_BlankCooldown_FallsBackToTheDefault()
    {
        await using var db = await SeededAsync();
        var edits = await CurrentEditsAsync(db);
        var index = edits.FindIndex(e => e.MonsterName == "Cerberus");
        edits[index] = edits[index] with { CooldownValue = null };

        Assert.Null(await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None));
        var saved = await db.LinkshellMonsterTimings.SingleAsync(r => r.MonsterName == "Cerberus");
        Assert.Equal(48 * 60, saved.CooldownMinutes);
    }
}
