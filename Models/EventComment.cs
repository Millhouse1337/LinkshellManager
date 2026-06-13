using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// A post-event discussion comment on a closed event (EventHistory). Authors can
// post anonymously (IsAnonymous hides their name in display — the AppUserId is
// still stored for moderation/abuse handling, never surfaced for anonymous rows).
// When the linkshell has a discussion channel configured, each comment is also
// mirrored to Discord (DiscordMessageId records the mirror).
public class EventComment
{
    [Key]
    public int Id { get; set; }

    public int EventHistoryId { get; set; }

    [ForeignKey(nameof(EventHistoryId))]
    public EventHistory? EventHistory { get; set; }

    public int LinkshellId { get; set; }

    [MaxLength(450)]
    public string? AppUserId { get; set; }

    // Snapshot of the author's character name at post time (shown when not anonymous).
    [MaxLength(256)]
    public string? CharacterName { get; set; }

    [MaxLength(2000)]
    public string Body { get; set; } = string.Empty;

    public bool IsAnonymous { get; set; }

    public DateTime CreatedAt { get; set; }

    // The mirrored Discord message id, when posted to the linkshell's discussion
    // channel (null when not mirrored).
    [MaxLength(20)]
    public string? DiscordMessageId { get; set; }
}
