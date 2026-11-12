using System.Security.Claims;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LinkshellManager.Tests;

// A POSTED card can't gain people. Post paid who was on it then, and "Pay newly registered" pays
// anyone on it with no row from it yet -- so a name added to a posted card (typed in, a capture
// confirmed or set back to Active, a capture filed onto it) would be paid by the next press of
// that button, without the review the card went through. Each of those now answers 400 on a posted
// card and changes nothing; the same edits on an unposted card still work, and edits that only
// take people off a posted card are still allowed.
public class PostedCardCaptureEditTests
{
    private const int Ls = 7;
    private const int CardId = 300;
    private const int OnCardId = 1;    // a capture already on the card
    private const int UnlinkedId = 2;  // an unlinked capture waiting to be filed
    private const string Officer = "officer";
    private static readonly DateTime CapturedAt = new(2026, 11, 10, 21, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    // The app's controller with only what these routes touch, and a signed-in Leader.
    private static ActivityDataController NewController(ApplicationDbContext db)
    {
        var userManager = new UserManager<AppUser>(
            new UserStore<AppUser>(db),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            Array.Empty<IUserValidator<AppUser>>(),
            Array.Empty<IPasswordValidator<AppUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance);
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, Officer) }, authenticationType: "Test")),
        };
        return new ActivityDataController(
            db, null!, null!, null!, null!, userManager, null!, null!, null!, null!, null!, null!,
            NullLogger<ActivityDataController>.Instance, null!, null!, new MemberActivityService(db), null!,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance), pools,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            TestPermissions.For(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static SnapshotFilingService NewFiling(ApplicationDbContext db) =>
        new(db, new WindowEventLinkService(db, new MonsterTimingResolver(db)), new AllianceIdentityService(db));

    // The card "Sky" (posted or not) with one capture on it (Alice, in `onCardStatus`), and one
    // unlinked capture (Carol) also named "Sky".
    private static async Task<ApplicationDbContext> SeededAsync(bool posted, string onCardStatus)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS" });
        db.Users.Add(new AppUser { Id = Officer, UserName = Officer, CharacterName = "Boss" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            LinkshellId = Ls, AppUserId = Officer, CharacterName = "Boss", Rank = LinkshellRanks.Leader,
        });
        db.WindowEvents.Add(new WindowEvent
        {
            Id = CardId, LinkshellId = Ls, Name = "Sky", NormalizedName = "SKY",
            Status = WindowEventStatuses.Open, DkpAmount = 10, EntryType = "Sky",
            FirstCapturedAtUtc = CapturedAt, LastCapturedAtUtc = CapturedAt,
            PostedToSheetAt = posted ? CapturedAt.AddHours(1) : null,
        });
        var onCard = new AttendanceSnapshot
        {
            Id = OnCardId, LinkshellId = Ls, WindowEventId = CardId, CapturedAtUtc = CapturedAt,
            SnapshotStatus = onCardStatus, Name = "Sky", EntryCount = 1,
        };
        onCard.Entries.Add(new AttendanceSnapshotEntry { CharacterName = "Alice" });
        var unlinked = new AttendanceSnapshot
        {
            Id = UnlinkedId, LinkshellId = Ls, CapturedAtUtc = CapturedAt.AddMinutes(10),
            SnapshotStatus = AttendanceSnapshotStatuses.Active, Name = "Sky", EntryCount = 1,
        };
        unlinked.Entries.Add(new AttendanceSnapshotEntry { CharacterName = "Carol" });
        db.AttendanceSnapshots.AddRange(onCard, unlinked);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private static string? ErrorOf(IActionResult result)
    {
        var value = Assert.IsType<BadRequestObjectResult>(result).Value!;
        return value.GetType().GetProperty("error")?.GetValue(value) as string;
    }

    private static Task<List<string>> NamesOnCardAsync(ApplicationDbContext db) =>
        db.AttendanceSnapshotEntries.AsNoTracking()
            .Where(e => e.Snapshot!.WindowEventId == CardId)
            .Select(e => e.CharacterName)
            .OrderBy(n => n)
            .ToListAsync();

    private static Task<string> StatusOfAsync(ApplicationDbContext db, int snapshotId) =>
        db.AttendanceSnapshots.AsNoTracking().Where(s => s.Id == snapshotId).Select(s => s.SnapshotStatus).SingleAsync();

    private static ActivityDataController.ActivityAddSnapshotEntryRequest AddBob() =>
        new("Bob", null, null, null, null, null);

    [Fact]
    public async Task AddingANameToAPostedCard_IsRefused()
    {
        await using var db = await SeededAsync(posted: true, AttendanceSnapshotStatuses.Active);

        var result = await NewController(db).AddSnapshotEntryAsync(OnCardId, AddBob(), CancellationToken.None);

        Assert.Equal(SnapshotFilingService.PostedCardRefusal, ErrorOf(result));
        Assert.Equal(["Alice"], await NamesOnCardAsync(db));
    }

    [Fact]
    public async Task AddingANameToAnUnpostedCard_StillWorks()
    {
        await using var db = await SeededAsync(posted: false, AttendanceSnapshotStatuses.Active);

        var result = await NewController(db).AddSnapshotEntryAsync(OnCardId, AddBob(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(["Alice", "Bob"], await NamesOnCardAsync(db));
    }

    [Fact]
    public async Task ConfirmingACaptureOnAPostedCard_IsRefused()
    {
        await using var db = await SeededAsync(posted: true, AttendanceSnapshotStatuses.Pending);

        var result = await NewController(db).VerifyWindowSnapshotAsync(
            OnCardId, new ActivityDataController.ActivityWindowSnapshotVerifyRequest(true), CancellationToken.None);

        Assert.Equal(SnapshotFilingService.PostedCardRefusal, ErrorOf(result));
        Assert.Equal(AttendanceSnapshotStatuses.Pending, await StatusOfAsync(db, OnCardId));
    }

    [Fact]
    public async Task RejectingACaptureOnAPostedCard_StillWorks()
    {
        await using var db = await SeededAsync(posted: true, AttendanceSnapshotStatuses.Pending);

        var result = await NewController(db).VerifyWindowSnapshotAsync(
            OnCardId, new ActivityDataController.ActivityWindowSnapshotVerifyRequest(false), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(AttendanceSnapshotStatuses.Ignored, await StatusOfAsync(db, OnCardId));
    }

    [Fact]
    public async Task SettingACaptureBackToActiveOnAPostedCard_IsRefused()
    {
        await using var db = await SeededAsync(posted: true, AttendanceSnapshotStatuses.Ignored);

        var result = await NewController(db).SetWindowSnapshotStatusAsync(
            OnCardId, new ActivityDataController.ActivityWindowSnapshotStatusRequest(AttendanceSnapshotStatuses.Active),
            CancellationToken.None);

        Assert.Equal(SnapshotFilingService.PostedCardRefusal, ErrorOf(result));
        Assert.Equal(AttendanceSnapshotStatuses.Ignored, await StatusOfAsync(db, OnCardId));
    }

    [Fact]
    public async Task SettingACaptureToIgnoredOnAPostedCard_StillWorks()
    {
        await using var db = await SeededAsync(posted: true, AttendanceSnapshotStatuses.Active);

        var result = await NewController(db).SetWindowSnapshotStatusAsync(
            OnCardId, new ActivityDataController.ActivityWindowSnapshotStatusRequest(AttendanceSnapshotStatuses.Ignored),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(AttendanceSnapshotStatuses.Ignored, await StatusOfAsync(db, OnCardId));
    }

    [Fact]
    public async Task FilingACaptureOntoAPostedCard_IsRefused()
    {
        await using var db = await SeededAsync(posted: true, AttendanceSnapshotStatuses.Active);

        var result = await NewController(db).AttachWindowSnapshotAsync(
            UnlinkedId, new ActivityDataController.ActivityAttachWindowSnapshotRequest(CardId, null),
            NewFiling(db), CancellationToken.None);

        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, refused.StatusCode);
        Assert.Null((await db.AttendanceSnapshots.AsNoTracking().SingleAsync(s => s.Id == UnlinkedId)).WindowEventId);
        Assert.Equal(["Alice"], await NamesOnCardAsync(db));
    }

    // Filing by name ("Sky") used to fold the capture into the posted "Sky" card, which camp review
    // cards keep Open. It now gets a card of its own.
    [Fact]
    public async Task FilingByNameBesideAPostedCard_MakesANewCard()
    {
        await using var db = await SeededAsync(posted: true, AttendanceSnapshotStatuses.Active);

        var result = await NewController(db).AttachWindowSnapshotAsync(
            UnlinkedId, new ActivityDataController.ActivityAttachWindowSnapshotRequest(null, "Sky"),
            NewFiling(db), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var filedOn = (await db.AttendanceSnapshots.AsNoTracking().SingleAsync(s => s.Id == UnlinkedId)).WindowEventId;
        Assert.NotNull(filedOn);
        Assert.NotEqual(CardId, filedOn);
        Assert.Equal(["Alice"], await NamesOnCardAsync(db));
    }

    // Before Post, filing by name still joins the open card of that name, as it always has.
    [Fact]
    public async Task FilingByNameBesideAnUnpostedCard_StillJoinsIt()
    {
        await using var db = await SeededAsync(posted: false, AttendanceSnapshotStatuses.Active);

        var result = await NewController(db).AttachWindowSnapshotAsync(
            UnlinkedId, new ActivityDataController.ActivityAttachWindowSnapshotRequest(null, "Sky"),
            NewFiling(db), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(["Alice", "Carol"], await NamesOnCardAsync(db));
    }
}
