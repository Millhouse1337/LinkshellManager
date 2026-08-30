using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using NodaTime;
using Xunit;

namespace LinkshellManager.Tests;

// Misc posts: attendance that belongs to a camp but to no window.
//
// The case it exists for is a pop the shell did NOT claim, where members stayed in zone in case the
// holding group wiped. They were there, they should be paid, and there is no window to file them
// under. Before this the only way to express "no window" was a null WindowNumber — which already
// means something else ("this camp runs no window grid"), so the two were indistinguishable.
//
// Three things are pinned here, all of which fail SILENTLY if they regress: the Window default, the
// read-time window derivation, and who gets the misc rate.
public class SnapshotSlotKindTests
{
    private static readonly DateTimeZone Utc = DateTimeZoneProviders.Tzdb["UTC"];
    private static readonly DateTime Anchor = new(2026, 8, 29, 21, 0, 0, DateTimeKind.Utc);

    // A 10-minute/7-window camp, so a window boundary is reachable inside a test.
    private static WindowEvent GriddedCamp() => new()
    {
        Id = 700,
        LinkshellId = 1,
        Name = "Fafnir",
        NormalizedName = "FAFNIR",
        Status = WindowEventStatuses.Open,
        FirstCapturedAtUtc = Anchor,
        LastCapturedAtUtc = Anchor,
        WindowAnchorAtUtc = Anchor,
        WindowCount = 7,
        WindowMinutes = 10,
    };

    // Kirin and friends: a real camp with no cadence at all, where a null WindowNumber is the
    // correct answer for an ORDINARY capture.
    private static WindowEvent UngriddedCamp() => new()
    {
        Id = 701,
        LinkshellId = 1,
        Name = "Kirin",
        NormalizedName = "KIRIN",
        Status = WindowEventStatuses.Open,
        FirstCapturedAtUtc = Anchor,
        LastCapturedAtUtc = Anchor,
        WindowAnchorAtUtc = Anchor,
    };

