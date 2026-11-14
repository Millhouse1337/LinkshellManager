using System.Net;
using System.Net.Http;
using System.Text.Json;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;
using static LinkshellManager.Tests.ApiPipelineRequests;

namespace LinkshellManager.Tests;

// What the addon's own endpoints send back, through the real app. The addon reads these bodies
// field by field, so these tests pin the shape: what is in it, what was dropped, and which rows.
public sealed class AddonContractPipelineTests : IClassFixture<AddonContractPipelineTests.Host>
{
    public sealed class Host : IAsyncLifetime
    {
        public ApiPipelineHost App { get; } = new();

        public async Task InitializeAsync()
        {
            await App.SeedUserAsync(OfficerId, "200000000000000011");
            await App.SeedAsync(db =>
            {
                db.Linkshells.Add(new Linkshell { Id = OwnLinkshellId, LinkshellName = "Own LS", AppUserId = OfficerId });
                db.Linkshells.Add(new Linkshell { Id = OtherLinkshellId, LinkshellName = "Other LS", AppUserId = OfficerId });
                db.AppUserLinkshells.Add(new AppUserLinkshell
                {
                    AppUserId = OfficerId, LinkshellId = OwnLinkshellId, CharacterName = "Officer",
                    Rank = LinkshellRanks.Leader, Status = "Active",
                });
                db.AddonApiTokens.Add(new AddonApiToken
                {
                    LinkshellId = OwnLinkshellId,
                    IssuedToAppUserId = OfficerId,
                    TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AddonToken))),
                    TokenPrefix = AddonToken[..8],
                    CreatedAt = DateTime.UtcNow,
                });

                // 55 timed events, all newer than the camps. Before the fix they filled the
                // 50-row cap and the addon (which drops non-HNM rows itself) saw no camps at all.
                for (var i = 0; i < 55; i++)
                {
                    db.Events.Add(new Event
                    {
                        Id = 1000 + i,
                        LinkshellId = OwnLinkshellId,
                        EventName = $"Sky run {i}",
                        EventType = "Sky",
                        StartTime = CampStart.AddDays(1).AddHours(i),
                    });
                }

