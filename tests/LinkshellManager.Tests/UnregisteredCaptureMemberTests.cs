using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using NodaTime;
using Xunit;

namespace LinkshellManager.Tests;

// STRANGERS IN A CAPTURE, and how the camp roster reports them.
//
// An addon capture reads the whole alliance — party memory slots 0-17 — so anyone standing with the
// shell lands in it whether or not the shell has ever heard of them. They were never going to be
// credited: WindowEventDkpLedgerService resolves a membership for every name and silently drops the
// ones it cannot place. Listing them unmarked beside the people who ARE being paid, with a DKP
// figure in their row, is the thing that misleads an officer at review time.
//
// So the row stays and carries the answer. What is pinned here is that the mark tracks the ROSTER
// and nothing else — not whether the name was scanned or typed, not which capture it came from —
// and that a mapper with no roster in hand marks nobody.
public class UnregisteredCaptureMemberTests
{
    private static readonly DateTimeZone Utc = DateTimeZoneProviders.Tzdb["UTC"];
    private static readonly DateTime Anchor = new(2026, 9, 22, 21, 0, 0, DateTimeKind.Utc);

    private static RosterNameSet Roster(params string[] mains)
        => RosterNameSet.From(mains.Select(m => new RosterNameCandidate(m, null, null, null)));

    private static AttendanceSnapshot Capture(params string[] members)
    {
        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = 1,
            CapturedAtUtc = Anchor,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
            SlotKind = AttendanceSnapshotSlotKinds.Window,
            AllianceNumber = 1,
        };
        foreach (var name in members) snapshot.Entries.Add(new AttendanceSnapshotEntry { CharacterName = name });
        return snapshot;
    }

    private static WindowEvent Camp(params AttendanceSnapshot[] snapshots)
    {
        var camp = new WindowEvent
        {
            Id = 900,
            LinkshellId = 1,
            Name = "Fafnir",
            NormalizedName = "FAFNIR",
            Status = WindowEventStatuses.Open,
            FirstCapturedAtUtc = Anchor,
            LastCapturedAtUtc = Anchor,
            WindowAnchorAtUtc = Anchor,
            DkpAmount = 1.5,
        };
        foreach (var snapshot in snapshots) camp.Snapshots.Add(snapshot);
        return camp;
    }

    // ---- the combined roster ----

    [Fact]
    public void AMemberOnTheRoster_IsMarkedRegistered()
    {
        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            new[] { Capture("Edicius") }, roster: Roster("Edicius"));

        Assert.True(Assert.Single(combined).IsRegistered);
    }

    // THE case. Another shell's player standing at the same camp.
    [Fact]
    public void SomeoneTheShellDoesNotKnow_IsMarkedUnregistered()
    {
        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            new[] { Capture("Edicius", "Randomguy") }, roster: Roster("Edicius"));

        Assert.True(combined.Single(m => m.CharacterName == "Edicius").IsRegistered);
        Assert.False(combined.Single(m => m.CharacterName == "Randomguy").IsRegistered);
    }

    // They stay ON the roster table — the officer asked for them to be visible, and a name that
    // vanished would just move the confusion ("who was that seventh person?") somewhere else.
    [Fact]
    public void AnUnregisteredName_IsStillListed()
    {
        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            new[] { Capture("Edicius", "Randomguy") }, roster: Roster("Edicius"));

        Assert.Equal(2, combined.Count);
    }

    // A member at camp on an alt is a member. The ledger credits them through the same alt index,
    // so marking them a stranger here would contradict the payout on the very next screen.
    [Fact]
    public void AMemberOnTheirAlt_IsMarkedRegistered()
    {
        var roster = RosterNameSet.From(new[]
        {
            new RosterNameCandidate("Edicius", "Edicius", "Athmilk", null),
        });

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            new[] { Capture("Athmilk") }, roster: roster);

        Assert.True(Assert.Single(combined).IsRegistered);
    }

    // The safety valve, at the level that matters: an existing caller that passes no roster gets
    // the old output back, with nobody accused.
    [Fact]
    public void WithNoRoster_NobodyIsMarkedUnregistered()
    {
        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(new[] { Capture("Randomguy") });

        Assert.True(Assert.Single(combined).IsRegistered);
    }

    // ---- the per-capture entry list ----

    [Fact]
    public void CaptureEntries_CarryTheSameMark()
    {
        var row = AttendanceSectionsBuilder.MapSnapshot(
            Capture("Edicius", "Randomguy"), Utc, null, Roster("Edicius"));

        Assert.True(row.Entries.Single(e => e.CharacterName == "Edicius").IsRegistered);
        Assert.False(row.Entries.Single(e => e.CharacterName == "Randomguy").IsRegistered);
    }

    // A name an officer TYPED in during review is judged by the roster like any other. Being
    // hand-added is not evidence of membership — it is how a stranger gets into a capture in the
    // first place, since the typeahead only suggests roster names but never required one.
    [Fact]
    public void AHandAddedName_IsJudgedByTheRosterToo()
    {
        var capture = Capture("Edicius");
        capture.Entries.Add(new AttendanceSnapshotEntry { CharacterName = "Randomguy", AddedManually = true });

        var row = AttendanceSectionsBuilder.MapSnapshot(capture, Utc, null, Roster("Edicius"));

        Assert.False(row.Entries.Single(e => e.CharacterName == "Randomguy").IsRegistered);
    }

    // ---- the card header ----

    // The count an officer reads before pressing Post. CombinedMemberCount still describes the
    // table (every name is in it), so the number that cannot be paid has to be said separately.
    [Fact]
    public void TheCardCounts_HowManyCannotBePaid()
    {
        var row = AttendanceSectionsBuilder.MapWindowEvent(
            Camp(Capture("Edicius", "Randomguy", "Someoneelse")), Utc, Roster("Edicius"));

        Assert.Equal(3, row.CombinedMemberCount);
        Assert.Equal(2, row.UnregisteredMemberCount);
    }

    [Fact]
    public void ACampOfMembersOnly_CountsNoneUnregistered()
    {
        var row = AttendanceSectionsBuilder.MapWindowEvent(
            Camp(Capture("Edicius", "Athmilk")), Utc, Roster("Edicius", "Athmilk"));

        Assert.Equal(0, row.UnregisteredMemberCount);
    }
}
