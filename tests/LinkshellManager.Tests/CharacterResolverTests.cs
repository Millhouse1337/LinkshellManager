using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The one rule for "which member is this character?" (Services/CharacterResolver.cs): roster
// name, then account name, then alt 1, then alt 2; within a slot the oldest membership; trimmed
// and ignoring capitals; never a membership with no account.
public class CharacterResolverTests
{
    private const int Ls = 7;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AppUserLinkshell Member(
        int id, string? appUserId, string? roster, string? account = null, string? alt1 = null, string? alt2 = null) =>
        new()
        {
            Id = id,
            LinkshellId = Ls,
            AppUserId = appUserId,
            CharacterName = roster,
            AppUser = appUserId is null
                ? null
                : new AppUser
                {
                    Id = appUserId, CharacterName = account, AltCharacterName1 = alt1, AltCharacterName2 = alt2,
                },
        };

    [Theory]
    [InlineData("Roster", "u1")]
    [InlineData("ACCOUNT", "u1")]
    [InlineData(" altone", "u1")]
    [InlineData("Alttwo ", "u1")]
    [InlineData("Nobody", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void ResolvesAnyOfTheFourNames(string? typed, string? expected)
    {
        var roster = CharacterResolver.ForMemberships(new[]
        {
            Member(1, "u1", "Roster", "Account", "Altone", "Alttwo"),
        });

        Assert.Equal(expected, roster.Resolve(typed)?.AppUserId);
    }

    // A name that is an older member's alt AND a newer member's roster name belongs to the roster
    // name. The loops this replaced went member by member, so the older member's alt took it.
    [Fact]
    public void ARosterNameBeatsSomebodyElsesAlt_WhateverTheOrder()
    {
        var altOwner = Member(1, "u1", "First", alt1: "Shared");
        var rosterOwner = Member(2, "u2", "Shared");

        Assert.Equal("u2", CharacterResolver.ForMemberships(new[] { altOwner, rosterOwner }).Resolve("shared")?.AppUserId);
        Assert.Equal("u2", CharacterResolver.ForMemberships(new[] { rosterOwner, altOwner }).Resolve("shared")?.AppUserId);
    }

    [Fact]
    public void AnAccountNameBeatsSomebodyElsesAlt()
    {
        var altOwner = Member(1, "u1", "First", alt2: "Shared");
        var accountOwner = Member(2, "u2", "Second", account: "Shared");

        var roster = CharacterResolver.ForMemberships(new[] { altOwner, accountOwner });

        Assert.True(roster.TryResolve("Shared", out var match));
        Assert.Equal("u2", match.Member.AppUserId);
        Assert.Equal(CharacterNameSlot.Account, match.Slot);
    }

    // Same slot on two members: the older membership, however the rows arrive.
    [Fact]
    public void WithinOneSlot_TheOldestMembershipWins()
    {
        var older = Member(3, "u3", "Twin");
        var newer = Member(9, "u9", "Twin");

        Assert.Equal("u3", CharacterResolver.ForMemberships(new[] { newer, older }).Resolve("Twin")?.AppUserId);
    }

    // Nobody to charge or credit, so it must not take the name from a member who has an account.
    [Fact]
    public void AMembershipWithNoAccount_IsNeverTheAnswer()
    {
        var noAccount = Member(1, null, "Shared");
        var real = Member(2, "u2", "Other", alt1: "Shared");

        Assert.Equal("u2", CharacterResolver.ForMemberships(new[] { noAccount, real }).Resolve("Shared")?.AppUserId);
    }

    [Fact]
    public void AddIfMissing_NeverTakesANameSomebodyHas()
    {
        var holder = Member(1, "u1", "Taken");
        var latecomer = Member(2, "u2", null);
        var roster = CharacterResolver.ForMemberships(new[] { holder, latecomer });

        roster.AddIfMissing("taken", latecomer, CharacterNameSlot.Membership);
        roster.AddIfMissing(" Fresh ", latecomer, CharacterNameSlot.Membership);

        Assert.Equal("u1", roster.Resolve("Taken")?.AppUserId);
        Assert.Equal("u2", roster.Resolve("fresh")?.AppUserId);
    }

    // The joined (AsNoTracking) overload and the database loader give the same answers.
    [Fact]
    public async Task TheLoaderAndThePairOverload_AgreeWithTheInMemoryOne()
    {
        await using var db = NewDb();
        db.Users.Add(new AppUser { Id = "u1", UserName = "one", CharacterName = "Account1", AltCharacterName1 = "Shared" });
        db.Users.Add(new AppUser { Id = "u2", UserName = "two", CharacterName = "Shared" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Roster1" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = "u2", CharacterName = "Roster2" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 3, LinkshellId = Ls + 1, AppUserId = "u1", CharacterName = "Elsewhere" });
        await db.SaveChangesAsync();

        var loaded = await CharacterResolver.LoadMembershipsAsync(db, Ls, CancellationToken.None);
        var pairs = CharacterResolver.ForMemberships(db.AppUserLinkshells
            .Where(link => link.LinkshellId == Ls)
            .Join(db.Users, link => link.AppUserId, user => user.Id, (link, user) => new { link, user })
            .AsEnumerable()
            .Select(x => (x.link, x.user)));

        foreach (var roster in new[] { loaded, pairs })
        {
            Assert.Equal("u2", roster.Resolve("shared")?.AppUserId); // u2's account name beats u1's alt
            Assert.Equal("u1", roster.Resolve("account1")?.AppUserId);
            Assert.Null(roster.Resolve("Elsewhere"));               // another linkshell's roster name
        }
    }
}
