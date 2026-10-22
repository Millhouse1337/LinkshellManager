using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The launcher URL is shown to every visitor, signed in or not, so only an absolute https://
// address may be saved; an empty box clears it.
public class LauncherDownloadPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Clears(string? url)
    {
        Assert.Equal(LauncherUrlOutcome.Clear, LauncherDownloadPolicy.Check(url, out var normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void Https_IsSaved_Trimmed()
    {
        Assert.Equal(LauncherUrlOutcome.Save, LauncherDownloadPolicy.Check("  https://example.com/setup.exe ", out var normalized));
        Assert.Equal("https://example.com/setup.exe", normalized);
    }

    [Theory]
    [InlineData("http://example.com/setup.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/download/launcher")]
    [InlineData("example.com/setup.exe")]
    public void AnythingElse_IsInvalid(string url)
    {
        Assert.Equal(LauncherUrlOutcome.Invalid, LauncherDownloadPolicy.Check(url, out _));
    }
}
