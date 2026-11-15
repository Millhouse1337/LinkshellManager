using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LinkshellManager.Tests;

// Every officer's addon reports the same alliance, often at the same moment. Two heartbeats both
// find no row for a new name and both add one; on Postgres the one-row-per-name index refuses the
// later save. The heartbeat now re-runs: it finds the row the other saved and updates it, so the
// reporter gets an answer instead of an error and the table still has one row per name.
//
// In-memory has no unique index, so the race is staged: just before this heartbeat's first save,
// another heartbeat saves the same name, and this save is refused the way Postgres refuses it.
public class PresenceRaceRetryTests
{
    private const int Ls = 7;

    private static ApplicationDbContext NewDb(string name, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .AddInterceptors(interceptors)
            .Options);

    private static AddonApiController NewAddonController(ApplicationDbContext db)
    {
        var http = new DefaultHttpContext();
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1, LinkshellId = Ls, IssuedToAppUserId = "u1", TokenPrefix = "att_1", TokenHash = "hash-1",
            CreatedAt = DateTime.UtcNow,
        };
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = Ls;
        // Presence uses only the database.
        return new AddonApiController(
            db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    [Fact]
    public async Task A_heartbeat_that_loses_the_race_updates_the_winners_row()
    {
        var name = Guid.NewGuid().ToString();
        await using (var seed = NewDb(name))
        {
            seed.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS" });
            await seed.SaveChangesAsync();
        }

        var race = new OtherHeartbeatFirst(async () =>
        {
            await using var other = NewDb(name);
            other.LinkshellPresences.Add(new LinkshellPresence
            {
                LinkshellId = Ls, CharacterName = "Newcomer", AllianceNumber = 2, LastSeenUtc = DateTime.UtcNow,
            });
            await other.SaveChangesAsync();
        });

        await using var db = NewDb(name, race);
        var result = await NewAddonController(db).PostPresenceAsync(
            new AddonApiController.AddonPresenceRequest(
                "Newcomer", null, null, 1,
                [new AddonApiController.AddonPresenceMemberDto("Newcomer", 230, "WAR", 75, "NIN", 37)]),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, (int)ok.Value!.GetType().GetProperty("accepted")!.GetValue(ok.Value)!);
        Assert.Equal(2, race.Saves);

        await using var check = NewDb(name);
        var row = Assert.Single(check.LinkshellPresences);
        Assert.Equal("Newcomer", row.CharacterName);
        Assert.Equal(1, row.AllianceNumber);
        Assert.Equal("WAR", row.MainJob);
    }

    // Before the first save: lets the other heartbeat save, then refuses this save as Postgres would.
    private sealed class OtherHeartbeatFirst : SaveChangesInterceptor
    {
        private readonly Func<Task> _other;

        public OtherHeartbeatFirst(Func<Task> other) => _other = other;

        public int Saves { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Saves == 1)
            {
                await _other();
                throw new DbUpdateException("save failed", new PostgresException(
                    "duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation,
                    tableName: "LinkshellPresences"));
            }
            return result;
        }
    }
}
