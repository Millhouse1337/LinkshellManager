using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The launcher URL comes from server config and is handed to every visitor, signed in or not, so
// only an absolute https:// address is used; anything else keeps the link off.
public class LauncherDownloadPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_IsNull(string? url)
    {
        Assert.Null(LauncherDownloadPolicy.Validate(url));
    }

    [Fact]
    public void Https_IsUsed_Trimmed()
    {
        Assert.Equal("https://example.com/setup.exe", LauncherDownloadPolicy.Validate("  https://example.com/setup.exe "));
    }

    [Theory]
    [InlineData("http://example.com/setup.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/download/launcher")]
    [InlineData("example.com/setup.exe")]
    public void AnythingElse_IsNull(string url)
    {
        Assert.Null(LauncherDownloadPolicy.Validate(url));
    }
}
