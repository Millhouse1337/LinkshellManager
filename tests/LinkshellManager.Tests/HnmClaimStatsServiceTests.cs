using System;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The dashboard "HNM Claims" donut.
//
// Both surfaces used to count this off whatever ToD rows they were already holding for other
// cards — the Activity off the overview's 25 most recent ToDs of ANY monster, the web off its
// 200-row Recent Activity page — so the chart showed a tail of the real history and the "All"
// toggle charted the same tail as the others. These pin the properties that made it wrong:
// every claim counted, merge-pair halves counted as ONE monster, and no top-N truncation.
public class HnmClaimStatsServiceTests
{
    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Tod Claim(int id, int linkshellId, string monster, DateTime when, bool? claim = true, bool hq = false) => new()
    {
        Id = id,
        LinkshellId = linkshellId,
        MonsterName = monster,
        Time = when,
        Claim = claim,
        Hq = hq,
    };

    [Fact]
    public async Task Counts_EveryClaim_NotJustARecentTail()
    {
        using var db = NewInMemoryContext();
        var now = DateTime.UtcNow;

        // 40 old HNM claims, then 30 recent NON-HNM pops on top of them. Anything reading a
        // 25-row "most recent ToDs" tail sees only the Sky pops and charts nothing.
        for (var i = 0; i < 40; i++)
        {
            db.Tods.Add(Claim(i + 1, 10, "Tiamat", now.AddDays(-200 - i)));
        }
        for (var i = 0; i < 30; i++)
        {
            db.Tods.Add(Claim(1000 + i, 10, "Genbu", now.AddMinutes(-i)));
        }
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        var tiamat = Assert.Single(stats.AllTime);
        Assert.Equal("Tiamat", tiamat.MonsterName);
        Assert.Equal(40, tiamat.Count);
        Assert.Equal(100, tiamat.Percent);

        // Sky farm pops are claimed ToDs too, and must never reach the HNM donut.
        Assert.DoesNotContain(stats.AllTime, slice => slice.MonsterName == "Genbu");
    }

    // A merge pair charts as TWO slices: which half popped is the whole point of a wyrm camp, and
    // folding them into one entry hid it.
    //
    // Three spellings, three sources of the answer. The combined label is what a board writes
    // whichever half showed up, so only its Hq flag can say. A bare "King Behemoth" is an HQ kill
    // by name — that is how every row written before the toggle existed reads. And a bare
    // "Behemoth" with the flag off is the NQ.
    [Fact]
    public async Task MergePairHalves_ChartSeparatelyByNqAndHq()
    {
        using var db = NewInMemoryContext();
        var now = DateTime.UtcNow;
        db.Tods.Add(Claim(1, 10, "Behemoth", now.AddDays(-1)));
        db.Tods.Add(Claim(2, 10, "Behemoth/King Behemoth", now.AddDays(-2), hq: true));
        db.Tods.Add(Claim(3, 10, "King Behemoth", now.AddDays(-3)));
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        var nq = Assert.Single(stats.AllTime, slice => slice.MonsterName == "Behemoth");
        var hq = Assert.Single(stats.AllTime, slice => slice.MonsterName == "King Behemoth");
        Assert.Equal(1, nq.Count);
        Assert.Equal(2, hq.Count);

        Assert.False(nq.IsHq);
        Assert.True(hq.IsHq);
        // Both are a merge pair, so both are badged NQ / HQ on the two surfaces.
        Assert.True(nq.HasHqVariant);
        Assert.True(hq.HasHqVariant);
        // ...and both take the FAMILY's colour, which is what makes the two arcs read as one
        // monster's two outcomes rather than two unrelated monsters.
        Assert.Equal(nq.ColorClass, hq.ColorClass);
    }

