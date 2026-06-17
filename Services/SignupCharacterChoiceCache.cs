using System.Collections.Concurrent;

namespace LinkshellManagerDiscordApp.Services;

// Short-lived store of "which character" a Discord user picked for an event's
// in-channel signup. The character-pick ephemeral step writes here; the final
// signup write points read it (defaulting to the member's main when absent).
// Single in-memory instance (the app runs as one process); entries lazily
// expire, so there's no cleanup loop. Keyed per (user, event) so a choice
// persists across a multi-step signup wizard but not across different events.
public sealed class SignupCharacterChoiceCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (string Name, DateTime Expires)> _choices = new();

    public void Set(string appUserId, int eventId, string characterName)
        => _choices[Key(appUserId, eventId)] = (characterName, DateTime.UtcNow + Ttl);

    // Forget the choice for (user, event). Called on withdrawal so the next signup
    // re-prompts for a character instead of silently reusing the prior pick.
    public void Clear(string appUserId, int eventId)
        => _choices.TryRemove(Key(appUserId, eventId), out _);

    public string? Peek(string appUserId, int eventId)
    {
        var key = Key(appUserId, eventId);
        if (_choices.TryGetValue(key, out var entry))
        {
            if (entry.Expires > DateTime.UtcNow) return entry.Name;
            _choices.TryRemove(key, out _);
        }
        return null;
    }

    private static string Key(string appUserId, int eventId) => $"{appUserId}:{eventId}";
}
