using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.Tests;

// Per-alliance attendance, and the review gate that came with opening posting to every member.
//
// Two behaviours are pinned here, both of which fail SILENTLY if they regress:
//
//   * Two alliances at one camp must stay two snapshots. Nothing in the game can tell them apart —
//     the FFXI client only ever sees your own alliance — so the poster's chosen alliance number is
//     the entire signal, and it is the merge key that keeps their rosters from collapsing into one
//     line. Before it existed the merge was purely time-based, and two officers posting the same
//     second WOULD have folded.
//   * A capture from a member without moderation rights is inert until confirmed. That works
//     because the combined roster reads ACTIVE snapshots only, which is exactly the kind of
//     implicit coupling that breaks quietly when someone widens a filter.
public class SnapshotAllianceVerificationTests
{
    private const int Ls = 11;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static WindowEventLinkService NewLinks(ApplicationDbContext db) =>
        new(db, new MonsterTimingResolver(db));

    // A 10-minute-cadence camp (kings/dragons), so the merge window is 3 minutes and two windows
    // are reachable inside a short test.
    private static WindowEvent SeedCamp(ApplicationDbContext db, DateTime anchor)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        var camp = new WindowEvent
        {
            Id = 500,
            LinkshellId = Ls,
            Name = "Fafnir",
            NormalizedName = "FAFNIR",
            Status = WindowEventStatuses.Open,
            CreatedAtUtc = anchor,
            FirstCapturedAtUtc = anchor,
            LastCapturedAtUtc = anchor,
            WindowAnchorAtUtc = anchor,
            WindowCount = 7,
            WindowMinutes = 10,
            EntryType = WindowEventEntryTypes.KingsCamp,
        };
        db.WindowEvents.Add(camp);
        db.SaveChanges();
        return camp;
    }

    private static AttendanceSnapshot AddSnapshot(
        ApplicationDbContext db,
        WindowEvent camp,
        DateTime capturedAt,
        int alliance,
        string status,
        params string[] members)
    {
        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = Ls,
            WindowEventId = camp.Id,
            CapturedAtUtc = capturedAt,
            CreatedAtUtc = capturedAt,
            WindowNumber = 1,
            AllianceNumber = alliance,
            SnapshotStatus = status,
            EntryCount = members.Length,
        };
        foreach (var name in members)
        {
            snapshot.Entries.Add(new AttendanceSnapshotEntry { CharacterName = name });
        }
        db.AttendanceSnapshots.Add(snapshot);
        db.SaveChanges();
        return snapshot;
    }

    // An UNLINKED capture — what every /lsm now post is now, before an officer files it. No camp,
    // so no window number: those are set by the officer's slot choice, not at ingest.
    private static AttendanceSnapshot AddUnlinkedSnapshot(
        ApplicationDbContext db,
        DateTime capturedAt,
        string allianceKey,
        string status,
        params string[] members)
    {
        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = Ls,
            WindowEventId = null,
            CapturedAtUtc = capturedAt,
            CreatedAtUtc = capturedAt,
            WindowNumber = null,
            // The IDENTITY is what merges now, not a typed number -- see AllianceIdentityService.
            AllianceKey = allianceKey,
            SnapshotStatus = status,
            EntryCount = members.Length,
        };
        foreach (var name in members)
        {
            snapshot.Entries.Add(new AttendanceSnapshotEntry { CharacterName = name });
        }
        db.AttendanceSnapshots.Add(snapshot);
        db.SaveChanges();
        return snapshot;
    }

    private static readonly DateTime Anchor = new(2026, 8, 27, 21, 0, 0, DateTimeKind.Utc);

    // THE case this feature exists for. Two officers, one in each alliance, post one minute
    // apart — well inside the 3-minute merge window. They are capturing two different 18-person
    // rosters, so they must not fold.
    [Fact]
    public async Task DifferentAlliances_DoNotMerge_EvenPostedSecondsApart()
    {
        using var db = NewDb();
        AddUnlinkedSnapshot(db, Anchor.AddMinutes(1), "Millhouse", AttendanceSnapshotStatuses.Active, "Millhouse");

        var target = await NewLinks(db).FindUnlinkedMergeTargetAsync(
            Ls, Anchor.AddMinutes(2), "Ramuh", AttendanceSnapshotStatuses.Active, CancellationToken.None);

        Assert.Null(target);
    }

    // The other half of the same rule: two people in the SAME alliance are capturing one roster.
    // Folding them is what keeps a double-tap at a pop from doubling the officer's triage queue.
    [Fact]
    public async Task SameAlliance_MergesWithinTheWindow()
    {
        using var db = NewDb();
        var first = AddUnlinkedSnapshot(db, Anchor.AddMinutes(1), "Ramuh", AttendanceSnapshotStatuses.Active, "Millhouse");

        var target = await NewLinks(db).FindUnlinkedMergeTargetAsync(
            Ls, Anchor.AddMinutes(2), "Ramuh", AttendanceSnapshotStatuses.Active, CancellationToken.None);

        Assert.NotNull(target);
        Assert.Equal(first.Id, target!.Id);
    }

    // A Pending capture must never be absorbed into a verified one: an unvouched-for roster would
    // otherwise ride into the payout on someone else's Confirm.
    [Fact]
    public async Task PendingPost_DoesNotMergeIntoAVerifiedSnapshot()
    {
        using var db = NewDb();
        AddUnlinkedSnapshot(db, Anchor.AddMinutes(1), "Millhouse", AttendanceSnapshotStatuses.Active, "Millhouse");

        var target = await NewLinks(db).FindUnlinkedMergeTargetAsync(
            Ls, Anchor.AddMinutes(2), "Millhouse", AttendanceSnapshotStatuses.Pending, CancellationToken.None);

        Assert.Null(target);
    }

    // Pending captures from the same alliance still fold into each other, so two members of one
    // alliance both pressing Post don't create two review rows an officer has to confirm twice.
    [Fact]
    public async Task PendingPosts_MergeWithEachOther()
    {
        using var db = NewDb();
        var first = AddUnlinkedSnapshot(db, Anchor.AddMinutes(1), "Millhouse", AttendanceSnapshotStatuses.Pending, "Millhouse");

        var target = await NewLinks(db).FindUnlinkedMergeTargetAsync(
            Ls, Anchor.AddMinutes(2), "Millhouse", AttendanceSnapshotStatuses.Pending, CancellationToken.None);

        Assert.Equal(first.Id, target?.Id);
    }

    // The whole point of Pending: the capture is visible but its people are not in the roster the
    // payout is computed from. Nothing in BuildCombinedMembers mentions Pending — it filters to
    // Active — which is why this is worth a test rather than a comment.
    [Fact]
    public void PendingSnapshot_IsExcludedFromTheCombinedRoster()
    {
        using var db = NewDb();
        var camp = SeedCamp(db, Anchor);
        AddSnapshot(db, camp, Anchor, alliance: 1, AttendanceSnapshotStatuses.Active, "Millhouse", "Sylph");
        AddSnapshot(db, camp, Anchor, alliance: 2, AttendanceSnapshotStatuses.Pending, "Ramuh", "Ifrit");

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            db.AttendanceSnapshots.Include(s => s.Entries).ToList());

        Assert.Equal(new[] { "Millhouse", "Sylph" }, combined.Select(m => m.CharacterName).ToArray());
    }

    // Confirming is the only thing that has to happen for those people to start counting.
    [Fact]
    public void ConfirmingAPendingSnapshot_AddsItsMembersToTheRoster()
    {
        using var db = NewDb();
        var camp = SeedCamp(db, Anchor);
        AddSnapshot(db, camp, Anchor, alliance: 1, AttendanceSnapshotStatuses.Active, "Millhouse");
        var pending = AddSnapshot(db, camp, Anchor, alliance: 2, AttendanceSnapshotStatuses.Pending, "Ramuh");

        pending.SnapshotStatus = AttendanceSnapshotStatuses.Active;
        db.SaveChanges();

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            db.AttendanceSnapshots.Include(s => s.Entries).ToList());

        Assert.Equal(new[] { "Millhouse", "Ramuh" }, combined.Select(m => m.CharacterName).OrderBy(n => n).ToArray());
    }

    // The roster has to be able to say WHICH alliance each person was counted in — that is the
    // readable half of per-alliance posting, as against the merge key which is the mechanical half.
    [Fact]
    public void CombinedRoster_RecordsEachMembersAlliance()
    {
        using var db = NewDb();
        var camp = SeedCamp(db, Anchor);
        AddSnapshot(db, camp, Anchor, alliance: 1, AttendanceSnapshotStatuses.Active, "Millhouse");
        AddSnapshot(db, camp, Anchor, alliance: 2, AttendanceSnapshotStatuses.Active, "Ramuh");

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            db.AttendanceSnapshots.Include(s => s.Entries).ToList());

        Assert.Equal(new[] { 1 }, combined.Single(m => m.CharacterName == "Millhouse").AllianceNumbers);
        Assert.Equal(new[] { 2 }, combined.Single(m => m.CharacterName == "Ramuh").AllianceNumbers);
    }

    // Somebody who moved between alliances mid-camp is listed once, in both — flattening to
    // whichever capture happened to be latest would hide the move.
    [Fact]
    public void MemberSeenInTwoAlliances_ListsBoth()
    {
        using var db = NewDb();
        var camp = SeedCamp(db, Anchor);
        AddSnapshot(db, camp, Anchor, alliance: 2, AttendanceSnapshotStatuses.Active, "Millhouse");
        AddSnapshot(db, camp, Anchor.AddMinutes(30), alliance: 1, AttendanceSnapshotStatuses.Active, "Millhouse");

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            db.AttendanceSnapshots.Include(s => s.Entries).ToList());

        Assert.Equal(new[] { 1, 2 }, Assert.Single(combined).AllianceNumbers);
    }

    // Posting is one-way, so the block is what stops a payout that is visibly missing people.
    [Fact]
    public async Task PendingCount_IsWhatBlocksPostingToTheSheet()
    {
        using var db = NewDb();
        var camp = SeedCamp(db, Anchor);
        var links = NewLinks(db);
        AddSnapshot(db, camp, Anchor, alliance: 1, AttendanceSnapshotStatuses.Active, "Millhouse");
        Assert.Equal(0, await links.CountPendingSnapshotsAsync(camp.Id, CancellationToken.None));

        var pending = AddSnapshot(db, camp, Anchor, alliance: 2, AttendanceSnapshotStatuses.Pending, "Ramuh");
        Assert.Equal(1, await links.CountPendingSnapshotsAsync(camp.Id, CancellationToken.None));

        // Rejecting clears the block as surely as confirming does — an officer must not be stuck
        // unable to pay a camp because of one junk capture.
        pending.SnapshotStatus = AttendanceSnapshotStatuses.Ignored;
        db.SaveChanges();
        Assert.Equal(0, await links.CountPendingSnapshotsAsync(camp.Id, CancellationToken.None));
    }

    // allowCreate: false is the spam gate on unverified posts. A member may join a camp an officer
    // already opened, but a typo'd monster name must not put a fresh empty camp on the Event page.
    [Fact]
    public async Task UnverifiedPost_CannotMintACamp_AndLandsUnlinked()
    {
        using var db = NewDb();
        SeedCamp(db, Anchor);

        var created = await NewLinks(db).FindOrCreateAsync(
            Ls, "Some Monster Nobody Opened", Anchor, "Millhouse", DateTime.UtcNow,
            CancellationToken.None, forceNew: false, allowCreate: false);

        Assert.Null(created);
    }

    [Fact]
    public async Task UnverifiedPost_StillJoinsACampAnOfficerOpened()
    {
        using var db = NewDb();
        var camp = SeedCamp(db, Anchor);

        var found = await NewLinks(db).FindOrCreateAsync(
            Ls, "fafnir", Anchor.AddMinutes(5), "Millhouse", DateTime.UtcNow,
            CancellationToken.None, forceNew: false, allowCreate: false);

        Assert.Equal(camp.Id, found?.Id);
    }

    // "Make a New Event from this Snapshot" means create, not reuse. On a repeat camp the same
    // monster name comes round often, and folding into yesterday's open row merges two payrolls.
    [Fact]
    public async Task ForceNew_MintsAFreshCamp_RatherThanReusingTheOpenOne()
    {
        using var db = NewDb();
        var camp = SeedCamp(db, Anchor);

        var created = await NewLinks(db).FindOrCreateAsync(
            Ls, "Fafnir", Anchor.AddMinutes(5), "Millhouse", DateTime.UtcNow,
            CancellationToken.None, forceNew: true);

        Assert.NotNull(created);
        Assert.NotEqual(camp.Id, created!.Id);
    }

    // Every event this service creates carries a window grid. Two of the three copies this replaced
    // did not stamp one, so a camp created from either web surface labelled none of its snapshots.
    [Fact]
    public async Task CreatedCamp_CarriesItsWindowGridAndAnchor()
    {
        using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.SaveChanges();

        var created = await NewLinks(db).FindOrCreateAsync(
            Ls, "Fafnir", Anchor, "Millhouse", DateTime.UtcNow, CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(Anchor, created!.WindowAnchorAtUtc);
        Assert.True(created.WindowCount > 0, "a windowed monster must arrive with its window count");
        Assert.True(created.WindowMinutes > 0, "a windowed monster must arrive with its cadence");
        Assert.Equal(WindowEventEntryTypes.KingsCamp, created.EntryType);
    }

    // Filing a capture and vouching for it are separate decisions. Attach used to force Active,
    // which would have verified a Pending capture the moment an officer sorted it into a camp.
    [Fact]
    public void Attach_DoesNotVerifyAPendingSnapshot()
    {
        using var db = NewDb();
        var camp = SeedCamp(db, Anchor);
        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = Ls,
            CapturedAtUtc = Anchor.AddHours(-1),
            SnapshotStatus = AttendanceSnapshotStatuses.Pending,
            AllianceNumber = 1,
        };

        NewLinks(db).Attach(snapshot, camp);

        Assert.Equal(camp.Id, snapshot.WindowEventId);
        Assert.Equal(AttendanceSnapshotStatuses.Pending, snapshot.SnapshotStatus);
        // ...and the camp widened to cover a capture older than it.
        Assert.Equal(Anchor.AddHours(-1), camp.FirstCapturedAtUtc);
    }

    [Theory]
    [InlineData(null, 1)]   // an addon that predates the selector
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(99, AttendanceSnapshotAlliances.MaxAllianceNumber)]
    public void AllianceNumber_IsClampedToARealAlliance(int? supplied, int expected) =>
        Assert.Equal(expected, AttendanceSnapshotAlliances.Resolve(supplied));

    // Null is a real state — "captured before per-alliance posting existed" — and must not be
    // rendered as alliance 1 next to captures that really are.
    [Fact]
    public void UnnumberedSnapshot_LabelsAsUnassigned() =>
        Assert.Equal("Unassigned", AttendanceSnapshotAlliances.Label(null));
