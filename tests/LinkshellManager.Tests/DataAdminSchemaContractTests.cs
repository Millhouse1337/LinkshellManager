using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// The schema diagram's hand-offs between C#, the Razor view, the stylesheet and the two scripts.
///
/// The page (/data-admin) is drawn entirely in the browser. The server writes one JSON block
/// (DataAdminSchemaGraph.Json) and two scripts read it back: data-admin-schema-layout.js (where
/// things go) and data-admin-schema.js (the cards and lines). Nothing type-checks any of it, and
/// each piece fails silently:
///
///  1. A renamed C# record member renames the JSON property. The script then reads undefined, which
///     it treats as "false" or "none": no error, just a diagram with no keys, no lines or no cards.
///  2. The view must give the data block its id, its JSON type and a nonce, and must load the
///     layout script before the page script (the page script returns quietly when
///     window.LsmSchemaLayout is missing: an empty canvas, again with no error).
///  3. STYLE.md: colours come from tokens. A hardcoded colour in the diagram's CSS block would look
///     fine today and wrong the day the theme changes.
///  4. The site's .fade animation holds `transform`, which is what pan and zoom write; the
///     Content-Security-Policy (script-src is nonce-only) blocks inline event handlers; and STYLE.md
///     keeps styling in lsm-theme.css, which is also where the layout module's numbers must come from.
///
/// The browser is not part of this test suite, so these pin the text on both sides of each hand-off.
/// </summary>
public class DataAdminSchemaContractTests
{
    private const string LayoutScript = "data-admin-schema-layout.js";
    private const string PageScript = "data-admin-schema.js";
    private const string CssStartMarker = "/* ===== Data Admin: schema diagram (.erd) =====";
    private const string CssEndMarker = "/* ===== end Data Admin: schema diagram ===== */";

    // One JSON property a script reads: which kind of object carries it, its wire name, which of
    // the two scripts read it today, and whether every object has it (false: a flag or an optional
    // value, which the serializer leaves out when it is false / null). Receiver names the variable
    // the scripts read it from when the bare name is too common to be proof of a read: ".from" and
    // ".to" also occur as Array.from and geometry.to, so only "rel.from" / "rel.to" count.
    private sealed record Wire(
        string Kind, string Name, bool Layout, bool Page, bool Always, string? Receiver = null);

    // Kept as literals: the point is to fail when the wire name changes, which is exactly what
    // nameof() would hide. Every property the graph serializes is read by some script now.
    private static readonly Wire[] WireNames =
    {
        new("root", "tables", Layout: true, Page: false, Always: true),
        new("root", "relationships", Layout: true, Page: false, Always: true),
        new("root", "junctions", Layout: true, Page: false, Always: true),

        new("table", "id", Layout: true, Page: true, Always: true),
        new("table", "name", Layout: false, Page: true, Always: true),
        new("table", "label", Layout: false, Page: true, Always: true),
        new("table", "slug", Layout: false, Page: true, Always: false),
        new("table", "columns", Layout: true, Page: true, Always: true),
        new("table", "uniques", Layout: false, Page: true, Always: true),
        new("table", "parent", Layout: true, Page: false, Always: false),
        new("table", "hub", Layout: true, Page: true, Always: false),
        new("table", "framework", Layout: false, Page: true, Always: false),

        new("column", "name", Layout: true, Page: true, Always: true),
        new("column", "type", Layout: false, Page: true, Always: true),
        new("column", "storeType", Layout: false, Page: true, Always: true),
        new("column", "pk", Layout: true, Page: true, Always: false),
        new("column", "fk", Layout: true, Page: true, Always: false),
        new("column", "unique", Layout: true, Page: true, Always: false),
        new("column", "nullable", Layout: false, Page: true, Always: false),
        new("column", "protected", Layout: false, Page: true, Always: false),

        // "unique.filter": a bare ".filter" is also Array.prototype.filter, all over both scripts.
        new("unique", "columns", Layout: false, Page: true, Always: true, Receiver: "unique"),
        new("unique", "filter", Layout: false, Page: true, Always: false, Receiver: "unique"),

        new("relationship", "id", Layout: true, Page: true, Always: true),
        new("relationship", "from", Layout: true, Page: true, Always: true, Receiver: "rel"),
        new("relationship", "to", Layout: true, Page: true, Always: true, Receiver: "rel"),
        new("relationship", "fromColumns", Layout: false, Page: true, Always: true),
        new("relationship", "toColumns", Layout: false, Page: true, Always: true),
        new("relationship", "onDelete", Layout: false, Page: true, Always: true),
        new("relationship", "efDelete", Layout: false, Page: true, Always: true),
        new("relationship", "required", Layout: true, Page: true, Always: false),
        new("relationship", "unique", Layout: true, Page: false, Always: false),

        new("junction", "via", Layout: true, Page: true, Always: true),
        new("junction", "left", Layout: true, Page: true, Always: true),
        new("junction", "right", Layout: true, Page: true, Always: true),
        new("junction", "leftRelationship", Layout: false, Page: true, Always: true),
        new("junction", "rightRelationship", Layout: false, Page: true, Always: true),
    };

