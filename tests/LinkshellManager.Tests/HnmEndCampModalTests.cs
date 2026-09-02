using System.Text.Json;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The "End Camp / Enter ToD" modal. Discord allows a modal FIVE components and no more — a sixth is
// a rejected interaction, i.e. an officer clicking End Camp and getting nothing. That cap is why the
// outcome is one three-way dropdown rather than separate Claimed/Killed fields, so it's asserted
// here rather than left to be discovered in production on the one monster family that fills all five.
public class HnmEndCampModalTests
{
    private const int DiscordModalComponentCap = 5;

    private static Event Camp(string monster, int windowNumber = 1) => new()
    {
        Id = 7,
        EventName = monster,
        EventType = "HNM",
        AssignedMonsterName = monster,
        HnmWindowNumber = windowNumber,
        StartTime = new DateTime(2026, 8, 12, 20, 0, 0, DateTimeKind.Utc),
    };

    private static JsonElement Fields(Event ev, double? currentLeadHours = null) =>
        JsonSerializer.SerializeToElement(
            DiscordInteractionsController.BuildWdPopModalFields(ev, currentLeadHours));

    // A field is either a text input in an action row (type 1 → components[0]) or a select wrapped
    // in a Label (type 18 → component). Both shapes carry the custom_id one level down.
    private static string FieldId(JsonElement field) =>
        field.GetProperty("type").GetInt32() == 1
            ? field.GetProperty("components")[0].GetProperty("custom_id").GetString()!
            : field.GetProperty("component").GetProperty("custom_id").GetString()!;

    private static IEnumerable<string> FieldIds(Event ev) =>
        Fields(ev).EnumerateArray().Select(FieldId);

    private static JsonElement Options(JsonElement field) =>
        field.GetProperty("component").GetProperty("options");

    // Every monster the modal can open on, including the HQ families that carry the extra NQ/HQ row.
    [Theory]
    [InlineData("Tiamat")]
    [InlineData("Jormungand")]
    [InlineData("Vrtra")]
    [InlineData("Cerberus")]
    [InlineData("Behemoth/King Behemoth")]
    [InlineData("Fafnir/Nidhogg")]
    [InlineData("Adamantoise/Aspidochelone")]
    [InlineData("Behemoth")]
    [InlineData("Bune")]
    [InlineData("Goblin Furrier")]
    public void NoCamp_ExceedsDiscordsModalCap(string monster)
    {
        var count = Fields(Camp(monster)).GetArrayLength();

        Assert.InRange(count, 1, DiscordModalComponentCap);
    }

    // The HQ families are the tight case: they're the only camps carrying all five rows, which is
    // exactly why Claimed and Killed had to fold back into one Outcome field to fit pop window and
    // the re-post lead. If this ever reads six, the merge got undone.
    [Fact]
    public void HqFamily_FillsAllFiveRows_InOrder()
    {
        var fields = FieldIds(Camp("Behemoth/King Behemoth")).ToList();

        Assert.Equal(new[] { "wdpop_tod", "wdpop_hq", "wdpop_outcome", "wdpop_window", "wdpop_repost" }, fields);
    }

    // A wyrm has no HQ half, so it spends four rows and leaves one spare.
    [Fact]
    public void Wyrm_SkipsTheHqRow()
    {
        var fields = FieldIds(Camp("Tiamat")).ToList();

        Assert.Equal(new[] { "wdpop_tod", "wdpop_outcome", "wdpop_window", "wdpop_repost" }, fields);
    }

    // The outcome values have to be the ones ParseCampOutcome reads, or every camp silently records
    // as claimed+killed — its blank/unrecognized default.
    [Fact]
    public void Outcome_OffersTheThreeReachableStates()
    {
        var outcome = Fields(Camp("Tiamat")).EnumerateArray()
            .Single(f => FieldId(f) == "wdpop_outcome");

        Assert.Equal(
            new[] { "killed", "claimed", "missed" },
            Options(outcome).EnumerateArray().Select(o => o.GetProperty("value").GetString()));
        // The happy path is pre-selected so an officer can submit without touching the field.
        Assert.True(Options(outcome)[0].GetProperty("default").GetBoolean());
    }

