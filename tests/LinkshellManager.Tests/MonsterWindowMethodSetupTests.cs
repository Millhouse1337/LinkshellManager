using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The Monster Setups "Window Method" column: its built-in defaults, and the save contract it shares
// with AdditionalSeconds (a full-replace save where null means "leave it alone").
//
// Stored as null whenever the choice equals the default, so a built-in row keeps following the
// default rather than freezing it into the table.
public class MonsterWindowMethodSetupTests
{
    private const int LinkshellId = 42;
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

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

    private static Task<List<LinkshellMonsterTiming>> RowsAsync(ApplicationDbContext db) =>
        db.LinkshellMonsterTimings
            .Where(row => row.LinkshellId == LinkshellId)
            .OrderBy(row => row.SortOrder)
            .ToListAsync();

    private static MonsterTimingEdit EditFor(LinkshellMonsterTiming row, string? windowMethod, int? windows = -1) =>
        new(
            row.Id,
            row.MonsterName,
            windows == -1 ? row.WindowCount : windows,
            row.WindowCadenceMinutes,
            "mins",
            row.CooldownMinutes,
            "mins",
            row.Category,
            WindowMethod: windowMethod);

    // Every seeded row round-tripped, with `pick` choosing each row's window method (null = omit).
    private static async Task<string?> SaveAllAsync(
        ApplicationDbContext db, Func<LinkshellMonsterTiming, string?> pick, params MonsterTimingEdit[] extra)
    {
        var edits = (await RowsAsync(db)).Select(row => EditFor(row, pick(row))).Concat(extra).ToList();
        return await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None);
    }

    private static async Task<LinkshellMonsterTiming> RowAsync(ApplicationDbContext db, string startsWith) =>
        (await RowsAsync(db)).First(row => row.NormalizedMonsterName.StartsWith(startsWith));

    [Theory]
    [InlineData("Tiamat", MonsterWindowMethods.WindowN)]
    [InlineData("Jormungand", MonsterWindowMethods.WindowN)]
    [InlineData("Vrtra", MonsterWindowMethods.WindowN)]
    [InlineData("Cerberus", MonsterWindowMethods.WindowN)]
    [InlineData("Hydra", MonsterWindowMethods.WindowN)]
    [InlineData("Khimaira", MonsterWindowMethods.WindowN)]
    [InlineData("Fafnir/Nidhogg", MonsterWindowMethods.OpenClose)]
    [InlineData("Behemoth/King Behemoth", MonsterWindowMethods.OpenClose)]
    [InlineData("Adamantoise/Aspidochelone", MonsterWindowMethods.OpenClose)]
    [InlineData("Bune", MonsterWindowMethods.OpenClose)]
    [InlineData("Shikigami Weapon", MonsterWindowMethods.OpenClose)]
    [InlineData("Jugner Funguar", MonsterWindowMethods.OpenClose)]
    [InlineData(null, MonsterWindowMethods.OpenClose)]
    public void Default_IsWindowNOnlyForTheLongWindowMonsters(string? monster, string expected) =>
        Assert.Equal(expected, MonsterTimingDefaults.DefaultWindowMethod(monster));

    [Fact]
    public async Task SeededRows_StoreNull_AndResolveToTheirDefault()
    {
        await using var db = await SeededAsync();
        var rows = await RowsAsync(db);

        Assert.All(rows, row => Assert.Null(row.WindowMethod));

        var map = new MonsterTimingMap(LinkshellId, rows);
        Assert.Equal(MonsterWindowMethods.WindowN, map.For("Tiamat").WindowMethod);
        Assert.Equal(MonsterWindowMethods.OpenClose, map.For("Nidhogg").WindowMethod);
        // An unseeded name falls through to the built-in default too.
        Assert.Equal(MonsterWindowMethods.OpenClose, map.For("Jugner Funguar").WindowMethod);
    }

    [Fact]
    public async Task SaveWithoutTheField_LeavesEveryRowAlone()
    {
        await using var db = await SeededAsync();
        var fafnir = await RowAsync(db, "fafnir");
        fafnir.WindowMethod = MonsterWindowMethods.WindowN;
        await db.SaveChangesAsync();

        Assert.Null(await SaveAllAsync(db, _ => null));

        Assert.Equal(MonsterWindowMethods.WindowN, (await RowAsync(db, "fafnir")).WindowMethod);
    }

    [Fact]
    public async Task ChoosingTheNonDefault_IsStored_AndChoosingTheDefaultStoresNull()
    {
        await using var db = await SeededAsync();

        Assert.Null(await SaveAllAsync(db, row =>
            row.NormalizedMonsterName.StartsWith("fafnir") ? MonsterWindowMethods.WindowN
            : row.NormalizedMonsterName == "tiamat" ? MonsterWindowMethods.OpenClose
            : null));
        Assert.Equal(MonsterWindowMethods.WindowN, (await RowAsync(db, "fafnir")).WindowMethod);
        Assert.Equal(MonsterWindowMethods.OpenClose, (await RowAsync(db, "tiamat")).WindowMethod);

        // Back to the defaults: both rows return to null.
        Assert.Null(await SaveAllAsync(db, row =>
            row.NormalizedMonsterName.StartsWith("fafnir") ? MonsterWindowMethods.OpenClose
            : row.NormalizedMonsterName == "tiamat" ? MonsterWindowMethods.WindowN
            : null));
        Assert.Null((await RowAsync(db, "fafnir")).WindowMethod);
        Assert.Null((await RowAsync(db, "tiamat")).WindowMethod);
    }

    [Fact]
    public async Task UnknownValue_IsRejected()
    {
        await using var db = await SeededAsync();
        var error = await SaveAllAsync(db, row => row.NormalizedMonsterName == "tiamat" ? "Sometimes" : null);
        Assert.NotNull(error);
        Assert.Contains("Tiamat", error);
    }

    [Fact]
    public async Task WindowN_WithOneWindow_IsRejected_WhetherTheMethodOrTheWindowsChanged()
    {
        await using var db = await SeededAsync();
        var custom = new MonsterTimingEdit(
            null, "Jugner Funguar", 1, 10, "mins", 60, "mins", MonsterTimingDefaults.OtherCategory,
            WindowMethod: MonsterWindowMethods.WindowN);
        Assert.NotNull(await SaveAllAsync(db, _ => null, custom));

        // Tiamat already runs Window N by default; editing its Windows down to 1 is the same mistake.
        var tiamat = await RowAsync(db, "tiamat");
        var edits = (await RowsAsync(db))
            .Select(row => row.Id == tiamat.Id ? EditFor(row, null, windows: 1) : EditFor(row, null))
            .ToList();
        Assert.NotNull(await NewEditor(db).SaveAsync(LinkshellId, edits, CancellationToken.None));
    }

    [Fact]
    public async Task CustomRow_WithBlankWindows_MayRunWindowN()
    {
        await using var db = await SeededAsync();
        var custom = new MonsterTimingEdit(
            null, "King Vinegarroon", null, null, "mins", 60, "mins", MonsterTimingDefaults.OtherCategory,
            WindowMethod: MonsterWindowMethods.WindowN);

        Assert.Null(await SaveAllAsync(db, _ => null, custom));
        Assert.Equal(MonsterWindowMethods.WindowN, (await RowAsync(db, "king vinegarroon")).WindowMethod);
    }

    [Fact]
    public void EditorDto_CarriesTheResolvedMethodAndTheDefault()
    {
        var row = new LinkshellMonsterTiming
        {
            MonsterName = "Fafnir/Nidhogg",
            WindowCount = 7,
            WindowCadenceMinutes = 10,
            CooldownMinutes = 22 * 60,
            Category = MonsterTimingDefaults.HnmCategory,
            WindowMethod = MonsterWindowMethods.WindowN,
        };

        var dto = ActivityDataController.ToMonsterTimingDto(row);

        Assert.Equal(MonsterWindowMethods.WindowN, dto.WindowMethod);
        Assert.Equal(MonsterWindowMethods.OpenClose, dto.DefaultWindowMethod);
    }
}
