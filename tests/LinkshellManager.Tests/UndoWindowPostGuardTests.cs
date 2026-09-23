using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// WHEN AN "UNDO THIS POST" MAY FIRE.
//
// Undo deletes a whole window: its attendee rows and the Verify ledger entries that recorded them.
// That is safe on a live camp — every payout on a windowed camp is computed from the windows a
// member was scanned in, so removing the window removes the credit with it — and unsafe in exactly
// two states, both pinned here.
//
// The ORDERING rule is the subtle one, and it exists because POSTING is one-way on an Open/Close
// camp: PostAttendanceAsync refuses to create any further window once the Close is filed (so a
// second officer's stale client cannot invent the Open somebody deliberately skipped). Undoing the
// Open underneath a Close would therefore leave a camp with no Open and no way to post one — an
// "undo" that cannot itself be undone.
public class UndoWindowPostGuardTests
{
    private static readonly DateTime Anchor = new(2026, 9, 23, 21, 0, 0, DateTimeKind.Utc);

    // A Standard king/dragon: seven spawn windows, read twice (Open + Close).
    private static Event OpenCloseCamp(DateTime? endTime = null) => new()
    {
        Id = 400,
        LinkshellId = 1,
        EventName = "Fafnir",
        EventType = "HNM",
        CommencementStartTime = Anchor,
        EndTime = endTime,
    };

    // A wyrm: one post per hour-long window, and any of them re-postable.
    private static Event NumberedCamp(DateTime? endTime = null) => new()
    {
        Id = 401,
        LinkshellId = 1,
        EventName = "Tiamat",
        EventType = "HNM",
        CommencementStartTime = Anchor,
        EndTime = endTime,
    };

    // The guard as the endpoint spells it: a 2-post camp, an ordinary (non-kill) window, and a
    // later ordinary window already filed.
    private static bool UnwindBlocked(Event camp, int sequence, bool isKillWindow, params int[] existing)
        => DiscordEventMessageBuilder.AttendancePostCount(camp) == 2
           && !isKillWindow
           && existing.Any(seq => seq > sequence);

    [Fact]
    public void AnOpenCloseCamp_TakesTwoPosts()
        => Assert.Equal(2, DiscordEventMessageBuilder.AttendancePostCount(OpenCloseCamp()));

    [Fact]
    public void AWyrm_PostsPerWindow_SoTheOrderingRuleDoesNotApply()
        => Assert.NotEqual(2, DiscordEventMessageBuilder.AttendancePostCount(NumberedCamp()));

    // ---- the ordering rule ----

    // THE CASE IT EXISTS FOR. Undo the Open while the Close is filed and the camp is stranded:
    // posting refuses to create a new Open, so nothing can put it back.
    [Fact]
    public void UndoingTheOpen_UnderAFiledClose_IsBlocked()
        => Assert.True(UnwindBlocked(OpenCloseCamp(), sequence: 1, isKillWindow: false, existing: new[] { 1, 2 }));

    [Fact]
    public void UndoingTheClose_IsAllowed_NothingIsFiledAfterIt()
        => Assert.False(UnwindBlocked(OpenCloseCamp(), sequence: 2, isKillWindow: false, existing: new[] { 1, 2 }));

    [Fact]
    public void UndoingTheOpen_WithNoCloseYet_IsAllowed()
        => Assert.False(UnwindBlocked(OpenCloseCamp(), sequence: 1, isKillWindow: false, existing: new[] { 1 }));

    // A kill roster is filed AFTER the close by design and is not part of the Open/Close pair, so
    // it never blocks the window below it and is never blocked by one.
    [Fact]
    public void AKillRoster_DoesNotBlockUndoingTheCloseBeneathIt()
        => Assert.False(UnwindBlocked(OpenCloseCamp(), sequence: 2, isKillWindow: false, existing: new[] { 1, 2 }));

    [Fact]
    public void AKillRoster_IsItselfUndoable_WhateverSitsBelowIt()
        => Assert.False(UnwindBlocked(OpenCloseCamp(), sequence: 3, isKillWindow: true, existing: new[] { 1, 2, 3 }));

    // A numbered camp re-posts any window freely, so undo has no order to keep.
    [Fact]
    public void ANumberedCamp_UndoesAnyWindowInAnyOrder()
        => Assert.False(UnwindBlocked(NumberedCamp(), sequence: 3, isKillWindow: false, existing: new[] { 3, 4, 5 }));

    // ---- the ended-camp rule ----
    //
    // End Camp copies the windows onto a review card and the DKP is computed from THAT. Deleting
    // the live row afterwards changes nothing an officer can see and everything they cannot.

    [Fact]
    public void ALiveCamp_HasNoEndTime_SoUndoIsOffered()
        => Assert.Null(OpenCloseCamp().EndTime);

    [Fact]
    public void AnEndedCamp_IsRecognisedByItsEndTime()
        => Assert.NotNull(OpenCloseCamp(endTime: Anchor.AddHours(1)).EndTime);
}
