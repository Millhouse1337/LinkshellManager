using System;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// dead-hnm-02: a member's ToD waits in PendingTodSubmissions until an officer approves it, and that
// table was a partial copy of Tods. An HQ kill (Nidhogg, Aspidochelone, King Behemoth) came out of
// approval as the NQ, so the next board's day cycle never went back to Day 1; the pop window logged
// in the app and the monster's Additional Seconds were dropped too. The queue now keeps all three.
public class PendingTodHqAndSecondsTests
{
    private static readonly DateTime KilledAt = new(2026, 11, 5, 13, 0, 0, DateTimeKind.Utc);

    // The finding's test: a member logs Nidhogg, and the approved ToD is HQ.
    [Fact]
    public async Task AMembersNidhogg_FromTheApp_IsApprovedAsHq()
    {
        await using var db = await TodCallers.SeededAsync(canManageTods: false);
        db.LinkshellMonsterTimings.Add(TodCallers.Setup("Fafnir/Nidhogg", 22 * 60, additionalSeconds: 12));
        await db.SaveChangesAsync();

        var queued = await TodCallers.AppPostAsync(db, TodCallers.AppRequest("Nidhogg", KilledAt, popWindow: 3));
        Assert.True(queued.GetProperty("pending").GetBoolean());

        var pending = await db.PendingTodSubmissions.SingleAsync();
        Assert.True(pending.Hq);
        Assert.Equal(3, pending.PopWindow);
        Assert.Equal(12, pending.AdditionalSeconds);

        await TodCallers.Approvals(db).ApproveTodAsync(pending.Id, CancellationToken.None);

        var tod = await db.Tods.SingleAsync();
        Assert.True(tod.Hq);
        Assert.Equal(3, tod.PopWindow);
        Assert.Equal(12, tod.AdditionalSeconds);
        Assert.Equal(KilledAt.AddHours(22).AddSeconds(12), tod.RepopTime);
    }

    // The addon posts the half that died by name, with no HQ toggle.
    [Fact]
    public async Task AMembersNidhogg_FromTheAddon_IsApprovedAsHq()
    {
        await using var db = await TodCallers.SeededAsync(canManageTods: false);
        db.LinkshellMonsterTimings.Add(TodCallers.Setup("Fafnir/Nidhogg", 22 * 60, additionalSeconds: 12));
        await db.SaveChangesAsync();

        await TodCallers.AddonPostAsync(db, "Nidhogg", KilledAt, popWindow: 2);

        var pending = await db.PendingTodSubmissions.SingleAsync();
        Assert.True(pending.Hq);
        Assert.Equal(12, pending.AdditionalSeconds);

        await TodCallers.Approvals(db).ApproveTodAsync(pending.Id, CancellationToken.None);

        var tod = await db.Tods.SingleAsync();
        Assert.True(tod.Hq);
        Assert.Equal(2, tod.PopWindow);
        Assert.Equal(12, tod.AdditionalSeconds);
    }

    [Fact]
    public async Task AMembersFafnir_StaysNq()
    {
        await using var db = await TodCallers.SeededAsync(canManageTods: false);

        await TodCallers.AppPostAsync(db, TodCallers.AppRequest("Fafnir", KilledAt));
        var pending = await db.PendingTodSubmissions.SingleAsync();
        await TodCallers.Approvals(db).ApproveTodAsync(pending.Id, CancellationToken.None);

        Assert.False(pending.Hq);
        Assert.False((await db.Tods.SingleAsync()).Hq);
    }

    // A reviewer who corrects the monster has said which one died, and the editor has no HQ box: the
    // new name decides HQ. Leaving the name alone keeps what the member submitted.
    [Theory]
    [InlineData("Nidhogg", true)]          // unchanged: the member's HQ stands
    [InlineData("Fafnir/Nidhogg", false)]  // the combined label names no half: NQ, as before Hq was kept
    [InlineData("Fafnir", false)]
    [InlineData("Cerberus", false)]
    public async Task AReviewerWhoChangesTheMonster_DecidesHqByTheNewName(string reviewed, bool expectedHq)
    {
        await using var db = await TodCallers.SeededAsync(canManageTods: false);
        await TodCallers.AddonPostAsync(db, "Nidhogg", KilledAt);
        var pending = await db.PendingTodSubmissions.SingleAsync();
        Assert.True(pending.Hq);

        await TodCallers.Approvals(db).ApproveTodWithEditsAsync(
            TodCallers.LinkshellId, pending.Id,
            new PendingTodEdits(reviewed, null, null, KilledAt, null, null, pending.RepopTime),
            CancellationToken.None);

        Assert.Equal(expectedHq, (await db.Tods.SingleAsync()).Hq);
    }

    // The app queues a member's Nidhogg as "Fafnir/Nidhogg" + HQ; a reviewer who keeps that name keeps HQ.
    [Fact]
    public async Task AReviewerWhoKeepsTheCombinedName_KeepsTheMembersHq()
    {
        await using var db = await TodCallers.SeededAsync(canManageTods: false);
        await TodCallers.AppPostAsync(db, TodCallers.AppRequest("Nidhogg", KilledAt));
        var pending = await db.PendingTodSubmissions.SingleAsync();
        Assert.Equal("Fafnir/Nidhogg", pending.MonsterName);

        await TodCallers.Approvals(db).ApproveTodWithEditsAsync(
            TodCallers.LinkshellId, pending.Id,
            new PendingTodEdits("Fafnir/Nidhogg", null, null, KilledAt, null, null, pending.RepopTime),
            CancellationToken.None);

        Assert.True((await db.Tods.SingleAsync()).Hq);
    }

    // A reviewer who blanks the repop gets it worked out again, with the seconds this time.
    [Fact]
    public async Task ABlankRepop_IsWorkedOutWithTheSeconds()
    {
        await using var db = TodCallers.NewDb();
        var pending = new PendingTodSubmission
        {
            LinkshellId = TodCallers.LinkshellId,
            SubmittedAtUtc = KilledAt,
            MonsterName = "Cerberus",
            Time = KilledAt,
            Cooldown = "48 Hour",
            AdditionalSeconds = 16,
        };
        db.PendingTodSubmissions.Add(pending);
        await db.SaveChangesAsync();

        await TodCallers.Approvals(db).ApproveTodAsync(pending.Id, CancellationToken.None);

        Assert.Equal(KilledAt.AddHours(48).AddSeconds(16), (await db.Tods.SingleAsync()).RepopTime);
    }
}
