using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// Which end-event path a camp takes, and therefore whether it gets paid at all.
///
/// There are two payout models and pressing "End Event" has to pick the right one:
///
///   * An HNM CAMP is priced by its SHAPE — a per-window rate plus open / close / claim / kill
///     bonuses off the linkshell's settings (HnmCampPricing). It is paid by staging a review row
///     that an officer Posts.
///   * Everything else — a timed event, and a Claim/Kill-style windowed event that is not an HNM
///     camp — is paid by EndEventCoreAsync, as durationHours x DkpPerHour or
///     windowsAttended x DkpPerHour.
///
/// Sending a camp down the second path pays it NOTHING, silently: Event.DkpPerHour is forced to 0
/// on HNM camps at creation precisely because that column is not how they are priced, so the
/// windowed branch computes windowsAttended x 0 and archives every attendee on 0 DKP, never having
/// consulted a single bonus.
///
/// That is what happened. The guard existed in the web and Activity end actions but tested
/// Manual Check In only, and the addon's end endpoint -- the button an officer at the camp actually
/// presses -- had no guard at all. Standard camps, the common case, fell through on all three.
/// </summary>
public class EndEventCampPathRoutingTests
{
    private static Event Camp(string? eventType = "HNM", string? mode = null, DateTime? finalized = null)
        => new() { EventType = eventType, AttendanceMode = mode, WdFinalizedAt = finalized };

    // BOTH attendance modes are camps. AttendanceMode distinguishes how a camp gathers its roster
    // (per-window scans vs Check In / Check Out); it says nothing about who prices it, and both are
    // priced by the finalizer. Gating on it is the narrowing that caused the bug.
    [Theory]
    [InlineData(null)]              // Standard -- the common case, and the one that was falling through
    [InlineData("Standard")]
    [InlineData(HnmAttendanceModes.Wd)]
    public void EveryHnmCamp_EndsThroughTheCampPath(string? mode)
        => Assert.True(HnmCampReviewHandoffService.EndsThroughCampPath(Camp(mode: mode)));

    // The other side of the line, and the reason this is not simply "is it windowed": a Claim/Kill
    // windowed event genuinely IS paid windowsAttended x DkpPerHour by EndEventCoreAsync, so
    // routing it to the camp path would strand it with no payout at all.
    [Theory]
    [InlineData("Sky")]
    [InlineData("Dynamis")]
    [InlineData("Limbus")]
    [InlineData("Sea")]
    [InlineData(null)]
    [InlineData("")]
    public void ANonHnmEvent_KeepsTheGenericPath(string? eventType)
        => Assert.False(HnmCampReviewHandoffService.EndsThroughCampPath(Camp(eventType: eventType)));

    // EventType comes off a form and a JSON body, so it is matched the way every other reader
    // matches it (DiscordEventMessageBuilder.IsHnm): case-insensitively, trimmed.
    [Theory]
    [InlineData("hnm")]
    [InlineData("Hnm")]
    [InlineData("  HNM  ")]
    public void TheEventTypeMatchIsCaseAndWhitespaceTolerant(string eventType)
        => Assert.True(HnmCampReviewHandoffService.EndsThroughCampPath(Camp(eventType: eventType)));

    // The idempotence latch HandOffAndRecycleAsync itself gates on. A camp that has already been
    // handed off is RECYCLED, not deleted -- so a second End Event must fall through to the generic
    // path, which is what actually removes the board. Answering true forever would make the board
    // unremovable.
    [Fact]
    public void ACampAlreadyHandedOff_FallsThroughSoTheBoardCanBeRemoved()
        => Assert.False(HnmCampReviewHandoffService.EndsThroughCampPath(
            Camp(finalized: new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc))));

    // THE REGRESSION GUARD.
    //
    // The bug was not a wrong predicate -- it was three hand-written copies of one, which drifted.
    // So the rule is that no end-event action may state the condition itself: each must call the
    // shared predicate. A fourth end path added later inherits the fix instead of re-deriving it.
    [Fact]
    public void NoEndEventActionRestatesTheConditionByHand()
    {
        var repoRoot = FindRepoRoot();
        var endPaths = new[]
        {
            "Controllers/EventController.Lifecycle.cs",
            "Controllers/ActivityDataController.EventsLifecycle.cs",
            "Controllers/AddonApiController.AddonEvents.cs",
        };

        foreach (var relative in endPaths)
        {
            var path = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{relative} not found -- did it move?");
            var source = File.ReadAllText(path);

            Assert.True(
                source.Contains("HnmCampReviewHandoffService.EndsThroughCampPath", StringComparison.Ordinal),
                $"{relative} ends events but never asks EndsThroughCampPath. Every end path must "
                + "route HNM camps to the camp handoff, or it archives them paying 0 DKP.");

            // The two shapes the condition was previously written in, either of which means someone
            // has re-derived the answer locally instead of asking for it.
            Assert.False(
                Regex.IsMatch(source, @"AttendanceMode,\s*HnmAttendanceModes\.Wd[\s\S]{0,120}?WdFinalizedAt\s+is\s+null"),
                $"{relative} re-tests Manual Check In + WdFinalizedAt by hand. That is the exact "
                + "condition that was too narrow: it let every Standard camp fall through to a 0 "
                + "payout. Call EndsThroughCampPath instead.");
            Assert.False(
                Regex.IsMatch(source, @"DiscordEventMessageBuilder\.IsHnm\([^)]*\)\s*&&\s*[^;]*WdFinalizedAt\s+is\s+null"),
                $"{relative} restates EndsThroughCampPath inline. Call the predicate so a later "
                + "change to it reaches every end path.");
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Controllers")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate the repo root above {AppContext.BaseDirectory}.");
    }
}
