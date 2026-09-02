using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// The web Add ToD form's pre-fill contract.
///
/// The form hands the browser one blob -- the linkshell's per-monster cooldown / cadence -- and
/// tod-manager.js reads it back to fill the Cooldown and Interval boxes the moment a monster is
/// picked. Nothing type-checks that hand-off, and it broke in the two ways a hand-off can:
///
///  1. The view serialized the dictionary with the DEFAULT naming policy (PascalCase) while the
///     script read `timing.cooldownMinutes`. Every read came back undefined, which the script's
///     "no configured duration" branch treats as "clear the field" -- so picking a monster BLANKED
///     the cooldown, and the repop then computed off a zero cooldown and landed on the time of
///     death itself.
///  2. The dictionary was keyed off the linkshell's stored ROWS, so a linkshell that had never
///     opened the Monster setups editor sent an empty map while its picker still offered the whole
///     built-in catalog -- every option pre-filled nothing.
///
/// These pin both, plus the repop sum the three save paths share.
/// </summary>
public class TodFormPrefillTests
{
    // The property names tod-manager.js reads off each hint. Kept as literals: the point is to
    // fail when the wire name changes, which is exactly what nameof() would hide.
    private static readonly string[] HintPropertiesTheScriptReads =
    {
        "cooldownMinutes",
        "cadenceMinutes",
        "hasHqVariant",
        "hasSpawnGrid"
    };

    /// <summary>
    /// The serialized hint carries the names the script reads. This is bug (1) above: the payload
    /// and the reader disagreed on casing, and nothing failed -- the fields just went blank.
    /// </summary>
    [Fact]
    public void HintSerializesUnderTheNamesTheScriptReads()
    {
        var json = JsonSerializer.Serialize(
            new TodMonsterTimingHint(22 * 60, 60, true, true),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var parsed = JsonDocument.Parse(json);
        foreach (var property in HintPropertiesTheScriptReads)
        {
            Assert.True(
                parsed.RootElement.TryGetProperty(property, out _),
                $"The ToD form's timing hint no longer serializes '{property}', which tod-manager.js reads.");
        }
    }

    /// <summary>The other half of the same hand-off: the script still reads those names.</summary>
    [Fact]
    public void ScriptReadsThoseNamesOffTheHint()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoDirectory("wwwroot"), "js", "tod-manager.js"));
        foreach (var property in HintPropertiesTheScriptReads)
        {
            Assert.Contains($"timing.{property}", script, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// And the view must serialize it with an explicit camelCase policy. A bare
    /// JsonSerializer.Serialize(...) is the regression -- it compiles, renders, and silently emits
    /// PascalCase.
    /// </summary>
    [Fact]
    public void ViewSerializesTheTimingsCamelCased()
    {
        var view = File.ReadAllText(Path.Combine(FindRepoDirectory("Views"), "ToD", "Create.cshtml"));

        Assert.Contains("JsonNamingPolicy.CamelCase", view, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "JsonSerializer.Serialize(Model.MonsterTimings)",
            view,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Bug (2): an UNSEEDED linkshell offers the built-in catalog, so every one of those options
    /// has to carry a hint. Keying the map off the stored rows left all of them with none.
    /// </summary>
    [Fact]
    public void EveryPickerOptionCarriesAHintWhenTheLinkshellIsUnseeded()
    {
        var map = new MonsterTimingMap(1, Array.Empty<LinkshellMonsterTiming>());
        var hints = BuildHints(map);

        Assert.NotEmpty(map.EventMonsterOptions);
        foreach (var option in map.EventMonsterOptions)
        {
            Assert.True(hints.ContainsKey(option), $"'{option}' is offered by the picker with no timing hint.");
            Assert.True(hints[option].CooldownMinutes > 0, $"'{option}' pre-fills a non-positive cooldown.");
        }
    }

    /// <summary>
    /// A seeded linkshell keys its hints by the option text the picker shows -- which for the three
    /// NQ/HQ families is the COMBINED label, the one thing a per-half lookup would miss.
    /// </summary>
    [Fact]
    public void MergedPairIsHintedUnderItsCombinedLabel()
    {
        var map = new MonsterTimingMap(1, new[]
        {
            new LinkshellMonsterTiming
            {
                LinkshellId = 1,
                MonsterName = "Behemoth/King Behemoth",
                CooldownMinutes = 22 * 60,
                WindowCadenceMinutes = 10,
                WindowCount = 25
            }
        });

        var hints = BuildHints(map);
        var hint = Assert.Contains("Behemoth/King Behemoth", (System.Collections.Generic.IDictionary<string, TodMonsterTimingHint>)hints);

        Assert.Equal(22 * 60, hint.CooldownMinutes);
        Assert.Equal(10, hint.CadenceMinutes);
        // Both conditional fields the form only asks of some monsters: a merge pair has a day
        // cycle, and this one has a spawn grid to number a pop window against.
        Assert.True(hint.HasHqVariant);
        Assert.True(hint.HasSpawnGrid);
    }

    /// <summary>
    /// Repop = time of death + cooldown + the "Additional seconds" offset. The web form used to
    /// drop the offset entirely (it had no input for it), so a ToD logged on the web and the same
    /// one logged in the Activity predicted different windows.
    /// </summary>
    [Fact]
    public void RepopAddsTheCooldownAndTheAdditionalSeconds()
    {
        var death = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(death.AddHours(22).AddSeconds(45), ResolveRepop(death, "22 Hour", 45));
        Assert.Equal(death.AddHours(22), ResolveRepop(death, "22 Hour", 0));
        // A negative offset is clamped rather than winding the window backwards.
        Assert.Equal(death.AddHours(22), ResolveRepop(death, "22 Hour", -30));
        // No time of death = nothing was seen die, so there is no repop to predict.
        Assert.Null(ResolveRepop(null, "22 Hour", 45));
    }

    private static System.Collections.Generic.Dictionary<string, TodMonsterTimingHint> BuildHints(MonsterTimingMap map) =>
        (System.Collections.Generic.Dictionary<string, TodMonsterTimingHint>)Invoke("BuildMonsterTimingHints", map)!;

    private static DateTime? ResolveRepop(DateTime? death, string cooldown, int additionalSeconds) =>
        (DateTime?)Invoke("ResolveRepopTime", death, cooldown, additionalSeconds);

    private static object? Invoke(string name, params object?[] args)
    {
        var method = typeof(TodController).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method!.Invoke(null, args);
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