                db.Events.Add(Camp(LiveCampId, live: true));
                db.Events.Add(new Event
                {
                    Id = QueuedCampId,
                    LinkshellId = OwnLinkshellId,
                    EventName = "Fafnir D1",
                    EventType = "hnm",
                    AssignedMonsterName = "Fafnir",
                    StartTime = CampStart.AddDays(2),
                });
                db.Events.Add(new Event
                {
                    Id = EndedCampId,
                    LinkshellId = OwnLinkshellId,
                    EventName = "Adamantoise D1",
                    EventType = "HNM",
                    AssignedMonsterName = "Adamantoise",
                    StartTime = CampStart.AddDays(-1),
                    CommencementStartTime = CampStart.AddDays(-1),
                    EndTime = CampStart.AddDays(-1).AddHours(2),
                });
                db.Events.Add(new Event
                {
                    Id = OtherLinkshellCampId,
                    LinkshellId = OtherLinkshellId,
                    EventName = "Nidhogg D1",
                    EventType = "HNM",
                    AssignedMonsterName = "Nidhogg",
                    StartTime = CampStart,
                });
                return Task.CompletedTask;
            });
        }

        public Task DisposeAsync()
        {
            App.Dispose();
            return Task.CompletedTask;
        }
    }

    private const string OfficerId = "addon-contract-officer";
    private const int OwnLinkshellId = 701;
    private const int OtherLinkshellId = 702;
    private const int LiveCampId = 1;
    private const int QueuedCampId = 2;
    private const int EndedCampId = 3;
    private const int OtherLinkshellCampId = 4;
    private const string AddonToken = "att_addoncontracttoken0000000000000000";

    private static readonly DateTime CampStart = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    // A live King Behemoth camp made from the addon: its WindowCountOverride holds the POST
    // count (2), while the camp runs 7 SPAWN windows.
    private static Event Camp(int id, bool live) => new()
    {
        Id = id,
        LinkshellId = OwnLinkshellId,
        EventName = "Behemoth/King Behemoth D1",
        EventType = "HNM",
        AssignedMonsterName = "Behemoth/King Behemoth",
        DayNumber = 1,
        StartTime = CampStart,
        CommencementStartTime = live ? CampStart : null,
        WindowAnchorAt = CampStart,
        HnmWindowNumber = 3,
        NextWindowAt = CampStart.AddMinutes(30),
        WindowCountOverride = 2,
    };

    private readonly Host _host;

    public AddonContractPipelineTests(Host host) => _host = host;

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage request)
    {
        using var client = _host.App.NewClient();
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var (status, body) = await SendAsync(Get(path, bearer: AddonToken));
        Assert.Equal(HttpStatusCode.OK, status);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    // ---- GET /api/addon/me (contracts-12) ----

    // Polled every minute per pairing. Four fields nobody read are gone; what the addon reads stays.
    [Fact]
    public async Task Me_SendsOnlyWhatTheAddonReads()
    {
        var me = await GetJsonAsync("/api/addon/me");

        foreach (var dropped in new[] { "issuedToCharacterName", "issuedToAppUserId", "snapshotScope", "maxAllianceNumber" })
        {
            Assert.False(me.TryGetProperty(dropped, out _), $"'{dropped}' should no longer be sent.");
        }
        foreach (var kept in new[]
        {
            "linkshellId", "linkshellName", "canModerateLiveEvent", "canPostLoot", "attendanceMode",
            "claimShieldEnabled", "claimShieldDisabledMonsters", "todCaptureMonsters",
        })
        {
            Assert.True(me.TryGetProperty(kept, out _), $"'{kept}' must still be sent.");
        }
        Assert.Equal(OwnLinkshellId, me.GetProperty("linkshellId").GetInt32());
        Assert.Equal("Own LS", me.GetProperty("linkshellName").GetString());
    }

    // ---- GET /api/addon/events (contracts-11) ----

    [Fact]
    public async Task EventList_HasOnlyHnmCampsThatHaveNotEnded_LiveFirst()
    {
        var root = await GetJsonAsync("/api/addon/events");

        var ids = root.GetProperty("events").EnumerateArray().Select(ev => ev.GetProperty("id").GetInt32()).ToList();

        // The 55 timed events no longer push the camps past the cap, the ended camp is gone, and
        // the camp list stays the same however the type is cased.
        Assert.Equal(new[] { LiveCampId, QueuedCampId }, ids);
    }

    [Fact]
    public async Task EventList_DropsTheFieldsNoAddonReads()
    {
        var root = await GetJsonAsync("/api/addon/events");
        var camp = root.GetProperty("events")[0];

        foreach (var dropped in new[] { "location", "dkpPerHour", "windowMethod", "supportsBreakRoom", "todCooldownHours" })
        {
            Assert.False(camp.TryGetProperty(dropped, out _), $"'{dropped}' should no longer be sent.");
        }
        // And the ones the addon does read are still there.
        foreach (var kept in new[] { "id", "name", "type", "isLive", "windowCount", "attendancePostCount", "openedWindowNumber" })
        {
            Assert.True(camp.TryGetProperty(kept, out _), $"'{kept}' must still be sent.");
        }
    }

    // The single-event read gave the POST count (2) where the list gave the SPAWN count (7).
    [Fact]
    public async Task EventDetail_WindowCount_MatchesTheEventList()
    {
        var list = await GetJsonAsync("/api/addon/events");
        var detail = await GetJsonAsync($"/api/addon/events/{LiveCampId}");

        var expected = HnmCampRules.EffectiveWindowCount(Camp(LiveCampId, live: true));
        Assert.NotEqual(2, expected);
        Assert.Equal(expected, list.GetProperty("events")[0].GetProperty("windowCount").GetInt32());
        Assert.Equal(expected, detail.GetProperty("windowCount").GetInt32());
        Assert.False(detail.TryGetProperty("windowMethod", out _));
    }
}
