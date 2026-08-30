using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the wide event-board payload.
//
// The board is a CLASSIC message with the roster in `content` as a fenced code block, because
// that is the only shape that is wide AND columned AND keeps the icons. Measured with a
// 140-char ruler: `content` runs ~136 characters, a code block inside it ~112, an embed ~430px,
// a Components V2 text component ~70 — and V2 cannot carry `content` at all.
//
// Invariants: no V2 flag; the grid in `content` under Discord's 2000-char cap; a constant emoji
// count per cell (an emoji is ~2.3 monospace cells, so only a constant count keeps the columns
// from shearing); columns starting at the same offset on every row; the key naming every icon;
// and the same signup buttons surviving.

public class ComponentsV2BoardTests
{
    // Serializer mirrors DiscordBotClient.JsonOptions (camelCase + drop nulls) so the
    // assertions see the exact JSON that would go on the wire.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // A two-alliance board with a couple of seated members and one open slot, so both the
    // wide layout and the multi-section roster text fallback get exercised.
    private static (Event Ev, PartySetup Setup, Dictionary<int, EventPartySlotSignup> Signups) BuildBoard()
    {
        var a1p1 = new PartySetupParty
        {
            Id = 11, PartySetupAllianceId = 21, SortOrder = 0, Name = "Party 1",
            Slots = new List<PartySetupSlot>
            {
                new() { Id = 101, PartySetupPartyId = 11, SortOrder = 0, RequirementType = "Any" },
                new() { Id = 102, PartySetupPartyId = 11, SortOrder = 1, RequirementType = "Any" },
            },
        };
        var a2p1 = new PartySetupParty
        {
            Id = 12, PartySetupAllianceId = 22, SortOrder = 0, Name = "Party 1",
            Slots = new List<PartySetupSlot>
            {
                new() { Id = 201, PartySetupPartyId = 12, SortOrder = 0, RequirementType = "Any" },
            },
        };
        var setup = new PartySetup
        {
            Id = 31, Name = "Sky",
            Alliances = new List<PartySetupAlliance>
            {
                new() { Id = 21, PartySetupId = 31, SortOrder = 0, Name = "Alliance 1", Parties = new List<PartySetupParty> { a1p1 } },
                new() { Id = 22, PartySetupId = 31, SortOrder = 1, Name = "Alliance 2", Parties = new List<PartySetupParty> { a2p1 } },
            },
        };
        var ev = new Event { Id = 7, EventName = "Kirin", EventType = "Sky" };
        var signups = new Dictionary<int, EventPartySlotSignup>
        {
            [101] = new() { Id = 1, EventId = 7, PartySetupSlotId = 101, CharacterName = "Alpha", Role = "Tank", MainJob = "PLD", SubJob = "NIN", IsPartyLeader = true },
            [201] = new() { Id = 2, EventId = 7, PartySetupSlotId = 201, CharacterName = "Bravo", Role = "DPS", MainJob = "WAR", SubJob = "NIN" },
            // slot 102 intentionally left open
        };
        return (ev, setup, signups);
    }

    // The board is a CLASSIC message with the roster in `content` as a fenced code block.
    //
    // Measured with a 140-char ruler on one window, and this is the whole reason for the shape:
    //   embed fields         ~430px   columns ✓  icons ✓   (an embed is hard-capped — too narrow)
    //   flowing markdown    ~1100px   columns ✗  icons ✓
    //   fenced code block   ~1080px   columns ✓  icons ✓   ← the only one with all three
    //   Components V2 text   ~600px   and V2 forbids `content` outright
    [Fact]
    public void WideBoard_IsAClassicMessage_WithTheGridInContent()
    {
        var (ev, setup, signups) = BuildBoard();

        var payload = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, signups);
        var json = JsonSerializer.Serialize(payload, Wire);
        using var doc = JsonDocument.Parse(json);

