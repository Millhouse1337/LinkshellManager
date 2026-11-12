using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Claiming a placeholder that has really been used: it went to events, was credited in windows,
// was rated and ticked on a chart. The old claim moved only memberships and DKP rows and then
// deleted the placeholder, so Postgres refused the delete (attendance rows still pointed at it)
// and the claim failed. UserMergeService now moves every account-id column.
//
// The check does not trust that list: after the claim it reads EVERY text column of EVERY table
// and asserts that none holds the placeholder's id any more. A table added later with an account-id
// column that UserMergeService forgets fails here.
[Collection("postgres")]
public class PlaceholderClaimMergeTests : IAsyncLifetime
{
    private const string Real = "real-account";
    private const string Ph = "placeholder-account";

    private readonly PostgresFixture _pg;

    public PlaceholderClaimMergeTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateTime When = new(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ClaimingAPlaceholderWithHistory_LeavesNoReferenceToIt_AndAddsTheBalances()
    {
        int linkshellId, phMembershipId, realMembershipId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.Add(new AppUser { Id = Real, UserName = "real", CharacterName = "Bob" });
            db.Users.Add(new AppUser { Id = Ph, UserName = "ph", CharacterName = "Bobalt", IsPlaceholder = true });
            var realMember = new AppUserLinkshell { Linkshell = linkshell, AppUserId = Real, CharacterName = "Bob", LinkshellDkp = 30 };
            var phMember = new AppUserLinkshell { Linkshell = linkshell, AppUserId = Ph, CharacterName = "Bobalt", LinkshellDkp = 100 };
            db.AppUserLinkshells.AddRange(realMember, phMember);
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;
            phMembershipId = phMember.Id;
            realMembershipId = realMember.Id;

            // Balances with the ledger rows that explain them: Bob earned 30, the import gave
            // Bobalt 100.
            db.DkpLedgerEntries.Add(new DkpLedgerEntry { LinkshellId = linkshellId, AppUserId = Real, EntryType = "EventEarned", Amount = 30, OccurredAt = When });
            db.DkpLedgerEntries.Add(new DkpLedgerEntry { LinkshellId = linkshellId, AppUserId = Ph, EntryType = "SheetImport", Amount = 100, OccurredAt = When });

            // One live event both accounts signed up for (dual-boxing), and one only the
            // placeholder went to.
            var shared = new Event { LinkshellId = linkshellId, EventName = "Dynamis", EventType = "Dynamis" };
            var solo = new Event { LinkshellId = linkshellId, EventName = "Sky", EventType = "Sky" };
            db.Events.AddRange(shared, solo);
            await db.SaveChangesAsync();
            var realSignup = new AppUserEvent { EventId = shared.Id, AppUserId = Real, CharacterName = "Bob" };
            var phSignup = new AppUserEvent { EventId = shared.Id, AppUserId = Ph, CharacterName = "Bobalt" };
            var phSolo = new AppUserEvent { EventId = solo.Id, AppUserId = Ph, CharacterName = "Bobalt" };
            db.AppUserEvents.AddRange(realSignup, phSignup, phSolo);
            await db.SaveChangesAsync();
            db.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
            {
                AppUserEventId = phSignup.Id, EventId = shared.Id, AppUserId = Ph, ActionType = "Join", OccurredAt = When,
            });

            // Window credits: window 1 credited both (one person, counted twice); window 2 only
            // the placeholder.
            var window1 = new EventAttendanceWindow { EventId = shared.Id, SequenceNumber = 1, PostedAt = When };
            var window2 = new EventAttendanceWindow { EventId = shared.Id, SequenceNumber = 2, PostedAt = When };
            db.EventAttendanceWindows.AddRange(window1, window2);
            await db.SaveChangesAsync();
            db.AppUserEventWindows.AddRange(
                new AppUserEventWindow { EventAttendanceWindow = window1, AppUserEventId = realSignup.Id, AppUserId = Real, VerifiedAt = When },
                new AppUserEventWindow { EventAttendanceWindow = window1, AppUserEventId = phSignup.Id, AppUserId = Ph, VerifiedAt = When },
                new AppUserEventWindow { EventAttendanceWindow = window2, AppUserEventId = phSignup.Id, AppUserId = Ph, VerifiedAt = When });

            // A past event both attended.
            var history = new EventHistory { LinkshellId = linkshellId, EventName = "Old Dynamis", EventType = "Dynamis" };
            db.EventHistories.Add(history);
            await db.SaveChangesAsync();
            db.AppUserEventHistories.AddRange(
                new AppUserEventHistory { EventHistoryId = history.Id, AppUserId = Real, EventDkp = 5 },
                new AppUserEventHistory { EventHistoryId = history.Id, AppUserId = Ph, EventDkp = 7 });

            // Chart ticks on the placeholder's MEMBERSHIP (by id), one the real member also has.
            db.ChartMemberKeyItems.AddRange(
                new ChartMemberKeyItem { LinkshellId = linkshellId, Board = "Sky", KeyItemName = "A", MembershipId = realMembershipId, CharacterName = "Bob" },
                new ChartMemberKeyItem { LinkshellId = linkshellId, Board = "Sky", KeyItemName = "A", MembershipId = phMembershipId, CharacterName = "Bobalt", SetByAppUserId = Ph },
                new ChartMemberKeyItem { LinkshellId = linkshellId, Board = "Sky", KeyItemName = "B", MembershipId = phMembershipId, CharacterName = "Bobalt" });

            // Ratings, a Claim Shield capture, loot won, a notification.
            db.JobRatings.Add(new JobRating { LinkshellId = linkshellId, TargetAppUserId = Ph, RaterAppUserId = Real, JobIndex = 1, UpdatedAt = When });
            var capture = new ClaimShieldCapture { LinkshellId = linkshellId, MonsterName = "Fafnir", CapturedAtUtc = When, CreatedAtUtc = When };
            db.ClaimShieldCaptures.Add(capture);
            await db.SaveChangesAsync();
            db.ClaimShieldCaptureMembers.AddRange(
                new ClaimShieldCaptureMember { CaptureId = capture.Id, CharacterName = "Bob", AppUserId = Real },
                new ClaimShieldCaptureMember { CaptureId = capture.Id, CharacterName = "Bobalt", AppUserId = Ph });
            db.EventLootDetails.Add(new EventLootDetail { LinkshellId = linkshellId, EventHistoryId = history.Id, ItemWinnerAppUserId = Ph });
            db.Notifications.Add(new Notification { AppUserId = Ph, NotificationType = "Info", CreatedAt = When });
            await db.SaveChangesAsync();
        }