    // The window picker opens EMPTY and has to be answered. It used to pre-select the window the
    // board was on, which meant an officer submitting without looking recorded the board's
    // clock-driven counter — and that counter runs ahead of the pop by however long the kill, the
    // loot and the rebuff took. No option may carry `default`, or the old behaviour is back.
    [Fact]
    public void PopWindow_OpensEmptyAndIsRequired()
    {
        var camp = Camp("Tiamat", windowNumber: 9);

        var select = Fields(camp).EnumerateArray()
            .Single(f => FieldId(f) == "wdpop_window").GetProperty("component");

        Assert.True(select.GetProperty("required").GetBoolean());
        Assert.Equal(1, select.GetProperty("min_values").GetInt32());
        Assert.DoesNotContain(
            select.GetProperty("options").EnumerateArray(),
            o => o.TryGetProperty("default", out var d) && d.GetBoolean());
    }

    // The board's counter isn't thrown away — it is demoted to the placeholder, so the officer
    // still sees where the board thought it was while having to choose for themselves.
    [Fact]
    public void PopWindow_KeepsTheBoardsCounterAsAHint()
    {
        var camp = Camp("Tiamat", windowNumber: 9);
        var expected = $"{DiscordEventMessageBuilder.FocusWindow(camp)}";

        var placeholder = Fields(camp).EnumerateArray()
            .Single(f => FieldId(f) == "wdpop_window")
            .GetProperty("component").GetProperty("placeholder").GetString()!;

        Assert.Contains(expected, placeholder, StringComparison.Ordinal);
        Assert.InRange(placeholder.Length, 1, 150); // Discord's placeholder cap
    }

    // "I don't know" is the first option on every multi-window camp — an officer who didn't see the
    // pop has to be able to say so instead of picking a plausible window, and has to see it without
    // scrolling. Its value is what the submit side reads as "fall back to the board's counter".
    [Theory]
    [InlineData("Tiamat")]
    [InlineData("Behemoth/King Behemoth")]
    [InlineData("Cerberus")]
    [InlineData("Goblin Furrier")]
    public void PopWindow_OffersIDontKnowFirst(string monster)
    {
        var window = Fields(Camp(monster)).EnumerateArray().Single(f => FieldId(f) == "wdpop_window");
        var options = Options(window).EnumerateArray().ToList();

        Assert.Equal("unknown", options[0].GetProperty("value").GetString());
        Assert.StartsWith("I don't know", options[0].GetProperty("label").GetString(), StringComparison.Ordinal);
        // And exactly once — a second escape hatch would mean two ways to record the same thing.
        Assert.Single(options, o => o.GetProperty("value").GetString() == "unknown");
    }

    // A select is capped at 25 options and the wyrms' 25 windows filled it EXACTLY, so the "I don't
    // know" row is paid for out of the window list: a wyrm shows 24 windows, everything shorter
    // still shows every window it has. Breaching the cap is a 400 from Discord — an officer clicking
    // End Camp and getting nothing at all.
    [Theory]
    [InlineData("Tiamat", 24)]                      // 25-window wyrm, trimmed by one
    [InlineData("Behemoth/King Behemoth", 7)]       // king/dragon band, untouched
    [InlineData("Cerberus", 5)]                     // the ToAU three, untouched
    [InlineData("Goblin Furrier", 2)]               // 2-post camp, untouched
    public void PopWindow_FitsTheOptionCap_WithTheIDontKnowRow(string monster, int expectedWindows)
    {
        var window = Fields(Camp(monster)).EnumerateArray().Single(f => FieldId(f) == "wdpop_window");
        var options = Options(window).EnumerateArray().ToList();

        Assert.InRange(options.Count, 1, 25);
        Assert.Equal(expectedWindows + 1, options.Count); // + the "I don't know" row
        Assert.Equal(Math.Min(HnmConfig.EffectiveWindowCount(monster), 24), expectedWindows);
    }

