using System;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// contracts-11: the addon's event list query, on real Postgres. Postgres sorts NULLs FIRST in a
// descending order, which is what put queued boards ahead of live camps before; the in-memory
// tests cannot see that. Also checks the type filter translates to SQL.
[Collection("postgres")]
public class AddonCampListTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public AddonCampListTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task OnlyHnmCampsThatHaveNotEnded_LiveFirst_ThenTheCap()
    {
        var start = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        int linkshellId, live, queued;
        await using (var db = _pg.CreateContext())
        {
            var ls = new Linkshell { LinkshellName = "LS" };
            var other = new Linkshell { LinkshellName = "Other" };
            db.Linkshells.AddRange(ls, other);
            await db.SaveChangesAsync();
            linkshellId = ls.Id;

            // Newer timed events than the camps, more than the cap.
            for (var i = 0; i < AddonCampList.MaxRows + 5; i++)
            {
                db.Events.Add(new Event { LinkshellId = ls.Id, EventName = $"Sky {i}", EventType = "Sky", StartTime = start.AddDays(1).AddHours(i) });
            }
            var queuedCamp = new Event { LinkshellId = ls.Id, EventName = "Fafnir", EventType = " hnm ", StartTime = start.AddDays(3) };
            var liveCamp = new Event { LinkshellId = ls.Id, EventName = "Behemoth", EventType = "HNM", StartTime = start, CommencementStartTime = start };
            db.Events.AddRange(
                queuedCamp,
                liveCamp,
                new Event { LinkshellId = ls.Id, EventName = "Ended", EventType = "HNM", StartTime = start, CommencementStartTime = start, EndTime = start.AddHours(1) },
                new Event { LinkshellId = other.Id, EventName = "Other shell", EventType = "HNM", StartTime = start });
            await db.SaveChangesAsync();
            (live, queued) = (liveCamp.Id, queuedCamp.Id);
        }

        await using (var db = _pg.CreateContext())
        {
            var ids = await AddonCampList.Query(db.Events.AsNoTracking(), linkshellId)
                .Select(evt => evt.Id)
                .ToListAsync();

            Assert.Equal(new[] { live, queued }, ids);
        }
    }
}
