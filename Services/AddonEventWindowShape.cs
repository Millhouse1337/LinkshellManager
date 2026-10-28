using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// How a camp posts, as the in-game addon needs it — sent on GET /api/addon/events and
// /events/{id} so the addon can draw its Post buttons from the server instead of from its own
// monster-name tables (constants.lua), which knew nothing about a linkshell's Monster Setups.
//
//   WindowMethod         MonsterWindowMethods value, or null for a camp with none stamped (made
//                        before the setting existed, Manual Check In, or a non-HNM event). The addon
//                        falls back to its old `attendancePostCount == 2` test on null.
//   PostsOpenAndClose    Open / Close buttons (true) or numbered "Window N" buttons (false).
//   WindowsOpenEnded     Numbered windows with no "of M": the next post is one past the last.
//   WindowCadenceSeconds Seconds between window openings, or null for a camp with no timed grid —
//                        what replaces the addon's built-in cadence table for its countdowns.
//
// One record built in one place, so the two endpoints can't describe the same camp differently.
public sealed record AddonEventWindowShape(
    string? WindowMethod,
    bool PostsOpenAndClose,
    bool WindowsOpenEnded,
    int? WindowCadenceSeconds)
{
    public static AddonEventWindowShape For(Event ev)
    {
        var minutes = DiscordEventMessageBuilder.UsesWindows(ev)
            ? DiscordEventMessageBuilder.EffectiveWindowMinutes(ev)
            : 0;
        return new AddonEventWindowShape(
            DiscordEventMessageBuilder.WindowMethodFor(ev),
            DiscordEventMessageBuilder.PostsOpenAndClose(ev),
            DiscordEventMessageBuilder.WindowsOpenEnded(ev),
            minutes > 0 ? minutes * 60 : null);
    }
}
