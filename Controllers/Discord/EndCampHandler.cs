using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// "🏁 End Camp / Enter ToD" on every windowed HNM board: the button that opens the modal, and the
// modal's submit, which logs the Time of Death and ends the camp through HnmCampPopService. Both
// need Manage events.
public sealed class EndCampHandler : DiscordInteractionHandlerBase, IDiscordInteractionHandler
{
    // "🏁 End Camp / Enter ToD" modal (every windowed HNM board — Standard + Manual Check In).
    // custom_id carries the eventId; the fields capture the Time of Death (with seconds), NQ/HQ
    // (only for the three merge-pair families), the camp's outcome, the window it popped on, and
    // the auto-re-post lead.
    //
    // That is FIVE rows on an HQ family and Discord's modal cap is exactly five, which is what
    // decided the outcome field's shape: Claimed and Killed were briefly two Yes/No dropdowns, and
    // they are one three-way dropdown again because pop window + re-post need the row back. Nothing
    // is lost in the merge — "somebody else claimed it, and we killed it" isn't a state that exists,
    // so the 2x2 only ever had three reachable corners.
    //
    // The day number is still not asked: the event form owns it, and it's the one field here the
    // officer would be re-typing rather than reporting. The wire ids keep the "wdpop" spelling so
    // boards posted before this stopped being Manual Check In-only keep working.
    private const string WdPopModalPrefix = "evt:wdpopmodal:";
    private const string WdPopTodFieldId = "wdpop_tod";
    // The Date half of the Time of Death. Its own box so a kill that is not from today can be
    // entered without the officer having to know that the time box would have taken a date too.
    private const string WdPopTodDateFieldId = "wdpop_tod_date";
    private const string WdPopHqFieldId = "wdpop_hq";
    private const string WdPopOutcomeFieldId = "wdpop_outcome";
    // Retired as a QUESTION — the Date box took its row — but still read on submit, so a modal
    // opened before the picker was removed still records the window its officer actually picked.
    // (WdPopWindowUnknownValue went with it: "unknown" and absent both read as null here, and null
    // is now the ordinary path rather than an opt-in.)
    private const string WdPopWindowFieldId = "wdpop_window";
    private const string WdPopRepostFieldId = "wdpop_repost";
    // Retired — no longer rendered, but still READ, so a modal opened before the field changed and
    // submitted afterwards is still recorded correctly instead of silently taking a default. A
    // Discord modal can sit open on someone's screen indefinitely, so both spellings of the outcome
    // have to keep parsing for as long as an old one might still be sitting there.
    private const string WdPopDayFieldId = "wdpop_day";
    private const string WdPopClaimFieldId = "wdpop_claim";
    private const string WdPopKillFieldId = "wdpop_kill";

    private readonly ApplicationDbContext _db;
    private readonly TimeZoneConversionService _timeZones;
    private readonly DiscordPermissionGates _gates;

    public EndCampHandler(
        DiscordInteractionContext interaction,
        ApplicationDbContext db,
        TimeZoneConversionService timeZones,
        DiscordPermissionGates gates)
        : base(interaction)
    {
        _db = db;
        _timeZones = timeZones;
        _gates = gates;
    }

    public IReadOnlyList<DiscordInteractionRoute> Routes => new[]
    {
        // "🏁 Pop / End Camp" (Manual Check In boards) → officers only: open the pop-window + ToD modal.
        Component(DiscordEventMessageBuilder.WdPopPrefix, nameof(HandleWdPopButtonAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.WdPopPrefix);
            return await HandleWdPopButtonAsync(eventId, AppUserId, cancellationToken);
        }),

