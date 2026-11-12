using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using Xunit;

namespace LinkshellManager.Tests;

// The places that record a Time of Death, each built with only what that path touches, so tests can
// drive them exactly as a request would: the app's Log ToD, the addon's ToD post, an officer approving
// a member's submission, and End Camp. One linkshell, one signed-in user ("tod-user") whose role
// decides whether they save a ToD or queue it.
internal static class TodCallers
{
    internal const int LinkshellId = 7;
    internal const string User = "tod-user";

    internal static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // The linkshell, the user as a Member, and the Member role with or without Manage ToDs.
    internal static async Task<ApplicationDbContext> SeededAsync(bool canManageTods = true, string? timeZone = null)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test", EnableHnmSection = true });
        db.Users.Add(new AppUser { Id = User, UserName = User, CharacterName = "Tester", TimeZone = timeZone });
        db.LinkshellRoles.Add(new LinkshellRole
        {
            LinkshellId = LinkshellId,
            Name = LinkshellRanks.Member,
            CanManageTods = canManageTods,
            CanSubmitTodForApproval = true,
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = User,
            LinkshellId = LinkshellId,
            CharacterName = "Tester",
            Rank = LinkshellRanks.Member,
        });
        await db.SaveChangesAsync();
        return db;
    }

    internal static SubmissionApprovalService Approvals(ApplicationDbContext db) =>
        new(db, NullLogger<SubmissionApprovalService>.Instance);

    private static IServiceProvider Services(ApplicationDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(Approvals(db));
        return services.BuildServiceProvider();
    }

    // ---- the app's Log ToD (POST /api/activity/tods) ----

    internal static ActivityDataController App(ApplicationDbContext db)
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
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, User) }, authenticationType: "Test")),
            RequestServices = Services(db),
        };
        var timeZones = new TimeZoneConversionService(
            DateTimeZoneProviders.Tzdb, NullLogger<TimeZoneConversionService>.Instance);

        return new ActivityDataController(
            db, null!, null!, null!, null!, userManager, null!, null!, timeZones, null!, null!, null!,
            NullLogger<ActivityDataController>.Instance, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            new MonsterTimingResolver(db), null!, null!,
            TestPermissions.For(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    // A ToD logged from the app, the time sent as an explicit UTC instant.
    internal static ActivityCreateTodRequest AppRequest(
        string monster, DateTime killedAtUtc, bool hq = false, bool? claim = true, int? dayNumber = null,
        int? popWindow = null, int additionalSeconds = 0, string? cooldown = null, string? imagePath = null) =>
        new(LinkshellId, monster, dayNumber, claim, killedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            cooldown, null, imagePath, hq, additionalSeconds, popWindow);

    internal static async Task<JsonElement> AppPostAsync(ApplicationDbContext db, ActivityCreateTodRequest request)
    {
        var result = await App(db).CreateTodAsync(request, CancellationToken.None);
        return JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>(result).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    // ---- the addon's ToD post (POST /api/addon/tod) ----

    internal static AddonApiController Addon(ApplicationDbContext db)
    {
        var http = new DefaultHttpContext { RequestServices = Services(db) };
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1,
            LinkshellId = LinkshellId,
            IssuedToAppUserId = User,
            TokenPrefix = "att_1",
            TokenHash = "hash-1",
            CreatedAt = DateTime.UtcNow,
        };
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = LinkshellId;

        return new AddonApiController(
            db, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            new MonsterTimingResolver(db), null!, TestPermissions.For(db), NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    internal static async Task<JsonElement> AddonPostAsync(
        ApplicationDbContext db, string monster, DateTime killedAtUtc, bool? claim = true, int? dayNumber = null,
        int? popWindow = null)
    {
        var result = await Addon(db).PostTodAsync(
            new AddonApiController.AddonPostTodRequest(monster, killedAtUtc, null, claim, dayNumber, popWindow),
            CancellationToken.None);
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
    }

    // ---- End Camp (the Discord End Camp modal and the app's End Camp form) ----

    internal static HnmCampPopService Pop(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var handoff = new HnmCampReviewHandoffService(
            db,
            new WdCampFinalizer(db, NullLogger<WdCampFinalizer>.Instance),
            new HnmStandardCampFinalizer(db, NullLogger<HnmStandardCampFinalizer>.Instance),
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<HnmCampReviewHandoffService>.Instance);
        return new HnmCampPopService(db, handoff, new MonsterTimingResolver(db), NullLogger<HnmCampPopService>.Instance);
    }

    // ---- shared seeds ----

    internal static LinkshellMonsterTiming Setup(string monster, int cooldownMinutes, int additionalSeconds = 0) => new()
    {
        LinkshellId = LinkshellId,
        MonsterName = monster,
        NormalizedMonsterName = MonsterTimingDefaults.Normalize(monster),
        CooldownMinutes = cooldownMinutes,
        AdditionalSeconds = additionalSeconds,
    };

    // A party setup with one member signed up, assigned to `monster`: what a new ToD resets.
    internal static PartySetupSlot SignedUpSetup(ApplicationDbContext db, string monster)
    {
        var slot = new PartySetupSlot
        {
            SignedUpAppUserId = User,
            SignedUpCharacterName = "Tester",
            SignedUpAtUtc = DateTime.UtcNow,
        };
        db.PartySetups.Add(new PartySetup
        {
            LinkshellId = LinkshellId,
            AssignedMonsterName = monster,
            Alliances = { new PartySetupAlliance { Parties = { new PartySetupParty { Slots = { slot } } } } },
        });
        return slot;
    }

    // A board parked after its last camp, still advertising `oldRepop`.
    internal static Event ParkedBoard(int id, string monster, DateTime oldRepop) => new()
    {
        Id = id,
        LinkshellId = LinkshellId,
        EventName = monster,
        EventType = "HNM",
        AssignedMonsterName = monster,
        StartTime = oldRepop,
        HnmDefeatedAt = oldRepop.AddHours(-22),
    };
}
