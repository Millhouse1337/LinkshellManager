namespace LinkshellManagerDiscordApp.ViewModels;

// Carried via TempData from SignUpPartySlot back to the board page when the
// "fill earlier alliances first" nudge fires: the member chose a later-alliance
// slot while an earlier matching slot is open. The board renders a confirm modal
// offering the earlier slot (SuggestedSlotId) or to sign up where they chose
// (OriginalSlotId, force=true). Original picks are carried so "sign up anyway"
// re-posts identically.
public sealed record SignupNudgePayload(
    int EventId,
    int OriginalSlotId,
    int SuggestedSlotId,
    string SuggestedLocation,
    string? SuggestedRequirement,
    string? Role,
    string? MainJob,
    string? SubJob,
    bool AsLeader,
    string? SelectedCharacter,
    string? ReturnUrl);
