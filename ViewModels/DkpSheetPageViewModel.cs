using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.ViewModels;

// The DKP Sheet page: the computed sheet data. Posting the sheet to a Discord
// channel is configured in the channel-routes editor (the "DKP sheet" post type),
// so this page no longer carries any channel-picker state.
public sealed class DkpSheetPageViewModel
{
    public required DkpSheetData Data { get; init; }
}