    private static AttendanceSnapshot Capture(
        DateTime at, string slotKind = AttendanceSnapshotSlotKinds.Window, params string[] members)
    {
        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = 1,
            CapturedAtUtc = at,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
            SlotKind = slotKind,
            AllianceNumber = 1,
        };
        foreach (var name in members) snapshot.Entries.Add(new AttendanceSnapshotEntry { CharacterName = name });
        return snapshot;
    }

    // ---- the default ----

    // The migration backfills "Window", and the model default has to agree with it. If it did not,
    // every camp-handoff roster — HnmCampReviewHandoffService builds its snapshot inline and never
    // sets this — would arrive classified as Misc and be paid the misc rate for a whole camp.
    [Fact]
    public void ANewSnapshot_IsAWindowCapture_NotMisc()
    {
        Assert.Equal(AttendanceSnapshotSlotKinds.Window, new AttendanceSnapshot().SlotKind);
        Assert.False(AttendanceSnapshotSlotKinds.IsMisc(new AttendanceSnapshot().SlotKind));
    }

    // Anything unrecognised is a Window, never Misc: a garbled value must not silently reprice
    // somebody at a rate no officer chose.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("misc")]
    [InlineData("Whatever")]
    public void UnrecognisedSlotKinds_FailClosedToWindow(string? value)
        => Assert.Equal(AttendanceSnapshotSlotKinds.Window, AttendanceSnapshotSlotKinds.Resolve(value));

    // ---- ApplySlot ----

    [Fact]
    public void FilingAsMisc_ClearsTheWindowNumber()
    {
        var snapshot = Capture(Anchor.AddMinutes(35));
        snapshot.WindowNumber = 4;

        WindowEventLinkService.ApplySlot(snapshot, GriddedCamp(), AttendanceSnapshotSlotKinds.Misc, windowNumber: 4);

        Assert.Equal(AttendanceSnapshotSlotKinds.Misc, snapshot.SlotKind);
        Assert.Null(snapshot.WindowNumber);
    }

    // The officer's explicit choice is stamped concretely rather than left to be re-derived later
    // against a grid that may have moved.
    [Fact]
    public void FilingAsAWindow_StampsTheChosenNumber()
    {
        var snapshot = Capture(Anchor);

        WindowEventLinkService.ApplySlot(snapshot, GriddedCamp(), AttendanceSnapshotSlotKinds.Window, windowNumber: 3);

        Assert.Equal(AttendanceSnapshotSlotKinds.Window, snapshot.SlotKind);
        Assert.Equal(3, snapshot.WindowNumber);
    }

    // A bad client cannot invent window 99 on a 7-window camp.
    [Fact]
    public void AChosenWindow_IsClampedToTheCampsOwnGrid()
    {
        var snapshot = Capture(Anchor);

        WindowEventLinkService.ApplySlot(snapshot, GriddedCamp(), AttendanceSnapshotSlotKinds.Window, windowNumber: 99);

        Assert.Equal(7, snapshot.WindowNumber);
    }

    // No number given falls back to the grid: 35 minutes into a 10-minute cadence is window 4.
    [Fact]
    public void FilingAsAWindow_WithNoNumber_DerivesItFromTheGrid()
    {
        var snapshot = Capture(Anchor.AddMinutes(35));

        WindowEventLinkService.ApplySlot(snapshot, GriddedCamp(), AttendanceSnapshotSlotKinds.Window, windowNumber: null);

        Assert.Equal(4, snapshot.WindowNumber);
    }

    // The state that must stay distinct from Misc: an ordinary capture on a camp that runs no
    // windows. Null number, but Window kind, so it is priced at the ordinary rate.
    [Fact]
    public void AWindowCaptureOnAnUngriddedCamp_KeepsANullNumberButStaysAWindow()
    {
        var snapshot = Capture(Anchor);

        WindowEventLinkService.ApplySlot(snapshot, UngriddedCamp(), AttendanceSnapshotSlotKinds.Window, windowNumber: null);

        Assert.Equal(AttendanceSnapshotSlotKinds.Window, snapshot.SlotKind);
        Assert.Null(snapshot.WindowNumber);
    }

    // ---- the read-time derivation ----

    // THE trap. MapSnapshot derives a window number when the stored one is null, and Misc stores
    // null — so without an explicit guard a misc post on a gridded camp renders "Window 4 of 7"
    // beside its own Misc chip.
    [Fact]
    public void AMiscCapture_RendersNoWindowLabel_EvenOnAGriddedCamp()
    {
        var misc = Capture(Anchor.AddMinutes(35), AttendanceSnapshotSlotKinds.Misc, "Millhouse");

        var row = AttendanceSectionsBuilder.MapSnapshot(misc, Utc, GriddedCamp());

        Assert.True(row.IsMisc);
        Assert.Null(row.WindowNumber);
        Assert.Null(row.WindowLabel);
        Assert.Equal("Misc", row.SlotLabel);
    }

    // The other side of it: an ordinary capture still gets its label derived, exactly as before.
    [Fact]
    public void AWindowCapture_StillDerivesItsLabel()
    {
        var window = Capture(Anchor.AddMinutes(35), AttendanceSnapshotSlotKinds.Window, "Millhouse");

        var row = AttendanceSectionsBuilder.MapSnapshot(window, Utc, GriddedCamp());

        Assert.False(row.IsMisc);
        Assert.Equal(4, row.WindowNumber);
        Assert.Equal("Window 4 of 7", row.WindowLabel);
        Assert.Equal("Window 4 of 7", row.SlotLabel);
    }

    // ---- who gets the misc rate ----

    [Fact]
    public void AMiscOnlyMember_IsPaidTheMiscRate()
    {
        var snapshots = new[] { Capture(Anchor, AttendanceSnapshotSlotKinds.Misc, "Millhouse") };

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            snapshots, memberDkpOverrides: null, defaultDkpAmount: 1.5, miscDkpAmount: 0.5);

        var row = Assert.Single(combined);
        Assert.Equal(AttendanceSnapshotSlotKinds.Misc, row.CreditSource);
        Assert.Equal(0.5, row.EffectiveDkpAmount);
    }

    // Somebody who was in a window AND turned up in a misc post is an ordinary attendee. Paying
    // them the misc rate would dock a person for being present twice.
    [Fact]
    public void AMemberSeenInBoth_IsPaidTheWindowRate()
    {
        var snapshots = new[]
        {
            Capture(Anchor, AttendanceSnapshotSlotKinds.Window, "Millhouse"),
            Capture(Anchor.AddMinutes(20), AttendanceSnapshotSlotKinds.Misc, "Millhouse"),
        };

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            snapshots, memberDkpOverrides: null, defaultDkpAmount: 1.5, miscDkpAmount: 0.5);

        var row = Assert.Single(combined);
        Assert.Equal("Both", row.CreditSource);
        Assert.Equal(1.5, row.EffectiveDkpAmount);
    }

    // A null misc rate is the DEFAULT, and it means "pay them what a window pays".
    [Fact]
    public void NoMiscRateSet_PaysMiscOnlyMembersTheOrdinaryRate()
    {
        var snapshots = new[] { Capture(Anchor, AttendanceSnapshotSlotKinds.Misc, "Millhouse") };

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            snapshots, memberDkpOverrides: null, defaultDkpAmount: 1.5, miscDkpAmount: null);

        Assert.Equal(1.5, Assert.Single(combined).EffectiveDkpAmount);
    }

    // An officer's explicit per-character amount beats both rates.
    [Fact]
    public void AnExplicitOverride_BeatsTheMiscRate()
    {
        var snapshots = new[] { Capture(Anchor, AttendanceSnapshotSlotKinds.Misc, "Millhouse") };
        var overrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["Millhouse"] = 3.0 };

        var combined = AttendanceSectionsBuilder.BuildCombinedMembers(
            snapshots, overrides, defaultDkpAmount: 1.5, miscDkpAmount: 0.5);

        Assert.Equal(3.0, Assert.Single(combined).EffectiveDkpAmount);
    }

    // A Pending misc capture must not make anyone look misc-only: an unconfirmed post could
    // otherwise reprice a genuine window attendee.
    [Fact]
    public void OnlyActiveSnapshots_DecideWhoIsMiscOnly()
    {
        var pendingMisc = Capture(Anchor, AttendanceSnapshotSlotKinds.Misc, "Millhouse");
        pendingMisc.SnapshotStatus = AttendanceSnapshotStatuses.Pending;
        var windowEvent = new WindowEvent { Id = 700, LinkshellId = 1 };
        windowEvent.Snapshots.Add(Capture(Anchor, AttendanceSnapshotSlotKinds.Window, "Millhouse"));
        windowEvent.Snapshots.Add(pendingMisc);

        Assert.Empty(WindowEventMiscDkp.MiscOnlyCharacterNames(windowEvent.Snapshots));
    }

    // ---- the override safety net ----

    [Fact]
    public void MiscOverrides_AreWrittenForMiscOnlyMembers()
    {
        var windowEvent = new WindowEvent { Id = 700, LinkshellId = 1 };
        windowEvent.Snapshots.Add(Capture(Anchor, AttendanceSnapshotSlotKinds.Window, "Millhouse"));
        windowEvent.Snapshots.Add(Capture(Anchor, AttendanceSnapshotSlotKinds.Misc, "Ramuh"));

        WindowEventMiscDkp.ApplyMiscOverrides(windowEvent, resolvedDefault: 1.5, miscAmount: 0.5, submittedNames: null);

        var row = Assert.Single(windowEvent.MemberDkpOverrides);
        Assert.Equal("Ramuh", row.CharacterName);
        Assert.Equal(0.5, row.DkpAmount);
    }

    // Misc equal to the default is the default state. Writing rows that merely repeat the event
    // amount would fight ApplyMemberDkpOverrides, which removes exactly such rows.
    [Fact]
    public void AMiscRateEqualToTheDefault_WritesNoOverrides()
    {
        var windowEvent = new WindowEvent { Id = 700, LinkshellId = 1 };
        windowEvent.Snapshots.Add(Capture(Anchor, AttendanceSnapshotSlotKinds.Misc, "Ramuh"));

        WindowEventMiscDkp.ApplyMiscOverrides(windowEvent, resolvedDefault: 1.5, miscAmount: 1.5, submittedNames: null);

        Assert.Empty(windowEvent.MemberDkpOverrides);
    }

    // An officer who typed a value for somebody has spoken for them; the rule must not overrule it.
    [Fact]
    public void ASubmittedMember_IsLeftAlone()
    {
        var windowEvent = new WindowEvent { Id = 700, LinkshellId = 1 };
        windowEvent.Snapshots.Add(Capture(Anchor, AttendanceSnapshotSlotKinds.Misc, "Ramuh"));
        var submitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ramuh" };

        WindowEventMiscDkp.ApplyMiscOverrides(windowEvent, resolvedDefault: 1.5, miscAmount: 0.5, submitted);

        Assert.Empty(windowEvent.MemberDkpOverrides);
    }

    // THE cleanup case. Re-slotting a capture from Misc to a window makes its members ordinary
    // attendees; without this their old misc row would survive and underpay them forever.
    [Fact]
    public void ReSlottingOutOfMisc_RemovesTheStaleMiscOverride()
    {
        var windowEvent = new WindowEvent { Id = 700, LinkshellId = 1 };
        var capture = Capture(Anchor, AttendanceSnapshotSlotKinds.Misc, "Ramuh");
        windowEvent.Snapshots.Add(capture);
        WindowEventMiscDkp.ApplyMiscOverrides(windowEvent, 1.5, 0.5, submittedNames: null);
        Assert.Single(windowEvent.MemberDkpOverrides);

        // The officer moves it to window 2.
        WindowEventLinkService.ApplySlot(capture, GriddedCamp(), AttendanceSnapshotSlotKinds.Window, 2);
        WindowEventMiscDkp.ApplyMiscOverrides(windowEvent, 1.5, 0.5, submittedNames: null);

        Assert.Empty(windowEvent.MemberDkpOverrides);
    }

    // Cleanup only reclaims rows it could have written. A hand-typed amount that differs from the
    // misc rate is somebody's decision and stays.
    [Fact]
    public void Cleanup_LeavesAHandTypedOverrideAlone()
    {
        var windowEvent = new WindowEvent { Id = 700, LinkshellId = 1 };
        windowEvent.Snapshots.Add(Capture(Anchor, AttendanceSnapshotSlotKinds.Window, "Millhouse"));
        windowEvent.MemberDkpOverrides.Add(new WindowEventMemberDkp
        {
            WindowEventId = 700,
            CharacterName = "Millhouse",
            DkpAmount = 4.0,
        });

        WindowEventMiscDkp.ApplyMiscOverrides(windowEvent, 1.5, 0.5, submittedNames: null);

        Assert.Equal(4.0, Assert.Single(windowEvent.MemberDkpOverrides).DkpAmount);
    }
}
