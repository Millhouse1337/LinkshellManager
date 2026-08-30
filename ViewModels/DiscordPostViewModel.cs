namespace LinkshellManagerDiscordApp.ViewModels;

// One rule / announcement drawn as a Discord message by Views/Shared/_DiscordPost.cshtml.
// Rules and announcements are the same shape on screen, so both Index views project into
// this and share the partial. Edit/Delete post back to the *current* controller, so the
// partial carries no controller name of its own.
public class DiscordPostViewModel
{
    public int Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? Details { get; init; }

    public string? Category { get; init; }

    public string? Author { get; init; }

    public DateTime CreatedAt { get; init; }

    public bool CanManage { get; init; }

    // Position in the list -- only used to cycle the accent color for uncategorised posts.
    public int Index { get; init; }

    public string DeleteConfirm { get; init; } = "Delete this post?";
}
