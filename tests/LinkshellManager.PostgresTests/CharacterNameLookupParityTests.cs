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

// Round 5 (names, read switch): the name lookups stopped reading the membership's roster name
// (AppUserLinkshell.CharacterName). Every roster on production has that name equal to the
// account's main (pre-check B2: 0 differences), and while it is, which member a name belongs to
// must not change. So every lookup is run both ways over a messy roster and compared: the old way
// (a test-only copy of the four-slot resolver: roster name, then the account columns) and the new
// way (the account's main, then its Characters alts).
//
// The roster has: three accounts sharing "Bob" (one a placeholder), capitals and padding that differ
// (" bob ", tabs, a no-break and an ideographic space), an alt that is another member's main, an alt
// that is another member's alt, a main repeated by both alts of the same account, blank alts, a
// blank main, a membership with no account, and an account in another linkshell with the same name.
// Every roster name is its account's main, exactly as saved.
[Collection("postgres")]
public class CharacterNameLookupParityTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public CharacterNameLookupParityTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // (account, placeholder, main, alt 1, alt 2). The roster name is the main. A null account is a
    // membership with no account, whose roster name is "Ghost".
    private static readonly (string? Id, bool Placeholder, string? Main, string? Alt1, string? Alt2)[] Roster =
    {
        ("u-bob", false, "Bob", "Bobalt", null),
        ("u-robert", false, " bob ", null, "Bobalt"),
        ("u-ph", true, "Bob", "\tcarl", null),
        ("u-twin", false, "Twin", "twin", "TWIN "),
        ("u-dee", false, "Dee", "   ", ""),
        ("u-eve", false, "Eve", null, null),
        ("u-fay", false, null, "Fay", null),
        ("u-gus", false, "Gus", "dee", "Carl"),
        ("u-hal", false, " Hal　", "Hal Two", "Halthree"),
        (null, false, null, null, null),
    };

    private const string Ghost = "Ghost";
    private const string Elsewhere = "u-other-ls";

    [Fact]
    public async Task EveryLookup_GivesTheSameAnswer_WithoutTheRosterName()
    {
        var linkshellId = await SeedAsync();
        var probes = Probes();

        await using var db = _pg.CreateContext();

        // CharacterResolver: the old four-slot rule over the memberships and the account columns.
        var memberships = await db.AppUserLinkshells.AsNoTracking()
            .Include(link => link.AppUser)
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .OrderBy(link => link.Id)
            .ToListAsync();
        var oldResolver = OldFourSlotResolver.Build(memberships);
        var newResolver = await CharacterResolver.LoadMembershipsAsync(db, linkshellId, CancellationToken.None);
        foreach (var probe in probes)
        {
            Assert.Equal(oldResolver.Answer(probe), Answer(newResolver, probe));
        }
        // The roster really is messy: some names resolve through an alt, some through the main.
        Assert.Contains(probes, probe => newResolver.TryResolve(probe, out var m) && m.Slot == CharacterNameSlot.Alt2);
        Assert.Contains(probes, probe => newResolver.TryResolve(probe, out var m) && m.Slot == CharacterNameSlot.Alt1);
        Assert.Contains(probes, probe => newResolver.TryResolve(probe, out var m) && m.Slot == CharacterNameSlot.Account);
        // "dee" is u-dee's main and u-gus's alt: the main wins, as the roster name did before.
        Assert.Equal("u-dee", newResolver.Resolve("dee")?.AppUserId);

        // LinkshellRosterNames: the old candidates carried the roster name as a fourth name.
        var oldCandidates = await db.AppUserLinkshells.AsNoTracking()
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .Join(db.Users.AsNoTracking(), link => link.AppUserId, user => user.Id,
                (link, user) => new { link.AppUserId, Names = new[] { link.CharacterName, user.CharacterName, user.AltCharacterName1, user.AltCharacterName2 } })
            .ToListAsync();
        var newCandidates = await LinkshellRosterNames.LoadCandidatesAsync(db, linkshellId, CancellationToken.None);
        Assert.Equal(
            oldCandidates.Select(c => c.AppUserId).OrderBy(id => id, StringComparer.Ordinal),
            newCandidates.Select(c => c.AppUserId).OrderBy(id => id, StringComparer.Ordinal));
        foreach (var candidate in newCandidates)
        {
            var old = oldCandidates.Single(c => c.AppUserId == candidate.AppUserId);
            Assert.Equal(NameSet(old.Names), NameSet(new[] { candidate.AccountCharacterName, candidate.AltCharacterName1, candidate.AltCharacterName2 }));
        }

        var oldSet = new HashSet<string>(
            oldCandidates.SelectMany(c => c.Names).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var newSet = await LinkshellRosterNames.LoadAsync(db, linkshellId, CancellationToken.None);
        Assert.Equal(oldSet.Count, newSet.Count);
        foreach (var probe in probes)
        {
            Assert.Equal(!string.IsNullOrWhiteSpace(probe) && oldSet.Contains(probe.Trim()), newSet.Contains(probe));
            Assert.Equal(
                OldSearch(oldCandidates.Select(c => (c.AppUserId, c.Names)), probe),
                RosterMemberSearch.Find(newCandidates, probe).Select(c => c.AppUserId).OrderBy(id => id, StringComparer.Ordinal));
        }
        Assert.DoesNotContain(newCandidates, c => c.AppUserId == Elsewhere);
        Assert.False(newSet.Contains(Ghost));
    }

    // The addon's /roster: the same rows, names and job levels as when it listed roster names,
    // except that the membership with no account (production has none) is no longer listed, and an
    // alt repeating the same account's main (u-twin's two) is not listed either (it has no
    // Characters row; that already held before this step).
    [Fact]
    public async Task AddonRoster_ListsTheSameMembersByTheirMain()
    {
        var linkshellId = await SeedAsync();

        await using var db = _pg.CreateContext();
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(user => user.Id);
        var oldRows = (await db.AppUserLinkshells.AsNoTracking()
                .Where(link => link.LinkshellId == linkshellId && !string.IsNullOrWhiteSpace(link.CharacterName))
                .OrderBy(link => link.CharacterName)
                .Select(link => new { link.CharacterName, link.AppUserId })
                .ToListAsync())
            .Where(link => link.AppUserId is not null)
            .Select(link =>
            {
                var user = users[link.AppUserId!];
                var alts = new List<string>();
                var details = new List<object>();
                if (!string.IsNullOrWhiteSpace(user.AltCharacterName1))
                {
                    alts.Add(user.AltCharacterName1.Trim());
                    details.Add(new { name = user.AltCharacterName1.Trim(), jobLevels = user.Alt1JobLevels });
                }
                if (!string.IsNullOrWhiteSpace(user.AltCharacterName2))
                {
                    alts.Add(user.AltCharacterName2.Trim());
                    details.Add(new { name = user.AltCharacterName2.Trim(), jobLevels = user.Alt2JobLevels });
                }
                return JsonSerializer.Serialize(new { characterName = link.CharacterName, alts, altDetails = details, jobLevels = user.JobLevels });
            })
            .ToList();

        var ok = Assert.IsType<OkObjectResult>(await NewAddonController(db, linkshellId).GetRosterAsync(CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var newRows = json.RootElement.GetProperty("roster").EnumerateArray().Select(row => row.GetRawText()).ToList();

        // Compared as sets: two rows share the name "Bob", and the order of a tie is not fixed.
        var expected = oldRows
            .Select(row => row.Contains("\"characterName\":\"Twin\"", StringComparison.Ordinal)
                ? "{\"characterName\":\"Twin\",\"alts\":[],\"altDetails\":[],\"jobLevels\":null}"
                : row)
            .OrderBy(row => row, StringComparer.Ordinal);
        Assert.Equal(expected, newRows.OrderBy(row => row, StringComparer.Ordinal));
        Assert.Contains(newRows, row => row.Contains("\"jobLevels\":[0,40]", StringComparison.Ordinal));
        Assert.Contains(newRows, row => row.Contains("\"jobLevels\":[0,75]", StringComparison.Ordinal));
        Assert.Equal(
            Roster.Where(r => r.Id is not null && !string.IsNullOrWhiteSpace(r.Main)).Select(r => r.Main).OrderBy(n => n, StringComparer.Ordinal),
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
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                Linkshell = linkshell, AppUserId = member.Id, CharacterName = member.Id is null ? Ghost : member.Main,
            });
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
            .SelectMany(r => new[] { r.Main, r.Alt1, r.Alt2 })
            .Append("Zed")
            .Append(Ghost)
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

    // The member and the slot, with the old roster-name slot folded into the main's: the two held
    // the same name.
    private static (int? MembershipId, CharacterNameSlot? Slot) Answer(CharacterResolver<AppUserLinkshell> resolver, string? name) =>
        resolver.TryResolve(name, out var match) ? (match.Member.Id, match.Slot) : (null, null);

    private static string NameSet(IEnumerable<string?> names) => string.Join("|",
        names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim().ToLowerInvariant())
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal));

    // RosterMemberSearch.Find as it was: exact over the four names first, else containing.
    private static IEnumerable<string?> OldSearch(IEnumerable<(string? AppUserId, string?[] Names)> members, string? typed)
    {
        var query = (typed ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            return Array.Empty<string?>();
        }
        var roster = members
            .Select(m => (m.AppUserId, Names: m.Names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).ToList()))
            .ToList();
        var exact = roster.Where(m => m.Names.Any(n => string.Equals(n, query, StringComparison.OrdinalIgnoreCase))).ToList();
        var found = exact.Count > 0
            ? exact
            : roster.Where(m => m.Names.Any(n => n.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        return found.Select(m => m.AppUserId).OrderBy(id => id, StringComparer.Ordinal);
    }

    // A test-only copy of the resolver as it was before Round 5: four slots in order -- the roster
    // name, the account's main, alt 1, alt 2 -- first slot then oldest membership wins. Its answers
    // are mapped to the new slots: the roster-name slot and the main's are the same name.
    private sealed class OldFourSlotResolver
    {
        private readonly Dictionary<string, (int Id, CharacterNameSlot Slot)> _byName =
            new(StringComparer.OrdinalIgnoreCase);

        public static OldFourSlotResolver Build(IEnumerable<AppUserLinkshell> memberships)
        {
            var list = memberships.Where(m => !string.IsNullOrWhiteSpace(m.AppUserId)).OrderBy(m => m.Id).ToList();
            var resolver = new OldFourSlotResolver();
            var slots = new (Func<AppUserLinkshell, string?> Name, CharacterNameSlot Slot)[]
            {
                (m => m.CharacterName, CharacterNameSlot.Account),
                (m => m.AppUser?.CharacterName, CharacterNameSlot.Account),
                (m => m.AppUser?.AltCharacterName1, CharacterNameSlot.Alt1),
                (m => m.AppUser?.AltCharacterName2, CharacterNameSlot.Alt2),
            };
            foreach (var (name, slot) in slots)
            {
                foreach (var membership in list)
                {
                    var key = CharacterResolver.Normalize(name(membership));
                    if (key is not null)
                    {
                        resolver._byName.TryAdd(key, (membership.Id, slot));
                    }
                }
            }
            return resolver;
        }

        public (int? MembershipId, CharacterNameSlot? Slot) Answer(string? name)
        {
            var key = CharacterResolver.Normalize(name);
            return key is not null && _byName.TryGetValue(key, out var hit) ? (hit.Id, hit.Slot) : (null, null);
        }
    }

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
