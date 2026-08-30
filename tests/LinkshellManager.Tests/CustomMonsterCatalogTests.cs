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

// A CUSTOM monster is one a linkshell added itself, and it has to be campable — pickable when
// creating an event, assignable to a party setup, narrowable on a Discord channel route — not just
// a row with a ToD cooldown on it.
//
// It used not to be. Every one of those surfaces validated against the compile-time
// TodManagerViewModel.SupportedMonsters, so "+ Add monster" produced something you could time and
// then not use, which is not what the button says. The per-linkshell answer is
// MonsterTimingMap.EventMonsterOptions / .Allows, and these tests pin that a custom monster is
// indistinguishable from a built-in one everywhere it matters.
public class CustomMonsterCatalogTests
{
    private const int LinkshellId = 42;
    private const int OtherLinkshellId = 43;
    private static readonly DateTime Now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> SeededAsync(params string[] customNames)
    {
        var db = NewInMemoryContext();
        db.LinkshellMonsterTimings.AddRange(
            LinkshellMonsterTimingProvisioner.BuildSeed(LinkshellId, null, Now));
        foreach (var name in customNames)
        {
            db.LinkshellMonsterTimings.Add(new LinkshellMonsterTiming
            {
                LinkshellId = LinkshellId,
                MonsterName = name,
                NormalizedMonsterName = name.ToLowerInvariant(),
                CooldownMinutes = 22 * 60,
                Category = MonsterTimingDefaults.OtherCategory,
                IsCustom = true,
            });
        }
        await db.SaveChangesAsync();
        return db;
    }

    // ---- the eleven NMs that left the built-in catalog ----

    [Fact]
    public void RetiredNms_AreGoneFromTheBuiltInCatalog()
    {
        foreach (var name in RemoveRetiredNmMonsterTimings.RetiredNms
                     .Concat(RetireGroundNmMonsterTimings.RetiredGroundNms))
        {
            Assert.DoesNotContain(name, TodManagerViewModel.SupportedMonsters, StringComparer.OrdinalIgnoreCase);
        }
    }

    // The migration deletes rows by name. A name left in BOTH lists would delete a row the seeder
    // immediately writes back, so the two can never overlap.
    [Fact]
    public void RetiredNms_AndTheCatalog_DoNotOverlap()
    {
        var catalog = TodManagerViewModel.SupportedMonsters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(RemoveRetiredNmMonsterTimings.RetiredNms, catalog.Contains);
        Assert.DoesNotContain(RetireGroundNmMonsterTimings.RetiredGroundNms, catalog.Contains);
    }

    // A retired NM must not come back on ANY linkshell, and the two ways it could are: a fresh
    // seed still listing it, or the provisioner "topping up" a linkshell that already has rows.
    // The migration deletes the existing rows (for every linkshell — its DELETE has no LinkshellId
    // filter); these two pin the code side, which is what would silently undo it.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(999)]
    public void FreshSeed_ForAnyLinkshell_ExcludesRetiredNms(int linkshellId)
    {
        var seeded = LinkshellMonsterTimingProvisioner
            .BuildSeed(linkshellId, null, Now)
            .Select(row => row.MonsterName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in RemoveRetiredNmMonsterTimings.RetiredNms
                     .Concat(RetireGroundNmMonsterTimings.RetiredGroundNms))
        {
            Assert.DoesNotContain(name, seeded);
        }
        // ...and the seed is not simply empty: the twelve HNMs stay built in, merged into nine rows.
        Assert.Contains("Tiamat", seeded);
        Assert.Contains("Fafnir/Nidhogg", seeded);
    }

    // The seeded catalog is HNMs and nothing else, so the editor's "Other NMs" heading starts empty
    // on every linkshell and is filled by "+ Add monster". This is the assertion that fails if a
    // built-in NM is ever quietly reintroduced.
    [Fact]
    public void FreshSeed_HasNoOtherNms()
    {
        var seeded = LinkshellMonsterTimingProvisioner.BuildSeed(LinkshellId, null, Now);

        Assert.NotEmpty(seeded);
        Assert.All(seeded, row => Assert.Equal(MonsterTimingDefaults.HnmCategory, row.Category));
    }

    // The provisioner seeds ONLY a linkshell with no rows at all. If it ever grew a "add any
    // built-in this linkshell is missing" pass, it would re-create all eight the migration just
    // deleted, on every linkshell, the first time anyone opened the editor.
    [Fact]
    public async Task AlreadySeededLinkshell_IsNotToppedUp()
    {
        await using var db = await SeededAsync();
        var before = await db.LinkshellMonsterTimings.CountAsync(r => r.LinkshellId == LinkshellId);

        // Simulate the migration having removed a built-in row.
        var victim = await db.LinkshellMonsterTimings
            .FirstAsync(r => r.LinkshellId == LinkshellId && r.NormalizedMonsterName == "tiamat");
        db.LinkshellMonsterTimings.Remove(victim);
        await db.SaveChangesAsync();

        var rows = await new LinkshellMonsterTimingProvisioner(db)
            .EnsureSeededAsync(LinkshellId, CancellationToken.None);

        Assert.Equal(before - 1, rows.Count);
        Assert.DoesNotContain("Tiamat", rows.Select(r => r.MonsterName), StringComparer.OrdinalIgnoreCase);
    }

    // RemoveLegacySkyNmCustomRows deletes these eight by NAME, ignoring IsCustom, because the legacy
    // blob importer kept resurrecting them wearing that flag. That only stays safe while none of
    // them is in the built-in catalog — otherwise the seeder would rewrite the rows the migration
    // just deleted, on every linkshell, forever.
    [Fact]
    public void SkyFarmNms_AreNotInTheBuiltInCatalog()
    {
        foreach (var name in RemoveSkyNmMonsterTimings.SkyFarmNms)
        {
            Assert.DoesNotContain(name, TodManagerViewModel.SupportedMonsters, StringComparer.OrdinalIgnoreCase);
        }
        Assert.Equal(8, RemoveSkyNmMonsterTimings.SkyFarmNms.Length);
    }

