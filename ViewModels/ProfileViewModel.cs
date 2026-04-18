namespace LinkshellManagerDiscordApp.ViewModels;

public class ProfileViewModel
{
    public string? CharacterName { get; set; }
    public string? TimeZone { get; set; }
    public IFormFile? ProfileImage { get; set; }
    public byte[]? ProfileImageData { get; set; }
}
