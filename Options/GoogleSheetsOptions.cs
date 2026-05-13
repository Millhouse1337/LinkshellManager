namespace LinkshellManagerDiscordApp.Options;

public sealed class GoogleSheetsOptions
{
    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string CallbackPath { get; init; } = "/signin-google-sheets";

    public int DebounceSeconds { get; init; } = 3;

    public string DefaultTabName { get; init; } = "Main";

    public string DefaultWriteRange { get; init; } = "B8:C200";
}