        // Bob asked ("that's me"), and an officer approves: the approval runs the claim.
        await using (var db = _pg.CreateContext())
        {
            var requests = new PlaceholderClaimRequestService(db, new PlaceholderClaimService(db, new UserMergeService(db)));
            Assert.True(await requests.RequestForAccountAsync(linkshellId, Ph, Real, "Bob", CancellationToken.None));
            var requestId = (await db.PlaceholderClaimRequests.AsNoTracking().SingleAsync()).Id;

            var result = await requests.ApproveAsync(linkshellId, requestId, "officer", CancellationToken.None);
            Assert.True(result.Success, result.Error);
        }

        // Nothing anywhere still says the placeholder's id, except the request itself: that row is
        // the record of which placeholder Bob asked for, and is kept on purpose.
        Assert.Equal(
            new[] { "PlaceholderClaimRequests.PlaceholderAppUserId" },
            await FindTextColumnsHoldingAsync(Ph));

        await using (var db = _pg.CreateContext())
        {
            Assert.Null(await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Ph));

            // Balances ADDED (30 + 100), and the ledger still explains the balance.
            var member = await db.AppUserLinkshells.AsNoTracking().SingleAsync();
            Assert.Equal(realMembershipId, member.Id);
            Assert.Equal(130d, member.LinkshellDkp);
            var ledger = await db.DkpLedgerEntries.AsNoTracking().Where(e => e.AppUserId == Real).ToListAsync();
            Assert.Equal(130d, ledger.Sum(e => e.Amount));
            var mergeRow = Assert.Single(ledger, e => e.EntryType == PlaceholderClaimService.MergeEntryType);
            Assert.Equal(0d, mergeRow.Amount);

            // The shared event: one signup kept, with the placeholder's window 2 credit and status row.
            var signups = await db.AppUserEvents.AsNoTracking().OrderBy(p => p.EventId).ToListAsync();
            Assert.Equal(2, signups.Count);
            Assert.All(signups, p => Assert.Equal(Real, p.AppUserId));
            var windows = await db.AppUserEventWindows.AsNoTracking().ToListAsync();
            Assert.Equal(2, windows.Count);
            Assert.Single(windows.Select(w => w.AppUserEventId).Distinct());
            Assert.Equal(signups[0].Id, (await db.AppUserEventStatusLedgers.AsNoTracking().SingleAsync()).AppUserEventId);

            // The past event: one row, holding both rows' DKP.
            var attended = await db.AppUserEventHistories.AsNoTracking().SingleAsync();
            Assert.Equal(12d, attended.EventDkp);

            // Chart ticks: A once, B moved onto the real membership.
            var ticks = await db.ChartMemberKeyItems.AsNoTracking().OrderBy(k => k.KeyItemName).ToListAsync();
            Assert.Equal(new[] { "A", "B" }, ticks.Select(k => k.KeyItemName));
            Assert.All(ticks, k => Assert.Equal(realMembershipId, k.MembershipId));

            Assert.Single(await db.ClaimShieldCaptureMembers.AsNoTracking().ToListAsync());
        }
    }

    // Every (table, column) of a text type whose value equals `id`, as "Table.Column".
    private async Task<List<string>> FindTextColumnsHoldingAsync(string id)
    {
        await using var connection = new NpgsqlConnection(_pg.ConnectionString);
        await connection.OpenAsync();

        var columns = new List<(string Table, string Column)>();
        await using (var list = connection.CreateCommand())
        {
            list.CommandText =
                "SELECT c.table_name, c.column_name FROM information_schema.columns c " +
                "JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name " +
                "WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE' " +
                "AND c.data_type IN ('text', 'character varying')";
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var hits = new List<string>();
        foreach (var (table, column) in columns)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = $"SELECT EXISTS (SELECT 1 FROM \"{table}\" WHERE \"{column}\" = @id)";
            check.Parameters.AddWithValue("id", id);
            if ((bool)(await check.ExecuteScalarAsync())!)
            {
                hits.Add($"{table}.{column}");
            }
        }
        return hits;
    }
}
