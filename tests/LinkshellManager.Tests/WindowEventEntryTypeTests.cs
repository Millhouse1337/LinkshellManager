using LinkshellManagerDiscordApp.Models;
using Xunit;

namespace LinkshellManager.Tests;

// WindowEvent.EntryType is auto-tagged from the monster at creation and is no longer asked for
// in the UI. It is NOT cosmetic: WindowEventDkpLedgerService gates on IsValid and returns 0 with
// no exception and no log, so a posted event carrying an invalid tag reports success and credits
// nobody. Resolve is what guarantees a save can never put the column into that state.
public class WindowEventEntryTypeTests
{
    [Theory]
    [InlineData("Tiamat", WindowEventEntryTypes.WyrmsCamp)]
    [InlineData("Jormungand", WindowEventEntryTypes.WyrmsCamp)]  // on both lists in lore; Wyrms wins
    [InlineData("Vrtra", WindowEventEntryTypes.WyrmsCamp)]
    [InlineData("Fafnir", WindowEventEntryTypes.KingsCamp)]
    [InlineData("Nidhogg", WindowEventEntryTypes.KingsCamp)]
    [InlineData("Behemoth", WindowEventEntryTypes.KingsCamp)]
    [InlineData("Adamantoise", WindowEventEntryTypes.KingsCamp)]
    public void FromMonsterName_TagsKnownCamps(string monster, string expected) =>
        Assert.Equal(expected, WindowEventEntryTypes.FromMonsterName(monster));

    // The fallback is what makes removing the input safe — an unrecognized or missing name still
    // yields a valid tag rather than null.
    [Theory]
    [InlineData("Some Random NM")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FromMonsterName_FallsBackToMisc(string? monster) =>
        Assert.Equal(WindowEventEntryTypes.MiscCamp, WindowEventEntryTypes.FromMonsterName(monster));

    // An explicitly supplied valid tag wins (older clients still post the field).
    [Fact]
    public void Resolve_PrefersSuppliedValue() =>
        Assert.Equal(
            WindowEventEntryTypes.Kill,
            WindowEventEntryTypes.Resolve(WindowEventEntryTypes.Kill, WindowEventEntryTypes.MiscCamp, "Tiamat"));

    // The UI no longer sends one — the stored tag must survive the save untouched.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not A Real Type")]
    public void Resolve_KeepsStoredValueWhenNoneSupplied(string? supplied) =>
        Assert.Equal(
            WindowEventEntryTypes.WyrmsCamp,
            WindowEventEntryTypes.Resolve(supplied, WindowEventEntryTypes.WyrmsCamp, "Tiamat"));

    // Legacy rows predating auto-tagging: re-derive from the event name so the row stops being
    // uncreditable instead of staying stuck.
    [Fact]
    public void Resolve_HealsLegacyNullFromEventName() =>
        Assert.Equal(WindowEventEntryTypes.WyrmsCamp, WindowEventEntryTypes.Resolve(null, null, "Tiamat"));

    // Every combination has to come out valid — that is the whole contract callers rely on
    // instead of validating.
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData("garbage", "garbage", "garbage")]
    [InlineData(null, null, "Unknown Monster")]
    public void Resolve_NeverReturnsAnInvalidTag(string? supplied, string? stored, string? name) =>
        Assert.True(WindowEventEntryTypes.IsValid(WindowEventEntryTypes.Resolve(supplied, stored, name)));

    [Fact]
    public void IsValid_RejectsNullAndBlank()
    {
        Assert.False(WindowEventEntryTypes.IsValid(null));
        Assert.False(WindowEventEntryTypes.IsValid(""));
        Assert.False(WindowEventEntryTypes.IsValid("kings camp")); // exact-match only: sheet formulas pivot on these strings
        Assert.True(WindowEventEntryTypes.IsValid(WindowEventEntryTypes.KingsCamp));
    }
}
