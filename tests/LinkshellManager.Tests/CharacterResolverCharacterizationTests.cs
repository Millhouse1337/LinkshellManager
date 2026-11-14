using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// "Which member is this character?" was answered by a separate hand-written loop in every place
// that needed it. These tests pin what each of those places answered for the plain cases -- the
// roster name, either alt, any capitals, stray spaces, a stranger, a blank -- so moving them onto
// the one CharacterResolver cannot quietly change who gets charged, refunded or credited.
//
// u1 is on the roster as "Rostername", their account's main is "Mainacct" and their alts are
// "Altone" / "Alttwo". u2 is "Other" everywhere, with the alt "Contested". u3, who joined after
// u2, is on the roster as "Contested".
//
// ChangedCases are the two answers that changed ON PURPOSE when the copies became one rule:
//   - the account's own CharacterName now resolves everywhere. The close, End Camp, the
//     affordability guard, hand-entered loot and Presence used to skip it (a loot edit refunded
//     it while the close never charged it);
//   - a name that is one member's alt and another's roster name belongs to the roster name.
//     Every loop went member by member, so the older member's alt used to take it.
public class CharacterResolverCharacterizationTests
{
    private const int Ls = 7;

    private static ApplicationDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static void Seed(ApplicationDbContext db)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.LinkshellRoles.Add(new LinkshellRole
        {
            LinkshellId = Ls,
            Name = LinkshellRanks.Member,
            CanManageTods = true,
            CanManageAttendanceWithAddon = true,
        });
        db.Users.Add(new AppUser
        {
            Id = "u1", UserName = "one", CharacterName = "Mainacct",
            AltCharacterName1 = "Altone", AltCharacterName2 = "Alttwo",
        });
        db.Users.Add(new AppUser { Id = "u2", UserName = "two", CharacterName = "Other", AltCharacterName1 = "Contested" });
        db.Users.Add(new AppUser { Id = "u3", UserName = "three", CharacterName = "Third" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Rostername",
            Rank = LinkshellRanks.Member, LinkshellDkp = 100,
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 2, LinkshellId = Ls, AppUserId = "u2", CharacterName = "Other",
            Rank = LinkshellRanks.Member, LinkshellDkp = 100,
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 3, LinkshellId = Ls, AppUserId = "u3", CharacterName = "Contested",
            Rank = LinkshellRanks.Member, LinkshellDkp = 100,
        });
        db.SaveChanges();
    }

    private static DkpPoolResolver NewPools(ApplicationDbContext db) => new(db, new DkpPoolProvisioner(db));

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, NewPools(db), NullLogger<DkpLedgerWriter>.Instance);

    private static AddonApiController NewAddonController(ApplicationDbContext db, string issuer = "u2")
    {
        var http = new DefaultHttpContext();
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1, LinkshellId = Ls, IssuedToAppUserId = issuer, TokenPrefix = "att_1", TokenHash = "hash-1",
            CreatedAt = DateTime.UtcNow,
        };
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = Ls;
        var pools = NewPools(db);
        return new AddonApiController(
            db, null!, null!, null!, null!, NewWriter(db),
            pools, new DkpPoolBalanceService(db, pools), null!, null!, TestPermissions.For(db),
            NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    // What every copy answers now and some did not before (see the header).
    public static TheoryData<string, string?> ChangedCases => new()
    {
        { "Mainacct", "u1" },
        { " contested ", "u3" },
    };

    // The cases every copy agreed on before the move. Null = nobody.
    public static TheoryData<string, string?> SharedCases => new()
    {
        { "Rostername", "u1" },
        { "rostername", "u1" },
        { "Altone", "u1" },
        { " ALTTWO ", "u1" },
        { "Other", "u2" },
        { "Nobody", null },
    };

    [Theory]
    [MemberData(nameof(SharedCases))]
    [MemberData(nameof(ChangedCases))]
    public void EventClose_ResolvesTheWinnerByName(string typed, string? expected)
    {
        using var db = NewDb();
        Seed(db);
        var memberships = db.AppUserLinkshells.Include(m => m.AppUser).ToList();

        var winner = EventEndService.ResolveLootWinnerMembership(
            new EventLootDetail { ItemWinner = typed },
            memberships.ToDictionary(m => m.AppUserId!),
            new Dictionary<string, AppUserEvent>(StringComparer.OrdinalIgnoreCase),
            CharacterResolver.ForMemberships(memberships));

        Assert.Equal(expected, winner?.AppUserId);
    }

    // The event's own participant row answers first: they joined under that name, so their
    // account is who won it.
    [Fact]
    public void EventClose_PrefersTheParticipantRowOfThatName()
    {
        using var db = NewDb();
        Seed(db);
        var memberships = db.AppUserLinkshells.Include(m => m.AppUser).ToList();
        var participants = new Dictionary<string, AppUserEvent>(StringComparer.OrdinalIgnoreCase)
        {
            ["Stranger"] = new AppUserEvent { AppUserId = "u2", CharacterName = "Stranger" },
        };

        var winner = EventEndService.ResolveLootWinnerMembership(
            new EventLootDetail { ItemWinner = " stranger " },
            memberships.ToDictionary(m => m.AppUserId!),
            participants,
            CharacterResolver.ForMemberships(memberships));

        Assert.Equal("u2", winner?.AppUserId);
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    [MemberData(nameof(ChangedCases))]
    public async Task AffordabilityGuard_ResolvesTheWinnerByName(string typed, string? expected)
    {
        await using var db = NewDb();
        Seed(db);

        var winner = await LootDkpGuard.ResolveWinnerAsync(db, Ls, typed, CancellationToken.None);

        Assert.Equal(expected, winner?.AppUserId);
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    [MemberData(nameof(ChangedCases))]
    public async Task HandEnteredLoot_ResolvesTheWinnerByName(string typed, string? expected)
    {
        await using var db = NewDb();
        Seed(db);
        var pools = NewPools(db);
        var manual = new ManualLootService(db, NewWriter(db), pools, new DkpPoolBalanceService(db, pools));

        var result = await manual.AddAsync(
            Ls, ManualLootTarget.None, "Ridill", typed, 10, null, null, CancellationToken.None);

        if (expected is null)
        {
            Assert.False(result.Success);
            return;
        }
        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, db.EventLootDetails.Single().ItemWinnerAppUserId);
    }

    // A row from before winners were recorded by account: deleting it refunds whoever the name is.
    [Theory]
    [MemberData(nameof(SharedCases))]
    [MemberData(nameof(ChangedCases))]
    public async Task LootDeleteRefund_ResolvesAnUnrecordedWinnerByName(string typed, string? expected)
    {
        await using var db = NewDb();
        Seed(db);
        db.EventHistories.Add(new EventHistory { Id = 50, LinkshellId = Ls, EventName = "Sky", EventType = "Sky" });
        db.EventLootDetails.Add(new EventLootDetail
        {
            Id = 60, EventHistoryId = 50, LinkshellId = Ls, ItemName = "Ridill", ItemWinner = typed,
            WinningDkpSpent = 30, ActualDeductedDkp = 30, DkpDebitedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        var actor = db.Users.Single(u => u.Id == "u2");
        var edits = new LootEditService(db, NewWriter(db), NewPools(db), NullLogger<LootEditService>.Instance);

        var result = await edits.DeleteEventLootAsync(60, actor, "Wrong item", DateTime.UtcNow);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, db.DkpLedgerEntries.SingleOrDefault()?.AppUserId);
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    [MemberData(nameof(ChangedCases))]
    public async Task PendingLootSpend_ResolvesTheWinnerByName(string typed, string? expected)
    {
        await using var db = NewDb();
        Seed(db);
        db.Events.Add(new Event { Id = 100, LinkshellId = Ls, EventName = "Sky", EventType = "Sky" });
        db.EventLootDetails.Add(new EventLootDetail
        {
            Id = 60, EventId = 100, LinkshellId = Ls, ItemName = "Ridill", ItemWinner = typed, WinningDkpSpent = 30,
        });
        db.SaveChanges();

        var byUser = await AuctionDkpService.ComputePendingLiveLootSpendByUserAsync(db, Ls, CancellationToken.None);

        Assert.Equal(expected, byUser.Keys.SingleOrDefault());
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    [MemberData(nameof(ChangedCases))]
    public async Task ClaimShieldCapture_ResolvesEachNameToAnAccount(string typed, string? expected)
    {
        await using var db = NewDb();
        Seed(db);

        var result = await NewAddonController(db).PostClaimShieldCaptureAsync(
            new AddonApiController.AddonClaimShieldRequest(
                "Fafnir", true, 10, DateTime.UtcNow.AddMinutes(-1), "result line", [typed]),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(expected, db.ClaimShieldCaptureMembers.Single().AppUserId);
    }

    [Theory]
    [MemberData(nameof(SharedCases))]
    [MemberData(nameof(ChangedCases))]
    public async Task WindowRosterPost_ResolvesEachNameToAnAccount(string typed, string? expected)
    {
        await using var db = NewDb();
        Seed(db);
        db.Events.Add(new Event
        {
            Id = 100, LinkshellId = Ls, EventName = "Sky", EventType = "Sky",
            StartTime = DateTime.UtcNow.AddHours(-1), CommencementStartTime = DateTime.UtcNow.AddHours(-1),
        });
        db.SaveChanges();

        var result = await NewAddonController(db).PostAttendanceAsync(
            100,
            new AddonApiController.AddonAttendanceRequest(
                DateTime.UtcNow, [new AddonApiController.AddonAttendanceEntry(typed, null, null, null)]),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(expected, db.AppUserEvents.SingleOrDefault()?.AppUserId);
    }

    // A posted "/lsm now" card pays whoever each captured name is.
    [Theory]
    [MemberData(nameof(SharedCases))]
    [MemberData(nameof(ChangedCases))]
    public async Task PostedWindowCard_PaysEachNameToAnAccount(string typed, string? expected)
    {
        await using var db = NewDb();
        Seed(db);
        var now = DateTime.UtcNow;
        db.WindowEvents.Add(new WindowEvent
        {
            Id = 1, LinkshellId = Ls, Name = "Fafnir", Status = WindowEventStatuses.Open,
            CreatedAtUtc = now, FirstCapturedAtUtc = now, LastCapturedAtUtc = now,
            EntryType = WindowEventEntryTypes.KingsCamp, DkpAmount = 4, PostedToSheetAt = now,
        });
        db.AttendanceSnapshots.Add(new AttendanceSnapshot
        {
            Id = 1, LinkshellId = Ls, WindowEventId = 1, CapturedAtUtc = now,
            SnapshotStatus = AttendanceSnapshotStatuses.Active, EntryCount = 1,
        });
        db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry { Id = 1, SnapshotId = 1, CharacterName = typed });
        db.SaveChanges();
        var pools = NewPools(db);
        var ledger = new WindowEventDkpLedgerService(
            db, NewWriter(db), pools, NullLogger<WindowEventDkpLedgerService>.Instance);

        await ledger.EnsurePostedWindowEventLedgerEntriesAsync(1, CancellationToken.None);

        Assert.Equal(expected, db.DkpLedgerEntries.SingleOrDefault(e => e.EntryType == "SnapshotEarned")?.AppUserId);
    }

    // Presence knows a name when it resolves to an account (expected = which one), and names the
    // roster main only when the name was one of their alts.
    [Theory]
    [InlineData("Rostername", "u1", null)]
    [InlineData("rostername", "u1", null)]
    [InlineData("Altone", "u1", "Rostername")]
    [InlineData(" ALTTWO ", "u1", "Rostername")]
    [InlineData("Other", "u2", null)]
    [InlineData("Nobody", null, null)]
    [InlineData("Mainacct", "u1", "Rostername")]
    [InlineData(" contested ", "u3", null)]
    public async Task Presence_TagsEachNameWithItsAccount(string typed, string? expected, string? main)
    {
        await using var db = NewDb();
        Seed(db);

        var result = await NewAddonController(db).PostPresenceAsync(
            new AddonApiController.AddonPresenceRequest(
                "Other", null, null, 1,
                [new AddonApiController.AddonPresenceMemberDto(typed, 1, null, null, null, null)]),
            CancellationToken.None);

        // The row no longer stores the account (nothing read it); what a resolved name still decides
        // is whether it counts as unregistered, and the "alt of" hint.
        var ok = Assert.IsType<OkObjectResult>(result);
        var unregistered = (int)ok.Value!.GetType().GetProperty("unregistered")!.GetValue(ok.Value)!;
        Assert.Equal(expected is null ? 1 : 0, unregistered);
        var row = db.LinkshellPresences.Single();
        Assert.Equal(main, row.MainCharacterName);
    }
}
