using System.Collections.Concurrent;

namespace LinkshellManagerDiscordApp.Services;

// Short-lived store of the member an OFFICER is in the middle of manually adding to an
// event's Discord signup board. The board's "Add Member" button starts the flow; the
// member picker / new-player modal writes the chosen target here; the slot-pick + job-
// wizard steps then read it so the claim is attributed to the TARGET member rather than
// the clicking officer. Keyed by (officer Discord id, event) so a target survives the
// multi-step picker but is scoped to that officer + event. Single in-memory instance (the
// app runs as one process); entries lazily expire, so there's no cleanup loop. Mirrors
// SignupCharacterChoiceCache, which does the same for the "which character" pick.
public sealed class OfficerAddTargetCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    // The member being seated: AppUserId is always set (a real or placeholder account, so
    // the AppUserId-keyed DKP / history system tracks them). DiscordUserId is set only for
    // unsynced/placeholder members (so they can still self-withdraw from the board);
    // CharacterName is the name to record on the signup.
    public sealed record Target(string AppUserId, string CharacterName, string? DiscordUserId);

    private readonly ConcurrentDictionary<string, (Target Target, DateTime Expires)> _targets = new();

    public void Set(string officerDiscordUserId, int eventId, Target target)
        => _targets[Key(officerDiscordUserId, eventId)] = (target, DateTime.UtcNow + Ttl);

    public void Clear(string officerDiscordUserId, int eventId)
        => _targets.TryRemove(Key(officerDiscordUserId, eventId), out _);

    public Target? Peek(string officerDiscordUserId, int eventId)
    {
        var key = Key(officerDiscordUserId, eventId);
        if (_targets.TryGetValue(key, out var entry))
        {
            if (entry.Expires > DateTime.UtcNow) return entry.Target;
            _targets.TryRemove(key, out _);
        }
        return null;
    }

    private static string Key(string officerDiscordUserId, int eventId) => $"{officerDiscordUserId}:{eventId}";
}