    // The data-erd-* hooks the page script fills or listens to. A hook missing from the view is
    // silent: the script either returns before drawing anything or skips that feature.
    private static readonly string[] ViewHooks =
    {
        "data-erd-canvas", "data-erd-layer", "data-erd-edges", "data-erd-edges-top", "data-erd-cards",
        "data-erd-empty", "data-erd-zoom-label", "data-erd-zoom-in", "data-erd-zoom-out", "data-erd-fit",
        "data-erd-reset", "data-erd-panel", "data-erd-search", "data-erd-selected", "data-erd-selected-body",
        "data-erd-clear", "data-erd-tables", "data-erd-rels", "data-erd-rels-title", "data-erd-junctions",
        "data-erd-legend", "data-erd-count=\"tables\"", "data-erd-count=\"relationships\"",
        "data-erd-count=\"junctions\"",
        // The View section and the Panel button. Each option control is one hook with its value:
        // a control the script cannot find leaves that option stuck at whatever was stored.
        "data-erd-view", "data-erd-opt=\"density\"", "data-erd-opt=\"edges\"", "data-erd-opt=\"types\"",
        "data-erd-opt=\"hubs\"", "data-erd-opt=\"framework\"", "data-erd-panel-toggle",
        // Focus mode: the bar's Focus button and the chip (its name slot and its leave button).
        "data-erd-focus", "data-erd-focus-chip", "data-erd-focus-name", "data-erd-focus-exit",
    };

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// Hand-off (1), the C# half: the real graph serializes under the names the scripts read. A
    /// name every object carries must be on every object; a flag must be on at least one (the
    /// model has primary keys, hubs, optional columns and so on, so each flag is true somewhere).
    /// </summary>
    [Fact]
    public void GraphSerializesUnderTheNamesTheScriptsRead()
    {
        using var db = NewInMemoryContext();
        var json = new DataAdminCatalog(db.Model, DataAdminPolicy.Default).Schema.Json;

        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var tables = Items(root, "tables");
        var objects = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal)
        {
            ["root"] = new List<JsonElement> { root },
            ["table"] = tables,
            ["column"] = tables.SelectMany(table => Items(table, "columns")).ToList(),
            ["unique"] = tables.SelectMany(table => Items(table, "uniques")).ToList(),
            ["relationship"] = Items(root, "relationships"),
            ["junction"] = Items(root, "junctions"),
        };

