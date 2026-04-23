using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class ManageItemViewModel
{
    public int Id { get; set; }

    [Required]
    public int LinkshellId { get; set; }

    public string? LinkshellName { get; set; }

    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string ItemName { get; set; } = string.Empty;

    [StringLength(128)]
    public string? ItemType { get; set; }

    [Range(0, int.MaxValue)]
    public int Quantity { get; set; }

    [StringLength(1024)]
    public string? Notes { get; set; }

    public string? CreatedByCharacterName { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool CanManage { get; set; }

    public List<Linkshell> Linkshells { get; set; } = new();
}
