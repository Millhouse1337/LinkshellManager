using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5 (R5.4): a profile save used to rename EVERY open sign-up of the account to its main, so a
// member signed up on an alt was silently moved onto their main by any profile save. Now a sign-up
// follows only the character it was made under: renaming the main renames the main's sign-ups,
// renaming an alt renames that alt's, and the rest are left alone. Closed events keep their name.
[Collection("postgres")]
public class ProfileRenameSignupTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public ProfileRenameSignupTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Seeded(int OnAlt, int OnMain, int Closed, int SlotOnAlt, int SlotOnMain);

    [Fact]
    public async Task RenamingTheMain_KeepsTheAltSignup_AndRenamingTheAlt_RenamesIt()
    {
        var seeded = await SeedAsync();

        // 1. The main is renamed; the alts stay.
        var first = await SaveAsync(me => (me, "Newmain", "Altone", "Alttwo"));
        Assert.True(first.Succeeded, string.Join("; ", first.Errors.Select(e => e.Description)));
        Assert.Equal("altone", await ParticipationNameAsync(seeded.OnAlt)); // untouched, as signed up
        Assert.Equal("Newmain", await ParticipationNameAsync(seeded.OnMain));
        Assert.Equal("Mine", await ParticipationNameAsync(seeded.Closed));
        Assert.Equal("Altone", await SlotNameAsync(seeded.SlotOnAlt));
        Assert.Equal("Newmain", await SlotNameAsync(seeded.SlotOnMain));

        // 2. Alt 1 is renamed (capitals and padding ignored when matching the sign-up's name).
        var second = await SaveAsync(me => (me, "Newmain", " Altnew ", "Alttwo"));
        Assert.True(second.Succeeded, string.Join("; ", second.Errors.Select(e => e.Description)));
        Assert.Equal("Altnew", await ParticipationNameAsync(seeded.OnAlt));
        Assert.Equal("Newmain", await ParticipationNameAsync(seeded.OnMain));
        Assert.Equal("Altnew", await SlotNameAsync(seeded.SlotOnAlt));
        Assert.Equal("Mine", await ParticipationNameAsync(seeded.Closed));

        // 3. The main and alt 1 swap names: each sign-up follows its own character.
        var third = await SaveAsync(me => (me, "Altnew", "Newmain", "Alttwo"));
        Assert.True(third.Succeeded, string.Join("; ", third.Errors.Select(e => e.Description)));
        Assert.Equal("Newmain", await ParticipationNameAsync(seeded.OnAlt));
        Assert.Equal("Altnew", await ParticipationNameAsync(seeded.OnMain));

        // 4. An alt is cleared: its sign-up goes to the main.
        var fourth = await SaveAsync(me => (me, "Altnew", null, "Alttwo"));
        Assert.True(fourth.Succeeded, string.Join("; ", fourth.Errors.Select(e => e.Description)));
        Assert.Equal("Altnew", await ParticipationNameAsync(seeded.OnAlt));

        // Since R5.6 the roster name is not written: it keeps what it was seeded with. The account's
        // CharacterName follows the main.
        await using var check = _pg.CreateContext();
        var link = await check.AppUserLinkshells.AsNoTracking().Include(l => l.AppUser).SingleAsync(l => l.AppUserId == "me");
        Assert.Equal(("Mine", "Altnew"), (link.CharacterName, link.AppUser!.CharacterName));
    }

    // The old rule's other half: an open sign-up with no name at all still takes the main's.
    [Fact]
    public async Task ASignupWithNoName_TakesTheMainsName()
    {
        var seeded = await SeedAsync();
        await using (var db = _pg.CreateContext())
        {
            var row = await db.AppUserEvents.SingleAsync(p => p.Id == seeded.OnMain);
            row.CharacterName = null;
            await db.SaveChangesAsync();
        }

        var result = await SaveAsync(me => (me, "Mine", "Altone", "Alttwo"));
        Assert.True(result.Succeeded);
        Assert.Equal("Mine", await ParticipationNameAsync(seeded.OnMain));
        Assert.Equal("altone", await ParticipationNameAsync(seeded.OnAlt)); // untouched, as signed up
    }

    private async Task<Seeded> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        db.Users.Add(new AppUser
        {
            Id = "me", UserName = "me", CharacterName = "Mine", AltCharacterName1 = "Altone", AltCharacterName2 = "Alttwo",
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell { LinkshellId = linkshell.Id, AppUserId = "me", CharacterName = "Mine" });

        var setup = new PartySetup { LinkshellId = linkshell.Id, Name = "Sky" };
        var slotA = new PartySetupSlot { SortOrder = 0 };
        var slotB = new PartySetupSlot { SortOrder = 1 };
        setup.Alliances.Add(new PartySetupAlliance
        {
            Name = "Alliance A",
            Parties = { new PartySetupParty { Name = "Party 1", Slots = { slotA, slotB } } },
        });
        db.PartySetups.Add(setup);

        var start = DateTime.UtcNow.AddHours(1);
        var onAltEvent = new Event { LinkshellId = linkshell.Id, EventName = "Sky", EventType = "Sky", StartTime = start, PartySetup = setup };
        var onMainEvent = new Event { LinkshellId = linkshell.Id, EventName = "Sea", EventType = "Sea", StartTime = start, PartySetup = setup };
        var closedEvent = new Event
        {
            LinkshellId = linkshell.Id, EventName = "Old", EventType = "Sky",
            StartTime = start.AddDays(-2), EndTime = start.AddDays(-2).AddHours(2),
        };
        var onAlt = new AppUserEvent { AppUserId = "me", CharacterName = "altone", StartTime = start };
        var onMain = new AppUserEvent { AppUserId = "me", CharacterName = "Mine", StartTime = start };
        var closed = new AppUserEvent { AppUserId = "me", CharacterName = "Mine", StartTime = start.AddDays(-2) };
        onAltEvent.AppUserEvents.Add(onAlt);
        onMainEvent.AppUserEvents.Add(onMain);
        closedEvent.AppUserEvents.Add(closed);
        db.Events.AddRange(onAltEvent, onMainEvent, closedEvent);
        await db.SaveChangesAsync();

        var slotOnAlt = new EventPartySlotSignup
        {
            EventId = onAltEvent.Id, PartySetupSlotId = slotA.Id, AppUserId = "me", CharacterName = "Altone",
            SignedUpAtUtc = DateTime.UtcNow,
        };
        var slotOnMain = new EventPartySlotSignup
        {
            EventId = onMainEvent.Id, PartySetupSlotId = slotB.Id, AppUserId = "me", CharacterName = "Mine",
            SignedUpAtUtc = DateTime.UtcNow,
        };
        db.EventPartySlotSignups.AddRange(slotOnAlt, slotOnMain);
        await db.SaveChangesAsync();
        return new Seeded(onAlt.Id, onMain.Id, closed.Id, slotOnAlt.Id, slotOnMain.Id);
    }

    // One profile save in its own context, as one request would make it.
    private async Task<IdentityResult> SaveAsync(Func<AppUser, (AppUser Me, string? Main, string? Alt1, string? Alt2)> names)
    {
        await using var db = _pg.CreateContext();
        var me = await db.Users.SingleAsync(u => u.Id == "me");
        var (user, main, alt1, alt2) = names(me);
        var service = new AppUserProfileService(db, NewUserManager(db), new CharacterNameValidator(db));
        return await service.UpdateProfileAsync(user, main, "UTC", alt1, alt2, preserveExistingAlts: false, CancellationToken.None);
    }

    private async Task<string?> ParticipationNameAsync(int id)
    {
        await using var db = _pg.CreateContext();
        return (await db.AppUserEvents.AsNoTracking().SingleAsync(p => p.Id == id)).CharacterName;
    }

    private async Task<string?> SlotNameAsync(int id)
    {
        await using var db = _pg.CreateContext();
        return (await db.EventPartySlotSignups.AsNoTracking().SingleAsync(s => s.Id == id)).CharacterName;
    }

    private static UserManager<AppUser> NewUserManager(ApplicationDbContext db) => new(
        new UserStore<AppUser>(db),
        Options.Create(new IdentityOptions()),
        new PasswordHasher<AppUser>(),
        Array.Empty<IUserValidator<AppUser>>(),
        Array.Empty<IPasswordValidator<AppUser>>(),
        new UpperInvariantLookupNormalizer(),
        new IdentityErrorDescriber(),
        null!,
        NullLogger<UserManager<AppUser>>.Instance);
}