    // The 24-window pane slides so it always ENDS on the window the board is on: the pop has already
    // happened, so a window past the board's counter hasn't opened yet, while the window the board
    // IS on is the single most likely answer and can never be the one that drops off.
    [Theory]
    [InlineData(1, "Window 1", "Window 24")]
    [InlineData(25, "Window 2", "Window 25")]
    public void PopWindow_PaneEndsOnTheBoardsWindow(int boardWindow, string firstLabel, string lastLabel)
    {
        var window = Fields(Camp("Tiamat", windowNumber: boardWindow)).EnumerateArray()
            .Single(f => FieldId(f) == "wdpop_window");
        var labels = Options(window).EnumerateArray()
            .Skip(1) // the "I don't know" row
            .Select(o => o.GetProperty("label").GetString()).ToList();

        Assert.Equal(firstLabel, labels[0]);
        Assert.Equal(lastLabel, labels[^1]);
    }

    private static JsonElement RepostInput(Event ev, double? lead) =>
        Fields(ev, lead).EnumerateArray().Single(f => FieldId(f) == "wdpop_repost")
            .GetProperty("components")[0];

    // A monster with a lead already set gets it as a real editable VALUE, not a grey hint — the
    // officer reads the number, and whatever the box says is what gets saved. The two are mutually
    // exclusive: a prefilled box sends no placeholder.
    [Fact]
    public void RepostField_PrefillsTheLeadAlreadyConfigured()
    {
        var repost = RepostInput(Camp("Tiamat"), lead: 4);

        Assert.Equal("4", repost.GetProperty("value").GetString());
        Assert.False(repost.TryGetProperty("placeholder", out _));
    }

    // A FRACTIONAL lead is prefilled as itself. The field used to round it to a whole number to
    // match a whole-number parser, which was safe only while the End Camp form was the one thing
    // that could set a lead — the create-event form asks for one in quarter hours now, so rounding
    // here would rewrite an officer's 1.5 to 2 just by their submitting this modal.
    [Theory]
    [InlineData(2.5, "2.5")]
    [InlineData(1.4, "1.4")]
    [InlineData(0.25, "0.25")]
    public void RepostField_PrefillsAFractionalLeadAsItself(double lead, string expected) =>
        Assert.Equal(expected, RepostInput(Camp("Tiamat"), lead).GetProperty("value").GetString());

    // No standing board, or one that's switched off, means no lead is in effect — so the field
    // shows the example and stays EMPTY. Prefilling a stored number there would arm a re-post the
    // officer never asked for, just by their submitting the form.
    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    public void RepostField_ShowsTheExampleAndNoValue_WhenNothingIsInEffect(double? lead)
    {
        var repost = RepostInput(Camp("Tiamat"), lead);

        Assert.False(repost.TryGetProperty("value", out _));
        Assert.StartsWith("Example:", repost.GetProperty("placeholder").GetString(), StringComparison.Ordinal);
    }

    // Discord rejects a text input whose value runs past its own max_length, and rejects a
    // placeholder past 100 — the prefill has to fit the box it's being poured into.
    [Fact]
    public void RepostField_FitsItsOwnLimits()
    {
        foreach (var lead in new double?[] { null, 0.25, 1, 24, 168 })
        {
            var repost = RepostInput(Camp("Tiamat"), lead);
            var maxLength = repost.GetProperty("max_length").GetInt32();

            if (repost.TryGetProperty("value", out var value))
            {
                Assert.InRange(value.GetString()!.Length, 1, maxLength);
            }
            else
            {
                Assert.InRange(repost.GetProperty("placeholder").GetString()!.Length, 1, 100);
            }
        }
    }

    // A 2-post camp names its windows Open/Close, so the picker says so rather than asking "which
    // of the two?" — the same labels the board and the addon use.
    [Fact]
    public void PopWindow_UsesTheNamedLabels_OnATwoPostCamp()
    {
        var window = Fields(Camp("Goblin Furrier")).EnumerateArray().Single(f => FieldId(f) == "wdpop_window");
        var labels = Options(window).EnumerateArray()
            .Skip(1) // the "I don't know" row
            .Select(o => o.GetProperty("label").GetString()).ToList();

        Assert.Equal(new[] { "Open (window 1)", "Close (window 2)" }, labels);
    }
}