// The duplicate system is gone, and this is what replaces it: two captures of the SAME
    // alliance are unioned by character name, so a person in both is counted once and a person in
    // only one is still counted. The old flagging marked the second capture PossibleDuplicate,
    // which EXCLUDED it from the roster -- so anyone who appeared only in that second post was
    // silently unpaid. That is the regression this pins.
    [Fact]
    public void TwoCapturesOfOneAlliance_UnionByName_CountingEachPersonOnce()
    {
        using var db = NewDb();
        var camp = SeedCamp(db, Anchor);
        AddSnapshot(db, camp, Anchor, alliance: 1, AttendanceSnapshotStatuses.Active, "Millhouse", "Sylph");
        // A minute later, someone else in the same alliance posts — Sylph overlaps, Ramuh is new.
        AddSnapshot(db, camp, Anchor.AddMinutes(1), alliance: 1, AttendanceSnapshotStatuses.Active, "Sylph", "Ramuh");

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            db.AttendanceSnapshots.Include(s => s.Entries).ToList());

        Assert.Equal(
            new[] { "Millhouse", "Ramuh", "Sylph" },
            combined.Select(m => m.CharacterName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray());
        // Overlapping name counted once, and credited to both captures it appeared in.
        Assert.Equal(2, combined.Single(m => m.CharacterName == "Sylph").SnapshotCount);
    }

    // A later capture that is MISSING someone must never drop them: people step away mid-window,
    // and a roster that shrinks between posts would quietly un-credit them.
    [Fact]
    public void ALaterCaptureMissingSomeone_DoesNotRemoveThem()
    {
        using var db = NewDb();
        var camp = SeedCamp(db, Anchor);
        AddSnapshot(db, camp, Anchor, alliance: 1, AttendanceSnapshotStatuses.Active, "Millhouse", "Sylph");
        AddSnapshot(db, camp, Anchor.AddMinutes(2), alliance: 1, AttendanceSnapshotStatuses.Active, "Millhouse");

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            db.AttendanceSnapshots.Include(s => s.Entries).ToList());

        Assert.Contains(combined, m => m.CharacterName == "Sylph");
    }
}
