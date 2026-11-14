using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// The HNM camp board's own buttons: Manual Check In's Check In / Check Out, "🔒 Stay Next Window",
// the read-only window viewer (◀ View Previous Window and its paging), and "📋 View Camp Details"
// on the defeated note. End Camp is EndCampHandler's.
public sealed class HnmCampBoardHandler : DiscordInteractionHandlerBase, IDiscordInteractionHandler
{
    private readonly ApplicationDbContext _db;
    private readonly DiscordEventChannelQueue _eventQueue;
    private readonly DiscordSignupIdentity _identity;
    private readonly DiscordBoardSupport _board;

    public HnmCampBoardHandler(
        DiscordInteractionContext interaction,
        ApplicationDbContext db,
        DiscordEventChannelQueue eventQueue,
        DiscordSignupIdentity identity,
        DiscordBoardSupport board)
        : base(interaction)
    {
        _db = db;
        _eventQueue = eventQueue;
        _identity = identity;
        _board = board;
    }

    public IReadOnlyList<DiscordInteractionRoute> Routes => new[]
    {
        // "🔒 Stay Next Window" → the clicker toggles the lock on their OWN slot so it
        // survives the next automatic window turnover (window-cycle HNM boards).
        Component(DiscordEventMessageBuilder.LockNextWindowPrefix, nameof(HandleLockNextWindowAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.LockNextWindowPrefix);
            return await HandleLockNextWindowAsync(eventId, AppUserId, cancellationToken);
        }),

