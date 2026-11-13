using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LinkshellManager.Tests;

// A party setup is a reusable template: members sign up on an EVENT's board only.
// Sign-ups made on the template (the ToDs tab panel) never reached the event or
// the Discord board, so the template sign-up path was removed (dead-hnm-03).
public class PartySetupTemplateViewOnlyTests
{
    [Fact]
    public void Template_has_no_signup_or_withdraw_endpoint()
    {
        var templates = typeof(ActivityDataController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>())
            .Select(a => a.Template ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(templates, t => t.StartsWith("party-setups/") && t.Contains("/slots/"));
        // The event board's own sign-up is the one that stays.
        Assert.Contains(templates, t => t.StartsWith("events/") && t.EndsWith("/signup") && t.Contains("slots"));
    }

    [Fact]
    public async Task Template_detail_hides_leftover_signups()
    {
        await using var db = await TodCallers.SeededAsync();
        var slot = TodCallers.SignedUpSetup(db, "Cerberus"); // a sign-up stored before the change
        await db.SaveChangesAsync();
        var setupId = db.PartySetups.Single().Id;

        var result = await TodCallers.App(db).GetPartySetupAsync(setupId, CancellationToken.None);

        var json = JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>(result).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var returned = json.GetProperty("alliances")[0].GetProperty("parties")[0].GetProperty("slots")[0];
        Assert.Equal(slot.Id, returned.GetProperty("slotId").GetInt32());
        Assert.Equal(JsonValueKind.Null, returned.GetProperty("signedUpAppUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, returned.GetProperty("signedUpCharacterName").ValueKind);
    }
}
