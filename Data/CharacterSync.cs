namespace LinkshellManagerDiscordApp.Data;

// What is left of the old copy from the account columns into Characters (AddCharacters).
//
// Since Round 5 Characters is the only source of names, jobs and crafts (CharacterWriter writes it),
// and the old account columns it was copied from are gone (Round5Contract, R5.7). One rule of the
// old copy stays: a character name is never blank. NameNotBlankCheckSql is that check on
// Characters.Name (CK_Characters_Name_NotBlank, part of the schema). "Blank" uses the same
// whitespace .NET's Trim removes (TodDerivedSql.WhitespaceChars).
public static class CharacterSync
{
    private const string Ws = TodDerivedSql.WhitespaceChars;

    // The check on Characters.Name: at least one character that is not whitespace.
    public static readonly string NameNotBlankCheckSql = $"\"Name\" ~ '[^{Ws}]'";
}
