using System.Linq;
using LinkshellManagerDiscordApp.Controllers;
using Xunit;

namespace LinkshellManager.Tests;

// The overview runs on every page load. It used to carry every confirmed treasury entry of the
// primary linkshell (a leftover "revenueEntries" field kept for one release after the treasury
// rewrite), which nothing on screen read. The Treasury tab has its own endpoint, so the overview
// must not grow that field back.
public class OverviewPayloadNoTreasuryJournalTests
{
    [Fact]
    public void PrimaryLinkshellPayload_HasNoTreasuryEntryList()
    {
        var propertyNames = typeof(ActivityPrimaryLinkshellDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain("RevenueEntries", propertyNames);
        Assert.DoesNotContain(propertyNames, name => name.Contains("Journal"));
    }
}
