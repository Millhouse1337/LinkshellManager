using System.Net;
using LinkshellManagerDiscordApp.Utils;
using Xunit;

namespace LinkshellManager.Tests;

// The server trusts X-Forwarded-For only from Cloudflare's ranges, read from the env file.
// .NET 10's IPNetwork is stricter than the old one, so these pin that every range we used to
// accept is still accepted and still matches the same addresses.
public class TrustedNetworkParserTests
{
    [Theory]
    [InlineData("173.245.48.0/20", "173.245.63.255", true)]
    [InlineData("173.245.48.0/20", "173.245.64.0", false)]
    [InlineData("2606:4700::/32", "2606:4700:10::1", true)]
    [InlineData("2606:4700::/32", "2606:4701::1", false)]
    public void CloudflareRanges_MatchTheirAddresses(string range, string address, bool expected)
    {
        Assert.True(TrustedNetworkParser.TryParse(range, out var network));
        Assert.Equal(expected, network.Contains(IPAddress.Parse(address)));
    }

    [Fact]
    public void ExtraBitsPastThePrefix_AreClearedNotRefused()
    {
        // The old ASP.NET type accepted this; the new System.Net type throws on it.
        Assert.True(TrustedNetworkParser.TryParse("10.1.2.3/8", out var network));
        Assert.Equal("10.0.0.0/8", network.ToString());
        Assert.True(network.Contains(IPAddress.Parse("10.200.0.1")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.0.0.0")]
    [InlineData("not-an-ip/8")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    public void Garbage_IsSkipped(string? text)
    {
        Assert.False(TrustedNetworkParser.TryParse(text, out _));
    }
}
