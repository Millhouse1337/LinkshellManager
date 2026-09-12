using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The rules a typed character name has to meet before it goes on a roster: letters only, 3 to 15
// of them, and the game's own casing (one capital, then lowercase) applied for the person rather
// than demanded of them.
public class CharacterNameFormatTests
{
    [Theory]
    [InlineData("Millhouse", "Millhouse")]
    [InlineData("millhouse", "Millhouse")]
    [InlineData("MILLHOUSE", "Millhouse")]
    [InlineData("mILLhouse", "Millhouse")]
    [InlineData("  Milltwo  ", "Milltwo")]
    [InlineData("Abc", "Abc")]
    [InlineData("Abcdefghijklmno", "Abcdefghijklmno")]   // 15 letters
    public void LettersOnly_IsAccepted_AndPutInGameCase(string raw, string expected)
    {
        Assert.True(CharacterNameFormat.TryNormalize(raw, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Millhouse2401")]
    [InlineData("Mill House")]
    [InlineData("Mill-house")]
    [InlineData("Mill'house")]
    [InlineData("Millhouse!")]
    [InlineData("Millhouse🎮")]
    [InlineData("Míllhouse")]
    [InlineData("_Millhouse")]
    public void AnythingButLetters_IsRejected(string raw)
    {
        Assert.False(CharacterNameFormat.TryNormalize(raw, out var normalized, out var error));
        Assert.Equal(string.Empty, normalized);
        Assert.NotNull(error);
    }

    [Fact]
    public void ASpace_GetsItsOwnExplanation()
    {
        CharacterNameFormat.TryNormalize("Mill House", out _, out var error);
        Assert.Contains("no spaces", error);
    }

    [Theory]
    [InlineData("Ab")]                  // 2
    [InlineData("Abcdefghijklmnop")]    // 16
    public void OutsideThreeToFifteen_IsRejected(string raw)
    {
        Assert.False(CharacterNameFormat.TryNormalize(raw, out _, out var error));
        Assert.Contains("3 to 15", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_IsRejected(string? raw)
    {
        Assert.False(CharacterNameFormat.TryNormalize(raw, out _, out var error));
        Assert.Equal("Enter a character name.", error);
    }

    [Theory]
    [InlineData("Mill House", "Millhouse")]
    [InlineData("millhouse2401", "Millhouse")]
    [InlineData("🎮 Mill House 🎮", "Millhouse")]
    [InlineData("Abcdefghijklmnopqrstuvwxyz", "Abcdefghijklmno")]  // capped at 15
    [InlineData("Ab", "")]                                         // too little survives
    [InlineData("42", "")]
    [InlineData(null, "")]
    public void SuggestFrom_KeepsWhatCouldBeAName(string? displayName, string expected)
    {
        Assert.Equal(expected, CharacterNameFormat.SuggestFrom(displayName));
    }
}
