using System.Net;
using System.Net.Http;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static LinkshellManager.Tests.ApiPipelineRequests;

namespace LinkshellManager.Tests;

// R2.1 through the real app: the Past Events DKP routes take an optional reason, still accept the
// body-less Remove an app loaded before the Reason box sends, and refuse a plain member.
public sealed class PastEventCorrectionPipelineTests : IAsyncLifetime
{
    private const string LeaderId = "leader-user";
    private const string LeaderDiscordId = "200000000000000011";
    private const string MemberId = "member-user";
    private const string MemberDiscordId = "200000000000000012";
    private const string LeaderToken = "LeaderTokenAAAAAAAAAAAAAAAAAAAA";
    private const string MemberToken = "MemberTokenBBBBBBBBBBBBBBBBBBBB";
    private const int Ls = 601;
    private const int HistoryId = 602;
    private const int LeaderRow = 603;
    private const int MemberRow = 604;

    private readonly ApiPipelineHost _app = new();

    public async Task InitializeAsync()
    {
        await _app.SeedUserAsync(LeaderId, LeaderDiscordId);
        await _app.SeedUserAsync(MemberId, MemberDiscordId);
        await _app.SeedAsync(db =>
        {
            db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", AppUserId = LeaderId });
            db.AppUserLinkshells.AddRange(
                new AppUserLinkshell { AppUserId = LeaderId, LinkshellId = Ls, CharacterName = "Boss", Rank = LinkshellRanks.Leader, Status = "Active", LinkshellDkp = 10 },
                new AppUserLinkshell { AppUserId = MemberId, LinkshellId = Ls, CharacterName = "Bob", Rank = LinkshellRanks.Member, Status = "Active", LinkshellDkp = 10 });
            db.EventHistories.Add(new EventHistory { Id = HistoryId, LinkshellId = Ls, EventName = "Sky", EventType = "Sky", DkpPerHour = 10, Duration = 1 });
            db.AppUserEventHistories.AddRange(
                new AppUserEventHistory { Id = LeaderRow, EventHistoryId = HistoryId, AppUserId = LeaderId, CharacterName = "Boss", EventDkp = 10 },
                new AppUserEventHistory { Id = MemberRow, EventHistoryId = HistoryId, AppUserId = MemberId, CharacterName = "Bob", EventDkp = 10 });
            db.DkpLedgerEntries.AddRange(
                new DkpLedgerEntry { LinkshellId = Ls, AppUserId = LeaderId, EntryType = "EventEarned", Amount = 10, EventHistoryId = HistoryId, EventType = "Sky", OccurredAt = DateTime.UtcNow },
                new DkpLedgerEntry { LinkshellId = Ls, AppUserId = MemberId, EntryType = "EventEarned", Amount = 10, EventHistoryId = HistoryId, EventType = "Sky", OccurredAt = DateTime.UtcNow });
            return Task.CompletedTask;
        });
        _app.Discord.Accept(LeaderToken, LeaderDiscordId);
        _app.Discord.Accept(MemberToken, MemberDiscordId);
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private async Task<HttpStatusCode> SendAsync(HttpRequestMessage request)
    {
        using var client = _app.NewClient();
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private Task<List<DkpLedgerEntry>> RowsAsync(string appUserId) =>
        _app.ReadAsync(db => db.DkpLedgerEntries.AsNoTracking()
            .Where(e => e.AppUserId == appUserId).OrderBy(e => e.Id).ToListAsync());

    private static string Participant(int row, string action) =>
        $"/api/activity/event-history/{HistoryId}/participants/{row}/{action}";

    [Fact]
    public async Task APlainMember_Gets403_OnEveryDkpEdit()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(
            Post(Participant(MemberRow, "dkp"), bearer: MemberToken, body: "{\"amount\":50,\"reason\":\"mine\"}")));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(
            Post(Participant(LeaderRow, "remove"), bearer: MemberToken, body: "{\"reason\":\"bye\"}")));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(
            Post($"/api/activity/event-history/{HistoryId}/edit", bearer: MemberToken, body: "{\"dkpPerHour\":99}")));

        Assert.Single(await RowsAsync(MemberId));
        Assert.Single(await RowsAsync(LeaderId));
    }

    [Fact]
    public async Task TheLeader_SetsWithAReason_AndRemovesWithNoBodyAtAll()
    {
        Assert.Equal(HttpStatusCode.OK, await SendAsync(
            Post(Participant(MemberRow, "dkp"), bearer: LeaderToken, body: "{\"amount\":6,\"reason\":\"left early\"}")));
        Assert.Equal(HttpStatusCode.OK, await SendAsync(
            Build(HttpMethod.Post, Participant(LeaderRow, "remove"), LeaderToken, cookie: null, guildId: null, body: "")));

        var member = await RowsAsync(MemberId);
        Assert.Equal(new[] { 10d, -4d }, member.Select(e => e.Amount));
        Assert.Equal(DkpCorrections.EntryType, member[1].EntryType);
        Assert.Equal("left early", member[1].EditReason);
        Assert.StartsWith("Correction by ", member[1].Details);
        Assert.Equal(new[] { 10d, -10d }, (await RowsAsync(LeaderId)).Select(e => e.Amount));
    }
}