    // The HQ toggle is the answer for a board-logged ToD, whose name is the combined label either
    // way. Without it every board camp charted as the NQ.
    [Fact]
    public async Task CombinedLabel_SplitsOnTheHqFlag()
    {
        using var db = NewInMemoryContext();
        var now = DateTime.UtcNow;
        db.Tods.Add(Claim(1, 10, "Fafnir/Nidhogg", now.AddDays(-1), hq: true));
        db.Tods.Add(Claim(2, 10, "Fafnir/Nidhogg", now.AddDays(-2)));
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        Assert.Equal(
            new[] { "Fafnir", "Nidhogg" },
            stats.AllTime.Select(slice => slice.MonsterName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(50, stats.AllTime.Single(slice => slice.MonsterName == "Nidhogg").Percent);
    }

    // A monster with no stronger half is never badged and never shaded — NQ/HQ is not a question
    // it has, and the HQ flag on such a row means nothing.
    [Fact]
    public async Task MonstersWithoutAnHqVariant_AreNeverMarkedHq()
    {
        using var db = NewInMemoryContext();
        var now = DateTime.UtcNow;
        db.Tods.Add(Claim(1, 10, "Tiamat", now.AddDays(-1), hq: true));
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        var slice = Assert.Single(stats.AllTime);
        Assert.Equal("Tiamat", slice.MonsterName);
        Assert.False(slice.HasHqVariant);
        Assert.False(slice.IsHq);
    }

    // A pair's two halves are always neighbours in the ring, busiest family first. Ordering the
    // halves by their own counts scattered them — and the shared hue with the HQ lightened only
    // reads as "one monster, two outcomes" when the two arcs are side by side.
    [Fact]
    public async Task PairHalves_SitTogether_NqBeforeHq()
    {
        using var db = NewInMemoryContext();
        var now = DateTime.UtcNow;
        var id = 1;
        // Fafnir family: 5 total. Behemoth family: 4. Tiamat: 3 — between the two halves of each
        // pair if the slices were ordered by their own counts.
        for (var i = 0; i < 2; i++) db.Tods.Add(Claim(id++, 10, "Fafnir", now.AddDays(-1)));
        for (var i = 0; i < 3; i++) db.Tods.Add(Claim(id++, 10, "Nidhogg", now.AddDays(-1)));
        for (var i = 0; i < 3; i++) db.Tods.Add(Claim(id++, 10, "Tiamat", now.AddDays(-1)));
        for (var i = 0; i < 1; i++) db.Tods.Add(Claim(id++, 10, "Behemoth", now.AddDays(-1)));
        for (var i = 0; i < 3; i++) db.Tods.Add(Claim(id++, 10, "King Behemoth", now.AddDays(-1)));
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        Assert.Equal(
            new[] { "Fafnir", "Nidhogg", "Behemoth", "King Behemoth", "Tiamat" },
            stats.AllTime.Select(slice => slice.MonsterName));
    }

    // Colour is fixed per family, so a monster keeps it across the 7d / 30d / All toggle. It used
    // to be handed out in each window's count order, which repainted the entire chart whenever the
    // ranking moved.
    [Fact]
    public async Task MonsterColour_IsStableAcrossWindows()
    {
        using var db = NewInMemoryContext();
        var now = DateTime.UtcNow;
        // Vrtra leads the 7-day window; Tiamat leads all-time.
        db.Tods.Add(Claim(1, 10, "Vrtra", now.AddDays(-1)));
        db.Tods.Add(Claim(2, 10, "Tiamat", now.AddDays(-2)));
        db.Tods.Add(Claim(3, 10, "Tiamat", now.AddDays(-100)));
        db.Tods.Add(Claim(4, 10, "Tiamat", now.AddDays(-200)));
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        string ColorOf(IReadOnlyList<HnmClaimSlice> window, string monster) =>
            window.Single(slice => slice.MonsterName == monster).ColorClass;

        Assert.Equal(ColorOf(stats.AllTime, "Tiamat"), ColorOf(stats.Last7Days, "Tiamat"));
        Assert.Equal(ColorOf(stats.AllTime, "Vrtra"), ColorOf(stats.Last7Days, "Vrtra"));
        Assert.NotEqual(ColorOf(stats.AllTime, "Tiamat"), ColorOf(stats.AllTime, "Vrtra"));
    }

    // The full chart roster, and the whole of it: the 12 built-in HNMs as 12 entries now that each
    // NQ/HQ family splits into the half that popped. (It charted 9 before, and 6 before that — the
    // palette was 6 colours long and the aggregation truncated to a top 6, so a linkshell camping
    // the full list lost the tail of the legend and the ring never closed.)
    [Fact]
    public async Task ChartsExactlyTheTwelveHnms()
    {
        using var db = NewInMemoryContext();
        var now = DateTime.UtcNow;
        // Logged under a mix of spellings — bare halves, stronger halves, combined labels — the
        // way boards and manual ToDs actually record them. The two combined labels carry the HQ
        // flag, which is the only thing that can place them.
        var logged = new (string Monster, bool Hq)[]
        {
            ("Adamantoise", false), ("Aspidochelone/Adamantoise", true), ("Behemoth", false),
            ("King Behemoth", false), ("Fafnir/Nidhogg", true), ("Fafnir", false),
            ("Cerberus", false), ("Hydra", false), ("Jormungand", false), ("Khimaira", false),
            ("Tiamat", false), ("Vrtra", false)
        };
        var id = 1;
        foreach (var (monster, hq) in logged)
        {
            db.Tods.Add(Claim(id++, 10, monster, now.AddDays(-1), hq: hq));
        }
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        Assert.Equal(
            new[]
            {
                "Adamantoise", "Aspidochelone", "Behemoth", "Cerberus", "Fafnir", "Hydra",
                "Jormungand", "Khimaira", "King Behemoth", "Nidhogg", "Tiamat", "Vrtra"
            },
            stats.AllTime.Select(slice => slice.MonsterName).OrderBy(name => name, StringComparer.Ordinal));

        // Accounts for the whole ring. Truncating to a top 6 landed here at ~50%, which is what
        // left the donut visibly open.
        Assert.Equal(100, stats.AllTime.Sum(slice => slice.Percent), 0);
        // Nine hues for twelve slices, because each pair's two halves share their family's colour
        // and are separated by the HQ shading instead.
        Assert.Equal(9, stats.AllTime.Select(slice => slice.ColorClass).Distinct().Count());
        Assert.Equal(3, stats.AllTime.Count(slice => slice.IsHq));
        Assert.Equal(6, stats.AllTime.Count(slice => slice.HasHqVariant));
    }

    [Fact]
    public async Task WindowsFilterByDate_AndPercentIsRelativeToItsOwnWindow()
    {
        using var db = NewInMemoryContext();
        var now = DateTime.UtcNow;
        db.Tods.Add(Claim(1, 10, "Tiamat", now.AddDays(-2)));    // in 7d, 30d, all
        db.Tods.Add(Claim(2, 10, "Vrtra", now.AddDays(-20)));    // in 30d, all
        db.Tods.Add(Claim(3, 10, "Vrtra", now.AddDays(-400)));   // all only
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        Assert.Equal(1, stats.Last7Days.Sum(slice => slice.Count));
        Assert.Equal(2, stats.Last30Days.Sum(slice => slice.Count));
        Assert.Equal(3, stats.AllTime.Sum(slice => slice.Count));
        Assert.Equal(100, Assert.Single(stats.Last7Days).Percent);
    }

    [Fact]
    public async Task IgnoresUnclaimedRows_AndOtherLinkshells()
    {
        using var db = NewInMemoryContext();
        var now = DateTime.UtcNow;
        db.Tods.Add(Claim(1, 10, "Tiamat", now.AddDays(-1)));
        db.Tods.Add(Claim(2, 10, "Tiamat", now.AddDays(-1), claim: false));  // someone else took it
        db.Tods.Add(Claim(3, 10, "Tiamat", now.AddDays(-1), claim: null));   // never answered
        db.Tods.Add(Claim(4, 99, "Tiamat", now.AddDays(-1)));                // another linkshell
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        Assert.Equal(1, Assert.Single(stats.AllTime).Count);
    }

    // A ToD with no Time and no TimeStamp can't be placed in a dated window. It still happened,
    // so it counts toward All — but it must not be silently dated into 7d/30d.
    [Fact]
    public async Task UndatedClaim_CountsTowardAllTimeOnly()
    {
        using var db = NewInMemoryContext();
        db.Tods.Add(new Tod { Id = 1, LinkshellId = 10, MonsterName = "Vrtra", Claim = true });
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        Assert.Empty(stats.Last7Days);
        Assert.Empty(stats.Last30Days);
        Assert.Equal(1, Assert.Single(stats.AllTime).Count);
    }

    // The chart is a CLOSED roster: the 12 built-in HNMs, collapsed to 9 entries. Nothing else
    // charts, however a linkshell has categorised it in Monster setups — not a custom monster,
    // not Bahamut (absent from HnmConfig entirely), and not the timed NMs that share the kings'
    // spawn band but are NMs by tier.
    [Theory]
    [InlineData("Bahamut")]
    [InlineData("Capricious Cassie")]
    [InlineData("Bune")]
    [InlineData("Boroka")]
    [InlineData("Roc")]
    [InlineData("Lord of Onzozo")]
    [InlineData("Genbu")]
    [InlineData("Kirin")]
    [InlineData("Absolute Virtue")]
    public async Task NonHnmTierMonsters_NeverChart(string monster)
    {
        using var db = NewInMemoryContext();
        db.Tods.Add(Claim(1, 10, monster, DateTime.UtcNow.AddDays(-1)));
        // A row filed under the "HNMs" heading must not widen the chart either.
        db.LinkshellMonsterTimings.Add(new LinkshellMonsterTiming
        {
            Id = 1,
            LinkshellId = 10,
            MonsterName = monster,
            NormalizedMonsterName = monster.ToLowerInvariant(),
            Category = MonsterTimingDefaults.HnmCategory,
            IsCustom = true,
        });
        await db.SaveChangesAsync();

        var stats = await new HnmClaimStatsService(db).BuildAsync(10);

        Assert.Empty(stats.AllTime);
    }

    [Fact]
    public async Task NoLinkshell_IsEmpty()
    {
        using var db = NewInMemoryContext();
        var stats = await new HnmClaimStatsService(db).BuildAsync(null);
        Assert.Empty(stats.AllTime);
    }
}
