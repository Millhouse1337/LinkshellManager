using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LinkshellManager.Tests;

// The roster half of the sign-up board's first-time form. Once the names pass CharacterNameFormat,
// FindOrCreateForOutsideAsync is what puts a player who has never opened LSM onto the linkshell
// roster, keyed to their Discord id so their next Sign Up skips the form.
//
// The refusals matter as much as the happy path: the form is open to anyone in the Discord, so
// typing a real member's name must never hand that member's roster spot (and DKP) to a stranger.
public class OutsideOnboardingRosterTests
{
    private const int Ls = 1;
    private const string Discord = "111";
    private const string OtherDiscord = "222";

    private static ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS" });
        db.SaveChanges();
        return db;
    }

    // A real UserManager over the in-memory store, so creating the placeholder account runs the
    // same Identity checks (unique user name, allowed characters) it does in the app.
    private static ManualMemberService NewService(ApplicationDbContext db) =>
        new(db, new UserManager<AppUser>(
            new UserStore<AppUser>(db),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            new[] { new UserValidator<AppUser>() },
            Array.Empty<IPasswordValidator<AppUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance));

    private static void AddMember(ApplicationDbContext db, string appUserId, string name, bool placeholder, string? discordUserId = null)
    {
        db.Users.Add(new AppUser { Id = appUserId, UserName = appUserId, CharacterName = name, IsPlaceholder = placeholder });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            LinkshellId = Ls, AppUserId = appUserId, CharacterName = name, DiscordUserId = discordUserId,
            Rank = LinkshellRanks.Member, Status = "Active",
        });
        db.SaveChanges();
    }

    // ---- a first-timer -------------------------------------------------------------

    [Fact]
    public async Task FirstTimer_IsAddedToTheRoster_AsAnActiveMember_LinkedToTheirDiscord()
    {
        using var db = NewDb();

        var result = await NewService(db).FindOrCreateForOutsideAsync(
            Ls, "Millhouse", "Milltwo", "Millthree", Discord, CancellationToken.None);

        Assert.True(result.Success);
        var member = Assert.Single(db.AppUserLinkshells.Include(m => m.AppUser));
        Assert.Equal(result.AppUserId, member.AppUserId);
        Assert.Equal(Ls, member.LinkshellId);
        Assert.Equal("Millhouse", member.CharacterName);
        Assert.Equal(LinkshellRanks.Member, member.Rank);
        Assert.Equal("Active", member.Status);
        Assert.Equal(0, member.LinkshellDkp);
        // The Discord id on the roster row is what the next Sign Up looks them up by.
        Assert.Equal(Discord, member.DiscordUserId);
        // A placeholder can't sign in; opening LSM with this Discord later promotes it in place.
        Assert.True(member.AppUser!.IsPlaceholder);
        Assert.Equal("Millhouse", member.AppUser.CharacterName);
        Assert.Equal("Milltwo", member.AppUser.AltCharacterName1);
        Assert.Equal("Millthree", member.AppUser.AltCharacterName2);
    }

    [Fact]
    public async Task Alts_AreOptional()
    {
        // The form sends an untouched alt box as an empty string, not null; both mean "no alt".
        using var db = NewDb();

        var result = await NewService(db).FindOrCreateForOutsideAsync(
            Ls, "Millhouse", null, "", Discord, CancellationToken.None);

        Assert.True(result.Success);
        var user = Assert.Single(db.Users);
        Assert.Null(user.AltCharacterName1);
        Assert.Null(user.AltCharacterName2);
    }

    [Fact]
    public async Task SubmittingTheFormAgain_ReusesTheSameRosterSpot()
    {
        // Two open forms, or a double click: the second submit must not make a second Millhouse.
        using var db = NewDb();
        var service = NewService(db);

        var first = await service.FindOrCreateForOutsideAsync(Ls, "Millhouse", null, null, Discord, CancellationToken.None);
        var second = await service.FindOrCreateForOutsideAsync(Ls, "Millhouse", "Milltwo", null, Discord, CancellationToken.None);

        Assert.True(second.Success);
        Assert.Equal(first.AppUserId, second.AppUserId);
        Assert.Single(db.AppUserLinkshells);
        Assert.Equal("Milltwo", Assert.Single(db.Users).AltCharacterName1);
    }

    // ---- someone of that name is already on the roster -----------------------------

    [Fact]
    public async Task AnUnlinkedPlaceholder_OfThatName_IsAdopted_NotDuplicated()
    {
        // A leader added Millhouse by hand (or a DKP import did) before they ever clicked Sign Up.
        // Their first Sign Up should claim that row, DKP and all, not start a second one at zero.
        using var db = NewDb();
        AddMember(db, "ph1", "Millhouse", placeholder: true);

        var result = await NewService(db).FindOrCreateForOutsideAsync(
            Ls, "Millhouse", "Milltwo", null, Discord, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ph1", result.AppUserId);
        var member = Assert.Single(db.AppUserLinkshells.Include(m => m.AppUser));
        Assert.Equal(Discord, member.DiscordUserId);
        Assert.Equal("Milltwo", member.AppUser!.AltCharacterName1);
    }

    [Fact]
    public async Task ARealMembersName_IsRefused_AndChangesNothing()
    {
        using var db = NewDb();
        AddMember(db, "real1", "Millhouse", placeholder: false);

        var result = await NewService(db).FindOrCreateForOutsideAsync(
            Ls, "Millhouse", null, null, Discord, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("synced member", result.Error);
        var member = Assert.Single(db.AppUserLinkshells);
        Assert.Null(member.DiscordUserId);
        Assert.Single(db.Users);
    }

    [Fact]
    public async Task APlaceholderLinkedToAnotherDiscord_IsRefused_AndChangesNothing()
    {
        using var db = NewDb();
        AddMember(db, "ph1", "Millhouse", placeholder: true, discordUserId: OtherDiscord);

        var result = await NewService(db).FindOrCreateForOutsideAsync(
            Ls, "Millhouse", null, null, Discord, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("another Discord user", result.Error);
        var member = Assert.Single(db.AppUserLinkshells);
        Assert.Equal(OtherDiscord, member.DiscordUserId);
        Assert.Single(db.Users);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankMainName_IsRefused(string? main)
    {
        using var db = NewDb();

        var result = await NewService(db).FindOrCreateForOutsideAsync(
            Ls, main, null, null, Discord, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(db.AppUserLinkshells);
        Assert.Empty(db.Users);
    }
}