        // "🏁 Pop / End Camp" modal (every windowed HNM board) → officers only: log the ToD, cap
        // credit at the pop window, record claim/kill, tear the camp down, and stage its roster as
        // a pending review row in the Event System page's attendance sections. Delegates to the shared HnmCampPopService.
        Modal(WdPopModalPrefix, nameof(HandleEndCampModalAsync), async cancellationToken =>
            await HandleEndCampModalAsync(CustomId, Data, Interaction.DiscordUserId!, cancellationToken)),
    };

    // The End Camp modal's submit. `discordUserId` is the submitter, already read from the payload.
    private async Task<IActionResult> HandleEndCampModalAsync(
        string customId, JsonElement data, string discordUserId, CancellationToken cancellationToken)
    {
        var popEventId = int.TryParse(customId[WdPopModalPrefix.Length..], out var pid) ? pid : 0;
        if (popEventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == popEventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        if (!DiscordEventMessageBuilder.UsesWindows(ev))
        {
            return Ephemeral("This board doesn't use windows.");
        }
        if (ev.WdFinalizedAt is not null)
        {
            return Ephemeral("This camp has already been processed.");
        }

        var officerAppUserId = await _db.DiscordActivityUsers
            .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
            .Select(link => link.IdentityUserId!)
            .FirstOrDefaultAsync(cancellationToken);
        if (!await _gates.CanEndCampAsync(officerAppUserId, ev.LinkshellId, cancellationToken))
        {
            return Ephemeral("You need the **Manage events** permission to end the camp.");
        }

        // NQ/HQ only exists for the three merge-pair families, and it's asked even when we didn't
        // claim (which spawn it was still drives the next pop).
        var hasHq = HnmConfig.HasHqVariant(ev.AssignedMonsterName);
        bool? hq = hasHq ? ParseYesNo(ExtractModalValue(data, WdPopHqFieldId), defaultValue: false) : null;
        // The outcome is ONE three-way field again. A modal opened while it was briefly two
        // Yes/No dropdowns still submits those ids, so they win when present — an old modal
        // sitting open on someone's screen must record what they actually picked rather than
        // fall through to the merged field's default.
        var claimRaw = ExtractModalValue(data, WdPopClaimFieldId);
        var killRaw = ExtractModalValue(data, WdPopKillFieldId);
        var (claimed, killed) = claimRaw is null && killRaw is null
            ? ParseCampOutcome(ExtractModalValue(data, WdPopOutcomeFieldId))
            : (ParseYesNo(claimRaw, defaultValue: true), ParseYesNo(killRaw, defaultValue: true));

        // The window it popped on, capping credit. The modal no longer ASKS — the Date box took
        // its row — so this is normally null and the pop service falls back to the board's own
        // counter, which is exactly what the picker's "I don't know" answer already did.
        //
        // Still READ, because a modal opened before the picker was removed can be submitted
        // afterwards, and an officer who did pick a window deserves to have that answer kept
        // rather than quietly replaced by the fallback. "unknown" and absent both mean null,
        // which is the same thing here.
        var effectiveWindows = DiscordEventMessageBuilder.EffectiveWindowCount(ev);
        var popWindowRaw = ExtractModalValue(data, WdPopWindowFieldId)?.Trim();
        int? popWindow = int.TryParse(popWindowRaw, out var pw) && pw >= 1
            ? Math.Clamp(pw, 1, effectiveWindows)
            : (int?)null;

        // Day number is still not asked — it only finds a value when a modal opened before its
        // removal is submitted afterwards. Absent (the normal path) leaves the board's day alone.
        int? dayNumber = int.TryParse(ExtractModalValue(data, WdPopDayFieldId)?.Trim(), out var dn) && dn > 0
            ? dn : (int?)null;
        // The re-post lead: a positive number of hours enables Repeat-on-ToD at that lead
        // (creating this monster's standing board if it has none), "no"/"off"/"0" turns it off
        // for this cycle, and blank leaves whatever is configured untouched.
        //
        // FRACTIONAL, matching the quarter-hour box on the app's own forms. It took whole hours
        // only while nothing could set a fractional lead but the End Camp form itself; the
        // create-event form asks for the lead now, so "1.5" is an ordinary value to arrive here
        // — and rejecting it would have meant an officer's prefilled 1.5 could only be submitted
        // by first changing it.
        //
        // The off-spellings are matched BEFORE the number, so "0" reads as "turn it off" rather
        // than falling through to the reject below as a non-positive number.
        //
        // Anything else is an error rather than a shrug. This field used to swallow whatever it
        // couldn't parse, which on an optional field is the worst of both worlds: the officer
        // typed something, the camp closed reporting success, and the re-post they thought they
        // had set silently never existed. Bailing here costs them a re-open — nothing has been
        // written yet at this point — which is the cheaper of the two mistakes.
        bool? repost = null;
        double? repostLead = null;
        var repostRaw = ExtractModalValue(data, WdPopRepostFieldId)?.Trim();
        if (!string.IsNullOrEmpty(repostRaw))
        {
            if (repostRaw.Equals("no", StringComparison.OrdinalIgnoreCase)
                || repostRaw.Equals("off", StringComparison.OrdinalIgnoreCase)
                || repostRaw == "0")
            {
                repost = false;
            }
            else if (double.TryParse(repostRaw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var lead)
                && double.IsFinite(lead) && lead > 0)
            {
                repost = true;
                // Same ceiling HnmRecurringBoardService clamps to.
                repostLead = Math.Min(lead, 168);
            }
            else
            {
                return Ephemeral(
                    $"**Repost Event** takes a number of hours (like `1`, `1.5` or `4`), or `no` to turn "
                    + $"re-posting off — `{repostRaw}` is neither. Nothing was saved; hit **End Camp** again.");
            }
        }

        // Time of Death: blank/"now" → now; "HH:mm[:ss]" or a full date-time → the officer's local
        // time, converted to UTC. Unparseable → error (don't silently guess a time).
        var officerTimeZone = string.IsNullOrEmpty(officerAppUserId)
            ? null
            : await _db.Users.Where(u => u.Id == officerAppUserId).Select(u => u.TimeZone).FirstOrDefaultAsync(cancellationToken);
        if (!TryParseCampTod(
                _timeZones,
                ExtractModalValue(data, WdPopTodDateFieldId),
                ExtractModalValue(data, WdPopTodFieldId),
                officerTimeZone, out var todUtc, out var todError))
        {
            return Ephemeral(todError!);
        }

        var isWd = DiscordEventMessageBuilder.IsWd(ev);
        var popService = HttpContext.RequestServices.GetRequiredService<HnmCampPopService>();
        HnmCampPopService.Result result;
        try
        {
            result = await popService.PopAsync(new HnmCampPopService.Request(
                EventId: popEventId,
                TodTimeUtc: todUtc,
                Cooldown: null,
                Interval: null,
                DayNumber: dayNumber,
                Claimed: claimed,
                Killed: killed,
                PopWindow: popWindow, // null → the pop service falls back to the board's counter
                Hq: hq ?? false,
                Repost: repost,
                RepostLeadHours: repostLead), cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The camp (or a member's balance) was saved by someone else while this ran, so the
            // event's xmin check turned the save away. Say so in Discord rather than letting the
            // interaction fail with no message (see HnmCampPopService.PopAsync).
            return Ephemeral(ConcurrencyRetry.ConflictMessage);
        }
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't end the camp.");
        }

        // Nothing to say when it worked: the board has already turned into the "defeated" note.
        // The exception is a camp ended with no ToD on a monster that re-posts, because nothing
        // on the board says the re-post isn't coming.
        if (todUtc is null && !isWd)
        {
            return Ephemeral(
                "Camp ended, but no Time of Death was recorded — the tracker shows **Not entered** "
                + "and the board won't auto-re-post. Post the next one by hand.");
        }
        return Ok(new { type = ResponseDeferredUpdate });
    }

    // Loose yes/no parse for modal text inputs (blank falls back to the default).
    private static bool ParseYesNo(string? text, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(text)) return defaultValue;
        return text.Trim().ToLowerInvariant() switch
        {
            "y" or "yes" or "true" or "1" => true,
            "n" or "no" or "false" or "0" => false,
            _ => defaultValue,
        };
    }

    // The End Camp modal's single "Outcome" field → (claimed, killed). "killed" = we had it and it
    // died; "claimed" = we had it but it got away or wiped us; "missed" = somebody else claimed it.
    // Blank or unrecognized stays on the button's happy path (killed), the same default the old
    // separate kill field used.
    private static (bool Claimed, bool Killed) ParseCampOutcome(string? text)
    {
        var s = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(s)) return (true, true);
        return s switch
        {
            "claimed" or "claim" or "c" or "no kill" or "nokill" or "wipe" or "wiped" => (true, false),
            "missed" or "miss" or "m" or "no claim" or "noclaim" or "none" or "no" or "n" => (false, false),
            _ => (true, true),
        };
    }

    // "🏁 End Camp / Enter ToD" (every windowed HNM board) → officers only. Opens the ToD modal.
    private async Task<IActionResult> HandleWdPopButtonAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
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
        if (!DiscordEventMessageBuilder.UsesWindows(ev))
        {
            return Ephemeral("This board doesn't use windows.");
        }
        if (!await _gates.CanEndCampAsync(appUserId, ev.LinkshellId, cancellationToken))
        {
            return Ephemeral("You need the **Manage events** permission to end the camp.");
        }
        if (ev.WdFinalizedAt is not null)
        {
            return Ephemeral("This camp has already been processed.");
        }

        // What this monster's Repeat-on-ToD is set to right now, so the re-post field can show it as
        // its placeholder. Only an ENABLED board has a lead in effect; a disabled one reports null
        // so the field doesn't advertise a re-post that isn't going to happen.
        var monsterName = ev.AssignedMonsterName?.Trim();
        var standingBoard = string.IsNullOrWhiteSpace(monsterName)
            ? null
            : await HnmRecurringBoardService.FindAsync(_db, ev.LinkshellId, monsterName, cancellationToken);
        var currentLead = standingBoard is { Enabled: true, LeadHours: > 0 } ? standingBoard.LeadHours : (double?)null;

        // How this monster's next repop is worked out, for the note on the Time of Death box. The
        // same setup HnmCampPopService reads when the modal comes back.
        var timing = await HttpContext.RequestServices.GetRequiredService<MonsterTimingResolver>()
            .ResolveAsync(ev.LinkshellId, monsterName, cancellationToken);
        var repopFormula = TodRepopNote.Formula(
            TodDurationFormat.Format(timing.CooldownMinutes), timing.AdditionalSeconds);

        return WdPopModal(ev, currentLead, repopFormula);
    }

    // The End Camp / Enter ToD modal, and Discord's cap of 5 components is the whole design
    // constraint. Rows, in order:
    //
    //   1. Time of Death   — text, blank = not entered, accepts seconds
    //   2. Was it HQ?      — the three NQ/HQ families only
    //   3. Outcome         — killed / claimed-no-kill / somebody else claimed it
    //   4. Pop window      — multi-window camps only; REQUIRED, and starts empty
    //   5. Re-post lead    — text, blank = leave the standing Repeat-on-ToD config alone
    //
    // Five on an HQ family, four elsewhere. Rows 2 and 4 are the two that can drop out, and they
    // never both apply to a camp small enough to matter — every HQ family is a 7-window king/dragon,
    // so an HQ camp always shows all five. That is why Claimed/Killed had to fold back into one
    // Outcome dropdown to make room; see the field-id block for why nothing is lost by it.
    //
    // Day # stays absent: the event form owns it, and unlike everything above it isn't something
    // the officer OBSERVED at the camp — it's a value they'd be copying back in.
    private IActionResult WdPopModal(Event ev, double? currentLeadHours, string? repopFormula) =>
        Ok(new
        {
            type = ResponseModal,
            data = new
            {
                custom_id = $"{WdPopModalPrefix}{ev.Id}",
                title = "End Camp / Enter ToD",
                components = BuildWdPopModalFields(ev, currentLeadHours, repopFormula),
            }
        });

    // The modal's rows, split out from the response envelope so the 5-row cap above — the one
    // constraint the whole layout is built around — can be asserted in tests instead of discovered
    // as a Discord 400 the first time an officer ends an HQ camp.
    //
    // `currentLeadHours` is this monster's standing Repeat-on-ToD lead when that board exists AND is
    // enabled, else null. Passed in rather than looked up here so this stays a pure function of the
    // camp; the caller owns the query.
    //
    // `repopFormula` is TodRepopNote.Formula for this monster, null when it has no Additional Seconds.
    public static object[] BuildWdPopModalFields(Event ev, double? currentLeadHours, string? repopFormula = null)
    {
        // `value` PREFILLS the box with real, editable text; `placeholder` is the grey hint that
        // shows only while it's empty. A field carrying a prefill sends no placeholder at all —
        // the two would never be visible at the same moment anyway, and a prefilled box's hint is
        // the value itself.
        //
        // `description` is a line of text under the label, and is what moves the box into a Label
        // (type 18), the only component that can carry one. It still counts as one row, so the
        // 5-row cap is untouched. A text input inside a Label must not have a `label` of its own.
        static object TextRow(string fieldId, string label, string placeholder, bool required,
            int maxLength, string? value = null, string? description = null)
        {
            var input = new Dictionary<string, object?>
            {
                ["type"] = 4, // text input
                ["custom_id"] = fieldId,
                ["style"] = 1, // short
                ["min_length"] = required ? 1 : 0,
                ["max_length"] = maxLength,
                ["required"] = required,
            };
            if (value is { Length: > 0 })
            {
                input["value"] = value;
            }
            else
            {
                input["placeholder"] = placeholder;
            }
            if (description is { Length: > 0 })
            {
                return new Dictionary<string, object?>
                {
                    ["type"] = 18, // label
                    ["label"] = label,
                    ["description"] = description,
                    ["component"] = input,
                };
            }
            input["label"] = label;
            return new { type = 1, components = new object[] { input } };
        }

        // A modal dropdown is a string select (type 3) wrapped in a Label (type 18) — a bare select
        // in an action row is rejected. Label's own `label` caps around 45 chars, so the longer hint
        // goes in `description`. Nothing is pre-selected: the box opens on a "Select…" placeholder and
        // is REQUIRED, so Discord won't submit until the officer actually picks. It has to be required,
        // because a blank outcome parses as "claimed and killed" (ParseCampOutcome) and pays bonuses.
        static object SelectRow(string fieldId, string label, string description,
            params (string Value, string Label)[] options)
        {
            var select = new Dictionary<string, object?>
            {
                ["type"] = 3, // string select
                ["custom_id"] = fieldId,
                ["placeholder"] = "Select…",
                ["required"] = true,
                ["min_values"] = 1,
                ["max_values"] = 1,
                ["options"] = options
                    .Select(option => new Dictionary<string, object?>
                    {
                        ["label"] = option.Label,
                        ["value"] = option.Value,
                    })
                    .ToArray(),
            };
            return new Dictionary<string, object?>
            {
                ["type"] = 18, // label
                ["label"] = label,
                ["description"] = description,
                ["component"] = select,
            };
        }

        // (WindowSelectRow lived here: the pop-window picker's builder — a required select with no
        // default, so the officer had to answer rather than let the board's clock-driven counter be
        // recorded by a submit-without-looking. Nothing renders a required select on this modal any
        // more; SelectRow above covers the optional ones that remain.)

        var fields = new List<object>
        {
            // DATE and TIME are two boxes, and the Date one is why the pop-window picker is gone:
            // Discord caps a modal at 5 rows, an HQ family fills all five, and every HQ family is a
            // 7-window king/dragon — so the camps that most need a date field were exactly the ones
            // with no room. The window is recovered from the ToD tracker's own capture instead.
            //
            // A date has to be VISIBLE, not merely accepted, because the case it exists for is the
            // one where nobody is reading carefully: the mob died overnight and is logged the next
            // morning, or on Monday. A bare time still rolls back a day on its own (see
            // TryParseCampTod), but that only ever reaches yesterday, and an officer who does not
            // know a date can be typed will round to a time that is simply wrong.
            //
            // Blank Date = today, which is the ordinary path and stays a one-box form in practice.
            TextRow(WdPopTodDateFieldId, "Date (blank = today)",
                "Ex: 9/5  ·  9/5/2026  ·  yesterday", false, 12),
            // "now" and blank parse too, and are deliberately NOT in the placeholder: blank is
            // spelled out in the label already, and a placeholder listing every accepted spelling
            // reads as a syntax reference rather than as an example of what to type.
            //
            // The note sits on the box it is about: the time typed here is not the time the reposted
            // board will show when the monster has Additional Seconds.
            TextRow(WdPopTodFieldId, "Time of Death (blank = not entered)",
                "Ex: 9:05:15 PM, or 21:05:15", false, 16,
                description: repopFormula is null ? null : $"Reposted event starts at {repopFormula}."),
        };
        if (HnmConfig.HasHqVariant(ev.AssignedMonsterName))
        {
            fields.Add(SelectRow(WdPopHqFieldId, "NQ or HQ?",
                "Which spawn it was — this drives the next pop even if we didn't claim.",
                ("no", "NQ"), ("yes", "HQ")));
        }
        // One three-way field rather than two Yes/No ones — the row it saves is what pays for the
        // pop window below. Each option answers BOTH halves of the label in one line, which is the
        // reason the merge doesn't cost the officer anything: the fourth combination ("no claim,
        // but we killed it") isn't a state that exists, so nothing here is unreachable.
        // Values are the ones ParseCampOutcome already reads.
        fields.Add(SelectRow(WdPopOutcomeFieldId, "Claim? Kill?",
            "Claim and kill decide the camp's bonuses.",
            ("killed", "Yes, we claimed, and killed"),
            ("claimed", "Yes, we claimed, but did not kill"),
            ("missed", "No, we did not claim")));

        // (The "Which window did it pop on?" picker lived here. It was the fifth row, and the Date
        // box above needed one — Discord caps a modal at 5 rows and an HQ family fills every one of
        // them, so a date and a window picker could not both exist on a king or a dragon, which are
        // the camps that have windows in the first place.
        //
        // The window is not lost, it just stops being typed: the in-game Repop Timer captures the pop
        // window at the moment of the pop and posts it on the ToD, which is a better answer than an
        // officer reconstructing it from memory an hour later. The pop service keeps that reading,
        // and falls back to the board's own counter only when there is none -- see HnmCampPopService.)

        // The auto-re-post lead. Free text rather than a dropdown because the useful values run
        // from "half an hour" to "two days" depending on the monster, and because BLANK has to mean
        // "leave the standing config alone" — a dropdown has no natural way to say that without
        // spending an option on it. Parsed by the same reader that has always handled this field.
        //
        // A monster that already has a lead gets it PREFILLED as a real, editable value rather than
        // hinted at — the officer sees the number itself, edits it or leaves it, and either way what
        // they're looking at is what gets saved. A monster whose standing board is off (or absent)
        // has no lead in effect and shows the example instead: prefilling its stored number there
        // would arm a re-post the officer never asked for, just by their submitting the form.
        //
        // Printed at full precision, because the field takes fractional hours now. It used to round
        // to a whole number to match a whole-number parser, which was safe only while nothing but
        // this modal could set a lead — the app's ToD form takes quarter-hours, so rounding here
        // would quietly rewrite an officer's 1.5 to 2 just by their submitting this modal.
        fields.Add(TextRow(WdPopRepostFieldId, "Repost Event (Hours Before Repop)",
            "Example: 1 = 1 hr before, or no = dont re-post",
            false, 10,
            currentLeadHours is { } saved && saved > 0
                ? saved.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : null));

        return fields.ToArray();
    }

    // Parses the End Camp modal's Time of Death from its TWO boxes — Date and Time — in the
    // officer's local zone.
    //
    // Two fields rather than one because the case this exists for is a mob that died overnight and
    // gets logged the next morning, or a Friday kill entered on Monday. A single box could carry a
    // date and did, but nothing about it said so: an officer who does not know a date is accepted
    // types a bare time, and a bare time silently means today.
    //
    // Date accepts: blank (today), "today", "yesterday", "9/5", "9/5/2026", "2026-09-05".
    // Time accepts: blank (see below), "21:05", "21:05:15", "9:05 PM", "9:05:15 PM", "now".
    //
    //   both blank → todUtc = null, meaning NOT ENTERED: the camp ended without anyone seeing it
    //                die, so no time and no repop get recorded.
    //   date only  → an error. A date is not a Time of Death, and choosing a time on the officer's
    //                behalf would invent the number the whole repop clock is counted from.
    //   time only  → today in the officer's zone. When that is still AHEAD of their LSM clock:
    //                  - by more than LastNightThreshold (3 hours): last night, so rolled back a
    //                    day. That is the 11:50pm kill logged at 12:20am;
    //                  - by up to 3 hours: REFUSED, showing both clocks. A kill typed in soon after
    //                    it happened only looks an hour or two "in the future" when the officer's
    //                    Profile zone is behind the clock they read the time from, and rolling that
    //                    back used to save it 23 hours early (logic-09).
    //   any typed time needs a time zone on the officer's LSM Profile. With none it used to be
    //                read as UTC, hours off for everyone outside it. `now` needs none.
    //
    // Returns false with a user-facing message on unparseable input — a ToD is never silently
    // guessed.
    //
    // Static, and internal so the accepted spellings can be pinned directly in tests. The zone
    // service and `nowUtc` are passed in rather than read off the handler and the system clock
    // for the same reason: every branch here turns on the officer's LOCAL clock, so a test that
    // cannot choose the zone and the moment cannot actually test the rolling-back rules.
    internal static bool TryParseCampTod(
        TimeZoneConversionService timeZones,
        string? rawDate, string? rawTime, string? timeZoneId,
        out DateTime? todUtc, out string? error, DateTime? nowUtc = null)
    {
        todUtc = null;
        error = null;
        var datePart = rawDate?.Trim() ?? string.Empty;
        var timePart = rawTime?.Trim() ?? string.Empty;

        if (datePart.Length == 0 && timePart.Length == 0)
        {
            return true; // null → "Not entered"; the pop service leaves Time + RepopTime unset
        }

        var utcNow = nowUtc ?? DateTime.UtcNow;
        var localNow = timeZones.ToUserTime(utcNow, timeZoneId) ?? utcNow;

        // "now" carries its own date, so a Date beside it is a contradiction rather than extra
        // information — better to say so than to silently pick one of the two.
        if (timePart.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            if (datePart.Length > 0)
            {
                error = "Leave **Date** blank when the time is `now` — `now` already carries its own date.";
                return false;
            }
            todUtc = utcNow;
            return true;
        }

        // A date with no time. Blank/blank above is the deliberate "nobody saw it die"; this is a
        // half-filled form, and guessing midnight would look like a real answer forever after.
        if (timePart.Length == 0)
        {
            error = "Enter a **Time of Death** to go with the date — the repop clock is counted from it. "
                  + "Leave BOTH boxes blank if nobody saw it die.";
            return false;
        }

        // NormalizeMeridiem folds any AM/PM spelling down to the " AM"/" PM" the "tt" specifier
        // wants, so the officer can type it however they like; a 24-hour entry passes through.
        if (!TimeOnly.TryParseExact(
                NormalizeMeridiem(timePart),
                new[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm tt", "h:mm:ss tt" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var timeOnly))
        {
            error = "Enter a valid **Time of Death** — `9:05 PM`, `9:05:15 PM`, `21:05`, or `now`.";
            return false;
        }

        // A typed time is read in the Profile's zone, so without one there is nothing to read it in.
        if (!timeZones.IsKnownZone(timeZoneId))
        {
            error = "Your LSM Profile has no time zone, so a typed **Time of Death** can't be read. "
                  + "Set your time zone on your LSM Profile, then hit **End Camp** again "
                  + "(or type `now` if it just died). Nothing was saved.";
            return false;
        }

        if (!TryResolveCampTodDate(datePart, localNow, out var localDate, out var dateError))
        {
            error = dateError;
            return false;
        }

        var localDt = localDate.ToDateTime(timeOnly, DateTimeKind.Unspecified);
        if (timeZones.ToUtc(localDt, timeZoneId) is not { } candidateUtc)
        {
            return false;
        }

        // Only when NO date was typed. An explicit date is an instruction; second-guessing it would
        // move an officer's deliberate answer by a day. Compared in UTC rather than on the wall
        // clock, so a daylight-saving change between the two moments can't make "how far ahead" an
        // hour wrong.
        if (datePart.Length == 0 && candidateUtc > utcNow)
        {
            if (candidateUtc - utcNow <= LastNightThreshold)
            {
                error = CampTodNotYetMessage(timeZoneId, localDt, localNow);
                return false;
            }
            localDt = localDt.AddDays(-1); // well ahead of "now" today: last night
            if (timeZones.ToUtc(localDt, timeZoneId) is not { } lastNightUtc)
            {
                return false;
            }
            candidateUtc = lastNightUtc;
        }

        todUtc = candidateUtc;
        return true;
    }

    // How far ahead of the LSM clock a time with no date must be before it reads as last night.
    // Up to this it is refused instead: being an hour or two "in the future" is what a Profile zone
    // behind the officer's own clock looks like.
    private static readonly TimeSpan LastNightThreshold = TimeSpan.FromHours(3);

    // Why a ToD was refused for being ahead of the LSM clock, with both clocks spelled out so the
    // officer can see which one is wrong.
    private static string CampTodNotYetMessage(string? timeZoneId, DateTime typedLocal, DateTime localNow) =>
        $"**{FormatLsmClock(typedLocal, withSeconds: typedLocal.Second != 0)}** hasn't happened yet by your "
        + $"LSM clock: it's **{FormatLsmClock(localNow)}** ({timeZoneId}). Nothing was saved.\n"
        + "• If your clock shows a different time, fix the time zone on your LSM Profile, then hit "
        + "**End Camp** again.\n"
        + "• If it died yesterday, type `yesterday` in **Date**.";

    // "Nov 8, 8:10 AM", or with seconds "Nov 8, 9:07:16 AM".
    private static string FormatLsmClock(DateTime local, bool withSeconds = false) =>
        local.ToString(withSeconds ? "MMM d, h:mm:ss tt" : "MMM d, h:mm tt",
            System.Globalization.CultureInfo.InvariantCulture);

    // The Date box → a local calendar day. Blank is today.
    //
    // A year-less "9/5" resolves to its MOST RECENT occurrence at or before today, which is what
    // carries it across New Year: "12/31" entered on January 1st is a few days ago, not eleven
    // months away. An explicit year is taken exactly as typed, because the officer said it.
    private static bool TryResolveCampTodDate(
        string datePart, DateTime localNow, out DateOnly localDate, out string? error)
    {
        var today = DateOnly.FromDateTime(localNow);
        localDate = today;
        error = null;

        if (datePart.Length == 0 || datePart.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (datePart.Equals("yesterday", StringComparison.OrdinalIgnoreCase)
            || datePart.Equals("yday", StringComparison.OrdinalIgnoreCase))
        {
            localDate = today.AddDays(-1);
            return true;
        }

        // Explicit year FIRST: "9/5/2026" would otherwise never be reached, and the year the
        // officer typed would be silently replaced by the most-recent-occurrence rule below.
        if (DateOnly.TryParseExact(
                datePart,
                new[] { "M/d/yyyy", "M-d-yyyy", "yyyy-MM-dd", "yyyy/M/d" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var exact))
        {
            localDate = exact;
            return true;
        }

        if (DateOnly.TryParseExact(
                datePart,
                new[] { "M/d", "M-d" },
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var yearless))
        {
            // TryParseExact defaults a missing year to the CURRENT one, so this only steps back
            // when that lands ahead of the officer's own day.
            var resolved = yearless.AddYears(today.Year - yearless.Year);
            if (resolved > today)
            {
                resolved = resolved.AddYears(-1);
            }
            localDate = resolved;
            return true;
        }

        error = "Enter a valid **Date** — blank for today, `yesterday`, `9/5`, `9/5/2026`, or `2026-09-05`.";
        return false;
    }

    // Rewrites a trailing AM/PM marker — "pm", " PM", "p.m.", bare "p" — as the " AM"/" PM" that
    // the "tt" format specifier matches, leaving everything before it (clock time or full date-time)
    // alone. Input with no such marker comes back unchanged, so 24-hour entries are unaffected.
    private static readonly System.Text.RegularExpressions.Regex MeridiemSuffix = new(
        @"^(?<head>.*\d)\s*(?<half>[ap])\.?\s*m?\.?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string NormalizeMeridiem(string text)
    {
        var match = MeridiemSuffix.Match(text);
        return match.Success
            ? $"{match.Groups["head"].Value.TrimEnd()} {char.ToUpperInvariant(match.Groups["half"].Value[0])}M"
            : text;
    }
}
