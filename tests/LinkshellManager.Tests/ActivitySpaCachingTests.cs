using LinkshellManagerDiscordApp.Utils;
using Xunit;

namespace LinkshellManager.Tests;

// After a deploy, a browser that kept an old index.html asked for script files the new build no
// longer has. These pin the rule that prevents it: index.html is always re-checked, hashed files
// are kept for a year, and anything else is left alone.
public class ActivitySpaCachingTests
{
    [Theory]
    [InlineData("index.html")]
    [InlineData("INDEX.HTML")]
    public void IndexHtml_IsAlwaysRechecked(string name)
    {
        Assert.Equal(ActivitySpaCaching.NoCache, ActivitySpaCaching.CacheControlFor(name));
    }

    [Theory]
    [InlineData("main-BFI5SPP7.js")]
    [InlineData("chunk-F2EMGNSV.js")]
    [InlineData("polyfills-5CFQRCPP.js")]
    [InlineData("styles-G53MDH2Y.css")]
    public void HashedBuildFiles_AreKeptForAYear(string name)
    {
        Assert.Equal(ActivitySpaCaching.Immutable, ActivitySpaCaching.CacheControlFor(name));
    }

    [Theory]
    [InlineData("favicon.ico")]
    [InlineData("geist.woff2")]
    [InlineData("main.js")]
    [InlineData("logo-small.png")]
    [InlineData("")]
    public void OtherFiles_KeepTheDefault(string name)
    {
        Assert.Null(ActivitySpaCaching.CacheControlFor(name));
    }
}
