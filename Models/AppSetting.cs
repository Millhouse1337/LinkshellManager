using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.Models;

// Generic app-wide (NOT linkshell-scoped) key/value setting store. Currently
// backs the global addon kill-switch; reuse the same table for any future
// server-wide flags rather than adding one-off columns.
public class AppSetting
{
    [Key]
    [MaxLength(128)]
    public string Key { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Value { get; set; }
}
