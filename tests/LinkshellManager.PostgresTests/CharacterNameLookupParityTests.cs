using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.PostgresTests;

// CharactersSwitch moved the name lookups from the account's four name columns to Characters.
// Which member a name belongs to (and in which slot) must not change for any roster, so every
// lookup is run both ways over a messy one and compared: the old way (the account columns, exactly
// as the code read them before) and the new way (Characters).
//
// The roster has: three accounts sharing "Bob" (one a placeholder), capitals and padding that differ
// (" bob ", tabs, a no-break and an ideographic space), a roster name that differs from the account
// name, a roster name that is another member's alt, an alt that is another member's main, a main
// repeated by both alts of the same account, blank alts, a blank main, a membership with no account,
// and an account in another linkshell with the same name.
[Collection("postgres")]
public class CharacterNameLookupParityTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public CharacterNameLookupParityTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // (account, placeholder, main, alt 1, alt 2, roster name). A null account is a membership with
    // no account.
    private static readonly (string? Id, bool Placeholder, string? Main, string? Alt1, string? Alt2, string? Roster)[] Roster =
    {
        ("u-bob", false, "Bob", "Bobalt", null, "Bob"),
        ("u-robert", false, " bob ", null, "Bobalt", "Robert"),
        ("u-ph", true, "Bob", "\tcarl", null, "Bob"),
        ("u-twin", false, "Twin", "twin", "TWIN ", "Twin"),
        ("u-dee", false, "Dee", "   ", "", "Dee"),
        ("u-eve", false, "Eve", null, null, "Bobalt"),
        ("u-fay", false, null, "Fay", null, null),
        ("u-gus", false, "Gus", "dee", "Carl", "Gus"),
        ("u-hal", false, " Hal　", "Hal Two", "Halthree", " Hal "),
        (null, false, null, null, null, "Ghost"),
    };

    private const string Elsewhere = "u-other-ls";

    [Fact]
    public async Task EveryLookup_GivesTheSameAnswer_FromCharactersAsFromTheAccountColumns()
    {
        var linkshellId = await SeedAsync();
        var probes = Probes();

        await using var db = _pg.CreateContext();

        // CharacterResolver: the old LoadMembershipsAsync was ForMemberships over the tracked
        // memberships with their AppUser.
        var oldMemberships = await db.AppUserLinkshells.AsNoTracking()
            .Include(link => link.AppUser)
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .OrderBy(link => link.Id)
            .ToListAsync();
        var oldResolver = CharacterResolver.ForMemberships(oldMemberships);
        var newResolver = await CharacterResolver.LoadMembershipsAsync(db, linkshellId, CancellationToken.None);
        foreach (var probe in probes)
        {
            Assert.Equal(Answer(oldResolver, probe), Answer(newResolver, probe));
        }
        // The roster really is messy: some names resolve through an alt, some through the account.
        Assert.Contains(probes, probe => newResolver.TryResolve(probe, out var m) && m.Slot == CharacterNameSlot.Alt2);
        Assert.Contains(probes, probe => newResolver.TryResolve(probe, out var m) && m.Slot == CharacterNameSlot.Account);

        // LinkshellRosterNames: the old LoadCandidatesAsync joined the account columns.
        var oldCandidates = await db.AppUserLinkshells.AsNoTracking()
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .Join(db.Users.AsNoTracking(), link => link.AppUserId, user => user.Id,
                (link, user) => new RosterNameCandidate(
                    link.CharacterName, user.CharacterName, user.AltCharacterName1, user.AltCharacterName2, link.AppUserId))
            .ToListAsync();
        var newCandidates = await LinkshellRosterNames.LoadCandidatesAsync(db, linkshellId, CancellationToken.None);
        Assert.Equal(
            oldCandidates.Select(c => c.AppUserId).OrderBy(id => id, StringComparer.Ordinal),
            newCandidates.Select(c => c.AppUserId).OrderBy(id => id, StringComparer.Ordinal));
        foreach (var candidate in newCandidates)
        {
            var old = oldCandidates.Single(c => c.AppUserId == candidate.AppUserId);
            Assert.Equal(old.MembershipCharacterName, candidate.MembershipCharacterName);
            Assert.Equal(NameSet(old), NameSet(candidate));
        }

        var oldSet = RosterNameSet.From(oldCandidates);
        var newSet = await LinkshellRosterNames.LoadAsync(db, linkshellId, CancellationToken.None);
        Assert.Equal(oldSet.Count, newSet.Count);
        foreach (var probe in probes)
        {
            Assert.Equal(oldSet.Contains(probe), newSet.Contains(probe));
            Assert.Equal(
                RosterMemberSearch.Find(oldCandidates, probe).Select(c => c.AppUserId).OrderBy(id => id, StringComparer.Ordinal),
                RosterMemberSearch.Find(newCandidates, probe).Select(c => c.AppUserId).OrderBy(id => id, StringComparer.Ordinal));
        }
        Assert.DoesNotContain(newCandidates, c => c.AppUserId == Elsewhere);
    }

    // The addon's /roster: the same rows, names and job levels, except that an alt repeating the
    // same account's main (u-twin's two) is no longer listed: it has no Characters row.
    [Fact]
    public async Task AddonRoster_ListsTheSameAltsAndJobs_ExceptRepeatsOfTheMain()
    {
        var linkshellId = await SeedAsync();

        await using var db = _pg.CreateContext();
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(user => user.Id);
        var oldRows = (await db.AppUserLinkshells.AsNoTracking()
                .Where(link => link.LinkshellId == linkshellId && !string.IsNullOrWhiteSpace(link.CharacterName))
                .OrderBy(link => link.CharacterName)
                .Select(link => new { link.CharacterName, link.AppUserId })
                .ToListAsync())
            .Select(link =>
            {
                var user = link.AppUserId is null ? null : users[link.AppUserId];
                var alts = new List<string>();
                var details = new List<object>();
                if (!string.IsNullOrWhiteSpace(user?.AltCharacterName1))
                {
                    alts.Add(user.AltCharacterName1.Trim());
                    details.Add(new { name = user.AltCharacterName1.Trim(), jobLevels = user.Alt1JobLevels });
                }
                if (!string.IsNullOrWhiteSpace(user?.AltCharacterName2))
                {
                    alts.Add(user.AltCharacterName2.Trim());
                    details.Add(new { name = user.AltCharacterName2.Trim(), jobLevels = user.Alt2JobLevels });
                }
                return JsonSerializer.Serialize(new { characterName = link.CharacterName, alts, altDetails = details, jobLevels = user?.JobLevels });
            })
            .ToList();

        var ok = Assert.IsType<OkObjectResult>(await NewAddonController(db, linkshellId).GetRosterAsync(CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var newRows = json.RootElement.GetProperty("roster").EnumerateArray().Select(row => row.GetRawText()).ToList();

        Assert.Equal(oldRows.Count, newRows.Count);
        for (var i = 0; i < oldRows.Count; i++)
        {
            if (oldRows[i].Contains("\"characterName\":\"Twin\"", StringComparison.Ordinal))
            {
                Assert.Equal("{\"characterName\":\"Twin\",\"alts\":[],\"altDetails\":[],\"jobLevels\":null}", newRows[i]);
                continue;
            }
            Assert.Equal(oldRows[i], newRows[i]);
        }
        Assert.Contains(newRows, row => row.Contains("\"jobLevels\":[0,40]", StringComparison.Ordinal));
        Assert.Equal(
            Roster.Where(r => !string.IsNullOrWhiteSpace(r.Roster)).Select(r => r.Roster).OrderBy(n => n, StringComparer.Ordinal),
            json.RootElement.GetProperty("characterNames").EnumerateArray().Select(e => e.GetString()).OrderBy(n => n, StringComparer.Ordinal));
    }

    private async Task<int> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        var other = new Linkshell { LinkshellName = "Other" };
        db.Linkshells.AddRange(linkshell, other);
        foreach (var member in Roster)
        {
            if (member.Id is not null)
            {
                db.Users.Add(new AppUser
                {
                    Id = member.Id, UserName = member.Id, IsPlaceholder = member.Placeholder,
                    CharacterName = member.Main, AltCharacterName1 = member.Alt1, AltCharacterName2 = member.Alt2,
                    JobLevels = member.Id == "u-bob" ? new[] { 0, 75 } : null,
                    Alt1JobLevels = member.Id == "u-bob" ? new[] { 0, 40 } : null,
                    Alt2JobLevels = member.Id == "u-robert" ? new[] { 1 } : null,
                });
            }
            db.AppUserLinkshells.Add(new AppUserLinkshell { Linkshell = linkshell, AppUserId = member.Id, CharacterName = member.Roster });
        }
        db.Users.Add(new AppUser { Id = Elsewhere, UserName = Elsewhere, CharacterName = "Bob", AltCharacterName1 = "Zed" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Linkshell = other, AppUserId = Elsewhere, CharacterName = "Bob" });
        await db.SaveChangesAsync();
        return linkshell.Id;
    }

    // Every name on the roster, as typed and with other capitals and padding, plus misses.
    private static List<string?> Probes()
    {
        var names = Roster
            .SelectMany(r => new[] { r.Main, r.Alt1, r.Alt2, r.Roster })
            .Append("Zed")
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();
        var probes = new List<string?> { null, "", "   ", "Nobody", "bo", "al", "a" };
        foreach (var name in names)
        {
            probes.Add(name);
            probes.Add(name.Trim());
            probes.Add(name.ToUpperInvariant());
            probes.Add(name.ToLowerInvariant());
            probes.Add("  " + name.Trim() + "\t");
        }
        return probes.Distinct().ToList();
    }

    private static (int? MembershipId, CharacterNameSlot? Slot) Answer(CharacterResolver<AppUserLinkshell> resolver, string? name) =>
        resolver.TryResolve(name, out var match) ? (match.Member.Id, match.Slot) : (null, null);

    private static string NameSet(RosterNameCandidate candidate) => string.Join("|",
        new[] { candidate.AccountCharacterName, candidate.AltCharacterName1, candidate.AltCharacterName2 }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim().ToLowerInvariant())
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal));

    private static AddonApiController NewAddonController(ApplicationDbContext db, int linkshellId)
    {
        var http = new DefaultHttpContext();
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1, LinkshellId = linkshellId, TokenPrefix = "att_1", TokenHash = "hash-1", CreatedAt = DateTime.UtcNow,
        };
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = linkshellId;
        return new AddonApiController(db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }
}
