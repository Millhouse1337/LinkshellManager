using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class RuleViewModel
{
    public int Id { get; set; }

    public List<Linkshell> Linkshells { get; set; } = new();

    [Display(Name = "Linkshell")]
    public int LinkshellId { get; set; }

    public string? LinkshellName { get; set; }

    [Required]
    [Display(Name = "Title")]
    [StringLength(256)]
    public string RuleTitle { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Details")]
    [StringLength(4000)]
    public string RuleDetails { get; set; } = string.Empty;

    [Display(Name = "Category")]
    [StringLength(32)]
    public string? Category { get; set; }

    public string? CreatedByCharacterName { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool CanManage { get; set; }
}
