using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the attendance clock a new /signup row gets. It used to copy the event's
// commencement time, so signing up in the last minute of a 4-hour event earned all
// 4 hours. Now a signup on a live event is a late join that starts NOW, like quick-join.
public class EventSignupStartTimeTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 20, 0, 0, DateTimeKind.Utc);

    private static AppUserEvent Row(DateTime? commencedAt) => ActivityDataController.NewSignupRow(
        "user-1",
        new Event { Id = 42, LinkshellId = 7, EventName = "Sky", CommencementStartTime = commencedAt },
        "Mainchar",
        "WAR",
        "NIN",
        "Tank",
        Now);

    [Fact]
    public void SignupBeforeStart_LeavesTheClockForTheStartToStamp()
    {
        var row = Row(commencedAt: null);

        Assert.Null(row.StartTime);
        Assert.False(row.IsQuickJoin);
    }

    [Fact]
    public void SignupAfterStart_StartsTheClockNow_NotAtTheEventStart()
    {
        var row = Row(commencedAt: Now.AddHours(-4));

        Assert.Equal(Now, row.StartTime);
        Assert.True(row.IsQuickJoin);
    }

    [Fact]
    public void SignupRow_CarriesTheMemberAndJob()
    {
        var row = Row(commencedAt: null);

        Assert.Equal("user-1", row.AppUserId);
        Assert.Equal(42, row.EventId);
        Assert.Equal("Mainchar", row.CharacterName);
        Assert.Equal("WAR", row.JobName);
        Assert.Equal("NIN", row.SubJobName);
        Assert.Equal("Tank", row.JobType);
        Assert.Equal<double?>(0, row.EventDkp);
    }
}
