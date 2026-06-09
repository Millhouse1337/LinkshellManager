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
        IReadOnlyList<EventSignupLine> generalSignups)
    {
        var parties = LabeledParties(setup).ToList();
        var totalSlots = parties.Sum(p => p.Party.Slots.Count);
        var filledSlots = parties.Sum(p => p.Party.Slots.Count(s => slotSignups.ContainsKey(s.Id)));
        var pct = totalSlots > 0 ? (int)Math.Round(filledSlots * 100.0 / totalSlots) : 0;

        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"/>
<link rel="preconnect" href="https://fonts.googleapis.com"/>
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin/>
<link href="https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@400;500;700&family=JetBrains+Mono:wght@400;700&display=swap" rel="stylesheet"/>
<style>
  :root{--bg:#0a0b0f;--panel:rgba(255,255,255,.04);--panel-faint:rgba(255,255,255,.012);
    --line:rgba(255,255,255,.06);--txt:#e7e9ee;--muted:#9aa0ad;--dim:#7c8290;
    --tank:#4a9eff;--heal:#3fcf6b;--supp:#f5c451;--dps:#f0556b;--mono:"JetBrains Mono",ui-monospace,monospace;}
  *{box-sizing:border-box;}
  body{margin:0;padding:0;background:#15171c;font-family:"Space Grotesk",system-ui,sans-serif;}
  .embed{width:1000px;color:var(--txt);background:var(--bg);
    background-image:linear-gradient(rgba(255,255,255,.025) 1px,transparent 1px),
                     linear-gradient(90deg,rgba(255,255,255,.025) 1px,transparent 1px);background-size:28px 28px;}
  .strip{height:3px;background:linear-gradient(90deg,var(--tank),var(--supp),var(--dps));}
  .pad{padding:22px 26px 24px;display:flex;flex-direction:column;gap:18px;}
  .head{display:flex;align-items:center;gap:14px;}
  .swords{font-size:26px;}
  .eyebrow{font-family:var(--mono);font-size:10px;letter-spacing:4px;color:var(--tank);}
  .title{font-size:28px;font-weight:700;line-height:1;letter-spacing:-.5px;color:#fff;}
  .roster{margin-left:auto;text-align:right;}
  .roster .lab{font-family:var(--mono);font-size:10px;letter-spacing:2px;color:var(--dim);}
  .roster .num{font-size:26px;font-weight:800;color:#fff;white-space:nowrap;}
  .roster .num small{color:#5b606d;font-size:18px;}
  .roster .pct{font-family:var(--mono);font-size:12px;color:var(--heal);margin-left:6px;}
  .tiles{display:flex;gap:2px;}
  .tile{flex:1;padding:10px 14px;background:rgba(255,255,255,.03);border-left:2px solid var(--tank);}
  .tile .lab{font-family:var(--mono);font-size:10px;letter-spacing:1.5px;color:var(--dim);text-transform:uppercase;}
  .tile .val{font-size:15px;font-weight:700;color:#fff;margin-top:3px;}
  .legend{display:flex;flex-wrap:wrap;gap:6px 16px;align-items:center;}
  .legend span.item{display:inline-flex;align-items:center;gap:6px;}
  .legend .sw{width:10px;height:10px;display:inline-block;}
  .legend .code{font-family:var(--mono);font-size:10px;letter-spacing:1px;color:var(--muted);}
  .legend .name{font-size:11px;color:#6b7180;}
  .row{display:flex;gap:16px;}
  .party{flex:1;display:flex;flex-direction:column;gap:6px;min-width:0;}
  .party .ptitle{display:flex;align-items:center;justify-content:space-between;}
  .party .ptitle .nm{font-family:var(--mono);font-size:12px;font-weight:700;letter-spacing:1px;color:#fff;}
  .party .ptitle .nm small{color:var(--dim);font-weight:400;}
  .party .ptitle .cnt{font-family:var(--mono);font-size:11px;color:var(--muted);}
  .party .ptitle .cnt.full{color:var(--heal);}
  .bar{height:3px;background:rgba(255,255,255,.07);}
  .bar>i{display:block;height:100%;background:var(--tank);}
  .bar.full>i{background:var(--heal);}
  .slots{display:flex;flex-direction:column;gap:4px;margin-top:2px;}
  .slot{display:flex;background:var(--panel-faint);border:1px solid var(--line);}
  .slot.on{background:var(--panel);}
  .slot .accent{width:3px;flex-shrink:0;}
  .slot .accent.empty{opacity:.55;}
  .slot .meat{flex:1;padding:6px 8px 6px 10px;display:flex;align-items:center;justify-content:space-between;gap:8px;min-width:0;}
  .slot .combo{font-family:var(--mono);font-size:12.5px;font-weight:700;letter-spacing:.5px;color:#6b7180;white-space:nowrap;}
  .slot.on .combo{color:#fff;}
  .slot .who{font-size:11.5px;color:#565c6b;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}
  .slot.on .who{color:#aab0bd;}
  .slot .open{font-family:var(--mono);font-size:10px;letter-spacing:1.5px;}
  .lead{color:var(--supp);}
  .bg-tank{background:var(--tank);} .bg-heal{background:var(--heal);} .bg-supp{background:var(--supp);} .bg-dps{background:var(--dps);} .bg-any{background:#5b606d;}
  .r-tank{color:var(--tank);} .r-heal{color:var(--heal);} .r-supp{color:var(--supp);} .r-dps{color:var(--dps);} .r-any{color:#7c8290;}
  .foot{padding-top:14px;border-top:1px solid rgba(255,255,255,.08);}
  .help{font-family:var(--mono);font-size:11px;color:var(--dim);letter-spacing:.5px;}
</style></head><body><div class="embed"><div class="strip"></div><div class="pad">
""");

        // Header
        var eyebrow = string.IsNullOrWhiteSpace(ev.EventType)
            ? "EVENT"
            : $"{Enc(ev.EventType!.Trim().ToUpperInvariant())} // EVENT";
        sb.Append($"""
<div class="head"><span class="swords">&#9876;&#65039;</span><div>
<div class="eyebrow">{eyebrow}</div><div class="title">{Enc((ev.EventName ?? $"Event #{ev.Id}").Trim())}</div></div>
<div class="roster"><div class="lab">ROSTER FILL</div>
<div><span class="num">{filledSlots}<small>/{totalSlots}</small></span><span class="pct">{pct}%</span></div></div></div>
""");

        // Stat tiles
        var startsVal = ev.CommencementStartTime is { } c ? FormatLocal(c) : (ev.StartTime is { } s ? FormatLocal(s) : "TBD");
        var startsLab = ev.CommencementStartTime is not null ? "Started" : "Starts";
        var status = ev.CommencementStartTime is not null ? "Live" : "Queued";
        var reward = ev.DkpPerHour is { } dkp ? $"{Enc(dkp.ToString())} DKP/hr" : "&mdash;";
        var location = string.IsNullOrWhiteSpace(ev.EventLocation) ? "&mdash;" : Enc(ev.EventLocation!.Trim());
        sb.Append($"""
<div class="tiles">
<div class="tile" style="border-left-color:var(--tank)"><div class="lab">{startsLab}</div><div class="val">{Enc(startsVal)}</div></div>
<div class="tile" style="border-left-color:var(--dim)"><div class="lab">Status</div><div class="val">{status}</div></div>
<div class="tile" style="border-left-color:var(--supp)"><div class="lab">Reward</div><div class="val">{reward}</div></div>
<div class="tile" style="border-left-color:var(--dps)"><div class="lab">Location</div><div class="val">{location}</div></div></div>
""");

        // Legend
        sb.Append("""
<div class="legend">
<span class="item"><span class="sw bg-tank"></span><span class="code">TANK</span><span class="name">Tank</span></span>
<span class="item"><span class="sw bg-heal"></span><span class="code">HEAL</span><span class="name">Healer</span></span>
<span class="item"><span class="sw bg-supp"></span><span class="code">SUPP</span><span class="name">Support</span></span>
<span class="item"><span class="sw bg-dps"></span><span class="code">DPS</span><span class="name">Melee / Caster</span></span></div>
""");

        // Parties — 3 per row.
        foreach (var chunk in parties.Chunk(3))
        {
            sb.Append("<div class=\"row\">");
            foreach (var (party, name, alliance) in chunk)
            {
                AppendParty(sb, party, name, alliance, slotSignups);
            }
            // Pad short final rows with empty columns so the 3 keep their width.
            for (var i = chunk.Length; i < 3 && parties.Count > 1; i++)
            {
                sb.Append("<div class=\"party\"></div>");
            }
            sb.Append("</div>");
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
            var names = string.Join(" &middot; ", extra.Select(g =>
                $"{Enc(g.CharacterName)}{(string.IsNullOrWhiteSpace(g.JobName) ? string.Empty : $" ({Enc(g.JobName!)})")}"));
            sb.Append($"""<div class="help">ALSO ATTENDING &mdash; NO SLOT ({extra.Count}): {names}</div>""");
        }

        sb.Append("""<div class="foot"><div class="help">SIGN UP to claim a slot &middot; JOIN (NO SLOT) for attendance &middot; LEAVE EVENT to drop out</div></div>""");
        sb.Append("</div></div></body></html>");
        return sb.ToString();
    }

    private static void AppendParty(
        StringBuilder sb, PartySetupParty party, string name, string? alliance,
        IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups)
    {
        var slots = party.Slots.OrderBy(s => s.SortOrder).ToList();
        var filled = slots.Count(s => slotSignups.ContainsKey(s.Id));
        var pctWidth = slots.Count > 0 ? (int)Math.Round(filled * 100.0 / slots.Count) : 0;
        var full = slots.Count > 0 && filled == slots.Count;

        sb.Append("<div class=\"party\">");
        var small = string.IsNullOrWhiteSpace(alliance) ? string.Empty : $" <small>&middot; {Enc(alliance!.ToUpperInvariant())}</small>";
        sb.Append($"""<div class="ptitle"><span class="nm">{Enc(name)}{small}</span><span class="cnt{(full ? " full" : string.Empty)}">{filled}/{slots.Count}</span></div>""");
        sb.Append($"""<div class="bar{(full ? " full" : string.Empty)}"><i style="width:{pctWidth}%"></i></div>""");
        sb.Append("<div class=\"slots\">");
        foreach (var slot in slots)
        {
            slotSignups.TryGetValue(slot.Id, out var signup);
            var roleClass = RoleClass(signup is not null && !string.IsNullOrWhiteSpace(signup.Role) ? signup.Role : slot.Role);
            if (signup is not null)
            {
                var crown = signup.IsPartyLeader ? "<span class=\"lead\">&#9733;</span> " : string.Empty;
                var combo = Enc(SignedUpCombo(signup, slot));
                sb.Append($"""<div class="slot on"><div class="accent bg-{roleClass}"></div><div class="meat"><div class="info"><div class="combo">{combo}</div><div class="who">{crown}{Enc(signup.CharacterName ?? "Member")}</div></div></div></div>""");
            }
            else
            {
                var combo = Enc(SlotCombo(slot));
                sb.Append($"""<div class="slot"><div class="accent empty bg-{roleClass}"></div><div class="meat"><div class="info"><div class="combo">{combo}</div><div class="who"><span class="open r-{roleClass}">OPEN</span></div></div></div></div>""");
            }
        }
        sb.Append("</div></div>");
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

    private static string FormatLocal(DateTime utc)
    {
        // The board has no per-viewer timezone (it's a baked image), so show UTC
        // explicitly rather than implying the server's local time.
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return u.ToString("MMM d, yyyy · h:mm tt") + " UTC";
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
