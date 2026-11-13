using System;
using System.IO;
using LinkshellManagerDiscordApp.Controllers;
using Xunit;

namespace LinkshellManager.Tests;

// A window post is stamped with the officer's PC clock only when that clock agrees with the
// server's. It used to be taken as-is, so a PC a day off filed the window (and a camp it started)
// a day off.
public class AddonPostClockTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 21, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ACloseClock_IsKept()
    {
        var sent = Now.AddSeconds(-20);

        Assert.Equal(sent, AddonApiController.TrustedPostTimeUtc(sent, Now));
    }

    [Theory]
    [InlineData(-24 * 60)]  // a day behind
    [InlineData(-6)]        // just past the tolerance
    [InlineData(6)]         // ahead
    [InlineData(24 * 60)]   // a day ahead
    public void AWrongClock_UsesTheServersTime(int minutesOff)
    {
        Assert.Equal(Now, AddonApiController.TrustedPostTimeUtc(Now.AddMinutes(minutesOff), Now));
    }

    [Fact]
    public void NoTimeSent_UsesTheServersTime()
    {
        Assert.Equal(Now, AddonApiController.TrustedPostTimeUtc(null, Now));
    }

    // Wiring: the window-post route reads the clock through the check, not straight off the request.
    [Fact]
    public void PostAttendance_ChecksTheSentTime()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "AddonApiController.AddonParticipation.cs"));

        Assert.Contains("var nowUtc = TrustedPostTimeUtc(request.RecordedAtUtc, DateTime.UtcNow);", text);
        Assert.DoesNotContain("request.RecordedAtUtc ?? DateTime.UtcNow", text);
    }

    private static string FindRepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate '{relative}' above {AppContext.BaseDirectory}.");
    }
}
