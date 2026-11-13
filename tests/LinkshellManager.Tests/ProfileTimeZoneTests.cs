using LinkshellManagerDiscordApp.Utils;
using Xunit;

namespace LinkshellManager.Tests;

// The browser's zone may only fill in a profile time zone nobody chose. Picking UTC used to be
// undone on the next visit because "UTC" was read as "not set".
public class ProfileTimeZoneTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NeverChosen_IsUnset(string? zone)
    {
        Assert.True(ProfileTimeZone.IsUnset(zone));
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("utc")]
    [InlineData("America/New_York")]
    public void AChosenZone_IncludingUtc_IsKept(string zone)
    {
        Assert.False(ProfileTimeZone.IsUnset(zone));
    }
}