    // These three outlived the first cut and were the last hardcoded "Other NMs"; RetireGroundNmMonsterTimings
    // removed them, so the heading is now filled entirely by whatever a linkshell adds itself. Kept
    // as a named test because it is the exact thing a well-meaning "the NM list looks empty" edit
    // would put back.
    [Theory]
    [InlineData("Bloodsucker")]
    [InlineData("King Arthro")]
    [InlineData("King Vinegarroon")]
    public void RetiredGroundNms_AreNotBuiltIn(string name)
        => Assert.DoesNotContain(name, TodManagerViewModel.SupportedMonsters, StringComparer.OrdinalIgnoreCase);

    // ...but the app still KNOWS them. A linkshell that adds Bloodsucker back, or an addon that
    // posts a free-text ToD for it, must land on its real 71-hour band and not the 22h catch-all.
    [Fact]
    public void RetiredGroundNm_KeepsItsBuiltInCooldown()
        => Assert.Equal(71 * 60, MonsterTimingDefaults.DefaultCooldownMinutes("Bloodsucker"));

    // ---- a custom monster is campable ----

    [Fact]
    public async Task CustomMonster_AppearsInTheEventPicker()
    {
        await using var db = await SeededAsync("Ouryu");
        var map = await new MonsterTimingResolver(db).GetMapAsync(LinkshellId, CancellationToken.None);

        Assert.Contains("Ouryu", map.EventMonsterOptions, StringComparer.OrdinalIgnoreCase);
        // ...alongside the built-ins, not instead of them. The picker's label for a merge pair is
        // the COMBINED one, because that is how the row is stored — see LinkshellMonsterTiming.
        Assert.Contains("Fafnir/Nidhogg", map.EventMonsterOptions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomMonster_IsAssignable()
    {
        await using var db = await SeededAsync("Ouryu");
        var map = await new MonsterTimingResolver(db).GetMapAsync(LinkshellId, CancellationToken.None);

        Assert.True(map.Allows("Ouryu"));
        Assert.True(map.Allows("ouryu"));      // case-insensitive, like every other name lookup
        Assert.True(map.Allows("  Ouryu  "));  // and whitespace-tolerant, since it arrives from a form
    }

    // Blank is "unassigned", which is a valid state — rejecting it would make every party setup
    // without a monster unsavable.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankMonster_IsAlwaysAllowed(string? monster)
    {
        await using var db = await SeededAsync();
        var map = await new MonsterTimingResolver(db).GetMapAsync(LinkshellId, CancellationToken.None);
        Assert.True(map.Allows(monster));
    }

    [Fact]
    public async Task AMonsterNobodyConfigured_IsStillRejected()
    {
        await using var db = await SeededAsync("Ouryu");
        var map = await new MonsterTimingResolver(db).GetMapAsync(LinkshellId, CancellationToken.None);

        // Retired from the catalog and not re-added here: the point of the change is that this is
        // now a per-linkshell answer, not that everything is allowed.
        Assert.False(map.Allows("Simurgh"));
        Assert.False(map.Allows("Some Mob That Does Not Exist"));
    }

    // The catalog is per LINKSHELL. One linkshell's custom monster must not leak into another's
    // picker — the rows are scoped, and this is the test that keeps them that way.
    [Fact]
    public async Task CustomMonsters_DoNotLeakBetweenLinkshells()
    {
        await using var db = await SeededAsync("Ouryu");
        db.LinkshellMonsterTimings.AddRange(
            LinkshellMonsterTimingProvisioner.BuildSeed(OtherLinkshellId, null, Now));
        await db.SaveChangesAsync();

        var other = await new MonsterTimingResolver(db).GetMapAsync(OtherLinkshellId, CancellationToken.None);

        Assert.DoesNotContain("Ouryu", other.EventMonsterOptions, StringComparer.OrdinalIgnoreCase);
        Assert.False(other.Allows("Ouryu"));
        // Its own built-ins are unaffected, by either half of a merge pair.
        Assert.True(other.Allows("Fafnir"));
        Assert.True(other.Allows("Nidhogg"));
    }

    // A custom monster with no timings of its own still resolves — MonsterTimingDefaults.Build
    // answers for anything, so adding a name can't produce a row that breaks the ToD form.
    [Fact]
    public async Task CustomMonster_ResolvesTimings()
    {
        await using var db = await SeededAsync("Ouryu");
        var map = await new MonsterTimingResolver(db).GetMapAsync(LinkshellId, CancellationToken.None);

        var timing = map.For("Ouryu");
        Assert.Equal(22 * 60, timing.CooldownMinutes);
    }

    // An unseeded linkshell has no rows at all, so the fallback has to offer the built-ins —
    // otherwise a brand-new linkshell gets an empty create-event picker.
    [Fact]
    public async Task UnseededLinkshell_StillAllowsTheBuiltIns()
    {
        await using var db = NewInMemoryContext();
        var map = await new MonsterTimingResolver(db).GetMapAsync(LinkshellId, CancellationToken.None);

        Assert.False(map.IsSeeded);
        Assert.True(map.Allows("Fafnir"));
        // Not a built-in any more, and an unseeded linkshell has no rows of its own to allow it —
        // it becomes assignable the moment someone adds it under Monster setups.
        Assert.False(map.Allows("Bloodsucker"));
        Assert.False(map.Allows("Ouryu"));
    }
}
