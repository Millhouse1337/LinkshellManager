using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// HnmCampPricing resolves "what does this camp pay for a window?" — the precedence
// (Event.Hnm*Override ?? the linkshell's setting) plus the branch that decides which settings even
// apply. The formulas stay in the finalizers; only the resolution lives here.
//
// It exists because a THIRD caller appeared beside the two finalizers: GET /api/addon/events has to
// quote the in-game addon a number before a post that matches what gets paid after it. The addon
// carrying its own answer instead is the whole bug this was written for — it displayed "+1 DKP
// each" on Standard camps the app pays 0 for, because its 1 came from a local settings key the
// server had never seen.
public class HnmCampPricingTests
{
    private static Linkshell Ls() => new()
    {
        Id = 1,
        LinkshellName = "Test",
        HnmAttendanceMode = HnmAttendanceModes.Standard,
        HnmStandardOpenBonus = 0.5,
        HnmStandardCloseBonus = 1.5,
        WdDkpPerWindow = 0.25,
    };

    private static Event StandardCamp() => new()
    {
        Id = 10,
        LinkshellId = 1,
        EventName = "Aspidochelone D3",
        EventType = "HNM",
        AssignedMonsterName = "Aspidochelone",
    };

    // The same camp stamped Manual Check In. AttendanceMode is what IsWd reads, and it is stamped
    // on the EVENT at creation so a linkshell switching modes can't re-price a camp mid-run.
    private static Event WdCamp()
    {
        var ev = StandardCamp();
        ev.AttendanceMode = HnmAttendanceModes.Wd;
        return ev;
    }

    // A camp's own price wins over the linkshell's, which is what "Change DKP" on the create form
    // is for — a pop nobody wants to sit is worth more than the linkshell's usual rate.
    [Fact]
    public void DefaultWindowValue_StandardCamp_PrefersEventOverrideOverLinkshellDefault()
    {
        var ev = StandardCamp();
        ev.HnmOpenBonusOverride = 4.0;

        // Sequence 1 on a camp with nothing posted yet: posting it makes it the close too, so the
        // quote is open + close. The override replaces only the open.
        Assert.Equal(4.0 + 1.5, HnmCampPricing.DefaultWindowValue(ev, Ls(), sequence: 1)!.Value, precision: 3);
    }

    [Fact]
    public void DefaultWindowValue_StandardCamp_FallsBackToTheLinkshellWhenNoOverrideIsSet()
    {
        Assert.Equal(0.5 + 1.5, HnmCampPricing.DefaultWindowValue(StandardCamp(), Ls(), sequence: 1)!.Value, precision: 3);
    }

    // Posting window 3 makes 3 the close, so window 1 is only worth the open by then. This number
    // MOVES on purpose — it is a prediction about the next post, not a fact about the past.
    [Fact]
    public void WindowValueFor_StandardCamp_WindowOneDropsToTheOpenOnceALaterWindowCloses()
    {
        Assert.Equal(
            0.5,
            HnmCampPricing.WindowValueFor(StandardCamp(), Ls(), sequence: 1, closeWindow: 3, explicitAmount: null)!.Value,
            precision: 3);
    }

    // The snapshot pays NOTHING on a Manual Check In camp: WdCampFinalizer credits the check-in
    // range, so a member is paid for windows that have no EventAttendanceWindow row at all. Three
    // things must agree on this — that finalizer, this resolver, and the Activity hiding its
    // per-window editor.
    [Fact]
    public void DefaultWindowValue_WdCamp_IsNull()
    {
        var ev = StandardCamp();
        ev.AttendanceMode = HnmAttendanceModes.Wd;

        Assert.Null(HnmCampPricing.DefaultWindowValue(ev, Ls(), sequence: 1));
    }

    [Fact]
    public void HonoursWindowAmount_OnlyStandardHnmCamps()
    {
        var standard = StandardCamp();
        var wd = StandardCamp();
        wd.AttendanceMode = HnmAttendanceModes.Wd;
        var timed = new Event { Id = 12, LinkshellId = 1, EventName = "Sky", EventType = "Sky" };

        Assert.True(HnmCampPricing.HonoursWindowAmount(standard));
        Assert.False(HnmCampPricing.HonoursWindowAmount(wd));
        Assert.False(HnmCampPricing.HonoursWindowAmount(timed));
    }

