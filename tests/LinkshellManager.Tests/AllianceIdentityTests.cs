using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.Tests;

// Alliance identity: who an alliance IS, rather than which number somebody typed for it.
//
// `/lsm alliance N` was a manual setting that defaulted to 1, and the FFXI client cannot see other
// alliances, so nothing could ever check the value. A linkshell where nobody ran the command
// reported every alliance as 1 and the whole per-alliance feature collapsed into a single row. The
// addon now reports the alliance LEADER's character name (or the poster's, when the game confirms
// no leader) and the number is derived from that here.
public class AllianceIdentityTests
{
    private const int Ls = 21;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static readonly DateTime Anchor = new(2026, 8, 29, 21, 0, 0, DateTimeKind.Utc);

    private static void AddFiled(ApplicationDbContext db, int windowEventId, string key, int number)
    {
        db.AttendanceSnapshots.Add(new AttendanceSnapshot
        {
            LinkshellId = Ls,
            WindowEventId = windowEventId,
            CapturedAtUtc = Anchor,
            CreatedAtUtc = Anchor,
            AllianceKey = key,
            AllianceNumber = number,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
        });
        db.SaveChanges();
    }

    // ---- normalization ----

    // Names arrive from party memory on several different clients, and only slot 0 is guaranteed to
    // match the game's exact casing. Two officers in one alliance must not land in separate rows
    // because one client reported "Millhouse" and another "MILLHOUSE".
    [Theory]
    [InlineData("Millhouse", "MILLHOUSE")]
    [InlineData("  Millhouse  ", "MILLHOUSE")]
    [InlineData("millhouse", "MILLHOUSE")]
    public void KeysNormalizeAcrossCasingAndPadding(string input, string expected)
        => Assert.Equal(expected, AllianceIdentityService.NormalizeKey(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankKeysNormalizeToNull(string? input)
        => Assert.Null(AllianceIdentityService.NormalizeKey(input));

    // ---- number derivation ----

    [Fact]
    public async Task TheFirstAllianceOnACampBecomesNumberOne()
    {
        using var db = NewDb();
        var number = await new AllianceIdentityService(db)
            .ResolveNumberAsync(500, "Millhouse", CancellationToken.None);

        Assert.Equal(1, number);
    }

    // THE case the whole rework exists for: a second officer in the SAME alliance gets the same
    // number without either of them having typed one.
    [Fact]
    public async Task TheSameAllianceKeepsItsNumber()
    {
        using var db = NewDb();
        AddFiled(db, 500, "Millhouse", 1);

        var number = await new AllianceIdentityService(db)
            .ResolveNumberAsync(500, "millhouse", CancellationToken.None);

        Assert.Equal(1, number);
    }

    [Fact]
    public async Task ADifferentAllianceGetsTheNextNumber()
    {
        using var db = NewDb();
        AddFiled(db, 500, "Millhouse", 1);

        var number = await new AllianceIdentityService(db)
            .ResolveNumberAsync(500, "Ramuh", CancellationToken.None);

        Assert.Equal(2, number);
    }

    // Numbers are per CAMP, not per linkshell. Alliance 1 at tonight's Fafnir has nothing to do with
    // alliance 1 at yesterday's Tiamat.
    [Fact]
    public async Task NumbersRestartOnADifferentCamp()
    {
        using var db = NewDb();
        AddFiled(db, 500, "Millhouse", 1);
        AddFiled(db, 500, "Ramuh", 2);

        var number = await new AllianceIdentityService(db)
            .ResolveNumberAsync(501, "Ramuh", CancellationToken.None);

        Assert.Equal(1, number);
    }

    // Counting distinct keys instead of taking the highest would reuse a number after a capture was
    // deleted, silently merging two alliances in the display.
    [Fact]
    public async Task ADeletedAllianceDoesNotFreeItsNumberForReuse()
    {
        using var db = NewDb();
        AddFiled(db, 500, "Millhouse", 1);
        AddFiled(db, 500, "Ramuh", 2);
        db.AttendanceSnapshots.Remove(db.AttendanceSnapshots.First(s => s.AllianceKey == "Millhouse"));
        db.SaveChanges();

        var number = await new AllianceIdentityService(db)
            .ResolveNumberAsync(500, "Sylph", CancellationToken.None);

        Assert.Equal(3, number);
    }

    // Past the sixth alliance there is no colour, column or chip to render, so the overflow shares
    // the last number rather than inventing one the UI cannot show.
    [Fact]
    public async Task NumbersAreCappedAtTheRenderableMaximum()
    {
        using var db = NewDb();
        AddFiled(db, 500, "Six", AttendanceSnapshotAlliances.MaxAllianceNumber);

        var number = await new AllianceIdentityService(db)
            .ResolveNumberAsync(500, "Seventh", CancellationToken.None);

        Assert.Equal(AttendanceSnapshotAlliances.MaxAllianceNumber, number);
    }

    // ---- labelling ----

    // A number is an ordinal an officer has to decode; a name is the answer they wanted.
    [Fact]
    public void AConfirmedLeaderNamesTheAlliance()
        => Assert.Equal("Millhouse's alliance",
            AttendanceSnapshotAlliances.Label(2, "Ramuh", "Millhouse"));

    [Fact]
    public void WithoutALeaderTheKeyNamesTheAlliance()
        => Assert.Equal("Ramuh's alliance",
            AttendanceSnapshotAlliances.Label(2, "Ramuh", null));

    // Legacy rows have neither, and fall back to exactly what they always showed.
    [Fact]
    public void WithoutAnIdentityTheNumberStillLabelsIt()
        => Assert.Equal("Alliance 2", AttendanceSnapshotAlliances.Label(2, null, null));

    [Fact]
    public void WithNothingAtAllItReadsUnassigned()
        => Assert.Equal("Unassigned", AttendanceSnapshotAlliances.Label(null, null, null));

    // ---- presence window ----

    // 2.5 heartbeats at the addon's 60s cadence: one dropped beat must not blink a whole alliance
    // off somebody's Lobby, two consecutive ones should.
    [Fact]
    public void ThePresenceWindowSpansMoreThanOneMissedHeartbeat()
    {
        Assert.True(LinkshellPresenceWindow.FreshSeconds > 120);
        Assert.True(LinkshellPresenceWindow.FreshSeconds < 180);
        // Party memory cannot report a nineteenth person.
        Assert.Equal(18, LinkshellPresenceWindow.MaxMembers);
    }
}
