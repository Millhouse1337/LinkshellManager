using System.Text.Json;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The controls inside the "View Previous Window" ephemeral: ◀ / ▶ across the windows a camp
// captured, plus a jump list. The whole point of building them from the CAPTURED list rather than
// from a 1..N range is that a camp's history can have holes in it — the advancer skips a window's
// clear when it comes to a boundary late (app down, service restarted) rather than wiping a live
// roster over an unwatched turnover. An arrow that stepped ±1 would land in one of those holes.
public class HnmWindowViewerTests
{
    private static readonly JsonSerializerOptions Wire = new() { WriteIndented = false };

    private static JsonElement Components(int window, params int[] captured) =>
        JsonSerializer.SerializeToElement(
            DiscordEventMessageBuilder.BuildWindowViewerComponents(42, window, captured), Wire);

    // The arrow row is always components[0]; the jump list, when present, is components[1].
    private static JsonElement Arrow(JsonElement rows, int index) =>
        rows[0].GetProperty("components")[index];

    private static string Label(JsonElement button) => button.GetProperty("label").GetString()!;
    private static string CustomId(JsonElement button) => button.GetProperty("custom_id").GetString()!;
    private static bool Disabled(JsonElement button) => button.GetProperty("disabled").GetBoolean();

    // Mid-history: both arrows live, each naming the window it goes to so the label doubles as the
    // destination. No guessing what "back" means.
    [Fact]
    public void Arrows_NameTheirDestination_AndCarryItOnTheWire()
    {
        var rows = Components(window: 5, captured: new[] { 3, 4, 5, 6, 7 });

        Assert.Equal("◀ Window 4", Label(Arrow(rows, 0)));
        Assert.Equal("Window 6 ▶", Label(Arrow(rows, 1)));
        Assert.Equal($"{DiscordEventMessageBuilder.WindowViewPrefix}42:4", CustomId(Arrow(rows, 0)));
        Assert.Equal($"{DiscordEventMessageBuilder.WindowViewPrefix}42:6", CustomId(Arrow(rows, 1)));
        Assert.False(Disabled(Arrow(rows, 0)));
        Assert.False(Disabled(Arrow(rows, 1)));
    }

    // THE reason the arrows read the captured list: windows 5 and 6 have no roster behind them, so
    // stepping back from 7 must land on 4, not on an empty 6. Both directions.
    [Fact]
    public void Arrows_StepOverAGapInTheHistory()
    {
        var backFrom7 = Components(window: 7, captured: new[] { 2, 3, 4, 7 });
        Assert.Equal("◀ Window 4", Label(Arrow(backFrom7, 0)));

        var forwardFrom4 = Components(window: 4, captured: new[] { 2, 3, 4, 7 });
        Assert.Equal("Window 7 ▶", Label(Arrow(forwardFrom4, 1)));
    }

    // At either end the arrow stays in place but goes dead, so the row doesn't reflow under a
    // cursor that's mid-click. A disabled arrow also can't send a window outside the history.
    [Fact]
    public void Arrows_AreDisabledAtEachEnd_RatherThanDisappearing()
    {
        var oldest = Components(window: 3, captured: new[] { 3, 4, 5 });
        Assert.True(Disabled(Arrow(oldest, 0)));
        Assert.Equal("◀ Oldest window", Label(Arrow(oldest, 0)));
        Assert.False(Disabled(Arrow(oldest, 1)));

        var newest = Components(window: 5, captured: new[] { 3, 4, 5 });
        Assert.True(Disabled(Arrow(newest, 1)));
        Assert.Equal("Newest window ▶", Label(Arrow(newest, 1)));
        Assert.False(Disabled(Arrow(newest, 0)));
    }

    // A camp with exactly one capture: both arrows dead, and no jump list — a picker offering the
    // window you are already looking at is noise.
    [Fact]
    public void SingleCapture_HasDeadArrowsAndNoJumpList()
    {
        var rows = Components(window: 4, captured: new[] { 4 });

        Assert.Equal(1, rows.GetArrayLength());
        Assert.True(Disabled(Arrow(rows, 0)));
        Assert.True(Disabled(Arrow(rows, 1)));
    }

    // The jump list is what keeps a 25-window Tiamat usable — reaching window 2 from window 24 is
    // one click, not twenty-two. The window in view is marked `default` so the select reads as
    // "you are here" instead of as an empty prompt under the roster.
    [Fact]
    public void JumpList_OffersEveryCapturedWindow_AndMarksTheCurrentOne()
    {
        var rows = Components(window: 3, captured: new[] { 1, 2, 3, 4 });

        var select = rows[1].GetProperty("components")[0];
        Assert.Equal(3, select.GetProperty("type").GetInt32()); // string select
        Assert.Equal(
            $"{DiscordEventMessageBuilder.WindowViewPickPrefix}42",
            select.GetProperty("custom_id").GetString());

        var options = select.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(new[] { "Window 1", "Window 2", "Window 3", "Window 4" },
            options.Select(o => o.GetProperty("label").GetString()));
        // The value is what comes back on the click, so it has to be the bare number.
        Assert.Equal(new[] { "1", "2", "3", "4" },
            options.Select(o => o.GetProperty("value").GetString()));
        var marked = Assert.Single(options, o => o.GetProperty("default").GetBoolean());
        Assert.Equal("Window 3", marked.GetProperty("label").GetString());
    }

    // A wyrm captures at most 24 windows (window 1 is never cleared, 25 is the ceiling), so the cap
    // never bites in practice — but if it ever did, the NEWEST windows are the ones worth keeping.
    [Fact]
    public void JumpList_StaysWithinDiscordsOptionCap()
    {
        var many = Enumerable.Range(1, 40).ToArray();
        var rows = Components(window: 40, captured: many);

        var options = rows[1].GetProperty("components")[0].GetProperty("options")
            .EnumerateArray().ToList();
        Assert.Equal(25, options.Count);
        Assert.Equal("Window 16", options[0].GetProperty("label").GetString());
        Assert.Equal("Window 40", options[^1].GetProperty("label").GetString());
    }
}
