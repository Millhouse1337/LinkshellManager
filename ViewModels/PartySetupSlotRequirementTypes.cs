namespace LinkshellManagerDiscordApp.ViewModels;

// Slot requirement discriminator values. Mirrors the existing constants-class
// convention (e.g. WindowEventStatuses) — stored as a short string on
// PartySetupSlot.RequirementType, not a DB enum.
public static class PartySetupSlotRequirementTypes
{
    public const string Any = "Any";
    public const string Role = "Role";
    public const string Job = "Job";

    public static readonly IReadOnlyList<string> All = new[] { Any, Role, Job };
}