        Assert.DoesNotContain("\"flags\":32768", json);   // NOT Components V2 — that's a narrower cap
        var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;
        Assert.Contains("```", content);                 // the grid is a code block
        Assert.Contains("Alpha", content);
        Assert.Contains("Bravo", content);
        Assert.True(content.Length <= 2000, $"content is {content.Length}; Discord rejects over 2000");
        Assert.Contains("\"custom_id\":\"evt:pssignup:7\"", json); // signup buttons preserved
    }

    // NO rendered picture on this board. The grid already shows every slot, job, name and icon,
    // so the PNG underneath was the same roster twice — and it was the expensive half, a
    // headless-Chromium render on every signup refresh. `attachments` must still be sent EMPTY
    // rather than omitted, or an edit from a board that had one leaves the old file attached.
    [Fact]
    public void WideBoard_CarriesNoRenderedImage()
    {
        var (ev, setup, signups) = BuildBoard();

        var payload = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, signups);
        var json = JsonSerializer.Serialize(payload, Wire);
        using var doc = JsonDocument.Parse(json);

        Assert.DoesNotContain("attachment://", json);
        Assert.True(doc.RootElement.TryGetProperty("attachments", out var attachments));
        Assert.Empty(attachments.EnumerateArray());
        foreach (var embed in doc.RootElement.GetProperty("embeds").EnumerateArray())
        {
            Assert.False(embed.TryGetProperty("image", out _));
        }
    }

    // THE alignment invariant, and the reason the icons could come back at all.
    //
    // An emoji is ~2.3 monospace cells — NOT a whole number — so their width only cancels out
    // when every cell carries the SAME count in the SAME position: one role icon, then one
    // state icon, with ▪️ standing in when a member is neither leader nor staying. Break that
    // and every column right of the first crown shears.
    [Fact]
    public void WideBoard_GivesEveryCellExactlyTwoIcons_SoTheColumnsHold()
    {
        var (ev, setup, signups) = BuildBoard();
        signups[101].StayNextWindow = true;   // a state icon that is NOT the crown

        var payload = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, signups);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire));
        var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;

        var roleIcons = new[] { "🔵", "🟢", "🟡", "🔴", "⚪" };
        var stateIcons = new[] { "👑", "🔒", "▪️" };

        var rows = GridRows(content).ToList();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            var role = roleIcons.FirstOrDefault(row.StartsWith);
            Assert.True(role is not null, $"every cell must open with a role icon: {row}");
            var after = row[role!.Length..];
            Assert.True(stateIcons.Count(after.StartsWith) == 1,
                $"every cell needs exactly one state icon (▪️ when neither): {row}");
        }
    }

    [Fact]
    public void WideBoard_RostersBothMembers()
    {
        var (ev, setup, signups) = BuildBoard();
        signups[101].StayNextWindow = true;

        var payload = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, signups);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire));
        var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;

        // Every cell in this fixture is one column wide, so alignment is asserted on the
        // multi-column board below instead. Here just prove the rows exist and carry both.
        Assert.Contains(GridRows(content), r => r.Contains("Alpha"));
        Assert.Contains(GridRows(content), r => r.Contains("Bravo"));
    }

    // THE alignment invariant, on a board that actually has columns: three parties in one
    // alliance, one member with a crown and a lock so the icon prefix varies. Alignment means
    // the COLUMNS start at the same offset on every row — NOT that the rows are equal length,
    // which they aren't, since trailing padding is trimmed and names differ in length.
    [Fact]
    public void WideBoard_ColumnsStartAtTheSameOffsetOnEveryRow()
    {
        var signups = new Dictionary<int, EventPartySlotSignup>();
        var parties = new List<PartySetupParty>();
        var slotId = 1;
        for (var p = 0; p < 3; p++)
        {
            var slots = new List<PartySetupSlot>();
            for (var s = 0; s < 6; s++)
            {
                slots.Add(new PartySetupSlot
                {
                    Id = slotId, SortOrder = s, Role = "Tank",
                    MainJob = "PLD", SubJob = "NIN", RequirementType = "Job",
                });
                // Leave some open, vary the icons, vary the name lengths.
                if (s % 2 == 0)
                {
                    signups[slotId] = new EventPartySlotSignup
                    {
                        PartySetupSlotId = slotId,
                        CharacterName = s == 0 ? "Al" : "Bartholomewxyz",
                        Role = "Tank", MainJob = "PLD", SubJob = "NIN",
                        IsPartyLeader = s == 0,
                        StayNextWindow = s == 2,
                    };
                }
                slotId++;
            }
            parties.Add(new PartySetupParty { Id = p, Name = $"Party {p + 1}", SortOrder = p, Slots = slots });
        }
        var setup = new PartySetup
        {
            Id = 77, Name = "Sky",
            Alliances = new List<PartySetupAlliance>
            {
                new() { Id = 1, PartySetupId = 77, SortOrder = 0, Name = "Alliance 1", Parties = parties },
            },
        };

        var ev = new Event { Id = 77, EventName = "Kirin", EventType = "Sky" };
        var payload = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, signups);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire));
        var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;

        var starts = GridRows(content)
            .Select(r => r.IndexOf("🔵", 1, StringComparison.Ordinal))
            .Where(i => i > 0)
            .Distinct()
            .ToList();

        Assert.NotEmpty(starts);
        Assert.True(starts.Count == 1,
            $"the second column must start at the same offset on every row; saw {string.Join(", ", starts)}");
    }

    // A readiness slot costs an emoji in every cell (the count has to stay constant), so a slot
    // is reserved only for a tag somebody has actually claimed. On a board where nobody has,
    // those slots would be a wall of spacers carrying no information.
    [Fact]
    public void Readiness_ShowsInTheGridOnlyWhenSomebodyHasSetATag()
    {
        var (ev, setup, signups) = BuildBoard();

        var untagged = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, signups);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(untagged, Wire)))
        {
            var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;
            foreach (var row in GridRows(content))
            {
                // Two icons only — role + state — so no slots are wasted on an untagged board.
                Assert.DoesNotContain("🧪", row);
                Assert.DoesNotContain("🛡️", row);
                Assert.DoesNotContain("🗡️", row);
            }
        }

        signups[101].EnfeebReady = true;
        signups[201].RelicWeapon = true;
        var tagged = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, signups);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(tagged, Wire)))
        {
            var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;
            var grid = string.Join("\n", GridRows(content));
            Assert.Contains("🧪", grid);
            Assert.Contains("🗡️", grid);
        }
    }

    // The name column is sized to the longest name actually PRESENT, not to FFXI's 15-char
    // limit — so a board of vacancies puts the job right against the icons instead of across a
    // corridor of padding nobody is standing in.
    [Fact]
    public void EmptyBoard_HasNoNameColumn()
    {
        var (ev, setup, _) = BuildBoard();

        var payload = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, new Dictionary<int, EventPartySlotSignup>());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire));
        var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;

        var roleIcons = new[] { "🔵", "🟢", "🟡", "🔴", "⚪" };
        var stateIcons = new[] { "👑", "🔒", "▪️" };
        foreach (var row in GridRows(content))
        {
            // Exactly one space between the icons and the requirement — no name column at all.
            // (Trailing padding before the column gutter is fine and expected.)
            var afterRole = row[roleIcons.First(row.StartsWith).Length..];
            var afterState = afterRole[stateIcons.First(afterRole.StartsWith).Length..];
            Assert.StartsWith(" ", afterState);
            Assert.False(afterState.StartsWith("  ", StringComparison.Ordinal),
                $"the job should sit against the icons, not across a corridor: {row}");
        }
        // And the party heading still fits: the column widens to its header rather than
        // hard-trimming "Party 1 (0/2)" down to "Party 1 (0/2". The fixture's parties hold 2
        // and 1 slots, so the counts are (0/2) and (0/1).
        Assert.Contains("Party 1 (0/2)", content);
        Assert.Contains("Party 1 (0/1)", content);
    }

    // Every message the board occupies stays inside Discord's 2000-character cap. That used to
    // be a squeeze on ONE message — names shed, spacing collapsed, icons dropped — and is now
    // simply true, because each alliance has its own message and uses about half of it.
    [Fact]
    public void EveryBoardMessage_StaysUnderTheContentCap()
    {
        var (ev, setup, signups) = BigBoard();

        var messages = DiscordEventMessageBuilder.BuildWideBoardMessages(
            ev, Array.Empty<EventSignupLine>(), setup, signups);

        Assert.NotEmpty(messages);
        foreach (var message in messages)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(message, Wire));
            var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;
            Assert.True(content.Length <= 2000, $"a board message is {content.Length}");
            Assert.False(content.TrimEnd().EndsWith("...", StringComparison.Ordinal),
                "no message may end mid-truncation");
        }
    }

    // The rows inside every fenced block — what alignment is asserted on. Header rows are
    // excluded: they are pure ASCII standing over proportional icons, padded on a
    // 2-characters-per-emoji approximation, so they legitimately differ in length.

    // The wide board is now a SET of messages, one per alliance. Most assertions below care
    // about the board as a whole — is every party present, do the columns line up, is the key
    // there — so this flattens it back into one payload: every message's content joined, and
    // the components/embeds from the last message, which is where they live.
    //
    // WideBoardIsSplitPerAlliance covers the split itself; everything else reads the whole.
    private static object MergedWideBoard(
        Event ev, IReadOnlyList<EventSignupLine> signups, PartySetup setup,
        IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups)
    {
        var messages = DiscordEventMessageBuilder.BuildWideBoardMessages(ev, signups, setup, slotSignups)
            .Cast<IDictionary<string, object?>>()
            .ToList();

        return new Dictionary<string, object?>
        {
            ["content"] = string.Join("\n", messages.Select(m => m["content"] as string ?? string.Empty)),
            ["components"] = messages[^1]["components"],
            ["embeds"] = messages[^1]["embeds"],
            ["attachments"] = messages[^1]["attachments"],
        };
    }
    private static IEnumerable<string> GridRows(string content)
    {
        var inBlock = false;
        var firstOfBlock = true;
        foreach (var line in content.Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inBlock = !inBlock;
                firstOfBlock = true;
                continue;
            }
            if (!inBlock || line.Length == 0) { continue; }
            if (firstOfBlock) { firstOfBlock = false; continue; } // party-name header row
            yield return Visible(line);
        }
    }

    // Strip the ANSI escapes. Every width and position assertion has to measure what a reader
    // SEES — an escape is zero-width on screen and several characters long in the string, so
    // asserting on the raw text would measure the wrong thing entirely.
    private static string Visible(string text)
        => Regex.Replace(text, ((char)0x1B) + Regex.Escape("[") + "[0-9;]*m", string.Empty);


    // The key has to explain every icon that can appear inline, or a bare ⬛ on somebody's name
    // is a mystery. Readiness only earned a key entry once it moved back onto the members.
    [Fact]
    public void GridKey_ExplainsEveryIconTheBoardCanShow()
    {
        var (ev, setup, signups) = BuildBoard();
        signups[101].StayNextWindow = true;

        var payload = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, signups);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire));

        // The key sits ABOVE the grid — you read it before the icons it explains — and it lists
        // EVERY readiness tag even when nobody has claimed one. It doubles as the advertisement
        // for the "My Readiness" button: a member has to see that a tag exists before setting it.
        var key = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;

        Assert.Contains("👑 party leader", key);
        Assert.Contains("🔒 staying next window", key);
        foreach (var tag in EventReadiness.All)
        {
            Assert.Contains($"{tag.Emoji} {tag.Label}", key);
        }
    }




    [Fact]
    public void DefeatedNoticeV2_IsV2Flagged_NoEmbeds()
    {
        var payload = DiscordEventMessageBuilder.BuildV2DefeatedNoticeMessage(
            "💀 Kirin defeated", "Will repop soon.");
        var json = JsonSerializer.Serialize(payload, Wire);

        Assert.Contains("\"flags\":32768", json);
        Assert.Contains("\"type\":17", json);
        Assert.Contains("Kirin defeated", json);
        Assert.DoesNotContain("\"embeds\":", json);
    }

    // The canvas is a ZOOM control, and it is inverted: Discord scales the board down to fit
    // a fixed message column, so a wider canvas arrives SMALLER. These pin the sizing rule
    // that replaced the old fixed 1600/2000 canvases (the 2000px "wide" V2 one rendered at
    // roughly 30% and was the least readable of the two).
    [Fact]
    public void Canvas_IsSizedFromContent_NotFromTheV2Flag()
    {
        var (ev, setup, signups) = BuildBoard(); // two alliances of ONE party each

        var html = EventBoardHtmlBuilder.Build(ev, setup, signups, Array.Empty<EventSignupLine>());

        // One party per row needs one column, so this board sits at the floor.
        Assert.Equal(680, EventBoardHtmlBuilder.CardWidthFor(setup));
        Assert.Contains($".embed{{width:{EventBoardHtmlBuilder.CardWidthFor(setup)}px;}}", html);
    }

    [Fact]
    public void Canvas_GrowsWithColumns_ButStaysFarUnderTheOldFixedWidths()
    {
        var (_, oneColumn, _) = BuildBoard();
        var threeColumn = new PartySetup
        {
            Id = 41,
            Name = "Sky",
            Alliances = new List<PartySetupAlliance>
            {
                new()
                {
                    Id = 51, PartySetupId = 41, SortOrder = 0, Name = "Alliance 1",
                    Parties = new List<PartySetupParty>
                    {
                        new() { Id = 61, PartySetupAllianceId = 51, SortOrder = 0, Name = "Party 1" },
                        new() { Id = 62, PartySetupAllianceId = 51, SortOrder = 1, Name = "Party 2" },
                        new() { Id = 63, PartySetupAllianceId = 51, SortOrder = 2, Name = "Party 3" },
                    },
                },
            },
        };

        var narrow = EventBoardHtmlBuilder.CardWidthFor(oneColumn);
        var wide = EventBoardHtmlBuilder.CardWidthFor(threeColumn);

        Assert.True(wide > narrow, "a 3-party row needs more columns than a 1-party row");
        // The whole point: even the widest board is well under the canvas it used to render
        // at, so Discord squeezes it far less.
        Assert.True(wide < EventBoardImageRenderer.DefaultCardWidth);
        // And still inside Discord's 4096px/side preview cap at the 2× render density.
        Assert.True(wide * 2 <= 4096);
        Assert.True(EventBoardImageRenderer.MaxCardWidth * 2 <= 4096);
    }

    // A vacant slot says so. It used to be a blank run in the name column, which reads as a
    // rendering fault rather than an opening — and it is the reason that column no longer
    // collapses to nothing on an empty board.
    [Fact]
    public void VacantSlots_AreLabelled()
    {
        var (ev, setup, signups) = BuildBoard();   // slot 102 is deliberately open

        var payload = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, signups);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire));
        var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;

        Assert.Contains("Vacant", content);
    }

    // Column separation is spent from whatever the names leave behind: a board with room gets
    // the wide gutter, a packed one spends it on characters of name instead. Names outrank
    // spacing because a name is information and a gap is not.
    // Columns SPREAD to the edge of the code block rather than bunching left with dead space to
    // the right of the last party. The gutter is computed from the display width — emoji counted
    // at ~2.3 cells, since a cell's width on screen isn't its string length.
    //
    // This used to be a trade against name width on a big board. Since the split it isn't: every
    // alliance has its own message and its own room, so the big board spreads too.
    [Fact]
    public void Columns_SpreadAcrossTheFullWidth_EvenOnTheBigBoard()
    {
        var (ev, setup, signups) = BigBoard();

        var messages = DiscordEventMessageBuilder.BuildWideBoardMessages(
            ev, Array.Empty<EventSignupLine>(), setup, signups);

        var widest = 0;
        foreach (var message in messages)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(message, Wire));
            var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;
            foreach (var row in GridRows(content))
            {
                widest = Math.Max(widest, row.Length);
                // Measured between columns on a DATA row: the cell text fills its column exactly,
                // so the run of spaces before the next role icon IS the gutter.
                var second = row.IndexOf("🔵", 1, StringComparison.Ordinal);
                if (second > 0)
                {
                    var gutter = second - row[..second].TrimEnd().Length;
                    Assert.True(gutter >= 1, "columns must always keep at least one space between them");
                }
            }
        }

        // Measured in CHARACTERS, which UNDERCOUNTS the emoji — so a row already near the cap by
        // this measure is certainly filling the block.
        Assert.True(widest >= 90, $"columns should spread toward the block's edge; widest row was {widest}");
    }

    // Columns are SPREAD to the edge of the code block, not left bunched with dead space to the
    // right of the last party. The gutter is computed from the display width — emoji counted at
    // ~2.3 cells, since a cell's width on screen isn't its string length — and then capped by
    // whatever the 2000-char budget can afford.
    [Fact]
    public void SmallBoard_SpreadsItsColumnsAcrossTheFullWidth()
    {
        var parties = new List<PartySetupParty>();
        var slotId = 1;
        for (var p = 0; p < 3; p++)
        {
            var slots = new List<PartySetupSlot>();
            for (var s = 0; s < 6; s++)
            {
                slots.Add(new PartySetupSlot
                {
                    Id = slotId++, SortOrder = s, Role = "Tank",
                    MainJob = "PLD", SubJob = "NIN", RequirementType = "Job",
                });
            }
            parties.Add(new PartySetupParty { Id = p, Name = $"Party {p + 1}", SortOrder = p, Slots = slots });
        }
        var setup = new PartySetup
        {
            Id = 88, Name = "Sky",
            Alliances = new List<PartySetupAlliance>
            {
                new() { Id = 1, PartySetupId = 88, SortOrder = 0, Name = "Alliance 1", Parties = parties },
            },
        };

        var ev = new Event { Id = 88, EventName = "Kirin", EventType = "Sky" };
        var payload = MergedWideBoard(
            ev, Array.Empty<EventSignupLine>(), setup, new Dictionary<int, EventPartySlotSignup>());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire));
        var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;

        // One empty alliance is nowhere near the cap, so nothing stops the columns reaching the
        // right-hand edge. Measured in CHARACTERS, which undercounts the emoji — so a row that
        // is already at the cap by this measure is certainly filling the block.
        var widest = GridRows(content).Select(r => r.Length).DefaultIfEmpty(0).Max();
        Assert.True(widest >= 88, $"columns should spread toward the block's edge; widest row was {widest}");
        Assert.True(content.Length <= 2000);
    }

    // Builds the 3-alliance, 54-slot board that drove the split, with one member carrying all
    // three readiness tags — the exact shape that used to overflow.
    private static (Event Ev, PartySetup Setup, Dictionary<int, EventPartySlotSignup> Signups) BigBoard()
    {
        var jobs = new[] { ("PLD", "NIN"), ("WAR", "NIN"), ("SAM", "THF"), ("RDM", "WHM"), ("BRD", "WHM"), ("WHM", "BLM") };
        var roles = new[] { "Tank", "DPS", "DPS", "DPS", "DPS", "DPS" };
        var signups = new Dictionary<int, EventPartySlotSignup>();
        var alliances = new List<PartySetupAlliance>();
        var slotId = 1;
        for (var a = 0; a < 3; a++)
        {
            var parties = new List<PartySetupParty>();
            for (var p = 0; p < 3; p++)
            {
                var slots = new List<PartySetupSlot>();
                for (var s = 0; s < 6; s++)
                {
                    var (main, sub) = jobs[s];
                    slots.Add(new PartySetupSlot
                    {
                        Id = slotId, SortOrder = s, Role = roles[s],
                        MainJob = main, SubJob = sub, RequirementType = "Job", IsPartyLeader = s == 5,
                    });
                    if (a == 0 && p == 0 && s == 0)
                    {
                        signups[slotId] = new EventPartySlotSignup
                        {
                            PartySetupSlotId = slotId, CharacterName = "Millhouse",
                            Role = roles[s], MainJob = main, SubJob = sub,
                            EnfeebReady = true, ResistReady = true, RelicWeapon = true,
                        };
                    }
                    slotId++;
                }
                parties.Add(new PartySetupParty { Id = 100 * a + p, Name = $"Party {p + 1}", SortOrder = p, Slots = slots });
            }
            alliances.Add(new PartySetupAlliance { Id = a + 1, Name = $"Alliance {a + 1}", SortOrder = a, Parties = parties });
        }

        // A day number and a start time, both of which lengthen the heading — leaving them out is
        // what made an earlier test board fit when the real one didn't.
        var ev = new Event
        {
            Id = 99, EventName = "test", EventType = "HNM", AssignedMonsterName = "Adamantoise",
            DayNumber = 1,
            StartTime = new DateTime(2026, 8, 27, 1, 34, 42, DateTimeKind.Utc),
            SpawnWindowCount = 7, SpawnWindowMinutes = 60, HnmWindowNumber = 1,
        };
        return (ev, new PartySetup { Id = 99, Name = "Sky", Alliances = alliances }, signups);
    }

    // THE POINT OF THE SPLIT. This board needed ~2100 characters as one message and Discord caps
    // at 2000, which is what cost — in turn — full names, column spacing, grey vacancies and the
    // readiness icons. One message per alliance gives each its own 2000, and all of it fits.
    [Fact]
    public void BigBoard_SplitsPerAlliance_AndKeepsEverything()
    {
        var (ev, setup, signups) = BigBoard();

        var messages = DiscordEventMessageBuilder.BuildWideBoardMessages(
            ev, Array.Empty<EventSignupLine>(), setup, signups);

        Assert.Equal(3, messages.Count);   // one per alliance

        var contents = new List<string>();
        foreach (var message in messages)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(message, Wire));
            var content = doc.RootElement.GetProperty("content").GetString() ?? string.Empty;
            contents.Add(content);

            Assert.True(content.Length <= 2000, $"a board message is {content.Length}; Discord rejects over 2000");
            Assert.True(content.Split("```").Length - 1 == 2,
                "each message holds exactly one code block, opened and closed");
        }

        var whole = string.Join("\n", contents);
        Assert.Contains("Alliance 1", whole);
        Assert.Contains("Alliance 2", whole);
        Assert.Contains("Alliance 3", whole);
        Assert.Equal(9, Regex.Matches(whole, @"Party \d \(\d/\d\)").Count);

        // Everything that used to be traded away, all present at once.
        Assert.Contains("Millhouse", whole);   // a full name, not clipped to fit
        Assert.Contains("🧪", whole);                // readiness icons in the grid
        Assert.Contains("🛡️", whole);
        Assert.Contains("🗡️", whole);
        Assert.Contains("<Vacant>", whole);         // and the vacancy labels
    }

    // The heading and key ride on the FIRST message; the buttons and the no-slot roster on the
    // LAST, so a reader meets them after the parties rather than between them.
    [Fact]
    public void SplitBoard_PutsTheHeaderFirstAndTheButtonsLast()
    {
        var (ev, setup, signups) = BigBoard();

        var messages = DiscordEventMessageBuilder.BuildWideBoardMessages(
            ev, Array.Empty<EventSignupLine>(), setup, signups)
            .Select(m => JsonDocument.Parse(JsonSerializer.Serialize(m, Wire)))
            .ToList();
        try
        {
            var first = messages[0].RootElement.GetProperty("content").GetString() ?? string.Empty;
            Assert.Contains("👑 party leader", first);      // the icon key
            Assert.Contains("Alliance 1", first);

            for (var i = 0; i < messages.Count; i++)
            {
                var buttons = messages[i].RootElement.GetProperty("components").GetArrayLength();
                var expected = i == messages.Count - 1;
                Assert.True(expected == (buttons > 0),
                    $"message {i} should {(expected ? "carry" : "not carry")} the buttons");
            }

            var json = JsonSerializer.Serialize(messages[^1].RootElement, Wire);
            Assert.Contains("evt:pssignup:99", json);
        }
        finally
        {
            foreach (var doc in messages) { doc.Dispose(); }
        }
    }
}
