using System.Net;
using System.Text;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;

namespace LinkshellManagerDiscordApp.Services;

// Builds the standalone HTML for an event's party board — the "Esports HUD" card
// (header + stat tiles + role legend + parties laid out as 3-per-row colored
// columns). EventBoardImageRenderer screenshots this to a PNG that becomes the
// Discord post's image. This is the ONLY place a board can have colour + bold +
// side-by-side columns at a chosen width — a Discord message can't.
//
// Pure string building (no DB) so it's cheap and easy to unit-render. The caller
// passes the loaded setup tree + per-event slot signups.
public static class EventBoardHtmlBuilder
{
    public static string Build(
        Event ev,
        PartySetup setup,
        IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        IReadOnlyList<EventSignupLine> generalSignups,
        string? theme = null,
        bool wide = false)
    {
        // Components V2 boards (wide) use a wider internal canvas so each party column is
        // wider and more of the board reads at once. Kept at/below 2048px so the renderer
        // can screenshot it at 2× density and still stay under Discord's 4096px/side cap.
        var cardWidth = wide ? EventBoardImageRenderer.WideCardWidth : EventBoardImageRenderer.DefaultCardWidth;

        var parties = LabeledParties(setup).ToList();
        var totalSlots = parties.Sum(p => p.Party.Slots.Count);
        var filledSlots = parties.Sum(p => p.Party.Slots.Count(s => slotSignups.ContainsKey(s.Id)));
        // How many signups are "locked" to survive the next window advance (window-cycle
        // HNMs only) — surfaced as a stat tile so officers see the coming carryover at a glance.
        var stayingCount = HnmConfig.SupportsWindowAdvance(ev.AssignedMonsterName)
            ? slotSignups.Values.Count(s => s.StayNextWindow)
            : 0;

        var sb = new StringBuilder();
        // The :root block is split: the theme-independent half (role colours,
        // fonts, the neutral --any) is fixed here; the per-theme palette half is
        // injected from EventBoardThemes so a linkshell's chosen theme swaps the
        // colours without touching any structural rule below.
        sb.Append("""
<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"/>
<link rel="preconnect" href="https://fonts.googleapis.com"/>
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin/>
<link href="https://fonts.googleapis.com/css2?family=Cinzel:wght@600;700;800&family=Spectral:ital,wght@0,400;0,600;1,400&display=swap" rel="stylesheet"/>
<style>
  :root{
    --tank:#4a9eff;--heal:#3fcf6b;--supp:#f5c451;--dps:#f0556b;
    --cinzel:"Cinzel",serif;--spectral:"Spectral",Georgia,serif;
    --any:#5f8088;
""");
        sb.Append(EventBoardThemes.PaletteFor(theme));
        sb.Append("""
  }
  *{box-sizing:border-box;}
  body{margin:0;padding:0;background:var(--page-bg);font-family:var(--spectral);}
    .embed{width:1600px;position:relative;color:var(--txt);overflow:hidden;background:var(--bg-grad);}
  .toprule{height:3px;background:linear-gradient(90deg,transparent,var(--accent),transparent);}
    .pad{padding:34px 34px 36px;display:flex;flex-direction:column;gap:28px;}
    .head{display:flex;align-items:flex-end;gap:22px;justify-content:center;position:relative;}
    .head-main{text-align:center;}
    .swords{font-size:40px;filter:drop-shadow(0 0 8px var(--glow));}
    .eyebrow{font-family:var(--cinzel);font-size:14px;font-weight:700;letter-spacing:7px;color:var(--eyebrow);}
    .title{font-family:var(--cinzel);font-size:48px;font-weight:800;line-height:1;color:var(--txt);text-shadow:var(--title-shadow);}
  .roster{position:absolute;right:0;bottom:0;text-align:right;}
    .roster .lab{font-family:var(--cinzel);font-size:14px;letter-spacing:3px;color:var(--dim);}
    .roster .num{font-family:var(--cinzel);font-size:34px;font-weight:700;color:var(--accent);white-space:nowrap;}
  .roster .num small{color:var(--dim);}
  .tiles{display:flex;gap:2px;border-top:1px solid var(--line);border-bottom:1px solid var(--line);}
    .tile{flex:1;padding:16px 20px;background:var(--tint);border-left:2px solid var(--accent);}
  .tile:first-child{border-left:none;}
    .tile .lab{font-family:var(--cinzel);font-size:13px;letter-spacing:2px;color:var(--dim);text-transform:uppercase;}
    .tile .val{font-size:22px;font-weight:600;color:var(--soft);margin-top:6px;display:flex;align-items:center;gap:9px;}
  .tile .val .rel{font-style:italic;color:var(--dim);font-weight:400;}
    .legend{display:flex;flex-wrap:wrap;gap:10px 22px;align-items:center;padding:12px 16px;background:var(--tint);border-left:2px solid var(--accent);border-radius:2px;}
    .legend .item{display:inline-flex;align-items:center;gap:10px;}
    .legend .name{font-family:var(--cinzel);font-size:14px;letter-spacing:1px;color:var(--muted);}
    .legend .key-lab{font-family:var(--cinzel);font-size:13px;font-weight:700;letter-spacing:3px;text-transform:uppercase;color:var(--dim);margin-right:4px;}
    .legend .crown{color:var(--accent);font-size:18px;line-height:1;}
  /* gem marker */
    .gem{position:relative;display:inline-block;flex-shrink:0;transform:rotate(45deg);width:20px;height:20px;}
    .gem.sm{width:14px;height:14px;}
  .gem .face{position:absolute;inset:0;border-radius:3px;}
  .gem.empty .face{background:transparent!important;border-width:1.5px;border-style:dashed;opacity:.6;box-shadow:none!important;}
  .gem .spark{position:absolute;left:18%;top:18%;width:32%;height:32%;background:rgba(255,255,255,.7);border-radius:1px;}
  .gem.empty .spark{display:none;}
  .g-tank .face{background:linear-gradient(135deg,var(--tank),#4a9eff99);border:1px solid var(--tank);box-shadow:0 0 9px rgba(74,158,255,.55),inset 1px 1px 2px rgba(255,255,255,.4);}
  .g-heal .face{background:linear-gradient(135deg,var(--heal),#3fcf6b99);border:1px solid var(--heal);box-shadow:0 0 9px rgba(63,207,107,.55),inset 1px 1px 2px rgba(255,255,255,.4);}
  .g-supp .face{background:linear-gradient(135deg,var(--supp),#f5c45199);border:1px solid var(--supp);box-shadow:0 0 9px rgba(245,196,81,.55),inset 1px 1px 2px rgba(255,255,255,.4);}
  .g-dps  .face{background:linear-gradient(135deg,var(--dps),#f0556b99);border:1px solid var(--dps);box-shadow:0 0 9px rgba(240,85,107,.55),inset 1px 1px 2px rgba(255,255,255,.4);}
  .g-any  .face{background:linear-gradient(135deg,var(--any),#5f808899);border:1px solid var(--any);box-shadow:0 0 9px rgba(95,128,136,.4),inset 1px 1px 2px rgba(255,255,255,.3);}
  .b-tank{border-color:var(--tank);} .b-heal{border-color:var(--heal);} .b-supp{border-color:var(--supp);} .b-dps{border-color:var(--dps);} .b-any{border-color:var(--any);}
    .parties{display:flex;gap:22px;}
    .party{flex:1;min-width:0;display:flex;flex-direction:column;gap:8px;}
    .ptitle{display:flex;align-items:center;gap:12px;margin-bottom:4px;}
    .ptitle .nm{font-family:var(--cinzel);font-size:20px;font-weight:700;letter-spacing:2px;color:var(--accent);text-transform:uppercase;white-space:nowrap;}
    .ptitle .nm small{font-size:14px;letter-spacing:1px;color:var(--dim);font-weight:600;}
  .ptitle .rule{flex:1;height:1px;background:linear-gradient(90deg,var(--accent),transparent);opacity:.5;}
    .ptitle .cnt{font-family:var(--cinzel);font-size:16px;letter-spacing:1px;color:var(--dim);}
  .ptitle .cnt.full{color:var(--heal);}
    .pbar{height:6px;border-radius:3px;background:var(--line);overflow:hidden;margin-bottom:4px;}
  .pbar>i{display:block;height:100%;border-radius:2px;background:linear-gradient(90deg,var(--accent-deep),var(--accent-bright));box-shadow:0 0 8px var(--glow);}
  .pbar.full>i{background:linear-gradient(90deg,#3fcf6b,#8fe0a6);box-shadow:0 0 8px rgba(63,207,107,.45);}
    .slot{display:flex;align-items:center;gap:12px;padding:10px 2px;border-bottom:1px solid var(--slot-line);}
    .slot .combo{font-family:var(--cinzel);font-size:17px;font-weight:600;letter-spacing:.5px;color:var(--soft);white-space:nowrap;flex-shrink:0;}
  .slot.empty .combo{color:var(--vacant);}
    .slot .who{font-size:18px;font-style:italic;color:var(--name);display:flex;align-items:center;gap:6px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
  .slot .crown{color:var(--accent);font-style:normal;}
  .slot .lock{font-style:normal;font-size:15px;line-height:1;}
    .slot .vacant{font-family:var(--cinzel);font-size:14px;letter-spacing:2px;text-transform:uppercase;opacity:.8;}
  .v-tank{color:var(--tank);} .v-heal{color:var(--heal);} .v-supp{color:var(--supp);} .v-dps{color:var(--dps);} .v-any{color:var(--muted);}
    .foot{padding-top:22px;border-top:1px solid var(--line);display:flex;flex-direction:column;gap:12px;align-items:center;}
    .help{font-size:16px;font-style:italic;color:var(--dim);text-align:center;}
    /* Alliance grouping header above each alliance's row of parties. */
    .alliance-head{font-family:var(--cinzel);font-size:20px;font-weight:700;letter-spacing:3px;text-transform:uppercase;color:var(--accent);padding-bottom:6px;border-bottom:1px solid var(--line);text-align:center;}
    /* The alliance lead's name, shown to the right of the alliance name. */
    .alliance-lead{margin-left:12px;font-size:16px;font-weight:600;letter-spacing:1px;color:var(--soft);}
    .alliance-lead .crown{color:var(--accent);margin-right:4px;}
    /* Each alliance = header + its party row, with a SMALL internal gap so the
       title hugs its parties; the larger `.pad` gap between groups keeps the
       separation ABOVE each alliance title (dividing it from the alliance before). */
    .alliance-group{display:flex;flex-direction:column;gap:10px;}
    /* "Also attending — no slot": prominent (not muted) list with role + jobs. */
    .extra{padding-top:20px;border-top:1px solid var(--line);display:flex;flex-direction:column;gap:12px;}
    .extra-title{font-family:var(--cinzel);font-size:15px;font-weight:700;letter-spacing:2px;text-transform:uppercase;color:var(--accent);}
    .extra-list{display:flex;flex-wrap:wrap;gap:12px 26px;}
    .extra-member{display:inline-flex;align-items:center;gap:10px;}
    .extra-name{font-size:18px;font-weight:600;color:var(--soft);}
    .extra-jobs{font-family:var(--cinzel);font-size:14px;letter-spacing:.5px;color:var(--name);}
""");
        // Card width override: a later .embed rule (equal specificity, later in source order)
        // wins over the base width:1600px above, so the wide (V2) canvas needs no change to
        // the big stylesheet literal.
        sb.Append($".embed{{width:{cardWidth}px;}}</style></head><body><div class=\"embed\"><div class=\"toprule\"></div><div class=\"pad\">");

        // Header
        var eyebrow = string.IsNullOrWhiteSpace(ev.EventType)
            ? "EVENT"
            : Enc(ev.EventType!.Trim().ToUpperInvariant());
        sb.Append($"""
<div class="head"><span class="swords">&#9876;&#65039;</span><div class="head-main">
<div class="eyebrow">{eyebrow}</div><div class="title">{Enc((ev.EventName ?? $"Event #{ev.Id}").Trim())}</div></div>
<div class="roster"><div class="lab">ROSTER</div>
<div class="num">{filledSlots}<small> / {totalSlots}</small></div></div></div>
""");

        // Stat tiles. The start time is deliberately NOT shown here — a baked image
        // can't localize per viewer, so the time lives in the embed's "Started/Starts"
        // field (DiscordEventMessageBuilder) as a Discord timestamp that renders in
        // each user's timezone.
        var status = ev.CommencementStartTime is not null ? "Live" : "Recruiting";
        var reward = ev.DkpPerHour is { } dkp ? $"{Enc(dkp.ToString())} DKP/hr" : "&mdash;";
        var location = string.IsNullOrWhiteSpace(ev.EventLocation) ? "&mdash;" : Enc(ev.EventLocation!.Trim());
        // Optional "Day N" tile for HNM boards (set on the create-event form).
        var dayTile = ev.DayNumber is { } dayNum
            ? $"""<div class="tile"><div class="lab">Day</div><div class="val">{Enc(dayNum.ToString())}</div></div>"""
            : string.Empty;
        // Optional "Staying" tile — how many locked signups carry into the next window.
        var stayingTile = stayingCount > 0
            ? $"""<div class="tile"><div class="lab">Staying</div><div class="val">&#128274; {stayingCount}</div></div>"""
            : string.Empty;
        sb.Append($"""
<div class="tiles">
<div class="tile"><div class="lab">Status</div><div class="val">{status}</div></div>{dayTile}
<div class="tile"><div class="lab">Reward</div><div class="val">{reward}</div></div>
<div class="tile"><div class="lab">Location</div><div class="val">{location}</div></div>{stayingTile}</div>
""");

        // Color key — explains what each gem colour / marker means so the board
        // reads at a glance. Gem colour = the slot's ROLE; the dashed gem is an
        // open slot; the crown marks the party leader.
        sb.Append("""
<div class="legend">
<span class="key-lab">Color Key</span>
<span class="item"><span class="gem sm g-tank"><span class="face"></span><span class="spark"></span></span><span class="name">Tank</span></span>
<span class="item"><span class="gem sm g-heal"><span class="face"></span><span class="spark"></span></span><span class="name">Healer</span></span>
<span class="item"><span class="gem sm g-supp"><span class="face"></span><span class="spark"></span></span><span class="name">Support</span></span>
<span class="item"><span class="gem sm g-dps"><span class="face"></span><span class="spark"></span></span><span class="name">DPS</span></span>
<span class="item"><span class="gem sm g-any"><span class="face"></span><span class="spark"></span></span><span class="name">Any</span></span>
<span class="item"><span class="gem sm empty b-any"><span class="face"></span></span><span class="name">Open slot</span></span>
<span class="item"><span class="crown">&#9819;</span><span class="name">Party leader</span></span>
""");
        // Window-cycle HNMs explain the 🔒 (a signup staying through the next window advance).
        if (HnmConfig.SupportsWindowAdvance(ev.AssignedMonsterName))
        {
            sb.Append("""<span class="item"><span class="lock">&#128274;</span><span class="name">Staying next window</span></span>""");
        }
        sb.Append("</div>");

        // Parties — grouped by alliance, 3 per row (matching the embed). Each
        // alliance gets a full-width header (when there's more than one) so the
        // grouping reads as one title instead of an "A1 ·" prefix on every party.
        var allianceGroups = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var multiAlliance = allianceGroups.Count > 1;
        for (var ai = 0; ai < allianceGroups.Count; ai++)
        {
            var allianceParties = allianceGroups[ai].Parties.OrderBy(p => p.SortOrder).ToList();
            if (allianceParties.Count == 0) { continue; }

            sb.Append("<div class=\"alliance-group\">");
            if (multiAlliance)
            {
                var allianceName = string.IsNullOrWhiteSpace(allianceGroups[ai].Name)
                    ? $"Alliance {ai + 1}"
                    : allianceGroups[ai].Name!;
                // The alliance lead (if claimed) rides to the right of the alliance name.
                var lead = AllianceLeadName(allianceGroups[ai], slotSignups);
                var leadHtml = string.IsNullOrEmpty(lead)
                    ? string.Empty
                    : $"""<span class="alliance-lead"><span class="crown">&#9819;</span>Alliance Lead: {Enc(lead)}</span>""";
                sb.Append($"""<div class="alliance-head">{Enc(allianceName)}{leadHtml}</div>""");
            }

            sb.Append("<div class=\"parties\">");
            for (var pi = 0; pi < allianceParties.Count; pi++)
            {
                var party = allianceParties[pi];
                var name = string.IsNullOrWhiteSpace(party.Name) ? $"Party {pi + 1}" : party.Name!;
                // Alliance is conveyed by the header above, so no per-party suffix.
                AppendParty(sb, party, name, null, slotSignups);
            }
            // Pad short rows to 3 columns so parties keep a consistent width.
            for (var i = allianceParties.Count; i < 3 && parties.Count > 1; i++)
            {
                sb.Append("<div class=\"party\"></div>");
            }
            sb.Append("</div>");  // .parties
            sb.Append("</div>");  // .alliance-group
        }

        // "Also attending — no slot" overflow (general AppUserEvent roster).
        var slotNames = new HashSet<string>(
            slotSignups.Values.Where(s => !string.IsNullOrWhiteSpace(s.CharacterName)).Select(s => s.CharacterName!.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var extra = generalSignups
            .Where(g => !string.IsNullOrWhiteSpace(g.CharacterName) && !slotNames.Contains(g.CharacterName.Trim()))
            .ToList();
        if (extra.Count > 0)
        {
            sb.Append("""<div class="extra"><div class="extra-title">Also Attending</div><div class="extra-list">""");
            foreach (var g in extra)
            {
                var roleClass = RoleClass(g.JobType);
                var combo = GeneralCombo(g);
                sb.Append($"""<div class="extra-member"><span class="gem sm g-{roleClass}"><span class="face"></span><span class="spark"></span></span><span class="extra-name">{Enc(g.CharacterName)}</span>""");
                if (!string.IsNullOrEmpty(combo))
                {
                    sb.Append($"""<span class="extra-jobs">{Enc(combo)}</span>""");
                }
                sb.Append("</div>");
            }
            sb.Append("</div></div>");
        }

        sb.Append("</div></div></body></html>");
        return sb.ToString();
    }

    private static void AppendParty(
        StringBuilder sb, PartySetupParty party, string name, string? alliance,
        IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups)
    {
        var slots = party.Slots.OrderBy(s => s.SortOrder).ToList();
        var filled = slots.Count(s => slotSignups.ContainsKey(s.Id));
        // If someone in this party has already claimed leadership, their filled slot wears
        // the crown — don't also crown the empty designated-leader seat (avoids two crowns).
        var hasSignedUpLeader = slots.Any(s => slotSignups.TryGetValue(s.Id, out var su) && su.IsPartyLeader);
        var pctWidth = slots.Count > 0 ? (int)Math.Round(filled * 100.0 / slots.Count) : 0;
        var full = slots.Count > 0 && filled == slots.Count;

        sb.Append("<div class=\"party\">");
        var small = string.IsNullOrWhiteSpace(alliance) ? string.Empty : $" <small>&middot; {Enc(alliance!.ToUpperInvariant())}</small>";
        sb.Append($"""<div class="ptitle"><span class="nm">{Enc(name)}{small}</span><span class="rule"></span><span class="cnt{(full ? " full" : string.Empty)}">{filled} / {slots.Count}</span></div>""");
        sb.Append($"""<div class="pbar{(full ? " full" : string.Empty)}"><i style="width:{pctWidth}%"></i></div>""");
        foreach (var slot in slots)
        {
            slotSignups.TryGetValue(slot.Id, out var signup);
            var roleClass = RoleClass(signup is not null && !string.IsNullOrWhiteSpace(signup.Role) ? signup.Role : slot.Role);
            if (signup is not null)
            {
                // The crown follows the ACTUAL party leader (the signed-up leader), so it
                // moves when a member takes leadership via "Make Me Party Lead".
                var crown = signup.IsPartyLeader ? "<span class=\"crown\">&#9819;</span>" : string.Empty;
                // 🔒 marks a signup locked to survive the next window advance (it's staying).
                var lockMark = signup.StayNextWindow ? "<span class=\"lock\">&#128274;</span>" : string.Empty;
                var combo = Enc(SignedUpCombo(signup, slot));
                sb.Append($"""<div class="slot"><span class="gem g-{roleClass}"><span class="face"></span><span class="spark"></span></span><span class="combo">{combo}</span><span class="who">{crown}{lockMark}{Enc(signup.CharacterName ?? "Member")}</span></div>""");
            }
            else
            {
                // An open slot that's pre-configured as this party's leader seat still shows
                // the crown, so signups can see up front that taking it makes them leader.
                // (Once someone signs up, the crown follows the ACTUAL signed-up leader above.)
                var crown = slot.IsPartyLeader && !hasSignedUpLeader ? "<span class=\"crown\">&#9819;</span>" : string.Empty;
                var combo = Enc(SlotCombo(slot));
                sb.Append($"""<div class="slot empty"><span class="gem empty b-{roleClass}"><span class="face"></span></span><span class="combo">{combo}</span><span class="vacant v-{roleClass}">{crown}&middot; vacant &middot;</span></div>""");
            }
        }
        sb.Append("</div>");
    }

    // The character name of the member designated this alliance's lead (👑 next to the
    // alliance header), or null when nobody has claimed it. At most one signup per
    // alliance carries IsAllianceLeader.
    private static string? AllianceLeadName(
        PartySetupAlliance alliance, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups)
    {
        foreach (var party in alliance.Parties)
        {
            foreach (var slot in party.Slots)
            {
                if (slotSignups.TryGetValue(slot.Id, out var su) && su.IsAllianceLeader)
                {
                    return string.IsNullOrWhiteSpace(su.CharacterName) ? "Member" : su.CharacterName!.Trim();
                }
            }
        }
        return null;
    }

    // (party, partyName, allianceName-or-null) in board order. Alliance name is
    // null when there's only one alliance (so single-alliance setups don't show a
    // redundant suffix).
    private static IEnumerable<(PartySetupParty Party, string Name, string? Alliance)> LabeledParties(PartySetup setup)
    {
        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var multi = alliances.Count > 1;
        for (var ai = 0; ai < alliances.Count; ai++)
        {
            var allianceName = string.IsNullOrWhiteSpace(alliances[ai].Name) ? $"Alliance {ai + 1}" : alliances[ai].Name!;
            var parties = alliances[ai].Parties.OrderBy(p => p.SortOrder).ToList();
            for (var pi = 0; pi < parties.Count; pi++)
            {
                var name = string.IsNullOrWhiteSpace(parties[pi].Name) ? $"Party {pi + 1}" : parties[pi].Name!;
                yield return (parties[pi], name, multi ? allianceName : null);
            }
        }
    }

    private static string RoleClass(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        "tank" => "tank",
        "heal" or "healer" => "heal",
        "support" => "supp",
        "dps" => "dps",
        _ => "any",
    };

    // "Role · MAIN/SUB" for a no-slot attendee (role + jobs from AppUserEvent).
    private static string GeneralCombo(EventSignupLine g)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(g.JobType)) { parts.Add(g.JobType!); }
        if (!string.IsNullOrWhiteSpace(g.JobName))
        {
            parts.Add(string.IsNullOrWhiteSpace(g.SubJobName) ? g.JobName! : $"{g.JobName}/{g.SubJobName}");
        }
        return string.Join(" · ", parts);
    }

    // The member's signed-up combo ("MAIN/SUB" / "MAIN" / role), falling back to
    // the slot's requirement when nothing job-ish was recorded.
    private static string SignedUpCombo(EventPartySlotSignup signup, PartySetupSlot slot)
    {
        if (!string.IsNullOrWhiteSpace(signup.MainJob))
        {
            return string.IsNullOrWhiteSpace(signup.SubJob) ? signup.MainJob! : $"{signup.MainJob}/{signup.SubJob}";
        }
        if (!string.IsNullOrWhiteSpace(signup.Role))
        {
            return signup.Role!;
        }
        return SlotCombo(slot);
    }

    // The slot's requirement as a compact combo for an open slot.
    private static string SlotCombo(PartySetupSlot slot)
    {
        if (string.Equals(slot.RequirementType, PartySetupSlotRequirementTypes.Job, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(slot.MainJob))
        {
            return string.IsNullOrWhiteSpace(slot.SubJob) ? slot.MainJob! : $"{slot.MainJob}/{slot.SubJob}";
        }
        if (string.Equals(slot.RequirementType, PartySetupSlotRequirementTypes.Role, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(slot.Role))
        {
            return slot.Role!;
        }
        return "ANY";
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