        foreach (var wire in WireNames)
        {
            var ofKind = objects[wire.Kind];
            Assert.NotEmpty(ofKind);
            var carrying = ofKind.Count(item => item.TryGetProperty(wire.Name, out _));
            Assert.True(
                wire.Always ? carrying == ofKind.Count : carrying > 0,
                $"The schema graph no longer serializes '{wire.Name}' on a {wire.Kind}, which the diagram scripts read.");
        }
    }

    /// <summary>
    /// The other half of the same hand-off: each script still reads the names listed for it.
    /// Comments are stripped first so that a name mentioned in prose never counts as a read.
    /// </summary>
    [Fact]
    public void ScriptsReadThoseNames()
    {
        var folder = Path.Combine(FindRepoDirectory("wwwroot"), "js");
        var layout = StripLineComments(File.ReadAllText(Path.Combine(folder, LayoutScript)));
        var page = StripLineComments(File.ReadAllText(Path.Combine(folder, PageScript)));

        foreach (var wire in WireNames)
        {
            Assert.True(wire.Layout || wire.Page, $"'{wire.Name}' is listed but no script is said to read it.");
            // A property read: ".name" (or "receiver.name") not followed by more of an identifier.
            var receiver = wire.Receiver is null ? @"\." : @"\b" + Regex.Escape(wire.Receiver) + @"\.";
            var read = new Regex(receiver + Regex.Escape(wire.Name) + @"(?![A-Za-z0-9_$])");
            if (wire.Layout)
            {
                Assert.True(read.IsMatch(layout), $"{LayoutScript} no longer reads '.{wire.Name}'.");
            }
            if (wire.Page)
            {
                Assert.True(read.IsMatch(page), $"{PageScript} no longer reads '.{wire.Name}'.");
            }
        }
    }

    /// <summary>
    /// Hand-off (2): the data block is a nonce'd JSON block with the id the script looks up, and
    /// the layout module is loaded first. Both scripts are deferred, so they run in that order and
    /// only after the data block (which sits above them in the page) has been parsed.
    /// </summary>
    [Fact]
    public void ViewCarriesTheDataBlockAndLoadsTheScriptsInOrder()
    {
        var view = ReadView();
        var scriptTags = Regex.Matches(view, @"<script\b[^>]*>").Select(match => match.Value).ToList();

        var dataBlock = Assert.Single(scriptTags, tag => tag.Contains("id=\"erd-data\"", StringComparison.Ordinal));
        Assert.Contains("type=\"application/json\"", dataBlock, StringComparison.Ordinal);
        Assert.Contains("nonce=\"@Html.CspNonce()\"", dataBlock, StringComparison.Ordinal);

        var layoutTag = Assert.Single(scriptTags, tag => tag.Contains("/" + LayoutScript + "\"", StringComparison.Ordinal));
        var pageTag = Assert.Single(scriptTags, tag => tag.Contains("/" + PageScript + "\"", StringComparison.Ordinal));
        Assert.Matches(@"\sdefer\b", layoutTag);
        Assert.Matches(@"\sdefer\b", pageTag);
        Assert.True(
            view.IndexOf(layoutTag, StringComparison.Ordinal) < view.IndexOf(pageTag, StringComparison.Ordinal),
            $"{LayoutScript} must be loaded before {PageScript}: the page script needs window.LsmSchemaLayout.");
    }

    /// <summary>
    /// Hand-off (2), the panel and the controls: every hook the page script looks up is in the view
    /// exactly once, and the script really does look it up (so a hook nobody reads cannot linger).
    /// </summary>
    [Fact]
    public void ViewCarriesEveryHookThePageScriptUses()
    {
        var view = ReadView();
        var page = StripLineComments(
            File.ReadAllText(Path.Combine(FindRepoDirectory("wwwroot"), "js", PageScript)));

        foreach (var hook in ViewHooks)
        {
            var inView = Regex.Matches(view, @"\s" + Regex.Escape(hook) + @"(?=[\s>/])").Count;
            Assert.True(inView == 1, $"Index.cshtml must carry '{hook}' exactly once; found {inView}.");
            Assert.Contains("[" + hook + "]", page, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Hand-off (3): the diagram's CSS block uses tokens only. Comments are removed first, so a
    /// comment may still name a colour; an id selector such as #erd is not a hex colour.
    /// </summary>
    [Fact]
    public void CssBlockHasNoHardcodedColour()
    {
        var css = File.ReadAllText(Path.Combine(FindRepoDirectory("wwwroot"), "css", "lsm-theme.css"));
        var start = css.IndexOf(CssStartMarker, StringComparison.Ordinal);
        var end = css.IndexOf(CssEndMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The schema diagram's CSS start marker is missing from lsm-theme.css.");
        Assert.True(end > start, "The schema diagram's CSS end marker is missing from lsm-theme.css.");

        var block = Regex.Replace(css.Substring(start, end - start), @"/\*.*?\*/", " ", RegexOptions.Singleline);

        Assert.Contains(".erd", block, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})(?![\w-])", block);
        Assert.DoesNotMatch(@"(?i)\b(?:rgba?|hsla?)\s*\(", block);
    }

    /// <summary>
    /// Hand-off (4): nothing in the view carries the class "fade" (its animation would fight pan
    /// and zoom), there is no inline event handler (the CSP blocks it), and no style attribute (the
    /// house rule: styling lives in lsm-theme.css; the CSP itself allows inline styles).
    /// </summary>
    [Fact]
    public void ViewHasNoFadeClassAndNothingInline()
    {
        var view = ReadView();

        var classes = Regex.Matches(view, "class=\"([^\"]*)\"")
            .SelectMany(match => match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        Assert.NotEmpty(classes);
        Assert.DoesNotContain("fade", classes);

        Assert.DoesNotMatch(@"(?i)\son[a-z]+\s*=", view);
        Assert.DoesNotMatch(@"(?i)\sstyle\s*=", view);
    }

    // Both scripts comment with "//" only (house style), so this is enough to drop their prose.
    private static string StripLineComments(string script) => Regex.Replace(script, @"//[^\r\n]*", "");

    private static List<JsonElement> Items(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().ToList()
            : new List<JsonElement>();

    // The view without its Razor comments: they are notes for the reader and never reach the page.
    private static string ReadView()
    {
        var view = File.ReadAllText(Path.Combine(FindRepoDirectory("Views"), "DataAdmin", "Index.cshtml"));
        return Regex.Replace(view, @"@\*.*?\*@", " ", RegexOptions.Singleline);
    }

    /// <summary>Walks up from the test binary to the repo root, which holds the web assets.</summary>
    private static string FindRepoDirectory(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate '{relative}' above {AppContext.BaseDirectory}.");
    }
}
