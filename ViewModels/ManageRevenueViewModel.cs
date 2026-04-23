using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class ManageRevenueViewModel
{
    public int Id { get; set; }

    [Required]
    public int LinkshellId { get; set; }

    public string? LinkshellName { get; set; }

    [Required]
    [StringLength(64, MinimumLength = 1)]
    [Display(Name = "Source")]
    public string EntryType { get; set; } = string.Empty;

    [StringLength(128)]
    public string? Category { get; set; }

    [Range(0, long.MaxValue)]
    public long Value { get; set; }

    [StringLength(1024)]
    public string? Details { get; set; }

    [Display(Name = "Occurred at")]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public string? CreatedByCharacterName { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool CanManage { get; set; }

    public List<Linkshell> Linkshells { get; set; } = new();

    public static readonly IReadOnlyList<string> SourceOptions = new[]
    {
        "AH Sale",
        "Mercenary Work",
        "Donation",
        "Other"
    };
}
