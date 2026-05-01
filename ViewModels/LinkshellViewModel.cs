using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class LinkshellViewModel
{
    [Required]
    [MaxLength(100)]
    public string LinkshellName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Details { get; set; }
}

// Backs the /Linkshell/Customize page. Mirrors the Discord Activity's
// "Customize Linkshell" card on its Configurations tab.
public class LinkshellCustomizeViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }

    [Required, MaxLength(32)]
    public string? LootStructure { get; set; } = "Dkp";

    [Required, MaxLength(16)]
    public string? DkpRoundingIncrement { get; set; } = "Quarter";

    public bool EnableEndgame    { get; set; } = true;
    public bool EnableHnmSection { get; set; } = true;
    public bool EnableMissions   { get; set; } = true;
    public bool EnableAuctions   { get; set; } = true;
    public bool EnableToDs       { get; set; } = true;
    public bool EnableEvents     { get; set; } = true;
    public bool EnableDkp        { get; set; } = true;
    public bool EnableItems      { get; set; } = true;
    public bool EnableRevenue    { get; set; } = true;

    public List<Linkshell> ManageableLinkshells { get; set; } = new();
}
