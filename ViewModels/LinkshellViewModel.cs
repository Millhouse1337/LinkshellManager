using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.ViewModels;

public class LinkshellViewModel
{
    [Required]
    [MaxLength(100)]
    public string LinkshellName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Details { get; set; }
}