        // The window viewer's own navigation, sent from INSIDE the ephemeral it opened: the jump
        // list (value = the window) and the ◀ / ▶ arrows (window baked into the id). Checked ahead
        // of the board button below purely to keep the three ids together — they don't actually
        // overlap under StartsWith, since "evt:winviewpick:" diverges at the 'p'.
        Component(DiscordEventMessageBuilder.WindowViewPickPrefix, nameof(HandleViewPrevWindowAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.WindowViewPickPrefix);
            return await HandleViewPrevWindowAsync(
                eventId,
                int.TryParse(SelectedValue(Data), out var picked) ? picked : null,
                cancellationToken);
        }),
        Component(DiscordEventMessageBuilder.WindowViewPrefix, nameof(HandleViewPrevWindowAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.WindowViewPrefix);
            return await HandleViewPrevWindowAsync(
                eventId,
                ParseTrailingWindowNumber(CustomId, DiscordEventMessageBuilder.WindowViewPrefix),
                cancellationToken);
        }),

        // "📋 View Camp Details" on the DEFEATED note → anyone in the linkshell: the camp that just
        // ended, read-only. The trailing id is an EventHistory, not an event — the board row is
        // recycled for the next pop, so an event id here would open whatever is being camped now.
        Component(DiscordEventMessageBuilder.PastEventDetailsPrefix, nameof(HandlePastEventDetailsAsync), async cancellationToken =>
        {
            var historyId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.PastEventDetailsPrefix);
            return await HandlePastEventDetailsAsync(historyId, AppUserId, cancellationToken);
        }),

        // "◀ View Previous Window" on the board → anyone: read-only ephemeral opening on the newest
        // captured roster, navigable from there. Boards posted before this change send the same id
        // from what was "Prev Window", so an old button now opens the viewer instead of stepping the
        // counter.
        Component(DiscordEventMessageBuilder.ViewPrevWindowPrefix, nameof(HandleViewPrevWindowAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.ViewPrevWindowPrefix);
            return await HandleViewPrevWindowAsync(eventId, requestedWindow: null, cancellationToken);
        }),

        // "✅ Check In (this window)" (Manual Check In boards) → member self-serve attendance for the current window.
        Component(DiscordEventMessageBuilder.XinPrefix, nameof(HandleXinAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.XinPrefix);
            return await HandleXinAsync(eventId, AppUserId, cancellationToken);
        }),

        // "🚪 Check Out" (Manual Check In boards) → member leaves mid-camp; credit stops at the current window.
        Component(DiscordEventMessageBuilder.CheckOutPrefix, nameof(HandleCheckOutAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.CheckOutPrefix);
            return await HandleCheckOutAsync(eventId, AppUserId, cancellationToken);
        }),
    };

    // The window-roster viewer on a board whose roster wipes each window (the wyrms, either
    // attendance mode). READ-ONLY in every direction: it reads EventWindowRosterSnapshots and
    // touches nothing else, so nothing here can move the board or anyone else's view. Open to
    // everyone — it shows the same rosters the board displayed publicly at the time, so there is no
    // gate to enforce.
    //
    // `requestedWindow` is null for the board button (open on the newest capture) and set when the
    // member pages inside the ephemeral with its ◀ / ▶ arrows or jump list. Because that ephemeral
    // is private to its clicker, paging MORPHS it in place (PickerResponse) instead of stacking a
    // fresh message per press — one member browsing the camp's history is invisible to the channel.
    //
    // There is no "Next Window" counterpart any more: the counter belongs to the cadence
    // (HnmWindowAdvanceBackgroundService) alone. Everything here looks BACKWARDS at what the wipe
    // already captured.
    private async Task<IActionResult> HandleViewPrevWindowAsync(
        int eventId, int? requestedWindow, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That board is no longer available.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        if (!DiscordEventMessageBuilder.UsesWindows(ev))
        {
            return Ephemeral("This board doesn't use windows.");
        }
        // The window in view is on the PRINTED scale (FocusWindow), the same number the board and
        // the captures carry — reading the raw counter here hid the camp's first capture, which is
        // taken at commencement while the counter is still on 1.
        var liveWindow = DiscordEventMessageBuilder.FocusWindow(ev);
        if (liveWindow <= 1)
        {
            return Ephemeral("This camp is still on its first window — there's nothing before it yet.");
        }

        // Every window this camp has a capture for, oldest first. Normally that's an unbroken
        // 1..liveWindow-1, but a camp that sat through a boundary while the app was down is
        // missing that window (the advancer deliberately skips the clear rather than wipe a live
        // roster over an unwatched boundary). So both the arrows and the jump list are built from
        // what EXISTS rather than from a computed range — nothing can offer a window that would
        // come back empty.
        var captured = await _db.EventWindowRosterSnapshots
            .Where(s => s.EventId == eventId && s.WindowNumber < liveWindow)
            .Select(s => s.WindowNumber)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);
        if (captured.Count == 0)
        {
            return Ephemeral(
                $"No roster was captured for a window before {liveWindow} — "
                + "either nobody was signed up, or this camp turned over while the bot was down.");
        }

        // The board button opens on the newest capture; so does an arrow naming a window that has
        // since gone (a board re-post clears the history out from under a stale ephemeral).
        var window = requestedWindow is { } asked && captured.Contains(asked) ? asked : captured[^1];

        var rows = await _db.EventWindowRosterSnapshots
            .Where(s => s.EventId == eventId && s.WindowNumber == window)
            .OrderBy(s => s.AllianceSortOrder).ThenBy(s => s.PartySortOrder).ThenBy(s => s.SlotSortOrder)
            .ToListAsync(cancellationToken);

        return PickerResponse(
            RenderWindowSnapshot(ev, window, rows),
            DiscordEventMessageBuilder.BuildWindowViewerComponents(eventId, window, captured));
    }

    // The camp behind the defeated note — what it paid, who was on it, what dropped.
    //
    // WHY THIS EXISTS. Ending a camp replaces its board with three lines saying the monster is down
    // and when it repops. Everything the camp actually WAS goes with it: the roster, the DKP, the
    // loot, the lotteries. That record lives on a web page nobody in the channel has a link to, and
    // the recycled board will shortly be advertising the next pop over the top of it. This is the
    // way back.
    //
    // Read-only and ephemeral. Anyone in the linkshell may open it — it is their own attendance and
    // their own DKP — but nobody outside it, because a Discord channel can hold guests.
    private async Task<IActionResult> HandlePastEventDetailsAsync(
        int eventHistoryId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventHistoryId <= 0)
        {
            return Ephemeral("That camp isn't recognized.");
        }

        var history = await _db.EventHistories
            .AsNoTracking()
            .Include(h => h.AppUserEventHistories)
            .FirstOrDefaultAsync(h => h.Id == eventHistoryId, cancellationToken);
        if (history is null)
        {
            return Ephemeral("That camp's record has been deleted.");
        }

        if (string.IsNullOrWhiteSpace(appUserId))
        {
            return Ephemeral(
                "Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var isMember = await _db.AppUserLinkshells.AnyAsync(
            m => m.AppUserId == appUserId && m.LinkshellId == history.LinkshellId, cancellationToken);
        if (!isMember)
        {
            return Ephemeral("That camp belongs to a linkshell you're not in.");
        }

        var loot = await _db.EventLootDetails
            .AsNoTracking()
            .Where(l => l.EventHistoryId == eventHistoryId)
            .OrderBy(l => l.Id)
            .Select(l => new { l.ItemName, l.ItemWinner, l.WinningDkpSpent })
            .ToListAsync(cancellationToken);

        var captures = await _db.ClaimShieldCaptures
            .AsNoTracking()
            .Where(c => c.EventHistoryId == eventHistoryId)
            .OrderByDescending(c => c.CapturedAtUtc)
            .Select(c => new ClaimShieldBoardCapture(
                c.MonsterName, c.Won, c.TotalPlayers, c.CapturedAtUtc,
                c.Members.OrderBy(m => m.Id)
                    .Select(m => new ClaimShieldBoardMember(m.CharacterName, m.Matched))
                    .ToList()))
            .ToListAsync(cancellationToken);

        // Each person's DKP from the ledger, the same figure Past Events shows in the app.
        var dkp = await PastEventDkp.LoadAsync(_db, new[] { history.Id }, cancellationToken);

        return Ephemeral(RenderPastEventDetails(history, dkp, loot.Count, loot.Select(
            l => (l.ItemName, l.ItemWinner, l.WinningDkpSpent)).ToList(), captures));
    }

    // Plain text, not an embed: this has to come back inside Discord's 3-second interaction window,
    // and a roster of eighteen with amounts is already most of an embed's field budget.
    private static string RenderPastEventDetails(
        EventHistory history,
        PastEventDkpTotals dkpTotals,
        int lootCount,
        IReadOnlyList<(string? ItemName, string? ItemWinner, int? WinningDkpSpent)> loot,
        IReadOnlyList<ClaimShieldBoardCapture> captures)
    {
        var lines = new List<string>
        {
            $"📋 **{history.EventName ?? "Camp"}** · past event, read-only",
        };

        var when = new List<string>();
        if (history.StartTime is { } start)
        {
            when.Add($"Started <t:{ToUnixSeconds(start)}:f>");
        }
        if (history.EndTime is { } end)
        {
            when.Add($"ended <t:{ToUnixSeconds(end)}:t>");
        }
        if (history.Duration is { } hours and > 0)
        {
            var span = TimeSpan.FromHours(hours);
            when.Add($"{(int)span.TotalHours}h {span.Minutes:00}m");
        }
        if (when.Count > 0) lines.Add(string.Join(" · ", when));
        if (!string.IsNullOrWhiteSpace(history.EventLocation)) lines.Add($"📍 {history.EventLocation}");

        // The roster, with what each person was credited. Ordered by amount then name, so the
        // question this is usually opened to answer -- "what did I get" -- is answered by scanning
        // rather than by reading all eighteen.
        var roster = history.AppUserEventHistories
            .Where(p => !string.IsNullOrWhiteSpace(p.CharacterName))
            .OrderByDescending(p => dkpTotals.For(p) ?? 0)
            .ThenBy(p => p.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        lines.Add(string.Empty);
        if (roster.Count == 0)
        {
            lines.Add("_Nobody was credited for this camp._");
        }
        else
        {
            var total = dkpTotals.TotalFor(roster);
            lines.Add($"**Roster ({roster.Count})** — {total:0.##} DKP in total");
            foreach (var member in roster.Take(PastEventRosterLimit))
            {
                var dkp = dkpTotals.For(member) is { } amount and > 0 ? $" — **{amount:0.##}**" : string.Empty;
                var job = string.IsNullOrWhiteSpace(member.JobName)
                    ? string.Empty
                    : $" ({member.JobName}{(string.IsNullOrWhiteSpace(member.SubJobName) ? "" : "/" + member.SubJobName)})";
                lines.Add($"• {member.CharacterName}{job}{dkp}");
            }
            if (roster.Count > PastEventRosterLimit)
            {
                lines.Add($"-# +{roster.Count - PastEventRosterLimit} more on the event page.");
            }
        }

        if (lootCount > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"**Loot ({lootCount})**");
            foreach (var drop in loot.Take(PastEventLootLimit))
            {
                // 0 or null both mean "no DKP changed hands" -- a free drop, or Loot Council.
                var spent = drop.WinningDkpSpent is > 0
                    ? $" for **{drop.WinningDkpSpent}** DKP"
                    : string.Empty;
                lines.Add($"• {drop.ItemName} → {drop.ItemWinner}{spent}");
            }
            if (lootCount > PastEventLootLimit)
            {
                lines.Add($"-# +{lootCount - PastEventLootLimit} more on the event page.");
            }
        }

        if (ClaimShieldBoardSection.Build(captures) is { Length: > 0 } shield)
        {
            lines.Add(string.Empty);
            lines.Add(shield);
        }

        var text = string.Join("\n", lines);
        // Discord rejects a message body over 2000 outright, so the trim is not optional.
        return text.Length <= 1990 ? text : text[..1990].TrimEnd() + "\n-# (truncated)";
    }

    // An alliance is 18, and a camp roster is rarely more. Past that the event page is the right
    // place to read it -- an ephemeral caps at 2000 characters and a truncated roster in the middle
    // of a list reads as data loss.
    private const int PastEventRosterLimit = 24;
    private const int PastEventLootLimit = 12;

    private static long ToUnixSeconds(DateTime value)
        => ((DateTimeOffset)DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds();

    // The window out of a viewer arrow's "{eventId}:{window}" tail, or null when it carries none
    // (in which case the caller opens on the newest capture).
    private static int? ParseTrailingWindowNumber(string customId, string prefix)
    {
        var tail = customId[prefix.Length..];
        var colon = tail.IndexOf(':');
        return colon >= 0 && int.TryParse(tail[(colon + 1)..], out var window) ? window : null;
    }

    // Renders a captured window roster as plain text for the ephemeral. Grouped alliance → party in
    // the board's own order, since that's the shape people remember seeing. Kept plain text on
    // purpose: this has to come back inside Discord's 3s
    // interaction window — and it re-renders on every arrow press, so the cost is paid per page,
    // not once. The heading names both the window in view and the live one, which is the only thing
    // orienting a reader several pages back in the history.
    private static string RenderWindowSnapshot(
        Event ev, int windowNumber, IReadOnlyList<EventWindowRosterSnapshot> rows)
    {
        var effectiveMax = DiscordEventMessageBuilder.EffectiveWindowCount(ev);
        var lines = new List<string>
        {
            // Said outright, above the roster. What's underneath is a roster with crowns and locks
            // on it, laid out exactly like the live board — the one thing it does NOT look like is
            // history, and a reader who arrived here by pressing an arrow has no other cue that
            // they're several hours back. Discord's own "Only you can see this" covers privacy but
            // says nothing about the roster being frozen.
            $"📜 **Past window · read-only** — nothing here changes the board.",
            $"**{ev.EventName} — Window {windowNumber} of {effectiveMax}** · the camp is now on window {DiscordEventMessageBuilder.FocusWindow(ev)}",
        };
        if (rows.Count == 0)
        {
            lines.Add("_Nobody was signed up for that window._");
            return string.Join("\n", lines);
        }

        string? currentGroup = null;
        foreach (var row in rows)
        {
            var group = $"{row.AllianceName ?? "Alliance"} · {row.PartyName ?? "Party"}";
            if (group != currentGroup)
            {
                lines.Add(string.Empty);
                lines.Add($"__{group}__");
                currentGroup = group;
            }

            // "PLD/WAR", "PLD", or the role when no job was committed — matching how the board reads.
            var job = row.MainJob is { Length: > 0 }
                ? (row.SubJob is { Length: > 0 } ? $"{row.MainJob}/{row.SubJob}" : row.MainJob)
                : row.Role;
            var marks = string.Concat(
                row.IsAllianceLeader || row.IsPartyLeader ? " 👑" : string.Empty,
                row.WasLocked ? " 🔒" : string.Empty);
            lines.Add(
                $"• {row.CharacterName ?? "(unnamed)"}"
                + (job is { Length: > 0 } ? $" — {job}" : string.Empty)
                + (row.SlotLabel is { Length: > 0 } ? $" {row.SlotLabel}" : string.Empty)
                + marks);
        }

        var locked = rows.Count(r => r.WasLocked);
        lines.Add(string.Empty);
        lines.Add($"_{rows.Count} signed up_" + (locked > 0 ? $" · _{locked} carried over via 🔒_" : string.Empty));
        return string.Join("\n", lines);
    }

    // Manual Check In "X-in (this window)": records the clicker's arrival window on their AppUserEvent.
    // The finalizer credits windows arrival..last, so a single click covers every later window;
    // re-clicking a later window just overwrites the arrival (the Manual Check In "x2 -> x3" correction).
    // Identity resolution (member / placeholder / outside onboard / character picker) is fully
    // delegated to ResolveSignupContextAsync — the same path Sign Up uses.
    internal Task<IActionResult> HandleXinAsync(int eventId, string? appUserId, CancellationToken cancellationToken) =>
        _board.RetryIfParticipationRaceAsync(() => HandleXinOnceAsync(eventId, appUserId, cancellationToken));

    private async Task<IActionResult> HandleXinOnceAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That board isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        if (!DiscordEventMessageBuilder.IsWd(ev))
        {
            return Ephemeral("This board doesn't use Manual Check In.");
        }
        if (ev.WdFinalizedAt is not null)
        {
            return Ephemeral("This camp has already been processed — attendance is locked.");
        }

        var ctx = await _identity.ResolveSignupContextAsync(ev, appUserId, $"xin:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }
        var characterName = ctx.CharacterName!;
        // Credit is recorded against the window the BOARD shows — the one being awaited — so the
        // number in this reply matches the heading directly above the button. See
        // DiscordEventMessageBuilder.FocusWindow. HnmCampPopService stamps WdPopWindow off the
        // same helper, so arrival and pop stay on one scale and total credit is unchanged.
        var arrivalWindow = DiscordEventMessageBuilder.FocusWindow(ev);
        var openedWindow = Math.Clamp(ev.HnmWindowNumber, 1, DiscordEventMessageBuilder.EffectiveWindowCount(ev));
        var checkedInEarly = arrivalWindow > openedWindow;

        // First x-in makes the camp live (mirrors the addon participation path), so the board
        // shows "Started" and the finalizer has a sensible StartTime.
        ev.CommencementStartTime ??= DateTime.UtcNow;

        var existing = ctx.AppUserId is not null
            ? await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.AppUserId == ctx.AppUserId, cancellationToken)
            : await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.AppUserId == null && item.DiscordUserId == ctx.DiscordUserId, cancellationToken);
        if (existing is not null)
        {
            existing.CharacterName = characterName;
            existing.WdArrivalWindow = arrivalWindow; // overwrite = the late-arrival correction
            existing.WdDepartureWindow = null;        // checking in clears any prior check-out
            existing.IsVerified = true;
            existing.IsQuickJoin = true;
            existing.StartTime ??= ev.CommencementStartTime;
        }
        else
        {
            _db.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = ctx.AppUserId,
                DiscordUserId = ctx.DiscordUserId,
                EventId = eventId,
                CharacterName = characterName,
                WdArrivalWindow = arrivalWindow,
                IsVerified = true,
                IsQuickJoin = true,
                StartTime = ev.CommencementStartTime,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        _eventQueue.Enqueue(eventId); // refresh the board roster off the 3s window
        return Ephemeral(
            (checkedInEarly
                ? $"✅ You checked in **before Window {arrivalWindow} opens** — you'll get credit for Window {arrivalWindow} through the kill. "
                : $"✅ You're checked in for **Window {arrivalWindow}** — you'll get credit for Window {arrivalWindow} through the kill. ")
            + "Arrived late? Tap **Check In** again on a later window to correct it.");
    }

    // "🚪 Check Out" (Manual Check In boards): the member is leaving mid-camp. Records their
    // departure window so credit stops there (they keep DKP for arrival..this window, inclusive).
    // They can Check In again to come back.
    private async Task<IActionResult> HandleCheckOutAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That board isn't recognized.");
        }
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        if (!DiscordEventMessageBuilder.IsWd(ev) || ev.WdFinalizedAt is not null)
        {
            return Ephemeral("This board isn't accepting check-outs.");
        }

        // Find the clicker's participation by their linked account OR their Discord id (placeholder/
        // outside members are keyed by Discord id). They must already be checked in.
        var uid = appUserId;
        var did = Interaction.DiscordUserId;
        var existing = await _db.AppUserEvents.FirstOrDefaultAsync(p =>
            p.EventId == eventId
            && ((uid != null && p.AppUserId == uid) || (did != null && p.DiscordUserId == did)),
            cancellationToken);
        if (existing?.WdArrivalWindow is not { } arrival)
        {
            return Ephemeral("You're not checked in, so there's nothing to check out of.");
        }

        // Same scale as check-in and the pop window: the board's number. Clamped to >= arrival so
        // checking out can never record a departure before the member arrived.
        var departWindow = Math.Clamp(
            DiscordEventMessageBuilder.FocusWindow(ev), arrival, DiscordEventMessageBuilder.EffectiveWindowCount(ev));
        existing.WdDepartureWindow = departWindow;
        await _db.SaveChangesAsync(cancellationToken);

        _eventQueue.Enqueue(eventId);
        return Ephemeral(
            $"🚪 Checked out at **Window {departWindow}** — you'll get credit for Windows {arrival} through {departWindow}. " +
            "Tap **Check In** again if you come back.");
    }

    // "🔒 Stay Next Window" → the clicker toggles the lock on their OWN slot so it survives
    // the automatic window-turnover wipe. Shared board button (Discord can't gate per-user), so
    // the handler resolves the clicker (like Withdraw / Make Me Party Lead), toggles their
    // slot, and confirms privately — the board's 🔒 marker + count is the shared feedback.
    private async Task<IActionResult> HandleLockNextWindowAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        // The button only shows on Standard windowed HNM boards, but re-check on click (a stale
        // board could surface it, and there's nothing to "stay" for without a window advance).
        if (!DiscordEventMessageBuilder.UsesWindows(ev))
        {
            return Ephemeral("This board doesn't use windows, so there's nothing to stay for.");
        }

        var identity = await _identity.ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;

        var nowLocked = await EventPartySignupService.ToggleStayNextWindowAsync(
            _db, eventId, idAppUser, idDiscord, cancellationToken);
        if (nowLocked is null)
        {
            return Ephemeral("You need to hold a slot to stay next window — sign up first.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        _eventQueue.Enqueue(eventId); // refresh the board so the 🔒 marker + count update
        return Ephemeral(nowLocked.Value
            ? "🔒 You're locked in — you'll keep your slot when an officer advances the window."
            : "🔓 Lock removed — you'll be cleared on the next window like everyone else.");
    }
}
