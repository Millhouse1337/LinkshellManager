using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.ViewModels;

public class ProfileViewModel
{
    public string? CharacterName { get; set; }

    [StringLength(64)]
    public string? AltCharacterName1 { get; set; }

    [StringLength(64)]
    public string? AltCharacterName2 { get; set; }

    public string? TimeZone { get; set; }
    public byte[]? ProfileImageData { get; set; }
}
