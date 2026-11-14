using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// The HNM camp rules: which window a camp is on, how many spawn windows it runs, how many
// attendance posts it takes and what they are called, and which windows wipe the roster.
//
// These used to live in DiscordEventMessageBuilder, but the board is only one of their readers:
// the addon API, the Activity app, the window advancer, the finalizers and DKP pricing all ask
// the same questions and must get the same answers. They are pure functions of the Event (no
// clock, no database), so they sit here on their own and the builder only formats messages.
public static class HnmCampRules
{
    // Manual Check In helpers, shared with the interactions controller / finalizer. IsWd gates every Manual Check In
    // board behavior; UsesWindows enables the window heading + Prev/Next advance for Manual Check In monsters
    // that aren't the curated window-cycle HNMs (e.g. Fafnir); EffectiveWindowCount is the per-event
    // window count (the per-linkshell override seeded into WindowCountOverride, else HnmConfig).
    public static bool IsWd(Event ev) =>
        string.Equals(ev.AttendanceMode, HnmAttendanceModes.Wd, StringComparison.OrdinalIgnoreCase);

    public static bool IsHnm(Event ev) =>
        string.Equals((ev.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);

    // A board "uses windows" (shows Window N of M + Prev/Next + the timed countdown) when its
    // monster runs a multi-window spawn cycle — the long-window wyrms OR the short-window
    // kings/dragons (both have a DefaultWindowCadence) — or when it's a Manual Check In camp with a custom count.
    // This is what lights up automatic window advance for Standard-mode kings/dragons, not just Manual Check In.
    // The stamped grid is included so a monster with no BUILT-IN cadence that a linkshell gave one
    // still lights up the window UI.
    public static bool UsesWindows(Event ev) =>
        IsWd(ev)
        || HnmConfig.SupportsWindowAdvance(ev.AssignedMonsterName)
        || HnmConfig.DefaultWindowCadence(ev.AssignedMonsterName) is not null
        || (ev.SpawnWindowCount > 1 && ev.SpawnWindowMinutes > 0);

    // The window number every surface must display: the board, the Activity/web app, and the
    // LSM addon. THIS is the single source of truth for "what window is this camp on" — callers
    // must not re-derive it from HnmWindowNumber, or they drift apart (which is exactly what
    // happened: the board said 18 while the addon and app said 17).
    //
    // Officer model: windows are the pop chances that FOLLOW the initial pop (shown as
    // "Started"), and Window N opens at StartTime + (N-1)xcadence — so Window 1 is AT Started.
    // On a camp that never wipes, what everyone watches is the window being AWAITED — the next pop
    // to look for — which is one past the window that has already opened. Before the camp starts,
    // on the final window, or once it's popped/defeated/finalized there IS no next window, so the
    // awaited window collapses back to the current one ("Window 25 of 25" at the end).
    //
    // Wiping boards (ClearsRosterOnWindowAdvance — the wyrms) used to be excepted here and named
    // the OPENED window instead, on the reasoning that the roster underneath belongs to it. It
    // doesn't. A window is a knife edge, not an hour-long door (see the "Window N passed" line in
    // BuildStartHeading): the pop chance is spent the instant the boundary is reached, and the
    // roster is wiped on that same tick. Everything signed up afterwards is signing up for the
    // NEXT chance — so a Tiamat board 20 minutes past window 7's boundary was heading a
    // freshly-emptied, re-signing roster "Window 7 of 25", naming a window that was already over
    // while its own countdown said "Next window 8".
    //
    // The visibility that exception was protecting is unaffected: the heading number still changes
    // on exactly the tick the roster is cleared (both move with HnmWindowNumber), so "the new
    // number over an emptied roster" is still one edit. Only WHICH number that is changes.
    //
    // This also un-breaks the addon's auto-post, which files its scheduled capture against the
    // wire's hnmWindowNumber and documents it as the awaited window (render/auto_post.lua: "at
    // boundary + delay the window that has just opened IS the awaited one"). Fed the opened window
    // on a wyrm, that seq was one low and collided with the capture already taken for it, so the
    // post-boundary snapshot was dropped as "already captured".
    public static int FocusWindow(Event ev) =>
        HasNextWindow(ev) ? OpenedWindow(ev) + 1 : OpenedWindow(ev);

    // The window currently IN PROGRESS — the one the board's live roster belongs to, and the one
    // the advancer moves. Always the stored counter, clamped to the camp's real window count.
    public static int OpenedWindow(Event ev) =>
        Math.Clamp(ev.HnmWindowNumber, 1, EffectiveWindowCount(ev));

    // The spawn window a post made RIGHT NOW is taken in, for EventAttendanceWindow.SpawnWindow.
    // Null on a camp with no window grid: there is no spawn window to name, and "window 1" would be
    // a guess.
    public static int? SpawnWindowNow(Event ev) =>
        UsesWindows(ev) && EffectiveWindowCount(ev) > 1 ? OpenedWindow(ev) : null;

    // Whether another window is still coming: what decides the "Next window N <countdown>" line,
    // and whether FocusWindow looks ahead at all.
    public static bool HasNextWindow(Event ev) =>
        UsesWindows(ev)
        && OpenedWindow(ev) < EffectiveWindowCount(ev)
        && ev.NextWindowAt is not null
        && ev.WdFinalizedAt is null
        && ev.HnmDefeatedAt is null;

    // Does stepping this camp's window throw its roster away? The wyrms do, in BOTH attendance
    // modes; the kings/dragons are one continuous camp and never do. This is the SAME condition
    // HnmWindowAdvanceBackgroundService gates its clear on — named once here so the number the
    // board prints and the roster underneath it can't disagree about which windows wipe.
    //
    // Manual Check In used to be excluded here, on the reasoning that members X-in per window so
    // the grid could stand. It can't: a Tiamat camp re-forms every hour whoever is tracking it, and
    // leaving the parties seated meant hour 12 was still showing the people who signed up at hour 1.
    // The check-in ledger is untouched by the wipe (ClearWindowRosterAsync spares any participation
    // carrying a WdArrivalWindow), so what a Manual Check In camp actually pays is unchanged — only
    // the party grid re-signs.
    public static bool ClearsRosterOnWindowAdvance(Event ev) =>
        HnmConfig.WindowAdvanceWipesRoster(ev.AssignedMonsterName);

    // The SPAWN window count: how many pop chances the camp sits through. This is the "of M" the
    // board prints, the ceiling the advance poller marches to, and the scale attendance posts
    // against. It is NOT how many attendance posts the camp takes — see Event.WindowCountOverride,
    // which is that other number ("Forces the post-by-window count"), 2 on a king/dragon against
    // its 7 spawn windows.
    //
    // The monster's built-in cadence therefore wins over the override. Reading the override FIRST
    // is what let an addon-made camp — which stores its 2-post count there, correctly — report 2
    // spawn windows: the board read "Window N of 2" and HnmWindowAdvanceBackgroundService stopped
    // there, five windows early.
    //
    // An override of exactly 1 still short-circuits, and that is deliberate: 1 means "this camp is
    // NOT windowed, pay it by accumulated duration", which EventBreakPolicy reads to decide the
    // camp keeps its Break Room. Letting the cadence overrule that would flip a timed camp onto the
    // windowed payout path and strand its members with no way to stop the clock.
    // ev.SpawnWindowCount — the grid this camp captured at creation from its linkshell's monster
    // setup — wins over the built-in cadence, and null falls straight through to the old chain, so
    // a camp created before per-linkshell setups behaves byte-identically.
    //
    // The WindowCountOverride == 1 short-circuit stays first for every camp WITHOUT a stamped Window
    // Method: 1 means "this camp is NOT windowed, pay it by accumulated duration", which
    // EventBreakPolicy reads to decide the camp keeps its Break Room.
    //
    // A camp WITH a stamped method ignores the override entirely. It is an HNM-type camp whose
    // monster setup decided how it posts — and every HNM camp is windowed in EventBreakPolicy
    // regardless — so the override's "not windowed" meaning doesn't apply to it. Reading it anyway
    // is what pinned every custom NM camp to "Window 1 of 1" whatever its Monster Setups grid said,
    // and what would cap an addon-made camp (which stores its POST count there) at 2 windows.
    public static int EffectiveWindowCount(Event ev)
    {
        var stamped = ev.WindowMethod is not null;
        return Math.Clamp(
            ev.WindowCountOverride == 1 && !stamped
                ? 1
                : ev.SpawnWindowCount
                    ?? HnmConfig.DefaultWindowCadence(ev.AssignedMonsterName)?.Windows
                    ?? (stamped ? null : ev.WindowCountOverride)
                    ?? HnmConfig.GetWindowCount(ev.AssignedMonsterName ?? ev.EventName),
            1, HnmConfig.MaxWindow);
    }

    // Minutes between window openings for THIS camp. The companion to EffectiveWindowCount, and its
    // one home, so the count and the cadence can never come from different grids.
    public static int EffectiveWindowMinutes(Event ev) =>
        ev.SpawnWindowMinutes ?? HnmConfig.WindowAdvanceMinutes(ev.AssignedMonsterName);

    // The ATTENDANCE POST count: how many times the roster is read. The companion to
    // EffectiveWindowCount above, and deliberately a different number — a Standard king/dragon
    // camp takes an Open and a Close (2) across the 7 spawn windows it sits through.
    //
    // THIS is what names a window. HnmConfig.GetDefaultWindowLabel returns "Open"/"Close" only at
    // a count of 2, so handing it the spawn count instead labelled every king/dragon window
    // "Window N" — the exact question that comment warns about ("which window of the seven?").
    //
    // For a curated HNM it comes off the MONSTER, not off WindowCountOverride. That column holds
    // the post count on an addon-made camp but the SPAWN count on an app-made one (HnmEventSeeder
    // stamps it there), so trusting it would name the same Behemoth camp's windows one way or the
    // other depending on where it was filed. How many times you read the roster is a property of
    // the camp, not of the form it was created on.
    //
    // Everything else — NMs, testing presets, custom events — keeps the override / name lookup,
    // which is the only signal those carry.
    //
    // One home, because it is applied wherever a window is named or counted: the Activity DTO, the
    // addon's event list, snapshot ingestion, and submission approval. They must never disagree, or
    // one camp ends up with two different names for the same window.
    //
    // A camp stamped with a Window Method (see WindowMethodFor) answers from the method instead:
    //   Open/Close -> 2, however many spawn windows it sits through.
    //   Window N        -> one post per spawn window, or HnmConfig.MaxWindow for an open-ended camp.
    public static int AttendancePostCount(Event ev) =>
        WindowMethodFor(ev) switch
        {
            MonsterWindowMethods.OpenClose => 2,
            MonsterWindowMethods.WindowN => WindowsOpenEnded(ev) ? HnmConfig.MaxWindow : EffectiveWindowCount(ev),
            _ => LegacyAttendancePostCount(ev),
        };

    // The post count as it was decided before Window Methods existed, from the HnmConfig tables.
    // Every camp without a stamped method still runs on it, so an event made before the setting
    // shipped (or a Manual Check In camp, or a non-HNM event) behaves exactly as it always did.
    public static int LegacyAttendancePostCount(Event ev) =>
        Math.Clamp(
            HnmConfig.IsTrueHnm(ev.AssignedMonsterName)
                ? HnmConfig.GetWindowCount(ev.AssignedMonsterName)
                : ev.WindowCountOverride ?? HnmConfig.GetWindowCount(ev.EventName),
            1, HnmConfig.MaxWindow);

    // The Window Method that decides how THIS camp posts, or null when none does.
    //
    // Only a Standard-mode HNM camp. A Manual Check In camp is paid from check-ins rather than from
    // posts, and a non-HNM event has no posts to shape, so both stay on the legacy rules even if a
    // method was stamped on them. Normalize drops anything unrecognised to null for the same reason.
    public static string? WindowMethodFor(Event ev) =>
        IsHnm(ev) && !IsWd(ev) ? MonsterWindowMethods.Normalize(ev.WindowMethod) : null;

    // Does this camp take a NAMED Open and Close (rather than numbered windows)? THE question every
    // "post count == 2" test used to stand in for. It isn't the same question once Window N can
    // have exactly two spawn windows: those are "Window 1" and "Window 2", not an Open and a Close.
    public static bool PostsOpenAndClose(Event ev) =>
        WindowMethodFor(ev) switch
        {
            MonsterWindowMethods.OpenClose => true,
            MonsterWindowMethods.WindowN => false,
            _ => LegacyAttendancePostCount(ev) == 2,
        };

    // A Window N camp whose monster has no window grid: it posts numbered windows for as long as
    // the camp runs, up to HnmConfig.MaxWindow, and there is no "of M" to print.
    public static bool WindowsOpenEnded(Event ev) =>
        WindowMethodFor(ev) == MonsterWindowMethods.WindowN && EffectiveWindowCount(ev) < 2;

    // The highest NON-kill sequence the camp accepts.
    //
    // A camp with no Window Method keeps the spawn count it was always checked against. With one,
    // an Open/Close camp accepts exactly its Open and Close — even when its monster has no spawn
    // grid at all (EffectiveWindowCount 1), which is the custom NM that couldn't post a Close
    // before — and a Window N camp accepts up to the larger of its spawn and post counts, so an
    // open-ended one reaches HnmConfig.MaxWindow.
    public static int MaxPostSequence(Event ev)
    {
        if (WindowMethodFor(ev) is null)
        {
            return EffectiveWindowCount(ev);
        }
        return PostsOpenAndClose(ev)
            ? 2
            : Math.Max(EffectiveWindowCount(ev), AttendancePostCount(ev));
    }

    // The highest sequence a KILL post may take: one past the last window, which is where the addon
    // files it (its post count + 1), capped one past the server's window ceiling.
    public static int KillPostCeiling(Event ev) =>
        Math.Min(MaxPostSequence(ev) + 1, HnmConfig.MaxWindow + 1);

    // The default label for window `sequenceNumber` of this camp: "Open" / "Close" / "Kill" on an
    // Open/Close camp, null (numbered) on any other.
    public static string? DefaultWindowLabel(Event ev, int sequenceNumber) =>
        HnmConfig.GetDefaultWindowLabel(sequenceNumber, PostsOpenAndClose(ev));
}