    // Claim/Kill-style windowed events really do pay windowsAttended × DkpPerHour, via the two
    // EndEvent bodies — so unlike a Standard camp they have an honest per-window number to report.
    [Fact]
    public void DefaultWindowValue_NonHnmWindowedEvent_IsDkpPerHour()
    {
        var ev = new Event
        {
            Id = 11,
            LinkshellId = 1,
            EventName = "Dynamis",
            EventType = "Dynamis",
            DkpPerHour = 3,
            WindowCountOverride = 4,
        };

        Assert.Equal(3d, HnmCampPricing.DefaultWindowValue(ev, Ls(), sequence: 2)!.Value, precision: 3);
    }

    // A window count of 1 means "not windowed, pay it by accumulated duration". Quoting a
    // per-window rate for a camp paid by the clock would be fiction.
    [Fact]
    public void DefaultWindowValue_SingleWindowEvent_IsNull()
    {
        var ev = new Event
        {
            Id = 12,
            LinkshellId = 1,
            EventName = "Sky",
            EventType = "Sky",
            DkpPerHour = 3,
            WindowCountOverride = 1,
        };

        Assert.Null(HnmCampPricing.DefaultWindowValue(ev, Ls(), sequence: 1));
    }

    // A missing linkshell must resolve to 0, not throw: ListEventsAsync loads it separately from
    // the events, so a race or a deleted row would otherwise take down the addon's whole poll.
    [Fact]
    public void StandardBonuses_NullLinkshell_ResolvesToZeroRatherThanThrowing()
    {
        var (window, open, close, claim, kill) =
            HnmCampPricing.StandardBonuses(StandardCamp(), linkshell: null, claimed: true, killed: true);

        Assert.Equal(0d, window, precision: 3);
        Assert.Equal(0d, open, precision: 3);
        Assert.Equal(0d, close, precision: 3);
        Assert.Equal(0d, claim, precision: 3);
        Assert.Equal(0d, kill, precision: 3);
    }

    // Ungated bonuses are the caller's job to never see: a camp that wasn't claimed pays no claim
    // bonus even when one is configured.
    [Fact]
    public void StandardBonuses_UnclaimedUnkilledCamp_ZeroesTheOutcomeBonuses()
    {
        var ls = Ls();
        ls.HnmStandardClaimBonus = 1.0;
        ls.HnmStandardKillBonus = 2.0;

        var (window, open, close, claim, kill) =
            HnmCampPricing.StandardBonuses(StandardCamp(), ls, claimed: false, killed: false);

        Assert.Equal(0d, window, precision: 3);   // regular-window rate left unset
        Assert.Equal(0.5, open, precision: 3);
        Assert.Equal(1.5, close, precision: 3);
        Assert.Equal(0d, claim, precision: 3);
        Assert.Equal(0d, kill, precision: 3);
    }

    // The regular-window rate reaches the addon through the same resolved-number channel as the
    // rest — the addon is told what one more window is worth, never the four bonuses, so it can't
    // re-derive the precedence and drift. A middle window that used to quote null now quotes the
    // rate, and the open/close ends quote the rate plus their bonus.
    [Fact]
    public void DefaultWindowValue_RegularWindowRate_ReachesTheAddonsQuote()
    {
        var ls = Ls();
        ls.HnmStandardWindowBonus = 0.25;

        // DefaultWindowValue prices the window being posted as the close, so window 4 quotes
        // rate + close and window 1 quotes rate + open + close.
        Assert.Equal(0.25 + 1.5, HnmCampPricing.DefaultWindowValue(StandardCamp(), ls, sequence: 4)!.Value, precision: 3);
        Assert.Equal(0.25 + 0.5 + 1.5, HnmCampPricing.DefaultWindowValue(StandardCamp(), ls, sequence: 1)!.Value, precision: 3);
    }

    // The Manual Check In open / close bonuses share Event.HnmOpen/CloseBonusOverride with their
    // Standard namesakes — one column per amount, and the camp's MODE decides which linkshell
    // default it falls back to. Same rule the claim / kill overrides have always followed.
    [Fact]
    public void WdAmounts_OpenAndClose_FallBackToTheWdSettings()
    {
        var ls = Ls();
        ls.WdOpenBonus = 1.0;
        ls.WdCloseBonus = 0.5;
        ls.HnmStandardOpenBonus = 99;   // must NOT be read on a Manual Check In camp
        ls.HnmStandardCloseBonus = 99;

        var (_, open, close, _, _) =
            HnmCampPricing.WdAmounts(WdCamp(), ls, claimed: false, killed: false);

        Assert.Equal(1.0, open, precision: 3);
        Assert.Equal(0.5, close, precision: 3);
    }
}
