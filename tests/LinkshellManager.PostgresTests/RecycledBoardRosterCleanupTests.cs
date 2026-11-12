using System;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P3.8: the ClearRecycledBoardWindowRosters migration removes "View Previous Window" captures left
// on a recurring HNM board by camps that are over, and nothing the camp now on the board could
// still show.
[Collection("postgres")]
public class RecycledBoardRosterCleanupTests : IAsyncLifetime
{
    private static readonly DateTime LastEndCamp = new(2026, 11, 10, 20, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public RecycledBoardRosterCleanupTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CleanupSql_RemovesOnlyRostersFromCampsThatAreOver()
    {
        int queued, running, runningNoEndCamp, parked, timed, oldRow, newRow;
        await using (var db = _pg.CreateContext())
        {
            var ls = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(ls);
            await db.SaveChangesAsync();

            Event Board(string type = "HNM") => new()
            {
                LinkshellId = ls.Id, EventName = "Tiamat", EventType = type,
                AssignedMonsterName = "Tiamat", HnmWindowNumber = 1,
            };

            // Revived for the next pop and waiting for it: nothing captured yet for that camp.
            var queuedBoard = Board();
            // Live again after an End Camp on LastEndCamp.
            var runningBoard = Board();
            runningBoard.CommencementStartTime = LastEndCamp.AddHours(10);
            // Live, but no End Camp on record to tell its camps apart: left alone.
            var runningNoEndCampBoard = Board();
            runningNoEndCampBoard.CommencementStartTime = LastEndCamp.AddHours(10);
            // Parked as defeated: these rows are the camp that just ended, cleared at its revive.
            var parkedBoard = Board();
            parkedBoard.HnmDefeatedAt = LastEndCamp;
            // Not an HNM board at all.
            var timedEvent = Board("Sky");
            db.Events.AddRange(queuedBoard, runningBoard, runningNoEndCampBoard, parkedBoard, timedEvent);
            await db.SaveChangesAsync();

            db.WindowEvents.AddRange(
                // An older End Camp and the latest one; the latest decides.
                new WindowEvent { LinkshellId = ls.Id, Name = "Tiamat", SourceEventId = runningBoard.Id, CreatedAtUtc = LastEndCamp.AddDays(-3), CampEndedAtUtc = LastEndCamp.AddDays(-3) },
                new WindowEvent { LinkshellId = ls.Id, Name = "Tiamat", SourceEventId = runningBoard.Id, CreatedAtUtc = LastEndCamp, CampEndedAtUtc = LastEndCamp },
                new WindowEvent { LinkshellId = ls.Id, Name = "Tiamat", SourceEventId = parkedBoard.Id, CreatedAtUtc = LastEndCamp, CampEndedAtUtc = LastEndCamp });
            await db.SaveChangesAsync();

            EventWindowRosterSnapshot Roster(Event ev, DateTime capturedAt)
            {
                var row = new EventWindowRosterSnapshot
                {
                    EventId = ev.Id, WindowNumber = 1, CapturedAtUtc = capturedAt, CharacterName = "Millhouse",
                };
                db.EventWindowRosterSnapshots.Add(row);
                return row;
            }

            Roster(queuedBoard, LastEndCamp.AddHours(-2));
            var oldCapture = Roster(runningBoard, LastEndCamp.AddHours(-2));
            var newCapture = Roster(runningBoard, LastEndCamp.AddHours(11));
            Roster(runningNoEndCampBoard, LastEndCamp.AddHours(-2));
            Roster(parkedBoard, LastEndCamp.AddHours(-2));
            Roster(timedEvent, LastEndCamp.AddHours(-2));
            await db.SaveChangesAsync();
            (queued, running, runningNoEndCamp, parked, timed, oldRow, newRow) =
                (queuedBoard.Id, runningBoard.Id, runningNoEndCampBoard.Id, parkedBoard.Id, timedEvent.Id, oldCapture.Id, newCapture.Id);
        }

        await using (var db = _pg.CreateContext())
        {
            var deleted = await db.Database.ExecuteSqlRawAsync(ClearRecycledBoardWindowRosters.CleanupSql);
            Assert.Equal(2, deleted);

            var left = await db.EventWindowRosterSnapshots.AsNoTracking().ToListAsync();
            Assert.DoesNotContain(left, s => s.EventId == queued);
            Assert.DoesNotContain(left, s => s.Id == oldRow);
            Assert.Contains(left, s => s.Id == newRow);
            Assert.Contains(left, s => s.EventId == runningNoEndCamp);
            Assert.Contains(left, s => s.EventId == parked);
            Assert.Contains(left, s => s.EventId == timed);
            Assert.Equal(4, left.Count);
            Assert.Single(left, s => s.EventId == running);
        }
    }
}
